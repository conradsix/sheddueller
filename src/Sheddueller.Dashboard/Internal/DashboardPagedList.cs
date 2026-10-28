namespace Sheddueller.Dashboard.Internal;

using System.Diagnostics.CodeAnalysis;

/// <summary>
/// The loaded rows of a paged list page and the state of loading them. Each <see cref="LoadAsync{TPage}"/> starts a
/// new query version; results and errors of a superseded query are dropped, so a slow earlier read never overwrites
/// a newer one.
/// </summary>
/// <typeparam name="T">The row type.</typeparam>
internal sealed class DashboardPagedList<T>
{
    private readonly List<T> _rows = [];

    public IReadOnlyList<T> Rows
      => this._rows;

    /// <summary>
    /// A result has been shown. Until then the list shows its loading placeholders.
    /// </summary>
    public bool HasPage { get; private set; }

    public string? ContinuationToken { get; private set; }

    /// <summary>
    /// Rows matching the query in total, or <see langword="null" /> before the first result.
    /// </summary>
    public long? TotalCount { get; private set; }

    public bool IsLoading { get; private set; }

    public bool IsLoadingMore { get; private set; }

    /// <summary>
    /// Why the latest query failed; any rows from before it stay shown.
    /// </summary>
    public string? LoadError { get; private set; }

    /// <summary>
    /// Why loading the next page failed; the loaded rows are kept.
    /// </summary>
    public string? LoadMoreError { get; private set; }

    /// <summary>
    /// Rows are shown while a new query runs, so the table shows them as loading.
    /// </summary>
    public bool IsReloading
      => this.IsLoading && this.HasPage;

    public bool CanLoadMore
      => this.ContinuationToken is not null;

    /// <summary>
    /// The current query. A refresh captures it before reading and applies its result only while
    /// <see cref="IsCurrent"/>.
    /// </summary>
    public int QueryVersion { get; private set; }

    public bool IsCurrent(int queryVersion)
      => queryVersion == this.QueryVersion;

    /// <summary>
    /// Runs a new query, superseding any still running. <paramref name="apply"/> runs only if this query is still
    /// current when its result arrives; a failure is kept in <see cref="LoadError"/>.
    /// </summary>
    /// <returns>Whether this query was still current when it finished.</returns>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed read is shown on the page; it must not escape the event handler and end the circuit.")]
    public async Task<bool> LoadAsync<TPage>(
        Func<ValueTask<TPage>> readAsync,
        Action<TPage> apply)
    {
        ArgumentNullException.ThrowIfNull(readAsync);
        ArgumentNullException.ThrowIfNull(apply);

        var queryVersion = ++this.QueryVersion;
        this.LoadError = null;
        this.LoadMoreError = null;
        this.IsLoading = true;

        try
        {
            var page = await readAsync();
            if (queryVersion != this.QueryVersion)
            {
                return false;
            }

            apply(page);
        }
        catch (Exception exception)
        {
            if (queryVersion == this.QueryVersion)
            {
                this.LoadError = exception.Message;
            }
        }
        finally
        {
            if (queryVersion == this.QueryVersion)
            {
                this.IsLoading = false;
            }
        }

        return queryVersion == this.QueryVersion;
    }

    /// <summary>
    /// Reads the page after the loaded rows, unless there is none or a load is already running. <paramref name="apply"/>
    /// runs only if the query has not changed meanwhile; a failure is kept in <see cref="LoadMoreError"/>.
    /// </summary>
    /// <returns>Whether a page was requested.</returns>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed read is shown on the page; it must not escape the event handler and end the circuit.")]
    public async Task<bool> LoadMoreAsync<TPage>(
        Func<string, ValueTask<TPage>> readAsync,
        Action<TPage> apply)
    {
        ArgumentNullException.ThrowIfNull(readAsync);
        ArgumentNullException.ThrowIfNull(apply);

        if (this.ContinuationToken is not { } continuationToken || this.IsLoading || this.IsLoadingMore)
        {
            return false;
        }

        var queryVersion = this.QueryVersion;
        this.LoadMoreError = null;
        this.IsLoadingMore = true;

        try
        {
            var page = await readAsync(continuationToken);
            if (queryVersion == this.QueryVersion)
            {
                apply(page);
            }
        }
        catch (Exception exception)
        {
            if (queryVersion == this.QueryVersion)
            {
                this.LoadMoreError = exception.Message;
            }
        }
        finally
        {
            this.IsLoadingMore = false;
        }

        return true;
    }

    /// <summary>
    /// Shows a fresh result of the query, clearing earlier load errors.
    /// </summary>
    public void Replace(
        IEnumerable<T> rows,
        string? continuationToken,
        long totalCount)
    {
        this.SetRows(rows, continuationToken, totalCount);
        this.LoadError = null;
        this.LoadMoreError = null;
    }

    /// <summary>
    /// Adds the next page after the loaded rows.
    /// </summary>
    public void Append(
        IEnumerable<T> rows,
        string? continuationToken,
        long totalCount)
      => this.SetRows([.. this._rows, .. rows], continuationToken, totalCount);

    /// <summary>
    /// Swaps the shown rows, for example for rows merged from a live refresh, keeping any load errors.
    /// </summary>
    public void SetRows(
        IEnumerable<T> rows,
        string? continuationToken,
        long totalCount)
    {
        ArgumentNullException.ThrowIfNull(rows);

        T[] next = [.. rows];
        this._rows.Clear();
        this._rows.AddRange(next);
        this.ContinuationToken = continuationToken;
        this.TotalCount = totalCount;
        this.HasPage = true;
    }
}
