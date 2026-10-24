using System.Runtime.InteropServices;
using SeaRaider.Core.Abstractions;
using SeaRaider.Core.Models;

namespace SeaRaider.App.Services;

/// <summary>
/// A faint, slow background sound while SeaRaider is in front, like a console's home screen (Settings › General › Sounds ›
/// Background ambience, with its own volume).
///
/// <para><b>Made here, nothing shipped</b>: a 48-second loop is synthesized at start-up: a soft, slowly swelling pad of a
/// few detuned notes, a low drone and a few far-off glassy chimes. Every tone fits a whole number of times into the loop,
/// so it repeats without a seam. Hallow's Eve gets its own darker loop: a minor chord, a little wind and music-box notes.</para>
///
/// <para><b>Never in the way</b>: it fades out (and stops) while a video plays in SeaRaider's player, while SeaRaider isn't
/// the window in front (a game, a browser, minimized), and fades back in after. Volume changes and fades are smooth.</para>
/// </summary>
public sealed class AmbienceService : IDisposable
{
    private const int SampleRate = 32000;
    private const double LoopSeconds = 48;

    private readonly ILibraryRepository _repository;
    private readonly SkinService _skins;
    private readonly object _lock = new();
    private readonly Timer _fader;
    private AppSettings _settings;

    private short[]? _samples;
    private Thread? _player;
    private volatile bool _stopping;
    private AppSkin? _loopSkin;
    private bool _started, _windowActive = true, _playerOpen;
    private double _level, _target;

