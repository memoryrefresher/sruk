using System.Runtime.InteropServices;
using SeaRaider.Core;

namespace SeaRaider.App.Services;

/// <summary>
/// The player's sound, played by SeaRaider.exe itself so that a Discord Go Live stream of the SeaRaider window includes it.
///
/// <para>Discord streams the sound of the program whose window is shared. The video plays in WebView2, whose sound comes out
/// of separate msedgewebview2.exe processes, so Discord heard nothing. Instead, <see cref="Script"/> (in the player page and
/// every site's player frame inside it) takes each video's sound before it reaches the speakers, and the player page writes it
/// into memory shared with SeaRaider (a ring of samples). This class reads that ring and plays it through winmm from
/// SeaRaider's own process.</para>
///
/// <para>Safe by design: the page only silences its own output while SeaRaider is reading (a heartbeat written here every few
/// milliseconds). If this stops, or anything in the page fails, the video's sound plays normally from the browser again.</para>
/// </summary>
internal sealed class PlayerSoundPipe : IDisposable
{
    /// <summary>[0] frames written so far (by the page), [1] sample rate (page), [2] heartbeat: unix seconds (here).</summary>
    public const int HeaderBytes = 16;

    /// <summary>One second at 48 kHz, stereo float samples.</summary>
    public const int CapacityFrames = 48000;

    public static ulong Size => (ulong)(HeaderBytes + CapacityFrames * 2 * sizeof(float));

    private readonly IntPtr _memory;
    private readonly Func<bool> _audible;
    private readonly Thread _thread;
    private volatile bool _stop;

