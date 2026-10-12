using System.IO;
using System.Media;
using StickerSense.Core;

namespace StickerSense.App.Speech;

/// <summary>
/// Plays the synthesized earcons (see <see cref="ToneSynth"/>): quiet, short, no files to ship.
/// The volume is baked into the samples. A new sound replaces one still playing.
/// </summary>
internal sealed class EarconPlayer : IEarcons
{
    private readonly object gate = new();
    private readonly Dictionary<Earcon, byte[]> cache = [];
    private double volume = 0.35;
    private SoundPlayer? playing; // kept alive while it plays

    public bool IsEnabled { get; set; } = true;

    public double Volume
    {
        get => volume;
        set
        {
            lock (gate)
            {
                volume = Math.Clamp(value, 0.05, 1);
                cache.Clear();
            }
        }
    }

    public void Play(Earcon sound)
    {
        if (!IsEnabled) return;
        lock (gate)
        {
            if (!cache.TryGetValue(sound, out var wav))
            {
                wav = ToneSynth.Wav(ToneSynth.Notes(sound), volume);
                cache[sound] = wav;
            }
            try
            {
                var player = new SoundPlayer(new MemoryStream(wav, writable: false));
                player.Load();
                player.Play();
                playing?.Dispose();
                playing = player;
            }
            catch (Exception e) when (e is InvalidOperationException or TimeoutException or IOException)
            {
                // No audio device: earcons are optional.
            }
        }
    }
}
