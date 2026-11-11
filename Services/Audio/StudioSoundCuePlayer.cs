using System.IO;
using System.Media;
using SmartMacro.Core.Models;

namespace SmartMacro.Services.Audio;

/// <summary>
/// Pure in-memory PCM WAV sound cue generator and player for <see cref="SoundCueKind"/>.
/// Generates pleasant, non-intrusive studio chimes without shipping external audio files.
/// </summary>
public static class StudioSoundCuePlayer
{
    public static void Play(SoundCueKind kind)
    {
        if (kind == SoundCueKind.None) return;

        try
        {
            var wavBytes = BuildWaveBytes(kind);
            if (wavBytes.Length == 0) return;

            _ = Task.Run(() =>
            {
                try
                {
                    using var stream = new MemoryStream(wavBytes, writable: false);
                    using var player = new SoundPlayer(stream);
                    player.PlaySync();
                }
                catch
                {
                    // Never interrupt playback if audio hardware is unavailable.
                }
            });
        }
        catch
        {
            // Ignore audio synthesis errors.
        }
    }

    public static byte[] BuildWaveBytes(SoundCueKind kind) => kind switch
    {
        SoundCueKind.SuccessChime => SynthesizeToneSequence(new[] { (587.33, 90, 0.25), (880.0, 160, 0.25) }),
        SoundCueKind.SoftBell => SynthesizeToneSequence(new[] { (659.25, 210, 0.22) }),
        SoundCueKind.DoubleBeep => SynthesizeToneSequence(new[] { (784.0, 75, 0.25), (0.0, 45, 0.0), (784.0, 95, 0.25) }),
        _ => Array.Empty<byte>(),
    };

    private static byte[] SynthesizeToneSequence((double FrequencyHz, int DurationMs, double Volume)[] segments)
    {
        const int sampleRate = 44100;
        var totalSamples = 0;
        foreach (var seg in segments)
        {
            totalSamples += Math.Max(1, (sampleRate * seg.DurationMs) / 1000);
        }

        var dataBytes = totalSamples * 2;
        using var ms = new MemoryStream(44 + dataBytes);
        using var bw = new BinaryWriter(ms);

        // RIFF WAVE header (16-bit mono PCM)
        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)1); // Mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write("data"u8);
        bw.Write(dataBytes);

        foreach (var (freq, durationMs, volume) in segments)
        {
            var count = Math.Max(1, (sampleRate * durationMs) / 1000);
            var attackSamples = Math.Max(1, sampleRate / 120); // ~8ms fade-in
            for (var i = 0; i < count; i++)
            {
                if (freq <= 0.0 || volume <= 0.0)
                {
                    bw.Write((short)0);
                    continue;
                }

                var t = (double)i / sampleRate;
                var attack = Math.Min(1.0, (double)i / attackSamples);
                var decay = Math.Exp(-3.5 * i / count);
                var sample = Math.Sin(2.0 * Math.PI * freq * t) * volume * attack * decay;
                var pcm = (short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue);
                bw.Write(pcm);
            }
        }

        bw.Flush();
        return ms.ToArray();
    }
}
