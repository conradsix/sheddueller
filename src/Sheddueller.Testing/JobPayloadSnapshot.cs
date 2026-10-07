namespace Sheddueller.Testing;

using Sheddueller.Serialization;

internal static class JobPayloadSnapshot
{
    public static SerializedJobPayload Copy(SerializedJobPayload payload)
      => new(payload.ContentType, [.. payload.Data]);

    public static bool AreEqual(SerializedJobPayload left, SerializedJobPayload right)
      => string.Equals(left.ContentType, right.ContentType, StringComparison.Ordinal)
        && left.Data.SequenceEqual(right.Data);
}
