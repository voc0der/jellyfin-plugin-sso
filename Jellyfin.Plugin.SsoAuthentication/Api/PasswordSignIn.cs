using System;
using System.Net.Mime;
using MediaBrowser.Common.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SsoAuthentication.Api;

/// <summary>
/// The switch for Jellyfin's own username and password sign-in. It lives in the server's
/// environment rather than in the plugin's settings, so an administrator who can no longer sign
/// in, because the provider is down, can still turn passwords back on: restart Jellyfin without it.
/// </summary>
internal static class PasswordSignIn
{
    /// <summary>
    /// The environment variable. <c>false</c>, in any case, turns password sign-in off; unset or
    /// <c>true</c>, it stays on.
    /// </summary>
    public const string EnvironmentVariable = "JELLYFIN_ENABLE_PASSWORD_LOGIN";

    /// <summary>
    /// Whether the variable's value turns password sign-in off. Only <c>false</c> does, so a server
    /// that never sets the variable signs in exactly as it did before.
    /// </summary>
    /// <param name="value">The variable's value, or null when it is not set.</param>
    /// <returns>Whether Jellyfin should refuse usernames and passwords.</returns>
    public static bool TurnsOff(string value) => bool.TryParse(value, out var enabled) && !enabled;

    /// <summary>
    /// Says in the log, at startup, that password sign-in is off, so an administrator can tell why
    /// passwords are refused; or that the value meant to turn it off did not.
    /// </summary>
    /// <param name="logger">The plugin's logger.</param>
    /// <param name="value">The variable's value, or null when it is not set.</param>
    public static void Log(ILogger logger, string value)
    {
        if (TurnsOff(value))
        {
            logger.LogInformation(
                "Password sign-in is off: {Variable} is false, so Jellyfin refuses usernames and passwords. SSO sign-in and Quick Connect work as before",
                EnvironmentVariable);
        }
        else if (!string.IsNullOrWhiteSpace(value) && !bool.TryParse(value, out _))
        {
            logger.LogWarning(
                "{Variable} is {Value}, which is neither true nor false; password sign-in stays on",
                EnvironmentVariable,
                value);
        }
    }
}

/// <summary>
/// Refuses Jellyfin's username and password sign-in, <c>POST /Users/AuthenticateByName</c>, with
/// 403 Forbidden, which Jellyfin Web shows as "not authorized to access the server". Registered by
/// <see cref="PluginServiceRegistrator"/> only while <see cref="PasswordSignIn.EnvironmentVariable"/>
/// is false.
/// </summary>
/// <remarks>
/// It runs before the request body is read: the password is never checked, a wrong one counts
/// toward no lockout, and the answer is the same whether it was right or not. Sign-in through this
/// plugin and Quick Connect never reach that action, since both open their session directly, and
/// sessions already signed in are left alone. So are other plugins' passwords, such as LDAP's:
/// they are checked behind the same action, and refused with it.
/// </remarks>
internal sealed class PasswordSignInFilter : IResourceFilter
{
    /// <summary>
    /// The action is recognised by its route, the address every Jellyfin client signs in at, which
    /// Jellyfin cannot move without breaking them all. The controller and method behind it are
    /// Jellyfin's own business, and free to change.
    /// </summary>
    internal const string Route = "Users/AuthenticateByName";

    private readonly ILogger<PasswordSignInFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PasswordSignInFilter"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{PasswordSignInFilter}"/> interface.</param>
    public PasswordSignInFilter(ILogger<PasswordSignInFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (!string.Equals(context.ActionDescriptor.AttributeRouteInfo?.Template, Route, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // The address only: the username is in the body, which is not read, and is the caller's
        // to make up anyway.
        _logger.LogInformation(
            "Refused a password sign-in from {RemoteIp}: {Variable} is false",
            context.HttpContext.GetNormalizedRemoteIP(),
            PasswordSignIn.EnvironmentVariable);

        context.Result = new ContentResult
        {
            StatusCode = StatusCodes.Status403Forbidden,
            ContentType = MediaTypeNames.Text.Plain,
            Content = "Signing in with a password is turned off on this server. Sign in with SSO, or with Quick Connect."
        };
    }

    /// <inheritdoc />
    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
