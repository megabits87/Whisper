using System.Diagnostics;
using System.IO;

namespace VoiceTyper
{
	/// <summary>
	/// whisper-server only decodes WAV/MP3/FLAC/OGG natively. For anything else (m4a, wma, and video
	/// containers like mp4/mkv/mov), we use ffmpeg to extract the audio track and downmix to 16 kHz mono WAV.
	/// </summary>
	static class MediaConverter
	{
		static readonly string[] native = { ".wav", ".mp3", ".flac", ".ogg" };

		public static bool IsNative( string path ) =>
			Array.IndexOf( native, Path.GetExtension( path ).ToLowerInvariant() ) >= 0;

		/// <summary>Locate ffmpeg: bundled next to whisper-server, else rely on PATH.</summary>
		public static string FindFfmpeg( string? serverExe )
		{
			try
			{
				string? dir = string.IsNullOrEmpty( serverExe ) ? null : Path.GetDirectoryName( serverExe );
				if( dir != null )
				{
					string bundled = Path.Combine( dir, "ffmpeg.exe" );
					if( File.Exists( bundled ) )
						return bundled;
				}
			}
			catch { }
			return "ffmpeg"; // resolved via PATH
		}

		/// <summary>
		/// Returns a path to feed the recognizer. Native formats are returned unchanged; everything else is
		/// converted (audio track extracted) into a temp 16 kHz mono WAV. Set <paramref name="isTemp"/> tells
		/// the caller to delete the result afterwards. Throws if ffmpeg is needed but unavailable / fails.
		/// </summary>
		public static string Prepare( string input, string? serverExe, out bool isTemp )
		{
			isTemp = false;
			if( IsNative( input ) )
				return input;

			string ffmpeg = FindFfmpeg( serverExe );
			string tmp = Path.Combine( Path.GetTempPath(), "wvt_" + Guid.NewGuid().ToString( "N" ) + ".wav" );

			var psi = new ProcessStartInfo
			{
				FileName = ffmpeg,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardError = true,
				RedirectStandardOutput = true,
			};
			// -vn: drop video; -ac 1: mono; -ar 16000: 16 kHz; pick the default audio track.
			foreach( string a in new[] { "-y", "-i", input, "-vn", "-ac", "1", "-ar", "16000", "-f", "wav", tmp } )
				psi.ArgumentList.Add( a );

			Process p;
			try
			{
				p = Process.Start( psi ) ?? throw new InvalidOperationException();
			}
			catch
			{
				throw new InvalidOperationException(
					"Для цього формату потрібен ffmpeg, але його не знайдено. Запустіть Tools\\setup-whispercpp.ps1 або встановіть ffmpeg." );
			}

			string err = p.StandardError.ReadToEnd();
			p.WaitForExit();
			if( p.ExitCode != 0 || !File.Exists( tmp ) || new FileInfo( tmp ).Length < 64 )
			{
				try { File.Delete( tmp ); } catch { }
				throw new InvalidOperationException( "ffmpeg не зміг витягти аудіо: " + LastLine( err ) );
			}
			isTemp = true;
			return tmp;
		}

		static string LastLine( string s )
		{
			string[] lines = s.Split( '\n' );
			for( int i = lines.Length - 1; i >= 0; i-- )
				if( !string.IsNullOrWhiteSpace( lines[ i ] ) )
					return lines[ i ].Trim();
			return "";
		}
	}
}
