using System.Text.Json;
using NzbWebDAV.Database.Backup;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ConfigPathCollection))]
public sealed class DatabaseBackupStoreMetricsReclaimTests : IDisposable
{
    private readonly string? _previousConfigPath;
    private readonly List<string> _tempRoots = [];

    public DatabaseBackupStoreMetricsReclaimTests()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
    }

    [Fact]
    public void EnsureInitialized_LegacyBackupWithMetricsDump_DropsDumpAndKeepsTheRest()
    {
        CreateConfigRoot();
        var store = new DatabaseBackupStore();
        var backupDir = SeedBackup(store, "20260830-000000000-scheduled-aaaaaa", withMetrics: true);

        store.EnsureInitialized();

        Assert.False(File.Exists(Path.Join(backupDir, DatabaseBackupStore.MetricsSqlName)));
        Assert.True(File.Exists(Path.Join(backupDir, DatabaseBackupStore.DbSqlName)));
        Assert.True(File.Exists(Path.Join(backupDir, DatabaseBackupStore.WardenSqlName)));
    }

    [Fact]
    public void EnsureInitialized_LegacyBackupWithMetricsDump_RewritesManifest()
    {
        CreateConfigRoot();
        var store = new DatabaseBackupStore();
        const string id = "20260830-000000000-scheduled-bbbbbb";
        SeedBackup(store, id, withMetrics: true);

        store.EnsureInitialized();

        var manifest = store.Get(id);
        Assert.NotNull(manifest);
        Assert.DoesNotContain(manifest!.Files, x => x.Name == DatabaseBackupStore.MetricsSqlName);
        Assert.Contains(manifest.Files, x => x.Name == DatabaseBackupStore.DbSqlName);
        Assert.Contains(manifest.Files, x => x.Name == DatabaseBackupStore.WardenSqlName);

        // The backup must remain listable and restorable, not just smaller.
        Assert.Contains(store.List(), x => x.Id == id);
    }

    [Fact]
    public void EnsureInitialized_BackupWithoutMetricsDump_LeavesManifestUntouched()
    {
        CreateConfigRoot();
        var store = new DatabaseBackupStore();
        const string id = "20260830-000000000-scheduled-cccccc";
        var backupDir = SeedBackup(store, id, withMetrics: false);
        var manifestPath = Path.Join(backupDir, DatabaseBackupStore.ManifestFileName);
        var before = File.ReadAllText(manifestPath);

        store.EnsureInitialized();
        store.EnsureInitialized();

        Assert.Equal(before, File.ReadAllText(manifestPath));
    }

    [Fact]
    public void EnsureInitialized_MetricsDumpWithUnreadableManifest_StillReclaimsTheDump()
    {
        CreateConfigRoot();
        var store = new DatabaseBackupStore();
        const string id = "20260830-000000000-scheduled-dddddd";
        var backupDir = SeedBackup(store, id, withMetrics: true);
        File.WriteAllText(Path.Join(backupDir, DatabaseBackupStore.ManifestFileName), "{ not json");

        store.EnsureInitialized();

        Assert.False(File.Exists(Path.Join(backupDir, DatabaseBackupStore.MetricsSqlName)));
    }

    private static string SeedBackup(DatabaseBackupStore store, string id, bool withMetrics)
    {
        var backupDir = Path.Join(store.BackupsRoot, id);
        Directory.CreateDirectory(backupDir);

        var files = new List<DatabaseBackupFileEntry>();
        files.Add(WriteDump(backupDir, DatabaseBackupStore.DbSqlName, "-- db\n"));
        if (withMetrics)
            files.Add(WriteDump(backupDir, DatabaseBackupStore.MetricsSqlName, "-- metrics, and lots of it\n"));
        files.Add(WriteDump(backupDir, DatabaseBackupStore.WardenSqlName, "-- warden\n"));

        var manifest = new DatabaseBackupManifest
        {
            Id = id,
            CreatedAt = DateTimeOffset.UtcNow,
            Kind = DatabaseBackupKinds.Scheduled,
            Files = files,
        };
        File.WriteAllText(
            Path.Join(backupDir, DatabaseBackupStore.ManifestFileName),
            JsonSerializer.Serialize(manifest, DatabaseBackupJson.Options));

        return backupDir;
    }

    private static DatabaseBackupFileEntry WriteDump(string backupDir, string name, string contents)
    {
        var path = Path.Join(backupDir, name);
        File.WriteAllText(path, contents);
        return new DatabaseBackupFileEntry { Name = name, Bytes = new FileInfo(path).Length };
    }

    private string CreateConfigRoot()
    {
        var root = Path.Join(Path.GetTempPath(), $"nzbdav-metrics-reclaim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        Environment.SetEnvironmentVariable("CONFIG_PATH", root);
        return root;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        foreach (var root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // best effort cleanup
            }
            catch (UnauthorizedAccessException)
            {
                // best effort cleanup
            }
        }
    }
}
