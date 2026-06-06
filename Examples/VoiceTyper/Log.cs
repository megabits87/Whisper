using System.IO;

namespace VoiceTyper
{
	/// <summary>Minimal append-only diagnostic log at %AppData%\WhisperVoiceTyper\log.txt.</summary>
	static class Log
	{
		static readonly object sync = new object();
		public static bool Verbose = false;

		public static string FilePath
		{
			get
			{
				string dir = Path.Combine(
					Environment.GetFolderPath( Environment.SpecialFolder.ApplicationData ),
					"WhisperVoiceTyper" );
				Directory.CreateDirectory( dir );
				return Path.Combine( dir, "log.txt" );
			}
		}

		public static void Write( string message )
		{
			try
			{
				lock( sync )
					File.AppendAllText( FilePath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}" );
			}
			catch { }
		}
	}
}
