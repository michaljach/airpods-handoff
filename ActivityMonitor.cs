using Microsoft.Win32;
using Windows.Media.Control;

namespace AirPodsHandoff;

using MediaSession = GlobalSystemMediaTransportControlsSession;
using PlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus;

// Answers "does the PC want audio right now?" from three signals: the default output's peak meter,
// media sessions reporting Playing, and the microphone being in use (calls with long silences).
sealed class ActivityMonitor
{
    IMMDeviceEnumerator? _enumerator;
    GlobalSystemMediaTransportControlsSessionManager? _media;
    string? _meterEndpointId;
    IAudioMeterInformation? _meter;

    IMMDeviceEnumerator Enumerator => _enumerator ??= CoreAudio.NewEnumerator();

    public async Task InitAsync()
    {
        try
        {
            _media = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception e)
        {
            Log.Info($"media: session manager unavailable: {e.Message}");
        }
    }

    public float DefaultOutputPeak()
    {
        try
        {
            Enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device);
            device.GetId(out var id);
            if (id != _meterEndpointId || _meter is null)
            {
                _meter = CoreAudio.Activate<IAudioMeterInformation>(device);
                _meterEndpointId = id;
            }
            _meter.GetPeakValue(out float peak);
            return peak;
        }
        catch (Exception)
        {
            // No output device at all, or it vanished mid-call.
            _meter = null;
            _meterEndpointId = null;
            return 0;
        }
    }

    public List<MediaSession> PlayingSessions()
    {
        var playing = new List<MediaSession>();
        if (_media is null) return playing;
        try
        {
            foreach (var session in _media.GetSessions())
                if (session.GetPlaybackInfo()?.PlaybackStatus == PlaybackStatus.Playing)
                    playing.Add(session);
        }
        catch (Exception e)
        {
            Log.Info($"media: listing sessions failed: {e.Message}");
        }
        return playing;
    }

    public static async Task PauseAsync(IEnumerable<MediaSession> sessions)
    {
        foreach (var s in sessions)
            try { await s.TryPauseAsync(); } catch (Exception e) { Log.Info($"media: pause failed: {e.Message}"); }
    }

    public async Task ResumeAsync(IEnumerable<MediaSession> sessions)
    {
        // Sessions can close while paused; only resume ones that still exist.
        var live = _media?.GetSessions().Select(s => s.SourceAppUserModelId).ToHashSet() ?? new HashSet<string>();
        foreach (var s in sessions)
        {
            if (!live.Contains(s.SourceAppUserModelId)) continue;
            try { await s.TryPlayAsync(); } catch (Exception e) { Log.Info($"media: play failed: {e.Message}"); }
        }
    }

    // Windows records microphone use per app under CapabilityAccessManager; LastUsedTimeStop == 0
    // means the app is using the mic right now.
    public static bool MicrophoneInUse()
    {
        const string root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(root);
            return key is not null && AnyInUse(key, nested: false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    static bool AnyInUse(RegistryKey key, bool nested)
    {
        foreach (var name in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(name);
            if (sub is null) continue;
            if (sub.GetValue("LastUsedTimeStart") is long start && start != 0
                && sub.GetValue("LastUsedTimeStop") is long stop && stop == 0)
                return true;
            if (!nested && name == "NonPackaged" && AnyInUse(sub, nested: true))
                return true;
        }
        return false;
    }
}
