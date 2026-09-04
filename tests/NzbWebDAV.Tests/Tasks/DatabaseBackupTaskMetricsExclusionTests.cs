using Microsoft.Data.Sqlite;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Backup;
using NzbWebDAV.Services;
using NzbWebDAV.Tasks;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Tasks;

[Collection(nameof(ConfigPathCollection))]
public sealed class DatabaseBackupTaskMetricsExclusionTests : IDisposable
{
    private readonly string? _previousConfigPath;
    private readonly List<string> _tempRoots = [];

    public DatabaseBackupTaskMetricsExclusionTests()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
    }

    [Fact]
    public async Task Backup_WithAMetricsDatabasePresent_DoesNotDumpIt()
    {
        CreateConfigRoot();
        CreateSqliteFile(DavDatabaseContext.DatabaseFilePath);
        CreateSqliteFile(MetricsDbContext.DatabaseFilePath);

        var store = new DatabaseBackupStore();
        var task = new DatabaseBackupTask(
            new ConfigManager(),
            new WebsocketManager(),
            store,
            DatabaseBackupKinds.Manual);

        var manifest = await task.RunInternalAsync();

        var backupDir = store.GetBackupDirectory(manifest.Id);
        Assert.True(File.Exists(Path.Join(backupDir, DatabaseBackupStore.DbSqlName)));
        Assert.False(File.Exists(Path.Join(backupDir, DatabaseBackupStore.MetricsSqlName)));
        Assert.DoesNotContain(manifest.Files, x => x.Name == DatabaseBackupStore.MetricsSqlName);
    }

    private static void CreateSqliteFile(string path)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        using var connection = new SqliteConnection(cs);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS Probe (Id INTEGER PRIMARY KEY); INSERT INTO Probe (Id) VALUES (1);";
        command.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private string CreateConfigRoot()
    {
        var root = Path.Join(Path.GetTempPath(), $"nzbdav-backup-metrics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        Environment.SetEnvironmentVariable("CONFIG_PATH", root);
        return root;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        SqliteConnection.ClearAllPools();
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
