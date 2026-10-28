namespace Sheddueller.Runtime;

/// <summary>
/// Registered with the first job retention hosted service, so a host that adds both the worker and the dashboard runs
/// one retention loop.
/// </summary>
internal sealed class ShedduellerJobRetentionHostedServiceMarker;
