using System.IO;
using System.Windows;

namespace FH6LocalCryptoTool;

public partial class MainWindow
{
    bool StageFh5ProfileInput(string path)
    {
        bool plain = Fh5ProfileCrypto.IsPlainPayload(path);
        bool database = !plain && Fh5ProfileCrypto.IsDatabaseExport(path);
        if (!plain && !database && !Fh5ProfileCrypto.IsEncryptedProfile(path)) return false;
        DbTemplateRow.Visibility = ProfileTemplateRow.Visibility = Visibility.Collapsed;
        StagedText.Text = $"Staged: {Path.GetFileName(path)}   (FH5 save — {(plain ? "decrypted profile" : database ? "decrypted SQLite export" : "Decrypt")})";
        StagedText.Visibility = Visibility.Visible;
        Log(plain || database ? "FH5 profile already decrypted. Save re-encryption/editing is disabled."
            : "Authenticated FH5 save staged. Decrypt exports the complete .decrypted profile. Profile Editor viewing and FH5 Save Swap are disabled.");
        Status("FH5 save staged — decryption only.");
        return true;
    }

    async Task DecryptFh5ProfileFlow(string input)
    {
        // Never add generated files to a live WGS container by default.
        if (_outputDir == null || !Directory.Exists(_outputDir))
        {
            BrowseOutput_Click(this, new RoutedEventArgs());
            if (_outputDir == null || !Directory.Exists(_outputDir)) { Status("Choose an output folder first; original unchanged."); return; }
        }
        string output = UniqueOutputPath(Path.Combine(_outputDir, Path.GetFileName(input) + ".fh5-profile.decrypted"));
        Status("Decrypting and authenticating FH5 save…");
        await Task.Run(() => Fh5ProfileCrypto.Decrypt(input, output));
        Log($"FH5 profile decrypted: {output}. All save sections retained; original unchanged.");
        Log("FH5 save decryption only. Profile Editor viewing, save re-encryption and FH5 Save Swap are disabled.");
        Done(output, "FH5 profile decrypted.");
    }
}
