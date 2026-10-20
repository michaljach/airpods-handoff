using System.Diagnostics;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AirPodsHandoff;

sealed class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "AirPodsHandoff";

    readonly Config _cfg = Config.Load();
    readonly HandoffController _controller;
    readonly NotifyIcon _tray = new() { Visible = true, Text = "AirPods Handoff" };
    readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };
    readonly ToolStripMenuItem _status = new("Starting…") { Enabled = false };
    readonly ToolStripMenuItem _auto = new("Automatic handoff") { CheckOnClick = true };
    readonly ToolStripMenuItem _pause = new("Pause when a bud is removed") { CheckOnClick = true };
    readonly ToolStripMenuItem _startup = new("Start with Windows") { CheckOnClick = true };
    string _iconKey = "";

    public TrayApp()
    {
        Log.Info($"start: config {Config.FilePath}");
        _controller = new HandoffController(_cfg);

        _auto.Checked = _cfg.AutoHandoff;
        _auto.CheckedChanged += (_, _) => { _cfg.AutoHandoff = _auto.Checked; _cfg.Save(); };
        _pause.Checked = _cfg.AutoPause;
        _pause.CheckedChanged += (_, _) => { _cfg.AutoPause = _pause.Checked; _cfg.Save(); };
        _startup.Checked = IsStartupEnabled();
        _startup.CheckedChanged += (_, _) => SetStartup(_startup.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_auto);
        menu.Items.Add(_pause);
        menu.Items.Add(_startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Connect now", null, (_, _) => _controller.ConnectNow());
        menu.Items.Add("Disconnect now", null, (_, _) => _controller.DisconnectNow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open config", null, (_, _) => Open(Config.FilePath));
        menu.Items.Add("Open log", null, (_, _) => Open(Log.FilePath));
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => _controller.ConnectNow();

        _uiTimer.Tick += (_, _) => Refresh();
        _uiTimer.Start();
        Refresh();
        _controller.Start();
    }

    void Refresh()
    {
        var s = _controller.Current;
        var p = s.Pods;
        string pods = p is null
            ? "no BLE data"
            : $"L {Pct(p.LeftBattery)}{(p.LeftCharging ? "+" : "")}  R {Pct(p.RightBattery)}{(p.RightCharging ? "+" : "")}  "
              + $"case {Pct(p.CaseBattery)}{(p.CaseCharging ? "+" : "")}  in ear {p.InEarCount}";
        string link = s.Connected ? "Connected to PC" : "Not connected";
        if (s.GrabSuppressed) link += " (held by other device)";

        _status.Text = $"{link} — {pods}";
        var tip = $"AirPods: {link}\n{pods}\n{s.LastAction}";
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;

        string label = p?.LowestPodBattery is { } b ? (b == 100 ? "99" : b.ToString()) : "–";
        string key = $"{label}|{s.Connected}";
        if (key != _iconKey)
        {
            _iconKey = key;
            var old = _tray.Icon;
            _tray.Icon = RenderIcon(label, s.Connected ? Color.FromArgb(0x2E, 0x7D, 0x32) : Color.FromArgb(0x61, 0x61, 0x61));
            old?.Dispose();
        }
    }

    static string Pct(int? v) => v is { } x ? $"{x}%" : "?";

    static Icon RenderIcon(string text, Color background)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(background);
            g.FillEllipse(brush, 0, 0, 31, 31);
            using var font = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
        }
        IntPtr handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is not null;
    }

    static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) { Log.Info($"ui: can't open {path}: {e.Message}"); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _uiTimer.Dispose();
            _controller.Dispose();
            _tray.Visible = false;
            _tray.Icon?.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
