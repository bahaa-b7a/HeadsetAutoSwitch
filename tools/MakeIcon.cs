// Renders assets/icon.ico + assets/icon.png with GDI+ (same geometry as assets/icon.svg).
// Each size is drawn natively so small sizes stay crisp.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcon
{
    static readonly Color Dark = ColorTranslator.FromHtml("#2E2A45");
    static readonly Color Pink = ColorTranslator.FromHtml("#FF9FB8");
    static readonly Color Cream = ColorTranslator.FromHtml("#FFF6EC");

    static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        var pngs = new List<byte[]>();
        foreach (int s in sizes)
        {
            using (var bmp = Render(s))
            using (var ms = new MemoryStream())
            {
                // small sizes as classic 32-bit DIBs (read everywhere), large ones as PNG (compact)
                if (s > 48) bmp.Save(ms, ImageFormat.Png); else WriteDib(bmp, ms);
                if (s == 256) File.WriteAllBytes(Path.Combine(outDir, "icon.png"), ms.ToArray());
                pngs.Add(ms.ToArray());
            }
        }
        using (var f = new BinaryWriter(File.Create(Path.Combine(outDir, "icon.ico"))))
        {
            f.Write((short)0); f.Write((short)1); f.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                f.Write((byte)0); f.Write((byte)0);
                f.Write((short)1); f.Write((short)32);
                f.Write(pngs[i].Length); f.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) f.Write(p);
        }
    }

    static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.ScaleTransform(size / 256f, size / 256f);
            bool tiny = size <= 24; // drop fine details that turn to mush

            using (var bg = new LinearGradientBrush(new PointF(8, 8), new PointF(248, 248),
                ColorTranslator.FromHtml("#8B7CFF"), ColorTranslator.FromHtml("#5BC8F5")))
                g.FillPath(bg, Rounded(8, 8, 240, 240, 60));

            using (var b = new SolidBrush(Cream)) g.FillEllipse(b, 66, 88, 124, 124);
            using (var pen = new Pen(Dark, tiny ? 24 : 18) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, 46, 68, 164, 164, 195, 150);
            using (var b = new SolidBrush(Dark))
            {
                g.FillPath(b, Rounded(38, 118, 38, 68, 17));
                g.FillPath(b, Rounded(180, 118, 38, 68, 17));
                float er = tiny ? 12 : 8.5f;
                g.FillEllipse(b, 106 - er, 148 - er, er * 2, er * 2);
                g.FillEllipse(b, 150 - er, 148 - er, er * 2, er * 2);
            }
            using (var b = new SolidBrush(Pink))
            {
                g.FillPath(b, Rounded(49, 132, 10, 40, 5));
                g.FillPath(b, Rounded(197, 132, 10, 40, 5));
            }
            if (!tiny)
            {
                using (var b = new SolidBrush(Color.White))
                {
                    g.FillEllipse(b, 108.5f - 2.6f, 145.5f - 2.6f, 5.2f, 5.2f);
                    g.FillEllipse(b, 152.5f - 2.6f, 145.5f - 2.6f, 5.2f, 5.2f);
                }
                using (var b = new SolidBrush(Color.FromArgb(217, Pink)))
                {
                    g.FillEllipse(b, 82, 161.5f, 22, 13);
                    g.FillEllipse(b, 152, 161.5f, 22, 13);
                }
                using (var pen = new Pen(Dark, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawBezier(pen, 119, 166, 125, 172, 131, 172, 137, 166);
            }
        }
        return bmp;
    }

    static void WriteDib(Bitmap bmp, Stream s)
    {
        int n = bmp.Width;
        var w = new BinaryWriter(s);
        w.Write(40); w.Write(n); w.Write(n * 2); w.Write((short)1); w.Write((short)32);
        w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (int y = n - 1; y >= 0; y--)          // XOR (color) bitmap, bottom-up BGRA
            for (int x = 0; x < n; x++)
            {
                Color c = bmp.GetPixel(x, y);
                w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
            }
        int stride = ((n + 31) / 32) * 4;         // AND mask: all zero, alpha channel does the work
        w.Write(new byte[stride * n]);
        w.Flush();
    }

    static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
