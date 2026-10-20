using System.Globalization;
using System.Text.Json;

namespace AirPodsHandoff;

sealed class Config
{
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AirPodsHandoff");

    public static string FilePath => Path.Combine(Dir, "config.json");

    // Substring of the Windows audio endpoint name, e.g. "Headphones (Michal's AirPods Pro)".
    // Make it specific if you have several AirPods paired.
    public string DeviceNameContains { get; set; } = "AirPods";

    // BLE model IDs to accept (hex). 0x2024 = AirPods Pro 2 (USB-C). Empty = any Apple audio device.
    public List<string> ModelIds { get; set; } = new() { "0x2024" };

    // Ignore advertisements weaker than this, so a colleague's AirPods don't drive your PC.
    public int MinRssi { get; set; } = -70;

    public bool AutoHandoff { get; set; } = true;

    // PC audio must be active this long before the AirPods are pulled over.
    public int GrabAfterMs { get; set; } = 1000;

    // Only grab when BLE says at least one bud is in an ear.
    public bool RequireInEar { get; set; } = true;

    // Disconnect after this long without PC audio so the phone/Mac can take over. 0 = never.
    public int ReleaseAfterSeconds { get; set; } = 120;

    // Default-endpoint peak (0..1) that counts as "PC is playing".
    public float ActivityPeakThreshold { get; set; } = 0.01f;

    // Pause media when a bud is removed, resume when it goes back in.
    public bool AutoPause { get; set; } = true;

    // Log every Apple proximity advertisement (raw hex) — use to verify model ID and bit layout.
    public bool LogAdvertisements { get; set; } = false;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Config Load()
    {
        Directory.CreateDirectory(Dir);
        if (!File.Exists(FilePath))
        {
            var fresh = new Config();
            fresh.Save();
            return fresh;
        }
        try
        {
            return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), Json) ?? new Config();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Log.Info($"config: failed to read {FilePath}, using defaults: {e.Message}");
            return new Config();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    public HashSet<ushort> ParsedModelIds()
    {
        var ids = new HashSet<ushort>();
        foreach (var s in ModelIds)
        {
            var hex = s.Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
                ids.Add(id);
            else
                Log.Info($"config: ignoring bad model id '{s}'");
        }
        return ids;
    }
}
