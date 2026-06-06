using System.Windows;

namespace VoiceTyper
{
	public partial class App
	{
		MainWindow? main;

		protected override void OnStartup( StartupEventArgs e )
		{
			base.OnStartup( e );
			main = new MainWindow();
			main.Show();
		}
	}
}
