using System.Runtime.InteropServices;

namespace Afterbreak;

internal static class BreakStartSound
{
    private const uint Async = 0x0001;
    private const uint NoDefault = 0x0002;
    private const uint Memory = 0x0004;
    private const uint NoStop = 0x0010;

    // Keep one small pinned buffer alive for the process. PlaySound reads it after returning.
    private static readonly Lazy<GCHandle> Wave = new(() => GCHandle.Alloc(CreateWave(), GCHandleType.Pinned));

    internal static void Play()
    {
        try
        {
            // A busy or unavailable audio device is silent and has no effect on the break.
            PlaySound(Wave.Value.AddrOfPinnedObject(), 0, Async | NoDefault | Memory | NoStop);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // The cue is optional on Windows installations without the waveform audio API.
        }
    }

    private static byte[] CreateWave()
    {
        const int sampleRate = 24000;
        const int frames = sampleRate * 38 / 100;
        const int bytesPerFrame = sizeof(short);
        const int dataLength = frames * bytesPerFrame;
        using var wave = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(wave);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * bytesPerFrame);
        writer.Write((short)bytesPerFrame);
        writer.Write((short)(8 * bytesPerFrame));
        writer.Write("data"u8);
        writer.Write(dataLength);
        for (var frame = 0; frame < frames; frame++)
        {
            var time = (double)frame / sampleRate;
            var sample = Tone(time, 523.25, 0.28) + 0.65 * Tone(time - 0.10, 659.25, 0.28);
            writer.Write((short)(short.MaxValue * 0.09 * sample));
        }
        return wave.ToArray();
    }

    private static double Tone(double time, double frequency, double length)
    {
        if (time < 0 || time >= length)
        {
            return 0;
        }
        var attack = Math.Min(1, time / 0.022);
        var decay = 0.5 + 0.5 * Math.Cos(Math.PI * time / length);
        return attack * decay * Math.Sin(2 * Math.PI * frequency * time);
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(nint sound, nint module, uint flags);
}
