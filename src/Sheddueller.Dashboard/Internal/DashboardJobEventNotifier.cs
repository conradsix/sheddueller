namespace Sheddueller.Dashboard.Internal;

using Sheddueller.Storage;

/// <summary>
/// Feeds job events to the in-process <see cref="DashboardLiveUpdateStream"/>, which dashboard circuits subscribe to.
/// </summary>
internal sealed class DashboardJobEventNotifier(DashboardLiveUpdateStream stream) : IJobEventNotifier
{
    public ValueTask NotifyAsync(
        JobEvent jobEvent,
        CancellationToken cancellationToken = default)
      => stream.NotifyAsync(jobEvent, cancellationToken);
}
