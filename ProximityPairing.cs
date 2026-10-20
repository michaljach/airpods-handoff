namespace AirPodsHandoff;

sealed record AirPodsState(
    ushort Model,
    int? LeftBattery,
    int? RightBattery,
    int? CaseBattery,
    bool LeftCharging,
    bool RightCharging,
    bool CaseCharging,
    bool LeftInEar,
    bool RightInEar,
    byte Lid,
    short Rssi,
    DateTime SeenAt)
{
    public int InEarCount => (LeftInEar ? 1 : 0) + (RightInEar ? 1 : 0);

    public int? LowestPodBattery =>
        LeftBattery is { } l && RightBattery is { } r ? Math.Min(l, r) : LeftBattery ?? RightBattery;
}

// Parser for Apple's "proximity pairing" BLE advertisement (manufacturer data, company 0x004C,
// type 0x07). Layout as reverse-engineered by OpenPods / AirPodsDesktop / LibrePods:
//
//   [0] 0x07 type      [1] length (0x19)   [2] prefix
//   [3..4] model, little-endian (0x2024 = AirPods Pro 2 USB-C)
//   [5] status: 0x20 = left bud is the broadcaster, 0x02 = broadcaster in ear, 0x08 = other bud in ear
//   [6] pod batteries: two nibbles, 0..10 (x10%), 15 = unknown; order depends on broadcaster
//   [7] high nibble charging flags (bit0/bit1 pods, bit2 case), low nibble case battery
//   [8] lid state   [9] color   [10] suffix   [11..] encrypted (needs iCloud keys, ignored)
static class ProximityPairing
{
    public static AirPodsState? TryParse(byte[] d, short rssi, DateTime seenAt)
    {
        if (d.Length < 10 || d[0] != 0x07) return null;

        ushort model = (ushort)(d[3] | d[4] << 8);
        byte status = d[5];
        bool leftBroadcasts = (status & 0x20) != 0;

        int lo = d[6] & 0x0F, hi = d[6] >> 4;
        int leftRaw = leftBroadcasts ? lo : hi;
        int rightRaw = leftBroadcasts ? hi : lo;

        int chargeFlags = d[7] >> 4;
        bool leftCharging = (chargeFlags & (leftBroadcasts ? 0b01 : 0b10)) != 0;
        bool rightCharging = (chargeFlags & (leftBroadcasts ? 0b10 : 0b01)) != 0;

        bool broadcasterInEar = (status & 0x02) != 0;
        bool otherInEar = (status & 0x08) != 0;

        return new AirPodsState(
            model,
            Battery(leftRaw),
            Battery(rightRaw),
            Battery(d[7] & 0x0F),
            leftCharging,
            rightCharging,
            (chargeFlags & 0b100) != 0,
            leftBroadcasts ? broadcasterInEar : otherInEar,
            leftBroadcasts ? otherInEar : broadcasterInEar,
            d[8],
            rssi,
            seenAt);
    }

    static int? Battery(int raw) => raw <= 10 ? raw * 10 : null;
}
