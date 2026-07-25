using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class ArrMonitoringGroupingTests
{
    private const string ImportFailed = "One or more episodes expected in this release were not imported";
    private const string NotAnUpgrade = "Not an upgrade for existing episode file";

    private static ArrConfig Config(params (string Message, ArrConfig.QueueAction Action)[] rules) => new()
    {
        QueueRules = rules
            .Select(x => new ArrConfig.QueueRule { Message = x.Message, Action = x.Action })
            .ToList(),
    };

    private static ArrQueueRecord Record(int id, string? downloadId, params string[] messages) => new()
    {
        Id = id,
        Title = $"Some.Series.S01E{id:D2}",
        DownloadId = downloadId,
        StatusMessages = [new ArrQueueStatusMessage { Messages = messages.ToList() }],
    };

    [Fact]
    public void SeasonPack_SharingOneDownloadId_CollapsesToASingleAction()
    {
        // sonarr lists a season pack as one record per episode, all sharing a download-id
        var records = Enumerable.Range(1, 22)
            .Select(i => Record(i, "abc123", ImportFailed))
            .ToList();

        var groups = ArrMonitoringService.GroupStuckRecordsByDownload(
            records, Config((ImportFailed, ArrConfig.QueueAction.RemoveAndBlocklistAndSearch)));

        var group = Assert.Single(groups);
        Assert.Equal(22, group.Count);
    }

    [Fact]
    public void RecordsOfOneDownload_TakeTheStrongestActionInTheGroup()
    {
        // the arr removes a download as a whole, so the weaker record's action cannot be
        // applied in isolation -- the group must act on the strongest rule that matched.
        var arrConfig = Config(
            (NotAnUpgrade, ArrConfig.QueueAction.Remove),
            (ImportFailed, ArrConfig.QueueAction.RemoveAndBlocklistAndSearch));

        var records = new List<ArrQueueRecord>
        {
            Record(1, "abc123", NotAnUpgrade),
            Record(2, "abc123", ImportFailed),
        };

        Assert.Equal(
            ArrConfig.QueueAction.RemoveAndBlocklistAndSearch,
            ArrMonitoringService.DecideQueueAction(records, arrConfig));
    }

    [Fact]
    public void ARecordMatchingOnlyADoNothingRule_DoesNotWeakenItsSiblings()
    {
        // a DoNothing rule still makes a record "actionable" for grouping purposes, so the
        // first record of a group can be one that on its own warrants nothing at all.
        var arrConfig = Config(
            (NotAnUpgrade, ArrConfig.QueueAction.DoNothing),
            (ImportFailed, ArrConfig.QueueAction.RemoveAndBlocklist));

        var records = new List<ArrQueueRecord>
        {
            Record(1, "abc123", NotAnUpgrade),
            Record(2, "abc123", ImportFailed),
        };

        Assert.Equal(
            ArrConfig.QueueAction.RemoveAndBlocklist,
            ArrMonitoringService.DecideQueueAction(records, arrConfig));
    }

    [Fact]
    public void RecordsWithoutADownloadId_KeepPerRecordBehaviour()
    {
        var records = new List<ArrQueueRecord>
        {
            Record(1, null, ImportFailed),
            Record(2, "", ImportFailed),
            Record(3, "   ", ImportFailed),
        };

        var groups = ArrMonitoringService.GroupStuckRecordsByDownload(
            records, Config((ImportFailed, ArrConfig.QueueAction.Remove)));

        Assert.Equal(3, groups.Count);
        Assert.All(groups, x => Assert.Single(x));
    }

    [Fact]
    public void ADownloadIdShapedLikeTheFallbackKey_DoesNotCollideWithARecordId()
    {
        var records = new List<ArrQueueRecord>
        {
            Record(5, null, ImportFailed),
            Record(9, "record:5", ImportFailed),
        };

        var groups = ArrMonitoringService.GroupStuckRecordsByDownload(
            records, Config((ImportFailed, ArrConfig.QueueAction.Remove)));

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void DistinctDownloads_AreStillActionedIndependently()
    {
        var records = new List<ArrQueueRecord>
        {
            Record(1, "abc123", ImportFailed),
            Record(2, "abc123", ImportFailed),
            Record(3, "def456", ImportFailed),
        };

        var groups = ArrMonitoringService.GroupStuckRecordsByDownload(
            records, Config((ImportFailed, ArrConfig.QueueAction.Remove)));

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, x => x.Count == 2);
        Assert.Contains(groups, x => x.Count == 1);
    }

    [Fact]
    public void RecordsMatchingNoRule_AreNotActionedAtAll()
    {
        var records = new List<ArrQueueRecord>
        {
            Record(1, "abc123", "Some unrelated status"),
        };

        var groups = ArrMonitoringService.GroupStuckRecordsByDownload(
            records, Config((ImportFailed, ArrConfig.QueueAction.Remove)));

        Assert.Empty(groups);
    }
}
