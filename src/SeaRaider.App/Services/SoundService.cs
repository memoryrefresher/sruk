using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;

namespace SeaRaider.App.Services;

/// <summary>The sounds SeaRaider makes.</summary>
public enum UiSound
{
    /// <summary>Any button: a soft, short tick.</summary>
    Click,

    /// <summary>Check boxes, switches, the wishlist heart: a little "pop".</summary>
    Toggle,

    /// <summary>A coming-soon page or dialog opens: a quick rising pair.</summary>
    Open,

    /// <summary>A download finished: a two-note bell.</summary>
    DownloadComplete,

    /// <summary>A game finished installing: a three-note marimba run.</summary>
    InstallComplete,

    /// <summary>Someone sent a friend request.</summary>
    FriendRequest,

    /// <summary>A chat message arrived.</summary>
    Message,

    /// <summary>A wishlisted game is in the store: a small sparkle.</summary>
    Wishlist,

    /// <summary>A download or install failed: two low, soft notes going down.</summary>
    Error,
}

/// <summary>
/// Interface sounds, synthesized at start-up (nothing to ship) and played through the Windows wave mapper, several at once
/// if need be. Settings → General → Sounds turns them on or off by kind and sets the volume.
///
/// <para><b>Not annoying</b>: everything is short (clicks 20 ms, chimes under a second) and soft (bell and marimba tones
/// with fast attacks and smooth tails, no harsh square waves); clicks sit well below the chimes; the same sound is never
/// repeated within a moment (clicks 40 ms, others 1.5 s); clicks give way to a chime that just started; at most four sounds
/// play at a time.</para>
/// </summary>
public sealed class SoundService
{
    private const int SampleRate = 44100;
    private const int MaxVoices = 4;

    private readonly ILibraryRepository _repository;
    private readonly ConcurrentDictionary<UiSound, float[]> _waves = new();
    private readonly ConcurrentDictionary<UiSound, DateTime> _lastPlayed = new();
    private readonly ConcurrentDictionary<(UiSound Sound, int Volume), short[]> _pcm = new();
    private DateTime _lastChime = DateTime.MinValue;
    private AppSettings _settings;
    private int _voices;

    public SoundService(ILibraryRepository repository)
    {
        _repository = repository;
        _settings = repository.GetSettings();
        repository.SettingsChanged += (_, settings) => _settings = settings;

        // Build every sound in the background now, so the first click never waits for synthesis.
        _ = Task.Run(() =>
        {
            foreach (var sound in Enum.GetValues<UiSound>()) _waves.GetOrAdd(sound, Synthesize);
        });
    }

    /// <summary>Plays <paramref name="sound"/> if its kind is switched on (and it isn't a repeat).</summary>
    public void Play(UiSound sound)
    {
        var settings = _settings;
        if (!settings.SoundsEnabled || !IsOn(settings, sound)) return;

        var now = DateTime.UtcNow;
        var isClick = sound is UiSound.Click or UiSound.Toggle;
        if (isClick && now - _lastChime < TimeSpan.FromMilliseconds(350)) return; // let the chime ring
        var minGap = isClick ? TimeSpan.FromMilliseconds(40) : TimeSpan.FromMilliseconds(1500);
        if (_lastPlayed.TryGetValue(sound, out var last) && now - last < minGap) return;
        _lastPlayed[sound] = now;
        if (!isClick && sound != UiSound.Open) _lastChime = now;

        Start(sound, settings.SoundVolume);
    }

    /// <summary>Plays a sound whatever the settings say, at <paramref name="volume"/> (the Settings "Play a sample").</summary>
    public void Preview(UiSound sound, int volume) => Start(sound, volume);

    private static bool IsOn(AppSettings s, UiSound sound) => sound switch
    {
        UiSound.Click or UiSound.Toggle or UiSound.Open => s.SoundClicks,
        UiSound.DownloadComplete or UiSound.InstallComplete => s.SoundDownloads,
        UiSound.FriendRequest or UiSound.Message => s.SoundFriends,
        _ => s.SoundAlerts,
    };

