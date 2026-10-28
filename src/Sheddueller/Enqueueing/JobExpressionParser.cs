namespace Sheddueller.Enqueueing;

using System.Linq.Expressions;

using Sheddueller.Storage;

internal static class JobExpressionParser
{
    public static ParsedJob Parse<TResult>(Expression<Func<CancellationToken, TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return Parse(serviceType: null, work, includesProgressReporter: false);
    }

    public static ParsedJob Parse<TResult>(Expression<Func<CancellationToken, IProgress<decimal>, TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return Parse(serviceType: null, work, includesProgressReporter: true);
    }

    public static ParsedJob Parse<TService, TResult>(Expression<Func<TService, CancellationToken, TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return Parse(typeof(TService), work, includesProgressReporter: false);
    }

    public static ParsedJob Parse<TService, TResult>(Expression<Func<TService, CancellationToken, IProgress<decimal>, TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return Parse(typeof(TService), work, includesProgressReporter: true);
    }

    public static ParsedJob Parse(Type? serviceType, LambdaExpression work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return Parse(serviceType, work, IncludesProgressReporter(serviceType, work));
    }

    private static ParsedJob Parse(
        Type? serviceType,
        LambdaExpression work,
        bool includesProgressReporter)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (serviceType is null && !ValidateStaticExpressionParameters(work, includesProgressReporter))
        {
            throw new ArgumentException(
              includesProgressReporter
                ? "Submitted work must accept the scheduler cancellation token and progress reporter."
                : "Submitted work must accept the scheduler cancellation token.",
              nameof(work));
        }

        if (serviceType is not null && !ValidateServiceExpressionParameters(work, includesProgressReporter))
        {
            throw new ArgumentException(
              includesProgressReporter
                ? "Submitted work must accept a service instance, scheduler cancellation token, and progress reporter."
                : "Submitted work must accept a service instance and scheduler cancellation token.",
              nameof(work));
        }

        var serviceParameter = serviceType is null ? null : work.Parameters[0];
        var cancellationTokenParameter = serviceType is null ? work.Parameters[0] : work.Parameters[1];
        var progressReporterParameter = includesProgressReporter
          ? serviceType is null ? work.Parameters[1] : work.Parameters[2]
          : null;
        var body = StripConvert(work.Body);

        if (body is not MethodCallExpression methodCall)
        {
            throw new ArgumentException("Submitted work must be a single job method call.", nameof(work));
        }

        var (targetServiceType, invocationTargetKind) = ValidateTargetMethod(serviceType, methodCall, serviceParameter, nameof(work));

        var methodParameters = methodCall.Method.GetParameters();
        if (methodParameters.Length != methodCall.Arguments.Count)
        {
            throw new ArgumentException("Submitted work must explicitly provide every target method argument.", nameof(work));
        }

        var serializableArguments = new List<object?>();
        var serializableParameterTypes = new List<Type>();
        var parameterBindings = new List<JobMethodParameterBinding>();
        var forwardedCancellationToken = false;
        var forwardedProgressReporter = false;

        for (var i = 0; i < methodParameters.Length; i++)
        {
            var parameter = methodParameters[i];
            var argument = methodCall.Arguments[i];

            if (parameter.ParameterType.IsByRef)
            {
                throw new ArgumentException("Submitted work cannot target methods with ref or out parameters.", nameof(work));
            }

            if (parameter.ParameterType == typeof(CancellationToken))
            {
                if (!IsSameParameter(argument, cancellationTokenParameter))
                {
                    throw new ArgumentException(
                      "CancellationToken target method parameters must receive the scheduler-owned cancellation token.",
                      nameof(work));
                }

                forwardedCancellationToken = true;
                parameterBindings.Add(new JobMethodParameterBinding(JobMethodParameterBindingKind.CancellationToken));
                continue;
            }

            if (parameter.ParameterType == typeof(IProgress<decimal>))
            {
                if (progressReporterParameter is null || !IsSameParameter(argument, progressReporterParameter))
                {
                    throw new ArgumentException(
                      "IProgress<decimal> target method parameters must receive the scheduler-owned progress reporter.",
                      nameof(work));
                }

                forwardedProgressReporter = true;
                parameterBindings.Add(new JobMethodParameterBinding(JobMethodParameterBindingKind.ProgressReporter));
                continue;
            }

            if (IsAnyProgressReporterType(parameter.ParameterType))
            {
                throw new ArgumentException("Only IProgress<decimal> is supported for scheduler-owned progress reporting.", nameof(work));
            }

            if (parameter.ParameterType == typeof(IJobContext))
            {
                if (!IsJobContextMarker(argument))
                {
                    throw new ArgumentException(
                      "IJobContext target method parameters must receive the Job.Context marker.",
                      nameof(work));
                }

                parameterBindings.Add(new JobMethodParameterBinding(JobMethodParameterBindingKind.JobContext));
                continue;
            }

            if (ReferencesJobContextMarker(argument))
            {
                throw new ArgumentException("Job.Context can only be passed to IJobContext target method parameters.", nameof(work));
            }

            if (TryGetJobResolveMarker(argument, out var resolvedServiceType))
            {
                parameterBindings.Add(
                  new JobMethodParameterBinding(
                    JobMethodParameterBindingKind.Service,
                    TypeNameFormatter.Format(resolvedServiceType)));
                continue;
            }

            if (ReferencesJobResolveMarker(argument))
            {
                throw new ArgumentException("Job.Resolve<TService>() can only be passed directly as a target method argument.", nameof(work));
            }

            if ((serviceParameter is not null && ReferencesParameter(argument, serviceParameter))
                || ReferencesParameter(argument, cancellationTokenParameter)
                || (progressReporterParameter is not null && ReferencesParameter(argument, progressReporterParameter)))
            {
                throw new ArgumentException(
                  progressReporterParameter is null
                    ? "Only the target service instance and scheduler cancellation token may be runtime-bound."
                    : "Only the target service instance, scheduler cancellation token, and progress reporter may be runtime-bound.",
                  nameof(work));
            }

            ValidateSerializableParameterType(parameter.ParameterType, nameof(work));
            var value = EvaluateArgument(argument);
            ValidateSerializableArgumentValue(value, nameof(work));

            serializableArguments.Add(value);
            serializableParameterTypes.Add(parameter.ParameterType);
            parameterBindings.Add(new JobMethodParameterBinding(JobMethodParameterBindingKind.Serialized));
        }

