using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SsoAuthentication.Api;
using Jellyfin.Plugin.SsoAuthentication.Config;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Jellyfin.Plugin.SsoAuthentication.Tests;

/// <summary>
/// A SAML sign-in end to end inside the controller: the provider's POST, the page's
/// call back with the signed response, and the account and session that come out.
/// </summary>
public sealed class SamlFlowTests : IDisposable
{
    private readonly SamlIdentityProvider _idp = new();
    private readonly PluginScope _scope = new();
    private readonly ControllerHarness _harness = new();
    private readonly List<AuthenticationRequest> _sessionRequests = new();

    public SamlFlowTests()
    {
        _scope.Plugin.Configuration.SamlConfigs["test"] = new SamlConfig
        {
            SamlEndpoint = "https://idp.example.test/saml",
            SamlClientId = "jellyfin",
            SamlCertificate = _idp.CertificateBase64,
            Enabled = true,
            EnableAuthorization = true,
            EnableAllFolders = false,
            EnabledFolders = new[] { "everyone-folder" },
            Roles = new[] { "jellyfin-users" },
            AdminRoles = new[] { "jellyfin-admins" },
            EnableFolderRoles = true,
            FolderRoleMapping = new List<FolderRoleMap> { new() { Role = "kids", Folders = new List<string> { "kids-folder" } } },
            EnableLiveTvRoles = true,
            LiveTvRoles = new[] { "live-tv" },
            LiveTvManagementRoles = new[] { "live-tv-admins" },
        };

        _harness.Sessions.AuthenticateDirect(Arg.Do<AuthenticationRequest>(_sessionRequests.Add))
            .Returns(new AuthenticationResult { AccessToken = "session-token" });
    }

    public void Dispose()
    {
        _scope.Dispose();
        _idp.Dispose();
    }

    private SamlConfig Config => _scope.Plugin.Configuration.SamlConfigs["test"];

    private string SignedResponse(string nameId, params string[] roles)
        => SamlIdentityProvider.Base64(_idp.Sign(SamlIdentityProvider.Unsigned(nameId, roles)));

    private static AuthResponse FromBrowser(string data) => new()
    {
        Data = data,
        DeviceID = "device-1",
        DeviceName = "Firefox",
        AppName = "Jellyfin Web",
        AppVersion = "10.8.0",
    };

    /// <summary>The user manager creates accounts as Jellyfin's does, and finds them by ID afterwards.</summary>
    private List<User> AccountsCreatedOnSignIn()
    {
        var created = new List<User>();
        _harness.Users.CreateUserAsync(Arg.Any<string>()).Returns(call =>
        {
            var user = TestUsers.Create(call.Arg<string>(), Guid.NewGuid());
            created.Add(user);
            _harness.Users.GetUserById(user.Id).Returns(user);
            return Task.FromResult(user);
        });
        return created;
    }

    /// <summary>
    /// The user manager as Jellyfin 12 behaves: a lookup hands back a fresh copy of what was
    /// last saved, not the instance CreateUserAsync returned, so a change to an account only
    /// counts once that instance goes through UpdateUserAsync. Returns what was saved.
    /// </summary>
    private Dictionary<Guid, (string Provider, string? Password, bool Admin)> AccountsStoredLikeJellyfin12()
    {
        var saved = new Dictionary<Guid, (string Provider, string? Password, bool Admin)>();
        _harness.Users.CreateUserAsync(Arg.Any<string>()).Returns(call =>
        {
            var created = TestUsers.Create(call.Arg<string>(), Guid.NewGuid());
            saved[created.Id] = (created.AuthenticationProviderId, created.Password, false);
            _harness.Users.GetUserById(created.Id).Returns(_ =>
            {
                var copy = TestUsers.Create(created.Username, created.Id);
                (copy.AuthenticationProviderId, copy.Password, var admin) = saved[created.Id];
                copy.SetPermission(PermissionKind.IsAdministrator, admin);
                return copy;
            });
            return Task.FromResult(created);
        });
        _harness.Users.UpdateUserAsync(Arg.Do<User>(user =>
            saved[user.Id] = (user.AuthenticationProviderId, user.Password, user.HasPermission(PermissionKind.IsAdministrator))));
        return saved;
    }

