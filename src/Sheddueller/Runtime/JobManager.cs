namespace Sheddueller.Runtime;

using Microsoft.Extensions.Logging;

using Sheddueller.Logging;
using Sheddueller.Storage;

internal sealed class JobManager(
    IJobStore store,
    TimeProvider timeProvider,
    ILogger<JobManager> logger) : IJobManager
{
    public async ValueTask<JobCancellationResult> CancelAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var result = await store.CancelAsync(new CancelJobRequest(jobId, timeProvider.GetUtcNow()), cancellationToken)
          .ConfigureAwait(false);
        logger.JobCancellationRequested(jobId, result.ToString());

        return result;
    }

    public async ValueTask<int> CancelQueuedJobsAsync(CancellationToken cancellationToken = default)
    {
        var canceledCount = await store.CancelQueuedJobsAsync(
            new CancelQueuedJobsRequest(timeProvider.GetUtcNow()),
            cancellationToken)
          .ConfigureAwait(false);
        logger.QueuedJobsCanceled(canceledCount);

        return canceledCount;
    }
}
