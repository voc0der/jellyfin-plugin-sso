using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SsoAuthentication.Config;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.SsoAuthentication.Api;

/// <summary>
/// Automatic sign-in: someone who opens Jellyfin Web without being signed in goes straight to a
/// provider, instead of to Jellyfin's login page. Only the browser knows whether it is signed in,
/// so a script decides, <c>Views/auto-sign-in.js</c>; <see cref="AutoSignInMiddleware"/> adds it
/// to Jellyfin Web's page while automatic sign-in is on.
/// </summary>
internal static class AutoSignIn
{
    /// <summary>
    /// What the script is served as, at <c>/SSOViews/auto-sign-in.js</c>.
    /// </summary>
    internal const string ScriptName = "auto-sign-in.js";

    /// <summary>
    /// The attribute of the script's tag that carries the address sign-in starts at.
    /// </summary>
    internal const string StartAttribute = "data-sso-auto-sign-in";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Where sign-in starts for the provider automatic sign-in uses, relative to Jellyfin Web's
    /// page: <c>../sso/OID/start/NAME</c> or <c>../sso/SAML/start/NAME</c>. Relative, so a base
    /// URL, or a proxy that serves Jellyfin under a path of its own, is kept.
    /// </summary>
    /// <param name="configuration">The plugin's settings.</param>
    /// <returns>The address, or null while automatic sign-in is off, or its provider is missing or disabled.</returns>
    internal static string StartPath(PluginConfiguration configuration)
    {
        var provider = configuration?.AutoSignInProvider;
        if (configuration is not { EnableAutoSignIn: true } || string.IsNullOrEmpty(provider))
        {
            return null;
        }

        var protocol = configuration.AutoSignInProtocol;
        if (string.IsNullOrEmpty(protocol) || string.Equals(protocol, "OID", StringComparison.OrdinalIgnoreCase))
        {
            return configuration.OidConfigs?.GetValueOrDefault(provider) is { Enabled: true } ? Start("OID", provider) : null;
        }

        if (string.Equals(protocol, "SAML", StringComparison.OrdinalIgnoreCase))
        {
            return configuration.SamlConfigs?.GetValueOrDefault(provider) is { Enabled: true } ? Start("SAML", provider) : null;
        }

        return null;
    }