    [Fact]
    public async Task A_new_account_is_saved_with_no_way_to_sign_in_by_password()
    {
        // Left unsaved, the account keeps Jellyfin's default provider and no password, which
        // Jellyfin accepts an empty password for: anyone knowing the name could sign in.
        var saved = AccountsStoredLikeJellyfin12();

        await _harness.Create().SamlAuth("test", FromBrowser(SignedResponse("alice", "jellyfin-users", "jellyfin-admins")));

        var account = Assert.Single(saved).Value;
        Assert.Equal("Jellyfin.Plugin.SsoAuthentication.Api.SSOController", account.Provider);
        Assert.StartsWith("$" + FakeCryptoProvider.HashId + "$", account.Password, StringComparison.Ordinal);
        Assert.True(account.Admin);
    }

    private SSOController PostedBack(string samlResponse, string pathBase = "")
    {
        var controller = _harness.Create(pathBase: pathBase);
        controller.HttpContext.Request.ContentType = "application/x-www-form-urlencoded";
        controller.HttpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues> { ["SAMLResponse"] = samlResponse });
        return controller;
    }

    [Fact]
    public void The_providers_post_back_serves_the_page_that_finishes_signing_in()
    {
        var response = SignedResponse("alice", "jellyfin-users");

        var result = Assert.IsType<ContentResult>(PostedBack(response).SamlPost("test"));

        Assert.Equal("text/html", result.ContentType);
        Assert.Contains("'https://jellyfin.example.test/sso/SAML/Auth/test'", result.Content, StringComparison.Ordinal);
        Assert.Contains("if (false) await link(request, account);", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Relay_state_linking_serves_a_page_that_links_first()
    {
        var result = Assert.IsType<ContentResult>(PostedBack(SignedResponse("alice", "jellyfin-users")).SamlPost("test", relayState: "linking"));

        Assert.Contains("if (true) await link(request, account);", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Someone_without_an_allowed_role_is_turned_away()
    {
        var result = Assert.IsType<ContentResult>(PostedBack(SignedResponse("mallory", "visitors")).SamlPost("test"));

        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
    }

    [Fact]
    public void Any_validly_signed_user_gets_in_when_no_roles_are_required()
    {
        Config.Roles = Array.Empty<string>();

        var result = Assert.IsType<ContentResult>(PostedBack(SignedResponse("anyone")).SamlPost("test"));

        Assert.Equal("text/html", result.ContentType);
    }

    [Fact]
    public void A_forged_post_back_is_refused()
    {
        using var other = new SamlIdentityProvider("CN=Someone else");
        var forged = SamlIdentityProvider.Base64(other.Sign(SamlIdentityProvider.Unsigned("alice", new[] { "jellyfin-users" })));

        var result = Assert.IsType<ObjectResult>(PostedBack(forged).SamlPost("test"));

        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
    }

    [Fact]
    public void An_unknown_or_disabled_provider_is_refused()
    {
        Assert.IsType<BadRequestObjectResult>(PostedBack(SignedResponse("alice")).SamlPost("nope"));

        Config.Enabled = false;
        var disabled = Assert.IsType<ContentResult>(PostedBack(SignedResponse("alice", "jellyfin-users")).SamlPost("test"));
        Assert.Equal(StatusCodes.Status400BadRequest, disabled.StatusCode);
    }

    [Fact]
    public void The_page_calls_back_to_the_address_the_request_came_in_on()
    {
        Config.SchemeOverride = "https";
        Config.PortOverride = 8920;
        var controller = PostedBack(SignedResponse("alice", "jellyfin-users"), pathBase: "/jellyfin");
        controller.HttpContext.Request.Scheme = "http";

        var result = Assert.IsType<ContentResult>(controller.SamlPost("test"));

        Assert.Contains("'https://jellyfin.example.test:8920/jellyfin/sso/SAML/Auth/test'", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_ports_are_left_out_of_the_address()
    {
        Config.PortOverride = 443;

        var result = Assert.IsType<ContentResult>(PostedBack(SignedResponse("alice", "jellyfin-users")).SamlPost("test"));

        Assert.Contains("'https://jellyfin.example.test/sso/SAML/Auth/test'", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_first_sign_in_creates_the_account_with_the_permissions_its_roles_give()
    {
        var created = AccountsCreatedOnSignIn();

        var result = await _harness.Create().SamlAuth("test", FromBrowser(SignedResponse("alice", "jellyfin-users", "jellyfin-admins", "kids", "live-tv")));

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("session-token", Assert.IsType<AuthenticationResult>(ok.Value).AccessToken);
        var user = Assert.Single(created);
        Assert.Equal("alice", user.Username);
        Assert.True(user.HasPermission(PermissionKind.IsAdministrator));
        Assert.False(user.HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal(new[] { "kids-folder" }, user.GetPreferenceValues<string>(PreferenceKind.EnabledFolders));
        Assert.True(user.HasPermission(PermissionKind.EnableLiveTvAccess));
        Assert.False(user.HasPermission(PermissionKind.EnableLiveTvManagement));
        await _harness.Users.Received().UpdateUserAsync(user);
    }

    [Fact]
    public async Task A_new_account_cannot_sign_in_with_a_password()
    {
        var created = AccountsCreatedOnSignIn();

        await _harness.Create().SamlAuth("test", FromBrowser(SignedResponse("alice", "jellyfin-users")));

        var user = Assert.Single(created);
        Assert.Equal("Jellyfin.Plugin.SsoAuthentication.Api.SSOController", user.AuthenticationProviderId);
        Assert.Equal(1, _harness.Crypto.Calls);
        Assert.StartsWith("$" + FakeCryptoProvider.HashId + "$", user.Password, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Roles_that_are_not_mapped_give_no_extra_access()
    {
        var created = AccountsCreatedOnSignIn();

        await _harness.Create().SamlAuth("test", FromBrowser(SignedResponse("bob", "jellyfin-users")));

        var user = Assert.Single(created);
        Assert.False(user.HasPermission(PermissionKind.IsAdministrator));
        Assert.Empty(user.GetPreferenceValues<string>(PreferenceKind.EnabledFolders));
        Assert.False(user.HasPermission(PermissionKind.EnableLiveTvAccess));
    }

    [Fact]
    public async Task Without_folder_roles_every_account_gets_the_default_folders()
    {
        Config.EnableFolderRoles = false;
        var created = AccountsCreatedOnSignIn();

        await _harness.Create().SamlAuth("test", FromBrowser(SignedResponse("bob", "jellyfin-users", "kids")));

        Assert.Equal(new[] { "everyone-folder" }, Assert.Single(created).GetPreferenceValues<string>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task The_session_is_issued_for_the_device_that_signed_in()
    {
        AccountsCreatedOnSignIn();

        await _harness.Create().SamlAuth("test", FromBrowser(SignedResponse("alice", "jellyfin-users")));

        var request = Assert.Single(_sessionRequests);
        Assert.Equal("alice", request.Username);
        Assert.Equal("device-1", request.DeviceId);
        Assert.Equal("Firefox", request.DeviceName);
        Assert.Equal("Jellyfin Web", request.App);
    }

    [Fact]
    public async Task The_account_is_linked_to_the_providers_name_and_the_link_is_saved()
    {
        var created = AccountsCreatedOnSignIn();

        await _harness.Create().SamlAuth("test", FromBrowser(SignedResponse("alice", "jellyfin-users")));

        var user = Assert.Single(created);
        Assert.Equal(user.Id, Config.CanonicalLinks["alice"]);
        using var reloaded = new PluginScope(File.ReadAllText(_scope.ConfigurationFile));
        Assert.Equal(user.Id, reloaded.Plugin.Configuration.SamlConfigs["test"].CanonicalLinks["alice"]);
    }

    [Fact]
    public async Task A_returning_user_is_found_by_their_link_even_after_a_rename()
    {
        var existing = TestUsers.Create("alice-renamed", Guid.NewGuid());
        Config.CanonicalLinks = new SerializableDictionary<string, Guid> { ["alice"] = existing.Id };
        _harness.Users.GetUserById(existing.Id).Returns(existing);

        await _harness.Create().SamlAuth("test", FromBrowser(SignedResponse("alice", "jellyfin-users")));

        await _harness.Users.DidNotReceive().CreateUserAsync(Arg.Any<string>());
        Assert.Equal("alice-renamed", Assert.Single(_sessionRequests).Username);
    }

    [Fact]
    public async Task Completing_a_sign_in_still_requires_an_allowed_role()
    {
        // The page calls this with the response the provider posted, but nothing stops a
        // browser calling it directly with its own signed response, so it checks roles too.
        AccountsCreatedOnSignIn();

        var result = await _harness.Create().SamlAuth("test", FromBrowser(SignedResponse("mallory", "visitors")));

        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<ContentResult>(result).StatusCode);
        await _harness.Users.DidNotReceive().CreateUserAsync(Arg.Any<string>());
        Assert.Empty(_sessionRequests);
        Assert.Empty(Config.CanonicalLinks);
    }

    [Fact]
    public async Task Completing_a_sign_in_needs_no_role_when_none_are_required()
    {
        Config.Roles = Array.Empty<string>();
        var created = AccountsCreatedOnSignIn();

        var result = await _harness.Create().SamlAuth("test", FromBrowser(SignedResponse("anyone")));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("anyone", Assert.Single(created).Username);
    }

    [Fact]
    public void Leaving_roles_unset_means_no_role_is_required()
    {
        // A provider added through the API without Roles has none, rather than an empty list.
        Config.Roles = null!;

        var result = Assert.IsType<ContentResult>(PostedBack(SignedResponse("anyone")).SamlPost("test"));

        Assert.Equal("text/html", result.ContentType);
    }

    [Fact]
    public async Task An_existing_account_with_the_same_name_is_taken_over_as_documented()
    {
        // The README warns about this: an SSO name matching a Jellyfin username signs in as
        // that account and overwrites its permissions.
        var existing = TestUsers.Create("alice", Guid.NewGuid());
        _harness.Users.GetUserByName("alice").Returns(existing);
        _harness.Users.GetUserById(existing.Id).Returns(existing);

        await _harness.Create().SamlAuth("test", FromBrowser(SignedResponse("alice", "jellyfin-users")));

        await _harness.Users.DidNotReceive().CreateUserAsync(Arg.Any<string>());
        Assert.Equal(existing.Id, Config.CanonicalLinks["alice"]);
    }

    /// <summary>
    /// An account already on the server, stored as Jellyfin 12 stores accounts: each lookup
    /// hands back a fresh copy of what was last saved. Returns what was saved.
    /// </summary>
    private Dictionary<Guid, (string? Provider, string? Password)> ExistingAccount(string name, string? provider, string? password, bool linked = false)
    {
        var id = Guid.NewGuid();
        var saved = new Dictionary<Guid, (string? Provider, string? Password)> { [id] = (provider, password) };
        User Copy()
        {
            var copy = TestUsers.Create(name, id);
            // Declared non-null, but rows with no provider at all exist, and are tested here.
            copy.AuthenticationProviderId = saved[id].Provider!;
            copy.Password = saved[id].Password;
            return copy;
        }

        _harness.Users.GetUserByName(name).Returns(_ => Copy());
        _harness.Users.GetUserById(id).Returns(_ => Copy());
        _harness.Users.UpdateUserAsync(Arg.Do<User>(user => saved[user.Id] = (user.AuthenticationProviderId, user.Password)));
        if (linked)
        {
            Config.CanonicalLinks = new SerializableDictionary<string, Guid> { [name] = id };
        }

        return saved;
    }

    [Fact]
    public async Task An_account_open_to_an_empty_password_is_locked_to_sso_when_its_owner_signs_in()
    {
        // What 9p4's plugin left behind on Jellyfin 12: Jellyfin's password provider, no password.
        var saved = ExistingAccount("alice", "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", null);

        var result = await _harness.Create().SamlAuth("test", FromBrowser(SignedResponse("alice", "jellyfin-users")));

        Assert.IsType<OkObjectResult>(result);
        var account = Assert.Single(saved).Value;
        Assert.Equal("Jellyfin.Plugin.SsoAuthentication.Api.SSOController", account.Provider);
        Assert.StartsWith("$" + FakeCryptoProvider.HashId + "$", account.Password, StringComparison.Ordinal);
        var (_, message) = Assert.Single(_harness.Log.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains("alice", message, StringComparison.Ordinal);
        await _harness.Users.DidNotReceive().CreateUserAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task An_account_found_through_its_link_with_no_provider_or_password_is_locked_too()
    {
        // No provider at all means every provider Jellyfin has, its password provider included.
        var saved = ExistingAccount("alice-renamed", null, null, linked: true);
        Config.CanonicalLinks = new SerializableDictionary<string, Guid> { ["alice"] = saved.Keys.Single() };

        await _harness.Create().SamlAuth("test", FromBrowser(SignedResponse("alice", "jellyfin-users")));

        var account = Assert.Single(saved).Value;
        Assert.Equal("Jellyfin.Plugin.SsoAuthentication.Api.SSOController", account.Provider);
        Assert.False(string.IsNullOrEmpty(account.Password));
    }

    [Fact]
    public async Task An_account_with_a_password_keeps_it()
    {
        var saved = ExistingAccount("alice", "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "$PBKDF2$existing-hash");

        await _harness.Create().SamlAuth("test", FromBrowser(SignedResponse("alice", "jellyfin-users")));

        Assert.Equal(("Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "$PBKDF2$existing-hash"), Assert.Single(saved).Value);
        Assert.DoesNotContain(_harness.Log.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public async Task An_account_another_provider_signs_in_is_left_to_it()
    {
        // LDAP, for one, checks its own directory and never accepts an empty password.
        var saved = ExistingAccount("alice", "Jellyfin.Plugin.LDAP_Auth.LdapAuthenticationProviderPlugin", null);

        await _harness.Create().SamlAuth("test", FromBrowser(SignedResponse("alice", "jellyfin-users")));

        var account = Assert.Single(saved).Value;
        Assert.Equal("Jellyfin.Plugin.LDAP_Auth.LdapAuthenticationProviderPlugin", account.Provider);
        Assert.Null(account.Password);
        Assert.DoesNotContain(_harness.Log.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public async Task A_default_provider_hands_the_account_back_to_password_sign_in()
    {
        Config.DefaultProvider = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";
        var created = AccountsCreatedOnSignIn();

        await _harness.Create().SamlAuth("test", FromBrowser(SignedResponse("alice", "jellyfin-users")));

        Assert.Equal("Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", Assert.Single(created).AuthenticationProviderId);
    }

    [Fact]
    public async Task Without_authorization_the_accounts_permissions_are_left_alone()
    {
        Config.EnableAuthorization = false;
        var created = AccountsCreatedOnSignIn();

        await _harness.Create().SamlAuth("test", FromBrowser(SignedResponse("alice", "jellyfin-users", "jellyfin-admins")));

        Assert.False(Assert.Single(created).HasPermission(PermissionKind.IsAdministrator));
    }

    [Fact]
    public async Task A_forged_response_signs_nobody_in()
    {
        using var other = new SamlIdentityProvider("CN=Someone else");
        var forged = SamlIdentityProvider.Base64(other.Sign(SamlIdentityProvider.Unsigned("alice", new[] { "jellyfin-admins" })));

        var result = await _harness.Create().SamlAuth("test", FromBrowser(forged));

        Assert.IsType<ObjectResult>(result);
        await _harness.Users.DidNotReceive().CreateUserAsync(Arg.Any<string>());
        Assert.Empty(_sessionRequests);
    }

    [Fact]
    public async Task Signing_in_with_an_unknown_or_disabled_provider_is_refused()
    {
        Assert.IsType<BadRequestObjectResult>(await _harness.Create().SamlAuth("nope", FromBrowser(SignedResponse("alice"))));

        Config.Enabled = false;
        Assert.IsType<ObjectResult>(await _harness.Create().SamlAuth("test", FromBrowser(SignedResponse("alice", "jellyfin-users"))));
        Assert.Empty(_sessionRequests);
    }

    [Fact]
    public void Starting_a_sign_in_redirects_to_the_provider_with_a_request()
    {
        var result = _harness.Create(path: "/sso/SAML/start/test").SamlChallenge("test");

        Assert.StartsWith("https://idp.example.test/saml?SAMLRequest=", result.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("RelayState", result.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void Starting_a_link_carries_relay_state()
    {
        var result = _harness.Create(path: "/sso/SAML/start/test").SamlChallenge("test", isLinking: true);

        Assert.EndsWith("&RelayState=linking", result.Url, StringComparison.Ordinal);
    }
}
