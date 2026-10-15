#if PROFILE_UNLOCK_TESTING
namespace FH6LocalCryptoTool;

/// <summary>UTC badge dates. Exact series windows are derived only from verified equality-based week objectives.</summary>
public static class BadgeEarnedDates
{
    public sealed record Window(DateTimeOffset StartUtc, DateTimeOffset EndUtc);
    // Installed SeasonsSettings: SeasonSequenceStartDateUTC=1779373800,
    // SeasonDuration=604800000 ms. SeasonScheduleOrderMap independently agrees.
    static readonly DateTimeOffset PlaylistStart = DateTimeOffset.FromUnixTimeSeconds(1779373800);
    static readonly HashSet<uint> PlaylistIds = new() { 68, 69, 71, 72, 73, 74, 75, 76, 97, 98 };

    public static Window? GetWindow(uint badgeId)
    {
        // These ChallengeDataSet OR objectives test Seasons/UnwrappedLSPWeek
        // for equality to four consecutive weeks. EndUtc is exclusive.
        int firstWeek = badgeId switch { 69 => 0, 71 => 4, 73 => 8, 74 => 12, 76 => 16, _ => -1 };
        return firstWeek < 0 ? null : new(PlaylistStart.AddDays(firstWeek * 7), PlaylistStart.AddDays((firstWeek + 4) * 7));
    }

    public static bool IsPlaylist(uint badgeId) => PlaylistIds.Contains(badgeId);

    internal static bool TryChoose(uint badgeId, DateTimeOffset? requested, DateTimeOffset now,
        out DateTimeOffset earnedUtc, out string error)
    {
        earnedUtc = default; error = ""; var window = GetWindow(badgeId);
        if (requested is null && IsPlaylist(badgeId) && window is null)
        { error = $"Badge {badgeId} needs an earned date. Its exact playlist window is not verified; enter a date you have checked in game."; return false; }
        var date = (requested ?? (window is not null && now >= window.EndUtc ? window.EndUtc.AddSeconds(-1) : now)).ToUniversalTime();
        if (date < DateTimeOffset.UnixEpoch.AddTicks(10) || date > now)
        { error = "Earned dates must be after 1970-01-01 and cannot be in the future."; return false; }
        if (IsPlaylist(badgeId) && date < PlaylistStart)
        { error = "Playlist earned dates cannot be before 2026-05-21 14:30 UTC."; return false; }
        if (window is not null && (date < window.StartUtc || date >= window.EndUtc))
        { error = $"Badge {badgeId} must be dated from {window.StartUtc:yyyy-MM-dd HH:mm:ss} UTC to before {window.EndUtc:yyyy-MM-dd HH:mm:ss} UTC."; return false; }
        // Badge 97's older definition uses >= week 20, NOT equality: only its
        // lower bound is known. Never invent a series end, or bypass its revision guard.
        if (badgeId == 97 && date < PlaylistStart.AddDays(20 * 7))
        { error = "Badge 97 cannot be dated before 2026-10-08 14:30 UTC."; return false; }
        earnedUtc = DateTimeOffset.UnixEpoch.AddTicks(((date.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10) * 10);
        return true;
    }
}
#endif
