namespace Sheddueller.Runtime;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Sheddueller.Enqueueing;
using Sheddueller.Storage;

internal sealed class ShedduellerCommonStartupValidator(
    IServiceProvider serviceProvider,
    IOptions<ShedduellerOptions> options) : IShedduellerStartupValidator
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IOptions<ShedduellerOptions> _options = options;

    public ValueTask ValidateAsync(CancellationToken cancellationToken)
    {
        var value = this._options.Value;

        try
        {
            SubmissionValidator.ValidateRetryPolicy(value.DefaultRetryPolicy);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("ShedduellerOptions.DefaultRetryPolicy is invalid.", exception);
        }

        if (this._serviceProvider.GetService<IJobStore>() is null)
        {
            throw new InvalidOperationException("No Sheddueller job store provider has been registered.");
        }

        ValidateJobRetentionOptions(value.JobRetention);
        ConcurrencyGroupCleanupConfiguration.FromOptions(value.ConcurrencyGroupCleanup).Validate();

        return ValueTask.CompletedTask;
    }

    private static void ValidateJobRetentionOptions(JobRetentionOptions options)
    {
        if (options.CompletedRetention is { } completedRetention && completedRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("ShedduellerOptions.JobRetention.CompletedRetention must be positive or null.");
        }

        if (options.FailedRetention is { } failedRetention && failedRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("ShedduellerOptions.JobRetention.FailedRetention must be positive or null.");
        }

        if (options.CanceledRetention is { } canceledRetention && canceledRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("ShedduellerOptions.JobRetention.CanceledRetention must be positive or null.");
        }

        if (options.CleanupInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("ShedduellerOptions.JobRetention.CleanupInterval must be positive.");
        }

        if (options.BatchSize <= 0)
        {
            throw new InvalidOperationException("ShedduellerOptions.JobRetention.BatchSize must be positive.");
        }
    }
}
