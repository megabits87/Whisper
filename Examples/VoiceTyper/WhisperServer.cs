using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;

namespace VoiceTyper
{
	/// <summary>
	/// Manages a background whisper.cpp HTTP server (GPU) with the model resident in VRAM, and sends it
	/// audio for transcription. Modern decoder: beam search + temperature fallback (far better than the
	/// in-proc DirectCompute engine on hard audio).
	/// </summary>
	sealed class WhisperServer : IDisposable
	{
		readonly string exePath;
		readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromMinutes( 30 ) };
		readonly object sync = new object();
		Process? proc;
		int port;

		public bool IsRunning { get; private set; }
		public string ModelPath { get; private set; } = "";

		/// <summary>GPU the server reported using (parsed from its CUDA init log), e.g. "NVIDIA GeForce RTX 3060 Laptop GPU".</summary>
		public string GpuName { get; private set; } = "";

		public WhisperServer( string exePath ) => this.exePath = exePath;

		/// <summary>Where to find whisper-server.exe: bundled next to the app (installer/portable layout)
		/// first, then the per-user location the setup script installs to.</summary>
		public static string DefaultExe
		{
			get
			{
				string portable = Path.Combine( AppContext.BaseDirectory, "whispercpp", "whisper-server.exe" );
				if( File.Exists( portable ) )
					return portable;
				return Path.Combine(
					Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ),
					"VoxType", "whispercpp", "whisper-server.exe" );
			}
		}

		/// <summary>Start (or restart) the server with the given model. Blocks until ready, or throws.</summary>
		public void Start( string model, int beamSize )
		{
			Stop();
			if( !File.Exists( exePath ) )
				throw new FileNotFoundException( "whisper-server.exe не знайдено: " + exePath );
			if( !File.Exists( model ) )
				throw new FileNotFoundException( "Модель не знайдено: " + model );

			port = FreePort();
			var psi = new ProcessStartInfo
			{
				FileName = exePath,
				WorkingDirectory = Path.GetDirectoryName( exePath ),
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			foreach( string a in new[]
			{
				"-m", model,
				"-bs", beamSize.ToString(),
				"-bo", beamSize.ToString(),
				"--host", "127.0.0.1",
				"--port", port.ToString(),
			} )
				psi.ArgumentList.Add( a );

			var p = Process.Start( psi ) ?? throw new InvalidOperationException( "Не вдалося запустити whisper-server" );
			p.OutputDataReceived += ( s, e ) => HandleServerLine( e.Data );
			p.ErrorDataReceived += ( s, e ) => HandleServerLine( e.Data );
			try { p.BeginOutputReadLine(); p.BeginErrorReadLine(); } catch { }

			lock( sync ) { proc = p; ModelPath = model; }
			Log.Write( $"whisper-server starting on port {port}, model='{model}', beam={beamSize}" );

			if( !WaitReady( 120000 ) )
			{
				Stop();
				throw new TimeoutException( "whisper-server не запустився вчасно (модель не завантажилась?)" );
			}
			IsRunning = true;
			Log.Write( "whisper-server ready" );
		}

		// Log every server line, and sniff the CUDA device name out of it (e.g. "Device 0: NVIDIA ...").
		void HandleServerLine( string? line )
		{
			if( line == null )
				return;
			Log.Write( "wsrv: " + line );
			int i = line.IndexOf( "Device 0:", StringComparison.Ordinal );
			if( i >= 0 )
			{
				string rest = line.Substring( i + "Device 0:".Length ).Trim();
				int comma = rest.IndexOf( ',' );
				GpuName = ( comma > 0 ? rest.Substring( 0, comma ) : rest ).Trim();
			}
		}

		// The server starts listening only after the model finishes loading, so a successful TCP
		// connection means it's ready to serve.
		bool WaitReady( int timeoutMs )
		{
			DateTime until = DateTime.UtcNow.AddMilliseconds( timeoutMs );
			while( DateTime.UtcNow < until )
			{
				Process? p;
				lock( sync ) p = proc;
				if( p == null || p.HasExited )
					return false;
				try
				{
					using var c = new TcpClient();
					IAsyncResult ar = c.BeginConnect( "127.0.0.1", port, null, null );
					if( ar.AsyncWaitHandle.WaitOne( 500 ) && c.Connected )
					{
						c.EndConnect( ar );
						return true;
					}
				}
				catch { }
				Thread.Sleep( 300 );
			}
			return false;
		}

		/// <summary>Transcribe mono 16 kHz PCM via the running server. Blocking.</summary>
		public string Transcribe( float[] mono16k, string langCode )
		{
			Process? p; int pt;
			lock( sync ) { p = proc; pt = port; }
			if( p == null || p.HasExited )
				throw new InvalidOperationException( "whisper-server не запущено" );

			byte[] wav = WavBytes( mono16k, 16000 );
			using var form = new MultipartFormDataContent();
			var file = new ByteArrayContent( wav );
			file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue( "audio/wav" );
			form.Add( file, "file", "audio.wav" );
			form.Add( new StringContent( "text" ), "response_format" );
			form.Add( new StringContent( langCode ), "language" );

			HttpResponseMessage resp = http.PostAsync( $"http://127.0.0.1:{pt}/inference", form ).GetAwaiter().GetResult();
			resp.EnsureSuccessStatusCode();
			return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
		}

		/// <summary>Transcribe an audio/video file directly (server decodes it via miniaudio). Blocking.</summary>
		public string TranscribeFile( string path, string langCode )
		{
			Process? p; int pt;
			lock( sync ) { p = proc; pt = port; }
			if( p == null || p.HasExited )
				throw new InvalidOperationException( "whisper-server не запущено" );

			byte[] bytes = File.ReadAllBytes( path );
			using var form = new MultipartFormDataContent();
			var file = new ByteArrayContent( bytes );
			file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue( "application/octet-stream" );
			form.Add( file, "file", Path.GetFileName( path ) );
			form.Add( new StringContent( "text" ), "response_format" );
			form.Add( new StringContent( langCode ), "language" );

			HttpResponseMessage resp = http.PostAsync( $"http://127.0.0.1:{pt}/inference", form ).GetAwaiter().GetResult();
			resp.EnsureSuccessStatusCode();
			return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
		}

		static int FreePort()
		{
			var l = new TcpListener( System.Net.IPAddress.Loopback, 0 );
			l.Start();
			int p = ( (System.Net.IPEndPoint)l.LocalEndpoint ).Port;
			l.Stop();
			return p;
		}

		public void Stop()
		{
			Process? p;
			lock( sync ) { p = proc; proc = null; IsRunning = false; }
			if( p != null )
			{
				try { if( !p.HasExited ) p.Kill( entireProcessTree: true ); } catch { }
				try { p.Dispose(); } catch { }
			}
		}

		public void Dispose()
		{
			Stop();
			http.Dispose();
		}

		// 16-bit PCM mono WAV in memory.
		static byte[] WavBytes( float[] samples, int rate )
		{
			using var ms = new MemoryStream( samples.Length * 2 + 64 );
			using( var bw = new BinaryWriter( ms, System.Text.Encoding.ASCII, leaveOpen: true ) )
			{
				int dataBytes = samples.Length * 2;
				bw.Write( System.Text.Encoding.ASCII.GetBytes( "RIFF" ) );
				bw.Write( 36 + dataBytes );
				bw.Write( System.Text.Encoding.ASCII.GetBytes( "WAVE" ) );
				bw.Write( System.Text.Encoding.ASCII.GetBytes( "fmt " ) );
				bw.Write( 16 );
				bw.Write( (short)1 );        // PCM
				bw.Write( (short)1 );        // mono
				bw.Write( rate );
				bw.Write( rate * 2 );        // byte rate
				bw.Write( (short)2 );        // block align
				bw.Write( (short)16 );       // bits
				bw.Write( System.Text.Encoding.ASCII.GetBytes( "data" ) );
				bw.Write( dataBytes );
				foreach( float f in samples )
					bw.Write( (short)( Math.Clamp( f, -1f, 1f ) * 32767f ) );
			}
			return ms.ToArray();
		}
	}
}
