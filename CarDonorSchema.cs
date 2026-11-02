using System.IO;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

/// <summary>Adapt a private donor copy to compatible destination columns.
/// Existing destination schemas and source donor files are never rewritten.</summary>
public static class CarDonorSchema
{
    public const int PortableOptionsVersion = 0x46020003;
    public static bool IsPortableOptions(string path)
    {
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());db.Open();
        using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA application_id";
        if(Convert.ToInt32(cmd.ExecuteScalar())!=CarRelatedDbExport.ApplicationId)return false;
        cmd.CommandText="PRAGMA user_version";return Convert.ToInt32(cmd.ExecuteScalar())==PortableOptionsVersion;
    }
    public sealed class Prepared : IDisposable
    {
        public string Path { get; }
        public IReadOnlyList<string> Changes { get; }
        private readonly bool _owned;
        internal Prepared(string path, bool owned, List<string> changes)
            => (Path, _owned, Changes) = (path, owned, changes);
        public void Dispose()
        {
            if (!_owned) return;
            foreach (string path in new[] { Path, Path + "-wal", Path + "-shm", Path + "-journal" })
                try { File.Delete(path); } catch { }
        }
    }
    sealed record Column(string Name, string Type, bool Required, string? Default, int Key);
    // These optional additions belong to the supplied water/methanol mod.
    // A stock receiver cannot use them. Never discard unknown schema changes.
    static bool OptionalColumn(string table, string column) =>
        table.Equals("UpgradePresetPackages", StringComparison.OrdinalIgnoreCase) &&
        column.Equals("WaterMethanolInjection", StringComparison.OrdinalIgnoreCase);
    static bool OptionalTable(string table) =>
        table.Equals("List_UpgradeEngineWaterMethanolInjection", StringComparison.OrdinalIgnoreCase);
    static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    static List<Column> Columns(SqliteConnection db, string schema, string table)
    {
        using var cmd=db.CreateCommand();cmd.CommandText=$"PRAGMA {schema}.table_info({Q(table)})";
        using var r=cmd.ExecuteReader();var rows=new List<Column>();
        while(r.Read())rows.Add(new(r.GetString(1),r.GetString(2),r.GetInt32(3)!=0,r.IsDBNull(4)?null:r.GetString(4),r.GetInt32(5)));
        return rows;
    }
    public static Prepared Prepare(string receivingPath,string donorPath,string tempFolder)
    {
        GameDbIdentity.RequireSameGame(receivingPath,donorPath);
        using var receiver=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=receivingPath,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());receiver.Open();
        using var donor=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=donorPath,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());donor.Open();
        using(var a=receiver.CreateCommand()){a.CommandText="ATTACH DATABASE $path AS incoming";a.Parameters.AddWithValue("$path",System.IO.Path.GetFullPath(donorPath));a.ExecuteNonQuery();}
        var candidates=new List<(string Table,string Ddl,List<Column> Source,List<Column> Extra,List<Column> Omitted)>();
        var omittedTables=new List<string>();
        using(var names=receiver.CreateCommand())
        {
            names.CommandText="SELECT b.name FROM incoming.sqlite_master b LEFT JOIN main.sqlite_master a ON a.name=b.name COLLATE NOCASE AND a.type='table' WHERE b.type='table' AND a.name IS NULL";
            using var r=names.ExecuteReader();while(r.Read())if(OptionalTable(r.GetString(0)))omittedTables.Add(r.GetString(0));
        }
        using(var names=receiver.CreateCommand())
        {
            names.CommandText="SELECT a.name,a.sql FROM main.sqlite_master a JOIN incoming.sqlite_master b ON a.name=b.name COLLATE NOCASE WHERE a.type='table' AND b.type='table' AND a.name NOT LIKE 'sqlite_%'";
            var tables=new List<(string Name,string Ddl)>();using(var r=names.ExecuteReader())while(r.Read())tables.Add((r.GetString(0),r.GetString(1)));
            foreach(var (table,ddl) in tables)
            {
                if(MergeVersionInfo.IsProtectedTable(table))continue;
                var target=Columns(receiver,"main",table);var source=Columns(receiver,"incoming",table);
                var omitted=source.Where(c=>!target.Any(t=>t.Name.Equals(c.Name,StringComparison.OrdinalIgnoreCase))).ToList();
                var extra=target.Where(c=>!source.Any(s=>s.Name.Equals(c.Name,StringComparison.OrdinalIgnoreCase))).ToList();
                if(extra.Count==0 && omitted.Count==0)continue;
                if(omitted.Any(c=>c.Key!=0 || !OptionalColumn(table,c.Name)) || !target.Where(c=>c.Key>0).OrderBy(c=>c.Key).Select(c=>c.Name).SequenceEqual(source.Where(c=>c.Key>0).OrderBy(c=>c.Key).Select(c=>c.Name),StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException(table+": incompatible donor columns or primary keys.");
                var retained=source.Except(omitted).ToList();
                foreach(var c in retained)
                    if(!target.First(t=>t.Name.Equals(c.Name,StringComparison.OrdinalIgnoreCase)).Type.Equals(c.Type,StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(table+": incompatible type for "+c.Name);
                if(extra.Any(c=>c.Key!=0 || c.Required && c.Default is null))
                    throw new InvalidDataException(table+": destination requires an additional value without a default.");
                candidates.Add((table,ddl,retained,extra,omitted));
            }
        }
        if(candidates.Count==0 && omittedTables.Count==0)return new(donorPath,false,new());
        Directory.CreateDirectory(tempFolder);string output=System.IO.Path.Combine(tempFolder,"car-donor-compatible-"+Guid.NewGuid().ToString("N")+".sqlite");
        try
        {
            using(var copy=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=output,Pooling=false}.ToString()))
            {
                copy.Open();donor.BackupDatabase(copy);
                using(var c=copy.CreateCommand()){c.CommandText="PRAGMA foreign_keys=OFF; PRAGMA legacy_alter_table=ON;";c.ExecuteNonQuery();}
                using var tx=copy.BeginTransaction();
                foreach(var (table,ddl,source,extra,omitted) in candidates)
                {
                    string scratch="__compatible_source_"+Guid.NewGuid().ToString("N");
                    void Run(string sql){using var c=copy.CreateCommand();c.Transaction=tx;c.CommandText=sql;c.ExecuteNonQuery();}
                    Run($"ALTER TABLE {Q(table)} RENAME TO {Q(scratch)}");Run(ddl);
                    string projection=string.Join(",",source.Select(c=>Q(c.Name)));
                    Run($"INSERT INTO {Q(table)} ({projection}) SELECT {projection} FROM {Q(scratch)}");
                    // Defaults fill only newly added fields; every supplied field
                    // must survive byte/type-exactly, including NULLs and blobs.
                    using(var check=copy.CreateCommand())
                    {
                        check.Transaction=tx;check.CommandText=$"SELECT EXISTS(SELECT {projection} FROM {Q(table)} EXCEPT SELECT {projection} FROM {Q(scratch)}) OR EXISTS(SELECT {projection} FROM {Q(scratch)} EXCEPT SELECT {projection} FROM {Q(table)})";
                        if(Convert.ToInt32(check.ExecuteScalar())!=0)throw new InvalidDataException(table+": adapting the schema changed donor values.");
                    }
                    Run($"DROP TABLE {Q(scratch)}");
                }
                foreach(string table in omittedTables)
                {
                    using var drop=copy.CreateCommand();drop.Transaction=tx;drop.CommandText=$"DROP TABLE {Q(table)}";drop.ExecuteNonQuery();
                }
                tx.Commit();using var integrity=copy.CreateCommand();integrity.CommandText="PRAGMA quick_check";
                if(integrity.ExecuteScalar() as string!="ok")throw new InvalidDataException("Adapted car donor integrity failed.");
            }
            var changes=new List<string>();
            foreach(var c in candidates)
            {
                if(c.Extra.Count!=0)changes.Add(c.Table+": destination-only fields use their schema defaults ("+string.Join(", ",c.Extra.Select(e=>e.Name))+")");
                if(c.Omitted.Count!=0)changes.Add(c.Table+": skipped optional mod fields unavailable in this destination ("+string.Join(", ",c.Omitted.Select(e=>e.Name))+"); original donor retained");
            }
            changes.AddRange(omittedTables.Select(t=>t+": skipped optional mod options unavailable in this destination; original donor retained"));
            return new(output,true,changes);
        }
        catch {try{File.Delete(output);}catch{} throw;}
    }
}
