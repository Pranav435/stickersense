namespace StickerSense.Core;

/// <summary>
/// Short, quiet earcons so the user knows StickerSense heard the shortcut and is working,
/// without adding more speech. Generated in code so there are no audio files to ship.
/// </summary>
public static class ToneSynth
{
    public static IReadOnlyList<(double Frequency, double Seconds)> Notes(Earcon sound) => sound switch
    {
        Earcon.Working => [(660, 0.05), (880, 0.06)],
        Earcon.Ready => [(988, 0.05), (1319, 0.09)],
        Earcon.Failed => [(330, 0.09), (0, 0.04), (262, 0.12)],
        _ => [(1760, 0.025)],
    };

    /// <summary>Sine notes with short fades as a 16-bit mono WAV. A frequency of 0 is silence.</summary>
    /// <param name="volume">0…1; scales the (already quiet) amplitude.</param>
    public static byte[] Wav(IReadOnlyList<(double Frequency, double Seconds)> notes, double volume = 1.0, int sampleRate = 44_100)
    {
        var amplitude = 0.6 * Math.Clamp(volume, 0, 1);
        var samples = new List<short>();
        foreach (var (frequency, seconds) in notes)
        {
            var count = (int)(seconds * sampleRate);
            var fade = Math.Min((int)(0.008 * sampleRate), count / 2);
            for (var i = 0; i < count; i++)
            {
                if (frequency <= 0)
                {
                    samples.Add(0);
                    continue;
                }
                var envelope = 1.0;
                if (i < fade) envelope = i / (double)fade;
                if (i > count - fade) envelope = (count - i) / (double)fade;
                var value = Math.Sin(2 * Math.PI * frequency * i / sampleRate) * envelope * amplitude;
                samples.Add((short)(value * short.MaxValue));
            }
        }

        var byteCount = samples.Count * 2;
        using var stream = new MemoryStream(44 + byteCount);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + byteCount);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);                 // fmt chunk size
        writer.Write((short)1);           // PCM
        writer.Write((short)1);           // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);     // byte rate
        writer.Write((short)2);           // block align
        writer.Write((short)16);          // bits per sample
        writer.Write("data"u8);
        writer.Write(byteCount);
        foreach (var sample in samples) writer.Write(sample);
        writer.Flush();
        return stream.ToArray();
    }
}
