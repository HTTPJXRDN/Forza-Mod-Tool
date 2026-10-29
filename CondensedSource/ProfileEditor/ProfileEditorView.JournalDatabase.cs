#if PROFILE_UNLOCK_TESTING
#nullable disable
using System.IO;
using FH6LocalCryptoTool;
using Microsoft.Data.Sqlite;

namespace FH6ProfileEditor;

public partial class ProfileEditorView
{
    // The editable table view owns a live SQLite working copy. A Journal action
    // must start with that copy (including pending credit/table edits), and put
    // native Promo changes back there so Export save cannot overwrite them.
    void CaptureJournalDatabase()
    {
        if (_conn == null) throw new InvalidOperationException("Load a profile first.");
        string path = Path.Combine(TempWorkspace.Root, "journal-snapshot-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            { snapshot.Open(); _conn.BackupDatabase(snapshot); }
            _container.Database = File.ReadAllBytes(path);
        }
        finally { DeleteJournalScratch(path); }
    }

    void CommitJournalDatabase(byte[] bytes)
    {
        string path = Path.Combine(TempWorkspace.Root, "journal-commit-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            File.WriteAllBytes(path, bytes);
            using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            snapshot.Open();
            // SQLite's backup writes in a destination transaction. If it fails,
            // the Journal plan has not yet committed any in-memory changes.
            snapshot.BackupDatabase(_conn);
        }
        finally { DeleteJournalScratch(path); }
    }

    static void DeleteJournalScratch(string path)
    {
        foreach (string file in new[] { path, path + "-journal", path + "-wal", path + "-shm" })
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
#endif
