using System.Reflection;
using Jellyfin.Plugin.SsoAuthentication.Api;
using Jellyfin.Plugin.SsoAuthentication.Config;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using NSubstitute;

namespace Jellyfin.Plugin.SsoAuthentication.Tests;

/// <summary>
/// The provider settings API the settings page and the README's curl examples use.
/// </summary>
public sealed class ProviderEndpointTests : IDisposable
{
    private readonly PluginScope _scope = new();
    private readonly ControllerHarness _harness = new();

    public void Dispose() => _scope.Dispose();

    private static OidConfig Oid(string secret = "oid-secret") => new()
    {
        OidEndpoint = "https://auth.example.test",
        OidClientId = "jellyfin",
        OidSecret = secret,
        Enabled = true,
        OidScopes = new[] { "groups" },
        Roles = Array.Empty<string>(),
    };

    private static SamlConfig Saml() => new()
    {
        SamlEndpoint = "https://idp.example.test/saml",
        SamlClientId = "jellyfin",
        SamlCertificate = "MIIC-not-a-real-certificate",
        Enabled = true,
        Roles = Array.Empty<string>(),
    };

    [Fact]
    public void Adding_an_openid_provider_saves_it()
    {
        _harness.Create().OidAdd("authelia", Oid());

        var saved = new PluginScope(File.ReadAllText(_scope.ConfigurationFile));
        using (saved)
        {
            Assert.Equal("https://auth.example.test", saved.Plugin.Configuration.OidConfigs["authelia"].OidEndpoint);
        }
    }

    [Fact]
    public void Adding_a_provider_under_an_existing_name_replaces_it()
    {
        var controller = _harness.Create();
        controller.OidAdd("authelia", Oid("first"));
        controller.OidAdd("authelia", Oid("second"));

        Assert.Equal("second", Assert.Single(_scope.Plugin.Configuration.OidConfigs).Value.OidSecret);
    }

    [Fact]
    public void Deleting_an_openid_provider_removes_it()
    {
        var controller = _harness.Create();
        controller.OidAdd("authelia", Oid());
        controller.OidAdd("other", Oid());

        controller.OidDel("authelia");

        Assert.Equal(new[] { "other" }, _scope.Plugin.Configuration.OidConfigs.Keys);
    }

    [Fact]
    public void Administrators_get_every_providers_settings()
    {
        var controller = _harness.Create();
        controller.OidAdd("authelia", Oid());
        controller.SamlAdd("keycloak", Saml());

        var oid = Assert.IsType<OkObjectResult>(controller.OidProviders());
        var saml = Assert.IsType<OkObjectResult>(controller.SamlProviders());

        Assert.Equal("oid-secret", Assert.IsType<SerializableDictionary<string, OidConfig>>(oid.Value)["authelia"].OidSecret);
        Assert.Equal("MIIC-not-a-real-certificate", Assert.IsType<SerializableDictionary<string, SamlConfig>>(saml.Value)["keycloak"].SamlCertificate);
    }

    [Fact]
    public void The_public_name_lists_carry_names_and_nothing_else()
    {
        // These are open to anyone, for sign-in buttons, so they must not leak settings.
        var controller = _harness.Create();
        controller.OidAdd("authelia", Oid());
        controller.SamlAdd("keycloak", Saml());

        var oidNames = Assert.IsType<OkObjectResult>(controller.OidProviderNames()).Value;
        var samlNames = Assert.IsType<OkObjectResult>(controller.SamlProviderNames()).Value;

        Assert.Equal(new[] { "authelia" }, Assert.IsAssignableFrom<IEnumerable<string>>(oidNames));
        Assert.Equal(new[] { "keycloak" }, Assert.IsAssignableFrom<IEnumerable<string>>(samlNames));
    }

    [Fact]
    public void Adding_and_deleting_a_saml_provider_saves_each_change()
    {
        var controller = _harness.Create();

        Assert.IsType<OkResult>(controller.SamlAdd("keycloak", Saml()));
        Assert.Contains("keycloak", File.ReadAllText(_scope.ConfigurationFile), StringComparison.Ordinal);

        Assert.IsType<OkResult>(controller.SamlDel("keycloak"));
        Assert.DoesNotContain("keycloak", File.ReadAllText(_scope.ConfigurationFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Unregistering_a_user_hands_them_to_another_provider()
    {
        var user = TestUsers.Create("alice", Guid.NewGuid());
        user.AuthenticationProviderId = typeof(SSOController).FullName!;
        _harness.Users.GetUserByName("alice").Returns(user);

        var result = _harness.Create().Unregister("alice", "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider");

        Assert.IsType<OkResult>(result);
        Assert.Equal("Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", user.AuthenticationProviderId);
    }

    private static readonly string[] AdministratorOnly =
    {
        nameof(SSOController.OidAdd),
        nameof(SSOController.OidDel),
        nameof(SSOController.OidProviders),
        nameof(SSOController.OidStates),
        nameof(SSOController.SamlAdd),
        nameof(SSOController.SamlDel),
        nameof(SSOController.SamlProviders),
        nameof(SSOController.Unregister),
    };

    public static TheoryData<string> AdministratorOnlyActions => new(AdministratorOnly);

    [Theory]
    [MemberData(nameof(AdministratorOnlyActions))]
    public void Settings_and_account_changes_need_an_administrator(string action)
    {
        var method = typeof(SSOController).GetMethod(action)!;

        var authorize = method.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.Equal(Policies.RequiresElevation, authorize!.Policy);
    }

    [Fact]
    public void Every_other_endpoint_is_public_or_checks_the_user_itself()
    {
        // Sign-in has to work signed out; linking requires a session and then checks that
        // it is the user's own account or an administrator's (see LinkingTests).
        var publicOrSelf = new HashSet<string>
        {
            nameof(SSOController.OidPost), nameof(SSOController.OidChallenge), nameof(SSOController.OidAuth),
            nameof(SSOController.OidProviderNames), nameof(SSOController.SamlProviderNames),
            nameof(SSOController.SamlPost), nameof(SSOController.SamlChallenge), nameof(SSOController.SamlAuth),
            nameof(SSOController.AddCanonicalLink), nameof(SSOController.DeleteCanonicalLink),
            nameof(SSOController.GetSamlLinksByUser), nameof(SSOController.GetOidLinksByUser),
        };
        var adminOnly = AdministratorOnly.ToHashSet();

        var endpoints = typeof(SSOController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any())
            .Select(method => method.Name)
            .ToHashSet();

        // A new endpoint must be added to one list or the other, deliberately.
        Assert.Equal(publicOrSelf.Union(adminOnly).OrderBy(name => name), endpoints.OrderBy(name => name));
    }
}
