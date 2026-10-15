using System.Runtime.InteropServices;
using System.Text;
using Microsoft.ML.OnnxRuntimeGenAI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using StickerSense.Core;

namespace StickerSense.App.Engine;

/// <summary>
/// Phi-3.5-vision-instruct (int4 ONNX) loaded through ONNX Runtime GenAI. Lives only in the
/// engine process (<see cref="EngineHost"/>), never in the app itself. Images go to the model
/// straight from memory, with one crop each (<see cref="PhiVisionInputs"/>).
/// </summary>
/// <summary>A vision-language model loaded in the engine process.</summary>
internal interface IEngineModel : IDisposable
{
    /// <summary>"the graphics card (DirectML)" or "the processor".</summary>
    string RunningOn { get; }

    bool UsesGraphicsCard { get; }

    /// <summary>The model's reply: what follows <see cref="VisionRequest.ReplyStart"/>.</summary>
    string Generate(VisionRequest request, CancellationToken ct);

    /// <summary>Loads whichever model the folder's genai_config.json describes.</summary>
    public static IEngineModel Load(EngineLoadOptions options, Action<string> log) =>
        ModelCatalog.ModelType(options.Directory) is "qwen3_vl" or "qwen2_5_vl"
            ? QwenModel.Load(options, log)
            : PhiModel.Load(options, log);
}

internal sealed class PhiModel : IEngineModel
{
    private readonly Model model;
    private readonly Tokenizer tokenizer;
    /// <summary>The image encoder takes float16 pixel values (the DirectML build) rather than float32.</summary>
    private readonly bool halfPixels;

    private Action<string> log = _ => { };

    private PhiModel(Model model, Tokenizer tokenizer, string runningOn, bool usesGraphicsCard, bool halfPixels)
    {
        this.model = model;
        this.tokenizer = tokenizer;
        RunningOn = runningOn;
        UsesGraphicsCard = usesGraphicsCard;
        this.halfPixels = halfPixels;
    }

    /// <summary>"the graphics card (DirectML)" or "the processor".</summary>
    public string RunningOn { get; }

    public bool UsesGraphicsCard { get; }

    /// <summary>
    /// Loads the model and runs one two-token generation on a blank image. On a graphics card,
    /// the first run compiles DirectML's kernels and sets up memory (seconds); doing it now keeps
    /// the first description quick, and a card that can't run the model fails here, so the
    /// processor takes over.
    /// </summary>
    public static PhiModel Load(EngineLoadOptions options, Action<string> log)
    {
        if (options.TryGraphicsCard)
        {
            try
            {
                return Create(options, "dml", "the graphics card (DirectML)", log);
            }
            catch (OnnxRuntimeGenAIException e)
            {
                log($"DirectML failed, using the processor instead: {e.Message}");
            }
        }
        return Create(options, null, "the processor", log);
    }

    private static PhiModel Create(EngineLoadOptions options, string? provider, string description, Action<string> log)
    {
        var started = DateTime.UtcNow;
        var directory = options.Directory;
        using var config = new Config(directory);
        config.ClearProviders();
        if (provider is not null) config.AppendProvider(provider);
        // The vision and embedding models use the decoder's session options unless they have their own.
        ApplyThreads(config, options.Threads);
        var model = new Model(config);
        PhiModel? result = null;
        try
        {
            // The DirectML build's image encoder takes 16-bit pixel values; the processor build's, 32-bit.
            var halfPixels = ModelCatalog.DetectVariant(directory) == ModelVariant.DirectML;
            result = new PhiModel(model, new Tokenizer(model), description, provider == "dml", halfPixels) { log = log };
            if (options.WarmUp || provider is not null)
            {
                using var blank = new Image<Rgba32>(64, 64, new Rgba32(255, 255, 255));
                result.Generate(new VisionRequest(options.WarmUpInstructions, "Describe this sticker or image.", [blank], MaxNewTokens: 2, Temperature: 0), CancellationToken.None);
            }
            log($"Model ready on {description} in {(DateTime.UtcNow - started).TotalSeconds:0.0}s");
            return result;
        }
        catch
        {
            // Exactly one owner frees the model: the loaded bundle if it was made, else the model itself.
            if (result is not null) result.Dispose();
            else model.Dispose();
            throw;
        }
    }

