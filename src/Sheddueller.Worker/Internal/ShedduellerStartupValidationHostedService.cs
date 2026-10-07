namespace Sheddueller.Worker.Internal;

using Microsoft.Extensions.Hosting;

using Sheddueller.Runtime;

internal sealed class ShedduellerStartupValidationHostedService(
    IEnumerable<IShedduellerStartupValidator> validators) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
      => StartupValidationRunner.ValidateAsync(validators, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
      => Task.CompletedTask;
}
