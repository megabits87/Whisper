using System.IO;
using System.Net.Http;
using System.Threading;

namespace VoiceTyper
{
	/// <summary>
	/// Downloads a Whisper GGML model from Hugging Face on first run, so the installer doesn't have to
	/// bundle a multi-GB model. Files go to %LocalAppData%\VoxType\models.
	/// </summary>
	static class ModelDownloader
	{
		/// <summary>Recommended default model: fast and accurate, multilingual (incl. Ukrainian).</summary>
		public const string DefaultModel = "ggml-large-v3-turbo.bin";

		const string BaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

		public static string ModelsDir
		{
			get
			{
				string d = Path.Combine(
					Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ),
					"VoxType", "models" );
				Directory.CreateDirectory( d );
				return d;
			}
		}

		public static string DefaultModelPath => Path.Combine( ModelsDir, DefaultModel );

		/// <summary>Download a model file, reporting progress in [0..1]. Blocking; call on a background thread.
		/// Writes to a .part file first, then atomically moves it into place.</summary>
		public static void Download( string fileName, Action<double>? progress, CancellationToken ct = default )
		{
			string dest = Path.Combine( ModelsDir, fileName );
			string tmp = dest + ".part";

			using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
			using HttpResponseMessage resp = http
				.GetAsync( BaseUrl + fileName, HttpCompletionOption.ResponseHeadersRead, ct )
				.GetAwaiter().GetResult();
			resp.EnsureSuccessStatusCode();
			long? total = resp.Content.Headers.ContentLength;

			using( Stream src = resp.Content.ReadAsStreamAsync( ct ).GetAwaiter().GetResult() )
			using( var dst = new FileStream( tmp, FileMode.Create, FileAccess.Write, FileShare.None ) )
			{
				byte[] buf = new byte[ 1 << 20 ];
				long done = 0;
				int read;
				while( ( read = src.Read( buf, 0, buf.Length ) ) > 0 )
				{
					dst.Write( buf, 0, read );
					done += read;
					if( total is > 0 )
						progress?.Invoke( (double)done / total.Value );
				}
			}

			if( File.Exists( dest ) )
				File.Delete( dest );
			File.Move( tmp, dest );
		}
	}
}
