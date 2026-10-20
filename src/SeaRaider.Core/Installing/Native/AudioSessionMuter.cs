using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SeaRaider.Core.Installing.Native;

/// <summary>
/// Mutes setup programs in the Windows volume mixer (Core Audio sessions), so a repack installer's music doesn't blast
/// while it unpacks. Only the setup's own processes are muted, nothing else, and Settings → Installing turns this off.
///
/// <para>A process only gets an audio session once it starts playing (and may open a new one later), so the installer
/// loop calls <see cref="MuteNew"/> several times a second with the setup's current process tree. The mute is kept:
/// installers switch their sound back on themselves (their own music button, or a new track), and a session that was
/// muted once and then unmuted by the setup is muted again on the next look.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AudioSessionMuter
{
    private const int RenderFlow = 0; // eRender
    private const uint DeviceStateActive = 1;
    private const uint ClsCtxAll = 23;

    /// <summary>Mutes the audio sessions of <paramref name="processIds"/> (and of processes <paramref name="alsoMute"/>
    /// accepts) that aren't in <paramref name="mutedSessions"/> yet.</summary>
    /// <returns>How many sessions were muted for the first time now.</returns>
    public static int MuteNew(IReadOnlySet<int> processIds, ISet<string> mutedSessions, Func<int, bool>? alsoMute = null)
    {
        var muted = 0;
        var context = Guid.Empty;
        var release = new List<object>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            release.Add(enumerator);
            if (enumerator.EnumAudioEndpoints(RenderFlow, DeviceStateActive, out var devices) != 0 || devices is null) return 0;
            release.Add(devices);
            devices.GetCount(out var deviceCount);

            for (uint d = 0; d < deviceCount; d++)
            {
                if (devices.Item(d, out var device) != 0 || device is null) continue;
                release.Add(device);
                var iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var managerObject) != 0 || managerObject is not IAudioSessionManager2 manager)
                    continue;
                release.Add(manager);
                if (manager.GetSessionEnumerator(out var sessions) != 0 || sessions is null) continue;
                release.Add(sessions);
                sessions.GetCount(out var sessionCount);

                for (var i = 0; i < sessionCount; i++)
                {
                    if (sessions.GetSession(i, out var session) != 0 || session is null) continue;
                    release.Add(session);
                    if (session.GetProcessId(out var pid) != 0 || pid == 0) continue;
                    var processId = (int)pid;
                    var key = $"{d}|{processId}|{InstanceId(session)}";
                    if (!processIds.Contains(processId) && alsoMute?.Invoke(processId) != true) continue;
                    if (session is not ISimpleAudioVolume volume) continue;

                    // Already muted: nothing to do. Muted before but switched back on by the setup: mute it again.
                    if (volume.GetMute(out var isMuted) == 0 && isMuted != 0)
                    {
                        if (mutedSessions.Add(key)) muted++;
                        continue;
                    }
                    if (volume.SetMute(1, ref context) == 0 && mutedSessions.Add(key)) muted++;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            // No audio device, or audio service unavailable: nothing to mute.
        }
        finally
        {
            foreach (var item in release)
            {
                try { Marshal.ReleaseComObject(item); }
                catch (ArgumentException) { }
            }
        }
        return muted;
    }

    /// <summary>
    /// How loud the setup is right now: the highest peak (0–1) of the audio sessions of <paramref name="processIds"/> (and of
    /// processes <paramref name="also"/> accepts). The meter reads the sound the program produces, before the mixer's mute,
    /// so it tells whether the installer's music is playing even while SeaRaider keeps it muted in the mixer.
    /// </summary>
    public static float PeakOf(IReadOnlySet<int> processIds, Func<int, bool>? also = null)
    {
        var peak = 0f;
        var release = new List<object>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            release.Add(enumerator);
            if (enumerator.EnumAudioEndpoints(RenderFlow, DeviceStateActive, out var devices) != 0 || devices is null) return 0;
            release.Add(devices);
            devices.GetCount(out var deviceCount);

            for (uint d = 0; d < deviceCount; d++)
            {
                if (devices.Item(d, out var device) != 0 || device is null) continue;
                release.Add(device);
                var iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var managerObject) != 0 || managerObject is not IAudioSessionManager2 manager)
                    continue;
                release.Add(manager);
                if (manager.GetSessionEnumerator(out var sessions) != 0 || sessions is null) continue;
                release.Add(sessions);
                sessions.GetCount(out var sessionCount);

                for (var i = 0; i < sessionCount; i++)
                {
                    if (sessions.GetSession(i, out var session) != 0 || session is null) continue;
                    release.Add(session);
                    if (session.GetProcessId(out var pid) != 0 || pid == 0) continue;
                    if (!processIds.Contains((int)pid) && also?.Invoke((int)pid) != true) continue;
                    if (session is IAudioMeterInformation meter && meter.GetPeakValue(out var value) == 0 && value > peak) peak = value;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            // No audio device, or audio service unavailable: silence.
        }
        finally
        {
            foreach (var item in release)
            {
                try { Marshal.ReleaseComObject(item); }
                catch (ArgumentException) { }
            }
        }
        return peak;
    }

    /// <summary>The session's instance id (unique per session, unlike the process id).</summary>
    private static string InstanceId(IAudioSessionControl2 session)
    {
        if (session.GetSessionInstanceIdentifier(out var id) != 0 || id == IntPtr.Zero) return string.Empty;
        try { return Marshal.PtrToStringUni(id) ?? string.Empty; }
        finally { Marshal.FreeCoTaskMem(id); }
    }

    // ---- Core Audio COM interfaces (mmdeviceapi.h, audiopolicy.h) -----------------------------------
    // Methods SeaRaider doesn't call are declared with placeholder signatures to keep the vtable order.

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr endpoint);
        [PreserveSig] int GetDevice(IntPtr id, out IntPtr device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);

        // IAudioSessionManager2
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification(IntPtr sessionId, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr eventContext);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(IntPtr grouping, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);

        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(int optOut);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
        [PreserveSig] int GetMeteringChannelCount(out uint count);
        [PreserveSig] int GetChannelsPeakValues(uint count, IntPtr peaks);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6F7C61AD7D17"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(int mute, ref Guid eventContext);
        [PreserveSig] int GetMute(out int mute);
    }
}
