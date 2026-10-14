#if PROFILE_UNLOCK_TESTING
#nullable disable
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FH6LocalCryptoTool;

namespace FH6ProfileEditor;

public partial class ProfileEditorView
{
    string _unlockTestInputPath;
    bool _hasUnlockTestEdits;
    Func<string, bool> _confirmUnlockTest;

    void AddUnlockTestingControls()
    {
        OverviewPanel.Children.Add(SectionHeader("Unlocks & progression"));
        OverviewPanel.Children.Add(Note("Changes affect the working copy; use Save edited to export a separate file. "
            + "Inventory edits do not claim rewards, alter UGC, or grant account permissions."));

        var badge = _allProps.FirstOrDefault(p => p.Path == "/Main/PlayerCardBadgeId").Node;
        var badgeRecords = _container.Career?.Records.Where(r => r.Name == ProfileUnlockTestState.BadgeRecordName).ToArray();
        var badgeStatus = Note($"Badges — equipped ID: {badge?.DisplayValue() ?? "not found"}. "
            + (badgeRecords?.Length == 1 ? $"Challenge record: {badgeRecords[0].Payload.Length:n0} bytes. " : "Challenge record unavailable/ambiguous. ")
            + "Unlock editing is not enabled: the challenge layout still needs a controlled before/after comparison.");
        badgeStatus.Name = "BadgeUnlockMappingStatus";
        OverviewPanel.Children.Add(badgeStatus);

        AddClothingUnlockControls();
        AddHornUnlockControls();
        AddCollectionJournalControls();
    }

    Button TestButton(string name, string text, bool enabled) => new()
    {
        Name = name, Content = text, Style = (Style)FindResource("PinkButton"),
        HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0),
        IsEnabled = enabled, Opacity = enabled ? 1 : 0.45,
    };

    void AddClothingUnlockControls()
    {
        bool supported = CharacterClothingUnlockState.TryRead(_container, out var info);
        var status = Note(supported
            ? $"Save-managed wardrobe unlocked: {info.Unlocked} / {info.Total - info.EntitlementManaged}. {info.EntitlementManaged} VIP-managed items are preserved unchanged. Covers all clothing and accessory categories."
            : "Clothing inventory layout is not safely recognized; no unlocks will be applied.");
        status.Name = "ClothingUnlockStatus";
        var button = TestButton("UnlockAllClothesButton", "Unlock all clothes", supported);
        button.ToolTip = "Unlocks the 498 save-managed items in the 500-item wardrobe catalog. The VIP helmet/outfit keep their original states; license recognition stays with the game. Equipment, existing NEW flags, identity and progression wristbands are preserved.";
        button.Click += (_, _) =>
        {
            if (!CharacterClothingUnlockState.TryRead(_container, out var preview))
            {
                Fail("unlock all clothes", new InvalidDataException("The clothing inventory is not safely recognized. Nothing was changed."));
                return;
            }
            if (!_confirmUnlockTest($"Unlock all {preview.Total - preview.EntitlementManaged} save-managed clothing items? {preview.Missing} missing entries will be added and {preview.StoredLocked} stored locked entries will be unlocked. "
                + "New grants use the observed 1,1 acquisition flags. Already-owned items, their existing NEW flags and your equipped outfit are preserved. "
                + "The two VIP-managed items keep their original flags. This does not change identity settings, campaign wristbands, challenges, points, reward claims or account/DLC licenses. "
                + "The catalog and save round trips are checked, but the all-clothes action still needs an in-game test.")) return;
            try
            {
                if (!CharacterClothingUnlockState.TryUnlockAll(_container, out var result))
                    throw new InvalidDataException("The clothing inventory is not safely recognized. Nothing was changed.");
                _hasUnlockTestEdits |= result.Changed > 0;
                CharacterClothingUnlockState.TryRead(_container, out var updated);
                status.Text = $"{result.Added} clothing items added; {result.UnlockedExisting} stored locked items unlocked; {updated.Unlocked} / {updated.Total - updated.EntitlementManaged} save-managed items unlocked. VIP items and equipped outfit unchanged. Save edited to export separately.";
                RefreshUnlockTestDisplays($"All-clothes test: {result.Added} items added, {result.UnlockedExisting} existing locked items unlocked; equipment, existing NEW flags, points and challenges preserved");
            }
            catch (Exception ex) { Fail("unlock all clothes", ex); }
        };
        OverviewPanel.Children.Add(button);
        OverviewPanel.Children.Add(status);
    }

    void AddHornUnlockControls()
    {
        bool supported = CarHornUnlockState.TryRead(_container, out var info);
        var status = Note(supported
            ? $"Horns unlocked: {info.Unlocked} / {info.Total}. Includes standard, musical and sound-effect horns."
            : "Horn inventory layout is not safely recognized; no unlocks will be applied.");
        status.Name = "HornUnlockStatus";
        var button = TestButton("UnlockAllHornsButton", "Unlock all horns", supported);
        button.ToolTip = "Adds missing IDs from all 184 horns in the inspected game catalog. Preserves existing horn order and your equipped horn. No wheelspins, credits or challenges are changed.";
        button.Click += (_, _) =>
        {
            if (!CarHornUnlockState.TryRead(_container, out var preview))
            {
                Fail("unlock all horns", new InvalidDataException("The horn inventory is not safely recognized. Nothing was changed."));
                return;
            }
            if (!_confirmUnlockTest($"Unlock all {preview.Total} horns? {preview.Total - preview.Unlocked} missing horns will be added. "
                + "Your equipped horn and existing inventory order are preserved. No wheelspins, credits, points, challenges, reward claims or account licenses are changed. "
                + "The catalog and save round trips are checked, but all-horn availability and NEW indicators still need an in-game test.")) return;
            try
            {
                if (!CarHornUnlockState.TryUnlockAll(_container, out int added))
                    throw new InvalidDataException("The horn inventory is not safely recognized. Nothing was changed.");
                _hasUnlockTestEdits |= added > 0;
                CarHornUnlockState.TryRead(_container, out var updated);
                status.Text = $"{added} horns added; {updated.Unlocked} / {updated.Total} unlocked. Equipped horn unchanged. Save edited to export separately.";
                RefreshUnlockTestDisplays($"All-horns test: {added} horns added; equipped horn, existing order, credits, wheelspins and challenges preserved");
            }
            catch (Exception ex) { Fail("unlock all horns", ex); }
        };
        OverviewPanel.Children.Add(button);
        OverviewPanel.Children.Add(status);
    }

    void RefreshUnlockTestDisplays(string message)
    {
        BuildCareer();
        Log(message + " — Save edited to export a separate file", "ok");
        StatusLine.Text = "Unlock changes applied to the working copy — Save edited to export separately";
    }
}
#endif
