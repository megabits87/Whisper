using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using Whisper;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace VoiceTyper
{
	public partial class MainWindow : Window
	{
		readonly AppSettings settings;
		readonly Recognizer recognizer = new Recognizer();
		readonly AudioRecorder recorder = new AudioRecorder();
		ListeningOverlay? overlay;
		KeyboardHook? hook;
		WinForms.NotifyIcon? trayIcon;
		Drawing.Icon? trayIco;

		List<MMDevice> devices = new List<MMDevice>();
		string[] gpuAdapters = Array.Empty<string>();

		volatile bool busy;
		volatile bool recording;
		bool initializing;

		static readonly (string name, int[] vks)[] HotkeyChoices =
		{
			("Caps Lock (рекомендовано)", new[] { 0x14 }),
			("Правий Ctrl", new[] { 0xA3 }),
			("Лівий Ctrl", new[] { 0xA2 }),
			("Ctrl (будь-який)", new[] { 0xA2, 0xA3 }),
			("Правий Alt", new[] { 0xA5 }),
			("Правий Shift", new[] { 0xA1 }),
			("Scroll Lock", new[] { 0x91 }),
			("Pause / Break", new[] { 0x13 }),
			("F8", new[] { 0x77 }),
			("F9", new[] { 0x78 }),
		};

		static readonly (string label, string code)[] LangChoices =
		{
			("Українська", "uk"),
			("English", "en"),
			("Русский", "ru"),
		};

		static readonly SolidColorBrush Green = new SolidColorBrush( Color.FromRgb( 0x3D, 0xD1, 0x7A ) );
		static readonly SolidColorBrush Orange = new SolidColorBrush( Color.FromRgb( 0xFF, 0xA5, 0x36 ) );
		static readonly SolidColorBrush Red = new SolidColorBrush( Color.FromRgb( 0xE0, 0x5A, 0x5A ) );
		static readonly SolidColorBrush Dim = new SolidColorBrush( Color.FromRgb( 0x8A, 0x90, 0xA2 ) );

		public MainWindow()
		{
			InitializeComponent();
			settings = AppSettings.Load();
			Loaded += OnLoaded;
			Closing += OnClosing;
			StateChanged += OnStateChanged;
		}

		// ---------- lifecycle ----------

		void OnLoaded( object sender, RoutedEventArgs e )
		{
			trayIco = AppIcon.Create( 32 );
			try
			{
				Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
					trayIco.Handle, Int32Rect.Empty,
					System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions() );
			}
			catch { }

			initializing = true;

			foreach( var l in LangChoices ) CmbLang.Items.Add( l.label );
			CmbInsert.Items.Add( "Емуляція набору (SendInput)" );
			CmbInsert.Items.Add( "Буфер обміну (Ctrl+V)" );
			foreach( var h in HotkeyChoices ) CmbHotkey.Items.Add( h.name );

			PopulateDevices();
			PopulateGpus();

			TxtModel.Text = settings.ModelPath;
			CmbLang.SelectedIndex = Math.Max( 0, Array.FindIndex( LangChoices, l => l.code == settings.LanguageMode ) );
			CmbInsert.SelectedIndex = settings.Insert == "Clipboard" ? 1 : 0;
			CmbHotkey.SelectedIndex = Math.Max( 0, Array.FindIndex( HotkeyChoices, h => h.vks[ 0 ] == settings.HotkeyVk ) );
			ChkSpace.IsChecked = settings.AppendSpace;
			ChkSwallow.IsChecked = settings.SwallowHotkey;

			initializing = false;

			overlay = new ListeningOverlay( () => recorder.CurrentLevel );

			ApplyHotkey();
			SetStatus( "Завантажте модель…", Green );

			if( !string.IsNullOrWhiteSpace( settings.ModelPath ) && File.Exists( settings.ModelPath ) )
				StartLoadModel( settings.ModelPath );
		}

		void OnClosing( object? sender, System.ComponentModel.CancelEventArgs e )
		{
			SaveSettings();
			hook?.Dispose();
			overlay?.Dispose();
			recorder.Dispose();
			recognizer.Dispose();
			if( trayIcon != null )
			{
				trayIcon.Visible = false;
				trayIcon.Dispose();
			}
			trayIco?.Dispose();
			System.Windows.Application.Current.Shutdown();
		}

		void OnStateChanged( object? sender, EventArgs e )
		{
			if( WindowState == WindowState.Minimized )
			{
				Hide();
				ShowInTaskbar = false;
				EnsureTray();
				trayIcon!.Visible = true;
			}
		}

		void RestoreFromTray()
		{
			Show();
			ShowInTaskbar = true;
			WindowState = WindowState.Normal;
			Activate();
		}

		void EnsureTray()
		{
			if( trayIcon != null )
				return;
			var menu = new WinForms.ContextMenuStrip();
			menu.Items.Add( "Показати", null, ( s, e ) => Dispatcher.Invoke( RestoreFromTray ) );
			menu.Items.Add( new WinForms.ToolStripSeparator() );
			menu.Items.Add( "Вийти", null, ( s, e ) => Dispatcher.Invoke( () => { trayIcon!.Visible = false; Close(); } ) );
			trayIcon = new WinForms.NotifyIcon
			{
				Icon = trayIco,
				Text = "Whisper Voice Typer",
				Visible = true,
				ContextMenuStrip = menu,
			};
			trayIcon.DoubleClick += ( s, e ) => Dispatcher.Invoke( RestoreFromTray );
		}

		// ---------- title bar ----------

		void BtnMin_Click( object sender, RoutedEventArgs e ) => WindowState = WindowState.Minimized;
		void BtnMax_Click( object sender, RoutedEventArgs e ) =>
			WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
		void BtnClose_Click( object sender, RoutedEventArgs e ) => Close();

		// ---------- devices / gpus ----------

		void PopulateDevices()
		{
			devices = AudioRecorder.ListDevices();
			CmbDevice.Items.Clear();
			CmbDevice.Items.Add( "За замовчуванням (зв'язок)" );
			foreach( var d in devices )
				CmbDevice.Items.Add( d.FriendlyName );

			int idx = 0;
			if( settings.DeviceId != null )
			{
				int found = devices.FindIndex( d => d.ID == settings.DeviceId );
				if( found >= 0 ) idx = found + 1;
			}
			CmbDevice.SelectedIndex = idx;
		}

		MMDevice? SelectedDevice()
		{
			int i = CmbDevice.SelectedIndex;
			return i <= 0 ? null : devices[ i - 1 ];
		}

		void PopulateGpus()
		{
			try { gpuAdapters = Library.listGraphicAdapters(); }
			catch { gpuAdapters = Array.Empty<string>(); }

			CmbGpu.Items.Clear();
			CmbGpu.Items.Add( "Авто" );
			foreach( var a in gpuAdapters )
				CmbGpu.Items.Add( a );

			int idx = 0;
			if( !string.IsNullOrWhiteSpace( settings.GpuAdapter ) )
			{
				int found = Array.FindIndex( gpuAdapters, a => a == settings.GpuAdapter );
				if( found >= 0 ) idx = found + 1;
			}
			else
			{
				int disc = Array.FindIndex( gpuAdapters, IsDiscrete );
				if( disc >= 0 ) idx = disc + 1;
			}
			CmbGpu.SelectedIndex = idx;
		}

		static bool IsDiscrete( string name )
		{
			string n = name.ToLowerInvariant();
			if( n.Contains( "intel" ) ) return false;
			return n.Contains( "nvidia" ) || n.Contains( "geforce" ) || n.Contains( "rtx" ) ||
				n.Contains( "radeon" ) || n.Contains( "amd" );
		}

		string? SelectedAdapter()
		{
			int i = CmbGpu.SelectedIndex;
			return i <= 0 ? null : gpuAdapters[ i - 1 ];
		}

		void CmbGpu_SelectionChanged( object sender, SelectionChangedEventArgs e )
		{
			if( initializing ) return;
			if( !string.IsNullOrWhiteSpace( TxtModel.Text ) && File.Exists( TxtModel.Text ) )
				StartLoadModel( TxtModel.Text );
		}

		// ---------- model ----------

		void BtnBrowse_Click( object sender, RoutedEventArgs e )
		{
			var ofd = new Microsoft.Win32.OpenFileDialog
			{
				Title = "Виберіть модель Whisper (GGML)",
				Filter = "Моделі GGML (*.bin)|*.bin|Усі файли (*.*)|*.*",
			};
			try { if( !string.IsNullOrWhiteSpace( TxtModel.Text ) ) ofd.InitialDirectory = Path.GetDirectoryName( TxtModel.Text ); } catch { }
			if( ofd.ShowDialog( this ) == true )
			{
				TxtModel.Text = ofd.FileName;
				StartLoadModel( ofd.FileName );
			}
		}

		void BtnReload_Click( object sender, RoutedEventArgs e ) => StartLoadModel( TxtModel.Text );

		void StartLoadModel( string path )
		{
			if( string.IsNullOrWhiteSpace( path ) || !File.Exists( path ) )
			{
				SetStatus( "Файл моделі не знайдено", Red );
				return;
			}
			string? adapter = SelectedAdapter();
			SetStatus( "Завантаження моделі…", Orange );
			LblModelInfo.Text = adapter != null ? "GPU: " + adapter : "GPU: авто";
			TxtGpu.Text = "GPU: " + ( adapter != null ? ShortGpu( adapter ) : "авто" );
			BtnReload.IsEnabled = BtnBrowse.IsEnabled = false;
			Log.Write( $"loading model '{path}' on GPU='{adapter ?? "auto"}'" );

			Task.Run( () =>
			{
				try
				{
					recognizer.Load( path, adapter );
					Dispatcher.Invoke( () => OnModelLoaded( true, null ) );
				}
				catch( Exception ex )
				{
					Dispatcher.Invoke( () => OnModelLoaded( false, ex.Message ) );
				}
			} );
		}

		void OnModelLoaded( bool ok, string? error )
		{
			BtnReload.IsEnabled = BtnBrowse.IsEnabled = true;
			if( !ok )
			{
				SetStatus( "Помилка завантаження моделі", Red );
				LblModelInfo.Text = error ?? "";
				return;
			}

			settings.ModelPath = recognizer.ModelPath;
			string ml = recognizer.IsMultilingual ? "multilingual" : "ТІЛЬКИ англійська";
			string gpu = SelectedAdapter() ?? "авто";
			LblModelInfo.Text = $"Завантажено · {ml} · {Path.GetFileName( recognizer.ModelPath )} · GPU: {ShortGpu( gpu )}";
			LblModelInfo.Foreground = recognizer.IsMultilingual ? Dim : Red;

			if( !recognizer.IsMultilingual && CurrentLanguageCode() != "en" )
				AppendLog( "УВАГА: модель розпізнається як англомовна. Для української візьміть multilingual-модель до v3 (medium / large-v2)." );

			ReadyStatus();
		}

		static string ShortGpu( string g )
		{
			if( g.Contains( "NVIDIA", StringComparison.OrdinalIgnoreCase ) ) return "NVIDIA";
			if( g.Contains( "AMD", StringComparison.OrdinalIgnoreCase ) || g.Contains( "Radeon", StringComparison.OrdinalIgnoreCase ) ) return "AMD";
			if( g.Contains( "Intel", StringComparison.OrdinalIgnoreCase ) ) return "Intel";
			return g;
		}

		// ---------- hotkey / push-to-talk ----------

		void ApplyHotkey()
		{
			int[] vks = CmbHotkey.SelectedIndex >= 0 ? HotkeyChoices[ CmbHotkey.SelectedIndex ].vks : new[] { 0x14 };
			if( hook == null )
			{
				hook = new KeyboardHook( vks );
				hook.KeyDown += OnPttDown;
				hook.KeyUp += OnPttUp;
				try { hook.Install(); }
				catch( Exception ex ) { SetStatus( "Не вдалося встановити хук: " + ex.Message, Red ); return; }
			}
			hook.Keys = vks;
			hook.Swallow = ChkSwallow.IsChecked == true;
			if( recognizer.IsLoaded ) ReadyStatus();
		}

		void CmbHotkey_SelectionChanged( object sender, SelectionChangedEventArgs e )
		{
			if( initializing ) return;
			ApplyHotkey();
		}

		void ChkSwallow_Changed( object sender, RoutedEventArgs e )
		{
			if( hook != null ) hook.Swallow = ChkSwallow.IsChecked == true;
		}

		void ReadyStatus()
		{
			string key = CmbHotkey.SelectedIndex >= 0 ? HotkeyChoices[ CmbHotkey.SelectedIndex ].name : "?";
			SetStatus( $"Готово — утримуйте «{key}» і говоріть", Green );
		}

		void OnPttDown()
		{
			Log.Write( $"PTT down (loaded={recognizer.IsLoaded} busy={busy} recording={recording})" );
			if( !recognizer.IsLoaded || busy || recording )
				return;
			try
			{
				recorder.Start( SelectedDevice() );
				recording = true;
				overlay?.ShowListening();
				SetStatus( "● Запис…", Red );
				Log.Write( "recording started" );
			}
			catch( Exception ex )
			{
				SetStatus( "Помилка мікрофона: " + ex.Message, Red );
				Log.Write( "mic start ERROR: " + ex );
			}
		}

		void OnPttUp()
		{
			Log.Write( $"PTT up (recording={recording})" );
			if( !recording )
				return;
			recording = false;
			busy = true;
			overlay?.ShowProcessing();
			SetStatus( "Розпізнавання…", Orange );

			eLanguage lang = ResolveLanguage();
			bool appendSpace = ChkSpace.IsChecked == true;
			InsertMode mode = CmbInsert.SelectedIndex == 1 ? InsertMode.Clipboard : InsertMode.SendInput;

			Task.Run( () =>
			{
				string text = "";
				string? err = null;
				try
				{
					float[] pcm = recorder.Stop();
					Log.Write( $"captured {pcm.Length} samples ({pcm.Length / 16000.0:0.00}s), lang={lang}" );
					text = recognizer.Transcribe( pcm, lang );
					Log.Write( $"recognized: \"{text}\"" );
				}
				catch( Exception ex ) { err = ex.Message; Log.Write( "recognize ERROR: " + ex ); }

				Dispatcher.Invoke( () => OnRecognized( text, err, appendSpace, mode ) );
			} );
		}

		void OnRecognized( string text, string? err, bool appendSpace, InsertMode mode )
		{
			busy = false;
			overlay?.HideOverlay();
			if( err != null )
			{
				SetStatus( "Помилка: " + err, Red );
				return;
			}
			if( string.IsNullOrWhiteSpace( text ) )
			{
				ReadyStatus();
				return;
			}

			string toType = appendSpace ? text + " " : text;
			Log.Write( $"inject mode={mode} len={toType.Length}" );
			TextInjector.Insert( toType, mode );
			AppendLog( text );
			ReadyStatus();
		}

		// ---------- log card ----------

		void BtnClear_Click( object sender, RoutedEventArgs e ) => TxtLog.Clear();

		void BtnCollapse_Click( object sender, RoutedEventArgs e )
		{
			bool show = TxtLog.Visibility != Visibility.Visible;
			TxtLog.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
			BtnCollapse.Content = ( (char)( show ? 0xE70E : 0xE70D ) ).ToString();
		}

		void AppendLog( string line )
		{
			TxtLog.AppendText( line + Environment.NewLine );
			TxtLog.ScrollToEnd();
		}

		// ---------- helpers ----------

		string CurrentLanguageCode() =>
			CmbLang.SelectedIndex >= 0 ? LangChoices[ CmbLang.SelectedIndex ].code : "uk";

		eLanguage ResolveLanguage() =>
			Library.languageFromCode( CurrentLanguageCode() ) ?? eLanguage.Ukrainian;

		void SetStatus( string text, Brush brush )
		{
			if( !Dispatcher.CheckAccess() )
			{
				Dispatcher.Invoke( () => SetStatus( text, brush ) );
				return;
			}
			TxtStatus.Text = text;
			TxtStatus.Foreground = brush;
			StatusDotBig.Fill = brush;
			StatusDotSmall.Fill = brush;
			TxtBottom.Text = text.Length > 40 ? "Готово" : text;
			StatusIcon.Visibility = ReferenceEquals( brush, Green ) ? Visibility.Visible : Visibility.Collapsed;
		}

		void SaveSettings()
		{
			settings.ModelPath = TxtModel.Text;
			settings.DeviceId = SelectedDevice()?.ID;
			settings.GpuAdapter = SelectedAdapter();
			settings.LanguageMode = CurrentLanguageCode();
			settings.Insert = CmbInsert.SelectedIndex == 1 ? "Clipboard" : "SendInput";
			settings.HotkeyVk = CmbHotkey.SelectedIndex >= 0 ? HotkeyChoices[ CmbHotkey.SelectedIndex ].vks[ 0 ] : 0x14;
			settings.AppendSpace = ChkSpace.IsChecked == true;
			settings.SwallowHotkey = ChkSwallow.IsChecked == true;
			settings.Save();
		}
	}
}
