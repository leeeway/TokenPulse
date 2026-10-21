namespace TokenPulse.Core;

/// <summary>Publish scanned logs immediately while the independent model query is pending.</summary>
public static class RefreshPipeline
{
    public static async Task RunAsync<TLogs, TModels>(Func<Task<TLogs>> scan,
        Func<Task<TModels>> models, Func<TLogs, Task> publishLogs, Func<TModels, Task> publishModels)
    {
        var modelTask = models();
        try
        {
            var logs = await scan();
            await publishLogs(logs);
            await publishModels(await modelTask);
        }
        finally
        {
            // Observe a failed/cancelled model query even when scanning or publishing failed first.
            try { await modelTask; } catch { /* The normal path propagates the original failure. */ }
        }
    }
}
