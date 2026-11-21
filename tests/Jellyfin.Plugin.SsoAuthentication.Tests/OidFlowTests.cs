using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SsoAuthentication.Api;
using Jellyfin.Plugin.SsoAuthentication.Config;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Jellyfin.Plugin.SsoAuthentication.Tests;

/// <summary>
/// Drives discovery, PKCE, token exchange, claims, linking and sign-in through the public
/// controller methods. HTTP alone is substituted; Duende processes the provider responses.
/// </summary>
public sealed class OidFlowTests : IDisposable
{
    private readonly PluginScope _scope = new();
    private readonly ControllerHarness _harness = new();
    private readonly OidIdentityProvider _idp = new();
    private readonly List<User> _created = new();

    public OidFlowTests()
    {
        _scope.Plugin.Configuration.OidConfigs["test"] = new OidConfig
        {
            OidEndpoint = " " + OidIdentityProvider.Authority + " ",
            OidClientId = " jellyfin ",
            OidSecret = " test-secret ",
            Enabled = true,
            OidScopes = new[] { "email", "groups" },
            DisablePushedAuthorization = true,
            EnableAuthorization = true,
            Roles = new[] { "members" },
            AdminRoles = new[] { "admins" },
            RoleClaim = " groups ",
            EnableFolderRoles = true,
            FolderRoleMapping = new List<FolderRoleMap>
            {
                new() { Role = " kids ", Folders = new List<string> { "kids-folder" } },
                new() { Role = "other", Folders = new List<string> { "other-folder" } },
                new() { Role = null, Folders = new List<string> { "unmapped-folder" } },
            },
            EnableLiveTvRoles = true,
            LiveTvRoles = new[] { "tv" },
            LiveTvManagementRoles = new[] { "tv-admins" },
        };
        _harness.HttpClients.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_idp, disposeHandler: false));
        _harness.Users.CreateUserAsync(Arg.Any<string>()).Returns(call =>
        {
            var account = TestUsers.Create(call.Arg<string>(), Guid.NewGuid());
            _created.Add(account);
            _harness.Users.GetUserById(account.Id).Returns(account);
            return Task.FromResult(account);
        });
        _harness.Sessions.AuthenticateDirect(Arg.Any<AuthenticationRequest>())
            .Returns(new AuthenticationResult { AccessToken = "session-token" });
    }

    private OidConfig Config => _scope.Plugin.Configuration.OidConfigs["test"];

    private IDictionary<string, TimedAuthorizeState> States
        => Assert.IsAssignableFrom<IDictionary<string, TimedAuthorizeState>>(Assert.IsType<OkObjectResult>(_harness.Create().OidStates()).Value);

    public void Dispose()
    {
        States.Clear();
        _idp.Dispose();
        _scope.Dispose();
    }

    private async Task<Dictionary<string, string>> Start(bool linking = false, string path = "/sso/OID/p/test")
    {
        var result = Assert.IsType<RedirectResult>(await _harness.Create(path: path).OidChallenge("test", linking));
        return _idp.Authorize(result.Url);
    }

    private async Task<ActionResult> Callback(Dictionary<string, string> authorization, string? query = null)
    {
        var controller = _harness.Create(path: new Uri(authorization["redirect_uri"]).AbsolutePath);
        controller.Request.QueryString = new QueryString(query ?? _idp.Callback(authorization));
        return await controller.OidPost("test", authorization["state"]);
    }

    private static AuthResponse FromBrowser(string state) => new()
    {
        Data = state,
        DeviceID = "device-1",
        DeviceName = "Firefox",
        AppName = "Jellyfin Web",
        AppVersion = "12.0",
    };

    [Fact]
    public async Task A_provider_response_creates_a_locked_account_with_roles_and_a_session()
    {
        var authorization = await Start();
        Assert.Equal("openid profile email groups", authorization["scope"]);
        Assert.Equal("code", authorization["response_type"]);
        Assert.False(States[authorization["state"]].Valid);

        var page = Assert.IsType<ContentResult>(await Callback(authorization));
        Assert.Equal("text/html", page.ContentType);
        Assert.Contains("'https://jellyfin.example.test/sso/OID/Auth/test'", page.Content, StringComparison.Ordinal);
        var state = States[authorization["state"]];
        Assert.Equal("alice", state.Username);
        Assert.True(state.Valid);
        Assert.True(state.Admin);
        Assert.Equal(new[] { "kids-folder" }, state.Folders);
        Assert.True(state.EnableLiveTv);
        Assert.True(state.EnableLiveTvManagement);

        var result = Assert.IsType<OkObjectResult>(await _harness.Create().OidAuth("test", FromBrowser(authorization["state"])));
        Assert.Equal("session-token", Assert.IsType<AuthenticationResult>(result.Value).AccessToken);
        var account = Assert.Single(_created);
        Assert.Equal("alice", account.Username);
        Assert.Equal(typeof(SSOController).FullName, account.AuthenticationProviderId);
        Assert.StartsWith("$" + FakeCryptoProvider.HashId + "$", account.Password, StringComparison.Ordinal);
        Assert.True(account.HasPermission(PermissionKind.IsAdministrator));
        Assert.False(account.HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal(new[] { "kids-folder" }, account.GetPreferenceValues<string>(PreferenceKind.EnabledFolders));
        Assert.Equal(account.Id, Config.CanonicalLinks["alice"]);
        await _harness.Users.Received().UpdateUserAsync(account);
        await _harness.Sessions.Received(1).AuthenticateDirect(Arg.Is<AuthenticationRequest>(request =>
            request.UserId == account.Id && request.Username == "alice" && request.DeviceId == "device-1"
            && request.DeviceName == "Firefox" && request.App == "Jellyfin Web" && request.AppVersion == "12.0"));
        Assert.Empty(States);
        Assert.All(_idp.Requests, request => Assert.StartsWith("Jellyfin-Plugin-SsoAuthentication/", request.UserAgent, StringComparison.Ordinal));

        Assert.IsType<ObjectResult>(await _harness.Create().OidAuth("test", FromBrowser(authorization["state"])));
        await _harness.Sessions.Received(1).AuthenticateDirect(Arg.Any<AuthenticationRequest>());
    }

    [Theory]
    [InlineData("/sso/OID/p/test", false, "/sso/OID/r/test")]
    [InlineData("/sso/OID/start/test", true, "/sso/OID/redirect/test")]
    public async Task The_challenge_remembers_the_callback_route_for_linking(string path, bool newPath, string callback)
    {
        var authorization = await Start(path: path);
        Assert.Equal("https://jellyfin.example.test" + callback, authorization["redirect_uri"]);
        Assert.Equal(newPath, Config.NewPath);
        var linking = await Start(linking: true);
        Assert.Equal(authorization["redirect_uri"], linking["redirect_uri"]);
        Assert.True(States[linking["state"]].IsLinking);
        Assert.False(States[authorization["state"]].IsLinking);
        Assert.Equal("text/html", Assert.IsType<ContentResult>(await Callback(authorization)).ContentType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_and_disabled_providers_cannot_start_a_flow(bool disabled)
    {
        Config.Enabled = !disabled;
        await Assert.ThrowsAsync<ArgumentException>(() => _harness.Create().OidChallenge(disabled ? "test" : "missing"));
        Assert.Empty(_idp.Requests);
        Assert.Empty(States);
    }

    [Fact]
    public async Task Starting_a_flow_removes_expired_states_but_keeps_recent_ones()
    {
        var expired = await Start();
        var recent = await Start();
        States[expired["state"]].Created = DateTime.Now.AddMinutes(-2);

        var current = await Start();

        Assert.False(States.ContainsKey(expired["state"]));
        Assert.True(States.ContainsKey(recent["state"]));
        Assert.True(States.ContainsKey(current["state"]));
        Assert.Equal(2, States.Count);
        Assert.Equal("Invalid or expired state", Assert.IsType<BadRequestObjectResult>(await Callback(expired)).Value);
    }

    [Fact]
    public async Task A_failed_pushed_authorization_request_returns_an_error_without_storing_state()
    {
        Config.DisablePushedAuthorization = false;
        _idp.OfferPushedAuthorization = true;
        _idp.RejectPushedAuthorization = true;

        var result = Assert.IsType<ContentResult>(await _harness.Create().OidChallenge("test"));

        Assert.Equal(400, result.StatusCode);
        Assert.Contains("Error preparing login:", result.Content, StringComparison.Ordinal);
        Assert.Empty(States);
        Assert.Contains(_idp.Requests, request => request.Path == "/par");
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("state")]
    [InlineData("token")]
    [InlineData("userinfo")]
    [InlineData("subject")]
    public async Task Failed_provider_responses_never_authorize_a_state(string failure)
    {
        var authorization = await Start();
        _idp.RejectToken = failure == "token";
        _idp.RejectUserInfo = failure == "userinfo";
        _idp.UserInfoSubject = failure == "subject" ? "someone-else" : null;
        var query = failure switch
        {
            "provider" => "?error=access_denied&error_description=Denied&state=" + authorization["state"],
            "state" => "?code=unused&state=wrong-state",
            _ => null,
        };

        var result = Assert.IsType<ContentResult>(await Callback(authorization, query));

        Assert.Equal(400, result.StatusCode);
        Assert.StartsWith("Error logging in:", result.Content, StringComparison.Ordinal);
        Assert.False(States[authorization["state"]].Valid);
        Assert.IsType<ObjectResult>(await _harness.Create().OidAuth("test", FromBrowser(authorization["state"])));
        await _harness.Sessions.DidNotReceiveWithAnyArgs().AuthenticateDirect(default!);
        Assert.Empty(_created);
    }

    [Theory]
    [InlineData("realm_access.roles", "realm_access", "{\"roles\":[\"members\",\"admins\",\"kids\",\"tv\",\"tv-admins\"]}", true)]
    [InlineData(" resource_access.jellyfin.roles ", "resource_access", "{\"jellyfin\":{\"roles\":[\"members\"]}}", true)]
    [InlineData("resource_access.jellyfin\\.web.roles", "resource_access", "{\"jellyfin.web\":{\"roles\":[\"members\"]}}", true)]
    [InlineData("realm\\.access.roles", "realm.access", "{\"roles\":[\"members\"]}", true)]
    [InlineData("realm_access.roles", "realm_access", "null", false)]
    [InlineData("realm_access.roles", "realm_access", "{}", false)]
    [InlineData("realm_access.roles", "realm_access", "{\"roles\":\"members\"}", false)]
    [InlineData("realm_access.roles", "realm_access", "{\"roles\":null}", false)]
    [InlineData("realm_access.roles", "realm_access", "{\"roles\":[\"visitors\"]}", false)]
    [InlineData("resource_access.jellyfin.roles", "resource_access", "{}", false)]
    [InlineData("resource_access.jellyfin.roles", "resource_access", "{\"jellyfin\":null}", false)]
    [InlineData("resource_access.jellyfin.roles", "resource_access", "{\"jellyfin\":[]}", false)]
    [InlineData("resource_access.jellyfin.roles", "resource_access", "{\"jellyfin\":\"members\"}", false)]
    public async Task Nested_role_paths_accept_only_the_configured_array(string path, string claim, string json, bool allowed)
    {
        Config.RoleClaim = path;
        _idp.Claims.Remove("groups");
        _idp.Claims[claim] = json;
        var authorization = await Start();

        var result = Assert.IsType<ContentResult>(await Callback(authorization));

        Assert.Equal(allowed, States[authorization["state"]].Valid);
        if (allowed)
        {
            Assert.Equal("text/html", result.ContentType);
            Assert.Equal("alice", States[authorization["state"]].Username);
        }
        else
        {
            Assert.Equal(401, result.StatusCode);
            Assert.Equal("Error. Check permissions.", result.Content);
            Assert.Contains(_harness.Log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("incorrect role claims", StringComparison.Ordinal));
            Assert.IsType<ObjectResult>(await _harness.Create().OidAuth("test", FromBrowser(authorization["state"])));
            await _harness.Sessions.DidNotReceiveWithAnyArgs().AuthenticateDirect(default!);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("missing")]
    public async Task Missing_role_claims_cannot_grant_access_when_roles_are_required(string? roleClaim)
    {
        Config.RoleClaim = roleClaim;
        var authorization = await Start();

        Assert.Equal(401, Assert.IsType<ContentResult>(await Callback(authorization)).StatusCode);
        Assert.False(States[authorization["state"]].Valid);
        Assert.Empty(_created);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Providers_without_required_roles_use_the_username_or_subject(bool nullRoles, bool subjectOnly)
    {
        Config.Roles = nullRoles ? null : Array.Empty<string>();
        Config.RoleClaim = null;
        if (subjectOnly)
        {
            _idp.Claims.Remove("preferred_username");
        }

        var authorization = await Start();
        // A configuration imported without scopes can still finish a flow already in progress.
        Config.OidScopes = null;

        Assert.Equal("text/html", Assert.IsType<ContentResult>(await Callback(authorization)).ContentType);
        var state = States[authorization["state"]];
        Assert.True(state.Valid);
        Assert.Equal(subjectOnly ? "subject-alice" : "alice", state.Username);
        Assert.False(state.Admin);
    }

    [Fact]
    public async Task A_custom_username_claim_and_avatar_template_use_the_provider_claims()
    {
        Config.DefaultUsernameClaim = " login ";
        Config.AvatarUrlFormat = "https://pictures.example.test/@{sub}/@{login}/@{missing}";
        _idp.Claims["login"] = "alice-custom";
        var authorization = await Start();

        await Callback(authorization);

        var state = States[authorization["state"]];
        Assert.Equal("alice-custom", state.Username);
        Assert.Equal("https://pictures.example.test/subject-alice/alice-custom/@{missing}", state.AvatarURL);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Static_permissions_and_optional_profile_loading_are_preserved(bool loadProfile)
    {
        Config.DoNotLoadProfile = !loadProfile;
        Config.EnableFolderRoles = false;
        Config.EnabledFolders = new[] { "static-folder" };
        Config.EnableLiveTvRoles = false;
        Config.EnableLiveTv = true;
        Config.EnableLiveTvManagement = true;
        Config.AdminRoles = null;
        var authorization = await Start();

        await Callback(authorization);

        var state = States[authorization["state"]];
        Assert.True(state.Valid);
        Assert.False(state.Admin);
        Assert.Equal(new[] { "static-folder" }, state.Folders);
        Assert.True(state.EnableLiveTv);
        Assert.True(state.EnableLiveTvManagement);
        Assert.Equal(loadProfile, _idp.Requests.Any(request => request.Path == "/userinfo"));
    }

    [Fact]
    public async Task Null_optional_permission_lists_grant_nothing_extra()
    {
        Config.AdminRoles = null;
        Config.FolderRoleMapping = null;
        Config.LiveTvRoles = null;
        Config.LiveTvManagementRoles = null;
        var authorization = await Start();

        await Callback(authorization);

        var state = States[authorization["state"]];
        Assert.True(state.Valid);
        Assert.False(state.Admin);
        Assert.Empty(state.Folders);
        Assert.False(state.EnableLiveTv);
        Assert.False(state.EnableLiveTvManagement);
    }

    [Fact]
    public async Task Linking_then_signing_in_uses_the_existing_account_and_persists_the_link()
    {
        var account = TestUsers.Create("local-alice", Guid.NewGuid());
        account.Password = "existing-password-hash";
        _harness.Users.GetUserById(account.Id).Returns(account);
        _harness.SignedInAs(account);
        // Another state ahead of the valid one must not prevent linking or sign-in.
        await Start();
        var authorization = await Start(linking: true);

        var page = Assert.IsType<ContentResult>(await Callback(authorization));
        Assert.Contains("if (true) await link(request, account);", page.Content, StringComparison.Ordinal);
        Assert.IsType<NoContentResult>(await _harness.Create().AddCanonicalLink("OID", "test", account.Id, FromBrowser(authorization["state"])));
        Assert.Equal(account.Id, Config.CanonicalLinks["alice"]);
        var saved = Assert.IsType<PluginConfiguration>(new TestXmlSerializer().DeserializeFromFile(typeof(PluginConfiguration), _scope.ConfigurationFile));
        Assert.Equal(account.Id, saved.OidConfigs["test"].CanonicalLinks["alice"]);

        Assert.IsType<OkObjectResult>(await _harness.Create().OidAuth("test", FromBrowser(authorization["state"])));
        Assert.Empty(_created);
        Assert.Equal("existing-password-hash", account.Password);
        await _harness.Sessions.Received(1).AuthenticateDirect(Arg.Is<AuthenticationRequest>(request => request.UserId == account.Id));
        Assert.IsType<OkResult>(await _harness.Create().DeleteCanonicalLink("oid", "test", account.Id, "alice"));
        Assert.Empty(Config.CanonicalLinks);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("unvalidated")]
    [InlineData("wrong-state")]
    public async Task Linking_requires_a_known_provider_and_validated_state(string failure)
    {
        var account = TestUsers.Create("local-alice", Guid.NewGuid());
        _harness.SignedInAs(account);
        var authorization = await Start(linking: true);
        var result = await _harness.Create().AddCanonicalLink("oid", failure == "unknown" ? "missing" : "test", account.Id,
            FromBrowser(failure == "wrong-state" ? "unknown-state" : authorization["state"]));

        if (failure == "unknown")
        {
            Assert.Equal("No matching provider found", Assert.IsType<BadRequestObjectResult>(result).Value);
        }
        else
        {
            Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
        }

        Assert.Empty(Config.CanonicalLinks);
    }

    [Fact]
    public async Task Disabling_a_provider_before_session_creation_refuses_sign_in()
    {
        var authorization = await Start();
        await Callback(authorization);
        Config.Enabled = false;

        Assert.IsType<ObjectResult>(await _harness.Create().OidAuth("test", FromBrowser(authorization["state"])));
        Assert.Empty(_created);
        await _harness.Sessions.DidNotReceiveWithAnyArgs().AuthenticateDirect(default!);
    }

    [Theory]
    [InlineData("http", "jellyfin.example.test:80", null, null, "http://jellyfin.example.test/jellyfin")]
    [InlineData("http", "jellyfin.example.test:8080", null, null, "http://jellyfin.example.test:8080/jellyfin")]
    [InlineData("https", "jellyfin.example.test:443", null, null, "https://jellyfin.example.test/jellyfin")]
    [InlineData("https", "jellyfin.example.test:8443", null, null, "https://jellyfin.example.test:8443/jellyfin")]
    [InlineData("http", "jellyfin.example.test", "https", 8920, "https://jellyfin.example.test:8920/jellyfin")]
    [InlineData("https", "jellyfin.example.test", "http", 8080, "http://jellyfin.example.test:8080/jellyfin")]
    [InlineData("https", "jellyfin.example.test", "javascript", null, "https://jellyfin.example.test/jellyfin")]
    public async Task Callback_addresses_respect_ports_path_bases_and_valid_scheme_overrides(string scheme, string host, string? schemeOverride, int? portOverride, string expectedBase)
    {
        Config.SchemeOverride = schemeOverride;
        Config.PortOverride = portOverride;
        var controller = _harness.Create(scheme: scheme, host: host, pathBase: "/jellyfin", path: "/sso/OID/start/test");

        var authorization = _idp.Authorize(Assert.IsType<RedirectResult>(await controller.OidChallenge("test")).Url);

        Assert.Equal(expectedBase + "/sso/OID/redirect/test", authorization["redirect_uri"]);
    }

    [Fact]
    public async Task An_account_deleted_before_session_creation_cannot_sign_in()
    {
        var account = TestUsers.Create("alice", Guid.NewGuid());
        account.Password = "existing-password-hash";
        Config.CanonicalLinks = new SerializableDictionary<string, Guid> { ["alice"] = account.Id };
        // The first lookup finds the linked account, but the second observes its deletion.
        _harness.Users.GetUserById(account.Id).Returns(account, (User?)null);
        var authorization = await Start();
        await Callback(authorization);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _harness.Create().OidAuth("test", FromBrowser(authorization["state"])));

        Assert.Equal("The account signing in no longer exists.", error.Message);
        await _harness.Sessions.DidNotReceiveWithAnyArgs().AuthenticateDirect(default!);
        Assert.Empty(_created);
    }

    [Fact]
    public async Task A_provider_removed_during_account_lookup_cannot_issue_a_session_without_a_link()
    {
        var account = TestUsers.Create("alice", Guid.NewGuid());
        account.Password = "existing-password-hash";
        Config.CanonicalLinks = new SerializableDictionary<string, Guid> { ["alice"] = account.Id };
        var authorization = await Start();
        await Callback(authorization);
        _harness.Users.GetUserById(account.Id).Returns(_ =>
        {
            _scope.Plugin.Configuration.OidConfigs.Remove("test");
            return account;
        });

        await Assert.ThrowsAsync<KeyNotFoundException>(() => _harness.Create().OidAuth("test", FromBrowser(authorization["state"])));

        await _harness.Sessions.DidNotReceiveWithAnyArgs().AuthenticateDirect(default!);
        Assert.Empty(_created);
    }
}
