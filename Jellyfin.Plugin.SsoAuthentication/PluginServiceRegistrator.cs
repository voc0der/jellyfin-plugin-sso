using System;
using Jellyfin.Plugin.SsoAuthentication.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SsoAuthentication;

/// <summary>
/// Registers this plugin's services with Jellyfin's container, at startup.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Always, as automatic sign-in can be turned on at any time from the settings page. Until
        // it is, Jellyfin Web's page passes through as Jellyfin serves it.
        serviceCollection.AddSingleton<IStartupFilter, AutoSignInStartupFilter>();
        Register(serviceCollection, Environment.GetEnvironmentVariable(PasswordSignIn.EnvironmentVariable));
    }

    /// <summary>
    /// Registers what the value of <see cref="PasswordSignIn.EnvironmentVariable"/> calls for. With
    /// password sign-in on, that is nothing at all: Jellyfin runs exactly as it would without this
    /// plugin's switch, and turning the switch back on undoes everything turning it off did.
    /// </summary>
    /// <param name="serviceCollection">Jellyfin's service collection.</param>
    /// <param name="enablePasswordLogin">The variable's value, or null when it is not set.</param>
    internal static void Register(IServiceCollection serviceCollection, string enablePasswordLogin)
    {
        if (!PasswordSignIn.TurnsOff(enablePasswordLogin))
        {
            return;
        }

        // A global filter rather than middleware: it sees which action the request was routed to,
        // so the server's base URL, the path's case and anything else routing forgives cannot
        // carry a password sign-in past it.
        serviceCollection.AddSingleton<PasswordSignInFilter>();
        serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<PasswordSignInFilter>());
    }
}
