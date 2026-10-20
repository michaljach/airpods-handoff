using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace AirPodsHandoff;

// Listens for the AirPods' unencrypted proximity advertisements (battery, in-ear, case).
sealed class BleMonitor : IDisposable
{
    const ushort AppleCompanyId = 0x004C;

    readonly Config _cfg;
    readonly HashSet<ushort> _models;
    readonly BluetoothLEAdvertisementWatcher _watcher = new() { ScanningMode = BluetoothLEScanningMode.Active };
    readonly object _gate = new();
    AirPodsState? _latest;

    public BleMonitor(Config cfg)
    {
        _cfg = cfg;
        _models = cfg.ParsedModelIds();
        _watcher.Received += OnReceived;
        _watcher.Stopped += (_, e) => Log.Info($"ble: watcher stopped ({e.Error})");
    }

    // Restarts the watcher if it died, e.g. after the Bluetooth radio was toggled.
    public void EnsureRunning()
    {
        if (_watcher.Status is BluetoothLEAdvertisementWatcherStatus.Started) return;
        try
        {
            _watcher.Start();
            Log.Info("ble: scanning");
        }
        catch (Exception e)
        {
            Log.Info($"ble: start failed: {e.Message}");
        }
    }

    public AirPodsState? Latest(TimeSpan maxAge)
    {
        lock (_gate)
            return _latest is { } s && DateTime.UtcNow - s.SeenAt <= maxAge ? s : null;
    }

    void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        foreach (var md in args.Advertisement.GetManufacturerDataByCompanyId(AppleCompanyId))
        {
            var data = ToArray(md.Data);
            var state = ProximityPairing.TryParse(data, args.RawSignalStrengthInDBm, DateTime.UtcNow);
            if (state is null) continue;

            if (_cfg.LogAdvertisements)
                Log.Info($"ble: rssi={args.RawSignalStrengthInDBm} model=0x{state.Model:X4} raw={Convert.ToHexString(data)} -> {state}");

            if (args.RawSignalStrengthInDBm < _cfg.MinRssi) continue;
            if (_models.Count > 0 && !_models.Contains(state.Model)) continue;

            lock (_gate)
            {
                // Prefer the strongest nearby set; a weaker one only wins once ours has gone quiet.
                if (_latest is null
                    || state.Rssi >= _latest.Rssi - 10
                    || state.SeenAt - _latest.SeenAt > TimeSpan.FromSeconds(5))
                    _latest = state;
            }
        }
    }

    static byte[] ToArray(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    public void Dispose()
    {
        _watcher.Received -= OnReceived;
        if (_watcher.Status is BluetoothLEAdvertisementWatcherStatus.Started) _watcher.Stop();
    }
}
