#if PROFILE_UNLOCK_TESTING
using System.Buffers.Binary;
using System.IO;
using System.Globalization;

namespace FH6LocalCryptoTool;

public static partial class PlaylistPointsState
{
    public sealed record Playlist(uint Series, string Name, int Points, int Maximum, bool Supported, string Reason)
    {
        public string Display => $"Series {Series} · {Name} ({Points:n0}/{Maximum:n0})";
        public bool NeedsInitialization { get; init; }
        public bool NeedsScoreRepair { get; init; }
    }
    public sealed record Completion(uint Series, int Activities, int Steps, int Points, int HistoryPointsAdded = 0, int HistoryPoints = 0);
    public static bool TryReadPlaylists(ProfileContainer? container, out Playlist[] playlists, out string reason)
    {
        playlists = Array.Empty<Playlist>(); reason = "Playlist activity records are not recognized.";
        if (!TryRecord(container, out var record) || !TryParse(record!.Payload, out var state, out reason)) return false;
        if (!TryActivities(record.Payload, state!, out var activities))
        { reason = "Playlist activity layout is not recognized; saved progress stays unchanged."; return false; }
        var definitions = ProfileGameData.For(container)?.Playlists ?? Array.Empty<PlaylistActivityCatalog.Series>();
        playlists = definitions.Select(d => d.Id).Concat(activities!.Maxima.Keys).Concat(state!.Series.Keys).Where(id => id != 0).Distinct()
            .OrderDescending().Select(id =>
            {
                var definition = definitions.SingleOrDefault(d => d.Id == id);
                bool supported = CanComplete(state, activities!, definition, id, out string error);
                int? savedMaximum = activities!.Maxima.TryGetValue(id, out var max) ? max.Maximum : null;
                int maximum = definition?.Maximum ?? savedMaximum ?? 0;
                return new Playlist(id, definition?.Name ?? "Playlist", state.Series.GetValueOrDefault(id)?.Value ?? 0, maximum, supported, error)
                {
                    NeedsInitialization = !state.Series.ContainsKey(id) || state.Seasons.Keys.Count(k => k.Series == id) != 4 ||
                        activities.Series.SingleOrDefault(s => s.Series == id)?.Seasons.Count != 4,
                    NeedsScoreRepair = !TrackScoresMatch(state, id) ||
                        definition != null && savedMaximum != definition.Maximum
                };
            }).ToArray();
        reason = ""; return true;
    }