    /// <summary>
    /// Whether a request is for Jellyfin Web's page, under whatever base URL the server has.
    /// </summary>
    /// <param name="path">The request's path, base URL included.</param>
    /// <returns>Whether Jellyfin answers it with <c>web/index.html</c>.</returns>
    internal static bool IsJellyfinWeb(PathString path)
    {
        var value = path.Value;
        return value is not null
            && (value.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Identifies a browser navigation through sign-in. API calls and account linking
    /// retain their normal error responses.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>Whether the request is a browser navigation through sign-in.</returns>
    internal static bool IsSignIn(HttpRequest request)
    {
        if ((!HttpMethods.IsGet(request.Method) && !HttpMethods.IsPost(request.Method))
            || !request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = request.Path.Value ?? string.Empty;
        var index = path.LastIndexOf("/sso/", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var parts = path[(index + 5)..].Split('/');
        if (parts.Length != 3 || string.IsNullOrEmpty(parts[2])
            || (parts[0].ToUpperInvariant() is not ("OID" or "SAML"))
            || (parts[1].ToLowerInvariant() is not ("start" or "redirect" or "post" or "p" or "r")))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Keeps an error readable for manual sign-ins and lets an automatic attempt return to login.
    /// The browser's tab storage decides which it was; no cookies or tokens are added to requests.
    /// </summary>
    /// <param name="page">The original error response.</param>
    /// <returns>An HTML error page with a login link and the automatic fallback script.</returns>
    internal static byte[] FailurePage(byte[] page)
    {
        const string login = "../../../web/index.html#/login";
        var version = typeof(AutoSignIn).Assembly.GetName().Version;
        var script = $"../../../SSOViews/{ScriptName}?v={version}";
        var message = WebUtility.HtmlEncode(Encoding.UTF8.GetString(page));
        return Encoding.UTF8.GetBytes($"<!doctype html><html><head><meta charset=\"utf-8\"><title>Sign-in failed</title>"
            + $"<script defer src=\"{script}\" data-sso-auto-fallback=\"{login}\"></script></head>"
            + $"<body><pre>{message}</pre><a href=\"{login}\">Return to sign-in</a></body></html>");
    }

    /// <summary>
    /// The script's tag. Deferred, so it runs once the page has been read; and with the plugin's
    /// version in its address, so a browser never keeps an earlier release's script.
    /// </summary>
    /// <param name="startPath">Where sign-in starts, from <see cref="StartPath"/>.</param>
    /// <returns>The tag.</returns>
    internal static string ScriptTag(string startPath)
    {
        var version = typeof(AutoSignIn).Assembly.GetName().Version;
        return $"<script defer src=\"../SSOViews/{ScriptName}?v={version}\" {StartAttribute}=\"{WebUtility.HtmlEncode(startPath)}\"></script>";
    }

    /// <summary>
    /// Adds the tag to Jellyfin Web's page, ahead of its first script, so the script runs before
    /// Jellyfin Web starts; or at the end of its head, for a page with no script there.
    /// </summary>
    /// <param name="page">The page as Jellyfin serves it.</param>
    /// <param name="tag">The tag, from <see cref="ScriptTag"/>.</param>
    /// <returns>The page with the tag, or null for a page with no head or that is not UTF-8.</returns>
    internal static byte[] Inject(byte[] page, string tag)
    {
        string html;
        try
        {
            html = StrictUtf8.GetString(page);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        var head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (head < 0)
        {
            return null;
        }

        var script = html.IndexOf("<script", 0, head, StringComparison.OrdinalIgnoreCase);
        return Encoding.UTF8.GetBytes(html.Insert(script < 0 ? head : script, tag));
    }

    private static string Start(string protocol, string provider) => $"../sso/{protocol}/start/{Uri.EscapeDataString(provider)}";
}

/// <summary>
/// Puts <see cref="AutoSignInMiddleware"/> in front of Jellyfin's own pipeline. A plugin has no
/// other way to reach Jellyfin Web's page, which Jellyfin serves from its static file middleware,
/// ahead of every controller; and in front of the pipeline, the page is not yet compressed.
/// </summary>
internal sealed class AutoSignInStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<AutoSignInMiddleware>();
            next(app);
        };
    }
}

/// <summary>
/// Adds automatic sign-in's script to Jellyfin Web's page, <c>web/index.html</c>, as Jellyfin
/// serves it. Nothing is written to disk, and while automatic sign-in is off, the page passes
/// through untouched, like every other response.
/// </summary>
internal sealed class AutoSignInMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AutoSignInMiddleware> _logger;
    private string _state;
    private int _warnedOfPage;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoSignInMiddleware"/> class.
    /// </summary>
    /// <param name="next">The rest of Jellyfin's pipeline.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{AutoSignInMiddleware}"/> interface.</param>
    public AutoSignInMiddleware(RequestDelegate next, ILogger<AutoSignInMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Answers a request, adding the script if it is for Jellyfin Web's page.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>A task that completes with the response.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var isWeb = HttpMethods.IsGet(context.Request.Method) && AutoSignIn.IsJellyfinWeb(context.Request.Path);
        var isSignIn = AutoSignIn.IsSignIn(context.Request);
        var tag = isWeb || isSignIn ? Tag() : null;
        if (tag is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // The whole page, every time: uncompressed, so it can be read, and never a 304 or a range,
        // which would leave a browser on a copy from before automatic sign-in was turned on.
        var headers = context.Request.Headers;
        headers.Remove(HeaderNames.AcceptEncoding);
        headers.Remove(HeaderNames.IfNoneMatch);
        headers.Remove(HeaderNames.IfModifiedSince);
        headers.Remove(HeaderNames.Range);
        headers.Remove(HeaderNames.IfRange);

        var response = context.Response;
        var body = response.Body;
        byte[] page;
        using (var buffer = new MemoryStream())
        {
            response.Body = buffer;
            try
            {
                await _next(context).ConfigureAwait(false);
            }
            finally
            {
                // On a failure too: nothing has been sent yet, so Jellyfin can still answer it.
                response.Body = body;
            }

            page = buffer.ToArray();
        }

        if (isSignIn && response.StatusCode >= StatusCodes.Status400BadRequest
            && !response.Headers.ContainsKey(HeaderNames.ContentEncoding))
        {
            page = AutoSignIn.FailurePage(page);
            response.ContentType = MediaTypeNames.Text.Html;
            response.ContentLength = page.Length;
            response.Headers.CacheControl = "no-store";
            response.Headers.Remove(HeaderNames.ETag);
            response.Headers.Remove(HeaderNames.LastModified);
        }
        else if (isWeb && response.StatusCode == StatusCodes.Status200OK
            && response.ContentType?.StartsWith(MediaTypeNames.Text.Html, StringComparison.OrdinalIgnoreCase) == true
            && !response.Headers.ContainsKey(HeaderNames.ContentEncoding))
        {
            var injected = AutoSignIn.Inject(page, tag);
            if (injected is null)
            {
                if (Interlocked.Exchange(ref _warnedOfPage, 1) == 0)
                {
                    _logger.LogWarning("Automatic sign-in cannot add its script to Jellyfin Web's page, which has no head or is not UTF-8; it is served as it is");
                }
            }
            else
            {
                page = injected;

                // The validators describe the file, which this page no longer is.
                response.Headers.Remove(HeaderNames.ETag);
                response.Headers.Remove(HeaderNames.LastModified);
                response.Headers.Remove(HeaderNames.AcceptRanges);
                response.ContentLength = page.Length;
            }
        }

        await body.WriteAsync(page).ConfigureAwait(false);
    }

    /// <summary>
    /// The script's tag while automatic sign-in can send people to its provider, and null otherwise.
    /// </summary>
    private string Tag()
    {
        PluginConfiguration configuration;
        try
        {
            configuration = SSOPlugin.Instance?.Configuration;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // File access and XML deserialization failures should not prevent Jellyfin Web loading.
            Report("unreadable", () => _logger.LogError(e, "Automatic sign-in is off until the plugin's settings can be read"));
            return null;
        }

        if (configuration is not { EnableAutoSignIn: true })
        {
            Report(null, () => _logger.LogInformation("Automatic sign-in is off: Jellyfin Web shows its login page"));
            return null;
        }

        var provider = configuration.AutoSignInProvider;
        var protocol = string.IsNullOrEmpty(configuration.AutoSignInProtocol) ? "OID" : configuration.AutoSignInProtocol;
        var start = AutoSignIn.StartPath(configuration);
        if (start is null)
        {
            Report(
                $"unusable {protocol} {provider}",
                () => _logger.LogWarning("Automatic sign-in is on, but no enabled {Protocol} provider is named {Provider}, so Jellyfin Web shows its login page", protocol, provider));
            return null;
        }

        Report(start, () => _logger.LogInformation("Automatic sign-in is on: Jellyfin Web sends anyone who is not signed in to the {Protocol} provider {Provider}", protocol, provider));
        return AutoSignIn.ScriptTag(start);
    }

    /// <summary>
    /// Logs what automatic sign-in does when that changes: every load of Jellyfin Web asks again,
    /// so a change of settings shows up in the log at the next one.
    /// </summary>
    private void Report(string state, Action log)
    {
        if (Interlocked.Exchange(ref _state, state) != state)
        {
            log();
        }
    }
}
