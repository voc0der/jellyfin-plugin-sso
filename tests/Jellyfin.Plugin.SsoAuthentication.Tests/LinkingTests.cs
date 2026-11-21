using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SsoAuthentication.Api;
using Jellyfin.Plugin.SsoAuthentication.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SsoAuthentication.Tests;

/// <summary>
/// Account linking from /SSOViews/linking: a signed-in user attaches a provider identity
/// to their own account, or an administrator to anyone's. Anyone else is refused.
/// </summary>
public sealed class LinkingTests : IDisposable
{
    private readonly SamlIdentityProvider _idp = new();
    private readonly PluginScope _scope = new();
    private readonly ControllerHarness _harness = new();
    private readonly User _alice = TestUsers.Create("alice", Guid.NewGuid());
    private readonly User _bob = TestUsers.Create("bob", Guid.NewGuid());
    private readonly User _admin = TestUsers.Create("admin", Guid.NewGuid());

    public LinkingTests()
    {
        _admin.SetPermission(PermissionKind.IsAdministrator, true);
        _scope.Plugin.Configuration.SamlConfigs["keycloak"] = new SamlConfig
        {
            SamlEndpoint = "https://idp.example.test/saml",
            SamlClientId = "jellyfin",
            SamlCertificate = _idp.CertificateBase64,
            Enabled = true,
            Roles = Array.Empty<string>(),
        };
        _scope.Plugin.Configuration.OidConfigs["authelia"] = new OidConfig { Enabled = true, OidScopes = Array.Empty<string>() };
    }

    public void Dispose()
    {
        _scope.Dispose();
        _idp.Dispose();
    }

    private AuthResponse Signed(string nameId) => new()
    {
        Data = SamlIdentityProvider.Base64(_idp.Sign(SamlIdentityProvider.Unsigned(nameId))),
        DeviceID = "device-1",
        DeviceName = "Firefox",
        AppName = "Jellyfin Web",
        AppVersion = "10.8.0",
    };

    private SerializableDictionary<string, Guid> SamlLinks => _scope.Plugin.Configuration.SamlConfigs["keycloak"].CanonicalLinks;

    /// <summary>
    /// Seeds a link the way the controller stores one: by assigning the dictionary back.
    /// While a provider has no links, CanonicalLinks hands out a new empty dictionary on
    /// every read, so writing into what it returns would be lost.
    /// </summary>
    private void LinkSaml(string name, Guid userId)
    {
        var links = new SerializableDictionary<string, Guid>(SamlLinks) { [name] = userId };
        _scope.Plugin.Configuration.SamlConfigs["keycloak"].CanonicalLinks = links;
    }

    private static int Status(ActionResult result) => result switch
    {
        ObjectResult objectResult => objectResult.StatusCode ?? StatusCodes.Status200OK,
        StatusCodeResult statusCodeResult => statusCodeResult.StatusCode,
        _ => throw new InvalidOperationException("Unexpected result " + result.GetType()),
    };

    [Fact]
    public async Task A_user_links_a_provider_identity_to_their_own_account()
    {
        _harness.SignedInAs(_alice);

        var result = await _harness.Create().AddCanonicalLink("saml", "keycloak", _alice.Id, Signed("alice@idp"));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(_alice.Id, SamlLinks["alice@idp"]);
    }

    [Fact]
    public async Task A_user_cannot_link_an_identity_to_someone_elses_account()
    {
        _harness.SignedInAs(_alice);

        var result = await _harness.Create().AddCanonicalLink("saml", "keycloak", _bob.Id, Signed("alice@idp"));

        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
        Assert.Empty(SamlLinks);
    }

    [Fact]
    public async Task An_administrator_links_an_identity_to_anyones_account()
    {
        _harness.SignedInAs(_admin);

        var result = await _harness.Create().AddCanonicalLink("saml", "keycloak", _bob.Id, Signed("bob@idp"));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(_bob.Id, SamlLinks["bob@idp"]);
    }

    [Fact]
    public async Task A_user_barred_from_changing_preferences_cannot_link()
    {
        _alice.EnableUserPreferenceAccess = false;
        _harness.SignedInAs(_alice);

        var result = await _harness.Create().AddCanonicalLink("saml", "keycloak", _alice.Id, Signed("alice@idp"));

        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
    }

