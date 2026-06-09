using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VoiceTyper
{
	/// <summary>
	/// Topmost, click-through, semi-transparent overlay shown at the bottom-centre of the active
	/// screen while listening. Renders a live equaliser that reacts to the microphone level.
	/// Uses a layered window (UpdateLayeredWindow) for per-pixel alpha and anti-aliased corners.
	/// </summary>
	sealed class ListeningOverlay : Form
	{
		enum Mode { Hidden, Listening, Processing }

		const int W = 280, H = 70, Bars = 28;
		const byte WindowAlpha = 205;   // overall translucency of the pill

		readonly System.Windows.Forms.Timer timer;
		readonly Func<float> getLevel;
		readonly float[] history = new float[ Bars ];
		Mode mode = Mode.Hidden;
		int phase;
		float scale = 1f;        // DPI scale of the monitor the overlay is shown on
		int pxW = W, pxH = H;    // physical-pixel size = logical size * scale

		public ListeningOverlay( Func<float> levelProvider )
		{
			getLevel = levelProvider;
			FormBorderStyle = FormBorderStyle.None;
			ShowInTaskbar = false;
			StartPosition = FormStartPosition.Manual;
			TopMost = true;
			Size = new Size( W, H );
			timer = new System.Windows.Forms.Timer { Interval = 33 }; // ~30 fps
			timer.Tick += ( s, e ) => Frame();
		}

		protected override CreateParams CreateParams
		{
			get
			{
				const int WS_EX_LAYERED = 0x00080000;
				const int WS_EX_TRANSPARENT = 0x00000020;
				const int WS_EX_NOACTIVATE = 0x08000000;
				const int WS_EX_TOOLWINDOW = 0x00000080;
				CreateParams cp = base.CreateParams;
				cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
				return cp;
			}
		}

		protected override bool ShowWithoutActivation => true;

		// ---- public state transitions (call on UI thread) ----

		public void ShowListening()
		{
			Array.Clear( history, 0, history.Length );
			mode = Mode.Listening;
			Reposition();
			if( !Visible )
				Show();
			timer.Start();
			Frame();
		}

		public void ShowProcessing() => mode = Mode.Processing;

		public void HideOverlay()
		{
			mode = Mode.Hidden;
			timer.Stop();
			if( Visible )
				Hide();
		}

		void Reposition()
		{
			scale = GetDpiScale();
			pxW = (int)Math.Round( W * scale );
			pxH = (int)Math.Round( H * scale );
			Size = new Size( pxW, pxH );
			Rectangle wa = Screen.FromPoint( Cursor.Position ).WorkingArea;
			Location = new Point( wa.Left + ( wa.Width - pxW ) / 2, wa.Bottom - pxH - (int)Math.Round( 28 * scale ) );
		}

		float GetDpiScale()
		{
			try
			{
				var cp = Cursor.Position;
				IntPtr mon = MonitorFromPoint( new POINT { x = cp.X, y = cp.Y }, MONITOR_DEFAULTTONEAREST );
				if( GetDpiForMonitor( mon, MDT_EFFECTIVE_DPI, out uint dx, out uint _ ) == 0 && dx > 0 )
					return dx / 96f;
			}
			catch { }
			return 1f;
		}

		// ---- rendering ----

		void Frame()
		{
			float level;
			if( mode == Mode.Listening )
				level = Math.Clamp( getLevel(), 0f, 1f );
			else
			{
				phase++;
				level = 0.30f + 0.18f * (float)Math.Sin( phase * 0.35 ); // gentle pulse while recognising
			}

			for( int i = 0; i < Bars - 1; i++ )
				history[ i ] = history[ i + 1 ];
			history[ Bars - 1 ] = level;

			using Bitmap bmp = Render();
			SetBitmap( bmp, WindowAlpha );
		}

		Bitmap Render()
		{
			Bitmap bmp = new Bitmap( pxW, pxH, PixelFormat.Format32bppArgb );
			using Graphics g = Graphics.FromImage( bmp );
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.Clear( Color.Transparent );
			// Draw everything below in logical (96-DPI) coordinates; the transform scales it crisply.
			g.ScaleTransform( scale, scale );

			// Pill background (opaque colour; whole window is dimmed by WindowAlpha).
			using( GraphicsPath path = RoundRect( new Rectangle( 0, 0, W - 1, H - 1 ), 20 ) )
			{
				using var bg = new SolidBrush( Color.FromArgb( 255, 24, 26, 32 ) );
				g.FillPath( bg, path );
				using var border = new Pen( Color.FromArgb( 255, 60, 64, 76 ), 1f );
				g.DrawPath( border, path );
			}

			DrawMic( g, 22, H / 2 );

			// Equaliser bars (newest on the right) — a live scrolling waveform.
			int areaX = 56, areaR = W - 18;
			int areaW = areaR - areaX;
			float bw = (float)areaW / Bars;
			float maxH = H - 26;
			float mid = H / 2f;
			for( int i = 0; i < Bars; i++ )
			{
				float v = history[ i ];
				float bh = Math.Max( 3f, v * maxH );
				float bx = areaX + i * bw;
				float bwidth = bw * 0.55f;
				var rect = new RectangleF( bx, mid - bh / 2, bwidth, bh );
				Color c = BarColor( v );
				using var br = new SolidBrush( c );
				using GraphicsPath bp = RoundRect( Rectangle.Round( rect ), (int)Math.Min( bwidth / 2, 3 ) );
				g.FillPath( br, bp );
			}

			return bmp;
		}

		static Color BarColor( float v )
		{
			// Teal at low level, brightening toward cyan/white as it gets louder.
			int r = (int)( 90 + 120 * v );
			int gg = (int)( 200 + 55 * v );
			int b = 245;
			return Color.FromArgb( 255, Math.Clamp( r, 0, 255 ), Math.Clamp( gg, 0, 255 ), b );
		}

		static void DrawMic( Graphics g, int cx, int cy )
		{
			using var white = new SolidBrush( Color.FromArgb( 255, 235, 238, 245 ) );
			using var pen = new Pen( Color.FromArgb( 255, 235, 238, 245 ), 2f );
			// capsule head
			var head = new RectangleF( cx - 6, cy - 16, 12, 20 );
			g.FillPath( white, RoundRect( Rectangle.Round( head ), 6 ) );
			// cradle arc
			g.DrawArc( pen, cx - 11, cy - 12, 22, 24, 20, 140 );
			// stem + base
			g.DrawLine( pen, cx, cy + 12, cx, cy + 17 );
			g.DrawLine( pen, cx - 6, cy + 17, cx + 6, cy + 17 );
		}

		static GraphicsPath RoundRect( Rectangle r, int radius )
		{
			int d = radius * 2;
			var path = new GraphicsPath();
			if( d <= 0 || d > r.Width || d > r.Height )
			{
				path.AddRectangle( r );
				return path;
			}
			path.AddArc( r.X, r.Y, d, d, 180, 90 );
			path.AddArc( r.Right - d, r.Y, d, d, 270, 90 );
			path.AddArc( r.Right - d, r.Bottom - d, d, d, 0, 90 );
			path.AddArc( r.X, r.Bottom - d, d, d, 90, 90 );
			path.CloseFigure();
			return path;
		}

		// ---- layered-window blit ----

		void SetBitmap( Bitmap bitmap, byte opacity )
		{
			if( !IsHandleCreated )
				return;
			IntPtr screenDc = GetDC( IntPtr.Zero );
			IntPtr memDc = CreateCompatibleDC( screenDc );
			IntPtr hBitmap = IntPtr.Zero, oldBitmap = IntPtr.Zero;
			try
			{
				hBitmap = bitmap.GetHbitmap( Color.FromArgb( 0 ) );
				oldBitmap = SelectObject( memDc, hBitmap );

				SIZE size = new SIZE { cx = bitmap.Width, cy = bitmap.Height };
				POINT src = new POINT { x = 0, y = 0 };
				POINT pos = new POINT { x = Left, y = Top };
				BLENDFUNCTION blend = new BLENDFUNCTION
				{
					BlendOp = AC_SRC_OVER,
					BlendFlags = 0,
					SourceConstantAlpha = opacity,
					AlphaFormat = AC_SRC_ALPHA,
				};
				UpdateLayeredWindow( Handle, screenDc, ref pos, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA );
			}
			finally
			{
				ReleaseDC( IntPtr.Zero, screenDc );
				if( hBitmap != IntPtr.Zero )
				{
					SelectObject( memDc, oldBitmap );
					DeleteObject( hBitmap );
				}
				DeleteDC( memDc );
			}
		}

		const byte AC_SRC_OVER = 0;
		const byte AC_SRC_ALPHA = 1;
		const int ULW_ALPHA = 2;

		[StructLayout( LayoutKind.Sequential )] struct POINT { public int x, y; }
		[StructLayout( LayoutKind.Sequential )] struct SIZE { public int cx, cy; }
		[StructLayout( LayoutKind.Sequential )] struct BLENDFUNCTION
		{
			public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
		}

		[DllImport( "user32.dll", ExactSpelling = true, SetLastError = true )]
		static extern bool UpdateLayeredWindow( IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
			IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags );
		const uint MONITOR_DEFAULTTONEAREST = 2;
		const int MDT_EFFECTIVE_DPI = 0;
		[DllImport( "user32.dll" )] static extern IntPtr MonitorFromPoint( POINT pt, uint dwFlags );
		[DllImport( "Shcore.dll" )] static extern int GetDpiForMonitor( IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY );

		[DllImport( "user32.dll", ExactSpelling = true )] static extern IntPtr GetDC( IntPtr hWnd );
		[DllImport( "user32.dll", ExactSpelling = true )] static extern int ReleaseDC( IntPtr hWnd, IntPtr hDC );
		[DllImport( "gdi32.dll", ExactSpelling = true )] static extern IntPtr CreateCompatibleDC( IntPtr hDC );
		[DllImport( "gdi32.dll", ExactSpelling = true )] static extern bool DeleteDC( IntPtr hdc );
		[DllImport( "gdi32.dll", ExactSpelling = true )] static extern IntPtr SelectObject( IntPtr hDC, IntPtr hObject );
		[DllImport( "gdi32.dll", ExactSpelling = true )] static extern bool DeleteObject( IntPtr hObject );
	}
}
