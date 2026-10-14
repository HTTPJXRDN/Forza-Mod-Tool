using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

/// <summary>FH6 profile SQLite exports and complete-container round trips, never a GameDB.</summary>
public static class ProfileCrypto
{
    const int MaxFileBytes = 128 * 1024 * 1024;
    public static bool IsPlainPayload(string path) {
        try {
            using var file=File.OpenRead(path);Span<byte> marker=stackalloc byte[4];
            if(file.Length>MaxFileBytes||file.Read(marker)!=4||BinaryPrimitives.ReadUInt32LittleEndian(marker)!=ProfileContainer.MarkerProfile)return false;
            // A matching prefix is only a candidate, never proof of plaintext:
            // an encrypted IV can start with the same bytes as a profile marker.
            file.Position=0;byte[] payload=new byte[checked((int)file.Length)];file.ReadExactly(payload);
            _=ProfileContainer.Parse(payload);return true;
        }
        catch(InvalidDataException) {return false;} catch(IOException) {return false;} catch(UnauthorizedAccessException) {return false;}
    }
    public static bool IsProfileDatabase(string path) {
        try {
            using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);Span<byte> header=stackalloc byte[16];
            if(file.Read(header)!=16||!header.SequenceEqual("SQLite format 3\0"u8))return false;
            using var db=OpenReadOnly(path);return HasProfileSchema(db);
        } catch(SqliteException) {return false;} catch(IOException) {return false;} catch(UnauthorizedAccessException) {return false;}
    }
    // Keep damaged exports on the profile path too: they must fail validation,
    // never fall through to the unrelated GameDB/SLT encryption workflow.
    public static bool IsDatabaseInput(string path) => IsProfileDatabase(path)||
        (Path.GetFileName(path).StartsWith("C_ProfileData",StringComparison.OrdinalIgnoreCase)&&
        new[]{".sqlite",".sqlite3",".db",".db3"}.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase));
    public static bool IsProfilePath(string path) => Path.GetFileName(path).StartsWith("C_ProfileData",StringComparison.OrdinalIgnoreCase)||IsPlainPayload(path)||IsProfileDatabase(path);
    static SqliteConnection OpenReadOnly(string path) {
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
        try {db.Open();return db;}catch {db.Dispose();throw;}
    }
    static bool HasProfileSchema(SqliteConnection db) {
        using var command=db.CreateCommand();
        command.CommandText="SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name COLLATE NOCASE IN ('Career_Garage','Career_PurchasedParts')";
        return Convert.ToInt64(command.ExecuteScalar())==2;
    }
    static void CheckDatabase(SqliteConnection db,bool requireProfileSchema) {
        if(requireProfileSchema&&!HasProfileSchema(db))throw new InvalidDataException("Choose the SQLite database extracted from an FH6 C_ProfileData save, not a GameDB or another database.");
        using var command=db.CreateCommand();command.CommandText="PRAGMA quick_check";
        using var reader=command.ExecuteReader();
        if(!reader.Read()||!string.Equals(reader.GetString(0),"ok",StringComparison.OrdinalIgnoreCase)||reader.Read())throw new InvalidDataException("The profile's SQLite database failed quick_check.");
    }
    static void CleanupDatabase(string path) {
        foreach(string file in new[]{path,path+"-wal",path+"-shm",path+"-journal"})if(File.Exists(file))File.Delete(file);
    }
    static byte[] Read(string path) {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length is < 1 or > MaxFileBytes)throw new InvalidDataException("Profile input exceeds safe size limits.");
        byte[] bytes=new byte[checked((int)file.Length)];file.ReadExactly(bytes);return bytes;
    }
    static byte[] DecryptValidated(byte[] template) {
        var key=Fh6Keys.Get("ProfileData");
        if(!ForzaZip.ValidateContainer(template,key.DataKey,key.MacKey,out _))
            throw new InvalidDataException("This is not an authenticated encrypted FH6 C_ProfileData save. Use the original encrypted save as the profile template, not a GameDB or decrypted file.");
        byte[] payload=ProfileData.Decrypt(template);ValidatePayload(payload);return payload;
    }
    static void ValidatePayload(byte[] payload) {
        if(payload.Length>MaxFileBytes)throw new InvalidDataException("Decrypted profile exceeds safe size limits.");
        var profile=ProfileContainer.Parse(payload);
        string work=Path.Combine(TempWorkspace.Root,"profile-crypto-check-"+Guid.NewGuid().ToString("N")+".sqlite");
        try {
            File.WriteAllBytes(work,profile.Database);
            using var db=OpenReadOnly(work);CheckDatabase(db,false);
        } finally {CleanupDatabase(work);}
    }
    static void VerifyUnchanged(string path,byte[] snapshot) {
        if(!Read(path).AsSpan().SequenceEqual(snapshot))throw new IOException("An input changed during processing; no output was written. Try again with a stable copy.");
    }
    static void WriteNew(string output,byte[] bytes) {
        bool created=false;
        try {using var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None);created=true;file.Write(bytes);}
        catch {if(created&&File.Exists(output))File.Delete(output);throw;}
    }
    public static void ValidateTemplate(string template) => _=DecryptValidated(Read(template));
    public static void Decrypt(string input,string output) {
        byte[] original=Read(input);byte[] payload=DecryptValidated(original);
        VerifyUnchanged(input,original);WriteNew(output,payload);
    }
    /// <summary>Extract just the fourth section as a normal standalone SQLite database.</summary>
    public static void DecryptDatabase(string input,string output) {
        byte[] original=Read(input);var profile=ProfileContainer.Parse(DecryptValidated(original));
        VerifyUnchanged(input,original);WriteNew(output,profile.Database);
    }
    static string DatabaseFingerprint(string path) {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(string file in new[]{path,path+"-wal",path+"-journal"}) {
            bool exists=File.Exists(file);hash.AppendData(new[]{exists?(byte)1:(byte)0});
            if(exists) {using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);hash.AppendData(SHA256.HashData(stream));}
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    static byte[] SnapshotDatabase(string input,string expectedFingerprint) {
        string work=Path.Combine(TempWorkspace.Root,"profile-crypto-snapshot-"+Guid.NewGuid().ToString("N")+".sqlite");
        try {
            using(var source=OpenReadOnly(input)) {
                using var begin=source.CreateCommand();begin.CommandText="BEGIN DEFERRED";begin.ExecuteNonQuery();
                CheckDatabase(source,true);
                using(var target=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=work,Mode=SqliteOpenMode.ReadWriteCreate,Pooling=false}.ToString())) {
                    target.Open();source.BackupDatabase(target);
                    // Fold committed WAL pages into a standalone file, even when
                    // an external SQLite editor still has the source open.
                    using var journal=target.CreateCommand();journal.CommandText="PRAGMA journal_mode=DELETE";journal.ExecuteScalar();
                    CheckDatabase(target,true);
                }
            }
            if(DatabaseFingerprint(input)!=expectedFingerprint)throw new IOException("The profile database changed during processing; no output was written. Save your edits and try again.");
            return Read(work);
        }finally {CleanupDatabase(work);}
    }
    /// <summary>Reinsert an edited SQLite snapshot; keep all three other template sections byte-exact.</summary>
    public static void EncryptDatabase(string input,string template,string output) {
        string fingerprint=DatabaseFingerprint(input);
        byte[] original=Read(template);byte[] originalPayload=DecryptValidated(original);
        var profile=ProfileContainer.Parse(originalPayload);
        profile.Database=SnapshotDatabase(input,fingerprint);
        byte[] payload=profile.Serialize();ValidatePayload(payload);
        byte[] encrypted=EncryptVerified(payload,original);
        if(DatabaseFingerprint(input)!=fingerprint)throw new IOException("The profile database changed during processing; no output was written.");
        VerifyUnchanged(template,original);WriteNew(output,encrypted);
    }
    static byte[] EncryptVerified(byte[] payload,byte[] original) {
        byte[] encrypted=ProfileData.Encrypt(payload,original);
        var key=Fh6Keys.Get("ProfileData");
        if(!ForzaZip.ValidateContainer(encrypted,key.DataKey,key.MacKey,out _)||!ProfileData.Decrypt(encrypted).AsSpan().SequenceEqual(payload))
            throw new CryptographicException("Re-encrypted profile failed authentication or exact payload verification; no output was written.");
        return encrypted;
    }
    public static void Encrypt(string input,string template,string output) {
        byte[] payload=Read(input);ValidatePayload(payload);
        byte[] original=Read(template);_=DecryptValidated(original);
        byte[] encrypted=EncryptVerified(payload,original);
        VerifyUnchanged(input,payload);VerifyUnchanged(template,original);WriteNew(output,encrypted);
    }
}
