using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Skimr.Web.Auth;

namespace Skimr.UnitTests.Auth;

public sealed class SignedOutCircuitGuardTests
{
    private sealed class SwitchableAuth : AuthenticationStateProvider
    {
        private AuthenticationState _state = new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test")));

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(_state);

        public void SignOut()
        {
            _state = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
            NotifyAuthenticationStateChanged(Task.FromResult(_state));
        }
    }

    [Fact]
    public async Task Inbound_activity_passes_while_signed_in_and_is_dropped_after()
    {
        var auth = new SwitchableAuth();
        var guard = new SignedOutCircuitGuard(auth);
        await guard.OnCircuitOpenedAsync(null!, TestContext.Current.CancellationToken);
        var calls = 0;
        var handler = guard.CreateInboundActivityHandler(_ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        await handler(null!);
        auth.SignOut();
        await handler(null!);
        await handler(null!);

        calls.Should().Be(1, "events and JS calls after sign-out never reach the page");
    }

    [Fact]
    public async Task Closing_the_circuit_unsubscribes()
    {
        var auth = new SwitchableAuth();
        var guard = new SignedOutCircuitGuard(auth);
        await guard.OnCircuitOpenedAsync(null!, TestContext.Current.CancellationToken);
        await guard.OnCircuitClosedAsync(null!, TestContext.Current.CancellationToken);
        var calls = 0;
        var handler = guard.CreateInboundActivityHandler(_ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        auth.SignOut();
        await handler(null!);

        calls.Should().Be(1, "a closed circuit no longer listens");
    }
}
