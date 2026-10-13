using System.Numerics;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace StickerSense.Core.Tests;

/// <summary>Emoji-like test art. Several are deliberately near-identical (same face, different eyes or mouth).</summary>
public enum Art
{
    Joy, Sob, SweatSmile, Rofl, Blush, HeartEyes, SmilingTear, Laughing, SlightSmile, Grinning,
    Dog, Cat, Bear, Fox, Cake, Fire, RedHeart, OrangeHeart,
    Sun, Party, Frog, Wave, HeartEyesCat, Beach,
}

/// <summary>
/// Renders sticker-like test images at runtime (emoji-like art + caption on transparency),
/// so the tests need no binary fixtures. Mirrors the Swift test fixtures.
/// </summary>
internal static class Fixtures
{
    public static readonly Color Pink = Color.ParseHex("FF2D55");

    public static Image<Rgba32> Sticker(
        Art art,
        string caption = "",
        int size = 512,
        float artX = 0.12f,
        Color? captionColor = null,
        Color? background = null)
    {
        var image = new Image<Rgba32>(size, size, (background ?? Color.Transparent).ToPixel<Rgba32>());
        float s = size;
        var painter = new Painter(artX * s, 0.12f * s, 0.56f * s, art == Art.Rofl ? -28f : 0f);
        image.Mutate(ctx =>
        {
            painter.Paint(ctx, art);
            if (caption.Length > 0) DrawCaption(ctx, caption, s, captionColor ?? Pink);
        });
        return image;
    }

    /// <summary>An "animated sticker": the cat slides across and its eyes turn into hearts.</summary>
    public static List<Image<Rgba32>> AnimatedFrames() =>
    [
        Sticker(Art.Cat, "hi!", artX: 0.02f),
        Sticker(Art.Cat, "hi!", artX: 0.25f),
        Sticker(Art.HeartEyesCat, "hi!", artX: 0.42f),
    ];

    /// <summary>
    /// Simulates the same sticker captured from a chat screen: different scale, composited on
    /// white (what a transparent sticker looks like in a light chat).
    /// </summary>
    public static Image<Rgba32> OnScreenCapture(Image<Rgba32> image, double scale = 0.6)
    {
        int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
        var canvas = new Image<Rgba32>(w, h, Color.White.ToPixel<Rgba32>());
        using var scaled = image.Clone(ctx => ctx.Resize(w, h));
        canvas.Mutate(ctx => ctx.DrawImage(scaled, 1f));
        return canvas;
    }

    /// <summary>The sticker smaller and off-centre inside a larger white area (a loose focus rectangle).</summary>
    public static Image<Rgba32> Padded(Image<Rgba32> image, int canvasSize, int stickerSize, int x, int y)
    {
        var canvas = new Image<Rgba32>(canvasSize, canvasSize, Color.White.ToPixel<Rgba32>());
        using var scaled = image.Clone(ctx => ctx.Resize(stickerSize, stickerSize));
        canvas.Mutate(ctx => ctx.DrawImage(scaled, new Point(x, y), 1f));
        return canvas;
    }

    public static readonly Rgba32 ChatBeige = new(237, 230, 219);

    /// <summary>
    /// A sticker as it appears in a group chat: the sender's name above it and the time below,
    /// all inside the focused item's rectangle. Also returns the OCR lines a reader would find,
    /// with the boxes where each text was drawn.
    /// </summary>
    public static (Image<Rgba32> Image, List<OcrLine> Lines) GroupChatCapture(string sender, Art art, string caption)
    {
        const int w = 280, h = 420;
        var image = new Image<Rgba32>(w, h, ChatBeige);
        var lines = new List<OcrLine>();
        var painter = new Painter(40, 60, 200, 0);
        image.Mutate(ctx =>
        {
            painter.Paint(ctx, art);
            lines.Add(new OcrLine(sender, DrawText(ctx, sender, 12, 14, 22, Color.Teal, w, h)));
            lines.Add(new OcrLine(caption, DrawText(ctx, caption, 100, 300, 36, Color.Black, w, h)));
            lines.Add(new OcrLine("15:48", DrawText(ctx, "15:48", w - 70, h - 34, 18, Color.Gray, w, h)));
        });
        return (image, lines);
    }

