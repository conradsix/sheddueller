namespace Sheddueller.Dashboard.Tests;

using Sheddueller.Dashboard.Internal;

using Shouldly;

public sealed class DashboardPagedListTests
{
    [Fact]
    public async Task Load_Succeeds_ShowsTheRowsAndPagePosition()
    {
        var list = new DashboardPagedList<string>();

        var current = await list.LoadAsync(() => Read(Page(["a", "b"], "next", 5)), page => Replace(list, page));

        current.ShouldBeTrue();
        list.HasPage.ShouldBeTrue();
        list.Rows.ShouldBe(["a", "b"]);
        list.TotalCount.ShouldBe(5);
        list.CanLoadMore.ShouldBeTrue();
        list.IsLoading.ShouldBeFalse();
        list.LoadError.ShouldBeNull();
    }

    [Fact]
    public async Task Load_NothingShownYet_IsLoadingButNotReloading()
    {
        var list = new DashboardPagedList<string>();
        var read = new TaskCompletionSource<TestPage>();

        var first = list.LoadAsync(() => new ValueTask<TestPage>(read.Task), page => Replace(list, page));
        list.IsLoading.ShouldBeTrue();
        list.IsReloading.ShouldBeFalse();
        read.SetResult(Page(["a"]));
        await first;

        var second = new TaskCompletionSource<TestPage>();
        var reload = list.LoadAsync(() => new ValueTask<TestPage>(second.Task), page => Replace(list, page));
        list.IsReloading.ShouldBeTrue();
        second.SetResult(Page(["b"]));
        await reload;
        list.IsReloading.ShouldBeFalse();
    }

    [Fact]
    public async Task Load_SupersededByANewerQuery_DropsTheOlderResult()
    {
        var list = new DashboardPagedList<string>();
        var older = new TaskCompletionSource<TestPage>();
        var newer = new TaskCompletionSource<TestPage>();

        var olderLoad = list.LoadAsync(() => new ValueTask<TestPage>(older.Task), page => Replace(list, page));
        var newerLoad = list.LoadAsync(() => new ValueTask<TestPage>(newer.Task), page => Replace(list, page));
        newer.SetResult(Page(["newer"]));
        older.SetResult(Page(["older"]));

        (await newerLoad).ShouldBeTrue();
        (await olderLoad).ShouldBeFalse();
        list.Rows.ShouldBe(["newer"]);
        list.IsLoading.ShouldBeFalse();
    }

    [Fact]
    public async Task Load_SupersededQueryFails_ErrorIsNotShown()
    {
        var list = new DashboardPagedList<string>();
        var older = new TaskCompletionSource<TestPage>();
        var newer = new TaskCompletionSource<TestPage>();

        var olderLoad = list.LoadAsync(() => new ValueTask<TestPage>(older.Task), page => Replace(list, page));
        var newerLoad = list.LoadAsync(() => new ValueTask<TestPage>(newer.Task), page => Replace(list, page));
        older.SetException(new InvalidOperationException("stale failure"));
        list.IsLoading.ShouldBeTrue();
        newer.SetResult(Page(["newer"]));

        (await olderLoad).ShouldBeFalse();
        (await newerLoad).ShouldBeTrue();
        list.LoadError.ShouldBeNull();
    }

    [Fact]
    public async Task Load_Fails_KeepsTheShownRowsAndReportsTheError()
    {
        var list = new DashboardPagedList<string>();
        await list.LoadAsync(() => Read(Page(["a"], "next", 2)), page => Replace(list, page));

        var current = await list.LoadAsync<TestPage>(
          () => throw new InvalidOperationException("store offline"),
          page => Replace(list, page));

        current.ShouldBeTrue();
        list.LoadError.ShouldBe("store offline");
        list.HasPage.ShouldBeTrue();
        list.Rows.ShouldBe(["a"]);
        list.IsLoading.ShouldBeFalse();
    }

    [Fact]
    public async Task Load_NewQuery_ClearsTheEarlierLoadMoreError()
    {
        var list = new DashboardPagedList<string>();
        await list.LoadAsync(() => Read(Page(["a"], "next", 2)), page => Replace(list, page));
        await list.LoadMoreAsync<TestPage>(_ => throw new InvalidOperationException("timeout"), page => Append(list, page));
        list.LoadMoreError.ShouldBe("timeout");

        var reload = new TaskCompletionSource<TestPage>();
        var load = list.LoadAsync(() => new ValueTask<TestPage>(reload.Task), page => Replace(list, page));

        list.LoadMoreError.ShouldBeNull();
        reload.SetResult(Page(["b"]));
        await load;
    }

    [Fact]
    public async Task LoadMore_NextPageArrives_AppendsItsRows()
    {
        var list = new DashboardPagedList<string>();
        await list.LoadAsync(() => Read(Page(["a"], "page-2", 3)), page => Replace(list, page));
        string? requestedToken = null;

        var requested = await list.LoadMoreAsync(
          continuationToken =>
          {
              requestedToken = continuationToken;
              return Read(Page(["b", "c"], continuationToken: null, 3));
          },
          page => Append(list, page));

        requested.ShouldBeTrue();
        requestedToken.ShouldBe("page-2");
        list.Rows.ShouldBe(["a", "b", "c"]);
        list.CanLoadMore.ShouldBeFalse();
        list.IsLoadingMore.ShouldBeFalse();
    }

