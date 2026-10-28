namespace Sheddueller.Worker.Internal;

using Microsoft.Extensions.Options;

using Sheddueller;
using Sheddueller.Runtime;

internal sealed class ShedduellerWorkerStartupValidator(
    IOptions<ShedduellerOptions> options) : IShedduellerStartupValidator
{
    private readonly IOptions<ShedduellerOptions> _options = options;

    public ValueTask ValidateAsync(CancellationToken cancellationToken)
    {
        var value = this._options.Value;

        if (value.NodeId is not null && value.NodeId.Length == 0)
        {
            throw new InvalidOperationException("ShedduellerOptions.NodeId must be null or a non-empty string.");
        }

        if (value.MaxConcurrentExecutionsPerNode <= 0)
        {
            throw new InvalidOperationException("ShedduellerOptions.MaxConcurrentExecutionsPerNode must be positive.");
        }

        if (value.IdlePollingInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("ShedduellerOptions.IdlePollingInterval must be positive.");
        }

        if (value.LeaseDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("ShedduellerOptions.LeaseDuration must be positive.");
        }

        if (value.HeartbeatInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("ShedduellerOptions.HeartbeatInterval must be positive.");
        }

        if (value.HeartbeatInterval >= value.LeaseDuration)
        {
            throw new InvalidOperationException("ShedduellerOptions.HeartbeatInterval must be less than LeaseDuration.");
        }

        if (value.EffectiveStaleNodeThreshold <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("ShedduellerOptions.StaleNodeThreshold must be positive.");
        }

        if (value.EffectiveDeadNodeThreshold <= value.EffectiveStaleNodeThreshold)
        {
            throw new InvalidOperationException("ShedduellerOptions.DeadNodeThreshold must be greater than the stale node threshold.");
        }

        if (value.DeadNodeRetention is { } deadNodeRetention && deadNodeRetention <= value.EffectiveDeadNodeThreshold)
        {
            throw new InvalidOperationException("ShedduellerOptions.DeadNodeRetention must be null or greater than the dead node threshold.");
        }

        return ValueTask.CompletedTask;
    }
}
