using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceTyper
{
	/// <summary>Captures audio from a WASAPI device while recording, then returns mono 16 kHz float PCM.</summary>
	sealed class AudioRecorder : IDisposable
	{
		readonly object sync = new object();
		WasapiCapture? capture;
		List<float> mono = new List<float>( 16000 * 10 );
		int sourceRate;
		int sourceChannels;

		public bool IsRecording { get; private set; }

		volatile float currentLevel;
		/// <summary>Most recent input level in [0..1], for live visualisation.</summary>
		public float CurrentLevel => currentLevel;

		/// <summary>Enumerate active capture (input) devices.</summary>
		public static List<MMDevice> ListDevices()
		{
			var result = new List<MMDevice>();
			using var en = new MMDeviceEnumerator();
			foreach( var d in en.EnumerateAudioEndPoints( DataFlow.Capture, DeviceState.Active ) )
				result.Add( d );
			return result;
		}

		public static MMDevice? DefaultDevice()
		{
			using var en = new MMDeviceEnumerator();
			try { return en.GetDefaultAudioEndpoint( DataFlow.Capture, Role.Communications ); }
			catch { return null; }
		}

		/// <summary>Begin capturing from the given device (null = default communications device).</summary>
		public void Start( MMDevice? device )
		{
			lock( sync )
			{
				if( IsRecording )
					return;

				mono = new List<float>( 16000 * 10 );
				currentLevel = 0;
				capture = device != null ? new WasapiCapture( device ) : new WasapiCapture();
				WaveFormat fmt = capture.WaveFormat;
				sourceRate = fmt.SampleRate;
				sourceChannels = fmt.Channels;
				capture.DataAvailable += onData;
				capture.StartRecording();
				IsRecording = true;
			}
		}

		void onData( object? sender, WaveInEventArgs e )
		{
			WasapiCapture? c = capture;
			if( c == null )
				return;
			WaveFormat fmt = c.WaveFormat;
			int channels = fmt.Channels;
			float peak = 0;

			// Convert the raw bytes into mono float samples at the source sample rate.
			if( fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32 )
			{
				int count = e.BytesRecorded / 4;
				lock( sync )
				{
					for( int i = 0; i < count; i += channels )
					{
						float sum = 0;
						for( int ch = 0; ch < channels && i + ch < count; ch++ )
							sum += BitConverter.ToSingle( e.Buffer, ( i + ch ) * 4 );
						float s = sum / channels;
						mono.Add( s );
						float a = Math.Abs( s );
						if( a > peak ) peak = a;
					}
				}
			}
			else if( fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16 )
			{
				int count = e.BytesRecorded / 2;
				lock( sync )
				{
					for( int i = 0; i < count; i += channels )
					{
						float sum = 0;
						for( int ch = 0; ch < channels && i + ch < count; ch++ )
							sum += BitConverter.ToInt16( e.Buffer, ( i + ch ) * 2 ) / 32768f;
						float s = sum / channels;
						mono.Add( s );
						float a = Math.Abs( s );
						if( a > peak ) peak = a;
					}
				}
			}

			// Scale peak to a lively visual range; decay slowly so bars don't flicker to zero.
			float scaled = Math.Clamp( peak * 3.0f, 0f, 1f );
			currentLevel = Math.Max( scaled, currentLevel * 0.6f );
		}

		/// <summary>Stop capturing and return the recorded audio resampled to mono 16 kHz.</summary>
		public float[] Stop()
		{
			WasapiCapture? c;
			float[] src;
			int rate, ch;
			lock( sync )
			{
				if( !IsRecording )
					return Array.Empty<float>();
				IsRecording = false;
				c = capture;
				capture = null;
				rate = sourceRate;
				ch = sourceChannels;
			}

			if( c != null )
			{
				try { c.StopRecording(); } catch { }
				c.DataAvailable -= onData;
				c.Dispose();
			}

			lock( sync )
				src = mono.ToArray();

			if( src.Length == 0 )
				return src;
			if( rate == 16000 )
				return src;

			return Resample( src, rate, 16000 );
		}

		static float[] Resample( float[] src, int srcRate, int dstRate )
		{
			var provider = new RawSourceSampleProvider( src, srcRate );
			var resampler = new WdlResamplingSampleProvider( provider, dstRate );
			var outList = new List<float>( (int)( (long)src.Length * dstRate / srcRate ) + 16 );
			float[] buffer = new float[ 16000 ];
			int read;
			while( ( read = resampler.Read( buffer, 0, buffer.Length ) ) > 0 )
				outList.AddRange( buffer.AsSpan( 0, read ).ToArray() );
			return outList.ToArray();
		}

		public void Dispose()
		{
			lock( sync )
			{
				if( capture != null )
				{
					try { capture.StopRecording(); } catch { }
					capture.Dispose();
					capture = null;
				}
				IsRecording = false;
			}
		}

		/// <summary>Minimal mono <see cref="ISampleProvider"/> over an in-memory float array.</summary>
		sealed class RawSourceSampleProvider : ISampleProvider
		{
			readonly float[] data;
			int pos;
			public RawSourceSampleProvider( float[] data, int sampleRate )
			{
				this.data = data;
				WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat( sampleRate, 1 );
			}
			public WaveFormat WaveFormat { get; }
			public int Read( float[] buffer, int offset, int count )
			{
				int n = Math.Min( count, data.Length - pos );
				if( n <= 0 )
					return 0;
				Array.Copy( data, pos, buffer, offset, n );
				pos += n;
				return n;
			}
		}
	}
}
