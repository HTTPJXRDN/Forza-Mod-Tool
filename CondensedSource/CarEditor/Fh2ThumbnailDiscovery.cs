using System.Diagnostics;
using System.IO;
using FH6LocalCryptoTool;

namespace FH6CarEditor;

internal static class Fh2ThumbnailDiscovery
{
    internal static IEnumerable<string> Sources(string? sourcePath)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? source in new[] { sourcePath, AppContext.BaseDirectory })
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            DirectoryInfo? directory = new(Directory.Exists(source) ? source : Path.GetDirectoryName(Path.GetFullPath(source))!);
            for (int i = 0; directory != null && directory.Parent != null && i < 12; i++, directory = directory.Parent) roots.Add(directory.FullName);
        }
        foreach (var process in Process.GetProcessesByName("azimuth"))
            using (process)
                try { if (process.MainModule?.FileName is string file) roots.Add(Path.GetDirectoryName(file)!); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            foreach (string name in new[] { "azimuth-windows", "azimuth" }) roots.Add(Path.Combine(drive.Name, name));
        foreach (string root in roots.ToArray())
        {
            string? game = ConfiguredGame(root);
            if (game != null) roots.Add(game);
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
            foreach (string folder in NativeFolders(root))
            {
                if (!seen.Add(folder)) continue;
                string database = Path.Combine(folder, "gamedbRC.slt");
                bool matches = false;
                try { matches = File.Exists(database) && GameDbIdentity.Detect(database) == ForzaGame.Horizon2; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { }
                if (matches) yield return folder;
            }
    }

    internal static IEnumerable<string> NativeFolders(string root)
    {
        foreach (string suffix in new[] { "", "stripped", @"media\stripped", @"Mount\media\stripped", @"Content\media\stripped" })
        {
            string path = Path.Combine(root, suffix);
            if (File.Exists(Path.Combine(path, "RC0.zip"))) yield return Path.GetFullPath(path);
        }
    }

    internal static string? ConfiguredGame(string root)
    {
        string ini = Path.Combine(root, "data", "azimuth.ini");
        try
        {
            if (!File.Exists(ini) || new FileInfo(ini).Length > 128 * 1024) return null;
            bool fh2 = false;
            foreach (string raw in File.ReadLines(ini))
            {
                string line = raw.Trim();
                if (line.StartsWith('[')) { fh2 = line.Equals("[title-1a96545b]", StringComparison.OrdinalIgnoreCase); continue; }
                int equals = line.IndexOf('=');
                if (fh2 && equals >= 0 && line[..equals].Trim().Equals("folder", StringComparison.OrdinalIgnoreCase))
                {
                    string folder = line[(equals + 1)..].Trim().Trim('"');
                    return folder.Length == 0 ? null : Path.GetFullPath(Path.IsPathRooted(folder) ? folder : Path.Combine(root, folder));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return null;
    }
}