    public AmbienceService(ILibraryRepository repository, SkinService skins, TrailerPlayerService player)
    {
        _repository = repository;
        _skins = skins;
        _settings = repository.GetSettings();
        _fader = new Timer(_ => Fade(), null, Timeout.Infinite, Timeout.Infinite);
        repository.SettingsChanged += (_, settings) =>
        {
            _settings = settings;
            Retarget();
        };
        skins.Changed += (_, _) => _ = Task.Run(EnsureLoop);
        player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(TrailerPlayerService.IsOpen)) return;
            _playerOpen = player.IsOpen;
            Retarget();
        };
    }

    /// <summary>Starts it (a moment after the window opens: it fades in under the app's own fade).</summary>
    public void Start()
    {
        if (_started || !OperatingSystem.IsWindows()) return;
        _started = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(1200).ConfigureAwait(false);
            EnsureLoop();
            Retarget();
        });
    }

    /// <summary>SeaRaider's window came to the front, or went behind something / was minimized.</summary>
    public void SetWindowActive(bool active)
    {
        _windowActive = active;
        Retarget();
    }

    private double WantedLevel()
    {
        var s = _settings;
        if (!_started || !s.AmbienceEnabled || !_windowActive || _playerOpen) return 0;
        return Math.Pow(Math.Clamp(s.AmbienceVolume, 0, 100) / 100.0, 1.7);
    }

    private void Retarget()
    {
        lock (_lock) _target = WantedLevel();
        _fader.Change(0, 40);
    }

    /// <summary>Moves the volume a step towards where it should be (about 2.5 s for a full fade), pausing at silence.</summary>
    private void Fade()
    {
        lock (_lock)
        {
            const double step = 0.016;
            if (Math.Abs(_level - _target) <= step) _level = _target;
            else _level += Math.Sign(_target - _level) * step;
            if (_level == _target) _fader.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Plays the loop, scaling its samples by the fade level itself. (Never waveOutSetVolume: on Windows that sets SeaRaider's own
    /// volume in the volume mixer, so fading the music out under the player turned the whole app — the video's sound too — to 0.)
    /// Releases the sound device while silent.
    /// </summary>
    private void PlayLoop()
    {
        PcmStreamOut? output = null;
        var position = 0;
        try
        {
            while (!_stopping)
            {
                double level;
                short[]? samples;
                lock (_lock)
                {
                    level = _level;
                    samples = _samples;
                }
                if (level <= 0 || samples is null)
                {
                    output?.Dispose();
                    output = null;
                    Thread.Sleep(60);
                    continue;
                }
                output ??= PcmStreamOut.Open(SampleRate);
                if (output is null)
                {
                    Thread.Sleep(1000); // no sound device right now
                    continue;
                }
                output.Reclaim();
                if (output.QueuedFrames < SampleRate / 6)
                {
                    var frames = SampleRate / 20; // 50 ms
                    var block = new float[frames * 2];
                    var gain = (float)level / short.MaxValue;
                    for (var i = 0; i < frames; i++)
                    {
                        var v = samples[position] * gain;
                        block[2 * i] = v;
                        block[2 * i + 1] = v;
                        if (++position >= samples.Length) position = 0;
                    }
                    output.Write(block, frames);
                }
                Thread.Sleep(20);
            }
        }
        catch (Exception ex)
        {
            Core.ErrorLog.Write(ex, "ambience playback");
        }
        finally
        {
            output?.Dispose();
        }
    }

    private void EnsureLoop()
    {
        if (!OperatingSystem.IsWindows()) return;
        var skin = _skins.Current == AppSkin.Halloween ? AppSkin.Halloween : AppSkin.NextGeneration;
        if (_loopSkin == skin && _samples is not null) return;
        short[] samples;
        try
        {
            samples = Synthesize(skin == AppSkin.Halloween);
        }
        catch (Exception ex)
        {
            Core.ErrorLog.Write(ex, "ambience");
            return;
        }
        lock (_lock)
        {
            _samples = samples;
            _loopSkin = skin;
            _level = 0; // fades in again
        }
        lock (_lock)
        {
            if (_player is null)
            {
                // Earlier versions faded the music with the app's own mixer volume and could leave SeaRaider at 0 in Windows'
                // volume mixer: put it back to full once (the music's own level is in its samples now).
                PcmStreamOut.ResetAppVolume(SampleRate);
                _player = new Thread(PlayLoop) { IsBackground = true, Name = "Background sound" };
                _player.Start();
            }
        }
        Retarget();
    }

    public void Dispose()
    {
        _fader.Dispose();
        _stopping = true;
        _player?.Join(500);
    }

    // ---- the sound ------------------------------------------------------------------------------------------

    /// <summary>One loop, peak at about half scale (the volume is set on top).</summary>
    internal static short[] Synthesize(bool halloween)
    {
        var n = (int)(SampleRate * LoopSeconds);
        var buffer = new double[n];
        // A frequency that fits a whole number of cycles into the loop: no seam where it repeats.
        static double Fit(double f) => Math.Max(1, Math.Round(f * LoopSeconds)) / LoopSeconds;

        void Pad(double frequency, double level, int swells, double phase)
        {
            var detunes = new[] { -0.125, 0.0, 0.125 };
            foreach (var detune in detunes)
            {
                var f = Fit(frequency + detune);
                var w = 2 * Math.PI * f / SampleRate;
                var p = phase * 3.1 + detune * 40;
                for (var i = 0; i < n; i++)
                {
                    var t = (double)i / n;
                    var swell = 0.55 + 0.45 * Math.Sin(2 * Math.PI * swells * t + phase);
                    var s = Math.Sin(w * i + p) + 0.12 * Math.Sin(2 * w * i + p);
                    buffer[i] += s * swell * level / detunes.Length;
                }
            }
        }

        void Chime(double atSeconds, double frequency, double level, double decaySeconds)
        {
            var start = (int)(atSeconds * SampleRate);
            var length = (int)(decaySeconds * 5 * SampleRate);
            var partials = new[] { (1.0, 1.0), (2.76, 0.25), (5.4, 0.08) };
            for (var k = 0; k < length; k++)
            {
                var t = (double)k / SampleRate;
                var envelope = Math.Min(1, t / 0.02) * Math.Exp(-t / decaySeconds);
                var s = 0.0;
                foreach (var (ratio, amount) in partials)
                    s += amount * Math.Sin(2 * Math.PI * frequency * ratio * t) * Math.Exp(-t * (ratio - 1) / (decaySeconds * 1.5));
                buffer[(start + k) % n] += s * envelope * level; // the tail wraps round to the start: still seamless
            }
        }

        if (!halloween)
        {
            // D major 9, airy and open
            Pad(73.42, 0.35, 1, 0.0);    // D2 drone
            Pad(146.83, 0.55, 1, 0.4);   // D3
            Pad(220.00, 0.45, 2, 1.3);   // A3
            Pad(329.63, 0.32, 3, 2.1);   // E4
            Pad(369.99, 0.30, 2, 3.0);   // F#4
            Pad(554.37, 0.14, 3, 4.2);   // C#5
            Chime(5.0, 1174.66, 0.10, 2.4);
            Chime(17.5, 1479.98, 0.08, 2.6);
            Chime(29.0, 1318.51, 0.09, 2.4);
            Chime(40.5, 1760.00, 0.06, 2.8);
        }
        else
        {
            // D minor (add 9) with a low drone, wind and a few music-box notes
            Pad(73.42, 0.40, 1, 0.0);    // D2
            Pad(146.83, 0.50, 1, 0.7);   // D3
            Pad(220.00, 0.38, 2, 1.6);   // A3
            Pad(349.23, 0.30, 3, 2.4);   // F4
            Pad(329.63, 0.20, 2, 3.3);   // E4
            Pad(207.65, 0.07, 3, 5.0);   // G#3: a faint unease underneath
            Chime(7.0, 1174.66, 0.08, 1.6);
            Chime(8.2, 1396.91, 0.06, 1.6);
            Chime(23.0, 1046.50, 0.07, 1.8);
            Chime(35.5, 932.33, 0.06, 2.0);

            // Wind: soft filtered noise, swelling twice per loop (filtered round the loop, so it joins up too).
            var random = new Random(31);
            var noise = new double[n];
            for (var i = 0; i < n; i++) noise[i] = random.NextDouble() * 2 - 1;
            double y1 = 0, y2 = 0;
            for (var pass = 0; pass < 2; pass++)
                for (var i = 0; i < n; i++)
                {
                    y1 += 0.02 * (noise[i] - y1);
                    y2 += 0.02 * (y1 - y2);
                    if (pass == 1)
                    {
                        var swell = Math.Pow(Math.Sin(Math.PI * 2 * i / n), 2);
                        buffer[i] += y2 * 1.6 * (0.25 + 0.75 * swell);
                    }
                }
        }

        var peak = Math.Max(1e-9, buffer.Max(x => Math.Abs(x)));
        var samples = new short[n];
        for (var i = 0; i < n; i++) samples[i] = (short)(buffer[i] / peak * 0.5 * short.MaxValue);
        return samples;
    }
}
