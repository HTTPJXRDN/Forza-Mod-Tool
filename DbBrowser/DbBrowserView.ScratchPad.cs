using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace FH6LocalCryptoTool.DbBrowser;

public partial class DbBrowserView
{
    bool _scratchPadBusy;

    void ScratchPadExpansion_Changed(object sender, RoutedEventArgs e)
    {
        if (ScratchPadRow == null || ScratchPadExpander == null) return;
        bool expanded = ScratchPadExpander.IsExpanded;
        ScratchPadRow.MinHeight = expanded ? 155 : 0;
        ScratchPadRow.Height = expanded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
    }

    void SetScratchPadBusy(bool busy)
    {
        _scratchPadBusy = busy;
        ScratchPad.IsEnabled = ImportScratchPadButton.IsEnabled = ExportScratchPadButton.IsEnabled = !busy;
    }

    async void ImportScratchPad_Click(object sender, RoutedEventArgs e)
    {
        if (_scratchPadBusy) return;
        var dialog = new OpenFileDialog { Title = "Import scratch pad", Filter = "Text files (*.txt)|*.txt", DefaultExt = ".txt", CheckFileExists = true };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        if (ScratchPad.Text.Length > 0 && MessageBox.Show(OwnerWindow,
            "Replace the current scratch-pad notes? Export them first if you want to keep them.",
            "Import scratch pad", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SetScratchPadBusy(true);
        try
        {
            string text = await ScratchPadFiles.Read(dialog.FileName);
            ScratchPad.Text = text;
            ScratchPadStatus.Text = "Imported " + Path.GetFileName(dialog.FileName) + " · session only.";
        }
        catch (Exception ex) when (ScratchPadFiles.IsFileError(ex)) { ScratchPadError("Import", ex); }
        finally { SetScratchPadBusy(false); }
    }

    async void ExportScratchPad_Click(object sender, RoutedEventArgs e)
    {
        if (_scratchPadBusy) return;
        var dialog = new SaveFileDialog { Title = "Export scratch pad", Filter = "Text files (*.txt)|*.txt", DefaultExt = ".txt", AddExtension = true, FileName = "scratch-pad.txt", OverwritePrompt = true };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        string text = ScratchPad.Text;
        SetScratchPadBusy(true);
        try
        {
            await ScratchPadFiles.Write(dialog.FileName, text);
            ScratchPadStatus.Text = "Exported " + Path.GetFileName(dialog.FileName) + ".";
        }
        catch (Exception ex) when (ScratchPadFiles.IsFileError(ex)) { ScratchPadError("Export", ex); }
        finally { SetScratchPadBusy(false); }
    }

    void ScratchPadError(string action, Exception error)
    {
        ScratchPadStatus.Text = action + " failed; your notes are unchanged.";
        MessageBox.Show(OwnerWindow, error.Message, action + " scratch pad", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}

internal static class ScratchPadFiles
{
    const int MaxBytes = 4 * 1024 * 1024;
    static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static bool IsFileError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
        NotSupportedException or System.Security.SecurityException;

    static string TextPath(string path)
    {
        if (!Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a .txt file for scratch-pad notes.");
        return Path.GetFullPath(path);
    }

    internal static async Task<string> Read(string path)
    {
        path = TextPath(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        if (input.Length > MaxBytes) throw new InvalidDataException("Scratch-pad files must be no larger than 4 MB.");
        using var reader = new StreamReader(input, Utf8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync();
    }

    internal static async Task Write(string path, string text)
    {
        path = TextPath(path);
        if (Utf8.GetByteCount(text) > MaxBytes) throw new InvalidDataException("Scratch-pad exports must be no larger than 4 MB.");
        string temporary = path + ".scratch-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            using (var writer = new StreamWriter(output, Utf8)) await writer.WriteAsync(text);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
