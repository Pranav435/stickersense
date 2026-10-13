using System.IO;
using System.Text;
using Microsoft.ML.OnnxRuntimeGenAI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using StickerSense.Core;

namespace StickerSense.App.Engine;

/// <summary>
/// Qwen2.5-VL / Qwen3-VL (ONNX Runtime GenAI types "qwen2_5_vl", "qwen3_vl") on the processor.
/// GenAI's own processor prepares the image; pictures are scaled down first, since Qwen's cost
/// grows with every 32×32 pixels.
/// </summary>
internal sealed class QwenModel : IEngineModel
{
    /// <summary>
    /// Longest side of a picture given to the model: Qwen spends a token on every 32×32 pixels, so
    /// quick readings get smaller pictures (336: about 120 tokens; 4 frames of 256: about 260) and
    /// detailed descriptions larger ones (448 and 320). Measured on an i7-11700, the smaller sizes
    /// save about a third of a second on a still and two seconds on a 4-frame GIF, with the same readings.
    /// </summary>
    public const int QuickSide = 336, QuickFrameSide = 256, DetailSide = 448, DetailFrameSide = 320;

    private readonly Model model;
    private readonly Tokenizer tokenizer;
    private readonly MultiModalProcessor processor;
    private readonly Action<string> log;

    private QwenModel(Model model, Tokenizer tokenizer, MultiModalProcessor processor, Action<string> log)
    {
        this.model = model;
        this.tokenizer = tokenizer;
        this.processor = processor;
        this.log = log;
    }

    public string RunningOn => "the processor";

    public bool UsesGraphicsCard => false;

    public static QwenModel Load(EngineLoadOptions options, Action<string> log)
    {
        var started = DateTime.UtcNow;
        using var config = new Config(options.Directory);
        config.ClearProviders();
        PhiModel.ApplyThreads(config, options.Threads);
        var model = new Model(config);
        try
        {
            var loaded = new QwenModel(model, new Tokenizer(model), new MultiModalProcessor(model), log);
            log($"Model ready on the processor in {(DateTime.UtcNow - started).TotalSeconds:0.0}s");
            return loaded;
        }
        catch
        {
            model.Dispose();
            throw;
        }
    }

    public string Generate(VisionRequest request, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        // Several frames: smaller each, so an animation costs about what one still does.
        var quick = request.StopAfterLines > 0;
        var side = request.Images.Count > 1 ? (quick ? QuickFrameSide : DetailFrameSide) : (quick ? QuickSide : DetailSide);
        var pictures = request.Images.Select(i => Scaled(i, side)).ToList();
        var paths = new List<string>();
        try
        {
            var prompt = QwenVisionPrompt.Build(request.Instructions, request.Prompt, pictures.Count, request.ReplyStart);
            Images images;
            if (pictures.Count == 1)
            {
                images = Images.Load(ImagePrep.PngData(pictures[0]));
            }
            else
            {
                // GenAI loads several pictures only from files.
                var folder = Path.Combine(Path.GetTempPath(), "StickerSense", "engine");
                Directory.CreateDirectory(folder);
                foreach (var picture in pictures)
                {
                    var path = Path.Combine(folder, $"{Guid.NewGuid():N}.png");
                    File.WriteAllBytes(path, ImagePrep.PngData(picture));
                    paths.Add(path);
                }
                images = Images.Load(paths.ToArray());
            }
            using (images)
            {
                using var inputs = processor.ProcessImages(prompt, images);
                int textTokens;
                using (var sequences = tokenizer.Encode(prompt)) textTokens = sequences[0].Length;
                var imageTokens = pictures.Sum(p => QwenVisionPrompt.MaxImageTokens(p.Width, p.Height));
                ct.ThrowIfCancellationRequested();

                using var parameters = new GeneratorParams(model);
                // The KV cache is allocated to max_length up front (past_present_share_buffer): keep it tight.
                parameters.SetSearchOption("max_length", textTokens + imageTokens + request.MaxNewTokens + 16);
                parameters.SetSearchOption("do_sample", request.Temperature > 0);
                if (request.RepetitionPenalty > 1) parameters.SetSearchOption("repetition_penalty", request.RepetitionPenalty);
                if (request.Temperature > 0)
                {
                    parameters.SetSearchOption("temperature", request.Temperature);
                    parameters.SetSearchOption("top_p", 0.9);
                    parameters.SetSearchOption("top_k", 40);
                }
                using var generator = new Generator(model, parameters);
                generator.SetInputs(inputs);
                var promptTokens = generator.TokenCount();
                var read = DateTime.UtcNow;

                using var stream = tokenizer.CreateStream();
                var output = new StringBuilder();
                var produced = 0;
                for (var i = 0; i < request.MaxNewTokens; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    generator.GenerateNextToken();
                    if (generator.IsDone()) break;
                    produced++;
                    output.Append(stream.Decode(generator.GetNextTokens()[0]));
                    var text = output.ToString();
                    if (text.Contains("<|im_end|>", StringComparison.Ordinal)) break;
                    if (request.StopAfterLines > 0 && ReadingParser.CompletedLines(request.ReplyStart + text) >= request.StopAfterLines) break;
                if (request.StopAfterSentences > 0 && ReadingParser.CompletedSentences(text) >= request.StopAfterSentences) break;
                if (request.MaxLineWords > 0 && ReadingParser.LastLineWords(text) > request.MaxLineWords) break;
                }
                var done = DateTime.UtcNow;
                log($"Engine: {promptTokens} prompt tokens read in {(read - started).TotalSeconds:0.0}s, {produced} written in {(done - read).TotalSeconds:0.0}s");
                return output.ToString().Replace("<|im_end|>", "", StringComparison.Ordinal);
            }
        }
        finally
        {
            foreach (var picture in pictures) picture.Dispose();
            foreach (var path in paths)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>A copy no larger than <paramref name="side"/> high, and no wider than four times that.</summary>
    private static Image<Rgba32> Scaled(Image<Rgba32> image, int side)
    {
        var scale = Math.Min(1.0, Math.Min((double)side / image.Height, 4.0 * side / image.Width));
        if (scale >= 1.0) return image.Clone();
        return ImagePrep.Fit(image, Math.Max(32, (int)Math.Round(image.Width * scale)), Math.Max(32, (int)Math.Round(image.Height * scale)));
    }

    public void Dispose()
    {
        processor.Dispose();
        tokenizer.Dispose();
        model.Dispose();
    }
}
