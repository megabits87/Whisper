using System.IO;
using System.Text.Json;

namespace VoiceTyper
{
	sealed class AppSettings
	{
		public string ModelPath { get; set; } = "";
		public string? DeviceId { get; set; }          // null = default communications device
		public string LanguageMode { get; set; } = "uk"; // uk | en | ru
		public string Insert { get; set; } = "SendInput"; // SendInput | Clipboard
		// Stable hotkey identity (see MainWindow.HotkeyChoices). MUST default to null, not a real id:
		// legacy settings.json files have no HotkeyId, and a non-null default would override their HotkeyVk.
		public string? HotkeyId { get; set; }
		public int HotkeyVk { get; set; } = 0x14;        // fresh-install default + legacy fallback (VK_CAPITAL)
		public bool AppendSpace { get; set; } = true;
		public bool SwallowHotkey { get; set; } = true;
		public bool AutoStart { get; set; } = false;   // run at Windows login

		// whisper.cpp GPU backend. Empty = use the per-user default (see WhisperServer.DefaultExe).
		public string WhisperServerExe { get; set; } = "";
		public int BeamSize { get; set; } = 5;

		/// <summary>True when the last <see cref="Load"/> found an existing file but failed to read it.</summary>
		public static bool LastLoadFailed { get; private set; }

		static string Dir
		{
			get
			{
				string dir = Path.Combine(
					Environment.GetFolderPath( Environment.SpecialFolder.ApplicationData ),
					"VoxType" );
				Directory.CreateDirectory( dir );
				return dir;
			}
		}

		static string FilePath => Path.Combine( Dir, "settings.json" );
		static string BakPath => Path.Combine( Dir, "settings.bak" );

		public static AppSettings Load()
		{
			LastLoadFailed = false;
			// Try the live file, then the backup (in case a previous save was interrupted).
			foreach( string path in new[] { FilePath, BakPath } )
			{
				try
				{
					if( !File.Exists( path ) )
						continue;
					string json = File.ReadAllText( path );
					var s = JsonSerializer.Deserialize<AppSettings>( json );
					if( s != null )
						return s;
				}
				catch( Exception ex )
				{
					LastLoadFailed = true;
					Log.Write( $"settings load failed ({Path.GetFileName( path )}): {ex.Message}" );
				}
			}
			return new AppSettings();
		}

		public void Save()
		{
			try
			{
				string json = JsonSerializer.Serialize( this, new JsonSerializerOptions { WriteIndented = true } );
				string tmp = FilePath + ".tmp";
				File.WriteAllText( tmp, json );
				// Atomic replace so an interrupted write can never corrupt the live file; keep a .bak.
				if( File.Exists( FilePath ) )
					File.Replace( tmp, FilePath, BakPath );
				else
					File.Move( tmp, FilePath );
			}
			catch( Exception ex )
			{
				Log.Write( "settings save failed: " + ex.Message );
			}
		}
	}
}
