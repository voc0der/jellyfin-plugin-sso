using Jellyfin.Plugin.SsoAuthentication.Config;

namespace Jellyfin.Plugin.SsoAuthentication.Tests;

/// <summary>
/// Settings files outlive the plugin that wrote them. Updates keep this plugin's own, and
/// uninstalling 9p4's plugin leaves its SSO-Auth.xml behind, which this one imports on first
/// start. So every build has to read the files earlier ones wrote, its own and 9p4's. The
/// fixture is laid out exactly as 9p4's SSO-Auth 4.0.0.4 writes it on Jellyfin 10.11, with
/// made-up hosts, secrets and accounts.
/// </summary>
public sealed class ConfigurationCompatibilityTests
{
    private static string Fixture() => File.ReadAllText(Path.Join(AppContext.BaseDirectory, "Fixtures", "SSO-Auth-4.0.0.4.xml"));

    [Fact]
    public void Settings_left_by_9p4s_4_0_0_4_import_every_openid_provider_value()
    {
        using var scope = new PluginScope(legacyConfigurationXml: Fixture());

        var oid = Assert.Single(scope.Plugin.Configuration.OidConfigs);
        Assert.Equal("authelia", oid.Key);
        var config = oid.Value;
        Assert.Equal("https://auth.example.test", config.OidEndpoint);
        Assert.Equal("jellyfin", config.OidClientId);
        Assert.Equal("fixture-secret-not-real", config.OidSecret);
        Assert.True(config.Enabled);
        Assert.True(config.EnableAuthorization);
        Assert.True(config.EnableAllFolders);
        Assert.Empty(config.EnabledFolders);
        Assert.Equal(new[] { "admins" }, config.AdminRoles);
        Assert.Equal(new[] { "jellyfin" }, config.Roles);
        Assert.Equal("groups", config.RoleClaim);
        Assert.Equal(new[] { "groups" }, config.OidScopes);
        Assert.Equal("https", config.SchemeOverride);
        Assert.Null(config.PortOverride);
        Assert.True(config.NewPath);
        Assert.Equal("preferred_username", config.DefaultUsernameClaim);
        Assert.True(config.DisablePushedAuthorization);
        Assert.False(config.DoNotLoadProfile);
        Assert.Equal(Guid.Parse("8c1f4e2a-9d3b-4a6e-b7c5-1e0f2d3c4b5a"), config.CanonicalLinks["alice"]);
        Assert.Equal(Guid.Parse("5a4b3c2d-1e0f-4a9b-8c7d-6e5f4a3b2c1d"), config.CanonicalLinks["bob"]);
    }

    [Fact]
    public void Settings_left_by_9p4s_4_0_0_4_import_every_saml_provider_value()
    {
        using var scope = new PluginScope(legacyConfigurationXml: Fixture());

        var saml = Assert.Single(scope.Plugin.Configuration.SamlConfigs);
        Assert.Equal("keycloak", saml.Key);
        var config = saml.Value;
        Assert.Equal("https://keycloak.example.test/realms/media/protocol/saml", config.SamlEndpoint);
        Assert.Equal("jellyfin-saml", config.SamlClientId);
        Assert.Equal(new[] { "f137a2dd21bbc1b99aa5c0f6bf02a805" }, config.EnabledFolders);
        Assert.Equal(new[] { "jellyfin-admins" }, config.AdminRoles);
        Assert.Equal(new[] { "jellyfin-users" }, config.Roles);
        Assert.True(config.EnableFolderRoles);
        var mapping = Assert.Single(config.FolderRoleMapping);
        Assert.Equal("kids", mapping.Role);
        Assert.Equal(new[] { "cc7df17e2f3509a4b5fc1d1ff0a6c4d0" }, mapping.Folders);
        Assert.True(config.EnableLiveTvRoles);
        Assert.Equal(new[] { "live-tv" }, config.LiveTvRoles);
        Assert.Equal(new[] { "live-tv-admins" }, config.LiveTvManagementRoles);
        Assert.Equal("Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", config.DefaultProvider);
        Assert.Equal(8443, config.PortOverride);
        Assert.Equal(Guid.Parse("3f0c2a1e-7b4d-4e8a-9c61-2d5e8f7a0b13"), config.CanonicalLinks["carol@example.test"]);
    }

