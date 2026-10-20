using System.Runtime.InteropServices;

namespace AirPodsHandoff;

// Connects/disconnects a paired Bluetooth audio device the way the Sound settings "Connect" button
// does: the BT audio KS filter behind the endpoint exposes one-shot reconnect/disconnect properties
// (same technique as ToothTray).
sealed class BtAudio
{
    static readonly Guid KsPropSetIdBtAudio = new("7fa06c40-b8f6-4c7e-8556-e8c33a12e54d");
    const uint KsPropertyOneShotReconnect = 0;
    const uint KsPropertyOneShotDisconnect = 1;
    const uint KsPropertyTypeGet = 0x1;

    readonly string _nameContains;
    IMMDeviceEnumerator? _enumerator;

    public BtAudio(string nameContains) => _nameContains = nameContains;

    // Created lazily so it lives on a thread-pool (MTA) thread, not the UI thread.
    IMMDeviceEnumerator Enumerator => _enumerator ??= CoreAudio.NewEnumerator();

    public bool IsConnected() => Endpoints(DeviceState.Active).Count > 0;

    public bool Connect() => SendOneShot(KsPropertyOneShotReconnect, "connect");

    public bool Disconnect() => SendOneShot(KsPropertyOneShotDisconnect, "disconnect");

    List<(string Name, IMMDevice Device)> Endpoints(uint stateMask)
    {
        var result = new List<(string, IMMDevice)>();
        Enumerator.EnumAudioEndpoints(EDataFlow.Render, stateMask, out var collection);
        collection.GetCount(out uint count);
        for (uint i = 0; i < count; i++)
        {
            collection.Item(i, out var device);
            var name = CoreAudio.FriendlyName(device);
            if (name.Contains(_nameContains, StringComparison.OrdinalIgnoreCase))
                result.Add((name, device));
        }
        return result;
    }

    bool SendOneShot(uint propertyId, string what)
    {
        var endpoints = Endpoints(DeviceState.Active | DeviceState.Unplugged);
        if (endpoints.Count == 0)
        {
            Log.Info($"bt: no audio endpoint matching '{_nameContains}' — is it paired?");
            return false;
        }

        // The stereo and hands-free endpoints sit on different KS filters; poke each one once.
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool ok = false;
        foreach (var (name, device) in endpoints)
        {
            try
            {
                var topology = CoreAudio.Activate<IDeviceTopology>(device);
                topology.GetConnector(0, out var connector);
                connector.GetDeviceIdConnectedTo(out var filterId);
                if (!done.Add(filterId)) continue;

                Enumerator.GetDevice(filterId, out var filter);
                var ks = CoreAudio.Activate<IKsControl>(filter);
                var property = new KsPropertyHeader { Set = KsPropSetIdBtAudio, Id = propertyId, Flags = KsPropertyTypeGet };
                int hr = ks.KsProperty(ref property, (uint)Marshal.SizeOf<KsPropertyHeader>(), IntPtr.Zero, 0, out _);
                Log.Info($"bt: {what} '{name}' -> 0x{hr:X8}");
                ok |= hr >= 0;
            }
            catch (Exception e)
            {
                Log.Info($"bt: {what} '{name}' failed: {e.Message}");
            }
        }
        return ok;
    }
}