    [Fact]
    public async Task LoadMore_LastPageLoaded_RequestsNothing()
    {
        var list = new DashboardPagedList<string>();
        await list.LoadAsync(() => Read(Page(["a"])), page => Replace(list, page));
        var reads = 0;

        var requested = await list.LoadMoreAsync(
          _ =>
          {
              reads++;
              return Read(Page(["b"]));
          },
          page => Append(list, page));

        requested.ShouldBeFalse();
        reads.ShouldBe(0);
    }

    [Fact]
    public async Task LoadMore_WhileANewQueryLoads_RequestsNothing()
    {
        var list = new DashboardPagedList<string>();
        await list.LoadAsync(() => Read(Page(["a"], "next", 2)), page => Replace(list, page));
        var reload = new TaskCompletionSource<TestPage>();
        var load = list.LoadAsync(() => new ValueTask<TestPage>(reload.Task), page => Replace(list, page));

        var requested = await list.LoadMoreAsync(_ => Read(Page(["b"])), page => Append(list, page));

        requested.ShouldBeFalse();
        reload.SetResult(Page(["c"]));
        await load;
    }

    [Fact]
    public async Task LoadMore_Fails_KeepsTheRowsAndReportsTheError()
    {
        var list = new DashboardPagedList<string>();
        await list.LoadAsync(() => Read(Page(["a"], "next", 2)), page => Replace(list, page));

        await list.LoadMoreAsync<TestPage>(_ => throw new InvalidOperationException("timeout"), page => Append(list, page));

        list.LoadMoreError.ShouldBe("timeout");
        list.LoadError.ShouldBeNull();
        list.Rows.ShouldBe(["a"]);
        list.CanLoadMore.ShouldBeTrue();
        list.IsLoadingMore.ShouldBeFalse();
    }

    [Fact]
    public async Task LoadMore_QueryChangedMeanwhile_DropsThePageAndCanLoadMoreAgain()
    {
        var list = new DashboardPagedList<string>();
        await list.LoadAsync(() => Read(Page(["a"], "next", 2)), page => Replace(list, page));
        var nextPage = new TaskCompletionSource<TestPage>();
        var loadMore = list.LoadMoreAsync(_ => new ValueTask<TestPage>(nextPage.Task), page => Append(list, page));

        await list.LoadAsync(() => Read(Page(["x"], "x-next", 2)), page => Replace(list, page));
        nextPage.SetException(new InvalidOperationException("stale failure"));
        await loadMore;

        list.Rows.ShouldBe(["x"]);
        list.LoadMoreError.ShouldBeNull();
        list.IsLoadingMore.ShouldBeFalse();
        (await list.LoadMoreAsync(_ => Read(Page(["y"])), page => Append(list, page))).ShouldBeTrue();
        list.Rows.ShouldBe(["x", "y"]);
    }

    [Fact]
    public async Task SetRows_AfterAFailedLoad_KeepsTheError()
    {
        var list = new DashboardPagedList<string>();
        await list.LoadAsync(() => Read(Page(["a"], "next", 2)), page => Replace(list, page));
        await list.LoadAsync<TestPage>(() => throw new InvalidOperationException("store offline"), page => Replace(list, page));

        list.SetRows(["a2"], list.ContinuationToken, 4);

        list.Rows.ShouldBe(["a2"]);
        list.TotalCount.ShouldBe(4);
        list.ContinuationToken.ShouldBe("next");
        list.LoadError.ShouldBe("store offline");

        list.Replace(["b"], continuationToken: null, 1);
        list.LoadError.ShouldBeNull();
    }

    [Fact]
    public async Task RefreshResult_CapturedBeforeANewQuery_IsNotCurrent()
    {
        var list = new DashboardPagedList<string>();
        await list.LoadAsync(() => Read(Page(["a"])), page => Replace(list, page));
        var refreshVersion = list.QueryVersion;

        await list.LoadAsync(() => Read(Page(["b"])), page => Replace(list, page));

        list.IsCurrent(refreshVersion).ShouldBeFalse();
        list.IsCurrent(list.QueryVersion).ShouldBeTrue();
    }

    private static TestPage Page(
        IReadOnlyList<string> rows,
        string? continuationToken = null,
        long? totalCount = null)
      => new(rows, continuationToken, totalCount ?? rows.Count);

    private static ValueTask<TestPage> Read(TestPage page)
      => ValueTask.FromResult(page);

    private static void Replace(
        DashboardPagedList<string> list,
        TestPage page)
      => list.Replace(page.Rows, page.ContinuationToken, page.TotalCount);

    private static void Append(
        DashboardPagedList<string> list,
        TestPage page)
      => list.Append(page.Rows, page.ContinuationToken, page.TotalCount);

    private sealed record TestPage(
        IReadOnlyList<string> Rows,
        string? ContinuationToken,
        long TotalCount);
}
