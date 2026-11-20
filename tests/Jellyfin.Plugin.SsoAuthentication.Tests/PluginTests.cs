using System.Text.Json;
using System.Xml.Linq;
using Jellyfin.Plugin.SsoAuthentication.Api;

namespace Jellyfin.Plugin.SsoAuthentication.Tests;

/// <summary>
/// The names here are paths and stored values in existing installations, not labels.
/// Each test says what an installation loses if the value changes.
/// </summary>
public sealed class PluginTests
{
    private const string PluginGuid = "3c3b1a1d-7470-466f-a565-b3041e54724d";

    // What 9p4/jellyfin-plugin-sso published. Sharing it would make Jellyfin update a 9p4
    // installation into this plugin, and list the two as one.
    private const string NinePFourGuid = "505ce9d1-d916-42fa-86ca-673ef241d7df";

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Join(dir.FullName, "manifest.json")) && File.Exists(Path.Join(dir.FullName, "Directory.Build.props")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Id_is_this_plugins_own_and_not_9p4s()
    {
        using var scope = new PluginScope();

        Assert.Equal(Guid.Parse(PluginGuid), scope.Plugin.Id);
        Assert.NotEqual(Guid.Parse(NinePFourGuid), scope.Plugin.Id);
    }

    [Fact]
    public void Id_matches_the_catalogue_and_the_build_properties()
    {
        var root = RepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Join(root, "manifest.json")));
        var props = XDocument.Load(Path.Join(root, "Directory.Build.props"));

        Assert.Equal(PluginGuid, manifest.RootElement[0].GetProperty("guid").GetString());
        Assert.Equal(PluginGuid, props.Descendants("PluginGuid").Single().Value);
    }

    [Fact]
    public void Name_is_the_catalogue_name_and_opens_the_settings_page()
    {
        using var scope = new PluginScope();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Join(RepositoryRoot(), "manifest.json")));

        // The dashboard's plugin list links to the first page, which carries the plugin's name.
        Assert.Equal("SSO Authentication", scope.Plugin.Name);
        Assert.Equal(scope.Plugin.Name, manifest.RootElement[0].GetProperty("name").GetString());
        Assert.Equal(scope.Plugin.Name, scope.Plugin.GetPages().First().Name);
    }

    [Theory]
    [InlineData("Jellyfin.Plugin.SsoAuthentication.Config.configPage.html", "data-controller=\"__plugin/", "\"")]
    [InlineData("Jellyfin.Plugin.SsoAuthentication.Config.linking.html", "data-controller=\"__plugin/", "\"")]
    [InlineData("Jellyfin.Plugin.SsoAuthentication.Config.config.js", "?name=", "\"")]
    public void Every_page_the_markup_asks_for_is_served_under_that_name(string resource, string before, string after)
    {
        using var scope = new PluginScope();
        using var reader = new StreamReader(typeof(SSOPlugin).Assembly.GetManifestResourceStream(resource)!);
        var source = reader.ReadToEnd();

        var start = source.IndexOf(before, StringComparison.Ordinal) + before.Length;
        var asked = source[start..source.IndexOf(after, start, StringComparison.Ordinal)];

        Assert.Contains(scope.Plugin.GetPages(), page => page.Name == asked);
    }

    [Fact]
    public void Assembly_name_names_the_settings_file()
    {
        using var scope = new PluginScope();

        // Jellyfin keeps plugin settings in plugins/configurations/<assembly>.xml, so once
        // released, renaming the assembly would lose everyone's providers and linked accounts.
        Assert.Equal("Jellyfin.Plugin.SsoAuthentication", typeof(SSOPlugin).Assembly.GetName().Name);
        Assert.Equal(scope.ConfigurationFile, scope.Plugin.ConfigurationFilePath);
    }

    [Fact]
    public void Controller_type_name_is_what_accounts_made_by_sign_in_store()
    {
        // Accounts created by sign-in carry this as their authentication provider ID; it names
        // no provider Jellyfin has, which is what keeps them from signing in with a password.
        Assert.Equal("Jellyfin.Plugin.SsoAuthentication.Api.SSOController", typeof(SSOController).FullName);
    }

    [Fact]
    public void Every_page_and_view_is_embedded_in_the_assembly()
    {
        using var scope = new PluginScope();
        var assembly = typeof(SSOPlugin).Assembly;
        var resources = assembly.GetManifestResourceNames();

        foreach (var page in scope.Plugin.GetPages().Concat(scope.Plugin.GetViews()))
        {
            Assert.Contains(page.EmbeddedResourcePath, resources);
            using var stream = assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
            Assert.NotNull(stream);
            Assert.True(stream!.Length > 0, page.EmbeddedResourcePath + " is empty");
        }
    }

    [Fact]
    public void Pages_carry_the_plugin_guid_they_load_settings_for()
    {
        var assembly = typeof(SSOPlugin).Assembly;

        foreach (var resource in new[] { "Jellyfin.Plugin.SsoAuthentication.Config.config.js", "Jellyfin.Plugin.SsoAuthentication.Config.linking.js" })
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            Assert.Contains($"pluginUniqueId: \"{PluginGuid}\"", reader.ReadToEnd());
        }
    }
}
