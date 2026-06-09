using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NAudio.CoreAudioApi;
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

		volatile bool busy;
		volatile bool recording;
		bool initializing;

		System.Windows.Threading.DispatcherTimer? pttWatchdog;
		DateTime recordStart;
		const double MaxRecordSeconds = 120.0;

		static readonly (string name, string id, int[] vks)[] HotkeyChoices =
		{
			("Caps Lock (рекомендовано)", "caps", new[] { 0x14 }),
			("Правий Ctrl", "rctrl", new[] { 0xA3 }),
			("Лівий Ctrl", "lctrl", new[] { 0xA2 }),
			("Ctrl (будь-який)", "ctrl", new[] { 0xA2, 0xA3 }),
			("Правий Alt", "ralt", new[] { 0xA5 }),
			("Правий Shift", "rshift", new[] { 0xA1 }),
			("Scroll Lock", "scroll", new[] { 0x91 }),
			("Pause / Break", "pause", new[] { 0x13 }),
			("F8", "f8", new[] { 0x77 }),
			("F9", "f9", new[] { 0x78 }),
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

			TxtModel.Text = settings.ModelPath;
			CmbLang.SelectedIndex = Math.Max( 0, Array.FindIndex( LangChoices, l => l.code == settings.LanguageMode ) );
			CmbInsert.SelectedIndex = settings.Insert == "Clipboard" ? 1 : 0;
			CmbHotkey.SelectedIndex = ResolveHotkeyIndex();
			ChkSpace.IsChecked = settings.AppendSpace;
			ChkSwallow.IsChecked = settings.SwallowHotkey;
			ChkAutostart.IsChecked = settings.AutoStart;

			initializing = false;

			// Persist as soon as the user changes any of these (crash-safe), not only on close.
			CmbLang.SelectionChanged += ( s, ev ) => PersistIfReady();
			CmbInsert.SelectionChanged += ( s, ev ) => PersistIfReady();
			CmbDevice.SelectionChanged += ( s, ev ) => PersistIfReady();
			ChkSpace.Checked += ( s, ev ) => PersistIfReady();
			ChkSpace.Unchecked += ( s, ev ) => PersistIfReady();

			overlay = new ListeningOverlay( () => recorder.CurrentLevel );

			ApplyHotkey();
			SetStatus( "Завантажте модель…", Green );

			if( AppSettings.LastLoadFailed )
				AppendLog( "Не вдалося прочитати налаштування — відновлено типові значення." );

			recognizer.ServerExe = string.IsNullOrWhiteSpace( settings.WhisperServerExe )
				? WhisperServer.DefaultExe : settings.WhisperServerExe;
			recognizer.BeamSize = settings.BeamSize;

			// Keep the run-at-login registry entry in sync with the saved preference (exe path may change).
			Autostart.Apply( settings.AutoStart );

			string gpuName = DetectGpuName();
			TxtGpuName.Text = gpuName;
			TxtGpu.Text = "GPU: " + ShortGpu( gpuName );

			if( !string.IsNullOrWhiteSpace( settings.ModelPath ) && File.Exists( settings.ModelPath ) )
				StartLoadModel( settings.ModelPath );
			else
				PromptDownloadModel();
		}

		// First run (or model missing): use the already-downloaded default, else offer to download it.
		void PromptDownloadModel()
		{
			if( File.Exists( ModelDownloader.DefaultModelPath ) )
			{
				TxtModel.Text = ModelDownloader.DefaultModelPath;
				StartLoadModel( ModelDownloader.DefaultModelPath );
				return;
			}
			var r = System.Windows.MessageBox.Show(
				"Модель мовлення не знайдено.\n\nЗавантажити рекомендовану модель large-v3-turbo (~1.5 ГБ)?\n" +
				"Це одноразово — далі застосунок працює офлайн.",
				"VoxType — завантаження моделі", MessageBoxButton.YesNo, MessageBoxImage.Question );
			if( r != MessageBoxResult.Yes )
			{
				SetStatus( "Виберіть модель…", Green );
				return;
			}

			BtnReload.IsEnabled = BtnBrowse.IsEnabled = false;
			SetStatus( "Завантаження моделі 0%…", Orange );
			Task.Run( () =>
			{
				string? err = null;
				try
				{
					ModelDownloader.Download( ModelDownloader.DefaultModel, p =>
						Dispatcher.Invoke( () => SetStatus( $"Завантаження моделі {p * 100:0}%…", Orange ) ) );
				}
				catch( Exception ex ) { err = ex.Message; Log.Write( "model download ERROR: " + ex ); }

				Dispatcher.Invoke( () =>
				{
					BtnReload.IsEnabled = BtnBrowse.IsEnabled = true;
					if( err != null ) { SetStatus( "Помилка завантаження моделі: " + err, Red ); return; }
					TxtModel.Text = ModelDownloader.DefaultModelPath;
					StartLoadModel( ModelDownloader.DefaultModelPath );
				} );
			} );
		}

		int ResolveHotkeyIndex()
		{
			if( !string.IsNullOrEmpty( settings.HotkeyId ) )
			{
				int byId = Array.FindIndex( HotkeyChoices, h => h.id == settings.HotkeyId );
				if( byId >= 0 ) return byId;
			}
			// Legacy settings (no HotkeyId): fall back to matching the stored virtual-key code.
			return Math.Max( 0, Array.FindIndex( HotkeyChoices, h => h.vks[ 0 ] == settings.HotkeyVk ) );
		}

		void PersistIfReady()
		{
			if( !initializing )
				SaveSettings();
		}

		void OnClosing( object? sender, System.ComponentModel.CancelEventArgs e )
		{
			SaveSettings();
			pttWatchdog?.Stop();
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
				Text = "VoxType",
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
			SetStatus( "Завантаження моделі…", Orange );
			BtnReload.IsEnabled = BtnBrowse.IsEnabled = false;
			Log.Write( $"loading model '{path}'" );

			Task.Run( () =>
			{
				try
				{
					recognizer.Load( path, null );
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
			LblModelInfo.Text = $"Завантажено · {ml} · {Path.GetFileName( recognizer.ModelPath )}";
			LblModelInfo.Foreground = recognizer.IsMultilingual ? Dim : Red;

			if( !recognizer.IsMultilingual && CurrentLanguageCode() != "en" )
				AppendLog( "УВАГА: модель розпізнається як англомовна. Для української візьміть multilingual-модель до v3 (medium / large-v2)." );

			ReadyStatus();
		}

		// The GPU whisper.cpp will use (CUDA device 0 = the discrete adapter). Shown read-only.
		// Queried via WMI (Win32_VideoController) so it works on any machine/vendor with no native dependency.
		static string DetectGpuName()
		{
			try
			{
				var names = new List<string>();
				using var searcher = new System.Management.ManagementObjectSearcher( "SELECT Name FROM Win32_VideoController" );
				foreach( System.Management.ManagementObject mo in searcher.Get() )
				{
					string? n = mo[ "Name" ]?.ToString();
					if( !string.IsNullOrWhiteSpace( n ) ) names.Add( n! );
				}
				foreach( string a in names )
				{
					string n = a.ToLowerInvariant();
					if( n.Contains( "nvidia" ) || n.Contains( "geforce" ) || n.Contains( "rtx" ) ||
						n.Contains( "radeon" ) || ( n.Contains( "amd" ) && !n.Contains( "intel" ) ) )
						return a;
				}
				return names.Count > 0 ? names[ 0 ] : "GPU";
			}
			catch { return "GPU"; }
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
				// The hook callback runs on the UI thread; do the heavy mic work via BeginInvoke so the
				// callback returns immediately and Windows can't drop the hook on LowLevelHooksTimeout.
				hook.KeyDown += () => Dispatcher.BeginInvoke( new Action( OnPttDown ) );
				hook.KeyUp += () => Dispatcher.BeginInvoke( new Action( OnPttUp ) );
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
			PersistIfReady();
		}

		void ChkSwallow_Changed( object sender, RoutedEventArgs e )
		{
			if( hook != null ) hook.Swallow = ChkSwallow.IsChecked == true;
			PersistIfReady();
		}

		void ChkAutostart_Changed( object sender, RoutedEventArgs e )
		{
			if( initializing ) return;
			bool on = ChkAutostart.IsChecked == true;
			Autostart.Apply( on );
			PersistIfReady();
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
				recordStart = DateTime.UtcNow;
				StartWatchdog();
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

		// Recover from a missed key-up (focus/session switch, secure desktop, app lost the hook) and
		// from a key that is held too long, so push-to-talk can never get stuck "recording" forever.
		void StartWatchdog()
		{
			if( pttWatchdog == null )
			{
				pttWatchdog = new System.Windows.Threading.DispatcherTimer
				{ Interval = TimeSpan.FromMilliseconds( 250 ) };
				pttWatchdog.Tick += ( s, e ) =>
				{
					if( !recording ) { pttWatchdog!.Stop(); return; }
					// NOTE: do NOT poll GetAsyncKeyState here — when the hotkey is swallowed, Windows does
					// not update the async key state, so it would falsely report "released" while held and
					// make recording flicker on/off. Rely on the hook's own key-up and a hard time cap.
					bool tooLong = ( DateTime.UtcNow - recordStart ).TotalSeconds > MaxRecordSeconds;
					bool keyReleased = hook != null && !hook.IsHeld;
					if( tooLong || keyReleased )
					{
						pttWatchdog!.Stop();
						Log.Write( $"watchdog releasing PTT (tooLong={tooLong} keyReleased={keyReleased})" );
						hook?.ForceRelease();
						OnPttUp();
					}
				};
			}
			pttWatchdog.Start();
		}

		void OnPttUp()
		{
			Log.Write( $"PTT up (recording={recording})" );
			pttWatchdog?.Stop();
			if( !recording )
				return;
			recording = false;
			busy = true;
			overlay?.ShowProcessing();
			SetStatus( "Розпізнавання…", Orange );

			string lang = CurrentLanguageCode();
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
				string? reason = recognizer.LastSkipReason;
				if( !string.IsNullOrEmpty( reason ) )
					AppendLog( "— (" + reason + ")" );
				ReadyStatus();
				return;
			}

			if( TextInjector.ForegroundTargetLikelyUnreachable() )
				AppendLog( "УВАГА: активне вікно запущене від адміністратора — запустіть VoiceTyper також від імені адміністратора, інакше текст не вставиться." );

			string toType = appendSpace ? text + " " : text;
			Log.Write( $"inject mode={mode} len={toType.Length}" );
			TextInjector.Insert( toType, mode );
			AppendLog( text );
			ReadyStatus();
		}

		// ---------- log card ----------

		void BtnClear_Click( object sender, RoutedEventArgs e ) => TxtLog.Clear();

		// ---------- transcribe a file (Input -> Output) ----------

		static readonly Brush Violet = new SolidColorBrush( Color.FromRgb( 0xD9, 0xD4, 0xFF ) );
		string outputFolder = "";

		void BtnInputBrowse_Click( object sender, RoutedEventArgs e )
		{
			var ofd = new Microsoft.Win32.OpenFileDialog
			{
				Title = "Input — виберіть аудіо/відео файл",
				Filter = "Аудіо/відео (*.wav;*.mp3;*.flac;*.ogg;*.m4a;*.aac;*.wma;*.opus;*.mp4;*.mkv;*.mov;*.avi;*.webm)" +
					"|*.wav;*.mp3;*.flac;*.ogg;*.m4a;*.aac;*.wma;*.opus;*.mp4;*.mkv;*.mov;*.avi;*.webm|Усі файли (*.*)|*.*",
			};
			if( ofd.ShowDialog( this ) == true )
				StartFileTranscribe( ofd.FileName );
		}

		void BtnOutputBrowse_Click( object sender, RoutedEventArgs e )
		{
			var fd = new Microsoft.Win32.OpenFolderDialog { Title = "Output — папка для збереження тексту" };
			try { if( Directory.Exists( outputFolder ) ) fd.InitialDirectory = outputFolder; } catch { }
			if( fd.ShowDialog( this ) == true )
			{
				outputFolder = fd.FolderName;
				TxtOutput.Text = outputFolder;
				TxtOutput.Foreground = Violet;
			}
		}

		void Input_DragOver( object sender, System.Windows.DragEventArgs e )
		{
			e.Effects = e.Data.GetDataPresent( System.Windows.DataFormats.FileDrop )
				? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
			e.Handled = true;
		}

		void Input_Drop( object sender, System.Windows.DragEventArgs e )
		{
			if( !e.Data.GetDataPresent( System.Windows.DataFormats.FileDrop ) )
				return;
			var files = (string[])e.Data.GetData( System.Windows.DataFormats.FileDrop );
			if( files != null && files.Length > 0 )
				StartFileTranscribe( files[ 0 ] );
		}

		// Transcribe the chosen Input file and save the .txt into the Output folder (or next to the input).
		void StartFileTranscribe( string input )
		{
			if( !recognizer.IsLoaded ) { SetStatus( "Спершу завантажте модель", Orange ); return; }
			if( busy || recording ) return;
			if( !File.Exists( input ) ) { SetStatus( "Файл не знайдено", Red ); return; }

			TxtInput.Text = input;
			TxtInput.Foreground = Violet;

			string dir = Directory.Exists( outputFolder ) ? outputFolder : ( Path.GetDirectoryName( input ) ?? "" );
			string output = Path.Combine( dir, Path.GetFileNameWithoutExtension( input ) + ".txt" );
			string lang = CurrentLanguageCode();

			busy = true;
			BtnInputBrowse.IsEnabled = false;
			SetStatus( "Транскрипція файлу…", Orange );
			AppendLog( $"▶ {Path.GetFileName( input )}  →  {Path.GetFileName( output )}" );

			Task.Run( () =>
			{
				string text = "";
				string? err = null;
				try { text = recognizer.TranscribeFile( input, lang ); }
				catch( Exception ex ) { err = ex.Message; Log.Write( "file transcribe ERROR: " + ex ); }
				Dispatcher.Invoke( () => OnFileTranscribed( output, text, err ) );
			} );
		}

		void OnFileTranscribed( string outputPath, string text, string? err )
		{
			busy = false;
			BtnInputBrowse.IsEnabled = true;
			if( err != null )
			{
				SetStatus( "Помилка файлу: " + err, Red );
				return;
			}
			if( string.IsNullOrWhiteSpace( text ) )
			{
				AppendLog( "— (порожньо)" );
				ReadyStatus();
				return;
			}
			try
			{
				File.WriteAllText( outputPath, text );
			}
			catch( Exception ex )
			{
				Log.Write( "save txt failed: " + ex.Message );
				SetStatus( "Не вдалося зберегти: " + ex.Message, Red );
				return;
			}
			try { System.Windows.Clipboard.SetText( text ); } catch { }
			AppendLog( text );
			SetStatus( "Збережено: " + Path.GetFileName( outputPath ), Green );
		}

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
			settings.LanguageMode = CurrentLanguageCode();
			settings.Insert = CmbInsert.SelectedIndex == 1 ? "Clipboard" : "SendInput";
			int hi = CmbHotkey.SelectedIndex >= 0 ? CmbHotkey.SelectedIndex : 0;
			settings.HotkeyId = HotkeyChoices[ hi ].id;
			settings.HotkeyVk = HotkeyChoices[ hi ].vks[ 0 ];
			settings.AppendSpace = ChkSpace.IsChecked == true;
			settings.SwallowHotkey = ChkSwallow.IsChecked == true;
			settings.AutoStart = ChkAutostart.IsChecked == true;
			settings.Save();
		}
	}
}
