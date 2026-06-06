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
				inputs.Add( CharInput( c, keyUp: false ) );
				inputs.Add( CharInput( c, keyUp: true ) );
			}
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
			string? previous = null;
			try
			{
				if( Clipboard.ContainsText() )
					previous = Clipboard.GetText();
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
			if( previous != null )
			{
				var prev = previous;
				var t = new System.Windows.Forms.Timer { Interval = 400 };
				t.Tick += ( s, e ) =>
				{
					t.Stop();
					t.Dispose();
					try { Clipboard.SetText( prev ); } catch { }
				};
				t.Start();
			}
		}

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

		// ---- Win32 ----

		const int INPUT_KEYBOARD = 1;
		const uint KEYEVENTF_KEYUP = 0x0002;
		const uint KEYEVENTF_UNICODE = 0x0004;

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

		[DllImport( "user32.dll", SetLastError = true )]
		static extern uint SendInput( uint nInputs, INPUT[] pInputs, int cbSize );
	}
}
