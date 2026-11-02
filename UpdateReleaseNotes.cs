using System.IO;
using System.Text;

namespace FH6LocalCryptoTool;

internal static class UpdateReleaseNotes
{
    internal static bool WasUpdated(string[] args, out Version? previous)
    {
        previous = null;
        return args.Length == 2 && args[0] == "--updated-from" &&
            ReleaseUpdates.TryVersion(args[1], out previous) && previous < ReleaseUpdates.InstalledVersion;
    }

    internal static string Since(Version previous)
    {
        using var stream = typeof(UpdateReleaseNotes).Assembly.GetManifestResourceStream("FH6LocalCryptoTool.ReleaseChanges.md");
        if (stream == null) return "The update is installed. You're ready to go.";
        using var reader = new StreamReader(stream);
        return Select(reader.ReadToEnd(), previous, ReleaseUpdates.InstalledVersion);
    }

    internal static string Select(string changelog, Version previous, Version current)
    {
        var result = new StringBuilder();
        bool include = false;
        foreach (string line in changelog.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("# Forza Mod Tool v", StringComparison.Ordinal))
            {
                string tag = line[17..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                include = ReleaseUpdates.TryVersion(tag, out var version) && version > previous && version <= current;
                if (include) result.AppendLine("Version " + tag.TrimStart('v'));
            }
            else if (include)
                result.AppendLine(line.StartsWith("- ") ? "• " + line[2..] : line.TrimStart('#', ' '));
        }
        return result.ToString().Trim() is { Length: > 0 } notes ? notes : "The update is installed. You're ready to go.";
    }
}
