namespace Sheddueller.Runtime;

internal static class StartupValidationRunner
{
    public static async Task ValidateAsync(
        IEnumerable<IShedduellerStartupValidator> validators,
        CancellationToken cancellationToken)
    {
        foreach (var validator in validators)
        {
            await validator.ValidateAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
