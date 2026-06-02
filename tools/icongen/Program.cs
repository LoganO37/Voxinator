using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

// One-off generator for Voxinator's brand assets. Draws the same speaker mark used in the
// app's web header (gradient rounded-square + white speaker cone + two sound-wave arcs) and
// emits a multi-resolution .ico plus a dark install splash. Run via:
//   dotnet run --project tools/icongen -- <out.ico> <splash.png>
internal static class IconGen
{
    private static readonly Color C1 = ColorTranslator.FromHtml("#4f8cff"); // accent (top-left)
    private static readonly Color C2 = ColorTranslator.FromHtml("#8b5cff"); // purple (bottom-right)

    private static int Main(string[] args)
    {
        // Alternate mode: emit browser-extension PNG icons (16/32/48/128) into a folder.
        //   dotnet run --project tools/icongen -- --pngs <outDir>
        if (args.Length >= 2 && args[0] == "--pngs")
        {
            string outDir = args[1];
            Directory.CreateDirectory(outDir);
            foreach (var s in new[] { 16, 32, 48, 128 })
            {
                using var bmp = RenderLogo(s);
                bmp.Save(Path.Combine(outDir, $"icon{s}.png"), ImageFormat.Png);
            }
            Console.WriteLine($"wrote extension PNG icons (16/32/48/128) to {outDir}");
            return 0;
        }

        string ico = args.Length > 0 ? args[0] : "voxinator.ico";
        string splash = args.Length > 1 ? args[1] : "splash.png";

        // Small frames MUST be classic BMP/DIB: System.Drawing.Icon / GDI (used by Form.Icon for
        // the title bar + taskbar) can't render PNG-compressed small frames, even though the shell
        // (shortcuts, Explorer) can. Use PNG only for the 256px frame.
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        var images = new List<byte[]>();
        foreach (var s in sizes)
        {
            using var bmp = RenderLogo(s);
            if (s >= 256) { using var ms = new MemoryStream(); bmp.Save(ms, ImageFormat.Png); images.Add(ms.ToArray()); }
            else images.Add(EncodeDib(bmp));
        }
        WriteIco(ico, sizes, images);

        using (var sp = RenderSplash(500, 300)) sp.Save(splash, ImageFormat.Png);

        Console.WriteLine($"wrote {ico} ({sizes.Length} sizes) and {splash}");
        return 0;
    }

    private static Bitmap RenderLogo(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        DrawLogo(g, 0, 0, size);
        return bmp;
    }

    // Draws the full mark (gradient rounded square + white speaker) into a size×size box at (x,y).
    private static void DrawLogo(Graphics g, float x, float y, float size)
    {
        var rect = new RectangleF(x, y, size, size);
        using (var path = RoundedRect(rect, size * 0.22f))
        using (var brush = new LinearGradientBrush(rect, C1, C2, 45f)) // 45° ≈ CSS 135deg (TL→BR)
            g.FillPath(brush, path);

        float u = size / 100f; // speaker geometry defined in a 0..100 space
        PointF P(float a, float b) => new(x + a * u, y + b * u);

        using (var spk = new GraphicsPath())
        {
            spk.AddPolygon(new[] { P(24, 42), P(36, 42), P(52, 30), P(52, 70), P(36, 58), P(24, 58) });
            using var white = new SolidBrush(Color.White);
            g.FillPath(white, spk);
        }
        using (var pen = new Pen(Color.White, Math.Max(1f, 5f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float cx = x + 52 * u, cy = y + 50 * u;
            Arc(g, pen, cx, cy, 14 * u);
            Arc(g, pen, cx, cy, 24 * u);
        }
    }

    private static void Arc(Graphics g, Pen pen, float cx, float cy, float r)
        => g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, -52f, 104f); // right-bulging wave

    private static GraphicsPath RoundedRect(RectangleF b, float r)
    {
        float d = r * 2;
        var p = new GraphicsPath();
        p.AddArc(b.Left, b.Top, d, d, 180, 90);
        p.AddArc(b.Right - d, b.Top, d, d, 270, 90);
        p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
        p.AddArc(b.Left, b.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static Bitmap RenderSplash(int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var bg = new SolidBrush(ColorTranslator.FromHtml("#0f1115"))) g.FillRectangle(bg, 0, 0, w, h);

        const float logo = 96;
        DrawLogo(g, (w - logo) / 2f, h / 2f - logo - 4, logo);

        using var f = new Font("Segoe UI", 26, FontStyle.Bold, GraphicsUnit.Pixel);
        using var tw = new SolidBrush(ColorTranslator.FromHtml("#e7eaf0"));
        const string text = "Voxinator";
        var ts = g.MeasureString(text, f);
        g.DrawString(text, f, tw, (w - ts.Width) / 2f, h / 2f + 10);
        return bmp;
    }

    // Encode a 32bpp BGRA icon image as a classic DIB (BITMAPINFOHEADER + bottom-up pixels +
    // a zeroed 1bpp AND mask, since alpha carries transparency).
    private static byte[] EncodeDib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        var pixels = new byte[stride * h];
        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        bmp.UnlockBits(data);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(40);            // biSize
        bw.Write(w);             // biWidth
        bw.Write(h * 2);         // biHeight (XOR color + AND mask)
        bw.Write((short)1);      // biPlanes
        bw.Write((short)32);     // biBitCount
        bw.Write(0);             // biCompression = BI_RGB
        bw.Write(0);             // biSizeImage
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0); // ppm + palette counts
        for (int y = h - 1; y >= 0; y--) bw.Write(pixels, y * stride, w * 4); // bottom-up, no padding at 32bpp
        bw.Write(new byte[((w + 31) / 32) * 4 * h]);        // AND mask, all zero
        return ms.ToArray();
    }

    // Minimal multi-image ICO writer. Entries are DIB (small) or PNG (256); Windows detects which
    // by the data's leading bytes.
    private static void WriteIco(string path, int[] sizes, List<byte[]> pngs)
    {
        using var fs = new FileStream(path, FileMode.Create);
        using var bw = new BinaryWriter(fs);
        bw.Write((short)0); // reserved
        bw.Write((short)1); // type: icon
        bw.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            int s = sizes[i];
            bw.Write((byte)(s >= 256 ? 0 : s)); // width  (0 means 256)
            bw.Write((byte)(s >= 256 ? 0 : s)); // height (0 means 256)
            bw.Write((byte)0);   // palette count
            bw.Write((byte)0);   // reserved
            bw.Write((short)1);  // color planes
            bw.Write((short)32); // bits per pixel
            bw.Write(pngs[i].Length);
            bw.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs) bw.Write(png);
    }
}
