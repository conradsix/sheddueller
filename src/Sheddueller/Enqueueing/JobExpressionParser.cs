namespace Sheddueller.Enqueueing;

using System.Linq.Expressions;
using System.Reflection;

using Sheddueller.Storage;

internal static class JobExpressionParser
{
    private static readonly MethodInfo JobResolveMethodDefinition =
      typeof(Job).GetMethods()
        .Single(method => method.Name == nameof(Job.Resolve) && method.IsGenericMethodDefinition);

    /// <summary>
    /// Parses submitted work: a lambda taking the scheduler cancellation token, optionally followed by the progress
    /// reporter, and preceded by the service instance when <paramref name="serviceType"/> is set, whose body is a single
    /// job method call.
    /// </summary>
    public static ParsedJob Parse(Type? serviceType, LambdaExpression work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var parameters = ValidateLambdaParameters(serviceType, work, nameof(work));
        if (StripConvert(work.Body) is not MethodCallExpression methodCall)
        {
            throw new ArgumentException("Submitted work must be a single job method call.", nameof(work));
        }

        var (targetServiceType, invocationTargetKind) = ValidateTargetMethod(serviceType, methodCall, parameters.Service, nameof(work));

        var methodParameters = methodCall.Method.GetParameters();
        if (methodParameters.Length != methodCall.Arguments.Count)
        {
            throw new ArgumentException("Submitted work must explicitly provide every target method argument.", nameof(work));
        }

        var binder = new ArgumentBinder(parameters, nameof(work));
        for (var i = 0; i < methodParameters.Length; i++)
        {
            binder.Bind(methodParameters[i].ParameterType, methodCall.Arguments[i]);
        }

        binder.EnsureSchedulerArgumentsForwarded();

        return new ParsedJob(
          targetServiceType,
          methodCall.Method.Name,
          [.. methodParameters.Select(parameter => parameter.ParameterType)],
          [.. methodParameters.Select(parameter => TypeNameFormatter.Format(parameter.ParameterType))],
          binder.SerializableArguments,
          binder.SerializableParameterTypes,
          invocationTargetKind,
          binder.ParameterBindings);
    }

