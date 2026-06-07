using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

string outPath = args.Length > 0
	? args[0]
	: @"d:\OneDrive\Документы\Codex\Whisper\Examples\VoiceTyper\app.ico";

int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
byte[][] pngs = sizes.Select(s =>
{
	using Bitmap bmp = Draw(s);
	using var ms = new MemoryStream();
	bmp.Save(ms, ImageFormat.Png);
	return ms.ToArray();
}).ToArray();

using (var fs = File.Create(outPath))
using (var bw = new BinaryWriter(fs))
{
	bw.Write((short)0);              // reserved
	bw.Write((short)1);              // type = icon
	bw.Write((short)sizes.Length);   // count
	int offset = 6 + 16 * sizes.Length;
	for (int i = 0; i < sizes.Length; i++)
	{
		int s = sizes[i];
		bw.Write((byte)(s >= 256 ? 0 : s)); // width
		bw.Write((byte)(s >= 256 ? 0 : s)); // height
		bw.Write((byte)0);                  // palette
		bw.Write((byte)0);                  // reserved
		bw.Write((short)1);                 // planes
		bw.Write((short)32);                // bpp
		bw.Write(pngs[i].Length);           // bytes
		bw.Write(offset);                   // offset
		offset += pngs[i].Length;
	}
	foreach (var p in pngs) bw.Write(p);
}

Console.WriteLine($"Wrote {outPath} ({sizes.Length} sizes)");

static Bitmap Draw(int size)
{
	var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
	using var g = Graphics.FromImage(bmp);
	g.SmoothingMode = SmoothingMode.AntiAlias;
	g.Clear(Color.Transparent);

	// Rounded violet tile with vertical gradient.
	int d = Math.Max(4, size / 3);
	var r = new Rectangle(1, 1, size - 3, size - 3);
	using (var path = new GraphicsPath())
	{
		path.AddArc(r.X, r.Y, d, d, 180, 90);
		path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
		path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
		path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
		path.CloseFigure();
		using var bg = new LinearGradientBrush(r,
			Color.FromArgb(255, 0x8B, 0x6C, 0xFF),
			Color.FromArgb(255, 0x5A, 0x3E, 0xE0),
			LinearGradientMode.Vertical);
		g.FillPath(bg, path);
	}

	// Microphone glyph.
	float cx = size / 2f;
	float headW = size * 0.26f;
	float headH = size * 0.40f;
	float headTop = size * 0.17f;
	using var white = new SolidBrush(Color.White);
	using var pen = new Pen(Color.White, Math.Max(1.5f, size / 16f))
	{ StartCap = LineCap.Round, EndCap = LineCap.Round };
	var head = new RectangleF(cx - headW / 2, headTop, headW, headH);
	g.FillPath(white, Capsule(head));
	float arcY = headTop + headH * 0.32f;
	float arcW = headW * 2.05f;
	g.DrawArc(pen, cx - arcW / 2, arcY, arcW, headH * 0.9f, 20, 140);
	float stemTop = headTop + headH + headH * 0.16f;
	g.DrawLine(pen, cx, stemTop, cx, stemTop + size * 0.12f);
	g.DrawLine(pen, cx - size * 0.12f, stemTop + size * 0.12f, cx + size * 0.12f, stemTop + size * 0.12f);

	return bmp;
}

static GraphicsPath Capsule(RectangleF r)
{
	var path = new GraphicsPath();
	float d = r.Width;
	path.AddArc(r.X, r.Y, d, d, 180, 180);
	path.AddArc(r.X, r.Bottom - d, d, d, 0, 180);
	path.CloseFigure();
	return path;
}
