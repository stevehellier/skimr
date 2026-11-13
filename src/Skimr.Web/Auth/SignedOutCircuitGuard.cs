using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Skimr.Web.Auth;

/// <summary>
/// Ends a live circuit's access once its user is no longer valid (disabled, password reset,
/// signed out elsewhere). The revalidating provider only flips the auth state to anonymous;
/// pages hold the user id they started with, so without this an open tab would keep reading
/// and writing as that user. It drops every inbound event and JS call on the server, so a
/// modified client can't get past it (PLAN D37). SessionWatch sends the browser to sign in.
/// </summary>
internal sealed class SignedOutCircuitGuard(AuthenticationStateProvider auth) : CircuitHandler
{
    private volatile bool _signedOut;

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        auth.AuthenticationStateChanged += OnChanged;
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        auth.AuthenticationStateChanged -= OnChanged;
        return Task.CompletedTask;
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        context => _signedOut ? Task.CompletedTask : next(context);

    private void OnChanged(Task<AuthenticationState> task)
    {
        // Usually already complete; set the flag at once so no event slips through in between.
        if (task.IsCompletedSuccessfully)
        {
            Apply(task.Result);
            return;
        }

        _ = task.ContinueWith(t => Apply(t.Result), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void Apply(AuthenticationState state) => _signedOut |= state.User.Identity?.IsAuthenticated != true;
}
