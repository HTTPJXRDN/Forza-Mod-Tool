#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace FH6LocalCryptoTool;

/// <summary>Local base-career finish/position records. No timing, rewards or online records.</summary>
public static class RaceProgressState
{
    const string RecordName = "CareerState";
    const int Limit = 100_000;
    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    sealed record Mode(string Name, uint Best, byte[] Raw);
    sealed record Entry(uint Id, uint Completions, uint Best, Mode[] Modes, byte[] Raw);
    sealed record State(BinaryCareer.Record Record, uint Version, List<Entry> Races, List<Entry> Collections, byte[] Tail);
    public sealed record Result(bool Present, uint Completions, uint BestPosition, IReadOnlyDictionary<string, uint> BestByOverride);

    public static bool TryRead(ProfileContainer? container, uint race, out Result? result, out string reason)
    {
        result = null;
        if (!TryState(container, out var state, out reason)) return false;
        var entry = state!.Races.SingleOrDefault(e => e.Id == race);
        result = entry is null ? new(false, 0, 0, new Dictionary<string, uint>())
            : new(true, entry.Completions, entry.Best, entry.Modes.ToDictionary(m => m.Name, m => m.Best));
        return true;
    }

    internal static bool TrySync(ProfileContainer container, IEnumerable<ulong> challenges, out int updated, out string reason)
    {
        updated = 0; reason = "";
        var requests = challenges.Distinct().Where(JournalRaceCatalog.ByChallenge.ContainsKey)
            .Select(id => JournalRaceCatalog.ByChallenge[id]).GroupBy(m => (m.Race, m.Collection, m.Mode))
            .Select(g => g.OrderByDescending(m => m.Win).First()).ToArray();
        if (requests.Length == 0) return true;
        if (!TryState(container, out var state, out reason)) return false;
        var changed = new HashSet<uint>();
        foreach (var request in requests)
        {
            // A newly synthesized finish-only result is non-winning. Never
            // downgrade a real existing finish or win. Winning implies finished.
            uint position = request.Win ? 1U : 2U;
            if (Update(state!.Races, request.Race, position, request.Mode) |
                Update(state.Collections, request.Collection, position, null)) changed.Add(request.Race);
        }
        if (changed.Count == 0) return true;
        if (state!.Races.Count > Limit || state.Collections.Count > Limit)
        { reason = "Too many saved career results; nothing was changed."; return false; }
        byte[] bytes = Serialize(state);
        if (!TryParse(state.Record, bytes, out var check) || !Serialize(check!).AsSpan().SequenceEqual(bytes) ||
            !check!.Tail.AsSpan().SequenceEqual(state.Tail))
        { reason = "Career-result validation failed; nothing was changed."; return false; }
        foreach (var request in requests)
        {
            var race = check.Races.Single(e => e.Id == request.Race);
            var collection = check.Collections.Single(e => e.Id == request.Collection);
            if (race.Completions == 0 || collection.Completions == 0 ||
                request.Win && (race.Best != 1 || collection.Best != 1 || race.Modes.Single(m => m.Name == request.Mode).Best != 1))
            { reason = "Career-result readback failed; nothing was changed."; return false; }
        }
        container.Career!.ReplacePayload(state.Record, bytes);
        updated = changed.Count; return true;
    }