        if (!forwardedCancellationToken)
        {
            throw new ArgumentException("Submitted work must forward the scheduler-owned CancellationToken.", nameof(work));
        }

        if (progressReporterParameter is not null && !forwardedProgressReporter)
        {
            throw new ArgumentException("Submitted work must forward the scheduler-owned progress reporter.", nameof(work));
        }

        return new ParsedJob(
          targetServiceType,
          methodCall.Method.Name,
          [.. methodParameters.Select(parameter => parameter.ParameterType)],
          [.. methodParameters.Select(parameter => TypeNameFormatter.Format(parameter.ParameterType))],
          serializableArguments,
          serializableParameterTypes,
          invocationTargetKind,
          parameterBindings);
    }

    private static (Type ServiceType, JobInvocationTargetKind InvocationTargetKind) ValidateTargetMethod(
        Type? serviceType,
        MethodCallExpression methodCall,
        ParameterExpression? serviceParameter,
        string parameterName)
    {
        if (methodCall.Method.IsGenericMethod || methodCall.Method.ContainsGenericParameters)
        {
            throw new ArgumentException("Submitted work cannot target generic methods.", parameterName);
        }

        if (methodCall.Method.ReturnType != typeof(Task) && methodCall.Method.ReturnType != typeof(ValueTask))
        {
            throw new ArgumentException("Submitted work must target a method returning Task or ValueTask.", parameterName);
        }

        if (methodCall.Method.IsStatic)
        {
            if (methodCall.Method.DeclaringType is null)
            {
                throw new ArgumentException("Submitted static work must have a declaring type.", parameterName);
            }

            if (serviceType is not null && !IsCompatibleServiceType(serviceType, methodCall.Method.DeclaringType))
            {
                throw new ArgumentException("Submitted static work must target the submitted service type.", parameterName);
            }

            return (methodCall.Method.DeclaringType, JobInvocationTargetKind.Static);
        }

        if (methodCall.Object is null)
        {
            throw new ArgumentException("Submitted work must target an instance method on the submitted service type.", parameterName);
        }

        if (TryGetJobResolveMarker(methodCall.Object, out var resolvedServiceType))
        {
            return (resolvedServiceType, JobInvocationTargetKind.Instance);
        }

        if (ReferencesJobResolveMarker(methodCall.Object))
        {
            throw new ArgumentException("Job.Resolve<TService>() can only be used directly as a job method target.", parameterName);
        }

        if (serviceType is null || serviceParameter is null)
        {
            throw new ArgumentException("Submitted instance work must target Job.Resolve<TService>().", parameterName);
        }

        if (!ReferencesParameter(methodCall.Object, serviceParameter))
        {
            throw new ArgumentException("Submitted work must call a method on the submitted service parameter.", parameterName);
        }

        if (!IsCompatibleServiceType(serviceType, methodCall.Object.Type))
        {
            throw new ArgumentException("Submitted work must target the submitted service type.", parameterName);
        }

        return (serviceType, JobInvocationTargetKind.Instance);
    }

    private static bool IsCompatibleServiceType(Type serviceType, Type targetType)
      => serviceType.IsAssignableTo(targetType) || targetType.IsAssignableTo(serviceType);

    private static bool ValidateStaticExpressionParameters(
        LambdaExpression work,
        bool includesProgressReporter)
      => includesProgressReporter
        ? work.Parameters.Count == 2
          && work.Parameters[0].Type == typeof(CancellationToken)
          && work.Parameters[1].Type == typeof(IProgress<decimal>)
        : work.Parameters.Count == 1
          && work.Parameters[0].Type == typeof(CancellationToken);

    private static bool ValidateServiceExpressionParameters(
        LambdaExpression work,
        bool includesProgressReporter)
      => includesProgressReporter
        ? work.Parameters.Count == 3
          && work.Parameters[1].Type == typeof(CancellationToken)
          && work.Parameters[2].Type == typeof(IProgress<decimal>)
        : work.Parameters.Count == 2
          && work.Parameters[1].Type == typeof(CancellationToken);

    private static bool IncludesProgressReporter(
        Type? serviceType,
        LambdaExpression work)
      => serviceType is null
        ? work.Parameters.Count == 2
          && work.Parameters[0].Type == typeof(CancellationToken)
          && work.Parameters[1].Type == typeof(IProgress<decimal>)
        : work.Parameters.Count == 3
          && work.Parameters[1].Type == typeof(CancellationToken)
          && work.Parameters[2].Type == typeof(IProgress<decimal>);

    private static object? EvaluateArgument(Expression argument)
    {
        var converted = Expression.Convert(argument, typeof(object));
        var lambda = Expression.Lambda<Func<object?>>(converted);

        return lambda.Compile().Invoke();
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            expression = unary.Operand;
        }

        return expression;
    }

    private static bool ReferencesParameter(Expression expression, ParameterExpression parameter)
    {
        var visitor = new ParameterReferenceVisitor(parameter);
        visitor.Visit(expression);

        return visitor.Found;
    }

    private static bool IsSameParameter(Expression expression, ParameterExpression parameter)
      => ReferenceEquals(StripConvert(expression), parameter);

    private static bool TryGetJobResolveMarker(Expression expression, out Type serviceType)
    {
        var stripped = StripConvert(expression);
        if (stripped is MethodCallExpression { Object: null } methodCall
            && methodCall.Method.IsGenericMethod
            && methodCall.Method.GetGenericMethodDefinition() == JobResolveMethodDefinition)
        {
            serviceType = methodCall.Method.GetGenericArguments()[0];
            return true;
        }

        serviceType = null!;
        return false;
    }

    private static bool IsJobContextMarker(Expression expression)
    {
        var stripped = StripConvert(expression);
        return stripped is MemberExpression { Member.Name: nameof(Job.Context), Expression: null } member
          && member.Member.DeclaringType == typeof(Job);
    }

    private static bool ReferencesJobContextMarker(Expression expression)
    {
        var visitor = new JobContextMarkerVisitor();
        visitor.Visit(expression);

        return visitor.Found;
    }

    private static bool ReferencesJobResolveMarker(Expression expression)
    {
        var visitor = new JobResolveMarkerVisitor();
        visitor.Visit(expression);

        return visitor.Found;
    }

    private static void ValidateSerializableParameterType(Type type, string parameterName)
    {
        if (typeof(CancellationToken).IsAssignableFrom(type)
            || typeof(IJobContext).IsAssignableFrom(type)
            || IsAnyProgressReporterType(type)
            || typeof(Delegate).IsAssignableFrom(type)
            || typeof(Stream).IsAssignableFrom(type))
        {
            throw new ArgumentException($"Parameter type '{type}' is not supported for serialized job arguments.", parameterName);
        }
    }

    private static void ValidateSerializableArgumentValue(object? value, string parameterName)
    {
        if (value is CancellationToken or IJobContext or Delegate or Stream
            || (value is not null && IsAnyProgressReporterType(value.GetType())))
        {
            throw new ArgumentException($"Argument value of type '{value.GetType()}' is not supported for serialized job arguments.", parameterName);
        }
    }

    private static bool IsAnyProgressReporterType(Type type)
      => IsProgressReporterInterface(type) || type.GetInterfaces().Any(IsProgressReporterInterface);

    private static bool IsProgressReporterInterface(Type type)
      => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IProgress<>);

    private sealed class ParameterReferenceVisitor(ParameterExpression parameter) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == parameter)
            {
                this.Found = true;
            }

            return node;
        }
    }

    private sealed class JobContextMarkerVisitor : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (IsJobContextMarker(node))
            {
                this.Found = true;
            }

            return base.VisitMember(node);
        }
    }

    private sealed class JobResolveMarkerVisitor : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (TryGetJobResolveMarker(node, out _))
            {
                this.Found = true;
            }

            return base.VisitMethodCall(node);
        }
    }

    private static readonly System.Reflection.MethodInfo JobResolveMethodDefinition =
      typeof(Job).GetMethods()
        .Single(method => method.Name == nameof(Job.Resolve) && method.IsGenericMethodDefinition);
}
