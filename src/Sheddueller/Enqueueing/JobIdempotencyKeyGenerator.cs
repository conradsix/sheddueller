namespace Sheddueller.Enqueueing;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using Sheddueller.Serialization;
using Sheddueller.Storage;

internal static class JobIdempotencyKeyGenerator
{
    private const string Prefix = "method-arguments:v1:";

    /// <summary>
    /// Creates the idempotency key a submission asks for, or null when it uses no idempotency.
    /// </summary>
    public static string? CreateKey(
        JobIdempotencyKind? idempotencyKind,
        ParsedJob parsedJob,
        string serviceType,
        SerializedJobPayload serializedArguments)
      => idempotencyKind switch
      {
          JobIdempotencyKind.MethodAndArguments => CreateMethodAndArgumentsKey(parsedJob, serviceType, serializedArguments),
          _ => null,
      };

    public static string CreateMethodAndArgumentsKey(
        ParsedJob parsedJob,
        string serviceType,
        SerializedJobPayload serializedArguments)
    {
        ArgumentNullException.ThrowIfNull(parsedJob);
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentNullException.ThrowIfNull(serializedArguments);

        using var stream = new MemoryStream();
        WriteString(stream, "sheddueller:idempotency:method-arguments:v1");
        WriteString(stream, serviceType);
        WriteString(stream, parsedJob.MethodName);
        WriteStringList(stream, parsedJob.MethodParameterTypeNames);
        WriteInt32(stream, (int)parsedJob.InvocationTargetKind);
        WriteParameterBindings(stream, parsedJob.MethodParameterBindings);
        WriteString(stream, serializedArguments.ContentType);
        WriteBytes(stream, serializedArguments.Data);

        var hash = SHA256.HashData(stream.ToArray());
        return string.Concat(Prefix, Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static void WriteParameterBindings(
        Stream stream,
        IReadOnlyList<JobMethodParameterBinding> parameterBindings)
    {
        WriteInt32(stream, parameterBindings.Count);
        foreach (var binding in parameterBindings)
        {
            WriteInt32(stream, (int)binding.Kind);
            WriteString(stream, binding.ServiceType);
        }
    }

    private static void WriteStringList(
        Stream stream,
        IReadOnlyList<string> values)
    {
        WriteInt32(stream, values.Count);
        foreach (var value in values)
        {
            WriteString(stream, value);
        }
    }

    private static void WriteString(Stream stream, string? value)
    {
        if (value is null)
        {
            WriteInt32(stream, -1);
            return;
        }

        WriteBytes(stream, Encoding.UTF8.GetBytes(value));
    }

    private static void WriteBytes(Stream stream, byte[] bytes)
    {
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }
}
