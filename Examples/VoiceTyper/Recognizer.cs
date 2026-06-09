using System.IO;

namespace VoiceTyper
{
	/// <summary>Owns the whisper.cpp server backend and turns PCM into text.</summary>
	sealed class Recognizer : IDisposable
	{
		WhisperServer? server;
		readonly object sync = new object();

		public bool IsLoaded => server?.IsRunning == true;
		public bool IsMultilingual { get; private set; } = true;
		public string ModelPath { get; private set; } = "";

		/// <summary>GPU the backend reported using (empty until a model is loaded).</summary>
		public string GpuName => server?.GpuName ?? "";

		/// <summary>Why the last <see cref="Transcribe"/> returned empty (null when it produced text or wasn't called).</summary>
		public string? LastSkipReason { get; private set; }

		/// <summary>Path to whisper-server.exe (GPU build).</summary>
		public string ServerExe { get; set; } = WhisperServer.DefaultExe;

		/// <summary>Beam-search width for the server decoder.</summary>
		public int BeamSize { get; set; } = 5;

		/// <summary>Start the whisper.cpp server with the given model. Blocking; call on a background thread.</summary>
		/// <param name="adapter">Ignored — whisper.cpp uses the CUDA GPU automatically. Kept for call-site compatibility.</param>
		public void Load( string path, string? adapter = null )
		{
			Unload();
			var s = new WhisperServer( ServerExe );
			s.Start( path, BeamSize );
			lock( sync )
			{
				server = s;
				ModelPath = path;
				// Whisper English-only models end in ".en" (e.g. base.en); everything else is multilingual.
				string name = Path.GetFileNameWithoutExtension( path ).ToLowerInvariant();
				IsMultilingual = !name.EndsWith( ".en" ) && !name.EndsWith( "-en" ) && !name.Contains( ".en." );
			}
		}

		/// <summary>Transcribe mono 16 kHz PCM. <paramref name="langCode"/> is e.g. "uk"/"en"/"ru".</summary>
		public string Transcribe( float[] mono16k, string langCode )
		{
			WhisperServer? s;
			lock( sync ) s = server;

			LastSkipReason = null;
			if( s == null )
				return "";
			if( mono16k.Length < 16000 / 4 ) // shorter than ~0.25s: ignore
			{
				LastSkipReason = "закоротко";
				return "";
			}

			// Peak for the silence gate — skip silent clips (Whisper hallucinates caption phrases on silence).
			float peak = 0;
			for( int i = 0; i < mono16k.Length; i++ )
			{
				float a = Math.Abs( mono16k[ i ] );
				if( a > peak ) peak = a;
			}
			Log.Write( $"level peak={peak:0.000}" );
			if( peak < 0.012f )
			{
				LastSkipReason = "тиша";
				return "";
			}

			// Gentle peak normalization into a copy. Deliberately NO filtering/denoise — raw PCM works best.
			float gain = Math.Min( 0.95f / peak, 25f );
			float[] samples = (float[])mono16k.Clone();
			if( gain > 1.05f )
				for( int i = 0; i < samples.Length; i++ )
					samples[ i ] *= gain;

			string outText = s.Transcribe( samples, langCode ).Trim();
			// whisper-server returns one line per segment; collapse ALL whitespace/newlines into single
			// spaces so dictation never injects an Enter — that would send the message early in
			// chat/Enter-to-send windows, leaving only the last segment in the input box.
			outText = System.Text.RegularExpressions.Regex.Replace( outText, @"\s+", " " ).Trim();
			if( IsHallucination( outText ) )
			{
				LastSkipReason = "відфільтровано (схоже на артефакт тиші)";
				return "";
			}
			return outText;
		}

		/// <summary>Transcribe an audio/video file via the server. Non-native formats are converted with ffmpeg.</summary>
		public string TranscribeFile( string path, string langCode )
		{
			WhisperServer? s;
			lock( sync ) s = server;
			if( s == null )
				return "";

			string send = MediaConverter.Prepare( path, ServerExe, out bool isTemp );
			try
			{
				return s.TranscribeFile( send, langCode ).Trim();
			}
			finally
			{
				if( isTemp )
					try { System.IO.File.Delete( send ); } catch { }
			}
		}

		// Credit artifacts Whisper emits on silence. Users never dictate these, so a substring match is safe.
		static readonly string[] hallucinationCredits =
		{
			"amara.org", "subtitles by", "subtitled by", "ukraïner", "украйнер",
			"редактор субтитрів", "субтитрував",
		};

		// Phrases that ARE real speech a user might say — only treat as hallucinations when they're the
		// ENTIRE output (exact match after trimming punctuation).
		static readonly string[] hallucinationWhole =
		{
			"дякую за перегляд", "дякую за увагу", "спасибо за просмотр",
			"thanks for watching",
		};

		// Bracketed non-speech tokens Whisper emits, e.g. [music] / (applause). Not arbitrary bracketed text.
		static readonly string[] nonSpeech =
		{
			"music", "музика", "музыка", "applause", "оплески", "аплодисменты",
			"silence", "тиша", "blank_audio", "blank audio", "sound", "звук",
			"laughter", "сміх", "смех", "noise", "шум",
		};

		static bool IsHallucination( string text )
		{
			if( string.IsNullOrWhiteSpace( text ) )
				return true;
			string t = text.Trim();
			if( IsNonSpeechToken( t ) )
				return true;
			string low = t.ToLowerInvariant();
			foreach( string c in hallucinationCredits )
				if( low.Contains( c ) )
					return true;
			string norm = low.TrimEnd( '.', '!', '?', '…', ' ', ',' );
			foreach( string w in hallucinationWhole )
				if( norm == w )
					return true;
			return false;
		}

		static bool IsNonSpeechToken( string t )
		{
			if( t.Length < 2 )
				return false;
			char a = t[ 0 ], b = t[ ^1 ];
			bool bracketed = ( a == '[' && b == ']' ) || ( a == '(' && b == ')' );
			if( !bracketed )
				return false;
			string inner = t.Substring( 1, t.Length - 2 ).Trim().ToLowerInvariant();
			return Array.IndexOf( nonSpeech, inner ) >= 0;
		}

		public void Unload()
		{
			lock( sync )
			{
				server?.Dispose();
				server = null;
				ModelPath = "";
				IsMultilingual = true;
			}
		}

		public void Dispose() => Unload();
	}
}
