using System.Runtime.CompilerServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace StickerSense.Core.Tests;

/// <summary>
/// The Windows app icon (src/StickerSense.App/StickerSense.ico) is made from the Mac app's icon
/// PNGs. The ICO format can hold PNG images directly, so this is all it takes. To rebuild it:
/// <c>STICKERSENSE_MAKE_ICON=1 dotnet test --filter AppIconTests</c>.
/// </summary>
public class AppIconTests
{
    private static readonly int[] Sizes = [16, 32, 48, 256];

    private static string WindowsFolder([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static string IconPath => Path.Combine(WindowsFolder(), "src", "StickerSense.App", "StickerSense.ico");

    [Fact]
    public void MakeIconWhenAsked()
    {
        if (Environment.GetEnvironmentVariable("STICKERSENSE_MAKE_ICON") != "1") return;
        var source = Path.Combine(WindowsFolder(), "..", "apple", "Mac", "Assets.xcassets", "AppIcon.appiconset");
        var images = new List<(int Size, byte[] Png)>
        {
            (16, File.ReadAllBytes(Path.Combine(source, "icon_16x16.png"))),
            (32, File.ReadAllBytes(Path.Combine(source, "icon_32x32.png"))),
            (48, Resize(Path.Combine(source, "icon_32x32@2x.png"), 48)), // from the 64-pixel image
            (256, File.ReadAllBytes(Path.Combine(source, "icon_256x256.png"))),
        };
        File.WriteAllBytes(IconPath, IcoFile.Write(images));
    }

    [Fact]
    public void TheIconHasEverySizeWindowsUses()
    {
        var entries = IcoFile.Read(File.ReadAllBytes(IconPath));
        Assert.Equal(Sizes, entries.Select(e => e.Size));
        foreach (var (size, png) in entries)
        {
            using var image = Image.Load<Rgba32>(png);
            Assert.Equal(size, image.Width);
            Assert.Equal(size, image.Height);
        }
    }

    private static byte[] Resize(string path, int size)
    {
        using var image = Image.Load<Rgba32>(path);
        image.Mutate(ctx => ctx.Resize(size, size, KnownResamplers.Lanczos3));
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }
}

/// <summary>Reads and writes .ico files whose images are stored as PNG (supported since Windows Vista).</summary>
internal static class IcoFile
{
    public static byte[] Write(IReadOnlyList<(int Size, byte[] Png)> images)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);            // reserved
        writer.Write((ushort)1);            // type: icon
        writer.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);          // palette colours
            writer.Write((byte)0);          // reserved
            writer.Write((ushort)1);        // colour planes
            writer.Write((ushort)32);       // bits per pixel
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) writer.Write(png);
        writer.Flush();
        return stream.ToArray();
    }

    public static List<(int Size, byte[] Png)> Read(byte[] ico)
    {
        using var reader = new BinaryReader(new MemoryStream(ico));
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        var count = reader.ReadUInt16();
        var entries = new List<(int Size, byte[] Png)>();
        for (var i = 0; i < count; i++)
        {
            int width = reader.ReadByte();
            reader.ReadBytes(7);
            var length = reader.ReadInt32();
            var offset = reader.ReadInt32();
            var png = ico.AsSpan(offset, length).ToArray();
            Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png[..4]);
            entries.Add((width == 0 ? 256 : width, png));
        }
        return entries;
    }
}
