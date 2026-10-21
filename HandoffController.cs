using Windows.Media.Control;

namespace AirPodsHandoff;

enum LinkOp { None, Connecting, Disconnecting }

sealed record Snapshot(bool Connected, AirPodsState? Pods, bool PcActive, bool GrabSuppressed, string LastAction, LinkOp Op);

// The policy loop. Every 500 ms:
//   - PC audio active for GrabAfterMs, buds in ear, AirPods not connected here -> connect (take them from the phone)
//   - connected, no PC audio for ReleaseAfterSeconds                           -> disconnect (give them back)
//   - bud removed / reinserted while connected here                            -> pause / resume media
sealed class HandoffController : IDisposable
{
    static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan PodsStaleAfter = TimeSpan.FromSeconds(30);
    static readonly TimeSpan ConnectRetryAfter = TimeSpan.FromSeconds(10);
    static readonly TimeSpan InEarDebounce = TimeSpan.FromSeconds(1);
    static readonly TimeSpan ResumeWithin = TimeSpan.FromMinutes(5);
    static readonly TimeSpan SuppressionClearsAfter = TimeSpan.FromSeconds(5);
    // Taking the AirPods from the phone routinely takes 20-25 s.
    static readonly TimeSpan OpTimeout = TimeSpan.FromSeconds(45);

    readonly Config _cfg;
    readonly BleMonitor _ble;
    readonly BtAudio _bt;
    readonly ActivityMonitor _activity = new();
    System.Threading.Timer? _timer;
    int _busy;

    DateTime? _activeSince;
    DateTime _inactiveSince = DateTime.UtcNow;
    DateTime _lastActive = DateTime.UtcNow;
    DateTime _lastConnectAttempt = DateTime.MinValue;
    DateTime _lastOwnDisconnect = DateTime.MinValue;
    DateTime _lastBleCheck = DateTime.MinValue;
    bool _wasConnected;
    bool _grabSuppressed;
    bool _warnedNoBle;

    int? _stableInEar;
    int? _pendingInEar;
    int? _pendingTransition;
    DateTime _pendingSince;
    List<GlobalSystemMediaTransportControlsSession> _pausedByUs = new();
    int _inEarBeforePause;
    DateTime _pausedAt;

    // A connect/disconnect stays in flight from the request until the endpoint state flips (or OpTimeout),
    // so the tray can show progress and nothing else is fired meanwhile.
    readonly object _opLock = new();
    LinkOp _op;
    DateTime _opStarted;

    public Snapshot Current { get; private set; } = new(false, null, false, false, "starting", LinkOp.None);

    public HandoffController(Config cfg)
    {
        _cfg = cfg;
        _ble = new BleMonitor(cfg);
        _bt = new BtAudio(cfg.DeviceNameContains);
    }

    public void Start()
    {
        _ble.EnsureRunning();
        Task.Run(async () =>
        {
            await _activity.InitAsync();
            _timer = new System.Threading.Timer(state => _ = TickAsync(), null, TimeSpan.Zero, Tick);
        });
    }

    public void ConnectNow()
    {
        if (!TryBeginOp(LinkOp.Connecting)) return;
        _grabSuppressed = false;
        Task.Run(() => { if (!_bt.Connect()) EndOp(); });
    }

    // Manual release: hold off auto-grab until PC audio stops, or it would grab them right back.
    public void DisconnectNow()
    {
        if (!TryBeginOp(LinkOp.Disconnecting)) return;
        _grabSuppressed = true;
        _lastOwnDisconnect = DateTime.UtcNow;
        Task.Run(() => { if (!_bt.Disconnect()) EndOp(); });
    }

    bool TryBeginOp(LinkOp op)
    {
        lock (_opLock)
        {
            if (_op != LinkOp.None) return false;
            _op = op;
            _opStarted = DateTime.UtcNow;
        }
        Current = Current with { Op = op };
        return true;
    }

    void EndOp()
    {
        lock (_opLock) _op = LinkOp.None;
        Current = Current with { Op = LinkOp.None };
    }

    // Finishes the in-flight op once the endpoint reaches the target state or it times out.
    LinkOp SettleOp(bool connected, DateTime now)
    {
        lock (_opLock)
        {
            if (_op == LinkOp.None) return _op;
            bool reached = _op == LinkOp.Connecting ? connected : !connected;
            if (!reached && now - _opStarted < OpTimeout) return _op;
            var elapsed = (now - _opStarted).TotalSeconds;
            Log.Info($"bt: {_op.ToString().ToLowerInvariant()} {(reached ? "done" : "timed out")} after {elapsed:F1}s");
            return _op = LinkOp.None;
        }
    }

