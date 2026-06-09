using System.Drawing.Drawing2D;

namespace VoiceTyper
{
	/// <summary>Generates the application/tray icon at runtime (a microphone on an accent tile).</summary>
	static class AppIcon
	{
		public static Icon Create( int size = 32, bool listening = false )
		{
			using var bmp = new Bitmap( size, size );
			using( var g = Graphics.FromImage( bmp ) )
			{
				g.SmoothingMode = SmoothingMode.AntiAlias;
				g.Clear( Color.Transparent );

				// Rounded tile background.
				int d = size / 3;
				using( var path = new GraphicsPath() )
				{
					var r = new Rectangle( 1, 1, size - 3, size - 3 );
					path.AddArc( r.X, r.Y, d, d, 180, 90 );
					path.AddArc( r.Right - d, r.Y, d, d, 270, 90 );
					path.AddArc( r.Right - d, r.Bottom - d, d, d, 0, 90 );
					path.AddArc( r.X, r.Bottom - d, d, d, 90, 90 );
					path.CloseFigure();
					Color top = listening ? Color.FromArgb( 220, 80, 90 ) : Theme.Accent;
					Color bot = listening ? Color.FromArgb( 180, 40, 60 ) : Color.FromArgb( 40, 110, 210 );
					using var bg = new LinearGradientBrush( r, top, bot, LinearGradientMode.Vertical );
					g.FillPath( bg, path );
				}

				// Microphone glyph.
				float cx = size / 2f;
				float headW = size * 0.26f;
				float headH = size * 0.40f;
				float headTop = size * 0.18f;
				using var white = new SolidBrush( Color.White );
				using var pen = new Pen( Color.White, Math.Max( 1.5f, size / 18f ) ) { StartCap = LineCap.Round, EndCap = LineCap.Round };
				var head = new RectangleF( cx - headW / 2, headTop, headW, headH );
				g.FillPath( white, Capsule( head ) );
				float arcY = headTop + headH * 0.35f;
				float arcW = headW * 2.0f;
				g.DrawArc( pen, cx - arcW / 2, arcY, arcW, headH * 0.85f, 20, 140 );
				float stemTop = headTop + headH + headH * 0.18f;
				g.DrawLine( pen, cx, stemTop, cx, stemTop + size * 0.12f );
				g.DrawLine( pen, cx - size * 0.12f, stemTop + size * 0.12f, cx + size * 0.12f, stemTop + size * 0.12f );
			}

			IntPtr h = bmp.GetHicon();
			try
			{
				using var tmp = Icon.FromHandle( h );
				return (Icon)tmp.Clone();
			}
			finally
			{
				// Icon.FromHandle does not own the handle, so the HICON from GetHicon must be freed
				// explicitly or it leaks on every call.
				DestroyIcon( h );
			}
		}

		[System.Runtime.InteropServices.DllImport( "user32.dll", SetLastError = true )]
		static extern bool DestroyIcon( IntPtr hIcon );

		static GraphicsPath Capsule( RectangleF r )
		{
			var path = new GraphicsPath();
			float d = r.Width;
			path.AddArc( r.X, r.Y, d, d, 180, 180 );
			path.AddArc( r.X, r.Bottom - d, d, d, 0, 180 );
			path.CloseFigure();
			return path;
		}
	}
}
