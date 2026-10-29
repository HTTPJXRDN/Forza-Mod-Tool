namespace FH6LocalCryptoTool;

/// <summary>Stock FH2 PlayerXPLevels.xml wristbands, not CareerPlayerLevels.xml or FH6 curves.</summary>
public static class Fh2DriverProgression
{
    public const uint MaxLevel = 1000;
    static readonly string[] Bands = { "Yellow", "Green", "Blue", "Pink", "Orange", "Purple", "Red", "Turquoise", "Bronze", "Silver", "Gold" };
    public sealed record Progress(uint Level, uint SavedIndex, string Wristband, uint StartXp, uint IntoLevel, uint RequiredXp);
    // The first 14 transitions cost 15k, the next 135 cost 20k. Gold
    // applies its first +300 before the first gold transition (20,300),
    // then adds another 300 per transition. The previous zero-step formula
    // incorrectly predicted level 311 at the user's 10,000,000-XP / level-310 sample.
    static uint Cost(uint index) => index < 14 ? 15000 : index < 149 ? 20000 : checked(20000 + (index - 148) * 300);
    internal static bool IsPreviousPreviewIndex(uint xp, uint savedIndex)
    {
        var current = Preview(xp);
        // Earlier builds saved the absolute displayed-level index. FH2 stores
        // the zero-based index within the current wristband instead: native
        // 10,071,953 XP / HUD 311 has Gold index 161, not absolute index 310.
        if (savedIndex == current.Level - 1 && savedIndex != current.SavedIndex) return true;
        if (xp < XpForLevel(150)) return false;
        uint index = 0, start = 0;
        while (index < MaxLevel - 1)
        {
            uint cost = index < 149 ? Cost(index) : Cost(index) - 300;
            if (xp - start < cost) break;
            start += cost; index++;
        }
        return index == savedIndex;
    }
    public static uint XpForLevel(uint level)
    {
        if (level is < 1 or > MaxLevel) throw new ArgumentOutOfRangeException(nameof(level), "Use an FH2 level from 1 to 1000.");
        uint xp = 0; for (uint index = 0; index < level - 1; index++) xp = checked(xp + Cost(index)); return xp;
    }
    public static uint MaxXp => checked(XpForLevel(MaxLevel) + Cost(MaxLevel - 1) - 1);
    public static Progress Preview(uint xp)
    {
        if (xp > MaxXp) throw new ArgumentOutOfRangeException(nameof(xp), $"XP above {MaxXp:N0} is outside the supported FH2 level range.");
        uint index = 0, start = 0;
        while (index < MaxLevel - 1 && xp - start >= Cost(index)) { start += Cost(index); index++; }
        int band = index < 14 ? 0 : index < 149 ? 1 + (int)(index - 14) / 15 : 10;
        uint bandStart = band == 0 ? 0U : band == 10 ? 149U : 14U + (uint)(band - 1) * 15U;
        return new(index + 1, index - bandStart, Bands[band], start, xp - start, Cost(index));
    }
}
