using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Duende.IdentityModel.OidcClient;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SsoAuthentication.Config;
using Jellyfin.Plugin.SsoAuthentication.Helpers;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Cryptography;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.SsoAuthentication.Api;

/// <summary>
/// The sso api controller.
/// </summary>
[ApiController]
[Route("[controller]")]
public class SSOController : ControllerBase
{
    /// <summary>
    /// Jellyfin's own username and password provider.
    /// </summary>
    private const string JellyfinPasswordProvider = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";

    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly IAuthorizationContext _authContext;
    private readonly ILogger<SSOController> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICryptoProvider _cryptoProvider;
    private readonly IProviderManager _providerManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private static readonly IDictionary<string, TimedAuthorizeState> StateManager = new Dictionary<string, TimedAuthorizeState>();

    /// <summary>
    /// Initializes a new instance of the <see cref="SSOController"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{SSOController}"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="authContext">Instance of the <see cref="IAuthorizationContext"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="cryptoProvider">Instance of the <see cref="ICryptoProvider"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    public SSOController(
        ILogger<SSOController> logger,
        ILoggerFactory loggerFactory,
        ISessionManager sessionManager,
        IUserManager userManager,
        IAuthorizationContext authContext,
        ICryptoProvider cryptoProvider,
        IProviderManager providerManager,
        IHttpClientFactory httpClientFactory,
        IServerConfigurationManager serverConfigurationManager)
    {
        _sessionManager = sessionManager;
        _userManager = userManager;
        _authContext = authContext;
        _cryptoProvider = cryptoProvider;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _providerManager = providerManager;
        _serverConfigurationManager = serverConfigurationManager;
        _httpClientFactory = httpClientFactory;
        _logger.LogInformation("SSO Controller initialized");
    }

    /// <summary>
    /// The GET endpoint for OpenID provider to callback to. Returns a webpage that parses client data and completes auth.
    /// </summary>
    /// <param name="provider">The ID of the provider which will use the callback information.</param>
    /// <param name="state">The current request state.</param>
    /// <returns>A webpage that will complete the client-side flow.</returns>
    // Actually a GET: https://github.com/IdentityModel/IdentityModel.OidcClient/issues/325
    [HttpGet("OID/r/{provider}")]
    [HttpGet("OID/redirect/{provider}")]
    public async Task<ActionResult> OidPost(
        [FromRoute] string provider,
        [FromQuery] string state) // Although this is a GET function, this function is called `Post` for consistency with SAML
    {
        OidConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.OidConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        if (config.Enabled)
        {
            if (string.IsNullOrEmpty(state))
            {
                return BadRequest("Missing state");
            }

            if (!StateManager.TryGetValue(state, out var timedState))
            {
                return BadRequest("Invalid or expired state");
            }

            var scopes = config.OidScopes == null ? new string[2] : config.OidScopes;
            var options = new OidcClientOptions
            {
                Authority = config.OidEndpoint?.Trim(),
                ClientId = config.OidClientId?.Trim(),
                ClientSecret = config.OidSecret?.Trim(),
                RedirectUri = GetRequestBase(config.SchemeOverride, config.PortOverride) + $"/sso/OID/{(Request.Path.Value.Contains("/start/", StringComparison.InvariantCultureIgnoreCase) ? "redirect" : "r")}/" + provider,
                Scope = string.Join(" ", scopes.Prepend("openid profile")),
                DisablePushedAuthorization = config.DisablePushedAuthorization,
                LoggerFactory = _loggerFactory,
                LoadProfile = !config.DoNotLoadProfile,
                HttpClientFactory = o =>
                {
                    var client = _httpClientFactory.CreateClient();
                    System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    System.Diagnostics.FileVersionInfo fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);
                    string version = fvi.FileVersion;
                    client.DefaultRequestHeaders.UserAgent.ParseAdd($"Jellyfin-Plugin-SsoAuthentication/{version} (+https://github.com/voc0der/jellyfin-plugin-sso)");
                    return client;
                }
            };
            var oidEndpointUri = new Uri(config.OidEndpoint?.Trim());
            options.Policy.Discovery.AdditionalEndpointBaseAddresses.Add(oidEndpointUri.GetLeftPart(UriPartial.Authority));
            options.Policy.Discovery.ValidateEndpoints = !config.DoNotValidateEndpoints; // For Google and other providers with different endpoints
            options.Policy.Discovery.RequireHttps = !config.DisableHttps;
            options.Policy.Discovery.ValidateIssuerName = !config.DoNotValidateIssuerName;
            var oidcClient = new OidcClient(options);
            var currentState = timedState.State;
            var result = await oidcClient.ProcessResponseAsync(Request.QueryString.Value, currentState).ConfigureAwait(false);

            if (result.IsError)
            {
                return ReturnError(StatusCodes.Status400BadRequest, $"Error logging in: {result.Error} - {result.ErrorDescription}");
            }

            timedState.Folders = !config.EnableFolderRoles && config.EnabledFolders != null
                ? new List<string>(config.EnabledFolders)
                : new List<string>();

            timedState.EnableLiveTv = config.EnableLiveTv;
            timedState.EnableLiveTvManagement = config.EnableLiveTvManagement;

            if (config.AvatarUrlFormat is not null)
            {
                timedState.AvatarURL = result.User.Claims.Aggregate(
                    config.AvatarUrlFormat,
                    (s, claim) => s.Contains($"@{{{claim.Type}}}") ? s.Replace($"@{{{claim.Type}}}", claim.Value) : s);
            }

            foreach (var claim in result.User.Claims)
            {
                if (claim.Type == (config.DefaultUsernameClaim?.Trim() ?? "preferred_username"))
                {
                    timedState.Username = claim.Value;
                    if (config.Roles == null || config.Roles.Length == 0)
                    {
                        timedState.Valid = true;
                    }
                }

                // Role processing
                // The regex matches any "." not preceded by a "\": a.b.c will be split into a, b, and c, but a.b\.c will be split into a, b.c (after processing the escaped dots)
                // We have to first process the RoleClaim string
                string[] segments = string.IsNullOrEmpty(config.RoleClaim) ? Array.Empty<string>() : Regex.Split(config.RoleClaim.Trim(), "(?<!\\\\)\\.");

                if (segments.Any())
                {
                    // Now we make sure that any escaped "."s ("\.") are replaced with "."
                    segments = segments.Select(i => i.Replace("\\.", ".")).ToArray();

                    if (claim.Type == segments[0])
                    {
                        List<string> roles;
                        // If we are not using JSON values, just use the raw info from the claim value
                        if (segments.Length == 1)
                        {
                            roles = new List<string> { claim.Value };
                        }
                        else
                        {
                            // We recursively traverse through the JSON data for the roles and parse it
                            var json = JsonConvert.DeserializeObject<IDictionary<string, object>>(claim.Value);
                            if (json is null)
                            {
                                roles = new List<string>();
                            }
                            else
                            {
                                bool missingSegment = false;
                                for (int i = 1; i < segments.Length - 1; i++)
                                {
                                    var segment = segments[i];
                                    if (!json.TryGetValue(segment, out var nextToken) || nextToken is not JObject nextObject)
                                    {
                                        missingSegment = true;
                                        break;
                                    }

                                    json = nextObject.ToObject<IDictionary<string, object>>();
                                    if (json is null)
                                    {
                                        missingSegment = true;
                                        break;
                                    }
                                }

                                // The final step is to take the JSON and turn it from a dictionary into a string
                                roles = missingSegment || !json.TryGetValue(segments[^1], out var rolesToken) || rolesToken is not JArray rolesArray
                                    ? new List<string>()
                                    : rolesArray.ToObject<List<string>>();
                            }
                        }

                        foreach (string role in roles)
                        {
                            // Check if allowed to login based on roles
                            if (config.Roles != null && config.Roles.Contains(role))
                            {
                                timedState.Valid = true;
                            }

                            // Check if admin based on roles
                            if (config.AdminRoles != null && config.AdminRoles.Contains(role))
                            {
                                timedState.Admin = true;
                            }

                            // Get allowed folders from roles
                            if (config.EnableFolderRoles && config.FolderRoleMapping != null)
                            {
                                foreach (FolderRoleMap folderRoleMap in config.FolderRoleMapping.Where(map => string.Equals(role, map.Role?.Trim(), StringComparison.Ordinal)))
                                {
                                    timedState.Folders.AddRange(folderRoleMap.Folders);
                                }
                            }

                            if (config.EnableLiveTvRoles)
                            {
                                // Check if allowed Live TV based on roles
                                if (config.LiveTvRoles != null && config.LiveTvRoles.Contains(role))
                                {
                                    timedState.EnableLiveTv = true;
                                }

                                // Check if allowed Live TV management based on roles
                                if (config.LiveTvManagementRoles != null && config.LiveTvManagementRoles.Contains(role))
                                {
                                    timedState.EnableLiveTvManagement = true;
                                }
                            }
                        }
                    }
                }
            }

            // If the provider doesn't support the preferred username claim, then use the sub claim
            if (!timedState.Valid)
            {
                foreach (var claim in result.User.Claims.Where(claim => claim.Type == "sub"))
                {
                    timedState.Username = claim.Value;
                    if (config.Roles == null || config.Roles.Length == 0)
                    {
                        timedState.Valid = true;
                    }
                }
            }

            bool isLinking = timedState.IsLinking;

            if (timedState.Valid)
            {
                _logger.LogInformation($"Is request linking: {isLinking}");
                return Content(WebResponse.Generator(data: state, provider: provider, baseUrl: GetRequestBase(config.SchemeOverride, config.PortOverride), mode: "OID", isLinking: isLinking), MediaTypeNames.Text.Html);
            }
            else
            {
                _logger.LogWarning(
                    "OpenID user {Username} has one or more incorrect role claims: {@Claims}. Expected any one of: {@ExpectedClaims}",
                    timedState.Username,
                    result.User.Claims.Select(o => new { o.Type, o.Value }),
                    config.Roles);

                return ReturnError(StatusCodes.Status401Unauthorized, "Error. Check permissions.");
            }
        }

        // If the config doesn't have an active provider matching the requeset, show an error
        return BadRequest("No matching provider found");
    }

    /// <summary>
    /// Initiates the login flow for OpenID. This redirects the user to the auth provider.
    /// </summary>
    /// <param name="provider">The name of the provider.</param>
    /// <param name="isLinking">Whether or not this request is to link accounts (Rather than authenticate).</param>
    /// <returns>An asynchronous result for the authentication.</returns>
    [HttpGet("OID/p/{provider}")]
    [HttpGet("OID/start/{provider}")]
    public async Task<ActionResult> OidChallenge(string provider, [FromQuery] bool isLinking = false)
    {
        Invalidate();
        OidConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.OidConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            throw new ArgumentException("Provider does not exist");
        }

        if (config.Enabled)
        {
            bool newPath = config.NewPath;
            if (!isLinking)
            {
                newPath = Request.Path.Value.Contains("/start/", StringComparison.InvariantCultureIgnoreCase);
                config.NewPath = newPath;
            }

            string redirectUri = GetRequestBase(config.SchemeOverride, config.PortOverride) + $"/sso/OID/{(newPath ? "redirect" : "r")}/" + provider;

            var options = new OidcClientOptions
            {
                Authority = config.OidEndpoint?.Trim(),
                ClientId = config.OidClientId?.Trim(),
                ClientSecret = config.OidSecret?.Trim(),
                RedirectUri = redirectUri,
                Scope = string.Join(" ", config.OidScopes.Prepend("openid profile")),
                DisablePushedAuthorization = config.DisablePushedAuthorization,
                LoggerFactory = _loggerFactory,
                LoadProfile = !config.DoNotLoadProfile,
                HttpClientFactory = o =>
                {
                    var client = _httpClientFactory.CreateClient();
                    System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    System.Diagnostics.FileVersionInfo fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);
                    string version = fvi.FileVersion;

                    client.DefaultRequestHeaders.UserAgent.ParseAdd($"Jellyfin-Plugin-SsoAuthentication/{version} (+https://github.com/voc0der/jellyfin-plugin-sso)");
                    return client;
                }
            };
            var oidEndpointUri = new Uri(config.OidEndpoint?.Trim());
            options.Policy.Discovery.AdditionalEndpointBaseAddresses.Add(oidEndpointUri.GetLeftPart(UriPartial.Authority));
            options.Policy.Discovery.ValidateEndpoints = !config.DoNotValidateEndpoints; // For Google and other providers with different endpoints
            options.Policy.Discovery.RequireHttps = !config.DisableHttps;
            options.Policy.Discovery.ValidateIssuerName = !config.DoNotValidateIssuerName;
            var oidcClient = new OidcClient(options);
            var state = await oidcClient.PrepareLoginAsync().ConfigureAwait(false);

            if (state.IsError)
            {
                return ReturnError(StatusCodes.Status400BadRequest, $"Error preparing login: {state.Error} - {state.ErrorDescription}");
            }

            StateManager.Add(state.State, new TimedAuthorizeState(state, DateTime.Now));

            // Track whether this is a linking request or not.
            StateManager[state.State].IsLinking = isLinking;
            return Redirect(state.StartUrl);
        }

        throw new ArgumentException("Provider does not exist");
    }

    /// <summary>
    /// Adds an OpenID auth configuration. Requires administrator privileges. If the provider already exists, it will be removed and readded.
    /// </summary>
    /// <param name="provider">The name of the provider to add.</param>
    /// <param name="config">The OID configuration (deserialized from a JSON post).</param>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("OID/Add/{provider}")]
    public void OidAdd(string provider, [FromBody] OidConfig config)
    {
        var configuration = SSOPlugin.Instance.Configuration;
        configuration.OidConfigs[provider] = config;
        SSOPlugin.Instance.UpdateConfiguration(configuration);
    }

    /// <summary>
    /// Deletes an OpenID provider.
    /// </summary>
    /// <param name="provider">Name of provider to delete.</param>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("OID/Del/{provider}")]
    public void OidDel(string provider)
    {
        var configuration = SSOPlugin.Instance.Configuration;
        configuration.OidConfigs.Remove(provider);
        SSOPlugin.Instance.UpdateConfiguration(configuration);
    }

    /// <summary>
    /// Lists the OpenID providers configured. Requires administrator privileges.
    /// </summary>
    /// <returns>The list of OpenID configurations.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("OID/Get")]
    public ActionResult OidProviders()
    {
        return Ok(SSOPlugin.Instance.Configuration.OidConfigs);
    }

    /// <summary>
    /// Lists the OpenID providers names only.
    /// </summary>
    /// <returns>The list of OpenID configurations.</returns>
    [HttpGet("OID/GetNames")]
    public ActionResult OidProviderNames()
    {
        return Ok(SSOPlugin.Instance.Configuration.OidConfigs.Keys);
    }

    /// <summary>
    /// Lists the SAML providers names only.
    /// </summary>
    /// <returns>The list of OpenID configurations.</returns>
    [HttpGet("SAML/GetNames")]
    public ActionResult SamlProviderNames()
    {
        return Ok(SSOPlugin.Instance.Configuration.SamlConfigs.Keys);
    }

    /// <summary>
    /// This is a debug endpoint to list all running OpenID flows. Requires administrator privileges.
    /// </summary>
    /// <returns>The list of OpenID flows in progress.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("OID/States")]
    public ActionResult OidStates()
    {
        return Ok(StateManager);
    }

    /// <summary>
    /// This endpoint accepts JSON and will authorize the user from the device values passed from the client.
    /// </summary>
    /// <param name="provider">Name of provider to authenticate against.</param>
    /// <param name="response">The data passed to the client to ensure it is the right one.</param>
    /// <returns>JSON for the client to populate information with.</returns>
    [HttpPost("OID/Auth/{provider}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> OidAuth(string provider, [FromBody] AuthResponse response)
    {
        OidConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.OidConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        if (config.Enabled)
        {
            foreach (var kvp in StateManager)
            {
                if (kvp.Value.State.State.Equals(response.Data) && kvp.Value.Valid)
                {
                    Guid userId = await CreateCanonicalLinkAndUserIfNotExist("oid", provider, kvp.Value.Username);

                    var authenticationResult = await Authenticate(
                        userId,
                        kvp.Value.Admin,
                        config.EnableAuthorization,
                        config.EnableAllFolders,
                        kvp.Value.Folders.ToArray(),
                        kvp.Value.EnableLiveTv,
                        kvp.Value.EnableLiveTvManagement,
                        response,
                        config.DefaultProvider?.Trim(),
                        kvp.Value.AvatarURL)
                        .ConfigureAwait(false);
                    StateManager.Remove(kvp.Key);
                    return Ok(authenticationResult);
                }
            }
        }

        return Problem("Something went wrong");
    }

    /// <summary>
    /// This is the callback for the SAML flow. This creates a webpage to complete auth.
    /// </summary>
    /// <param name="provider">The provider that is calling back.</param>
    /// <param name="relayState">
    ///    RelayState given in the original saml request. If it is equal to "linking",
    ///    We consider this to be a linking request.
    /// </param>
    /// <returns>A webpage that will complete the client-side flow.</returns>
    [HttpPost("SAML/p/{provider}")]
    [HttpPost("SAML/post/{provider}")]
    public ActionResult SamlPost(string provider, [FromQuery] string relayState = null)
    {
        SamlConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.SamlConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        bool isLinking = relayState == "linking";

        // What the relay state means, not the value itself: it comes from the request, and could
        // put forged lines in the log.
        _logger.LogInformation("SAML response received, for linking: {IsLinking}", isLinking);

        if (config.Enabled)
        {
            var samlResponse = new Response(config.SamlCertificate, Request.Form["SAMLResponse"]);

            if (!samlResponse.IsValid())
            {
                return Problem("Invalid SAML signature");
            }

            if (SamlRolesAllow(config, samlResponse))
            {
                return Content(
                        WebResponse.Generator(
                            data: Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(samlResponse.Xml)),
                            provider: provider,
                            baseUrl: GetRequestBase(config.SchemeOverride, config.PortOverride),
                            mode: "SAML",
                            isLinking: isLinking),
                        MediaTypeNames.Text.Html);
            }

            _logger.LogWarning(
                "SAML user: {UserId} has insufficient roles: {@Roles}. Expected any one of: {@ExpectedRoles}",
                samlResponse.GetNameID(),
                samlResponse.GetCustomAttributes("Role"),
                config.Roles);
            return ReturnError(StatusCodes.Status401Unauthorized, "Error. Check permissions.");
        }

        return ReturnError(StatusCodes.Status400BadRequest, "No active providers found");
    }

    /// <summary>
    /// Initializes the SAML flow. This will redirect the user to the SAML provider.
    /// </summary>
    /// <param name="provider">The provider to being the flow with.</param>
    /// <param name="isLinking">Whether this flow intends to link an account, or initiate auth.</param>
    /// <returns>A redirect to the SAML provider's auth page.</returns>
    [HttpGet("SAML/p/{provider}")]
    [HttpGet("SAML/start/{provider}")]
    public RedirectResult SamlChallenge(string provider, [FromQuery] bool isLinking = false)
    {
        SamlConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.SamlConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            throw new ArgumentException("Provider does not exist");
        }

        if (config.Enabled)
        {
            bool newPath = config.NewPath;
            if (!isLinking)
            {
                newPath = Request.Path.Value.Contains("/start/", StringComparison.InvariantCultureIgnoreCase);
                config.NewPath = newPath;
            }

            string redirectUri = GetRequestBase(config.SchemeOverride, config.PortOverride) + $"/sso/SAML/{(newPath ? "post" : "p")}/" + provider;
            string relayState = null;
            if (isLinking)
            {
                relayState = "linking";
            }

            var request = new AuthRequest(
                config.SamlClientId.Trim(),
                redirectUri);

            return Redirect(request.GetRedirectUrl(config.SamlEndpoint.Trim(), relayState));
        }

        throw new ArgumentException("Provider does not exist");
    }

    /// <summary>
    /// Adds a SAML configuration. If the provider already exists, overwrite it.
    /// </summary>
    /// <param name="provider">The provider name to add.</param>
    /// <param name="newConfig">The SAML configuration object (deserialized) from JSON.</param>
    /// <returns>The success result.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("SAML/Add/{provider}")]
    public OkResult SamlAdd(string provider, [FromBody] SamlConfig newConfig)
    {
        var configuration = SSOPlugin.Instance.Configuration;
        configuration.SamlConfigs[provider] = newConfig;
        SSOPlugin.Instance.UpdateConfiguration(configuration);
        return Ok();
    }

    /// <summary>
    /// Deletes a provider from the configuration with a given ID.
    /// </summary>
    /// <param name="provider">The ID of the provider to delete.</param>
    /// <returns>The success result.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("SAML/Del/{provider}")]
    public OkResult SamlDel(string provider)
    {
        var configuration = SSOPlugin.Instance.Configuration;
        configuration.SamlConfigs.Remove(provider);
        SSOPlugin.Instance.UpdateConfiguration(configuration);
        return Ok();
    }

    /// <summary>
    /// Returns a list of all SAML providers configured. Requires administrator privileges.
    /// </summary>
    /// <returns>A list of all of the Saml providers available.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("SAML/Get")]
    public ActionResult SamlProviders()
    {
        return Ok(SSOPlugin.Instance.Configuration.SamlConfigs);
    }

    /// <summary>
    /// This endpoint accepts JSON and will authorize the user from the device values passed from the client.
    /// </summary>
    /// <param name="provider">The provider to authenticate against.</param>
    /// <param name="response">The data passed to the client to ensure it is the right one.</param>
    /// <returns>JSON for the client to populate information with.</returns>
    [HttpPost("SAML/Auth/{provider}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> SamlAuth(string provider, [FromBody] AuthResponse response)
    {
        SamlConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.SamlConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        if (config.Enabled)
        {
            bool isAdmin = false;
            bool liveTv = config.EnableLiveTv;
            bool liveTvManagement = config.EnableLiveTvManagement;
            var samlResponse = new Response(config.SamlCertificate, response.Data);

            if (!samlResponse.IsValid())
            {
                return Problem("Invalid SAML signature");
            }

            // SamlPost already turned away anyone without an allowed role, but this endpoint
            // takes the response from the browser, which can send it here directly.
            if (!SamlRolesAllow(config, samlResponse))
            {
                _logger.LogWarning(
                    "SAML user: {UserId} has insufficient roles: {@Roles}. Expected any one of: {@ExpectedRoles}",
                    samlResponse.GetNameID(),
                    samlResponse.GetCustomAttributes("Role"),
                    config.Roles);
                return ReturnError(StatusCodes.Status401Unauthorized, "Error. Check permissions.");
            }

            List<string> folders = !config.EnableFolderRoles && config.EnabledFolders != null
                ? new List<string>(config.EnabledFolders)
                : new List<string>();

            foreach (string role in samlResponse.GetCustomAttributes("Role"))
            {
                if (config.AdminRoles != null && config.AdminRoles.Contains(role))
                {
                    isAdmin = true;
                }

                if (config.EnableFolderRoles && config.FolderRoleMapping != null)
                {
                    foreach (FolderRoleMap folderRoleMap in config.FolderRoleMapping.Where(map => string.Equals(map.Role, role, StringComparison.Ordinal)))
                    {
                        folders.AddRange(folderRoleMap.Folders);
                    }
                }

                if (config.EnableLiveTvRoles)
                {
                    if (config.LiveTvRoles != null && config.LiveTvRoles.Contains(role))
                    {
                        liveTv = true;
                    }

                    if (config.LiveTvManagementRoles != null && config.LiveTvManagementRoles.Contains(role))
                    {
                        liveTvManagement = true;
                    }
                }
            }

            Guid userId = await CreateCanonicalLinkAndUserIfNotExist("saml", provider, samlResponse.GetNameID());

            var authenticationResult = await Authenticate(
                userId,
                isAdmin,
                config.EnableAuthorization,
                config.EnableAllFolders,
                folders.ToArray(),
                liveTv,
                liveTvManagement,
                response,
                config.DefaultProvider?.Trim(),
                null)
                .ConfigureAwait(false);
            return Ok(authenticationResult);
        }

        return Problem("Something went wrong");
    }

    /// <summary>
    /// Removes a user from SSO auth and switches it back to another auth provider. Requires administrator privileges.
    /// </summary>
    /// <param name="username">The username to switch to the new provider.</param>
    /// <param name="provider">The new provider to switch to.</param>
    /// <returns>Whether this API endpoint succeeded.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Unregister/{username}")]
    public ActionResult Unregister(string username, [FromBody] string provider)
    {
        User user = _userManager.GetUserByName(username);
        user.AuthenticationProviderId = provider;

        return Ok();
    }

    private SerializableDictionary<string, Guid> GetCanonicalLinks(string mode, string provider)
    {
        SerializableDictionary<string, Guid> links = null;

        switch (mode.ToLower())
        {
            case "saml":
                links = SSOPlugin.Instance.Configuration.SamlConfigs[provider].CanonicalLinks;
                break;
            case "oid":
                links = SSOPlugin.Instance.Configuration.OidConfigs[provider].CanonicalLinks;
                break;
            default:
                throw new ArgumentException($"{mode} is not a valid choice between 'saml' and 'oid'");
        }

        if (links == null)
        {
            links = new SerializableDictionary<string, Guid>();
        }

        return links;
    }

    private async Task<Guid> CreateCanonicalLinkAndUserIfNotExist(string mode, string provider, string canonicalName)
    {
        // First try to get the user by its id in case it was already registered before, and
        // with no link, by name instead
        Guid userId = FindCanonicalLink(mode, provider, canonicalName);
        User user = userId == Guid.Empty
            ? _userManager.GetUserByName(canonicalName)
            : _userManager.GetUserById(userId);

        if (user == null)
        {
            _logger.LogInformation("SSO user {Username} doesn't exist, creating...", canonicalName);
            user = await _userManager.CreateUserAsync(canonicalName).ConfigureAwait(false);
            await LockToSso(user).ConfigureAwait(false);

            // Make sure there aren't any trailing existing links
            var links = GetCanonicalLinks(mode, provider);
            links.Remove(canonicalName);
            UpdateCanonicalLinkConfig(links, mode, provider);
        }
        else if (AcceptsEmptyPassword(user))
        {
            // 9p4's plugin left the accounts it made on Jellyfin 12 like this, as could an
            // administrator. Whoever is signing in has just proved who they are to the provider.
            _logger.LogWarning(
                "Account {Username} could be signed in to with an empty password; it now signs in through SSO only. Give it a password in the dashboard if it should also sign in with one",
                user.Username);
            await LockToSso(user).ConfigureAwait(false);
        }

        userId = FindCanonicalLink(mode, provider, canonicalName);
        if (userId == Guid.Empty)
        {
            _logger.LogInformation("SSO user link doesn't exist, creating...");
            userId = user.Id;
            CreateCanonicalLink(mode, provider, userId, canonicalName);
        }

        return userId;
    }

    /// <summary>
    /// Whether Jellyfin would let anyone sign in to the account with an empty password: its own
    /// password provider accepts one for an account that has no password.
    /// </summary>
    private static bool AcceptsEmptyPassword(User user)
    {
        return string.IsNullOrEmpty(user.Password)
            && (string.IsNullOrEmpty(user.AuthenticationProviderId) || user.AuthenticationProviderId == JellyfinPasswordProvider);
    }

    /// <summary>
    /// Gives the account a password nobody knows and an authentication provider Jellyfin cannot
    /// sign in with, so it can only be used through SSO, and saves it.
    /// </summary>
    private async Task LockToSso(User user)
    {
        user.AuthenticationProviderId = GetType().FullName;
        // https://jonathancrozier.com/blog/how-to-generate-a-cryptographically-secure-random-string-in-dot-net-with-c-sharp
        user.Password = _cryptoProvider.CreatePasswordHash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))).ToString();

        // Saved now: Jellyfin 12 hands back a fresh copy when the account is next looked up, so
        // changes left unsaved on this instance would be lost, and the account would keep the
        // default provider with no password, which Jellyfin accepts an empty password for.
        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
    }

    private Guid GetCanonicalLink(string mode, string provider, string canonicalName)
    {
        return GetCanonicalLinks(mode, provider)[canonicalName];
    }

    /// <summary>
    /// The account linked to the provider's name for someone, or <see cref="Guid.Empty"/> if none is.
    /// </summary>
    private Guid FindCanonicalLink(string mode, string provider, string canonicalName)
    {
        try
        {
            return GetCanonicalLink(mode, provider, canonicalName);
        }
        catch (KeyNotFoundException)
        {
            return Guid.Empty;
        }
    }

    /// <summary>
    /// Create a canonical link for a given user. Must be performed by the user being changed, or admin.
    /// </summary>
    /// <param name="mode">The mode of the function; SAML or OID.</param>
    /// <param name="provider">The name of the provider to link to a jellyfin account.</param>
    /// <param name="jellyfinUserId">The user ID within jellyfin to link to the provider.</param>
    /// <param name="authResponse">The client information to authenticate the user with.</param>
    /// <returns>Whether this API endpoint succeeded.</returns>
    [Authorize]
    [HttpPost("{mode}/Link/{provider}/{jellyfinUserId}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> AddCanonicalLink([FromRoute] string mode, [FromRoute] string provider, [FromRoute] Guid jellyfinUserId, [FromBody] AuthResponse authResponse)
    {
        if (!await RequestHelpers.AssertCanUpdateUser(_authContext, HttpContext.Request, jellyfinUserId, true).ConfigureAwait(false))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "User is not allowed to link SSO providers.");
        }

        switch (mode.ToLower())
        {
            case "saml":
                return SamlLink(provider, jellyfinUserId, authResponse);
            case "oid":
                return OidLink(provider, jellyfinUserId, authResponse);
            default:
                throw new ArgumentException($"{mode} is not a valid choice between 'saml' and 'oid'");
        }
    }

    /// <summary>
    /// Unregisters a given mapping from id within provider to user.
    /// </summary>
    /// <param name="mode">The mode of the function; SAML or OID.</param>
    /// <param name="provider">The name of the provider from which the link should be removed.</param>
    /// <param name="jellyfinUserId">The user ID within jellyfin to unlink from the provider.</param>
    /// <param name="canonicalName">The user ID within jellyfin to unlink.</param>
    /// <returns>Whether this API endpoint succeeded.</returns>
    [Authorize]
    [HttpDelete("{mode}/Link/{provider}/{jellyfinUserId}/{canonicalName}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> DeleteCanonicalLink([FromRoute] string mode, [FromRoute] string provider, [FromRoute] Guid jellyfinUserId, [FromRoute] string canonicalName)
    {
        if (!await RequestHelpers.AssertCanUpdateUser(_authContext, HttpContext.Request, jellyfinUserId, true).ConfigureAwait(false))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Current user is not allowed to unlink SSO providers for user ID.");
        }

        Guid linkedId = GetCanonicalLink(mode, provider, canonicalName);

        if (linkedId != jellyfinUserId)
        {
            return StatusCode(StatusCodes.Status409Conflict, "jellyfin UID does not match id registered to that canonical name.");
        }

        var links = GetCanonicalLinks(mode, provider);

        links.Remove(canonicalName);

        return UpdateCanonicalLinkConfig(links, mode, provider);
    }

    /// <summary>
    /// Gets all the saml links for a user.
    /// </summary>
    /// <param name="jellyfinUserId">The user ID within jellyfin for which to return the links.</param>
    /// <returns>A dictionary of provider : link mappings.</returns>
    [Authorize]
    [HttpGet("saml/links/{jellyfinUserId}")]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<SerializableDictionary<string, IEnumerable<string>>>> GetSamlLinksByUser(Guid jellyfinUserId)
    {
        if (!await RequestHelpers.AssertCanUpdateUser(_authContext, HttpContext.Request, jellyfinUserId, true).ConfigureAwait(false))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Non-admin is not allowed to query other user's mappings.");
        }

        var mappings = new SerializableDictionary<string, IEnumerable<string>>();
        var providerList = SSOPlugin.Instance.Configuration.SamlConfigs;

        foreach (var providerName in providerList.Keys)
        {
            var canonLinks = providerList[providerName].CanonicalLinks;
            var canonKeys = from link in canonLinks where link.Value == jellyfinUserId select link.Key;
            mappings[providerName] = canonKeys;
        }

        return mappings;
    }

    /// <summary>
    /// Gets all the oid links for a user.
    /// </summary>
    /// <param name="jellyfinUserId">The user ID within jellyfin for which to return the links.</param>
    /// <returns>A dictionary of provider : link mappings.</returns>
    [Authorize]
    [HttpGet("oid/links/{jellyfinUserId}")]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<SerializableDictionary<string, IEnumerable<string>>>> GetOidLinksByUser(Guid jellyfinUserId)
    {
        if (!await RequestHelpers.AssertCanUpdateUser(_authContext, HttpContext.Request, jellyfinUserId, true).ConfigureAwait(false))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Non-admin is not allowed to query other user's mappings.");
        }

        var mappings = new SerializableDictionary<string, IEnumerable<string>>();
        var providerList = SSOPlugin.Instance.Configuration.OidConfigs;

        foreach (var providerName in providerList.Keys)
        {
            var canonLinks = providerList[providerName].CanonicalLinks;
            var canonKeys = from link in canonLinks where link.Value == jellyfinUserId select link.Key;
            mappings[providerName] = canonKeys;
        }

        return mappings;
    }

    /// <summary>
    /// Validate a saml link request and create the link if it is valid.
    /// </summary>
    /// <param name="provider">The provider to authenticate against.</param>
    /// <param name="jellyfinUserId">
    ///   The ID of the account to be linked to the provider.
    ///   Must be performed by this user, or an admin.
    /// </param>
    /// <param name="response">The data passed to the client to ensure it is the right one.</param>
    /// <returns>JSON for the client to populate information with.</returns>
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    private ActionResult SamlLink(string provider, Guid jellyfinUserId, AuthResponse response)
    {
        SamlConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.SamlConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        var samlResponse = new Response(config.SamlCertificate, response.Data);

        if (!samlResponse.IsValid())
        {
            return Problem("Invalid SAML signature");
        }

        string providerUserId = samlResponse.GetNameID();

        return CreateCanonicalLink("saml", provider, jellyfinUserId, providerUserId);
    }

    /// <summary>
    /// Validate an OIDC link request and create the link if it is valid.
    /// </summary>
    /// <param name="provider">The provider to authenticate against.</param>
    /// <param name="jellyfinUserId">
    ///   The ID of the account to be linked to the provider.
    ///   Must be performed by this user, or an admin.
    /// </param>
    /// <param name="response">The data passed to the client to ensure it is the right one.</param>
    /// <returns>JSON for the client to populate information with.</returns>
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    private ActionResult OidLink(string provider, Guid jellyfinUserId, AuthResponse response)
    {
        if (!SSOPlugin.Instance.Configuration.OidConfigs.ContainsKey(provider))
        {
            return BadRequest("No matching provider found");
        }

        foreach (var kvp in StateManager)
        {
            if (kvp.Value.State.State.Equals(response.Data) && kvp.Value.Valid)
            {
                string providerUserId = kvp.Value.Username;
                return CreateCanonicalLink("oid", provider, jellyfinUserId, providerUserId);
            }
        }

        return Problem("Something went wrong!");
    }

    private ActionResult CreateCanonicalLink(string mode, string provider, [FromRoute] Guid jellyfinUserId, string providerUserId)
    {
        SerializableDictionary<string, Guid> links = null;
        try
        {
            links = GetCanonicalLinks(mode, provider);
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        links[providerUserId] = jellyfinUserId;
        UpdateCanonicalLinkConfig(links, mode, provider);

        return NoContent();
    }

    private OkResult UpdateCanonicalLinkConfig(SerializableDictionary<string, Guid> links, string mode, string provider)
    {
        var configuration = SSOPlugin.Instance.Configuration;
        switch (mode.ToLower())
        {
            case "saml":
                configuration.SamlConfigs[provider].CanonicalLinks = links;
                break;
            case "oid":
                configuration.OidConfigs[provider].CanonicalLinks = links;
                break;
            default:
                throw new ArgumentException($"{mode} is not a valid choice between 'saml' and 'oid'");
        }

        SSOPlugin.Instance.UpdateConfiguration(configuration);
        return Ok();
    }

    /// <summary>
    /// Authenticates the user with the given information.
    /// </summary>
    /// <param name="userId">The user id of the user to authenticate.</param>
    /// <param name="isAdmin">Determines whether this user is an administrator.</param>
    /// <param name="enableAuthorization">Determines whether RBAC is used for this user.</param>
    /// <param name="enableAllFolders">Determines whether all folders are enabled.</param>
    /// <param name="enabledFolders">Determines which folders should be enabled for this client.</param>
    /// <param name="enableLiveTv">Determines whether live TV access is allowed for this user.</param>
    /// <param name="enableLiveTvAdmin">Determines whether live TV can be managed by this user.</param>
    /// <param name="authResponse">The client information to authenticate the user with.</param>
    /// <param name="defaultProvider">The default provider of the user to be set after logging in.</param>
    /// <param name="avatarUrl">The new avatar url for the user.</param>
    private async Task<AuthenticationResult> Authenticate(Guid userId, bool isAdmin, bool enableAuthorization, bool enableAllFolders, string[] enabledFolders, bool enableLiveTv, bool enableLiveTvAdmin, AuthResponse authResponse, string defaultProvider, string avatarUrl)
    {
        // The caller has just found or created this account.
        User user = _userManager.GetUserById(userId)
            ?? throw new InvalidOperationException("The account signing in no longer exists.");
        if (enableAuthorization)
        {
            user.SetPermission(PermissionKind.IsAdministrator, isAdmin);
            user.SetPermission(PermissionKind.EnableAllFolders, enableAllFolders);
            if (!enableAllFolders)
            {
                user.SetPreference(PreferenceKind.EnabledFolders, enabledFolders);
            }
        }

        if (avatarUrl is not null)
        {
            await SetProfilePicture(user, avatarUrl).ConfigureAwait(false);
        }

        user.SetPermission(PermissionKind.EnableLiveTvAccess, enableLiveTv);
        user.SetPermission(PermissionKind.EnableLiveTvManagement, enableLiveTvAdmin);

        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

        var authRequest = new AuthenticationRequest();
        authRequest.UserId = user.Id;
        authRequest.Username = user.Username;
        authRequest.App = authResponse.AppName;
        authRequest.AppVersion = authResponse.AppVersion;
        authRequest.DeviceId = authResponse.DeviceID;
        authRequest.DeviceName = authResponse.DeviceName;
        _logger.LogInformation("Auth request created...");
        if (!string.IsNullOrEmpty(defaultProvider))
        {
            user.AuthenticationProviderId = defaultProvider;
            await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
            _logger.LogInformation("Set default login provider to {Provider}", defaultProvider);
        }

        return await _sessionManager.AuthenticateDirect(authRequest).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the account's profile picture with the image at the address the provider's claims
    /// give. A picture that cannot be fetched or saved leaves the old one, and sign-in goes on.
    /// </summary>
    private async Task SetProfilePicture(User user, string avatarUrl)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();

            System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
            System.Diagnostics.FileVersionInfo fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);
            string version = fvi.FileVersion;
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"Jellyfin-Plugin-SsoAuthentication/{version} (+https://github.com/voc0der/jellyfin-plugin-sso)");

            // GetAsync reads the whole picture before it returns, so a download that breaks off
            // fails here, before anything has changed.
            using var avatarResponse = await client.GetAsync(avatarUrl).ConfigureAwait(false);

            // Accepted as Jellyfin accepts an uploaded picture, and named the same way, profile.png
            // and so on: the extension is how it later knows what it is serving.
            var contentType = avatarResponse.Content.Headers.ContentType?.MediaType;
            var extension = contentType is not null && MimeTypes.IsImage(contentType) ? MimeTypes.ToExtension(contentType) : null;
            if (!avatarResponse.IsSuccessStatusCode || extension is null)
            {
                _logger.LogWarning(
                    "The profile picture for {Username} came back as {StatusCode} {ContentType}, not as an image; keeping the old one",
                    user.Username,
                    (int)avatarResponse.StatusCode,
                    contentType);
                return;
            }

            // Join, not Combine: Combine drops the folder if a later part is a rooted path.
            var path = Path.Join(_serverConfigurationManager.ApplicationPaths.UserConfigurationDirectoryPath, user.Username, "profile" + extension);
            var stream = await avatarResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);

            // The file first and the account after, so a picture the disk refuses leaves the account
            // with the one it had. Clearing the old picture forgets it, and leaves its file alone.
            await _providerManager.SaveImage(stream, contentType, path).ConfigureAwait(false);
            if (user.ProfileImage is not null)
            {
                await _userManager.ClearProfileImageAsync(user).ConfigureAwait(false);
            }

            user.ProfileImage = new ImageInfo(path);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Deliberately broad: the fetch, the database and the disk can each fail in their own
            // ways, and none of them is a reason to turn someone away. Only a fatal error goes on.
            _logger.LogError(e, "Could not set the profile picture of {Username}", user.Username);
        }
    }

    private void Invalidate()
    {
        foreach (var kvp in StateManager)
        {
            var now = DateTime.Now;
            if (now.Subtract(kvp.Value.Created).TotalMinutes > 1)
            {
                StateManager.Remove(kvp.Key);
            }
        }
    }

    private string GetRequestBase(string schemeOverride = null, int? portOverride = null)
    {
        int requestPort = portOverride ?? Request.Host.Port ?? -1;

        if ((requestPort == 80 && string.Equals(Request.Scheme, "http", StringComparison.OrdinalIgnoreCase)) || (requestPort == 443 && string.Equals(Request.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
        {
            requestPort = -1;
        }

        if (schemeOverride != "http" && schemeOverride != "https")
        {
            schemeOverride = null;
        }

        return new UriBuilder
        {
            Scheme = schemeOverride ?? Request.Scheme,
            Host = Request.Host.Host,
            Port = requestPort,
            Path = Request.PathBase
        }.ToString().TrimEnd('/');
    }

    /// <summary>
    /// Whether a SAML response's Role attributes let its user sign in: any one of the
    /// provider's roles will do, and a provider with no roles set lets everyone in.
    /// </summary>
    private static bool SamlRolesAllow(SamlConfig config, Response samlResponse)
    {
        if (config.Roles == null || config.Roles.Length == 0)
        {
            return true;
        }

        return samlResponse.GetCustomAttributes("Role").Any(role => config.Roles.Contains(role));
    }

    private ContentResult ReturnError(int code, string message)
    {
        var errorResult = new ContentResult();
        errorResult.Content = message;
        errorResult.ContentType = MediaTypeNames.Text.Plain;
        errorResult.StatusCode = code;
        return errorResult;
    }
}

/// <summary>
/// The data the client should pass back to the API.
/// </summary>
public class AuthResponse
{
    /// <summary>
    /// Gets or sets the device ID of the client.
    /// </summary>
    public string DeviceID { get; set; }

    /// <summary>
    /// Gets or sets the device name of the client.
    /// </summary>
    public string DeviceName { get; set; }

    /// <summary>
    /// Gets or sets the app name of the client.
    /// </summary>
    public string AppName { get; set; }

    /// <summary>
    /// Gets or sets the app version of the client.
    /// </summary>
    public string AppVersion { get; set; }

    /// <summary>
    /// Gets or sets the auth data of the client (for authorizing the response).
    /// </summary>
    public string Data { get; set; }
}

/// <summary>
/// A manager for OpenID to manage the state of the clients.
/// </summary>
public class TimedAuthorizeState
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TimedAuthorizeState"/> class.
    /// </summary>
    /// <param name="state">The AuthorizeState to time.</param>
    /// <param name="created">When this state was created.</param>
    public TimedAuthorizeState(AuthorizeState state, DateTime created)
    {
        State = state;
        Created = created;
        Valid = false;
        Admin = false;
        IsLinking = false;
        EnableLiveTv = false;
        EnableLiveTvManagement = false;
        AvatarURL = null;
    }

    /// <summary>
    /// Gets or sets the Authorization State of the client.
    /// </summary>
    public AuthorizeState State { get; set; }

    /// <summary>
    /// Gets or sets when this object was created to time it out.
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is valid.
    /// </summary>
    public bool Valid { get; set; }

    /// <summary>
    /// Gets or sets the user tied to the state.
    /// </summary>
    public string Username { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is an administrator.
    /// </summary>
    public bool Admin { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the state is
    /// tied to a linking flow (instead of a login flow).
    /// </summary>
    public bool IsLinking { get; set; }

    /// <summary>
    /// Gets or sets the folders the user is allowed access to.
    /// </summary>
    public List<string> Folders { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is allowed to view live TV.
    /// </summary>
    public bool EnableLiveTv { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is allowed to manage live TV.
    /// </summary>
    public bool EnableLiveTvManagement { get; set; }

    /// <summary>
    /// Gets or sets the user avatar url.
    /// </summary>
    public string AvatarURL { get; set; }
}