    [Fact]
    public async Task A_forged_identity_is_not_linked()
    {
        using var other = new SamlIdentityProvider("CN=Someone else");
        _harness.SignedInAs(_alice);
        var forged = Signed("alice@idp");
        forged.Data = SamlIdentityProvider.Base64(other.Sign(SamlIdentityProvider.Unsigned("admin@idp")));

        var result = await _harness.Create().AddCanonicalLink("saml", "keycloak", _alice.Id, forged);

        Assert.IsType<ObjectResult>(result);
        Assert.Empty(SamlLinks);
    }

    [Fact]
    public async Task A_user_removes_their_own_link()
    {
        LinkSaml("alice@idp", _alice.Id);
        _harness.SignedInAs(_alice);

        var result = await _harness.Create().DeleteCanonicalLink("saml", "keycloak", _alice.Id, "alice@idp");

        Assert.IsType<OkResult>(result);
        Assert.Empty(_scope.Plugin.Configuration.SamlConfigs["keycloak"].CanonicalLinks);
    }

    [Fact]
    public async Task A_link_belonging_to_another_account_is_not_removed()
    {
        LinkSaml("bob@idp", _bob.Id);
        _harness.SignedInAs(_alice);

        var result = await _harness.Create().DeleteCanonicalLink("saml", "keycloak", _alice.Id, "bob@idp");

        Assert.Equal(StatusCodes.Status409Conflict, Status(result));
        Assert.Equal(_bob.Id, SamlLinks["bob@idp"]);
    }

    [Fact]
    public async Task A_user_cannot_remove_links_from_someone_elses_account()
    {
        LinkSaml("bob@idp", _bob.Id);
        _harness.SignedInAs(_alice);

        var result = await _harness.Create().DeleteCanonicalLink("saml", "keycloak", _bob.Id, "bob@idp");

        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
        Assert.Equal(_bob.Id, SamlLinks["bob@idp"]);
    }

    [Fact]
    public async Task A_user_lists_their_own_links_and_nobody_elses()
    {
        LinkSaml("alice@idp", _alice.Id);
        LinkSaml("bob@idp", _bob.Id);
        _scope.Plugin.Configuration.OidConfigs["authelia"].CanonicalLinks = new SerializableDictionary<string, Guid> { ["alice"] = _alice.Id };
        _harness.SignedInAs(_alice);
        var controller = _harness.Create();

        var saml = (await controller.GetSamlLinksByUser(_alice.Id)).Value!;
        var oid = (await controller.GetOidLinksByUser(_alice.Id)).Value!;

        Assert.Equal(new[] { "alice@idp" }, saml["keycloak"]);
        Assert.Equal(new[] { "alice" }, oid["authelia"]);
    }

    [Fact]
    public async Task A_user_cannot_list_someone_elses_links()
    {
        _harness.SignedInAs(_alice);
        var controller = _harness.Create();

        var saml = await controller.GetSamlLinksByUser(_bob.Id);
        var oid = await controller.GetOidLinksByUser(_bob.Id);

        Assert.Equal(StatusCodes.Status403Forbidden, Status(saml.Result!));
        Assert.Equal(StatusCodes.Status403Forbidden, Status(oid.Result!));
    }

    [Fact]
    public async Task An_administrator_lists_anyones_links()
    {
        LinkSaml("bob@idp", _bob.Id);
        _harness.SignedInAs(_admin);

        var saml = (await _harness.Create().GetSamlLinksByUser(_bob.Id)).Value!;

        Assert.Equal(new[] { "bob@idp" }, saml["keycloak"]);
    }

    [Fact]
    public async Task A_missing_saml_provider_cannot_be_linked()
    {
        _harness.SignedInAs(_alice);

        var result = await _harness.Create().AddCanonicalLink("saml", "missing", _alice.Id, Signed("alice@idp"));

        Assert.Equal("No matching provider found", Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Empty(SamlLinks);
    }

    [Fact]
    public async Task An_unknown_protocol_cannot_add_or_delete_links()
    {
        _harness.SignedInAs(_alice);
        var controller = _harness.Create();

        await Assert.ThrowsAsync<ArgumentException>(() => controller.AddCanonicalLink("invalid", "keycloak", _alice.Id, Signed("alice@idp")));
        await Assert.ThrowsAsync<ArgumentException>(() => controller.DeleteCanonicalLink("invalid", "keycloak", _alice.Id, "alice@idp"));
        Assert.Empty(SamlLinks);
    }
}