    public PlayerSoundPipe(IntPtr memory, Func<bool> audible)
    {
        _memory = memory;
        _audible = audible;
        _thread = new Thread(Run) { IsBackground = true, Name = "Player sound for Discord", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        PcmStreamOut? output = null;
        var chunk = new float[CapacityFrames * 2];
        var started = false;
        uint read = 0;
        try
        {
            while (!_stop)
            {
                Marshal.WriteInt32(_memory, 8, (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                var written = (uint)Marshal.ReadInt32(_memory, 0);
                var rate = Marshal.ReadInt32(_memory, 4);
                if (!started)
                {
                    read = written;
                    started = true;
                }
                var available = written - read;
                if (available > CapacityFrames / 2) available = 0; // far behind, or the page started over: catch up
                if (available > 0 && rate is >= 8000 and <= 192000 && _audible())
                {
                    if (output?.SampleRate != rate)
                    {
                        output?.Dispose();
                        output = PcmStreamOut.Open(rate);
                    }
                    var frames = (int)available;
                    var start = (int)(read % CapacityFrames);
                    var first = Math.Min(frames, CapacityFrames - start);
                    Marshal.Copy(_memory + HeaderBytes + start * 8, chunk, 0, first * 2);
                    if (first < frames) Marshal.Copy(_memory + HeaderBytes, chunk, first * 2, (frames - first) * 2);
                    output?.Write(chunk, frames);
                }
                read = written;
                output?.Reclaim();
                Thread.Sleep(4);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "player sound for Discord");
        }
        finally
        {
            output?.Dispose();
        }
    }

    /// <summary>Stops reading (the page's own sound comes back within a couple of seconds). Call before freeing the memory.</summary>
    public void Dispose()
    {
        _stop = true;
        _thread.Join(1500);
    }

    /// <summary>
    /// Runs in the player page and in every frame inside it, only when <c>window.__srAppAudio</c> is set (prepended by the host).
    /// Each frame sends its videos' sound to the player page; the player page writes it into the shared ring.
    /// </summary>
    public const string Script = """
        (() => {
          if (!window.__srAppAudio || window.__srTap) return;
          window.__srTap = true;
          const isTop = window === window.top;

          if (isTop) {
            let hdr = null, data = null, cap = 0, w = 0, owner = null, ownerAt = 0;
            const states = new Map();
            try {
              window.chrome.webview.addEventListener('sharedbufferreceived', e => {
                try {
                  const b = e.getBuffer();
                  hdr = new Int32Array(b, 0, 4);
                  data = new Float32Array(b, 16);
                  cap = (data.length / 2) | 0;
                  w = hdr[0] >>> 0;
                } catch (x) { hdr = null; }
              });
            } catch (x) {}
            const alive = () => hdr !== null && cap > 0 && Math.abs(Date.now() / 1000 - hdr[2]) < 2.5;
            window.__srPcm = (id, rate, buf) => {
              if (!alive()) return false;
              const now = performance.now();
              if (owner !== null && owner !== id && now - ownerAt < 600) return false; // another video has the sound
              owner = id; ownerAt = now;
              hdr[1] = rate | 0;
              const frames = (buf.length / 2) | 0;
              const pos = w % cap;
              const first = Math.min(frames, cap - pos);
              data.set(buf.subarray(0, first * 2), pos * 2);
              if (first < frames) data.set(buf.subarray(first * 2, frames * 2), 0);
              w = (w + frames) >>> 0;
              hdr[0] = w | 0;
              return true;
            };
            window.addEventListener('message', e => {
              const d = e.data;
              if (!d || d.__sr !== 'pcm' || !(d.buf instanceof Float32Array)) return;
              const on = window.__srPcm(d.id, d.rate, d.buf);
              if (states.get(d.id) !== on) {
                states.set(d.id, on);
                try { if (e.source) e.source.postMessage({ __sr: 'pcm-state', on }, '*'); } catch (x) {}
              }
            });
          }

          const id = Math.random().toString(36).slice(2);
          let ctx = null, mix = null, direct = null, on = false;
          const setOn = v => {
            if (v === on || !direct) return;
            on = v;
            try { direct.gain.setTargetAtTime(v ? 0 : 1, ctx.currentTime, 0.015); } catch (x) {}
          };
          const send = buf => {
            if (isTop) { setOn(window.__srPcm ? window.__srPcm(id, ctx.sampleRate, buf) : false); return; }
            try { window.top.postMessage({ __sr: 'pcm', id, rate: ctx.sampleRate, buf }, '*', [buf.buffer]); } catch (x) {}
          };
          if (!isTop) window.addEventListener('message', e => {
            const d = e.data;
            if (d && d.__sr === 'pcm-state' && e.source === window.top) setOn(!!d.on);
          });

          const WORKLET = "class T extends AudioWorkletProcessor{constructor(){super();this.b=new Float32Array(2048);this.n=0;}" +
            "process(i){const c=i[0];if(c&&c.length){const l=c[0],r=c[1]||c[0];for(let k=0;k<l.length;k++){" +
            "this.b[this.n++]=l[k];this.b[this.n++]=r[k];if(this.n===2048){this.port.postMessage(this.b,[this.b.buffer]);" +
            "this.b=new Float32Array(2048);this.n=0;}}}return true;}}registerProcessor('sr-tap',T);";
          const startCapture = async () => {
            const zero = ctx.createGain();
            zero.gain.value = 0;
            zero.connect(ctx.destination);
            try {
              const url = URL.createObjectURL(new Blob([WORKLET], { type: 'application/javascript' }));
              await ctx.audioWorklet.addModule(url);
              const node = new AudioWorkletNode(ctx, 'sr-tap', { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [2] });
              node.port.onmessage = e => send(e.data);
              mix.connect(node);
              node.connect(zero);
            } catch (x) {
              // the site doesn't allow worklets: the older way, on the page's own thread
              const sp = ctx.createScriptProcessor(1024, 2, 2);
              sp.onaudioprocess = e => {
                const ib = e.inputBuffer, l = ib.getChannelData(0), r = ib.numberOfChannels > 1 ? ib.getChannelData(1) : l;
                const b = new Float32Array(l.length * 2);
                for (let k = 0; k < l.length; k++) { b[2 * k] = l[k]; b[2 * k + 1] = r[k]; }
                send(b);
              };
              mix.connect(sp);
              sp.connect(zero);
            }
          };
          const ensure = () => {
            if (ctx) return true;
            try { ctx = new AudioContext({ latencyHint: 'interactive' }); } catch (x) { return false; }
            direct = ctx.createGain();
            direct.gain.value = 1; // the browser keeps playing it until SeaRaider is taking the sound
            direct.connect(ctx.destination);
            mix = ctx.createGain();
            mix.connect(direct);
            startCapture();
            return true;
          };
          const tapped = new WeakSet();
          const tap = el => {
            if (!el || tapped.has(el)) return;
            tapped.add(el);
            if (!ensure()) return;
            try { ctx.createMediaElementSource(el).connect(mix); } catch (x) { return; } // the site already uses its sound
            if (ctx.state !== 'running') ctx.resume().catch(() => {});
          };
          document.addEventListener('play', e => {
            if (e.target instanceof HTMLMediaElement) tap(e.target);
            if (ctx && ctx.state !== 'running') ctx.resume().catch(() => {});
          }, true);
          setInterval(() => document.querySelectorAll('video,audio').forEach(el => { if (!el.paused) tap(el); }), 1500);
        })();
        """;

    /// <summary>Is Discord running on this PC (the regular, PTB or Canary app)?</summary>
    public static bool DiscordIsRunning()
    {
        foreach (var name in new[] { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment" })
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(name);
            foreach (var process in processes) process.Dispose();
            if (processes.Length > 0) return true;
        }
        return false;
    }
}

/// <summary>Stereo 16-bit sound streamed to the default output through winmm, in small queued blocks.</summary>
internal sealed class PcmStreamOut : IDisposable
{
    private const uint WaveMapper = 0xFFFFFFFF;
    private const int WhdrDone = 0x1;

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
    [DllImport("winmm.dll")] private static extern int waveOutSetVolume(IntPtr handle, uint volume);

    /// <summary>Puts SeaRaider's own volume in Windows' volume mixer back to full (winmm sets it for the whole app).</summary>
    public static void ResetAppVolume(int sampleRate)
    {
        if (Open(sampleRate) is not { } output) return;
        waveOutSetVolume(output._handle, 0xFFFFFFFF);
        output.Dispose();
    }

    /// <summary>Frames queued and not played yet.</summary>
    public int QueuedFrames => _queuedFrames;

    private static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags));

    private readonly IntPtr _handle;
    private readonly Queue<(IntPtr Header, IntPtr Data, int Frames)> _queued = new();
    private int _queuedFrames;

    public int SampleRate { get; }

    private PcmStreamOut(IntPtr handle, int sampleRate)
    {
        _handle = handle;
        SampleRate = sampleRate;
    }

    public static PcmStreamOut? Open(int sampleRate)
    {
        var format = new WaveFormatEx
        {
            FormatTag = 1, Channels = 2, SamplesPerSec = (uint)sampleRate, BitsPerSample = 16,
            BlockAlign = 4, AvgBytesPerSec = (uint)sampleRate * 4,
        };
        return waveOutOpen(out var handle, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0) == 0 ? new PcmStreamOut(handle, sampleRate) : null;
    }

    /// <summary>Queues interleaved stereo samples (-1..1). Keeps the delay short: a little cushion after a gap, and blocks are
    /// skipped when the queue is already a quarter of a second long.</summary>
    public void Write(float[] interleaved, int frames)
    {
        Reclaim();
        if (_queuedFrames > SampleRate / 4) return;
        if (_queuedFrames == 0) Enqueue(new short[SampleRate / 25 * 2], SampleRate / 25); // 40 ms against hiccups
        var pcm = new short[frames * 2];
        for (var i = 0; i < pcm.Length; i++)
        {
            var v = interleaved[i];
            pcm[i] = (short)(v >= 1f ? short.MaxValue : v <= -1f ? -short.MaxValue : v * short.MaxValue);
        }
        Enqueue(pcm, frames);
    }

    private void Enqueue(short[] pcm, int frames)
    {
        var bytes = pcm.Length * 2;
        var data = Marshal.AllocHGlobal(bytes);
        Marshal.Copy(pcm, 0, data, pcm.Length);
        var header = Marshal.AllocHGlobal(HeaderSize);
        Marshal.StructureToPtr(new WaveHeader { Data = data, BufferLength = (uint)bytes }, header, false);
        if (waveOutPrepareHeader(_handle, header, (uint)HeaderSize) != 0)
        {
            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(data);
            return;
        }
        if (waveOutWrite(_handle, header, (uint)HeaderSize) != 0)
        {
            waveOutUnprepareHeader(_handle, header, (uint)HeaderSize);
            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(data);
            return;
        }
        _queued.Enqueue((header, data, frames));
        _queuedFrames += frames;
    }

    /// <summary>Frees the blocks that have finished playing.</summary>
    public void Reclaim()
    {
        while (_queued.Count > 0)
        {
            var (header, data, frames) = _queued.Peek();
            if ((Marshal.ReadInt32(header, FlagsOffset) & WhdrDone) == 0) break;
            _queued.Dequeue();
            _queuedFrames -= frames;
            waveOutUnprepareHeader(_handle, header, (uint)HeaderSize);
            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(data);
        }
    }

    public void Dispose()
    {
        waveOutReset(_handle); // marks every queued block done
        Reclaim();
        waveOutClose(_handle);
    }
}
