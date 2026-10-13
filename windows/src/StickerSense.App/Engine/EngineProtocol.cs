using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using StickerSense.Core;

namespace StickerSense.App.Engine;

/// <summary>
/// Messages between StickerSense and its engine process, over the engine's standard input and
/// output: a type byte, then fields written with <see cref="BinaryWriter"/>.
/// </summary>
internal static class EngineProtocol
{
    // App → engine
    public const byte Load = 1;
    public const byte Generate = 2;
    public const byte Cancel = 3;

    // Engine → app
    public const byte Loaded = 101;
    public const byte Result = 102;
    public const byte Log = 103;

    public const byte Done = 0;
    public const byte Cancelled = 1;
    public const byte Failed = 2;

    public static void WriteRequest(BinaryWriter w, int id, VisionRequest request)
    {
        w.Write(Generate);
        w.Write(id);
        w.Write(request.Instructions);
        w.Write(request.Prompt);
        w.Write(request.ReplyStart);
        w.Write(request.MaxNewTokens);
        w.Write(request.Temperature);
        w.Write(request.Images.Count);
        foreach (var image in request.Images)
        {
            var pixels = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixels);
            w.Write(image.Width);
            w.Write(image.Height);
            w.Write(pixels.Length);
            w.Write(pixels);
        }
    }

    /// <summary>Reads a request written by <see cref="WriteRequest"/> (after its type byte). The caller disposes the images.</summary>
    public static (int Id, VisionRequest Request) ReadRequest(BinaryReader r)
    {
        var id = r.ReadInt32();
        var instructions = r.ReadString();
        var prompt = r.ReadString();
        var replyStart = r.ReadString();
        var maxNewTokens = r.ReadInt32();
        var temperature = r.ReadDouble();
        var count = r.ReadInt32();
        var images = new List<Image<Rgba32>>(count);
        for (var i = 0; i < count; i++)
        {
            var width = r.ReadInt32();
            var height = r.ReadInt32();
            var bytes = r.ReadBytes(r.ReadInt32());
            images.Add(Image.LoadPixelData<Rgba32>(bytes, width, height));
        }
        return (id, new VisionRequest(instructions, prompt, images, maxNewTokens, temperature, replyStart));
    }
}
