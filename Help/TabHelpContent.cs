using System.IO;
using System.Reflection;
using System.Text;

namespace FH6LocalCryptoTool.Help;

internal sealed record TabHelpTopic(string Id, string Title, string FileName);
internal sealed record TabHelpDocument(string Title, string Text, string Source);

internal static class TabHelpContent
{
    internal static readonly IReadOnlyList<TabHelpTopic> Topics = new[]
    {
        new TabHelpTopic("Crypto", "Crypto", "Crypto.txt"),
        new TabHelpTopic("CarEditor", "Car Editor", "CarEditor.txt"),
        new TabHelpTopic("AnimationSwap", "Animation Swap", "AnimationSwap.txt"),
        new TabHelpTopic("SaveSwap", "Save Swap", "SaveSwap.txt"),
        new TabHelpTopic("ProfileEditor", "Profile Editor", "ProfileEditor.txt"),
        new TabHelpTopic("GarageViewer", "Garage Viewer", "GarageViewer.txt"),
        new TabHelpTopic("DbBrowser", "DB Browser", "DbBrowser.txt"),
    };

    internal static TabHelpTopic? Find(string id) => Topics.FirstOrDefault(t => t.Id == id);

    // Only known topic IDs become filenames. Plain text is displayed literally;
    // it is never interpreted as XAML, HTML, commands or application settings.
    internal static TabHelpDocument Load(string id, string? helpDirectory = null)
    {
        var topic = Find(id) ?? throw new ArgumentException("Unknown help topic.", nameof(id));
        string path = Path.Combine(helpDirectory ?? Path.Combine(AppContext.BaseDirectory, "Help"), topic.FileName);
        try
        {
            // Read on every click, so authoring help does not require restarting.
            Stream? input;
            string source = path;
            try { input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                input = Assembly.GetExecutingAssembly().GetManifestResourceStream("FH6LocalCryptoTool.Help." + topic.FileName);
                source = "Built-in help — editable file: " + path;
            }
            using (input)
                return input == null
                    ? new(topic.Title, "Help for this tab has not been added yet.", "No help file available.")
                    : Read(topic, input, source);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return new(topic.Title, "This help file could not be read. The rest of the tool is still available.\n\n" + ex.Message, path);
        }
    }

    static TabHelpDocument Read(TabHelpTopic topic, Stream input, string source)
    {
        if (input.Length > 1_048_576) throw new InvalidDataException("Help files must be no larger than 1 MB.");
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string text = reader.ReadToEnd();
        return new(topic.Title, string.IsNullOrWhiteSpace(text) ? "Help for this tab has not been added yet." : text, source);
    }
}
