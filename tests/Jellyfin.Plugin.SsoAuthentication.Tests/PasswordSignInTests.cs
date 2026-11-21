using System.Net;
using System.Reflection;
using Jellyfin.Plugin.SsoAuthentication.Api;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Jellyfin.Plugin.SsoAuthentication.Tests;

/// <summary>
/// JELLYFIN_ENABLE_PASSWORD_LOGIN=false turns off Jellyfin's username and password sign-in, and
/// nothing else: not sign-in through the plugin, not Quick Connect, and not on any other value.
/// Whether the filter reaches Jellyfin's own action is checked against a real Jellyfin by tests/e2e.
/// </summary>
public sealed class PasswordSignInTests
{
    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    [InlineData(" false ")]
    public void False_in_any_case_turns_password_sign_in_off(string value)
    {
        Assert.True(PasswordSignIn.TurnsOff(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData("disabled")]
    public void Unset_true_or_anything_else_leaves_it_on(string? value)
    {
        Assert.False(PasswordSignIn.TurnsOff(value));
    }

    [Fact]
    public void Turned_off_every_request_passes_the_filter()
    {
        var services = new ServiceCollection().AddLogging();

        PluginServiceRegistrator.Register(services, "false");

        using var provider = services.BuildServiceProvider();
        var filter = Assert.Single(provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters.OfType<ServiceFilterAttribute>());
        Assert.Equal(typeof(PasswordSignInFilter), filter.ServiceType);
        Assert.IsType<PasswordSignInFilter>(filter.CreateInstance(provider));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    [InlineData("no")]
    public void Left_on_nothing_is_registered_at_all(string? value)
    {
        var services = new ServiceCollection();

        PluginServiceRegistrator.Register(services, value);

        Assert.Empty(services);
    }

    [Fact]
    public void Jellyfin_registers_from_the_servers_environment()
    {
        using (new EnvironmentVariable("false"))
        {
            var services = new ServiceCollection();
            new PluginServiceRegistrator().RegisterServices(services, Substitute.For<IServerApplicationHost>());
            Assert.Contains(services, service => service.ServiceType == typeof(PasswordSignInFilter));
        }

        using (new EnvironmentVariable(null))
        {
            // Nothing of the switch: only automatic sign-in's filter, which every server gets.
            var services = new ServiceCollection();
            new PluginServiceRegistrator().RegisterServices(services, Substitute.For<IServerApplicationHost>());
            var only = Assert.Single(services);
            Assert.Equal(typeof(AutoSignInStartupFilter), only.ImplementationType);
        }
    }

    [Theory]
    [InlineData("Users/AuthenticateByName")]
    [InlineData("users/authenticatebyname")]
    public void A_password_sign_in_is_refused_as_forbidden(string route)
    {
        var context = Request(route);

        new PasswordSignInFilter(new RecordingLogger<PasswordSignInFilter>()).OnResourceExecuting(context);

        // 403, not 401: Jellyfin Web says "invalid username or password" for 401, which would send
        // people looking for a password that is not the problem.
        var result = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal("text/plain", result.ContentType);
        Assert.Contains("turned off", result.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Users/AuthenticateWithQuickConnect")]
    [InlineData("QuickConnect/Authorize")]
    [InlineData("QuickConnect/Initiate")]
    [InlineData("Users/Password")]
    [InlineData("Users/ForgotPassword")]
    [InlineData("Users/Me")]
    [InlineData("Startup/User")]
    [InlineData(null)]
    public void Every_other_request_goes_through(string? route)
    {
        var context = Request(route);
        var log = new RecordingLogger<PasswordSignInFilter>();

        new PasswordSignInFilter(log).OnResourceExecuting(context);

        Assert.Null(context.Result);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Sign_in_through_the_plugin_goes_through()
    {
        // Every route the plugin answers, as its attributes declare them.
        var routes = typeof(SSOController).GetMethods()
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>())
            .Select(attribute => "SSO/" + attribute.Template)
            .ToList();
        Assert.Contains("SSO/OID/Auth/{provider}", routes);
        Assert.Contains("SSO/SAML/Auth/{provider}", routes);

        foreach (var context in routes.Select(Request))
        {
            new PasswordSignInFilter(new RecordingLogger<PasswordSignInFilter>()).OnResourceExecuting(context);
            Assert.Null(context.Result);
        }
    }

    [Fact]
    public void A_refusal_is_logged_with_the_address_it_came_from()
    {
        var context = Request("Users/AuthenticateByName");
        context.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.7");
        var log = new RecordingLogger<PasswordSignInFilter>();

        new PasswordSignInFilter(log).OnResourceExecuting(context);

        var (level, message) = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, level);
        Assert.Equal("Refused a password sign-in from 192.0.2.7: JELLYFIN_ENABLE_PASSWORD_LOGIN is false", message);
    }

    [Fact]
    public void Finishing_an_allowed_request_keeps_its_response()
    {
        var executing = Request("Users/AuthenticateWithQuickConnect");
        var log = new RecordingLogger<PasswordSignInFilter>();
        var filter = new PasswordSignInFilter(log);
        filter.OnResourceExecuting(executing);
        Assert.Null(executing.Result);
        var result = new OkObjectResult("session");
        var executed = new ResourceExecutedContext(executing, executing.Filters) { Result = result };

        filter.OnResourceExecuted(executed);

        Assert.Same(result, executed.Result);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Startup_says_password_sign_in_is_off()
    {
        var log = new RecordingLogger<SSOPlugin>();

        PasswordSignIn.Log(log, "false");

        var (level, message) = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, level);
        Assert.StartsWith("Password sign-in is off: JELLYFIN_ENABLE_PASSWORD_LOGIN is false", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_warns_of_a_value_that_does_nothing()
    {
        var log = new RecordingLogger<SSOPlugin>();

        PasswordSignIn.Log(log, "off");

        var (level, message) = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Equal("JELLYFIN_ENABLE_PASSWORD_LOGIN is set to neither true nor false; password sign-in stays on", message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("true")]
    public void Startup_says_nothing_while_password_sign_in_is_on(string? value)
    {
        var log = new RecordingLogger<SSOPlugin>();

        PasswordSignIn.Log(log, value);

        Assert.Empty(log.Entries);
    }

    [Fact]
    public void The_plugin_reads_the_servers_environment_when_it_starts()
    {
        using var variable = new EnvironmentVariable("false");
        using var scope = new PluginScope();

        Assert.Contains(scope.Logger.Entries, entry => entry.Message.StartsWith("Password sign-in is off", StringComparison.Ordinal));
    }

    /// <summary>A request routed to the action with this attribute route, or a conventional one for null.</summary>
    private static ResourceExecutingContext Request(string? route)
    {
        var action = new ActionDescriptor
        {
            AttributeRouteInfo = route is null ? null : new AttributeRouteInfo { Template = route }
        };

        return new ResourceExecutingContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), action),
            new List<IFilterMetadata>(),
            new List<IValueProviderFactory>());
    }

    /// <summary>
    /// Sets JELLYFIN_ENABLE_PASSWORD_LOGIN for this process until disposed. Safe because the tests
    /// in this assembly never run in parallel.
    /// </summary>
    private sealed class EnvironmentVariable : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable(PasswordSignIn.EnvironmentVariable);

        public EnvironmentVariable(string? value)
        {
            Environment.SetEnvironmentVariable(PasswordSignIn.EnvironmentVariable, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(PasswordSignIn.EnvironmentVariable, _previous);
    }
}
