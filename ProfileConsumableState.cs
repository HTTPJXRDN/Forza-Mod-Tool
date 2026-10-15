using System.Buffers.Binary;
using System.Globalization;

namespace FH6LocalCryptoTool;

/// <summary>Spendable balances only; no score, lifetime, prize RNG or reward-claim edits.</summary>
public static class ProfileConsumableState
{
    public enum Kind { SkillPoints, Wheelspins, SuperWheelspins }

    public static bool TryRead(ProfileContainer? container, Kind kind, out int balance, out string reason)
    {
        balance = 0;
        return TryField(container, kind, out _, out _, out balance, out _, out reason);
    }

    public static bool TrySet(ProfileContainer? container, Kind kind, int balance, out bool changed, out string reason)
    {
        changed = false;
        if (balance < 0) { reason = "Enter a whole number from 0 to 2,147,483,647."; return false; }
        if (!TryField(container, kind, out var bx, out var field, out int current, out int purchased, out reason)) return false;
        if (balance < purchased) { reason = "This balance cannot be lower than its existing purchased-spin portion."; return false; }
        if (balance == current) return true;
        // Validate on a clone before touching the live editor's working objects.
        var staged = ProfileContainer.Parse(container!.Serialize());
        if (!TryField(staged, kind, out var nextBx, out var nextField, out _, out _, out reason) ||
            !nextBx!.SetAttribute(nextField!, "value", balance.ToString(CultureInfo.InvariantCulture))) return false;
        if (kind == Kind.SkillPoints) SetSkillBinary(staged, balance);
        byte[] bytes = staged.Serialize(); var verified = ProfileContainer.Parse(bytes);
        if (!verified.Serialize().AsSpan().SequenceEqual(bytes) || !TryRead(verified, kind, out int check, out reason) || check != balance) return false;
        if (!bx!.SetAttribute(field!, "value", balance.ToString(CultureInfo.InvariantCulture))) return false;
        if (kind == Kind.SkillPoints) SetSkillBinary(container!, balance);
        changed = true; return true;
    }

    static bool TryField(ProfileContainer? container, Kind kind, out Bxml? bx, out Bxml.Node? field,
        out int balance, out int purchased, out string reason)
    {
        bx = container?.SaveState; field = null; balance = purchased = 0;
        reason = "This save's balance field is missing or not safely recognized.";
        if (bx == null || !Enum.IsDefined(kind)) return false;
        string key = kind == Kind.SkillPoints ? "CarPerkSaveState" : "TombolaState";
        var doc = bx;
        var maps = doc.Root.Children.Where(n => doc.Strings[n.NameIndex] == "map_element" &&
            n.Children.Count(c => doc.Strings[c.NameIndex] == "key" && doc.GetAttribute(c, "value") == key) == 1).ToArray();
        if (maps.Length != 1) return false;
        var values = maps[0].Children.Where(n => doc.Strings[n.NameIndex] == "value").ToArray();
        if (values.Length != 1 || doc.GetAttribute(values[0], "type") != key) return false;
        var parent = values[0];
        Bxml.Node? Property(Bxml.Node owner, string id, string type)
        {
            var found = owner.Children.Where(n => doc.Strings[n.NameIndex] == "property" && doc.GetAttribute(n, "id") == id).ToArray();
            return found.Length == 1 && doc.GetAttribute(found[0], "type") == type ? found[0] : null;
        }
        bool Integer(Bxml.Node? node, out int value)
        {
            value = 0;
            return node != null && node.Children.Count == 0 && node.Attributes.Count(a => doc.Strings[a.KeyIndex] == "value") == 1 &&
                int.TryParse(doc.GetAttribute(node, "value"), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
        }
        if (kind != Kind.SkillPoints)
        {
            var spin = Property(parent, kind == Kind.Wheelspins ? "NormalSpinInfo" : "SuperSpinInfo", "TombolaSpinTypeInfo");
            if (spin == null || !Integer(Property(spin, "NumSpinsPurchasedOfCurrentSpins", "int"), out purchased)) return false;
            parent = spin;
        }
        field = Property(parent, kind == Kind.SkillPoints ? "CurrentSkillTokensTotal" : "Spins", "int");
        if (!Integer(field, out balance) || purchased > balance) return false;
        if (kind == Kind.SkillPoints && !SkillCopiesAgree(container, doc, parent, out reason)) return false;
        reason = ""; return true;
    }

    static bool SkillCopiesAgree(ProfileContainer? container, Bxml bx, Bxml.Node parent, out string reason)
    {
        reason = "This save's binary Skill points layout is not recognized; no values were changed.";
        var records = container?.Career?.Records.Where(r => r.Name == "CarPerkSaveState").ToArray();
        if (records is not { Length: 1 } || records[0].Serializer != "CarPerkSaveState") return false;
        byte[] b = records[0].Payload;
        // Observed serializer version 1: version, skill score, raw score remainder,
        // spendable tokens, notification bool, best combo, then the mastery map.
        // Validate every mirrored header field, not a search for the current number.
        // The entire mastery-map tail remains opaque and byte-for-byte unchanged.
        if (b.Length < 25 || BinaryPrimitives.ReadUInt32LittleEndian(b) != 1 || b[16] > 1) return false;
        uint entries = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(21));
        if (entries > 100_000 || entries > b.Length - 25 || (entries == 0 && b.Length != 25)) return false;
        foreach (var (id, offset) in new[] { ("SkillPointsTotal", 4), ("UnspentSkillPointsTotal", 8), ("CurrentSkillTokensTotal", 12), ("BestSkillsCombo", 17) })
        {
            var nodes = parent.Children.Where(n => bx.Strings[n.NameIndex] == "property" && bx.GetAttribute(n, "id") == id).ToArray();
            if (nodes.Length != 1 || bx.GetAttribute(nodes[0], "type") != "int" || nodes[0].Children.Count != 0 ||
                nodes[0].Attributes.Count(a => bx.Strings[a.KeyIndex] == "value") != 1 ||
                !int.TryParse(bx.GetAttribute(nodes[0], "value"), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ||
                value < 0 || value != BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(offset)))
            {
                reason = "Skill-point copies disagree or have an unsupported layout. Load the game's saved copy before editing.";
                return false;
            }
        }
        var seen = parent.Children.Where(n => bx.Strings[n.NameIndex] == "property" && bx.GetAttribute(n, "id") == "SeenCarMasteriesNotification").ToArray();
        if (seen.Length != 1 || bx.GetAttribute(seen[0], "type") != "bool" || seen[0].Children.Count != 0 ||
            seen[0].Attributes.Count(a => bx.Strings[a.KeyIndex] == "value") != 1 ||
            bx.GetAttribute(seen[0], "value") != (b[16] == 1 ? "true" : "false")) return false;
        reason = ""; return true;
    }

    static void SetSkillBinary(ProfileContainer container, int balance)
    {
        // Called only after SkillCopiesAgree validates this exact record and mirror.
        var record = container.Career!.Records.Single(r => r.Name == "CarPerkSaveState");
        byte[] payload = (byte[])record.Payload.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(12), balance);
        container.Career.ReplacePayload(record, payload);
    }
}
