using System.Buffers.Binary;
using System.IO;

namespace FH6LocalCryptoTool;

/// <summary>Versioned FestivalPass score maps; never completes events or claims rewards.</summary>
public static partial class PlaylistPointsState
{
    const string Name = "FestivalPassSaveState";
    const int BaseTrack = 820, MaxSeries = 4096, MaxBytes = 2_000_000;
    public sealed record Season(uint Series, uint Index, string Name, int Points, int SeriesPoints)
    {
        public string Display => $"Series {Series} · {Name}";
    }
    sealed record Score(int Value, int Offset);
    sealed record Track(int Id, int Points);
    sealed record TrackSeries(uint Id, List<Track> Tracks);
    sealed record Tail(int Offset, List<TrackSeries> Series, byte[] Metadata, int LifetimeOffset, int Lifetime);
    sealed record State(Dictionary<uint, Score> Series, Dictionary<(uint Series, uint Season), Score> Seasons, Tail Tail, int ContentOffset,
        int ScoreMapsOffset, List<uint> SeasonRoots);

    public static bool TryRead(ProfileContainer? container, out Season[] seasons, out string reason)
    {
        seasons = Array.Empty<Season>();
        if (!TryRecord(container, out var record) || !TryParse(record!.Payload, out var state, out reason))
        { reason = "Playlist score maps are not recognized; saved activity and rewards are preserved."; return false; }
        seasons = state!.Seasons.OrderByDescending(p => p.Key.Series).ThenBy(p => p.Key.Season)
            .Select(p => new Season(p.Key.Series, p.Key.Season, new[] { "Summer", "Autumn", "Winter", "Spring" }[p.Key.Season], p.Value.Value, state.Series[p.Key.Series].Value)).ToArray();
        reason = ""; return true;
    }

