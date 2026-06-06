using System.IO;
using System.Text.Json;

namespace VoiceTyper
{
	sealed class AppSettings
	{
		public string ModelPath { get; set; } = "";
		public string? DeviceId { get; set; }          // null = default communications device
		public string? GpuAdapter { get; set; }         // null/empty = auto; otherwise exact adapter name
		public string LanguageMode { get; set; } = "uk"; // auto | uk | en | ru
		public string Insert { get; set; } = "SendInput"; // SendInput | Clipboard
		public int HotkeyVk { get; set; } = 0xA3;        // VK_RCONTROL
		public bool AppendSpace { get; set; } = true;
		public bool SwallowHotkey { get; set; } = true;

		static string FilePath
		{
			get
			{
				string dir = Path.Combine(
					Environment.GetFolderPath( Environment.SpecialFolder.ApplicationData ),
					"WhisperVoiceTyper" );
				Directory.CreateDirectory( dir );
				return Path.Combine( dir, "settings.json" );
			}
		}

		public static AppSettings Load()
		{
			try
			{
				if( File.Exists( FilePath ) )
				{
					string json = File.ReadAllText( FilePath );
					return JsonSerializer.Deserialize<AppSettings>( json ) ?? new AppSettings();
				}
			}
			catch { }
			return new AppSettings();
		}

		public void Save()
		{
			try
			{
				string json = JsonSerializer.Serialize( this, new JsonSerializerOptions { WriteIndented = true } );
				File.WriteAllText( FilePath, json );
			}
			catch { }
		}
	}
}
