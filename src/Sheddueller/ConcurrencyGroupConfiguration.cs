namespace Sheddueller;

/// <summary>
/// A group's concurrency configuration, independent of scheduler job occupancy.
/// </summary>
public sealed record ConcurrencyGroupConfiguration(int? DefaultLimit, int? OverrideLimit, int EffectiveLimit);
