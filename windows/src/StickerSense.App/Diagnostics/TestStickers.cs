using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp.PixelFormats;
using SharpImage = SixLabors.ImageSharp.Image;

namespace StickerSense.App.Diagnostics;

/// <summary>Stickers drawn with GDI+ for the self-test and the test chat window (no image files to ship).</summary>
internal static class TestStickers
{
    private static readonly Color Yellow = Color.FromArgb(255, 204, 51);
    private static readonly Color Rim = Color.FromArgb(229, 155, 0);
    private static readonly Color Ink = Color.FromArgb(102, 69, 0);

    /// <summary>A party face wearing a hat, with "HAPPY BDAY" written under it, on transparency.</summary>
    public static SixLabors.ImageSharp.Image<Rgba32> Birthday() => Draw(512, 512, g =>
    {
        Face(g, 116, 40, 280, mouth: Mouth.Smile);
        using var hat = new SolidBrush(Color.FromArgb(85, 172, 238));
        g.FillPolygon(hat, [new PointF(150, 92), new PointF(205, -4), new PointF(245, 70)]);
        Caption(g, "HAPPY BDAY", 512, 360, 64, Color.FromArgb(220, 30, 90));
    });

    /// <summary>Three frames: a face slides right, opens its mouth, then winks.</summary>
    public static List<SixLabors.ImageSharp.Image<Rgba32>> Animated() =>
    [
        Draw(400, 400, g => Face(g, 20, 80, 220, Mouth.Smile)),
        Draw(400, 400, g => Face(g, 90, 80, 220, Mouth.Open)),
        Draw(400, 400, g => Face(g, 160, 80, 220, Mouth.Smile, wink: true)),
    ];

    /// <summary>Black "HAPPY BDAY" on white, for the OCR check.</summary>
    public static SixLabors.ImageSharp.Image<Rgba32> CaptionOnly(string text) => Draw(640, 160, g =>
    {
        g.Clear(Color.White);
        Caption(g, text, 640, 40, 72, Color.Black);
    });

    private enum Mouth { Smile, Open }

    private static void Face(Graphics g, float x, float y, float size, Mouth mouth, bool wink = false)
    {
        using var rim = new SolidBrush(Rim);
        using var face = new SolidBrush(Yellow);
        using var ink = new SolidBrush(Ink);
        using var pen = new Pen(Ink, size * 0.05f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.FillEllipse(rim, x, y, size, size);
        g.FillEllipse(face, x + size * 0.04f, y + size * 0.04f, size * 0.92f, size * 0.92f);
        g.FillEllipse(ink, x + size * 0.3f, y + size * 0.28f, size * 0.1f, size * 0.16f);
        if (wink) g.DrawLine(pen, x + size * 0.58f, y + size * 0.36f, x + size * 0.72f, y + size * 0.36f);
        else g.FillEllipse(ink, x + size * 0.6f, y + size * 0.28f, size * 0.1f, size * 0.16f);
        if (mouth == Mouth.Open)
        {
            using var red = new SolidBrush(Color.FromArgb(122, 46, 14));
            g.FillEllipse(red, x + size * 0.38f, y + size * 0.56f, size * 0.24f, size * 0.24f);
        }
        else
        {
            g.DrawArc(pen, x + size * 0.25f, y + size * 0.38f, size * 0.5f, size * 0.38f, 20, 140);
        }
    }

    private static void Caption(Graphics g, string text, int width, float top, float size, Color color)
    {
        using var font = new Font("Arial", size, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        var measured = g.MeasureString(text, font);
        g.DrawString(text, font, brush, (width - measured.Width) / 2, top);
    }

    private static SixLabors.ImageSharp.Image<Rgba32> Draw(int width, int height, Action<Graphics> paint)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            paint(g);
        }
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            using var bgra = SharpImage.LoadPixelData<Bgra32>(bytes, width, height);
            return bgra.CloneAs<Rgba32>();
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
