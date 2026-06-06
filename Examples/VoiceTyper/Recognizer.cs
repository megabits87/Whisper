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
				return sb.ToString().Trim();
			}
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
