using Whisper;

namespace VoiceTyper
{
	/// <summary>Owns the loaded Whisper model and context, and turns PCM into text.</summary>
	sealed class Recognizer : IDisposable
	{
		iModel? model;
		Context? context;
		readonly object sync = new object();

		public bool IsLoaded => context != null;
		public bool IsMultilingual { get; private set; }
		public string ModelPath { get; private set; } = "";

		/// <summary>Load a GGML model from disk. Blocking; call on a background thread.</summary>
		/// <param name="adapter">Exact GPU adapter name (or null/empty to let the engine choose).</param>
		public void Load( string path, string? adapter = null )
		{
			Unload();
			iModel m = Library.loadModel( path, eGpuModelFlags.None,
				string.IsNullOrWhiteSpace( adapter ) ? null : adapter );
			Context ctx = m.createContext();
			lock( sync )
			{
				model = m;
				context = ctx;
				IsMultilingual = m.isMultilingual();
				ModelPath = path;
			}
		}

		/// <summary>Transcribe mono 16 kHz PCM. Returns the recognized text (may be empty).</summary>
		public string Transcribe( float[] mono16k, eLanguage language )
		{
			lock( sync )
			{
				if( context == null )
					return "";
				if( mono16k.Length < 16000 / 4 ) // shorter than ~0.25s: ignore
					return "";

				// Silence gate: Whisper hallucinates caption-credit phrases on silence, so skip quiet clips.
				float peak = 0;
				for( int i = 0; i < mono16k.Length; i++ )
				{
					float a = Math.Abs( mono16k[ i ] );
					if( a > peak ) peak = a;
				}
				Log.Write( $"level peak={peak:0.000}" );
				if( peak < 0.012f )
					return "";
				// Normalize quiet input to a healthy level — greatly improves accuracy for soft mics.
				float gain = Math.Min( 0.95f / peak, 25f );
				if( gain > 1.05f )
					for( int i = 0; i < mono16k.Length; i++ )
						mono16k[ i ] *= gain;

				context.parameters.language = language;
				context.parameters.setFlag( eFullParamsFlags.Translate, false );
				context.parameters.setFlag( eFullParamsFlags.PrintTimestamps, false );
				context.parameters.setFlag( eFullParamsFlags.PrintRealtime, false );
				context.parameters.setFlag( eFullParamsFlags.SingleSegment, false );
				context.parameters.setFlag( eFullParamsFlags.NoContext, true );

				using var buf = new ManagedPcmBuffer( mono16k );
				context.runFull( buf );

				var res = context.results();
				if( res.segments.Length == 0 )
					return "";

				var sb = new System.Text.StringBuilder();
				foreach( var seg in res.segments )
					sb.Append( seg.text );
				string outText = sb.ToString().Trim();
				return IsHallucination( outText ) ? "" : outText;
			}
		}

		// Common Whisper "silence hallucinations" — caption credits from its training data.
		static readonly string[] hallucinationMarkers =
		{
			"субтитр", "ukraïner", "украйнер",
			"дякую за перегляд", "спасибо за просмотр",
			"thanks for watching", "subtitles by", "amara.org", "редактор субтитрів",
		};

		static bool IsHallucination( string text )
		{
			if( string.IsNullOrWhiteSpace( text ) )
				return true;
			string t = text.Trim();
			if( ( t.StartsWith( "[" ) && t.EndsWith( "]" ) ) || ( t.StartsWith( "(" ) && t.EndsWith( ")" ) ) )
				return true;
			string low = t.ToLowerInvariant();
			foreach( string m in hallucinationMarkers )
				if( low.Contains( m ) )
					return true;
			return false;
		}

		public void Unload()
		{
			lock( sync )
			{
				( context as IDisposable )?.Dispose();
				model?.Dispose();
				context = null;
				model = null;
				ModelPath = "";
				IsMultilingual = false;
			}
		}

		public void Dispose() => Unload();
	}
}