    async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            await TickCoreAsync();
        }
        catch (Exception e)
        {
            Log.Info($"tick: {e}");
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    async Task TickCoreAsync()
    {
        var now = DateTime.UtcNow;
        if (now - _lastBleCheck > TimeSpan.FromSeconds(10))
        {
            _lastBleCheck = now;
            _ble.EnsureRunning();
        }

        var pods = _ble.Latest(PodsStaleAfter);
        bool connected = _bt.IsConnected();

        float peak = _activity.DefaultOutputPeak();
        var playing = _activity.PlayingSessions();
        bool mic = ActivityMonitor.MicrophoneInUse();
        bool active = peak >= _cfg.ActivityPeakThreshold || playing.Count > 0 || mic;

        if (active)
        {
            _lastActive = now;
            _activeSince ??= now;
        }
        else
        {
            if (_activeSince is not null) _inactiveSince = now;
            _activeSince = null;
            if (_grabSuppressed && now - _inactiveSince >= SuppressionClearsAfter)
            {
                _grabSuppressed = false;
                Log.Info("handoff: PC audio stopped, auto-grab re-armed");
            }
        }

        if (connected && !_wasConnected)
        {
            Log.Info("bt: AirPods connected to PC");
            _lastActive = now;
        }
        else if (!connected && _wasConnected)
        {
            // Lost them while the PC was still busy and we didn't ask for it: another device took them.
            // Don't fight it until PC audio has stopped.
            bool ours = now - _lastOwnDisconnect < TimeSpan.FromSeconds(5);
            Log.Info($"bt: AirPods disconnected from PC ({(ours ? "by us" : "elsewhere")})");
            if (!ours && active)
            {
                _grabSuppressed = true;
                Log.Info("handoff: taken by another device during PC audio, auto-grab paused until PC audio stops");
            }
        }
        _wasConnected = connected;
        var op = SettleOp(connected, now);

        int? inEar = UpdateInEar(pods?.InEarCount, now);
        if (inEar is { } count && _pendingTransition is { } prev)
        {
            _pendingTransition = null;
            await OnInEarChangedAsync(prev, count, connected, playing, now);
        }

        bool wearing = inEar is > 0 || (pods is null && !_cfg.RequireInEar);
        if (pods is null && _cfg.RequireInEar && !_warnedNoBle && active)
        {
            _warnedNoBle = true;
            Log.Info("handoff: no AirPods BLE data — not grabbing (RequireInEar). Check model id / MinRssi, or set RequireInEar=false.");
        }

        string action = Current.LastAction;
        if (_cfg.AutoHandoff && !_grabSuppressed && op == LinkOp.None)
        {
            if (!connected
                && _activeSince is { } since && now - since >= TimeSpan.FromMilliseconds(_cfg.GrabAfterMs)
                && wearing
                && now - _lastConnectAttempt > ConnectRetryAfter
                && TryBeginOp(LinkOp.Connecting))
            {
                _lastConnectAttempt = now;
                Log.Info($"handoff: PC audio active (peak={peak:F3} media={playing.Count} mic={mic}), grabbing AirPods");
                bool ok = _bt.Connect();
                if (!ok) EndOp();
                action = ok ? $"grabbed {DateTime.Now:HH:mm}" : "grab failed";
            }
            else if (connected
                && _cfg.ReleaseAfterSeconds > 0
                && now - _lastActive >= TimeSpan.FromSeconds(_cfg.ReleaseAfterSeconds)
                && TryBeginOp(LinkOp.Disconnecting))
            {
                Log.Info($"handoff: no PC audio for {_cfg.ReleaseAfterSeconds}s, releasing AirPods");
                _lastOwnDisconnect = now;
                _lastActive = now; // don't hammer if the disconnect doesn't take
                bool ok = _bt.Disconnect();
                if (!ok) EndOp();
                action = ok ? $"released {DateTime.Now:HH:mm}" : "release failed";
            }
        }

        lock (_opLock) op = _op; // a click may have started one during this tick
        Current = new Snapshot(connected, pods, active, _grabSuppressed, action, op);
    }

    // BLE in-ear bits flicker for a moment while a bud is moving; only accept a count held for 1 s.
    int? UpdateInEar(int? raw, DateTime now)
    {
        if (raw is null) return _stableInEar; // stale/no data: keep the last known state, never trigger
        if (raw != _pendingInEar)
        {
            _pendingInEar = raw;
            _pendingSince = now;
        }
        else if (raw != _stableInEar && now - _pendingSince >= InEarDebounce)
        {
            if (_stableInEar is { } prev) _pendingTransition = prev;
            _stableInEar = raw;
        }
        return _stableInEar;
    }

    async Task OnInEarChangedAsync(int prev, int count, bool connected, List<GlobalSystemMediaTransportControlsSession> playing, DateTime now)
    {
        Log.Info($"ear: {prev} -> {count} in ear");
        if (!_cfg.AutoPause || !connected) return;

        if (count < prev && playing.Count > 0)
        {
            _pausedByUs = playing;
            _inEarBeforePause = prev;
            _pausedAt = now;
            Log.Info($"ear: bud removed, pausing {playing.Count} session(s)");
            await ActivityMonitor.PauseAsync(playing);
        }
        else if (count > prev && _pausedByUs.Count > 0)
        {
            if (count >= _inEarBeforePause && now - _pausedAt <= ResumeWithin)
            {
                Log.Info($"ear: bud back in, resuming {_pausedByUs.Count} session(s)");
                await _activity.ResumeAsync(_pausedByUs);
            }
            if (count >= _inEarBeforePause) _pausedByUs = new();
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _ble.Dispose();
    }
}