    /// <summary>Completes the selected Series, adding missing progress from matching game definitions.
    /// Reward-claim flags, dates, account data and online result submissions are never changed.</summary>
    public static bool TryCompletePlaylist(ProfileContainer? container, uint series, out Completion? completion, out string reason)
    {
        completion = null; reason = "Playlist activity records are not recognized; nothing was changed.";
        if (!TryRecord(container, out var original) || !TryParse(original!.Payload, out var state, out reason)) return false;
        if (!TryActivities(original.Payload, state!, out var activityState))
        { reason = "Playlist activity layout is not recognized; nothing was changed."; return false; }
        var definition = ProfileGameData.For(container)?.Playlists.SingleOrDefault(d => d.Id == series);
        if (!CanComplete(state!, activityState!, definition, series, out reason)) return false;
        if (!TryHistoryTotal(container!, out var historyNode, out int oldHistory))
        { reason = "Playlist History points are not safely recognized; nothing was changed."; return false; }
        int activityCount = 0, stepCount = 0;
        var target = activityState!.Series.SingleOrDefault(s => s.Series == series);
        if (target == null) { target = new(series, new()); activityState.Series.Add(target); }
        foreach (var season in definition!.Seasons)
        {
            var saved = target.Seasons.SingleOrDefault(s => s.Index == season.Index);
            if (saved == null) { saved = new(season.Index, new()); target.Seasons.Add(saved); }
            foreach (var activity in season.Activities)
            {
                var group = saved.Groups.SingleOrDefault(g => g.Type == activity.Type);
                if (group == null) { group = new(activity.Type, new()); saved.Groups.Add(group); }
                var old = group.Entries.SingleOrDefault(e => e.Id == activity.Id);
                int changedSteps = old == null || old.Values.Length != activity.Stages ? activity.Stages : old.Values.Count(v => v < 1);
                if (changedSteps > 0)
                {
                    var next = new ActivityProgress(activity.Id, Enumerable.Repeat(1f, activity.Stages).ToArray());
                    if (old == null) group.Entries.Add(next); else group.Entries[group.Entries.IndexOf(old)] = next;
                    activityCount++; stepCount += changedSteps;
                }
            }
        }
        long delta = (long)definition.Maximum - (state!.Series.GetValueOrDefault(series)?.Value ?? 0);
        long lifetime = state.Tail.Lifetime + delta;
        if (lifetime is < 0 or > int.MaxValue) { reason = "Playlist total would overflow; nothing was changed."; return false; }
        // Keep independent historical awards, add only the new Series points,
        // and repair an old history total below the verified saved lifetime.
        // Never decrement history or touch reward-claim/checkpoint records.
        long history = Math.Max((long)oldHistory + Math.Max(0, delta), lifetime);
        if (history > int.MaxValue) { reason = "Playlist History points would overflow; nothing was changed."; return false; }
        var tracks = state.Tail.Series.Select(s => new TrackSeries(s.Id, s.Tracks.ToList())).ToList();
        var oldTracks = tracks.SingleOrDefault(s => s.Id == series);
        // Completing every verified scoring activity replaces legacy unattributed
        // (-1) cache points; retaining them would count those same activities twice.
        var nextTracks = new TrackSeries(series, new() { new(BaseTrack, definition.Maximum) });
        if (oldTracks == null) tracks.Add(nextTracks); else tracks[tracks.IndexOf(oldTracks)] = nextTracks;
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            WriteCompletionScores(w, original.Payload, state, definition);
            WriteCompletionMaxima(w, original.Payload, state, activityState, definition);
            WriteActivities(w, activityState.Series);
            w.Write(original.Payload.AsSpan(activityState.End, state.Tail.Offset - activityState.End));
            w.Write((uint)tracks.Count);
            foreach (var item in tracks) { w.Write(item.Id); w.Write((uint)item.Tracks.Count); foreach (var track in item.Tracks) { w.Write(track.Id); w.Write(track.Points); } }
            WriteCompletionMetadata(w, state.Tail, series, (int)lifetime);
        }
        byte[] edited = stream.ToArray();
        if (!TryParse(edited, out var check, out reason) || !TryActivities(edited, check!, out var nextState) ||
            !CanComplete(check!, nextState!, definition, series, out reason) ||
            check!.Series[series].Value != definition.Maximum || !TrackScoresMatch(check, series) ||
            !nextState!.Maxima.TryGetValue(series, out var checkedMaximum) || checkedMaximum.Maximum != definition.Maximum ||
            definition.Seasons.Any(s => check.Seasons[(series, s.Index)].Value != s.Maximum))
        { reason = "Playlist completion readback failed; nothing was changed."; return false; }
        foreach (var season in definition.Seasons)
            foreach (var activity in season.Activities)
            {
                var saved = nextState!.Series.Single(s => s.Series == series).Seasons.Single(s => s.Index == season.Index)
                    .Groups.Single(g => g.Type == activity.Type).Entries.Single(e => e.Id == activity.Id);
                if (saved.Values.Any(v => v != 1)) { reason = "Playlist activity validation failed; nothing was changed."; return false; }
            }
        var staged = ProfileContainer.Parse(container!.Serialize());
        ProfileGameData.Copy(container, staged);
        staged.Career!.ReplacePayload(staged.Career.Records.Single(r => r.Name == Name), edited);
        if (history != oldHistory)
        {
            if (!TryHistoryTotal(staged, out var nextHistoryNode, out _) ||
                !staged.SaveState!.SetAttribute(nextHistoryNode!, "value", history.ToString(CultureInfo.InvariantCulture))) return false;
        }
        byte[] serialized = staged.Serialize();
        var verifiedProfile = ProfileContainer.Parse(serialized);
        if (!verifiedProfile.Serialize().AsSpan().SequenceEqual(serialized) ||
            !TryHistoryTotal(verifiedProfile, out _, out int verifiedHistory) || verifiedHistory != history)
        { reason = "Edited profile did not round-trip; nothing was changed."; return false; }
        // Commit only after every check. No live file or other profile record is touched.
        if (!original.Payload.AsSpan().SequenceEqual(edited)) container.Career!.ReplacePayload(original, edited);
        if (history != oldHistory) container.SaveState!.SetAttribute(historyNode!, "value", history.ToString(CultureInfo.InvariantCulture));
        completion = new(series, activityCount, stepCount, definition.Maximum, checked((int)history - oldHistory), (int)history); reason = ""; return true;
    }

    static bool TryHistoryTotal(ProfileContainer container, out Bxml.Node? total, out int points)
    {
        total = null; points = 0;
        if (container.SaveState is not { } bx) return false;
        const string key = "CurrencyBucket_CollectionCategoryProgress_cc_festival_playlist";
        var maps = bx.Root.Children.Where(n => bx.Strings[n.NameIndex] == "map_element" &&
            n.Children.Any(c => bx.Strings[c.NameIndex] == "key" && bx.GetAttribute(c, "value") == key)).ToArray();
        if (maps.Length != 1 || maps[0].Children.Count != 2) return false;
        var values = maps[0].Children.Where(n => bx.Strings[n.NameIndex] == "value" && bx.GetAttribute(n, "type") == "CurrencyBucket").ToArray();
        if (values.Length != 1 || values[0].Attributes.Count != 1 || values[0].Children.Count != 3) return false;
        Bxml.Node? Field(string id, string fieldType)
        {
            var nodes = values[0].Children.Where(n => bx.Strings[n.NameIndex] == "property" && bx.GetAttribute(n, "id") == id).ToArray();
            return nodes.Length == 1 && bx.GetAttribute(nodes[0], "type") == fieldType &&
                nodes[0].Attributes.Count == 3 && nodes[0].Children.Count == 0 ? nodes[0] : null;
        }
        var name = Field("Name", "string");
        var type = Field("Type", "CurrencyTypes");
        total = Field("Total", "int");
        return name != null && type != null && total != null && bx.GetAttribute(name, "value") == "cc_festival_playlist" &&
            bx.GetAttribute(type, "value") == "CollectionCategoryProgress" &&
            int.TryParse(bx.GetAttribute(total, "value"), NumberStyles.None, CultureInfo.InvariantCulture, out points) && points >= 0;
    }

    static bool CanComplete(State state, ActivityState activities, PlaylistActivityCatalog.Series? definition, uint series, out string reason)
    {
        reason = "Select the matching ObjectModelGame.zip to load Playlist challenges.";
        if (definition == null) return false;
        reason = "Playlist definitions are incomplete; nothing was changed.";
        if (series == 0 || definition.Id != series || definition.Seasons.Length != 4 ||
            !definition.Seasons.Select(s => s.Index).Order().SequenceEqual(new uint[] { 0, 1, 2, 3 })) return false;
        reason = "This Playlist has an unsupported points track. Nothing was changed.";
        if (!activities.Maxima.TryGetValue(series, out var maximum) || !maximum.Tracks.ContainsKey(BaseTrack) ||
            maximum.Tracks.GetValueOrDefault(BaseTrack) != maximum.Maximum || maximum.Tracks.Any(t => t.Key != BaseTrack && t.Value != 0)) return false;
        if ((state.Series.GetValueOrDefault(series)?.Value ?? 0) > definition.Maximum)
        { reason = "Saved Playlist points exceed the matching maximum; nothing was changed."; return false; }
        // The game can retain retired activities and old stage counts after a
        // schedule changes. Complete only the current definition's exact IDs;
        // leave unmatched saved records intact instead of treating them as new tasks.
        reason = ""; return true;
    }

    static void WriteCompletionMaxima(BinaryWriter w, byte[] original, State state, ActivityState activities, PlaylistActivityCatalog.Series definition)
    {
        // Copy the cache before updating only the selected Series' maximum and
        // base-track maximum. Preserve opaque version tokens and other Series.
        byte[] cache = original[state.ContentOffset..activities.Start];
        var r = new Reader(cache);
        uint count = r.Count(MaxSeries);
        for (int i = 0; i < count; i++)
        {
            uint id = r.U32(); _ = r.U32(); _ = r.U32();
            int maximumOffset = r.Offset; _ = r.U32();
            uint tracks = r.Count(64);
            for (int j = 0; j < tracks; j++)
            {
                int track = unchecked((int)r.U32()); int pointsOffset = r.Offset; _ = r.U32();
                if (id == definition.Id && track == BaseTrack) Put(cache, pointsOffset, definition.Maximum);
            }
            if (id == definition.Id) Put(cache, maximumOffset, definition.Maximum);
        }
        w.Write(cache);
    }

    static void WriteCompletionScores(BinaryWriter w, byte[] original, State state, PlaylistActivityCatalog.Series definition)
    {
        // Keep the version and both reward-claim maps exactly as loaded.
        w.Write(original.AsSpan(0, state.ScoreMapsOffset));
        var scores = state.Series.ToDictionary(p => p.Key, p => p.Value.Value);
        scores[definition.Id] = definition.Maximum;
        w.Write((uint)scores.Count);
        foreach (var item in scores) { w.Write(item.Key); w.Write(item.Value); }
        var roots = state.SeasonRoots.ToList();
        if (!roots.Contains(definition.Id)) roots.Add(definition.Id);
        w.Write((uint)roots.Count);
        foreach (uint id in roots)
        {
            w.Write(id);
            if (id == definition.Id)
            {
                w.Write((uint)definition.Seasons.Length);
                foreach (var season in definition.Seasons) { w.Write(season.Index); w.Write(season.Maximum); }
            }
            else
            {
                var seasons = state.Seasons.Where(p => p.Key.Series == id).ToArray();
                w.Write((uint)seasons.Length);
                foreach (var season in seasons) { w.Write(season.Key.Season); w.Write(season.Value.Value); }
            }
        }
    }

    static void WriteCompletionMetadata(BinaryWriter w, Tail tail, uint series, int lifetime)
    {
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(tail.Metadata));
        var seen = Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(tail.Metadata.AsSpan(4 + i * 4))).ToList();
        if (!seen.Contains(series)) seen.Add(series);
        w.Write((uint)seen.Count);
        foreach (uint id in seen) w.Write(id);
        w.Write(lifetime);
        // Lifetime reward claims are independent of which Series has been opened.
        w.Write(tail.Metadata.AsSpan(tail.LifetimeOffset + 4));
    }

    static bool TryActivities(byte[] bytes, State state, out ActivityState? result) =>
        TryActivityMaps(bytes, state.ContentOffset, state.Series, out result) && result!.End <= state.Tail.Offset;

    static void WriteActivities(BinaryWriter w, List<SeriesProgress> series)
    {
        w.Write((uint)series.Count);
        foreach (var item in series)
        {
            w.Write(item.Series); w.Write((uint)item.Seasons.Count);
            foreach (var season in item.Seasons)
            {
                w.Write(season.Index); w.Write((uint)season.Groups.Count);
                foreach (var group in season.Groups)
                {
                    w.Write(group.Type); w.Write((uint)group.Entries.Count);
                    foreach (var activity in group.Entries) { w.Write(activity.Id); w.Write((uint)activity.Values.Length); foreach (float value in activity.Values) w.Write(value); }
                }
            }
        }
    }
}
#endif
