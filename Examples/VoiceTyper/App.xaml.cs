using System.Threading;
using System.Windows;

namespace VoiceTyper
{
	public partial class App
	{
		MainWindow? main;
		Mutex? singleInstance;

		protected override void OnStartup( StartupEventArgs e )
		{
			base.OnStartup( e );

			// Single-instance guard: a second copy would install a second global keyboard hook
			// and type every recognized phrase twice. Bail out early if we're not the first.
			singleInstance = new Mutex( initiallyOwned: true, "VoxType.SingleInstance", out bool isNew );
			if( !isNew )
			{
				try
				{
					System.Windows.MessageBox.Show(
						"VoxType вже запущено (дивіться іконку в треї).",
						"VoxType",
						MessageBoxButton.OK, MessageBoxImage.Information );
				}
				catch { }
				Shutdown();
				return;
			}

			// Log otherwise-unhandled exceptions instead of dying silently.
			DispatcherUnhandledException += ( s, ex ) =>
			{
				Log.Write( "UNHANDLED (UI): " + ex.Exception );
			};
			AppDomain.CurrentDomain.UnhandledException += ( s, ex ) =>
			{
				Log.Write( "UNHANDLED (domain): " + ex.ExceptionObject );
			};

			main = new MainWindow();
			main.Show();
		}

		protected override void OnExit( ExitEventArgs e )
		{
			try { singleInstance?.ReleaseMutex(); } catch { }
			singleInstance?.Dispose();
			base.OnExit( e );
		}
	}
}
