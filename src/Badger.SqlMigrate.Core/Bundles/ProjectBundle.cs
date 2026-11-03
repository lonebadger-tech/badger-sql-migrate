using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Badger.SqlMigrate.Core.Hashing;
using Badger.SqlMigrate.Core.Plan;
using Badger.SqlMigrate.Core.Project;

namespace Badger.SqlMigrate.Core.Bundles;

public sealed record BundleFile(string Path, string Sha256, long Size);

/// <summary>bsm-bundle.json: what a bundle holds and the hashes that prove it was not altered.</summary>
public sealed record BundleManifest
{
    public const int CurrentFormat = 1;

    public required int Format { get; init; }

    public required string Provider { get; init; }

    /// <summary>Chain id of the last change in the bundled plan (null for an empty plan).</summary>
    public string? PlanHead { get; init; }

    public required int Changes { get; init; }

    public required int Repeatables { get; init; }

    public required string ToolVersion { get; init; }

    public required bool IncludesConfig { get; init; }

    public required IReadOnlyList<BundleFile> Files { get; init; }

    /// <summary>SHA-256 over every file path and hash: the bundle's identity.</summary>
    public required string ContentHash { get; init; }
}

/// <summary>
/// A deployable snapshot of a project: bsm.plan, changes/, repeatable/, environments/ scripts and (optionally)
/// bsm.yaml in a zip with a manifest of SHA-256 hashes. Text is stored normalized (no BOM, LF line endings) and
/// entries are sorted with fixed timestamps, so the same project produces the same bundle on every machine.
/// Opening a bundle checks every hash and the plan head before anything can run from it.
/// </summary>
public static class ProjectBundle
{
    public const string ManifestName = "bsm-bundle.json";
    public const string ConfigName = "bsm.yaml";

    private static readonly DateTimeOffset FixedTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Writes a bundle of the project at <paramref name="root"/>. The project must load without problems.</summary>
    public static BundleManifest Create(string root, string providerId, string outputPath, bool includeConfig, string toolVersion)
    {
        root = System.IO.Path.GetFullPath(root);
        var project = MigrationProject.Load(root, providerId);
        if (project.Problems.Count > 0)
        {
            throw new MigrationException("The project has problems; fix them before bundling:\n  " + string.Join("\n  ", project.Problems));
        }

        var paths = new List<string> { PlanFile.FileName };
        foreach (var change in project.Changes)
        {
            paths.AddRange(Files(root, $"{MigrationProject.ChangesFolder}/{change.Name}", "*"));
        }

        paths.AddRange(Files(root, MigrationProject.RepeatableFolder, "*.sql"));
        paths.AddRange(Files(root, MigrationProject.EnvironmentsFolder, "*.sql"));
        if (includeConfig)
        {
            if (!File.Exists(System.IO.Path.Combine(root, ConfigName)))
            {
                throw new MigrationException($"No {ConfigName} in '{root}' to bundle (use --no-config to leave it out).");
            }

            paths.Add(ConfigName);
        }

        var contents = paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(p => (Path: p, Bytes: Encoding.UTF8.GetBytes(ContentHasher.Normalize(File.ReadAllText(System.IO.Path.Combine(root, p), Encoding.UTF8)))))
            .ToList();
        var files = contents.Select(c => new BundleFile(c.Path, Sha256(c.Bytes), c.Bytes.LongLength)).ToList();
        var manifest = new BundleManifest
        {
            Format = BundleManifest.CurrentFormat,
            Provider = providerId,
            PlanHead = project.HeadChainId,
            Changes = project.Changes.Count,
            Repeatables = project.Repeatables.Count,
            ToolVersion = toolVersion,
            IncludesConfig = includeConfig,
            Files = files,
            ContentHash = ContentHash(files),
        };

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(outputPath);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        Add(zip, ManifestName, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
        foreach (var (path, bytes) in contents)
        {
            Add(zip, path, bytes);
        }

        return manifest;
    }

    /// <summary>Reads and checks a bundle without extracting it.</summary>
    public static BundleManifest Verify(string bundlePath) => Read(bundlePath, null);

    /// <summary>Checks a bundle and extracts it into <paramref name="directory"/> (which must be empty or missing).</summary>
    public static BundleManifest Extract(string bundlePath, string directory) => Read(bundlePath, System.IO.Path.GetFullPath(directory));

    private static BundleManifest Read(string bundlePath, string? extractTo)
    {
        if (!File.Exists(bundlePath))
        {
            throw new MigrationException($"Bundle '{bundlePath}' does not exist.");
        }

        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(bundlePath);
        }
        catch (InvalidDataException ex)
        {
            throw new MigrationException($"'{bundlePath}' is not a bsm bundle: {ex.Message}", ex);
        }

        using (zip)
        {
            var manifestEntry = zip.GetEntry(ManifestName) ?? throw new MigrationException($"'{bundlePath}' is not a bsm bundle: {ManifestName} is missing.");
            BundleManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<BundleManifest>(ReadAll(manifestEntry), JsonOptions)
                    ?? throw new MigrationException($"{ManifestName} is empty.");
            }
            catch (JsonException ex)
            {
                throw new MigrationException($"'{bundlePath}': {ManifestName} is invalid: {ex.Message}", ex);
            }

            if (manifest.Format > BundleManifest.CurrentFormat)
            {
                throw new MigrationException($"'{bundlePath}' was made by a newer Badger SQL Migrate (bundle format {manifest.Format}). Upgrade the tool.");
            }

            var problems = new List<string>();
            if (!string.Equals(ContentHash(manifest.Files), manifest.ContentHash, StringComparison.Ordinal))
            {
                problems.Add("the manifest's content hash does not match its file list");
            }

            var listed = manifest.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
            var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName == ManifestName || entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                if (!listed.TryGetValue(entry.FullName, out var file))
                {
                    problems.Add($"{entry.FullName} is not listed in the manifest");
                    continue;
                }

                var bytes = ReadAll(entry);
                if (!string.Equals(Sha256(bytes), file.Sha256, StringComparison.Ordinal))
                {
                    problems.Add($"{entry.FullName} has been modified (hash mismatch)");
                }

                contents[entry.FullName] = bytes;
            }

