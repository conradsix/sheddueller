namespace Sheddueller.Dashboard.Internal;

using System.Globalization;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

internal static class DashboardJobActions
{
    /// <summary>
    /// Recognises a pasted job ID in the jobs search box (with or without hyphens or braces).
    /// </summary>
    public static bool TryParseJobId(string? text, out Guid jobId)
    {
        jobId = Guid.Empty;
        var trimmed = text?.Trim();
        return !string.IsNullOrEmpty(trimmed)
          && trimmed.Length >= 32
          && Guid.TryParse(trimmed, out jobId);
    }

    /// <summary>
    /// A job can be canceled while queued, or while running until a cancellation has been requested.
    /// </summary>
    public static bool IsCancellable(JobInspectionSummary job)
      => job.State == JobState.Queued
        || (job.State == JobState.Claimed && job.CancellationRequestedAtUtc is null);

    /// <summary>
    /// Describes the outcome of canceling one or more jobs, for a toast.
    /// </summary>
    public static (string Title, string? Description) DescribeCancellation(IReadOnlyCollection<JobCancellationResult> results)
    {
        var canceled = results.Count(result => result == JobCancellationResult.Canceled);
        var requested = results.Count(result => result == JobCancellationResult.CancellationRequested);
        var finished = results.Count(result => result == JobCancellationResult.AlreadyFinished);
        var missing = results.Count(result => result == JobCancellationResult.NotFound);

        var title = (canceled, requested) switch
        {
            ( > 0, > 0) => string.Create(CultureInfo.InvariantCulture, $"Canceled {Jobs(canceled)}; cancellation requested for {Jobs(requested)}"),
            ( > 0, 0) => string.Create(CultureInfo.InvariantCulture, $"Canceled {Jobs(canceled)}"),
            (0, > 0) => string.Create(CultureInfo.InvariantCulture, $"Cancellation requested for {Jobs(requested)}"),
            _ => results.Count == 1 ? "The job was not canceled" : "No jobs were canceled",
        };

        var notes = new List<string>(3);
        if (requested > 0)
        {
            notes.Add(requested == 1
              ? "The running job stops at its next cancellation check."
              : "Running jobs stop at their next cancellation check.");
        }

        if (finished > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{Jobs(finished)} had already finished."));
        }

        if (missing > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{Jobs(missing)} no longer {(missing == 1 ? "exists" : "exist")}."));
        }

        return (title, notes.Count == 0 ? null : string.Join(' ', notes));
    }

    private static string Jobs(int count)
      => string.Create(CultureInfo.InvariantCulture, $"{DashboardFormat.Count(count)} {(count == 1 ? "job" : "jobs")}");
}
