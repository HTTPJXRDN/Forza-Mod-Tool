using System.IO;

namespace FH6LocalCryptoTool;

/// <summary>Tool-owned scratch files under the inherited Windows temporary folder.</summary>
public static class TempWorkspace
{
    static readonly Lazy<string> Workspace = new(Create);
    public static string Root => Workspace.Value;

    static string Create()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ForzaModTool", "Work"));
        Directory.CreateDirectory(root);
        string probe = Path.Combine(root, "write-test-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        return root;
    }

    public static void Initialize() => _ = Root;
}
