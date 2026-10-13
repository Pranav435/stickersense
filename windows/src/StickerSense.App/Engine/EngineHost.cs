using System.Collections.Concurrent;
using System.IO;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace StickerSense.App.Engine;

/// <summary>
/// <c>StickerSense.exe --engine</c>: the process the model runs in, started by StickerSense with
/// its standard input and output connected (<see cref="EngineProcess"/>). Keeping ONNX Runtime
/// out of the app process means a native fault in the model can't take StickerSense down with
/// it: the app notices, says the description failed, and starts a new engine.
/// </summary>
/// <remarks>
/// One request runs at a time (the app queues them); a reader thread handles cancellation, and
/// ends the process when StickerSense goes away (its end of the pipe closes): the model and
/// GenAI are shut down first, since leaving GenAI running at exit can crash the process.
/// </remarks>
internal static class EngineHost
{
    private sealed record Work(byte Type, int Id, Core.VisionRequest? Request, string Directory = "", bool TryGraphicsCard = false, string WarmUp = "");

    public static int Run()
    {
        var input = new BinaryReader(Console.OpenStandardInput());
        var output = new BinaryWriter(Console.OpenStandardOutput());
        var writeGate = new object();
        void Send(Action<BinaryWriter> write)
        {
            lock (writeGate)
            {
                try
                {
                    write(output);
                    output.Flush();
                }
                catch (IOException)
                {
                    // StickerSense has gone; the reader thread is shutting down.
                }
            }
        }
        void Log(string message) => Send(w => { w.Write(EngineProtocol.Log); w.Write(message); });

        var work = new BlockingCollection<Work>();
        var cancellations = new ConcurrentDictionary<int, CancellationTokenSource>();
        var reader = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    var type = input.ReadByte();
                    switch (type)
                    {
                        case EngineProtocol.Load:
                            work.Add(new Work(type, 0, null, input.ReadString(), input.ReadBoolean(), input.ReadString()));
                            break;
                        case EngineProtocol.Generate:
                            var (id, request) = EngineProtocol.ReadRequest(input);
                            cancellations[id] = new CancellationTokenSource();
                            work.Add(new Work(type, id, request));
                            break;
                        case EngineProtocol.Cancel:
                            if (cancellations.TryGetValue(input.ReadInt32(), out var cts)) cts.Cancel();
                            break;
                    }
                }
            }
            catch (Exception e) when (e is EndOfStreamException or IOException or ObjectDisposedException)
            {
                // StickerSense closed its end (or went away): stop the request in progress and shut
                // down in order. ONNX Runtime GenAI must be shut down before the process ends.
                foreach (var cts in cancellations.Values) cts.Cancel();
                work.CompleteAdding();
                // A model can't be interrupted while it reads the prompt; don't linger for long.
                Thread.Sleep(TimeSpan.FromSeconds(30));
                Environment.Exit(0);
            }
        }) { IsBackground = true, Name = "engine requests" };
        reader.Start();

        using var oga = new OgaHandle();
        PhiModel? model = null;
        foreach (var item in work.GetConsumingEnumerable())
        {
            if (item.Type == EngineProtocol.Load)
            {
                model?.Dispose();
                model = null;
                try
                {
                    model = PhiModel.Load(item.Directory, item.TryGraphicsCard, item.WarmUp, Log);
                    var loaded = model;
                    Send(w => { w.Write(EngineProtocol.Loaded); w.Write(true); w.Write(loaded.RunningOn); w.Write(loaded.UsesGraphicsCard); w.Write(""); });
                }
                catch (Exception e)
                {
                    Send(w => { w.Write(EngineProtocol.Loaded); w.Write(false); w.Write(""); w.Write(false); w.Write(e.ToString()); });
                }
                continue;
            }

            var request = item.Request!;
            var token = cancellations.TryGetValue(item.Id, out var source) ? source.Token : CancellationToken.None;
            byte status;
            string text;
            try
            {
                if (model is null) throw new InvalidOperationException("The model isn't loaded.");
                text = model.Generate(request, token);
                status = EngineProtocol.Done;
            }
            catch (OperationCanceledException)
            {
                (status, text) = (EngineProtocol.Cancelled, "");
            }
            catch (Exception e)
            {
                (status, text) = (EngineProtocol.Failed, e.ToString());
            }
            finally
            {
                foreach (var image in request.Images) image.Dispose();
                if (cancellations.TryRemove(item.Id, out var done)) done.Dispose();
            }
            Send(w => { w.Write(EngineProtocol.Result); w.Write(item.Id); w.Write(status); w.Write(text); });
        }
        model?.Dispose();
        return 0;
    }
}
