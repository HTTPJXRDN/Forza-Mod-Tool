#nullable disable
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FH6LocalCryptoTool;
using Microsoft.Win32;

namespace FH6ProfileEditor;

public partial class ProfileEditorView
{
    bool _fh5ReadOnly;
    int _fh5TableOffset;
    long _fh5TableRows;

    void ApplyFh5ReadOnlyMode()
    {
        RowGrid.IsReadOnly = PropGrid.IsReadOnly = StringGrid.IsReadOnly = _fh5ReadOnly;
        SaveBtn.IsEnabled = !_fh5ReadOnly;
        XuidBox.IsReadOnly = _fh5ReadOnly;
        if (_fh5ReadOnly) XuidApplyBtn.IsEnabled = false;
        GridHint.Text = _fh5ReadOnly ? "FH5 · read-only" : "double-click a cell to edit";
        PropEditHint.Text = _fh5ReadOnly ? "FH5 · read-only" : "double-click a value to edit";
        StringEditHint.Text = _fh5ReadOnly ? "FH5 · read-only" : "double-click the Text column to edit";
        Fh5TablePages.Visibility = _fh5ReadOnly ? Visibility.Visible : Visibility.Collapsed;
    }

    string ViewHelpForMode(string normal) => _fh5ReadOnly
        ? "FH5 save · READ-ONLY. Browse database tables, Profile Values, Save State XML and Career records. Search/copy values; editing, SQL commands, account changes and save export are disabled. Unmapped binary records are shown by name/type/size, not guessed."
        : normal;

    void BuildFh5Overview()
    {
        OverviewPanel.Children.Clear();
        OverviewPanel.Children.Add(SectionHeader("FH5 save · read-only"));
        OverviewPanel.Children.Add(Note("Original save unchanged. Use Cars & Data for all database rows (Previous / Next pages), Profile Values for settings/counters, Save State for full XML, and Career for the saved record index. No FH6 editing/unlock actions are enabled."));
        var chips = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        chips.Children.Add(StatChip(_tables.Count.ToString("n0", CultureInfo.InvariantCulture), "Database tables"));
        chips.Children.Add(StatChip(_allProps.Count.ToString("n0", CultureInfo.InvariantCulture), "Profile nodes"));
        foreach (var item in new[] { ("Career_Garage", "Cars in garage"), ("PhotoCaptures", "Promo photos saved"), ("Career_PurchasedParts", "Purchased parts") })
        {
            long count = TableCountOrNeg(item.Item1);
            if (count >= 0) chips.Children.Add(StatChip(count.ToString("n0", CultureInfo.InvariantCulture), item.Item2));
        }
        OverviewPanel.Children.Add(chips);
        OverviewPanel.Children.Add(SectionHeader("Main values"));
        foreach (var pair in _allProps.Where(p => p.Node.IsEditable &&
            (p.Path.StartsWith("/Main/", StringComparison.OrdinalIgnoreCase) || p.Path.Contains("XP", StringComparison.OrdinalIgnoreCase) || p.Path.Contains("Credits", StringComparison.OrdinalIgnoreCase))))
            OverviewPanel.Children.Add(Note(FriendlyPath(pair.Path) + ": " + pair.Node.DisplayValue()));
    }

    void Fh5PreviousRows_Click(object sender, RoutedEventArgs e)
    {
        if (!_fh5ReadOnly || _currentTable == null || _fh5TableOffset == 0) return;
        _fh5TableOffset = Math.Max(0, _fh5TableOffset - RowLimit);
        try { LoadTable(_currentTable); } catch (Exception ex) { Fail("previous page", ex); }
    }

    void Fh5NextRows_Click(object sender, RoutedEventArgs e)
    {
        if (!_fh5ReadOnly || _currentTable == null || _fh5TableOffset + RowLimit >= _fh5TableRows) return;
        _fh5TableOffset += RowLimit;
        try { LoadTable(_currentTable); } catch (Exception ex) { Fail("next page", ex); }
    }
}