    public static bool TrySet(ProfileContainer? container, uint series, uint season, int points, out bool changed, out string reason)
    {
        changed = false; reason = "Playlist score maps are not recognized; nothing was changed.";
        if (points < 0 || !TryRecord(container, out var record) || !TryParse(record!.Payload, out var state, out reason)
            || !state!.Seasons.TryGetValue((series, season), out var old)) return false;
        long delta = (long)points - old.Value;
        if (delta == 0) { reason = ""; return true; }
        long nextSeries = state.Series[series].Value + delta, lifetime = state.Tail.Lifetime + delta;
        var target = state.Tail.Series.SingleOrDefault(s => s.Id == series);
        int index = target?.Tracks.FindIndex(t => t.Id == BaseTrack) ?? -1;
        long nextTrack = (index < 0 ? 0 : target!.Tracks[index].Points) + delta;
        if (nextSeries is < 0 or > int.MaxValue || lifetime is < 0 or > int.MaxValue || nextTrack is < 0 or > int.MaxValue)
        { reason = "That score would overflow a total or remove points belonging to another track. Nothing was changed."; return false; }
        byte[] prefix = record!.Payload[..state.Tail.Offset];
        Put(prefix, old.Offset, points); Put(prefix, state.Series[series].Offset, (int)nextSeries);
        var trackSeries = state.Tail.Series.Select(s => new TrackSeries(s.Id, s.Tracks.ToList())).ToList();
        target = trackSeries.SingleOrDefault(s => s.Id == series);
        if (target == null) { target = new(series, new()); trackSeries.Add(target); }
        if (index < 0) target.Tracks.Add(new(BaseTrack, (int)nextTrack));
        else target.Tracks[index] = new(BaseTrack, (int)nextTrack);
        if (nextTrack == 0)
        {
            // A zero contribution is represented by an absent map entry in
            // naturally uninitialized saves. Keeping a synthetic empty Series
            // can otherwise make the surrounding zero-valued suffix ambiguous.
            target.Tracks.RemoveAll(t => t.Id == BaseTrack);
            if (target.Tracks.Count == 0) trackSeries.Remove(target);
        }
        byte[] metadata = (byte[])state.Tail.Metadata.Clone(); Put(metadata, state.Tail.LifetimeOffset, (int)lifetime);
        using var output = new MemoryStream(); using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true))
        {
            writer.Write(prefix); writer.Write((uint)trackSeries.Count);
            foreach (var item in trackSeries) { writer.Write(item.Id); writer.Write((uint)item.Tracks.Count); foreach (var track in item.Tracks) { writer.Write(track.Id); writer.Write(track.Points); } }
            writer.Write(metadata);
        }
        byte[] edited = output.ToArray();
        if (!TryParse(edited, out var verified, out reason) || verified!.Seasons[(series, season)].Value != points
            || verified.Series[series].Value != nextSeries || verified.Tail.Lifetime != lifetime) return false;
        var staged = ProfileContainer.Parse(container!.Serialize());
        staged.Career!.ReplacePayload(staged.Career.Records.Single(r => r.Name == Name), edited);
        byte[] roundtrip = staged.Serialize();
        if (!ProfileContainer.Parse(roundtrip).Serialize().AsSpan().SequenceEqual(roundtrip)) return false;
        container.Career!.ReplacePayload(record, edited); changed = true; reason = ""; return true;
    }

    static bool TryRecord(ProfileContainer? container, out BinaryCareer.Record? record)
    {
        record = null;
        var records = container?.Career?.Records.Where(r => r.Name == Name && r.Serializer == Name).ToArray();
        if (records is not { Length: 1 } || container!.Career!.Records.Count(r => r.Name == Name) != 1) return false;
        record = records[0]; return true;
    }

    static bool TryParse(byte[] bytes, out State? state, out string reason)
    {
        state = null; reason = "Unsupported Festival Playlist score layout.";
        if (bytes.Length is < 40 or > MaxBytes) return false;
        try
        {
            var r = new Reader(bytes); uint version = r.U32();
            if (version is not (2 or 4)) return false;
            // Season reward maps, then Series reward maps. Each reward has a
            // saved u64 ID and three acquisition flags; never rewrite any flag.
            uint count = r.Count(MaxSeries); var roots = new HashSet<uint>();
            for (int i = 0; i < count; i++)
            {
                if (!roots.Add(r.U32())) return false;
                uint seasons = r.Count(4); var keys = new HashSet<uint>();
                for (int j = 0; j < seasons; j++) { uint key = r.U32(); if (key > 3 || !keys.Add(key)) return false; r.Rewards(); }
            }
            count = r.Count(MaxSeries); roots.Clear();
            for (int i = 0; i < count; i++) { if (!roots.Add(r.U32())) return false; r.Rewards(); }
            int scoreMapsOffset = r.Offset;
            count = r.Count(MaxSeries); var series = new Dictionary<uint, Score>();
            for (int i = 0; i < count; i++) { uint id = r.U32(); var score = r.Score(); if (!series.TryAdd(id, score)) return false; }
            count = r.Count(MaxSeries); roots.Clear(); var seasonScores = new Dictionary<(uint, uint), Score>(); var seasonRoots = new List<uint>();
            for (int i = 0; i < count; i++)
            {
                uint id = r.U32(); if (!series.ContainsKey(id) || !roots.Add(id)) return false;
                seasonRoots.Add(id);
                uint seasons = r.Count(4); long sum = 0;
                for (int j = 0; j < seasons; j++) { uint key = r.U32(); var score = r.Score(); if (key > 3 || !seasonScores.TryAdd((id, key), score)) return false; sum += score.Value; }
                if (sum > series[id].Value) return false; // Series-only activities may add extra points.
            }
            if (seasonScores.Count == 0 || series.Values.Sum(s => (long)s.Value) > int.MaxValue) return false;
            // The variable activity section is opaque. Locate the unique FULL
            // suffix grammar, cross-checking every per-track sum and lifetime
            // score against the independently parsed leading score maps. This
            // does not rely on fixed offsets or neighbouring point values.
            int contentOffset = r.Offset;
            Tail? tail = null; int minimum = series.Values.Count(s => s.Value > 0);
            for (int at = r.Offset; at <= bytes.Length - 20; at++)
            {
                uint possible = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
                if (possible < minimum || possible > series.Count) continue;
                if (!TryTail(bytes, at, series, out var candidate)) continue;
                if (tail != null) return false; // Ambiguous data must not be guessed.
                tail = candidate;
            }
            if (tail == null) return false;
            state = new(series, seasonScores, tail, contentOffset, scoreMapsOffset, seasonRoots); reason = ""; return true;
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException or OverflowException) { return false; }
    }

    static bool TryTail(byte[] bytes, int at, Dictionary<uint, Score> scores, out Tail? tail)
    {
        tail = null;
        try
        {
            var r = new Reader(bytes) { Offset = at }; uint count = r.Count(scores.Count);
            var series = new List<TrackSeries>(); var seen = new HashSet<uint>();
            for (int i = 0; i < count; i++)
            {
                uint id = r.U32(); if (!scores.TryGetValue(id, out var score) || !seen.Add(id)) return false;
                uint tracks = r.Count(64); var trackKeys = new HashSet<int>(); var values = new List<Track>(); long sum = 0;
                for (int j = 0; j < tracks; j++) { int key = unchecked((int)r.U32()); int points = r.Score().Value; if (key < -1 || key > 1_000_000 || !trackKeys.Add(key)) return false; values.Add(new(key, points)); sum += points; }
                if (sum != score.Value) return false;
                series.Add(new(id, values));
            }
            if (scores.Any(s => s.Value.Value > 0 && !seen.Contains(s.Key))) return false;
            int metadataStart = r.Offset; uint seenCount = r.Count(scores.Count); seen.Clear();
            if (seenCount == 0) return false;
            for (int i = 0; i < seenCount; i++) { uint id = r.U32(); if (!scores.ContainsKey(id) || !seen.Add(id)) return false; }
            int lifetimeOffset = r.Offset - metadataStart; int lifetime = r.Score().Value;
            if (lifetime != scores.Values.Sum(s => (long)s.Value)) return false;
            r.Rewards(); if (r.Offset != bytes.Length) return false;
            tail = new(at, series, bytes[metadataStart..], lifetimeOffset, lifetime); return true;
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException or OverflowException) { return false; }
    }

    sealed class Reader(byte[] bytes)
    {
        internal int Offset;
        internal uint U32() { if (Offset > bytes.Length - 4) throw new InvalidDataException(); uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(Offset)); Offset += 4; return value; }
        internal uint Count(int max) { uint value = U32(); if (value > max) throw new InvalidDataException(); return value; }
        internal Score Score() { int at = Offset; uint value = U32(); if (value > int.MaxValue) throw new InvalidDataException(); return new((int)value, at); }
        internal void Rewards()
        {
            uint count = Count(10000); var ids = new HashSet<ulong>();
            if (Offset + count * 11L > bytes.Length) throw new InvalidDataException();
            for (int i = 0; i < count; i++) { ulong id = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(Offset)); if (id == 0 || !ids.Add(id)) throw new InvalidDataException(); Offset += 8; for (int flag = 0; flag < 3; flag++) if (bytes[Offset++] > 1) throw new InvalidDataException(); }
        }
    }
    static void Put(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
}
