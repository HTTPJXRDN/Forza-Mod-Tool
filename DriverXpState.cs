using System.Globalization;

namespace FH6LocalCryptoTool;

/// <summary>PlayerXPLevels-backed preview. Does not grant rewards or alter the legacy Main/Level property.</summary>
public static class DriverXpState
{
    // Installed PlayerXPLevels (2026-10-14), grouped contiguous segments by
    // PrestigeLevel. All prestige-0 segments cost 15,000 XP/level; later
    // segments cost 30,000. The final prestige's final level is infinite.
    static readonly uint[] Levels = { 199, 299, 399, 499, 599, 749, 849, 899, 999, 1499, 2999 };
    public sealed record Level(uint Xp, uint Number, int Prestige, uint XpIntoLevel, uint? XpToNextLevel)
    {
        public string Display => $"Level {Number:n0} · Prestige {Prestige}";
    }

    public static Level Calculate(uint xp)
    {
        ulong remaining = xp;
        for (int prestige = 0; prestige < Levels.Length; prestige++)
        {
            uint rate = prestige == 0 ? 15_000U : 30_000U;
            uint count = Levels[prestige]; bool last = prestige == Levels.Length - 1;
            if (last && remaining >= (ulong)(count - 1) * rate)
                return new(xp, count, prestige, checked((uint)(remaining - (ulong)(count - 1) * rate)), null);
            ulong span = (ulong)count * rate;
            if (remaining < span)
                return new(xp, checked(1 + (uint)(remaining / rate)), prestige, (uint)(remaining % rate), rate - (uint)(remaining % rate));
            remaining -= span;
        }
        throw new InvalidOperationException("XP curve does not have a terminal level.");
    }

    public static bool TryRead(ProfileContainer? container, out Level? level)
    {
        level = null;
        if (!TryField(container, out var field) || !uint.TryParse(field!.DisplayValue(), NumberStyles.None, CultureInfo.InvariantCulture, out uint xp)) return false;
        level = Calculate(xp); return true;
    }

    public static bool TrySet(ProfileContainer? container, uint xp, out bool changed)
    {
        changed = false;
        if (!TryField(container, out var field) || !TryRead(container, out var old)) return false;
        if (old!.Xp == xp) return true;
        var staged = ProfileContainer.Parse(container!.Serialize());
        if (!TryField(staged, out var next) || !next!.TrySetFromText(xp.ToString(CultureInfo.InvariantCulture))) return false;
        byte[] bytes = staged.Serialize(); var verified = ProfileContainer.Parse(bytes);
        if (!verified.Serialize().AsSpan().SequenceEqual(bytes) || !TryRead(verified, out var check) || check!.Xp != xp) return false;
        if (!field!.TrySetFromText(xp.ToString(CultureInfo.InvariantCulture))) return false;
        changed = true; return true;
    }

    static bool TryField(ProfileContainer? container, out PropertyTree.PropertyNode? field)
    {
        field = null;
        var nodes = container?.Properties?.Walk().Where(p => p.Path == "/Main/XP").Select(p => p.Node).ToArray();
        if (nodes is not { Length: 1 } || nodes[0].TypeId is not (3 or 17) || nodes[0].RawValue.Length != 4 || !nodes[0].IsEditable) return false;
        field = nodes[0]; return true;
    }
}