    /// <summary>
    /// Checks the lambda's parameters: <c>(cancellationToken[, progress])</c>, or with a leading service instance when
    /// <paramref name="serviceType"/> is set.
    /// </summary>
    private static LambdaParameters ValidateLambdaParameters(
        Type? serviceType,
        LambdaExpression work,
        string parameterName)
    {
        var parameters = work.Parameters;
        var cancellationTokenIndex = serviceType is null ? 0 : 1;
        var includesProgressReporter = parameters.Count == cancellationTokenIndex + 2
          && parameters[cancellationTokenIndex].Type == typeof(CancellationToken)
          && parameters[cancellationTokenIndex + 1].Type == typeof(IProgress<decimal>);
        var acceptsCancellationToken = includesProgressReporter
          || (parameters.Count == cancellationTokenIndex + 1 && parameters[cancellationTokenIndex].Type == typeof(CancellationToken));

        if (!acceptsCancellationToken)
        {
            throw new ArgumentException(
              serviceType is null
                ? "Submitted work must accept the scheduler cancellation token."
                : "Submitted work must accept a service instance and scheduler cancellation token.",
              parameterName);
        }

        return new LambdaParameters(
          serviceType is null ? null : parameters[0],
          parameters[cancellationTokenIndex],
          includesProgressReporter ? parameters[cancellationTokenIndex + 1] : null);
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

    /// <summary>
    /// Evaluates a serialized argument. Constants and captured variables, which are fields of a closure object, are read
    /// directly; anything else is compiled and invoked.
    /// </summary>
    private static object? EvaluateArgument(Expression argument)
      => TryReadValue(argument, out var value)
        ? value
        : Expression.Lambda<Func<object?>>(Expression.Convert(argument, typeof(object))).Compile().Invoke();

    private static bool TryReadValue(Expression expression, out object? value)
    {
        switch (expression)
        {
            case ConstantExpression constant:
                value = constant.Value;
                return true;

            // A null instance falls back to the compiled read, which throws as it always has.
            case MemberExpression { Member: FieldInfo field, Expression: { } instanceExpression }
                when TryReadValue(instanceExpression, out var instance) && instance is not null:
                value = field.GetValue(instance);
                return true;

            default:
                value = null;
                return false;
        }
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

    /// <summary>
    /// The submitted lambda's parameters; <see cref="Service"/> is null for static or <c>Job.Resolve</c> work and
    /// <see cref="ProgressReporter"/> is null when the lambda takes none.
    /// </summary>
    private readonly record struct LambdaParameters(
        ParameterExpression? Service,
        ParameterExpression CancellationToken,
        ParameterExpression? ProgressReporter);

    /// <summary>
    /// Binds each target method argument to the value the worker supplies: a scheduler-owned parameter, a resolved
    /// service, or an evaluated value serialized with the job.
    /// </summary>
    private sealed class ArgumentBinder(
        LambdaParameters parameters,
        string parameterName)
    {
        private readonly List<object?> _serializableArguments = [];
        private readonly List<Type> _serializableParameterTypes = [];
        private readonly List<JobMethodParameterBinding> _parameterBindings = [];
        private bool _forwardedCancellationToken;
        private bool _forwardedProgressReporter;

        public IReadOnlyList<object?> SerializableArguments
          => this._serializableArguments;

        public IReadOnlyList<Type> SerializableParameterTypes
          => this._serializableParameterTypes;

        public IReadOnlyList<JobMethodParameterBinding> ParameterBindings
          => this._parameterBindings;

        public void Bind(Type parameterType, Expression argument)
        {
            if (parameterType.IsByRef)
            {
                throw new ArgumentException("Submitted work cannot target methods with ref or out parameters.", parameterName);
            }

            if (parameterType == typeof(CancellationToken))
            {
                if (!IsSameParameter(argument, parameters.CancellationToken))
                {
                    throw new ArgumentException(
                      "CancellationToken target method parameters must receive the scheduler-owned cancellation token.",
                      parameterName);
                }

                this._forwardedCancellationToken = true;
                this._parameterBindings.Add(new JobMethodParameterBinding(JobMethodParameterBindingKind.CancellationToken));
                return;
            }

            if (parameterType == typeof(IProgress<decimal>))
            {
                if (parameters.ProgressReporter is null || !IsSameParameter(argument, parameters.ProgressReporter))
                {
                    throw new ArgumentException(
                      "IProgress<decimal> target method parameters must receive the scheduler-owned progress reporter.",
                      parameterName);
                }

                this._forwardedProgressReporter = true;
                this._parameterBindings.Add(new JobMethodParameterBinding(JobMethodParameterBindingKind.ProgressReporter));
                return;
            }

            if (IsAnyProgressReporterType(parameterType))
            {
                throw new ArgumentException("Only IProgress<decimal> is supported for scheduler-owned progress reporting.", parameterName);
            }

            if (parameterType == typeof(IJobContext))
            {
                if (!IsJobContextMarker(argument))
                {
                    throw new ArgumentException(
                      "IJobContext target method parameters must receive the Job.Context marker.",
                      parameterName);
                }

                this._parameterBindings.Add(new JobMethodParameterBinding(JobMethodParameterBindingKind.JobContext));
                return;
            }

            if (ReferencesJobContextMarker(argument))
            {
                throw new ArgumentException("Job.Context can only be passed to IJobContext target method parameters.", parameterName);
            }

            if (TryGetJobResolveMarker(argument, out var resolvedServiceType))
            {
                this._parameterBindings.Add(
                  new JobMethodParameterBinding(
                    JobMethodParameterBindingKind.Service,
                    TypeNameFormatter.Format(resolvedServiceType)));
                return;
            }

            if (ReferencesJobResolveMarker(argument))
            {
                throw new ArgumentException("Job.Resolve<TService>() can only be passed directly as a target method argument.", parameterName);
            }

            if ((parameters.Service is not null && ReferencesParameter(argument, parameters.Service))
                || ReferencesParameter(argument, parameters.CancellationToken)
                || (parameters.ProgressReporter is not null && ReferencesParameter(argument, parameters.ProgressReporter)))
            {
                throw new ArgumentException(
                  parameters.ProgressReporter is null
                    ? "Only the target service instance and scheduler cancellation token may be runtime-bound."
                    : "Only the target service instance, scheduler cancellation token, and progress reporter may be runtime-bound.",
                  parameterName);
            }

            ValidateSerializableParameterType(parameterType, parameterName);
            var value = EvaluateArgument(argument);
            ValidateSerializableArgumentValue(value, parameterName);

            this._serializableArguments.Add(value);
            this._serializableParameterTypes.Add(parameterType);
            this._parameterBindings.Add(new JobMethodParameterBinding(JobMethodParameterBindingKind.Serialized));
        }

        public void EnsureSchedulerArgumentsForwarded()
        {
            if (!this._forwardedCancellationToken)
            {
                throw new ArgumentException("Submitted work must forward the scheduler-owned CancellationToken.", parameterName);
            }

            if (parameters.ProgressReporter is not null && !this._forwardedProgressReporter)
            {
                throw new ArgumentException("Submitted work must forward the scheduler-owned progress reporter.", parameterName);
            }
        }
    }

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
}
