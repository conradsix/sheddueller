namespace Sheddueller.Dashboard.Internal;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

/// <summary>
/// Keeps a list page's state in its URL. Pages write their state with <see cref="NavigateTo"/>, pushing history
/// for discrete changes and replacing it for typing, and are told about location changes they did not make
/// themselves (Back, Forward, or a link to the same page with different query values).
/// </summary>
internal sealed class DashboardLocationTracker : IDisposable
{
    private readonly Lock _gate = new();
    private readonly NavigationManager _navigation;
    private readonly string _path;
    private readonly Action<string> _externalLocationChanged;
    private readonly List<string> _pendingUris = [];
    private bool _disposed;

    public DashboardLocationTracker(
        NavigationManager navigation,
        string path,
        Action<string> externalLocationChanged)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(externalLocationChanged);

        this._navigation = navigation;
        this._path = path;
        this._externalLocationChanged = externalLocationChanged;
        this._navigation.LocationChanged += this.OnLocationChanged;
    }

    /// <summary>
    /// The current location relative to the dashboard base URI.
    /// </summary>
    public string CurrentRelativeUri
      => this._navigation.ToBaseRelativePath(this._navigation.Uri);

    /// <summary>
    /// Writes <paramref name="href"/> to the address bar unless it is already the current (or pending) location.
    /// </summary>
    /// <returns><see langword="true"/> when a navigation was started.</returns>
    public bool NavigateTo(
        string href,
        bool replace)
    {
        var absoluteUri = this._navigation.ToAbsoluteUri(href).AbsoluteUri;
        lock (this._gate)
        {
            if (this._disposed)
            {
                return false;
            }

            var latestUri = this._pendingUris.Count > 0
              ? this._pendingUris[^1]
              : NormalizeAbsoluteUri(this._navigation.Uri);
            if (string.Equals(latestUri, absoluteUri, StringComparison.Ordinal))
            {
                return false;
            }

            this._pendingUris.Add(absoluteUri);
        }

        this._navigation.NavigateTo(href, replace: replace);
        return true;
    }

    public void Dispose()
    {
        lock (this._gate)
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            this._pendingUris.Clear();
        }

        this._navigation.LocationChanged -= this.OnLocationChanged;
    }

    private void OnLocationChanged(
        object? sender,
        LocationChangedEventArgs eventArgs)
    {
        _ = sender;

        lock (this._gate)
        {
            if (this._disposed)
            {
                return;
            }

            if (!DashboardQueryString.IsPath(this._navigation.ToBaseRelativePath(eventArgs.Location), this._path))
            {
                this._pendingUris.Clear();
                return;
            }

            var index = this._pendingUris.IndexOf(NormalizeAbsoluteUri(eventArgs.Location));
            if (index >= 0)
            {
                this._pendingUris.RemoveRange(0, index + 1);
                return;
            }

            this._pendingUris.Clear();
        }

        this._externalLocationChanged(eventArgs.Location);
    }

    private static string NormalizeAbsoluteUri(string uri)
      => new Uri(uri, UriKind.Absolute).AbsoluteUri;
}