    [Fact]
    public void Saving_and_reloading_keeps_every_value()
    {
        using (var scope = new PluginScope(Fixture()))
        {
            // UpdateConfiguration writes the file through Jellyfin's serializer, as saving
            // the settings page does.
            scope.Plugin.UpdateConfiguration(scope.Plugin.Configuration);
            var written = File.ReadAllText(scope.ConfigurationFile);

            using var reloaded = new PluginScope(written);
            var oid = reloaded.Plugin.Configuration.OidConfigs["authelia"];
            var saml = reloaded.Plugin.Configuration.SamlConfigs["keycloak"];
            Assert.Equal(2, oid.CanonicalLinks.Count);
            Assert.Equal("groups", oid.RoleClaim);
            Assert.Equal("kids", Assert.Single(saml.FolderRoleMapping).Role);
            Assert.Equal(8443, saml.PortOverride);
        }
    }

    [Fact]
    public void The_import_copies_9p4s_file_and_leaves_it_in_place()
    {
        using var scope = new PluginScope(legacyConfigurationXml: Fixture());

        Assert.Equal(Fixture(), File.ReadAllText(scope.ConfigurationFile));
        Assert.Equal(Fixture(), File.ReadAllText(scope.LegacyConfigurationFile));
        var (level, message) = Assert.Single(scope.Logger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, level);
        Assert.Contains(scope.LegacyConfigurationFile, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_of_its_own_are_never_replaced_by_9p4s()
    {
        var own = new PluginConfiguration();
        own.OidConfigs["pocket-id"] = new OidConfig { OidEndpoint = "https://id.example.test" };
        // Written as Jellyfin writes settings: UTF-8, declared as such.
        string ownXml;
        using (var stream = new MemoryStream())
        {
            new TestXmlSerializer().SerializeToStream(own, stream);
            ownXml = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }

        using var scope = new PluginScope(existingConfigurationXml: ownXml, legacyConfigurationXml: Fixture());

        Assert.Equal(new[] { "pocket-id" }, scope.Plugin.Configuration.OidConfigs.Keys);
        Assert.Empty(scope.Plugin.Configuration.SamlConfigs);
        Assert.Empty(scope.Logger.Entries);
    }

    [Fact]
    public void A_fresh_installation_starts_with_no_providers()
    {
        using var scope = new PluginScope();

        Assert.Empty(scope.Plugin.Configuration.OidConfigs);
        Assert.Empty(scope.Plugin.Configuration.SamlConfigs);
        Assert.False(File.Exists(scope.LegacyConfigurationFile));
        Assert.Empty(scope.Logger.Entries);
    }

    [Fact]
    public void A_failed_import_is_logged_and_leaves_the_legacy_file_intact()
    {
        using var scope = new PluginScope(legacyConfigurationXml: Fixture(), blockConfigurationFile: true);

        Assert.Same(scope.Plugin, SSOPlugin.Instance);
        Assert.Equal(Fixture(), File.ReadAllText(scope.LegacyConfigurationFile));
        Assert.False(File.Exists(scope.ConfigurationFile));
        var (level, message) = Assert.Single(scope.Logger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, level);
        Assert.Contains("Could not import the settings", message, StringComparison.Ordinal);
        Assert.Contains(scope.LegacyConfigurationFile, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_links_read_as_empty_rather_than_null_when_absent()
    {
        // Providers saved before account linking existed have no CanonicalLinks element.
        var config = new OidConfig();

        Assert.NotNull(config.CanonicalLinks);
        Assert.Empty(config.CanonicalLinks);
        Assert.NotNull(new SamlConfig().CanonicalLinks);
    }
}
