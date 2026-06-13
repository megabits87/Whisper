using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoiceTyper
{
	/// <summary>
	/// Global low-level keyboard hook used for push-to-talk. Reports key-down and key-up of a single
	/// configured virtual key, and (optionally) swallows those events so they don't reach other apps.
	/// </summary>
	sealed class KeyboardHook : IDisposable
	{
		const int WH_KEYBOARD_LL = 13;
		const int WM_KEYDOWN = 0x0100;
		const int WM_KEYUP = 0x0101;
		const int WM_SYSKEYDOWN = 0x0104;
		const int WM_SYSKEYUP = 0x0105;

		readonly LowLevelKeyboardProc proc;
		IntPtr hook = IntPtr.Zero;
		bool keyHeld;

		/// <summary>Virtual-key code(s) that act as the push-to-talk key. Any of them triggers it.</summary>
		public int[] Keys { get; set; } = Array.Empty<int>();

		/// <summary>When true, the push-to-talk key is consumed and not delivered to the focused app.</summary>
		public bool Swallow { get; set; } = true;

		/// <summary>Raised (on the UI thread, via the hook callback) when the PTT key goes down.</summary>
		public event Action? KeyDown;
		/// <summary>Raised when the PTT key is released.</summary>
		public event Action? KeyUp;

		/// <summary>True while the PTT key is currently considered held down.</summary>
		public bool IsHeld => keyHeld;

		/// <summary>Force the key to be treated as released, raising <see cref="KeyUp"/> once if it was held.
		/// Lets a watchdog recover from a missed WM_KEYUP (focus/session switch, secure desktop, etc.).</summary>
		public void ForceRelease()
		{
			if( keyHeld )
			{
				keyHeld = false;
				try { KeyUp?.Invoke(); } catch { }
			}
		}

		public KeyboardHook( int[] keys )
		{
			Keys = keys;
			proc = HookCallback;
		}

		public void Install()
		{
			if( hook != IntPtr.Zero )
				return;
			using Process curProcess = Process.GetCurrentProcess();
			using ProcessModule curModule = curProcess.MainModule!;
			hook = SetWindowsHookEx( WH_KEYBOARD_LL, proc, GetModuleHandle( curModule.ModuleName ), 0 );
			if( hook == IntPtr.Zero )
			{
				int err = Marshal.GetLastWin32Error();
				Log.Write( $"HOOK install FAILED, err={err}" );
				throw new InvalidOperationException( "Failed to install keyboard hook: " + err );
			}
			Log.Write( "HOOK installed ok, keys=" + string.Join( ",", Array.ConvertAll( Keys, k => "0x" + k.ToString( "X2" ) ) ) );
		}

		IntPtr HookCallback( int nCode, IntPtr wParam, IntPtr lParam )
		{
			if( nCode >= 0 )
			{
				int msg = (int)wParam;
				int vk = Marshal.ReadInt32( lParam ); // KBDLLHOOKSTRUCT.vkCode is the first field
				if( Array.IndexOf( Keys, vk ) >= 0 )
				{
					// Privacy: this is a system-wide hook, so we only ever log the configured
					// push-to-talk key — never the codes of unrelated keystrokes.
					if( Log.Verbose && ( msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN || msg == WM_KEYUP || msg == WM_SYSKEYUP ) )
						Log.Write( $"PTT key msg=0x{msg:X3} vk=0x{vk:X2}" );
					if( msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN )
					{
						if( !keyHeld )
						{
							keyHeld = true;
							try { KeyDown?.Invoke(); } catch { }
						}
						if( Swallow )
							return (IntPtr)1;
					}
					else if( msg == WM_KEYUP || msg == WM_SYSKEYUP )
					{
						if( keyHeld )
						{
							keyHeld = false;
							try { KeyUp?.Invoke(); } catch { }
						}
						if( Swallow )
							return (IntPtr)1;
					}
				}
			}
			return CallNextHookEx( hook, nCode, wParam, lParam );
		}

		public void Dispose()
		{
			if( hook != IntPtr.Zero )
			{
				UnhookWindowsHookEx( hook );
				hook = IntPtr.Zero;
			}
		}

		delegate IntPtr LowLevelKeyboardProc( int nCode, IntPtr wParam, IntPtr lParam );

		[DllImport( "user32.dll", CharSet = CharSet.Auto, SetLastError = true )]
		static extern IntPtr SetWindowsHookEx( int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId );

		[DllImport( "user32.dll", CharSet = CharSet.Auto, SetLastError = true )]
		[return: MarshalAs( UnmanagedType.Bool )]
		static extern bool UnhookWindowsHookEx( IntPtr hhk );

		[DllImport( "user32.dll", CharSet = CharSet.Auto, SetLastError = true )]
		static extern IntPtr CallNextHookEx( IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam );

		[DllImport( "kernel32.dll", CharSet = CharSet.Auto, SetLastError = true )]
		static extern IntPtr GetModuleHandle( string lpModuleName );
	}
}
