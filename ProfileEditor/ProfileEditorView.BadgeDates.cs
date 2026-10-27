#if PROFILE_UNLOCK_TESTING
#nullable disable
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FH6LocalCryptoTool;

namespace FH6ProfileEditor;

public partial class ProfileEditorView
{
    static bool TryBadgeDate(string text, out DateTimeOffset utc) => DateTimeOffset.TryParseExact(text.Trim(),
        new[] { "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFF" },
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);

    void AddBadgeUnlockControls()
    {
        var dateInput = new TextBox
        {
            Name = "BadgeEarnedDateInput", Style = (Style)FindResource("Input"), MinWidth = 255,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 8, 0),
            ToolTip = "UTC: yyyy-MM-dd HH:mm:ss (optional .ffffff). A date alone means midnight UTC.",
        };
        DateTimeOffset? RequestedDate()
        {
            if (string.IsNullOrWhiteSpace(dateInput.Text)) return null;
            if (!TryBadgeDate(dateInput.Text, out var utc)) throw new InvalidDataException("Enter a UTC date as yyyy-MM-dd HH:mm:ss, for example 2026-06-20 18:00:00.");
            return utc;
        }

        AddUnlockPicker("Badge", "UnlockSelectedBadgesButton", "badges",
            () => BadgeUnlockState.TryRead(_container, out var rows)
                ? rows.Select(b => new UnlockItem(b.Id, b.Name, b.Category, b.Unlocked, b.Supported, b.Description,
                    b.Unlocked ? b.EarnedUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "date not recognized" : "")).ToArray() : null,
            selected =>
            {
                if (!BadgeUnlockState.TryUnlockSelected(_container, selected, RequestedDate(), out int changed, out string error)) throw new InvalidDataException(error);
                return (true, changed);
            },
            "Only badges with saved challenge records are listed. Existing dates, equipped badge, Journal and stats stay unchanged. Playlist dates are checked before applying.",
            (panel, list, refresh) =>
            {
                var label = Note("Earned date (UTC)"); label.Margin = new Thickness(4, 8, 0, 0); panel.Children.Add(label);
                var controls = new WrapPanel(); controls.Children.Add(dateInput);
                var setDate = TestButton("BadgeApplyEarnedDateButton", "Set earned date", false);
                setDate.Margin = new Thickness(0, 4, 0, 0); controls.Children.Add(setDate); panel.Children.Add(controls);
                var hint = Note(""); hint.Name = "BadgeEarnedDateHint"; panel.Children.Add(hint);
                var help = Note("Select a badge to see its date. Set earned date changes unlocked badges only. Blank on unlock uses known series dates; other playlist badges need a checked date.");
                panel.Children.Add(help);

                BadgeUnlockState.Row[] Selected()
                {
                    if (!BadgeUnlockState.TryRead(_container, out var current)) return Array.Empty<BadgeUnlockState.Row>();
                    var ids = list.SelectedItems.OfType<UnlockItem>().Select(i => i.Id).ToHashSet();
                    return current.Where(b => ids.Contains(b.Id)).ToArray();
                }
                void EnableDateAction()
                {
                    var selected = Selected();
                    setDate.IsEnabled = selected.Length > 0 && selected.All(b => b.Unlocked && b.Supported) && TryBadgeDate(dateInput.Text, out _);
                    setDate.Opacity = setDate.IsEnabled ? 1 : 0.45;
                }
                void ShowSelection()
                {
                    using var catalogScope = ProfileGameData.Use(_container);
                    var selected = Selected(); dateInput.Text = "";
                    if (selected.Length == 1)
                    {
                        var badge = selected[0];
                        if (badge.Unlocked && badge.EarnedUtc is { } date) dateInput.Text = date.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
                        var window = BadgeEarnedDates.GetWindow(badge.Id);
                        hint.Text = window != null
                            ? $"Valid UTC window: {window.StartUtc:yyyy-MM-dd HH:mm:ss} to before {window.EndUtc:yyyy-MM-dd HH:mm:ss}."
                            : BadgeEarnedDates.IsPlaylist(badge.Id) ? "Exact playlist window not verified. Enter a date checked in game."
                            : "Campaign / Horizon Play: no series date restriction.";
                        if (!badge.Supported) hint.Text += " Definition changed; this badge is read-only.";
                    }
                    else hint.Text = selected.Length == 0 ? "Choose badges above." : "One entered date applies to all selected badges. Different series may need separate dates.";
                    EnableDateAction();
                }
                list.SelectionChanged += (_, _) => ShowSelection();
                dateInput.TextChanged += (_, _) => EnableDateAction();
                setDate.Click += (_, _) =>
                {
                    using var catalogScope = ProfileGameData.Use(_container);
                    try
                    {
                        var selected = Selected(); var requested = RequestedDate();
                        if (requested is null || selected.Length == 0 || selected.Any(b => !b.Unlocked || !b.Supported)) return;
                        foreach (var badge in selected)
                            if (!BadgeEarnedDates.TryChoose(badge.Id, requested, DateTimeOffset.UtcNow, out _, out string error)) throw new InvalidDataException(error);
                        if (!_confirmUnlockTest($"Set the earned date of {selected.Length} selected unlocked badges to {requested:yyyy-MM-dd HH:mm:ss.ffffff} UTC? Only their dates change. Export save writes a separate file.")) return;
                        if (!BadgeUnlockState.TrySetEarnedDate(_container, selected.Select(b => (ulong)b.Id), requested.Value, out int changed, out string failure)) throw new InvalidDataException(failure);
                        _hasUnlockTestEdits |= changed > 0; refresh();
                        RefreshUnlockTestDisplays($"Badges: {changed} earned dates updated");
                    }
                    catch (Exception ex) { Fail("set badge earned date", ex); }
                };
                ShowSelection();
            },
            selected =>
            {
                if (!BadgeUnlockState.TryPlanUnlockDates(_container, selected, RequestedDate(), out var dates, out string error)) throw new InvalidDataException(error);
                var playlist = dates.Where(d => d.Playlist).ToArray();
                string summary = "\n\nNew earned dates (UTC):\n" + string.Join("\n", playlist.Select(d => $"{d.Name}: {d.Utc:yyyy-MM-dd HH:mm:ss}"));
                int other = dates.Length - playlist.Length;
                if (other > 0) summary += (playlist.Length > 0 ? "\n" : "") + $"{other} campaign / Horizon Play: {(RequestedDate() is { } date ? date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "current time")}.";
                if (playlist.Any(d => BadgeEarnedDates.GetWindow(d.Id) is null)) summary += "\nUnverified playlist windows: confirm your entered date against the game.";
                return summary;
            });
    }
}
#endif
