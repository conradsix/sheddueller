namespace Sheddueller.Dashboard.Tests;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

using Shouldly;

public sealed class DashboardJobListControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_OlderResponseAfterNewQuery_DoesNotReplaceCurrentResultsOrError(bool olderFails)
    {
        var reader = new DeferredReader();
        var updated = 0;
        var controller = Create(reader, () => updated++);
        var older = controller.LoadAsync(new JobInspectionQuery(HandlerContains: "older"));
        var newer = controller.LoadAsync(new JobInspectionQuery(HandlerContains: "newer"));
        var current = new JobInspectionPage([Job("newer")], null, 1);
        reader.Responses[1].SetResult(current);
        await newer;
        if (olderFails)
        {
            reader.Responses[0].SetException(new InvalidOperationException("older query failed"));
        }
        else
        {
            reader.Responses[0].SetResult(new JobInspectionPage([Job("older")], null, 1));
        }

        await older;

        controller.Page.ShouldBeSameAs(current);
        controller.Jobs.ShouldBe(current.Jobs);
        controller.Error.ShouldBeNull();
        controller.IsLoading.ShouldBeFalse();
        updated.ShouldBe(1);
    }

    [Fact]
    public async Task Pagination_LoadThenRefresh_PreservesFiltersAndVisibleResultCount()
    {
        var reader = new DeferredReader();
        var controller = Create(reader);
        var load = controller.LoadAsync(new JobInspectionQuery(HandlerContains: "handler", PageSize: 2));
        reader.Responses[0].SetResult(new JobInspectionPage([Job("first"), Job("second")], "next", 3));
        await load;
        var more = controller.LoadMoreAsync();
        reader.Queries[1].HandlerContains.ShouldBe("handler");
        reader.Queries[1].ContinuationToken.ShouldBe("next");
        reader.Responses[1].SetResult(new JobInspectionPage([Job("third")], null, 3));
        await more;
        controller.Jobs.Count.ShouldBe(3);

        var refresh = controller.RefreshAsync(CancellationToken.None);
        reader.Queries[2].PageSize.ShouldBe(3);
        reader.Queries[2].HandlerContains.ShouldBe("handler");
        reader.Queries[2].ContinuationToken.ShouldBeNull();
        reader.Responses[2].SetResult(new JobInspectionPage([Job("refreshed")], "next", 4));
        await refresh;

        controller.Jobs.ShouldHaveSingleItem().MethodName.ShouldBe("refreshed");
        controller.Page!.TotalCount.ShouldBe(4);
        controller.IsLoadingMore.ShouldBeFalse();
    }

    [Fact]
    public async Task Refresh_OlderResponseAfterFilterChange_DoesNotReplaceCurrentPage()
    {
        var reader = new DeferredReader();
        var controller = Create(reader);
        var refresh = controller.RefreshAsync(CancellationToken.None);
        var load = controller.LoadAsync(new JobInspectionQuery(TagContains: "new"));
        var current = new JobInspectionPage([Job("current")], null, 1);
        reader.Responses[1].SetResult(current);
        await load;
        reader.Responses[0].SetResult(new JobInspectionPage([Job("stale")], null, 1));
        await refresh;

        controller.Page.ShouldBeSameAs(current);
    }

    private static DashboardJobListController Create(DeferredReader reader, Action? updated = null)
      => new(reader, 2, () => { }, updated ?? (() => { }), () => { }, action =>
      {
          action();
          return Task.CompletedTask;
      });

    private static JobInspectionSummary Job(string method)
      => new(Guid.NewGuid(), JobState.Queued, "Service", method, 0, 1, DateTimeOffset.UtcNow, null, 0, 1, [], [], null, null, null, null, null, null, null);

    private sealed class DeferredReader : IJobInspectionReader
    {
        public List<JobInspectionQuery> Queries { get; } = [];

        public List<TaskCompletionSource<JobInspectionPage>> Responses { get; } = [];

        public ValueTask<JobInspectionPage> SearchJobsAsync(JobInspectionQuery query, CancellationToken cancellationToken = default)
        {
            this.Queries.Add(query);
            var response = new TaskCompletionSource<JobInspectionPage>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.Responses.Add(response);
            return new ValueTask<JobInspectionPage>(response.Task);
        }

        public ValueTask<JobInspectionOverview> GetOverviewAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<JobInspectionDetail?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<JobQueuePosition> GetQueuePositionAsync(Guid jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<JobEvent> ReadEventsAsync(Guid jobId, JobEventReadOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
