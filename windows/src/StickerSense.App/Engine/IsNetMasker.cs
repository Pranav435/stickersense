using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using StickerSense.Core;

namespace StickerSense.App.Engine;

/// <summary>
/// Lifts a picture's subject off its background with ISNet (trained on anime and cartoons, Apache
/// 2.0), on the processor through the ONNX Runtime StickerSense already ships. About a second a
/// picture. Loaded for a batch of stickers and let go afterwards (it takes a few hundred megabytes).
/// </summary>
internal sealed class IsNetMasker(string modelPath, IActivityLog log) : ISubjectMasker, IDisposable
{
    private const int Side = 1024;
    private readonly object gate = new();
    private InferenceSession? session;
    private bool failed;

    public Task<float[]?> MaskAsync(Image<Rgba32> image, CancellationToken ct) => Task.Run(() => Mask(image), ct);

    private float[]? Mask(Image<Rgba32> image)
    {
        lock (gate)
        {
            if (failed || !File.Exists(modelPath)) return null;
            try
            {
                session ??= new InferenceSession(modelPath, new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });
                var input = new DenseTensor<float>([1, 3, Side, Side]);
                using (var scaled = image.Clone(c => c.Resize(Side, Side, KnownResamplers.Bicubic)))
                {
                    // rembg's preparation: divided by the brightest value, then centred on 0.5.
                    var brightest = 1f;
                    scaled.ProcessPixelRows(rows =>
                    {
                        for (var y = 0; y < Side; y++)
                        {
                            foreach (var p in rows.GetRowSpan(y)) brightest = Math.Max(brightest, Math.Max(p.R, Math.Max(p.G, p.B)));
                        }
                        var planes = input.Buffer.Span;
                        const int plane = Side * Side;
                        for (var y = 0; y < Side; y++)
                        {
                            var row = rows.GetRowSpan(y);
                            for (var x = 0; x < Side; x++)
                            {
                                var i = y * Side + x;
                                planes[i] = row[x].R / brightest - 0.5f;
                                planes[plane + i] = row[x].G / brightest - 0.5f;
                                planes[2 * plane + i] = row[x].B / brightest - 0.5f;
                            }
                        }
                    });
                }
                var name = session.InputMetadata.Keys.First();
                using var results = session.Run([NamedOnnxValue.CreateFromTensor(name, input)]);
                var output = results.First().AsEnumerable<float>().ToArray();
                float min = output.Min(), max = output.Max();
                var range = Math.Max(max - min, 1e-6f);
                // Back to the picture's size.
                using var mask = new Image<L16>(Side, Side);
                mask.ProcessPixelRows(rows =>
                {
                    for (var y = 0; y < Side; y++)
                    {
                        var row = rows.GetRowSpan(y);
                        for (var x = 0; x < Side; x++) row[x] = new L16((ushort)((output[y * Side + x] - min) / range * 65535));
                    }
                });
                mask.Mutate(c => c.Resize(image.Width, image.Height, KnownResamplers.Bicubic));
                var values = new float[image.Width * image.Height];
                mask.ProcessPixelRows(rows =>
                {
                    for (var y = 0; y < rows.Height; y++)
                    {
                        var row = rows.GetRowSpan(y);
                        for (var x = 0; x < row.Length; x++) values[y * image.Width + x] = row[x].PackedValue / 65535f;
                    }
                });
                return values;
            }
            catch (Exception e) when (e is OnnxRuntimeException or IOException or InvalidOperationException)
            {
                failed = true;
                log.Add($"Cut-out model failed; using the plain-background cut-out instead. {e.Message}");
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            session?.Dispose();
            session = null;
        }
    }
}
