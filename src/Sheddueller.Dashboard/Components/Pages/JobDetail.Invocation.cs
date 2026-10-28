namespace Sheddueller.Dashboard.Components.Pages;

using Sheddueller.Dashboard.Components.Ui;
using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Jobs;

/// <content>
/// The method call shown in the header and on the Invocation tab.
/// </content>
public partial class JobDetail
{
    private const int HeaderValueLength = 60;

    private JobInvocationInspection? _callSource;
    private InvocationCall? _call;

    // The call is rebuilt only when a refresh brings a new invocation, not on every live log line.
    private InvocationCall CallFor(JobInvocationInspection invocation)
    {
        if (this._call is { } cached && ReferenceEquals(this._callSource, invocation))
        {
            return cached;
        }

        var arguments = DashboardInvocationView.Arguments(invocation, DashboardInvocationParameterNames.Resolve(invocation));
        var valuesAvailable = invocation.SerializedArgumentsStatus == JobSerializedArgumentsInspectionStatus.Displayable;
        var receiver = DashboardTypeName.Short(invocation.ServiceType);
        var fullCall = DashboardInvocationView.CallTokens(
          receiver,
          invocation.MethodName,
          arguments,
          valuesAvailable,
          dataOnly: false,
          maxValueLength: null,
          oneArgumentPerLine: arguments.Count > 2);

        this._callSource = invocation;
        this._call = new InvocationCall(
          invocation.MethodName,
          DashboardInvocationView.ArgumentGroups(arguments, valuesAvailable, dataOnly: true, HeaderValueLength),
          DashboardInvocationView.CallText(receiver, invocation.MethodName, arguments, valuesAvailable, dataOnly: true),
          fullCall,
          DashboardInvocationView.Text(fullCall),
          [.. arguments.Select(argument => new InvocationParameter(argument, DashboardInvocationView.JsonTokens(argument.ValueJson)))]);
        return this._call;
    }

    // The header shows only the caller's data arguments; the Invocation tab shows every argument.
    private sealed record InvocationCall(
        string MethodName,
        IReadOnlyList<IReadOnlyList<CodeToken>> HeaderArguments,
        string HeaderText,
        IReadOnlyList<CodeToken> FullCall,
        string FullText,
        IReadOnlyList<InvocationParameter> Parameters);

    private sealed record InvocationParameter(
        DashboardInvocationArgument Argument,
        IReadOnlyList<CodeToken> Value);
}