            foreach (var file in manifest.Files)
            {
                if (!IsSafePath(file.Path))
                {
                    problems.Add($"{file.Path} is not a safe relative path");
                }
                else if (!contents.ContainsKey(file.Path))
                {
                    problems.Add($"{file.Path} is listed in the manifest but missing");
                }
            }

            if (problems.Count > 0)
            {
                throw new MigrationException($"Bundle '{bundlePath}' failed verification:\n  " + string.Join("\n  ", problems));
            }

            if (extractTo is null)
            {
                return manifest;
            }

            if (Directory.Exists(extractTo) && Directory.EnumerateFileSystemEntries(extractTo).Any())
            {
                throw new MigrationException($"Cannot extract the bundle into '{extractTo}': the folder is not empty.");
            }

            foreach (var (path, bytes) in contents)
            {
                var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(extractTo, path));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
            }

            var project = MigrationProject.Load(extractTo, manifest.Provider);
            if (!string.Equals(project.HeadChainId, manifest.PlanHead, StringComparison.Ordinal))
            {
                throw new MigrationException($"Bundle '{bundlePath}' failed verification: its plan head is {project.HeadChainId ?? "(empty)"} but the manifest says {manifest.PlanHead ?? "(empty)"}.");
            }

            return manifest;
        }
    }

    private static IEnumerable<string> Files(string root, string relativeDir, string pattern)
    {
        var dir = System.IO.Path.Combine(root, relativeDir);
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).Select(f => System.IO.Path.GetRelativePath(root, f).Replace('\\', '/'))
            : [];
    }

    private static void Add(ZipArchive zip, string path, byte[] bytes)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = FixedTime;
        using var s = entry.Open();
        s.Write(bytes);
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static bool IsSafePath(string path) =>
        path.Length > 0 && !path.StartsWith('/') && !path.Contains('\\', StringComparison.Ordinal) && !path.Contains(':', StringComparison.Ordinal)
        && path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");

    private static string ContentHash(IReadOnlyList<BundleFile> files) =>
        ContentHasher.HashParts([.. files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => (f.Path, (string?)f.Sha256))]);

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
