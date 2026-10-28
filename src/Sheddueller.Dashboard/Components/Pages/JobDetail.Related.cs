namespace Sheddueller.Dashboard.Components.Pages;

using System.Diagnostics.CodeAnalysis;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Jobs;

/// <content>
/// The Related tab: prerequisites, continuations, and retries, with each related job's current state.
/// </content>
public partial class JobDetail
{
    private const int RelatedJobLimit = 50;

    private readonly Dictionary<Guid, JobInspectionSummary?> _relatedJobs = [];
    private string? _relatedError;
    private bool _isLoadingRelated;
    private bool _relatedStale;

    // Related jobs are read one by one when the Related tab is shown (the reader has no lookup by many IDs), at most
    // RelatedJobLimit per section. An explicit refresh marks them stale so their states are read again.
    private bool NeedsRelatedJobs()
      => this.ActiveTab == DashboardJobDetailTab.Related
        && this._detail is not null
        && !this._isLoadingRelated
        && this._relatedError is null
        && (this._relatedStale || this.RelatedJobIdsToShow().Any(jobId => !this._relatedJobs.ContainsKey(jobId)));

    private IEnumerable<Guid> RelatedJobIdsToShow()
      => this._detail is null
        ? []
        : RelatedSections(this._detail).SelectMany(section => section.JobIds.Take(RelatedJobLimit)).Distinct();

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed read is shown on the tab; it must not escape the render callback and end the circuit.")]
    private async Task LoadRelatedJobsAsync()
    {
        var jobId = this.JobId;
        IReadOnlyList<Guid> relatedJobIds = this._relatedStale
          ? [.. this.RelatedJobIdsToShow()]
          : [.. this.RelatedJobIdsToShow().Where(relatedJobId => !this._relatedJobs.ContainsKey(relatedJobId))];
        this._relatedStale = false;
        this._isLoadingRelated = true;

        try
        {
            foreach (var relatedJobId in relatedJobIds)
            {
                var related = await this.Reader.GetJobAsync(relatedJobId);
                if (jobId != this.JobId)
                {
                    return;
                }

                this._relatedJobs[relatedJobId] = related?.Summary;
            }
        }
        catch (Exception exception)
        {
            if (jobId == this.JobId)
            {
                this._relatedError = exception.Message;
            }
        }
        finally
        {
            if (jobId == this.JobId)
            {
                this._isLoadingRelated = false;
            }
        }

        this.StateHasChanged();
    }

    private void RetryRelatedJobs()
      => this._relatedError = null;

    private static List<RelatedSection> RelatedSections(JobInspectionDetail detail)
    {
        var sections = new List<RelatedSection>(4);
        AddRelatedSection(sections, "Prerequisites", "This job waits for these jobs to finish.", detail.PrerequisiteJobIds);
        AddRelatedSection(sections, "Continuations", "These jobs wait for this one to finish.", detail.DependentJobIds);
        if (detail.Summary.RetryCloneSourceJobId is { } sourceJobId)
        {
            AddRelatedSection(sections, "Retry of", "This job was created to retry a failed job.", [sourceJobId]);
        }

        AddRelatedSection(sections, "Retried as", "Copies of this job created to retry it.", detail.RetryCloneJobIds);
        return sections;
    }

    private static void AddRelatedSection(
        List<RelatedSection> sections,
        string title,
        string description,
        IReadOnlyList<Guid> jobIds)
    {
        if (jobIds.Count > 0)
        {
            sections.Add(new RelatedSection(title, description, jobIds));
        }
    }

    private sealed record RelatedSection(
        string Title,
        string Description,
        IReadOnlyList<Guid> JobIds);
}
