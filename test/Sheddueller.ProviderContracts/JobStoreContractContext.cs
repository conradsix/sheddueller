namespace Sheddueller.ProviderContracts;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

public sealed class JobStoreContractContext(
    IJobStore store,
    Func<string, ValueTask>? makeScheduleDueAsync = null,
    IAsyncDisposable? asyncDisposable = null,
    IJobInspectionReader? inspectionReader = null) : IAsyncDisposable
{
    public IJobStore Store { get; } = store;

    /// <summary>
    /// The provider's job inspection reader, or the store itself when it reads job inspection too.
    /// </summary>
    public IJobInspectionReader? InspectionReader { get; } = inspectionReader ?? store as IJobInspectionReader;

    public ValueTask MakeScheduleDueAsync(string scheduleKey)
      => makeScheduleDueAsync?.Invoke(scheduleKey) ?? ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (asyncDisposable is not null)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
    }
}
