using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// - This class takes care of monitoring Radarr/Sonarr instances
///   for stuck queue items which usually require manual intervention.
/// - NzbDAV can be configured to automatically remove these stuck items,
///   optionally block these stuck items, and optionally trigger a new
///   search for these stuck items.
/// </summary>
public class ArrMonitoringService : BackgroundService
{
    private readonly ConfigManager _configManager;

    public ArrMonitoringService(ConfigManager configManager)
    {
        _configManager = configManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Ensure delay runs on each iteration
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

                // if all queue-actions are disabled, then do nothing
                var arrConfig = _configManager.GetArrConfig();
                if (arrConfig.QueueRules.All(x => x.Action == ArrConfig.QueueAction.DoNothing))
                    continue;

                // otherwise, handle stuck queue items according to the config
                foreach (var arrClient in arrConfig.GetArrClients())
                    await HandleStuckQueueItems(arrConfig, arrClient, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Log.Error(e, "Unexpected error in Arr queue monitoring loop.");
            }
        }
    }

    private async Task HandleStuckQueueItems(ArrConfig arrConfig, ArrClient client, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var queueStatus = await client.GetQueueStatusAsync(timeout.Token).ConfigureAwait(false);
            if (queueStatus is { Warnings: false, UnknownWarnings: false }) return;
            var queue = await client.GetQueueAsync(timeout.Token).ConfigureAwait(false);
            foreach (var stuckDownload in GroupStuckRecordsByDownload(queue.Records, arrConfig))
                await HandleStuckDownload(stuckDownload, arrConfig, client, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException { InnerException: System.Net.Sockets.SocketException })
        {
            Log.Debug(e, "Could not reach Arr instance {Host} for queue monitoring", client.Host);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error occurred while monitoring queue for {Host}", client.Host);
        }
    }

    /// <summary>
    /// Selects the queue records that a configured rule applies to, and groups them by the
    /// download they belong to. A single download can occupy several queue records -- sonarr
    /// lists a season pack as one record per episode, all sharing one download-id -- and the
    /// arr removes a download as a whole, so acting per-record would fire one delete per
    /// episode for a single release. Records with no download-id cannot be attributed to a
    /// download and so keep a group of their own, preserving per-record behaviour.
    /// </summary>
    public static List<List<ArrQueueRecord>> GroupStuckRecordsByDownload(
        IEnumerable<ArrQueueRecord> records,
        ArrConfig arrConfig
    )
    {
        var actionableStatuses = arrConfig.QueueRules.Select(x => x.Message).ToList();
        return records
            .Where(x => actionableStatuses.Any(x.HasStatusMessage))
            .GroupBy(x => string.IsNullOrWhiteSpace(x.DownloadId)
                ? $"record:{x.Id}"
                : $"download:{x.DownloadId}")
            .Select(x => x.ToList())
            .ToList();
    }

    /// <summary>
    /// Since there may be multiple status messages -- spread across the several queue records
    /// that make up a single download -- multiple actions may apply. In such case, always
    /// perform the strongest action. The arr removes a download as a whole, so there is no way
    /// to apply a weaker action to one of its records without also removing its siblings;
    /// anything less than the max would leave the release in a state the config says it
    /// should not survive in.
    /// </summary>
    public static ArrConfig.QueueAction DecideQueueAction(
        IEnumerable<ArrQueueRecord> records,
        ArrConfig arrConfig
    ) => records
        .SelectMany(record => arrConfig.QueueRules.Where(rule => record.HasStatusMessage(rule.Message)))
        .Select(rule => rule.Action)
        .DefaultIfEmpty(ArrConfig.QueueAction.DoNothing)
        .Max();

    private async Task HandleStuckDownload(
        IReadOnlyList<ArrQueueRecord> records,
        ArrConfig arrConfig,
        ArrClient client,
        CancellationToken ct
    )
    {
        if (records.Count == 0) return;

        var action = DecideQueueAction(records, arrConfig);
        if (action is ArrConfig.QueueAction.DoNothing) return;

        // one delete is enough: the arr resolves the record back to its download and
        // removes, blocklists and re-searches the whole thing, dropping the siblings.
        var item = records[0];
        await client.DeleteQueueRecord(item.Id, action).ConfigureAwait(false);
        Log.Warning(
            "Resolved stuck queue item {QueueItemTitle} from {Host} with action {Action}, covering {QueueRecordCount} queue record(s)",
            item.Title,
            client.Host,
            action,
            records.Count);
    }
}
