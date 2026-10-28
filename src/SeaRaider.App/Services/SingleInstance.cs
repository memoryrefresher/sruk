using System.IO.Pipes;
using System.Text.Json;
using Avalonia.Threading;

namespace SeaRaider.App.Services;

/// <summary>
/// One SeaRaider per Windows user. Starting it again (a game's desktop shortcut, the Start menu) hands the command line
/// to the running one over a named pipe ("start this game", or just "come to the front") and exits.
/// </summary>
internal static class SingleInstance
{
    private static readonly string Name = $"SeaRaider-{Environment.UserName}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
    private static Mutex? _mutex;

    /// <summary>True when this is the only SeaRaider (it keeps that until it exits).</summary>
    public static bool TryBecomePrimary()
    {
        var mutex = new Mutex(false, $@"Local\{Name}");
        try
        {
            if (mutex.WaitOne(0))
            {
                _mutex = mutex;
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            _mutex = mutex; // the last SeaRaider crashed: this one takes over
            return true;
        }
        mutex.Dispose();
        return false;
    }

    /// <summary>Gives the running SeaRaider this command line. False when it doesn't answer.</summary>
    public static bool Forward(string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out);
            pipe.Connect(5000);
            using var writer = new StreamWriter(pipe);
            writer.WriteLine(JsonSerializer.Serialize(args));
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Receives command lines from later starts, on the UI thread, for as long as SeaRaider runs.</summary>
    public static void Listen(Action<string[]> received)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync().ConfigureAwait(false);
                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    var args = line is null ? null : JsonSerializer.Deserialize<string[]>(line);
                    if (args is not null) Dispatcher.UIThread.Post(() => received(args));
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
        });
    }
}
