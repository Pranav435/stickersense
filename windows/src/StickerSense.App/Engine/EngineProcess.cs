using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using StickerSense.Core;

namespace StickerSense.App.Engine;

/// <summary>The engine process stopped (crashed or was killed) while StickerSense was waiting on it.</summary>
internal sealed class EngineStoppedException(string message) : Exception(message);

/// <summary>A request the model itself failed (the engine is still running).</summary>
internal sealed class EngineFailedException(string message, bool onGraphicsCard) : Exception(message)
{
    public bool OnGraphicsCard { get; } = onGraphicsCard;
}

/// <summary>
/// StickerSense's side of the engine process (<see cref="EngineHost"/>): starts it, loads the
/// model in it, and passes requests to it. Not thread-safe for concurrent requests: the
/// describer's gate runs one at a time.
/// </summary>
internal sealed class EngineProcess : IDisposable
{
    private readonly Process process;
    private readonly BinaryWriter writer;
    private readonly object writeGate = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<(byte Status, string Text)>> pending = new();
    private readonly Action<string> log;
    private TaskCompletionSource<(bool Ok, string RunningOn, bool UsesGraphicsCard, string Error)>? loading;
    private int nextId;
    private volatile bool stopped;

    private EngineProcess(Process process, Action<string> log)
    {
        this.process = process;
        this.log = log;
        writer = new BinaryWriter(process.StandardInput.BaseStream);
        var reader = new Thread(() => ReadLoop(new BinaryReader(process.StandardOutput.BaseStream))) { IsBackground = true, Name = "engine replies" };
        reader.Start();
    }

    public int ProcessId => process.Id;

    public string RunningOn { get; private set; } = "";

    public bool UsesGraphicsCard { get; private set; }

    /// <summary>The engine's memory in use, in megabytes (0 once it has stopped).</summary>
    public long WorkingSetMegabytes
    {
        get
        {
            try
            {
                process.Refresh();
                return process.HasExited ? 0 : process.WorkingSet64 / (1024 * 1024);
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }
    }

    /// <summary>Starts an engine and loads the model in it (seconds; the engine warms the model up).</summary>
    public static async Task<EngineProcess> StartAsync(string directory, bool tryGraphicsCard, string warmUpInstructions, Action<string> log)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("StickerSense can't find its own program file.");
        var start = new ProcessStartInfo(exe, "--engine")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        var process = Process.Start(start) ?? throw new InvalidOperationException("The description engine didn't start.");
        var engine = new EngineProcess(process, log);
        try
        {
            await engine.LoadAsync(directory, tryGraphicsCard, warmUpInstructions).ConfigureAwait(false);
            return engine;
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    private async Task LoadAsync(string directory, bool tryGraphicsCard, string warmUpInstructions)
    {
        var done = loading = new TaskCompletionSource<(bool, string, bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Send(w =>
        {
            w.Write(EngineProtocol.Load);
            w.Write(directory);
            w.Write(tryGraphicsCard);
            w.Write(warmUpInstructions);
        });
        var (ok, runningOn, usesGraphicsCard, error) = await done.Task.ConfigureAwait(false);
        if (!ok) throw new InvalidOperationException(FirstLine(error));
        RunningOn = runningOn;
        UsesGraphicsCard = usesGraphicsCard;
    }

    /// <summary>The model's reply to one request. Cancelling asks the engine to stop at its next token.</summary>
    public async Task<string> GenerateAsync(VisionRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref nextId);
        var reply = new TaskCompletionSource<(byte, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = reply;
        if (stopped) Fail(reply);
        try
        {
            Send(w => EngineProtocol.WriteRequest(w, id, request));
        }
        catch (IOException)
        {
            Fail(reply);
        }
        using (ct.Register(() => TrySend(w => { w.Write(EngineProtocol.Cancel); w.Write(id); })))
        {
            var (status, text) = await reply.Task.ConfigureAwait(false);
            return status switch
            {
                EngineProtocol.Done => text,
                EngineProtocol.Cancelled => throw new OperationCanceledException(ct),
                _ => throw new EngineFailedException(FirstLine(text), UsesGraphicsCard),
            };
        }
    }

    private void ReadLoop(BinaryReader reader)
    {
        try
        {
            while (true)
            {
                switch (reader.ReadByte())
                {
                    case EngineProtocol.Log:
                        log(reader.ReadString());
                        break;
                    case EngineProtocol.Loaded:
                        var loadedReply = (reader.ReadBoolean(), reader.ReadString(), reader.ReadBoolean(), reader.ReadString());
                        loading?.TrySetResult(loadedReply);
                        break;
                    case EngineProtocol.Result:
                        var id = reader.ReadInt32();
                        var status = reader.ReadByte();
                        var text = reader.ReadString();
                        if (pending.TryRemove(id, out var reply)) reply.TrySetResult((status, text));
                        break;
                    default:
                        throw new InvalidDataException("Unexpected message from the description engine.");
                }
            }
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or ObjectDisposedException or InvalidDataException)
        {
        }
        // The engine has gone: fail whatever was waiting on it.
        stopped = true;
        var exit = "";
        try
        {
            if (process.WaitForExit(2000)) exit = $" (exit code 0x{process.ExitCode:X8})";
        }
        catch (InvalidOperationException)
        {
        }
        var message = $"The description engine stopped{exit}.";
        loading?.TrySetException(new EngineStoppedException(message));
        foreach (var reply in pending.Values) reply.TrySetException(new EngineStoppedException(message));
        pending.Clear();
        Stopped?.Invoke(message);
    }

    public bool HasStopped => stopped;

    /// <summary>Raised once, on a background thread, when the engine process has gone (crashed, killed or closed).</summary>
    public event Action<string>? Stopped;

    private void Fail(TaskCompletionSource<(byte, string)> reply) =>
        reply.TrySetException(new EngineStoppedException("The description engine stopped."));

    private void Send(Action<BinaryWriter> write)
    {
        lock (writeGate)
        {
            write(writer);
            writer.Flush();
        }
    }

    private void TrySend(Action<BinaryWriter> write)
    {
        try
        {
            Send(write);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length == 0 ? "unknown error" : line;
    }

    /// <summary>Closes the engine: its input ends, so it exits; it's killed if it hasn't within a few seconds.</summary>
    public void Dispose()
    {
        try
        {
            lock (writeGate) writer.Dispose();
        }
        catch (IOException)
        {
        }
        _ = Task.Run(() =>
        {
            try
            {
                if (!process.WaitForExit(5000)) process.Kill();
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        });
    }
}