    static bool Update(List<Entry> entries, uint id, uint position, string? mode)
    {
        var old = entries.SingleOrDefault(e => e.Id == id);
        byte[] header = old is null ? new byte[24] : old.Raw[..24];
        if (old is null) { Put32(header, 0, id); Put32(header, 4, 1); }
        uint completions = Math.Max(1U, old?.Completions ?? 0);
        uint Best(uint previous) => previous is > 0 and < uint.MaxValue ? Math.Min(previous, position) : position;
        // Finish-only edits preserve an existing valid placing, even if worse
        // than 2nd; 2nd is only the fallback when no result exists.
        uint best = position == 2 && old?.Best is > 0 and < uint.MaxValue ? old.Best : Best(old?.Best ?? 0);
        var modes = old?.Modes.ToList() ?? new List<Mode>();
        if (mode is not null)
        {
            var prior = modes.SingleOrDefault(m => m.Name == mode);
            uint next = position == 2 && prior?.Best is > 0 and < uint.MaxValue ? prior.Best : Best(prior?.Best ?? 0);
            byte[] raw;
            if (prior is null)
            {
                byte[] key = StrictUtf8.GetBytes(mode); raw = new byte[8 + key.Length];
                Put32(raw, 0, (uint)key.Length); key.CopyTo(raw, 4);
            }
            else raw = (byte[])prior.Raw.Clone();
            Put32(raw, raw.Length - 4, next);
            var replacement = new Mode(mode, next, raw);
            if (prior is null) modes.Add(replacement); else modes[modes.IndexOf(prior)] = replacement;
        }
        Put32(header, 8, completions); Put32(header, 12, best);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), (ulong)modes.Count);
        using var stream = new MemoryStream(); stream.Write(header); foreach (var value in modes) stream.Write(value.Raw);
        byte[] bytes = stream.ToArray();
        if (old is not null && old.Raw.AsSpan().SequenceEqual(bytes)) return false;
        var entry = new Entry(id, completions, best, modes.ToArray(), bytes);
        if (old is not null) entries[entries.IndexOf(old)] = entry;
        else { int index = entries.FindIndex(e => e.Id > id); entries.Insert(index < 0 ? entries.Count : index, entry); }
        return true;
    }

    static bool TryState(ProfileContainer? container, out State? state, out string reason)
    {
        state = null; reason = "The saved race-result layout is not supported; no Journal or race changes were made.";
        if (container?.Career is not { } career || container.SaveState is not { } bx) return false;
        var records = career.Records.Where(r => r.Name == RecordName).ToArray();
        var maps = bx.Root.Children.Where(n => bx.Strings[n.NameIndex] == "map_element" &&
            n.Children.Count(c => bx.Strings[c.NameIndex] == "key" && bx.GetAttribute(c, "value") == RecordName) == 1).ToArray();
        if (records is not { Length: 1 } || records[0].Serializer != RecordName || maps.Length != 1) return false;
        var values = maps[0].Children.Where(n => bx.Strings[n.NameIndex] == "value").ToArray();
        if (values.Length != 1 || bx.GetAttribute(values[0], "type") != RecordName) return false;
        if (!TryParse(records[0], records[0].Payload, out state)) return false;
        reason = ""; return true;
    }

    static bool TryParse(BinaryCareer.Record record, byte[] bytes, out State? state)
    {
        state = null;
        try
        {
            int at = 0;
            uint Read32() { if (at > bytes.Length - 4) throw new InvalidDataException(); uint n = U32(bytes, at); at += 4; return n; }
            uint version = Read32(); if (version is not (1 or 2)) return false;
            List<Entry> Map()
            {
                uint count = Read32(); if (count > Limit || count > (bytes.Length - at) / 24) throw new InvalidDataException();
                var entries = new List<Entry>(); uint? previous = null;
                for (int i = 0; i < count; i++)
                {
                    int start = at; uint id = Read32(), inner = Read32(), completed = Read32(), best = Read32();
                    if (inner != 1 || previous is { } last && id <= last || best > 10_000 && best != uint.MaxValue) throw new InvalidDataException();
                    previous = id; uint modesLow = Read32(), modesHigh = Read32();
                    if (modesHigh != 0 || modesLow > 1024) throw new InvalidDataException();
                    var modes = new List<Mode>(); var names = new HashSet<string>(StringComparer.Ordinal);
                    for (int j = 0; j < modesLow; j++)
                    {
                        int begin = at; uint length = Read32(); if (length is < 1 or > 256 || length > bytes.Length - at - 4) throw new InvalidDataException();
                        string name = StrictUtf8.GetString(bytes, at, (int)length); at += (int)length; uint place = Read32();
                        if (name.Contains('\0') || !names.Add(name) || place > 10_000 && place != uint.MaxValue) throw new InvalidDataException();
                        modes.Add(new(name, place, bytes[begin..at]));
                    }
                    entries.Add(new(id, completed, best, modes.ToArray(), bytes[start..at]));
                }
                return entries;
            }
            var races = Map(); var collections = Map();
            // Championship/bookkeeping data follows these two known maps.
            // Preserve it verbatim; do not manufacture timing or history there.
            if (bytes.Length - at < 4) return false;
            var parsed = new State(record, version, races, collections, bytes[at..]);
            if (!Serialize(parsed).AsSpan().SequenceEqual(bytes)) return false;
            state = parsed; return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException) { return false; }
    }

    static byte[] Serialize(State state)
    {
        using var stream = new MemoryStream();
        void Write32(uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); stream.Write(b); }
        Write32(state.Version); Write32((uint)state.Races.Count); foreach (var e in state.Races) stream.Write(e.Raw);
        Write32((uint)state.Collections.Count); foreach (var e in state.Collections) stream.Write(e.Raw);
        stream.Write(state.Tail); return stream.ToArray();
    }
    static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    static void Put32(byte[] b, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), value);
}
#endif
