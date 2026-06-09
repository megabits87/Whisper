using System.IO;

namespace VoiceTyper
{
	/// <summary>Minimal append-only diagnostic log at %AppData%\WhisperVoiceTyper\log.txt.</summary>
	static class Log
	{
		static readonly object sync = new object();
		const long MaxBytes = 2 * 1024 * 1024; // rotate once the log passes 2 MB
		static bool rotateChecked;

		public static bool Verbose = false;

		public static string FilePath
		{
			get
			{
				string dir = Path.Combine(
					Environment.GetFolderPath( Environment.SpecialFolder.ApplicationData ),
					"VoxType" );
				Directory.CreateDirectory( dir );
				return Path.Combine( dir, "log.txt" );
			}
		}

		public static void Write( string message )
		{
			try
			{
				lock( sync )
				{
					string path = FilePath;
					RotateIfNeeded( path );
					File.AppendAllText( path, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}" );
				}
			}
			catch { }
		}

		// Caller must hold `sync`. Rotates at most once per process, and only when oversized.
		static void RotateIfNeeded( string path )
		{
			if( rotateChecked )
				return;
			rotateChecked = true;
			try
			{
				var fi = new FileInfo( path );
				if( fi.Exists && fi.Length > MaxBytes )
				{
					string old = path + ".old";
					if( File.Exists( old ) )
						File.Delete( old );
					File.Move( path, old );
				}
			}
			catch { }
		}
	}
}
