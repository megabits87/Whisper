using Microsoft.Win32;

namespace VoiceTyper
{
	/// <summary>
	/// Runs VoxType automatically when the user logs in, via the per-user
	/// HKCU\...\Run registry key (no admin rights needed).
	/// </summary>
	static class Autostart
	{
		const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
		const string ValueName = "VoxType";

		/// <summary>Add or remove the run-at-login registry entry pointing at the current executable.</summary>
		public static void Apply( bool enable )
		{
			try
			{
				using RegistryKey? key = Registry.CurrentUser.OpenSubKey( RunKey, writable: true );
				if( key == null )
					return;
				if( enable )
				{
					string? exe = Environment.ProcessPath;
					if( !string.IsNullOrEmpty( exe ) )
						key.SetValue( ValueName, "\"" + exe + "\"" );
				}
				else if( key.GetValue( ValueName ) != null )
				{
					key.DeleteValue( ValueName, throwOnMissingValue: false );
				}
			}
			catch { }
		}

		/// <summary>True if the run-at-login entry currently exists.</summary>
		public static bool IsEnabled()
		{
			try
			{
				using RegistryKey? key = Registry.CurrentUser.OpenSubKey( RunKey );
				return key?.GetValue( ValueName ) != null;
			}
			catch { return false; }
		}
	}
}