    private void Start(UiSound sound, int volume)
    {
        if (!OperatingSystem.IsWindows() || volume <= 0) return;
        if (Interlocked.Increment(ref _voices) > MaxVoices)
        {
            Interlocked.Decrement(ref _voices);
            return;
        }

        // Volume feels linear on a curve; even 100 % stays polite, and clicks sit well under the chimes.
        var gain = (float)(Math.Pow(Math.Clamp(volume, 0, 100) / 100.0, 1.6) * 0.6 * KindGain(sound));
        // Ready-to-play samples per sound and volume (a click is converted once, not on every click).
        var pcm = _pcm.GetOrAdd((sound, volume), _ =>
        {
            var wave = _waves.GetOrAdd(sound, Synthesize);
            var samples = new short[wave.Length];
            for (var i = 0; i < wave.Length; i++) samples[i] = (short)(Math.Clamp(wave[i] * gain, -1f, 1f) * short.MaxValue);
            return samples;
        });

        _ = Task.Run(async () =>
        {
            try { await WaveOut.PlayAsync(pcm, SampleRate).ConfigureAwait(false); }
            catch (Exception) { /* no audio device: stay quiet */ }
            finally { Interlocked.Decrement(ref _voices); }
        });
    }

    private static double KindGain(UiSound sound) => sound switch
    {
        UiSound.Click => 0.32,
        UiSound.Toggle => 0.38,
        UiSound.Open => 0.34,
        UiSound.Error => 0.55,
        _ => 0.8,
    };

    // ---- synthesis ------------------------------------------------------------------------------------

    /// <summary>Unit-level samples (peak ≈ 1) for a sound.</summary>
    internal static float[] Synthesize(UiSound sound)
    {
        float[] buffer;
        switch (sound)
        {
            case UiSound.Click:
                buffer = new float[Ms(28)];
                Tone(buffer, 0, 2400, 22, decay: 4, partials: [(1, 1.0), (1.5, 0.25)]);
                Tone(buffer, 0, 1200, 22, decay: 6, amplitude: 0.45);
                break;
            case UiSound.Toggle:
                buffer = new float[Ms(110)];
                Sweep(buffer, 0, 700, 1150, 90, decay: 30);
                break;
            case UiSound.Open:
                buffer = new float[Ms(200)];
                Tone(buffer, 0, 784, 120, decay: 45, partials: Soft);
                Tone(buffer, Ms(55), 1175, 140, decay: 55, partials: Soft, amplitude: 0.8);
                break;
            case UiSound.DownloadComplete:
                buffer = new float[Ms(950)];
                Tone(buffer, 0, 1318.5, 700, decay: 260, partials: Bell);
                Tone(buffer, Ms(120), 1975.5, 800, decay: 320, partials: Bell, amplitude: 0.75);
                break;
            case UiSound.InstallComplete:
                buffer = new float[Ms(900)];
                Tone(buffer, 0, 1046.5, 500, decay: 200, partials: Marimba);
                Tone(buffer, Ms(95), 1318.5, 500, decay: 200, partials: Marimba, amplitude: 0.85);
                Tone(buffer, Ms(190), 1568.0, 650, decay: 280, partials: Marimba, amplitude: 0.8);
                break;
            case UiSound.FriendRequest:
                buffer = new float[Ms(560)];
                Tone(buffer, 0, 880, 300, decay: 140, partials: Soft);
                Tone(buffer, Ms(150), 1174.7, 380, decay: 170, partials: Soft, amplitude: 0.9);
                break;
            case UiSound.Message:
                buffer = new float[Ms(420)];
                Tone(buffer, 0, 1568, 380, decay: 150, partials: Glass);
                break;
            case UiSound.Wishlist:
                buffer = new float[Ms(900)];
                double[] notes = [783.99, 1046.5, 1318.5, 1568.0];
                for (var n = 0; n < notes.Length; n++)
                    Tone(buffer, Ms(70 * n), notes[n], 560, decay: 240, partials: Bell, amplitude: 0.55 + 0.1 * n);
                break;
            default: // Error
                buffer = new float[Ms(480)];
                Tone(buffer, 0, 523.25, 220, decay: 110, partials: Soft);
                Tone(buffer, Ms(140), 392.0, 300, decay: 150, partials: Soft, amplitude: 0.9);
                break;
        }
        Normalize(buffer);
        return buffer;
    }

    private static readonly (double Ratio, double Level)[] Soft = [(1, 1.0), (2, 0.18)];
    private static readonly (double Ratio, double Level)[] Bell = [(1, 1.0), (2.01, 0.32), (3.0, 0.12), (4.2, 0.05)];
    private static readonly (double Ratio, double Level)[] Marimba = [(1, 1.0), (4.0, 0.22), (9.9, 0.04)];
    private static readonly (double Ratio, double Level)[] Glass = [(1, 1.0), (1.5, 0.3), (2.76, 0.12)];

    private static int Ms(double milliseconds) => (int)(SampleRate * milliseconds / 1000);

