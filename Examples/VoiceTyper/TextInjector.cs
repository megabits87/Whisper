using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VoiceTyper
{
	enum InsertMode
	{
		SendInput,
		Clipboard,
	}

	/// <summary>Injects recognized text into whichever window currently has keyboard focus.</summary>
	static class TextInjector
	{
		public static void Insert( string text, InsertMode mode )
		{
			if( string.IsNullOrEmpty( text ) )
				return;
			if( mode == InsertMode.Clipboard )
				InsertViaClipboard( text );
			else
				InsertViaSendInput( text );
		}

		// ---- SendInput (synthesized Unicode keystrokes) ----

		static void InsertViaSendInput( string text )
		{
			var inputs = new List<INPUT>( text.Length * 2 );
			foreach( char c in text )
			{
				switch( c )
				{
					case '\r':
						continue; // CR is delivered together with the following LF
					case '\n':
						inputs.Add( KeyInput( VK_RETURN, false ) );
						inputs.Add( KeyInput( VK_RETURN, true ) );
						continue;
					case '\t':
						inputs.Add( KeyInput( VK_TAB, false ) );
						inputs.Add( KeyInput( VK_TAB, true ) );
						continue;
					default:
						inputs.Add( CharInput( c, keyUp: false ) );
						inputs.Add( CharInput( c, keyUp: true ) );
						continue;
				}
			}
			if( inputs.Count == 0 )
				return;
			INPUT[] arr = inputs.ToArray();
			int size = Marshal.SizeOf<INPUT>();
			uint sent = SendInput( (uint)arr.Length, arr, size );
			if( sent != arr.Length )
				Log.Write( $"SendInput sent {sent}/{arr.Length}, cbSize={size}, err={Marshal.GetLastWin32Error()}" );
		}

		static INPUT CharInput( char c, bool keyUp )
		{
			uint flags = KEYEVENTF_UNICODE | ( keyUp ? KEYEVENTF_KEYUP : 0u );
			return new INPUT
			{
				type = INPUT_KEYBOARD,
				U = new InputUnion
				{
					ki = new KEYBDINPUT
					{
						wVk = 0,
						wScan = c,
						dwFlags = flags,
						time = 0,
						dwExtraInfo = IntPtr.Zero,
					}
				}
			};
		}

		// ---- Clipboard paste (Ctrl+V) ----

		static void InsertViaClipboard( string text )
		{
			// Snapshot ALL existing clipboard formats (text, images, files, ...) so we can restore them,
			// not just text. Copy the data out now because the live IDataObject may not survive the overwrite.
			DataObject? saved = null;
			try
			{
				IDataObject? cur = Clipboard.GetDataObject();
				if( cur != null )
				{
					saved = new DataObject();
					foreach( string fmt in cur.GetFormats() )
					{
						try
						{
							object? data = cur.GetData( fmt );
							if( data != null )
								saved.SetData( fmt, data );
						}
						catch { }
					}
				}
			}
			catch { }

			try
			{
				Clipboard.SetText( text );
			}
			catch
			{
				// Clipboard can be momentarily locked by another process; fall back to typing.
				InsertViaSendInput( text );
				return;
			}

			SendCtrlV();

			// Give the target app a moment to read the clipboard before restoring it.
			if( saved != null )
			{
				var restore = saved;
				var t = new System.Windows.Forms.Timer { Interval = 400 };
				t.Tick += ( s, e ) =>
				{
					t.Stop();
					t.Dispose();
					try { Clipboard.SetDataObject( restore, copy: true ); } catch { }
				};
				t.Start();
			}
		}

		const ushort VK_RETURN = 0x0D;
		const ushort VK_TAB = 0x09;
		const ushort VK_CONTROL = 0x11;
		const ushort VK_V = 0x56;

		static void SendCtrlV()
		{
			INPUT[] arr =
			{
				KeyInput( VK_CONTROL, false ),
				KeyInput( VK_V, false ),
				KeyInput( VK_V, true ),
				KeyInput( VK_CONTROL, true ),
			};
			SendInput( (uint)arr.Length, arr, Marshal.SizeOf<INPUT>() );
		}

		static INPUT KeyInput( ushort vk, bool keyUp )
		{
			return new INPUT
			{
				type = INPUT_KEYBOARD,
				U = new InputUnion
				{
					ki = new KEYBDINPUT
					{
						wVk = vk,
						wScan = 0,
						dwFlags = keyUp ? KEYEVENTF_KEYUP : 0u,
						time = 0,
						dwExtraInfo = IntPtr.Zero,
					}
				}
			};
		}

		// ---- Elevation awareness ----

		/// <summary>
		/// Best-effort check: true when the focused window belongs to a higher-integrity (elevated) process
		/// while we are not elevated — in which case SendInput / paste is silently blocked by Windows UIPI.
		/// </summary>
		public static bool ForegroundTargetLikelyUnreachable()
		{
			try
			{
				if( IsProcessElevated() )
					return false; // we can inject anywhere

				IntPtr hwnd = GetForegroundWindow();
				if( hwnd == IntPtr.Zero )
					return false;
				GetWindowThreadProcessId( hwnd, out uint pid );
				if( pid == 0 )
					return false;

				IntPtr h = OpenProcess( PROCESS_QUERY_LIMITED_INFORMATION, false, pid );
				if( h == IntPtr.Zero )
					return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED; // can't even open => higher integrity

				try
				{
					if( OpenProcessToken( h, TOKEN_QUERY, out IntPtr tok ) )
					{
						try
						{
							if( GetTokenInformation( tok, TokenElevation, out TOKEN_ELEVATION te,
									Marshal.SizeOf<TOKEN_ELEVATION>(), out _ ) )
								return te.TokenIsElevated != 0;
						}
						finally { CloseHandle( tok ); }
					}
				}
				finally { CloseHandle( h ); }
			}
			catch { }
			return false;
		}

		static bool IsProcessElevated()
		{
			try
			{
				using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
				var p = new System.Security.Principal.WindowsPrincipal( id );
				return p.IsInRole( System.Security.Principal.WindowsBuiltInRole.Administrator );
			}
			catch { return false; }
		}

		// ---- Win32 ----

		const int INPUT_KEYBOARD = 1;
		const uint KEYEVENTF_KEYUP = 0x0002;
		const uint KEYEVENTF_UNICODE = 0x0004;

		const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
		const uint TOKEN_QUERY = 0x0008;
		const int ERROR_ACCESS_DENIED = 5;
		const int TokenElevation = 20;

		[StructLayout( LayoutKind.Sequential )]
		struct INPUT
		{
			public int type;
			public InputUnion U;
		}

		// The native INPUT union must be sized to its largest member (MOUSEINPUT), otherwise
		// Marshal.SizeOf<INPUT>() is too small, SendInput receives a wrong cbSize and does nothing.
		[StructLayout( LayoutKind.Explicit )]
		struct InputUnion
		{
			[FieldOffset( 0 )] public MOUSEINPUT mi;
			[FieldOffset( 0 )] public KEYBDINPUT ki;
			[FieldOffset( 0 )] public HARDWAREINPUT hi;
		}

		[StructLayout( LayoutKind.Sequential )]
		struct MOUSEINPUT
		{
			public int dx;
			public int dy;
			public uint mouseData;
			public uint dwFlags;
			public uint time;
			public IntPtr dwExtraInfo;
		}

		[StructLayout( LayoutKind.Sequential )]
		struct KEYBDINPUT
		{
			public ushort wVk;
			public ushort wScan;
			public uint dwFlags;
			public uint time;
			public IntPtr dwExtraInfo;
		}

		[StructLayout( LayoutKind.Sequential )]
		struct HARDWAREINPUT
		{
			public uint uMsg;
			public ushort wParamL;
			public ushort wParamH;
		}

		[StructLayout( LayoutKind.Sequential )]
		struct TOKEN_ELEVATION
		{
			public int TokenIsElevated;
		}

		[DllImport( "user32.dll", SetLastError = true )]
		static extern uint SendInput( uint nInputs, INPUT[] pInputs, int cbSize );

		[DllImport( "user32.dll" )]
		static extern IntPtr GetForegroundWindow();

		[DllImport( "user32.dll", SetLastError = true )]
		static extern uint GetWindowThreadProcessId( IntPtr hWnd, out uint lpdwProcessId );

		[DllImport( "kernel32.dll", SetLastError = true )]
		static extern IntPtr OpenProcess( uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId );

		[DllImport( "advapi32.dll", SetLastError = true )]
		static extern bool OpenProcessToken( IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle );

		[DllImport( "advapi32.dll", SetLastError = true )]
		static extern bool GetTokenInformation( IntPtr TokenHandle, int TokenInformationClass,
			out TOKEN_ELEVATION TokenInformation, int TokenInformationLength, out int ReturnLength );

		[DllImport( "kernel32.dll", SetLastError = true )]
		static extern bool CloseHandle( IntPtr hObject );
	}
}
