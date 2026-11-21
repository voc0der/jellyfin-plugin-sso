using Jellyfin.Plugin.SsoAuthentication.Api;
using Jellyfin.Plugin.SsoAuthentication.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Jellyfin.Plugin.SsoAuthentication.Tests;

/// <summary>
/// The OpenID endpoints' refusals, which need no provider to reach. OidFlowTests exercises
/// the rest of the controller with an in-memory provider; tests/e2e uses a real Authelia.
/// </summary>
public sealed class OidEndpointTests : IDisposable
{
    private readonly PluginScope _scope = new();
    private readonly ControllerHarness _harness = new();

    public OidEndpointTests()
    {
        _scope.Plugin.Configuration.OidConfigs["authelia"] = new OidConfig
        {
            OidEndpoint = "https://auth.example.test",
            OidClientId = "jellyfin",
            OidSecret = "secret",
            Enabled = true,
            OidScopes = Array.Empty<string>(),
            Roles = Array.Empty<string>(),
        };
    }

    public void Dispose() => _scope.Dispose();

    private static AuthResponse FromBrowser(string data) => new() { Data = data, DeviceID = "d", DeviceName = "n", AppName = "a", AppVersion = "1" };

    [Fact]
    public async Task A_callback_for_an_unknown_provider_is_refused()
    {
        Assert.IsType<BadRequestObjectResult>(await _harness.Create().OidPost("nope", "state"));
    }

    [Fact]
    public async Task A_callback_without_state_is_refused()
    {
        Assert.IsType<BadRequestObjectResult>(await _harness.Create().OidPost("authelia", string.Empty));
    }

    [Fact]
    public async Task A_callback_with_a_state_this_server_never_issued_is_refused()
    {
        var result = await _harness.Create().OidPost("authelia", "forged-state");

        Assert.Equal("Invalid or expired state", Assert.IsType<BadRequestObjectResult>(result).Value);
    }

    [Fact]
    public async Task A_callback_for_a_disabled_provider_is_refused()
    {
        _scope.Plugin.Configuration.OidConfigs["authelia"].Enabled = false;

        Assert.IsType<BadRequestObjectResult>(await _harness.Create().OidPost("authelia", "state"));
    }

    [Fact]
    public async Task Signing_in_with_a_state_this_server_never_issued_signs_nobody_in()
    {
        var result = await _harness.Create().OidAuth("authelia", FromBrowser("forged-state"));

        Assert.Equal(StatusCodes.Status500InternalServerError, Assert.IsType<ObjectResult>(result).StatusCode);
        await _harness.Sessions.DidNotReceiveWithAnyArgs().AuthenticateDirect(default!);
    }

    [Fact]
    public async Task Signing_in_with_an_unknown_provider_is_refused()
    {
        Assert.IsType<BadRequestObjectResult>(await _harness.Create().OidAuth("nope", FromBrowser("state")));
    }

    [Fact]
    public void No_sign_in_is_in_progress_on_a_fresh_server()
    {
        var states = Assert.IsType<OkObjectResult>(_harness.Create().OidStates()).Value;

        Assert.Empty(Assert.IsAssignableFrom<IDictionary<string, TimedAuthorizeState>>(states));
    }
}
