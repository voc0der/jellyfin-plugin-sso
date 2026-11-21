using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.SsoAuthentication.Api;
using Jellyfin.Plugin.SsoAuthentication.Config;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SsoAuthentication;

/// <summary>
/// The SSO plugin class.
/// </summary>
public class SSOPlugin : BasePlugin<PluginConfiguration>, IPlugin, IHasWebPages
{
    /// <summary>
    /// The settings file of 9p4/jellyfin-plugin-sso, which uninstalling that plugin leaves behind.
    /// </summary>
    internal const string LegacyConfigurationFileName = "SSO-Auth.xml";

    /// <summary>
    /// What the settings page's script, stylesheet and linking page are served as, from
    /// <c>configurationpage?name=</c>; the page markup asks for them by these names.
    /// </summary>
    internal const string PageName = "sso-authentication";

    /// <summary>
    /// Initializes a new instance of the <see cref="SSOPlugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Internal Jellyfin interface for the ApplicationPath.</param>
    /// <param name="xmlSerializer">Internal Jellyfin interface for the XML information.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{SSOPlugin}"/> interface.</param>
    public SSOPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<SSOPlugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        ImportLegacyConfiguration(logger);
        PasswordSignIn.Log(logger, Environment.GetEnvironmentVariable(PasswordSignIn.EnvironmentVariable));
    }

    /// <summary>
    /// Gets the instance of the SSO plugin.
    /// </summary>
    public static SSOPlugin Instance { get; private set; }

    /// <summary>
    /// Gets the name of the SSO plugin.
    /// </summary>
    public override string Name => "SSO Authentication";

    /// <summary>
    /// Gets the GUID of the SSO plugin.
    /// </summary>
    public override Guid Id => Guid.Parse("3c3b1a1d-7470-466f-a565-b3041e54724d");

    /// <summary>
    /// Returns the available internal web pages of this plugin.
    /// </summary>
    /// <returns>A list of internal webpages in this application.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            // First, so the dashboard's plugin list opens it.
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.configPage.html"
            },
            new PluginPageInfo
            {
                Name = PageName + ".js",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.config.js"
            },
            new PluginPageInfo
            {
                Name = PageName + ".css",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.style.css"
            },
            new PluginPageInfo
            {
                Name = PageName + "-linking",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.linking.html"
            },
            new PluginPageInfo
            {
                Name = PageName + "-linking.js",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.linking.js"
            },
        };
    }

    /// <summary>
    /// Takes over the settings 9p4's plugin left behind, on a server this plugin has no settings
    /// on yet. The format is the same, so the providers and linked accounts carry over; the old
    /// file is left as it is.
    /// </summary>
    private void ImportLegacyConfiguration(ILogger logger)
    {
        var legacy = Path.Join(ApplicationPaths.PluginConfigurationsPath, LegacyConfigurationFileName);
        if (File.Exists(ConfigurationFilePath) || !File.Exists(legacy))
        {
            return;
        }

        try
        {
            File.Copy(legacy, ConfigurationFilePath);
            logger.LogInformation("Imported the providers and linked accounts of 9p4's SSO plugin from {File}", legacy);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError(e, "Could not import the settings of 9p4's SSO plugin from {File}", legacy);
        }
    }

    /// <summary>
    /// Returns the available user views for this plugin.
    /// </summary>
    /// <returns>A list of user views for this plugin.</returns>
    public IEnumerable<PluginPageInfo> GetViews()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "style.css",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.style.css"
            },
            new PluginPageInfo
            {
                Name = "linking",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.linking.html"
            },
            new PluginPageInfo
            {
                Name = "linking.js",
                EmbeddedResourcePath = $"{GetType().Namespace}.Config.linking.js"
            },
            new PluginPageInfo
            {
                Name = "ApiClient.js",
                EmbeddedResourcePath = $"{GetType().Namespace}.Views.apiClient.js"
            },
            new PluginPageInfo
            {
                Name = "emby-restyle.css",
                EmbeddedResourcePath = $"{GetType().Namespace}.Views.emby-restyle.css"
            },
            new PluginPageInfo
            {
                Name = "jellyfin-apiClient.esm.min.js",
                EmbeddedResourcePath = $"{GetType().Namespace}.Views.jellyfin-apiClient.esm.min.js"
            },
        };
    }
}
