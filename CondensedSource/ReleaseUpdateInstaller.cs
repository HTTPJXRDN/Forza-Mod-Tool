using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace FH6LocalCryptoTool;

internal sealed record StagedReleaseUpdate(string PlanPath, string HelperPath);

internal static class ReleaseUpdateInstaller
{
    internal sealed class FilePlan
    {
        public string Path { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string? PreviousSha256 { get; set; }
    }
    internal sealed class Plan
    {
        public int Format { get; set; } = 1;
        public string TargetDirectory { get; set; } = "";
        public string Version { get; set; } = "";
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public bool RestartAfterUpdate { get; set; } = true;
        public List<FilePlan> Files { get; set; } = new();
    }
    static readonly HttpClient DownloadClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    internal static string UpdateRoot => System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ForzaModTool", "Updates"));
    static readonly HashSet<string> TopFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "FH6ModStudio.exe", "acl_compressor.exe", "forzatech_acl.dll",
        "NATIVE_RUNTIME.md", "XCOMPRESS-PROVENANCE.md", "SHA256.txt",
    };

    internal static Task<StagedReleaseUpdate> PrepareAsync(ReleaseUpdateResult release, string executable, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default) => PrepareAsync(DownloadClient, release, executable, progress, cancellationToken);

    internal static async Task<StagedReleaseUpdate> PrepareAsync(HttpClient client, ReleaseUpdateResult release, string executable,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (release.Asset is not { } asset) throw new InvalidDataException("This release needs a manual download from GitHub.");
        executable = System.IO.Path.GetFullPath(executable);
        if (!System.IO.Path.GetFileName(executable).Equals("FH6ModStudio.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            throw new InvalidOperationException("Run FH6ModStudio.exe to install an update automatically.");
        string session = System.IO.Path.Combine(UpdateRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        string zipPath = System.IO.Path.Combine(session, "release.zip");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        request.Headers.UserAgent.ParseAdd("ForzaModTool/" + ReleaseUpdates.InstalledVersionText);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != asset.Size) throw new InvalidDataException("Update download size does not match GitHub.");
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            await using var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            byte[] buffer = new byte[81920]; long received = 0; int lastProgress = -1;
            while (true)
            {
                int read = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                if (read == 0) break;
                received += read;
                if (received > asset.Size) throw new InvalidDataException("The update download is larger than expected.");
                hash.AppendData(buffer.AsSpan(0, read));
                await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                int percent = (int)(received * 100 / asset.Size);
                if (percent != lastProgress) { lastProgress = percent; progress?.Report(percent); }
            }
            if (received != asset.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update download did not pass its size and SHA256 checks.");
        }
        return PrepareArchive(zipPath, executable, release.Version);
    }

    internal static StagedReleaseUpdate PrepareArchive(string zipPath, string executable, string version)
    {
        string session = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(zipPath))!;
        ValidateSession(session);
        string target = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(executable))!;
        if (!System.IO.Path.GetFileName(executable).Equals("FH6ModStudio.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable) ||
            !ReleaseUpdates.TryVersion(version, out _)) throw new InvalidDataException("Invalid update destination or version.");
        string payload = System.IO.Path.Combine(session, "files");
        if (Directory.Exists(payload)) throw new InvalidDataException("This update was already prepared.");
        var files = new List<string>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            if (zip.Entries.Count is < 1 or > 200) throw new InvalidDataException("Unexpected update package layout.");
            foreach (var entry in zip.Entries)
            {
                string name = entry.FullName;
                if (name.EndsWith('/') && name == "licenses/") continue;
                if (!Allowed(name) || !names.Add(name) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                    entry.Length is < 0 or > 536_870_912 || (total += entry.Length) > 805_306_368)
                    throw new InvalidDataException("The package contains an unsupported or unsafe file: " + name);
                files.Add(name);
            }
            if (!names.Contains("FH6ModStudio.exe")) throw new InvalidDataException("The app package does not contain FH6ModStudio.exe.");
            foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
            {
                string destination = SafePath(payload, entry.FullName);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
                using var input = entry.Open(); using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[81920]; long written = 0;
                while (true)
                {
                    int read = input.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    written += read;
                    if (written > entry.Length) throw new InvalidDataException("An update file exceeds its declared size.");
                    output.Write(buffer, 0, read);
                }
                if (output.Length != entry.Length) throw new InvalidDataException("An update file did not extract completely.");
            }
        }
        string nextExe = SafePath(payload, "FH6ModStudio.exe");
        if (!Version.TryParse(FileVersionInfo.GetVersionInfo(nextExe).FileVersion, out var binaryVersion) ||
            !ReleaseUpdates.TryVersion(version, out var expectedVersion) || binaryVersion != expectedVersion)
            throw new InvalidDataException("The app inside the ZIP has a different version from its release.");
        using var process = Process.GetCurrentProcess();
        var plan = new Plan { TargetDirectory = target, Version = version, ProcessId = process.Id, ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks };
        foreach (string name in files)
        {
            string destination = SafePath(target, name);
            RejectReparsePoints(target, destination);
            if (Directory.Exists(destination)) throw new InvalidDataException("An update file conflicts with an existing folder: " + name);
            plan.Files.Add(new() { Path = name, Sha256 = Hash(SafePath(payload, name)), PreviousSha256 = File.Exists(destination) ? Hash(destination) : null });
        }
        string planPath = System.IO.Path.Combine(session, "plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
        return new(planPath, nextExe);
    }

    internal static void StartHelper(StagedReleaseUpdate update)
    {
        ValidatePlan(update.PlanPath);
        string expected = SafePath(System.IO.Path.GetDirectoryName(update.PlanPath)!, "files/FH6ModStudio.exe");
        if (!System.IO.Path.GetFullPath(update.HelperPath).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected update helper.");
        var start = new ProcessStartInfo(expected) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = System.IO.Path.GetDirectoryName(expected)! };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(update.PlanPath);
        using var helper = Process.Start(start) ?? throw new IOException("The update installer could not start.");
    }

    internal static int RunHelper(string planPath)
    {
        string? target = null;
        try
        {
            var plan = ValidatePlan(planPath); target = SafePath(plan.TargetDirectory, "FH6ModStudio.exe");
            if (!ReleaseUpdates.TryVersion(plan.Version, out var helperVersion) || ReleaseUpdates.InstalledVersion != helperVersion)
                throw new InvalidDataException("The installer version does not match the prepared update.");
            try
            {
                using var process = Process.GetProcessById(plan.ProcessId);
                if (process.StartTime.ToUniversalTime().Ticks != plan.ProcessStartUtcTicks) throw new InvalidDataException("The original tool process changed; update cancelled.");
                if (!process.WaitForExit(60_000)) throw new IOException("The tool did not close in time; update cancelled.");
            }
            catch (ArgumentException) { } // The old instance has already closed.
            ApplyPrepared(planPath);
            if (plan.RestartAfterUpdate) Restart(target);
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                string session = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(planPath))!;
                ValidateSession(session); File.WriteAllText(System.IO.Path.Combine(session, "error.txt"), ex.ToString());
            }
            catch { }
            MessageBox.Show("The update could not be installed. Any replaced files were restored where possible.\n\n" + ex.Message +
                "\n\nUpdate files and backups:\n" + System.IO.Path.GetDirectoryName(planPath), "Forza Mod Tool update", MessageBoxButton.OK, MessageBoxImage.Error);
            if (target != null && File.Exists(target)) try { Restart(target); } catch { }
            return 1;
        }
    }

    internal static void ApplyPrepared(string planPath)
    {
        var plan = ValidatePlan(planPath);
        string session = System.IO.Path.GetDirectoryName(planPath)!;
        string payload = System.IO.Path.Combine(session, "files"), backup = System.IO.Path.Combine(session, "backup");
        if (Directory.Exists(backup)) throw new InvalidDataException("This update has already started; keep its backup.");
        // Validate the entire installation before replacing any file.
        foreach (var file in plan.Files)
        {
            string destination = SafePath(plan.TargetDirectory, file.Path);
            if (file.PreviousSha256 == null ? File.Exists(destination) : !File.Exists(destination) || Hash(destination) != file.PreviousSha256)
                throw new IOException("A file changed after the update was prepared: " + file.Path);
        }
        Directory.CreateDirectory(backup);
        foreach (var file in plan.Files.Where(f => f.PreviousSha256 != null))
        {
            string copy = SafePath(backup, file.Path); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(copy)!);
            File.Copy(SafePath(plan.TargetDirectory, file.Path), copy, false);
            if (Hash(copy) != file.PreviousSha256) throw new IOException("An update backup failed validation.");
        }
        var applied = new List<FilePlan>();
        try
        {
            foreach (var file in plan.Files)
            {
                InstallFile(SafePath(payload, file.Path), SafePath(plan.TargetDirectory, file.Path)); applied.Add(file);
                if (Hash(SafePath(plan.TargetDirectory, file.Path)) != file.Sha256) throw new IOException("An installed update file failed validation.");
            }
            File.WriteAllText(System.IO.Path.Combine(session, "result.txt"), "Installed version " + plan.Version);
        }
        catch (Exception failure)
        {
            var errors = new List<string>();
            foreach (var file in applied.AsEnumerable().Reverse())
            {
                try
                {
                    string destination = SafePath(plan.TargetDirectory, file.Path);
                    if (file.PreviousSha256 != null) InstallFile(SafePath(backup, file.Path), destination);
                    else if (File.Exists(destination)) File.Delete(destination); // Only a new file created by this attempt.
                }
                catch (Exception ex) { errors.Add(file.Path + ": " + ex.Message); }
            }
            throw new IOException(errors.Count == 0 ? "The update failed; previous files were restored. " + failure.Message :
                "Some files need restoring from the update backup. " + string.Join("; ", errors), failure);
        }
    }

    static Plan ValidatePlan(string planPath)
    {
        planPath = System.IO.Path.GetFullPath(planPath);
        string session = System.IO.Path.GetDirectoryName(planPath)!; ValidateSession(session);
        if (System.IO.Path.GetFileName(planPath) != "plan.json" || new FileInfo(planPath).Length > 100_000) throw new InvalidDataException("Invalid update plan.");
        var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(planPath)) ?? throw new InvalidDataException("Missing update plan.");
        if (plan.Format != 1 || !ReleaseUpdates.TryVersion(plan.Version, out _) || !System.IO.Path.IsPathFullyQualified(plan.TargetDirectory) ||
            plan.Files.Count is < 1 or > 200 || !plan.Files.Any(f => f.Path == "FH6ModStudio.exe") ||
            plan.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Files.Count)
            throw new InvalidDataException("Invalid update plan layout.");
        foreach (var file in plan.Files)
        {
            if (!Allowed(file.Path) || !Regex.IsMatch(file.Sha256, "^[A-F0-9]{64}$") ||
                file.PreviousSha256 is { } old && !Regex.IsMatch(old, "^[A-F0-9]{64}$")) throw new InvalidDataException("Invalid planned file.");
            string source = SafePath(System.IO.Path.Combine(session, "files"), file.Path);
            RejectReparsePoints(session, source); RejectReparsePoints(plan.TargetDirectory, SafePath(plan.TargetDirectory, file.Path));
            if (!File.Exists(source) || Hash(source) != file.Sha256) throw new InvalidDataException("A staged update file changed: " + file.Path);
        }
        return plan;
    }

    static bool Allowed(string path) => TopFiles.Contains(path) || Regex.IsMatch(path, @"^licenses/[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);
    static string SafePath(string root, string relative)
    {
        string fullRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        string value = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, relative));
        if (!value.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe update path.");
        return value;
    }
    static void ValidateSession(string session)
    {
        if (!System.IO.Path.GetDirectoryName(session)!.Equals(UpdateRoot, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(System.IO.Path.GetFileName(session), "N", out _) || (File.GetAttributes(session) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Unexpected update staging folder.");
    }
    static void RejectReparsePoints(string root, string path)
    {
        var item = path;
        while (!item.Equals(System.IO.Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
        {
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("An update path uses a link.");
            item = System.IO.Path.GetDirectoryName(item) ?? throw new InvalidDataException("Invalid update path.");
        }
    }
    static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    static void InstallFile(string source, string destination)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        string incoming = destination + ".updating-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, incoming, false);
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(incoming, destination, true); break; }
                catch (IOException) when (attempt < 20) { Thread.Sleep(150); }
            }
        }
        finally { if (File.Exists(incoming)) File.Delete(incoming); }
    }
    static void Restart(string executable) => Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(executable)! });
}
