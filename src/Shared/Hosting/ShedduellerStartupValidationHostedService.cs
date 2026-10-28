namespace Sheddueller.Hosting;

using Microsoft.Extensions.Hosting;

using Sheddueller.Runtime;

/// <summary>
/// Runs every registered startup validator before the other Sheddueller hosted services start.
/// </summary>
internal sealed class ShedduellerStartupValidationHostedService(
    IEnumerable<IShedduellerStartupValidator> validators) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var validator in validators)
        {
            await validator.ValidateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
      => Task.CompletedTask;
}