    /// <summary>Processor threads for every part of the model (the vision and embedding parts use the decoder's options).</summary>
    internal static void ApplyThreads(Config config, int threads)
    {
        if (threads <= 0) return;
        var count = threads.ToString(System.Globalization.CultureInfo.InvariantCulture);
        config.Overlay("{\"model\":{\"decoder\":{\"session_options\":{\"intra_op_num_threads\":" + count + "}}}}");
    }

    /// <summary>The model's reply: what follows <see cref="VisionRequest.ReplyStart"/>.</summary>
    public string Generate(VisionRequest request, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var imageCount = request.Images.Count;
        // Phi-3.5-vision's inputs are built around pictures; planning stickers falls back to rules.
        if (imageCount == 0) throw new NotSupportedException("Phi-3.5-vision here only answers about a picture.");
        var prompt = PhiVisionPrompt.Build(request.Instructions, request.Prompt, imageCount, request.ReplyStart);
        var chunks = new List<int[]>();
        foreach (var chunk in PhiVisionInputs.TextChunks(prompt))
        {
            using var sequences = tokenizer.Encode(chunk);
            chunks.Add(sequences[0].ToArray());
        }
        var batch = PhiVisionInputs.Prepare(request.Images);
        var inputIds = PhiVisionInputs.InputIds(chunks, batch.TokenCounts);
        ct.ThrowIfCancellationRequested();

        // GenAI reads these buffers in place, so they stay pinned until the generator is gone.
        Array pixelData = halfPixels ? Array.ConvertAll(batch.PixelValues, v => (Half)v) : batch.PixelValues;
        var sizes = batch.ImageSizes;
        var tokenCounts = batch.TokenCounts;
        var pins = new[] { GCHandle.Alloc(pixelData, GCHandleType.Pinned), GCHandle.Alloc(sizes, GCHandleType.Pinned), GCHandle.Alloc(tokenCounts, GCHandleType.Pinned) };
        try
        {
            const int crop = PhiVisionInputs.CropSize;
            using var pixelValues = new Tensor(pins[0].AddrOfPinnedObject(), [imageCount, batch.Slots, 3, crop, crop],
                halfPixels ? ElementType.float16 : ElementType.float32);
            using var imageSizes = new Tensor(pins[1].AddrOfPinnedObject(), [imageCount, 2], ElementType.int64);
            using var imageTokens = new Tensor(pins[2].AddrOfPinnedObject(), [imageCount], ElementType.int64);
            using var parameters = new GeneratorParams(model);
            // The KV cache is allocated to max_length up front (past_present_share_buffer): keep it tight.
            parameters.SetSearchOption("max_length", inputIds.Length + request.MaxNewTokens + 8);
            parameters.SetSearchOption("do_sample", request.Temperature > 0);
            if (request.RepetitionPenalty > 1) parameters.SetSearchOption("repetition_penalty", request.RepetitionPenalty);
            if (request.Temperature > 0)
            {
                parameters.SetSearchOption("temperature", request.Temperature);
                parameters.SetSearchOption("top_p", 0.9);
                parameters.SetSearchOption("top_k", 40);
            }
            using var generator = new Generator(model, parameters);
            // Input names from the Phi-3.5-vision ONNX genai_config.json ("num_image_tokens" is GenAI's own).
            generator.SetModelInput("pixel_values", pixelValues);
            generator.SetModelInput("image_sizes", imageSizes);
            generator.SetModelInput("num_image_tokens", imageTokens);
            generator.AppendTokens(inputIds);
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
                if (text.Contains("<|end|>", StringComparison.Ordinal)) break;
                // The quick reading's lines are all there: anything more is wasted time.
                if (request.StopAfterLines > 0 && ReadingParser.CompletedLines(request.ReplyStart + text) >= request.StopAfterLines) break;
                if (request.StopAfterSentences > 0 && ReadingParser.CompletedSentences(text) >= request.StopAfterSentences) break;
                if (request.MaxLineWords > 0 && ReadingParser.LastLineWords(text) > request.MaxLineWords) break;
            }
            log($"Engine: {inputIds.Length} prompt tokens read in {(read - started).TotalSeconds:0.0}s, {produced} written in {(DateTime.UtcNow - read).TotalSeconds:0.0}s");
            return output.ToString();
        }
        finally
        {
            // The generator and tensors are disposed by now (end of the try block's scope).
            foreach (var pin in pins) pin.Free();
        }
    }

    public void Dispose()
    {
        tokenizer.Dispose();
        model.Dispose();
    }
}
