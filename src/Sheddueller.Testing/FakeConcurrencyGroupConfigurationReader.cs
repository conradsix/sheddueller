namespace Sheddueller.Testing;

using Sheddueller.Enqueueing;

/// <summary>
/// In-memory concurrency configuration for application admission and reporting tests.
/// </summary>
public sealed class FakeConcurrencyGroupConfigurationReader : IConcurrencyGroupConfigurationReader
{
    public Dictionary<string, ConcurrencyGroupConfiguration> Groups { get; } = new(StringComparer.Ordinal);

    public ValueTask<ConcurrencyGroupConfiguration> GetConfigurationAsync(string groupKey, CancellationToken cancellationToken = default)
    {
        SubmissionValidator.ValidateConcurrencyGroupKey(groupKey);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(this.Groups.GetValueOrDefault(groupKey) ?? new(null, null, 1));
    }
}
