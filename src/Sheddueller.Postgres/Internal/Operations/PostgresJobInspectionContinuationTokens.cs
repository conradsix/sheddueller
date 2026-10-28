namespace Sheddueller.Postgres.Internal.Operations;

using System.Globalization;

/// <summary>
/// The continuation tokens of job inspection pages.
/// </summary>
/// <remarks>
/// A newest-first page continues after an enqueue sequence, written as the bare number. An operational page
/// continues at a row offset, written with an <c>op:</c> prefix; a bare number is read as an offset too.
/// </remarks>
internal static class PostgresJobInspectionContinuationTokens
{
    private const string OperationalPrefix = "op:";

    public static string EncodeNewest(long enqueueSequence)
      => enqueueSequence.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads the enqueue sequence a newest-first page continues after, or null for the first page.
    /// </summary>
    /// <exception cref="ArgumentException">The token is not a positive number.</exception>
    public static long? DecodeNewest(string? continuationToken)
    {
        if (continuationToken is null)
        {
            return null;
        }

        if (!long.TryParse(continuationToken, NumberStyles.None, CultureInfo.InvariantCulture, out var enqueueSequence) || enqueueSequence <= 0)
        {
            throw new ArgumentException("Job inspection continuation token is invalid.", nameof(continuationToken));
        }

        return enqueueSequence;
    }

    public static string EncodeOperational(long offset)
      => string.Concat(OperationalPrefix, offset.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Reads the row offset an operational page starts at, which is 0 for the first page.
    /// </summary>
    /// <exception cref="ArgumentException">The token is not a non-negative number.</exception>
    public static long DecodeOperational(string? continuationToken)
    {
        if (continuationToken is null)
        {
            return 0;
        }

        var offsetToken = continuationToken.StartsWith(OperationalPrefix, StringComparison.Ordinal)
          ? continuationToken[OperationalPrefix.Length..]
          : continuationToken;

        if (!long.TryParse(offsetToken, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0)
        {
            throw new ArgumentException("Job inspection continuation token is invalid.", nameof(continuationToken));
        }

        return offset;
    }
}
