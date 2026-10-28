namespace Sheddueller.Dashboard.Internal;

using System.Collections.Concurrent;
using System.Reflection;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

/// <summary>
/// Parameter names aren't stored with a job, so they're read from the job method's metadata when the dashboard
/// can load the job's assembly (it usually can when hosted alongside the workers). Nothing is invoked.
/// </summary>
internal static class DashboardInvocationParameterNames
{
    private static readonly ConcurrentDictionary<string, string[]?> Cache = new(StringComparer.Ordinal);

    public static IReadOnlyList<string>? Resolve(JobInvocationInspection invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        string[] parameterTypeNames = [.. invocation.Parameters.OrderBy(parameter => parameter.ParameterIndex).Select(parameter => parameter.ParameterType)];
        var key = string.Join('\n', [invocation.TargetKind.ToString(), invocation.ServiceType, invocation.MethodName, .. parameterTypeNames]);
        return Cache.GetOrAdd(
          key,
          _ => TryResolve(invocation.TargetKind, invocation.ServiceType, invocation.MethodName, parameterTypeNames));
    }

    internal static string[]? TryResolve(
        JobInvocationTargetKind targetKind,
        string serviceTypeName,
        string methodName,
        IReadOnlyList<string> parameterTypeNames)
    {
        try
        {
            var serviceType = Type.GetType(serviceTypeName, throwOnError: false);
            if (serviceType is null)
            {
                return null;
            }

            // The same lookup the worker uses to invoke the job.
            var flags = BindingFlags.Public | BindingFlags.NonPublic
              | (targetKind == JobInvocationTargetKind.Static ? BindingFlags.Static : BindingFlags.Instance);
            var parameterTypes = parameterTypeNames.Select(name => Type.GetType(name, throwOnError: false)).ToArray();
            var method = parameterTypes.All(type => type is not null)
              ? serviceType.GetMethod(methodName, flags, binder: null, types: [.. parameterTypes.OfType<Type>()], modifiers: null)
              : null;
            method ??= SingleMethod(serviceType, methodName, flags, parameterTypeNames.Count);
            if (method is null)
            {
                return null;
            }

            var parameters = method.GetParameters();
            return parameters.Length == parameterTypeNames.Count
              ? [.. parameters.Select((parameter, index) => string.IsNullOrEmpty(parameter.Name) ? $"arg{index + 1}" : parameter.Name)]
              : null;
        }
        catch (Exception exception) when (exception is TypeLoadException or FileLoadException or FileNotFoundException
            or BadImageFormatException or AmbiguousMatchException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // When a parameter type can't be loaded, the method is still identifiable if it's the only overload with that arity.
    private static MethodInfo? SingleMethod(Type serviceType, string methodName, BindingFlags flags, int parameterCount)
    {
        var candidates = serviceType.GetMethods(flags)
          .Where(method => method.Name == methodName && method.GetParameters().Length == parameterCount)
          .Take(2)
          .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }
}
