namespace Sheddueller.Enqueueing;

using Sheddueller.Storage;

internal sealed record ParsedJob(
  Type ServiceType,
  string MethodName,
  IReadOnlyList<Type> MethodParameterTypes,
  IReadOnlyList<string> MethodParameterTypeNames,
  IReadOnlyList<object?> SerializableArguments,
  IReadOnlyList<Type> SerializableParameterTypes,
  JobInvocationTargetKind InvocationTargetKind,
  IReadOnlyList<JobMethodParameterBinding> MethodParameterBindings);