    /// <summary>A note with a 4 ms attack and an exponential tail (<paramref name="decay"/> = time constant in ms).</summary>
    private static void Tone(float[] buffer, int start, double frequency, double lengthMs, double decay,
        (double Ratio, double Level)[]? partials = null, double amplitude = 1.0)
    {
        partials ??= [(1, 1.0)];
        var length = Math.Min(Ms(lengthMs), buffer.Length - start);
        var attack = Ms(4);
        for (var i = 0; i < length; i++)
        {
            var t = (double)i / SampleRate;
            var envelope = Math.Min(1.0, (double)i / attack) * Math.Exp(-t * 1000 / decay) * FadeOut(i, length);
            var sample = 0.0;
            foreach (var (ratio, level) in partials)
                sample += level * Math.Sin(2 * Math.PI * frequency * ratio * t) * Math.Exp(-t * 1000 * (ratio - 1) / (decay * 2)); // highs fade first
            buffer[start + i] += (float)(sample * envelope * amplitude);
        }
    }

    /// <summary>A gliding note (the toggle's "pop").</summary>
    private static void Sweep(float[] buffer, int start, double from, double to, double lengthMs, double decay)
    {
        var length = Math.Min(Ms(lengthMs), buffer.Length - start);
        var phase = 0.0;
        for (var i = 0; i < length; i++)
        {
            var progress = (double)i / length;
            var frequency = from + (to - from) * (1 - Math.Pow(1 - progress, 3));
            phase += 2 * Math.PI * frequency / SampleRate;
            var envelope = Math.Min(1.0, i / (double)Ms(3)) * Math.Exp(-(double)i / SampleRate * 1000 / decay) * FadeOut(i, length);
            buffer[start + i] += (float)(Math.Sin(phase) * envelope);
        }
    }

    /// <summary>The last 8 ms ramp to silence, so nothing ends with a click.</summary>
    private static double FadeOut(int i, int length)
    {
        var tail = Ms(8);
        return i > length - tail ? Math.Max(0, (length - i) / (double)tail) : 1.0;
    }

    private static void Normalize(float[] buffer)
    {
        var peak = buffer.Length == 0 ? 0f : buffer.Max(x => Math.Abs(x));
        if (peak <= 0) return;
        for (var i = 0; i < buffer.Length; i++) buffer[i] /= peak;
    }

    // ---- playback (winmm wave out: no package, plays alongside other sounds) ------------------------------

    private static class WaveOut
    {
        private const uint WaveMapper = 0xFFFFFFFF;
        private const uint WhdrDone = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormatEx
        {
            public ushort FormatTag, Channels;
            public uint SamplesPerSec, AvgBytesPerSec;
            public ushort BlockAlign, BitsPerSample, Size;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHeader
        {
            public IntPtr Data;
            public uint BufferLength, BytesRecorded;
            public IntPtr User;
            public uint Flags, Loops;
            public IntPtr Next, Reserved;
        }

        [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr handle, uint device, ref WaveFormatEx format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr handle);
        [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr handle);

        public static async Task PlayAsync(short[] samples, int sampleRate)
        {
            var format = new WaveFormatEx
            {
                FormatTag = 1, Channels = 1, SamplesPerSec = (uint)sampleRate, BitsPerSample = 16,
                BlockAlign = 2, AvgBytesPerSec = (uint)sampleRate * 2,
            };
            if (waveOutOpen(out var handle, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0) return;

            var bytes = samples.Length * 2;
            var data = Marshal.AllocHGlobal(bytes);
            var headerSize = (uint)Marshal.SizeOf<WaveHeader>();
            var header = Marshal.AllocHGlobal((int)headerSize);
            try
            {
                Marshal.Copy(samples, 0, data, samples.Length);
                Marshal.StructureToPtr(new WaveHeader { Data = data, BufferLength = (uint)bytes }, header, false);
                if (waveOutPrepareHeader(handle, header, headerSize) != 0) return;
                waveOutWrite(handle, header, headerSize);

                await Task.Delay(samples.Length * 1000 / sampleRate + 20).ConfigureAwait(false);
                for (var i = 0; i < 50 && (Marshal.PtrToStructure<WaveHeader>(header).Flags & WhdrDone) == 0; i++)
                    await Task.Delay(20).ConfigureAwait(false);

                waveOutReset(handle);
                waveOutUnprepareHeader(handle, header, headerSize);
            }
            finally
            {
                waveOutClose(handle);
                Marshal.FreeHGlobal(header);
                Marshal.FreeHGlobal(data);
            }
        }
    }
}