    /// <summary>Draws text with its top-left at (x, y) and returns where it ended up, as a normalised box.</summary>
    private static NormalizedBox DrawText(IImageProcessingContext ctx, string text, float x, float y, float size, Color color, int w, int h)
    {
        if (CaptionFamily.Value is { } family)
        {
            var font = family.CreateFont(size, FontStyle.Bold);
            var options = new RichTextOptions(font) { Origin = new PointF(x, y) };
            ctx.DrawText(options, text, color);
            var bounds = TextMeasurer.MeasureBounds(text, options);
            return NormalizedBox.FromPixels(bounds.X, bounds.Y, bounds.Width, bounds.Height, w, h);
        }
        // No fonts on this machine: blocky stand-in letters.
        var cell = size * 0.6f;
        for (var i = 0; i < text.Length; i++)
        {
            ctx.Fill(color, new RectangularPolygon(x + i * cell, y, cell * 0.8f, size));
        }
        return NormalizedBox.FromPixels(x, y, text.Length * cell, size, w, h);
    }

    private static readonly System.Collections.Concurrent.ConcurrentBag<string> TempDirectories = CleanUpAtExit();

    /// <summary>A new empty folder under %TEMP%, deleted when the test run ends.</summary>
    public static string TempDirectory()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StickerSenseTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        TempDirectories.Add(path);
        return path;
    }

    private static System.Collections.Concurrent.ConcurrentBag<string> CleanUpAtExit()
    {
        var bag = new System.Collections.Concurrent.ConcurrentBag<string>();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var path in bag)
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Still in use (a library file a test left open): the OS cleans %TEMP% eventually.
                }
            }
        };
        return bag;
    }

    // MARK: Captions

    private static readonly Lazy<FontFamily?> CaptionFamily = new(() =>
    {
        foreach (var name in new[] { "Arial", "Helvetica", "Segoe UI", "DejaVu Sans", "Liberation Sans", "Verdana" })
        {
            if (SystemFonts.TryGet(name, out var family)) return family;
        }
        return SystemFonts.Families.Cast<FontFamily?>().FirstOrDefault();
    });

    private static void DrawCaption(IImageProcessingContext ctx, string caption, float s, Color color)
    {
        if (CaptionFamily.Value is { } family)
        {
            var font = family.CreateFont(s * 0.13f, FontStyle.Bold);
            var options = new RichTextOptions(font)
            {
                Origin = new PointF(s / 2, s * 0.74f),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            ctx.DrawText(options, caption, color);
            return;
        }
        // No fonts on this machine: draw deterministic blocky "letters" instead.
        var cell = s * 0.06f;
        var left = s / 2 - caption.Length * cell * 0.5f;
        for (var i = 0; i < caption.Length; i++)
        {
            int code = caption[i];
            for (var bit = 0; bit < 6; bit++)
            {
                if ((code >> bit & 1) == 0) continue;
                ctx.Fill(color, new RectangularPolygon(left + i * cell + bit % 2 * cell / 2, s * 0.76f + bit / 2 * cell / 2, cell / 2, cell / 2));
            }
        }
    }

    // MARK: Art

    /// <summary>Draws shapes given in a unit square (0…1, y down) into a pixel box, optionally rotated.</summary>
    private sealed class Painter(float left, float top, float side, float degrees)
    {
        private static readonly Color Yellow = Color.ParseHex("FFCC33");
        private static readonly Color Rim = Color.ParseHex("E59B00");
        private static readonly Color Dark = Color.ParseHex("664500");
        private static readonly Color MouthRed = Color.ParseHex("7A2E0E");
        private static readonly Color TearBlue = Color.ParseHex("5DADEC");
        private static readonly Color HeartRed = Color.ParseHex("DD2E44");
        private static readonly Color HeartOrange = Color.ParseHex("F4900C");
        private static readonly Color Blush = Color.ParseHex("FF7892");

        private readonly Matrix3x2 rotation = Matrix3x2.CreateRotation(degrees * MathF.PI / 180f, new Vector2(left + side / 2, top + side / 2));
        private IImageProcessingContext ctx = null!;

        private PointF P(float x, float y) => new(left + x * side, top + y * side);
        private IPath Rotated(IPath path) => degrees == 0 ? path : path.Transform(rotation);

        private void Ellipse(float cx, float cy, float rx, float ry, Color color) =>
            ctx.Fill(color, Rotated(new EllipsePolygon(P(cx, cy), new SizeF(2 * rx * side, 2 * ry * side))));

        private void Circle(float cx, float cy, float r, Color color) => Ellipse(cx, cy, r, r, color);

        private void Poly(Color color, params (float X, float Y)[] points) =>
            ctx.Fill(color, Rotated(new Polygon(new LinearLineSegment(points.Select(p => P(p.X, p.Y)).ToArray()))));

        private void Rect(float x, float y, float w, float h, Color color) => Poly(color, (x, y), (x + w, y), (x + w, y + h), (x, y + h));

        private void Stroke(Color color, float thickness, params (float X, float Y)[] points) =>
            ctx.Draw(color, thickness * side, Rotated(new SixLabors.ImageSharp.Drawing.Path(new LinearLineSegment(points.Select(p => P(p.X, p.Y)).ToArray()))));

        /// <summary>Arc with angles in degrees, counter-clockwise from the right, y up (so 20…160 is a "^").</summary>
        private void Arc(float cx, float cy, float r, float from, float to, float thickness, Color color)
        {
            var points = new List<(float, float)>();
            for (var i = 0; i <= 24; i++)
            {
                var a = (from + (to - from) * i / 24f) * MathF.PI / 180f;
                points.Add((cx + r * MathF.Cos(a), cy - r * MathF.Sin(a)));
            }
            Stroke(color, thickness, points.ToArray());
        }

        private void Heart(float cx, float cy, float size, Color color)
        {
            var r = size * 0.27f;
            Circle(cx - r * 0.95f, cy - size * 0.12f, r, color);
            Circle(cx + r * 0.95f, cy - size * 0.12f, r, color);
            Poly(color, (cx - size * 0.5f, cy - size * 0.02f), (cx + size * 0.5f, cy - size * 0.02f), (cx, cy + size * 0.48f));
        }

        private void Face(Color? fill = null)
        {
            Circle(0.5f, 0.5f, 0.48f, Rim);
            Circle(0.5f, 0.5f, 0.45f, fill ?? Yellow);
        }

        private void DotEyes(float y = 0.38f)
        {
            Ellipse(0.35f, y, 0.045f, 0.075f, Dark);
            Ellipse(0.65f, y, 0.045f, 0.075f, Dark);
        }

        private void HappyEyes(float y = 0.42f)
        {
            Arc(0.33f, y, 0.08f, 20, 160, 0.035f, Dark);
            Arc(0.67f, y, 0.08f, 20, 160, 0.035f, Dark);
        }

        private void SquintEyes()
        {
            Stroke(Dark, 0.035f, (0.24f, 0.30f), (0.38f, 0.38f), (0.24f, 0.46f));
            Stroke(Dark, 0.035f, (0.76f, 0.30f), (0.62f, 0.38f), (0.76f, 0.46f));
        }

        private void Grin(float top = 0.58f, float bottom = 0.84f, float halfWidth = 0.25f)
        {
            var points = new List<(float, float)> { (0.5f - halfWidth, top) };
            for (var i = 0; i <= 16; i++)
            {
                var a = MathF.PI * i / 16f;
                points.Add((0.5f - halfWidth * MathF.Cos(a), top + (bottom - top) * MathF.Sin(a)));
            }
            Poly(MouthRed, points.ToArray());
            Rect(0.5f - halfWidth * 0.92f, top, halfWidth * 1.84f, (bottom - top) * 0.25f, Color.White);
        }

        public void Paint(IImageProcessingContext context, Art art)
        {
            ctx = context;
            switch (art)
            {
                case Art.Joy:
                case Art.Rofl:
                    Face();
                    HappyEyes();
                    Grin();
                    Ellipse(0.12f, 0.5f, 0.07f, 0.11f, TearBlue);
                    Ellipse(0.88f, 0.5f, 0.07f, 0.11f, TearBlue);
                    break;
                case Art.Sob:
                    Face();
                    Arc(0.33f, 0.36f, 0.08f, 200, 340, 0.035f, Dark);
                    Arc(0.67f, 0.36f, 0.08f, 200, 340, 0.035f, Dark);
                    Rect(0.24f, 0.42f, 0.13f, 0.5f, TearBlue);
                    Rect(0.63f, 0.42f, 0.13f, 0.5f, TearBlue);
                    Ellipse(0.5f, 0.7f, 0.12f, 0.09f, MouthRed);
                    break;
                case Art.SweatSmile:
                    Face();
                    HappyEyes();
                    Grin();
                    Poly(TearBlue, (0.84f, 0.08f), (0.76f, 0.24f), (0.92f, 0.24f));
                    Circle(0.84f, 0.25f, 0.08f, TearBlue);
                    break;
                case Art.Blush:
                    Face();
                    HappyEyes(0.4f);
                    Arc(0.5f, 0.52f, 0.2f, 210, 330, 0.035f, Dark);
                    Ellipse(0.24f, 0.58f, 0.09f, 0.055f, Blush);
                    Ellipse(0.76f, 0.58f, 0.09f, 0.055f, Blush);
                    break;
                case Art.HeartEyes:
                    Face();
                    Heart(0.32f, 0.38f, 0.2f, HeartRed);
                    Heart(0.68f, 0.38f, 0.2f, HeartRed);
                    Grin();
                    break;
                case Art.SmilingTear:
                    Face();
                    DotEyes();
                    Arc(0.5f, 0.5f, 0.22f, 215, 325, 0.035f, Dark);
                    Ellipse(0.28f, 0.56f, 0.05f, 0.08f, TearBlue);
                    break;
                case Art.Laughing:
                    Face();
                    SquintEyes();
                    Grin(0.55f, 0.88f, 0.28f);
                    break;
                case Art.SlightSmile:
                    Face();
                    DotEyes();
                    Arc(0.5f, 0.5f, 0.24f, 225, 315, 0.035f, Dark);
                    break;
                case Art.Grinning:
                    Face();
                    DotEyes(0.34f);
                    Grin(0.56f, 0.8f, 0.3f);
                    break;
                case Art.Dog:
                    Ellipse(0.13f, 0.45f, 0.13f, 0.27f, Color.ParseHex("8B5A2B"));
                    Ellipse(0.87f, 0.45f, 0.13f, 0.27f, Color.ParseHex("8B5A2B"));
                    Circle(0.5f, 0.5f, 0.38f, Color.ParseHex("D9B38C"));
                    Ellipse(0.5f, 0.68f, 0.19f, 0.14f, Color.ParseHex("F5E6D3"));
                    Ellipse(0.5f, 0.6f, 0.08f, 0.055f, Color.Black);
                    Circle(0.36f, 0.42f, 0.045f, Color.Black);
                    Circle(0.64f, 0.42f, 0.045f, Color.Black);
                    break;
                case Art.Cat:
                case Art.HeartEyesCat:
                    Poly(Color.ParseHex("F18F26"), (0.14f, 0.42f), (0.2f, 0.06f), (0.44f, 0.24f));
                    Poly(Color.ParseHex("F18F26"), (0.86f, 0.42f), (0.8f, 0.06f), (0.56f, 0.24f));
                    Circle(0.5f, 0.55f, 0.38f, Color.ParseHex("FFCC4D"));
                    if (art == Art.HeartEyesCat)
                    {
                        Heart(0.35f, 0.48f, 0.17f, HeartRed);
                        Heart(0.65f, 0.48f, 0.17f, HeartRed);
                    }
                    else
                    {
                        Ellipse(0.36f, 0.48f, 0.04f, 0.07f, Dark);
                        Ellipse(0.64f, 0.48f, 0.04f, 0.07f, Dark);
                    }
                    Poly(Color.ParseHex("E75A70"), (0.45f, 0.6f), (0.55f, 0.6f), (0.5f, 0.66f));
                    Stroke(Dark, 0.015f, (0.08f, 0.62f), (0.3f, 0.65f));
                    Stroke(Dark, 0.015f, (0.92f, 0.62f), (0.7f, 0.65f));
                    break;
                case Art.Bear:
                    Circle(0.2f, 0.22f, 0.13f, Color.ParseHex("8A4B38"));
                    Circle(0.8f, 0.22f, 0.13f, Color.ParseHex("8A4B38"));
                    Circle(0.5f, 0.55f, 0.4f, Color.ParseHex("C1694F"));
                    Ellipse(0.5f, 0.7f, 0.17f, 0.13f, Color.ParseHex("E6B8A2"));
                    Ellipse(0.5f, 0.64f, 0.07f, 0.05f, Color.Black);
                    Circle(0.36f, 0.46f, 0.04f, Color.Black);
                    Circle(0.64f, 0.46f, 0.04f, Color.Black);
                    break;
                case Art.Fox:
                    Poly(Color.ParseHex("F4900C"), (0.08f, 0.1f), (0.32f, 0.3f), (0.68f, 0.3f), (0.92f, 0.1f), (0.88f, 0.55f), (0.5f, 0.92f), (0.12f, 0.55f));
                    Poly(Color.White, (0.14f, 0.55f), (0.5f, 0.66f), (0.5f, 0.9f));
                    Poly(Color.White, (0.86f, 0.55f), (0.5f, 0.66f), (0.5f, 0.9f));
                    Circle(0.5f, 0.86f, 0.05f, Color.Black);
                    Circle(0.35f, 0.45f, 0.04f, Color.Black);
                    Circle(0.65f, 0.45f, 0.04f, Color.Black);
                    break;
                case Art.Cake:
                    Ellipse(0.5f, 0.88f, 0.45f, 0.07f, Color.ParseHex("CCD6DD"));
                    Rect(0.14f, 0.5f, 0.72f, 0.36f, Color.ParseHex("F4ABBA"));
                    Rect(0.14f, 0.5f, 0.72f, 0.08f, Color.White);
                    foreach (var x in new[] { 0.3f, 0.5f, 0.7f })
                    {
                        Rect(x - 0.025f, 0.3f, 0.05f, 0.2f, Color.ParseHex("55ACEE"));
                        Ellipse(x, 0.24f, 0.035f, 0.06f, Color.ParseHex("FFAC33"));
                    }
                    break;
                case Art.Fire:
                    Poly(Color.ParseHex("F4900C"), (0.5f, 0.02f), (0.78f, 0.35f), (0.86f, 0.7f), (0.7f, 0.94f), (0.3f, 0.94f), (0.14f, 0.7f), (0.24f, 0.4f), (0.38f, 0.5f));
                    Poly(Color.ParseHex("FFCC4D"), (0.5f, 0.4f), (0.68f, 0.68f), (0.62f, 0.9f), (0.38f, 0.9f), (0.32f, 0.7f));
                    break;
                case Art.RedHeart:
                    Heart(0.5f, 0.5f, 0.9f, HeartRed);
                    break;
                case Art.OrangeHeart:
                    Heart(0.5f, 0.5f, 0.9f, HeartOrange);
                    break;
                case Art.Sun:
                    for (var i = 0; i < 8; i++)
                    {
                        var a = i * MathF.PI / 4;
                        Poly(Color.ParseHex("FFAC33"),
                            (0.5f + 0.48f * MathF.Cos(a), 0.5f + 0.48f * MathF.Sin(a)),
                            (0.5f + 0.3f * MathF.Cos(a + 0.25f), 0.5f + 0.3f * MathF.Sin(a + 0.25f)),
                            (0.5f + 0.3f * MathF.Cos(a - 0.25f), 0.5f + 0.3f * MathF.Sin(a - 0.25f)));
                    }
                    Circle(0.5f, 0.5f, 0.28f, Color.ParseHex("FFCC4D"));
                    break;
                case Art.Party:
                    Face();
                    HappyEyes(0.44f);
                    Arc(0.5f, 0.56f, 0.18f, 210, 330, 0.035f, Dark);
                    Poly(Color.ParseHex("AA8DD8"), (0.08f, 0.32f), (0.28f, 0.02f), (0.4f, 0.16f));
                    Poly(Color.ParseHex("55ACEE"), (0.62f, 0.68f), (0.95f, 0.6f), (0.95f, 0.76f));
                    break;
                case Art.Frog:
                    Ellipse(0.5f, 0.62f, 0.46f, 0.32f, Color.ParseHex("77B255"));
                    Circle(0.28f, 0.3f, 0.15f, Color.ParseHex("77B255"));
                    Circle(0.72f, 0.3f, 0.15f, Color.ParseHex("77B255"));
                    Circle(0.28f, 0.3f, 0.09f, Color.White);
                    Circle(0.72f, 0.3f, 0.09f, Color.White);
                    Circle(0.28f, 0.3f, 0.045f, Color.Black);
                    Circle(0.72f, 0.3f, 0.045f, Color.Black);
                    Arc(0.5f, 0.52f, 0.3f, 220, 320, 0.03f, Color.ParseHex("3E721D"));
                    break;
                case Art.Wave:
                    var skin = Color.ParseHex("FFDC5D");
                    Rect(0.3f, 0.45f, 0.42f, 0.45f, skin);
                    for (var i = 0; i < 4; i++) Rect(0.3f + i * 0.105f, 0.1f + Math.Abs(i - 1.5f) * 0.05f, 0.09f, 0.4f, skin);
                    Poly(skin, (0.3f, 0.6f), (0.1f, 0.42f), (0.18f, 0.36f), (0.34f, 0.5f));
                    Stroke(Color.ParseHex("55ACEE"), 0.03f, (0.8f, 0.15f), (0.9f, 0.3f));
                    break;
                case Art.Beach:
                    Rect(0f, 0.55f, 1f, 0.2f, Color.ParseHex("3B88C3"));
                    Rect(0f, 0.75f, 1f, 0.25f, Color.ParseHex("F5D78E"));
                    Rect(0.48f, 0.3f, 0.03f, 0.55f, Color.ParseHex("8A4B38"));
                    Arc(0.5f, 0.32f, 0.3f, 0, 180, 0.12f, Color.ParseHex("DD2E44"));
                    break;
            }
        }
    }
}
