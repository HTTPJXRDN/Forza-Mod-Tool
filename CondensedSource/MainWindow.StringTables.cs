using System.IO;

namespace FH6LocalCryptoTool;

public partial class MainWindow
{
    async Task DecodeStringTableFlow(string source)
    {
        string output = UniqueOutputPath(Path.Combine(OutputDirFor(source), Path.GetFileName(source) + ".json"));
        if (!StringTableDocument.IsDocument(output))
        {
            string stem = Path.Combine(OutputDirFor(source), Path.GetFileNameWithoutExtension(source));
            int counter = 2;
            do { output = stem + "." + counter++ + ".str.json"; } while (File.Exists(output));
        }
        await Task.Run(() => WriteNewAsset(output, StringTableDocument.Decode(ReadStringTableInput(source))));
        Log("String table decoded. Edit or add names in Entries; keep the other fields. Drop the JSON back and click Re-encrypt.");
        Done(output, "String-table JSON ready.");
    }

    async Task RebuildStringTableFlow(string source)
    {
        string output = UniqueOutputPath(Path.Combine(OutputDirFor(source), Path.GetFileName(source)[..^9] + ".rebuilt.str"));
        await Task.Run(() => WriteNewAsset(output, StringTableDocument.Rebuild(ReadStringTableInput(source))));
        Done(output, "String table rebuilt and verified.");
    }

    static byte[] ReadStringTableInput(string path)
    {
        if (new FileInfo(path).Length > 40_000_000) throw new InvalidDataException("String-table file exceeds the size limit.");
        return File.ReadAllBytes(path);
    }
}
