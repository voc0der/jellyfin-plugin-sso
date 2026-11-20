using Jellyfin.Plugin.SsoAuthentication.Views;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.SsoAuthentication.Tests;

/// <summary>
/// /SSOViews serves the self-service linking page and its scripts out of the DLL.
/// </summary>
public sealed class ViewsControllerTests : IDisposable
{
    private readonly PluginScope _scope = new();

    public void Dispose() => _scope.Dispose();

    private static SSOViewsController Controller()
    {
        var controller = new SSOViewsController(
            NullLogger<SSOViewsController>.Instance,
            Substitute.For<ISessionManager>(),
            Substitute.For<IUserManager>(),
            Substitute.For<IAuthorizationContext>());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    [Theory]
    [InlineData("linking", "text/html")]
    [InlineData("linking.js", "application/javascript")]
    [InlineData("ApiClient.js", "application/javascript")]
    [InlineData("jellyfin-apiClient.esm.min.js", "application/javascript")]
    [InlineData("style.css", "text/css")]
    [InlineData("emby-restyle.css", "text/css")]
    public void Each_view_is_served_with_its_type(string view, string contentType)
    {
        // Browsers only run a module script served with a JavaScript type; Jellyfin's own
        // MIME table answers application/javascript for .js.
        var result = Assert.IsType<FileStreamResult>(Controller().GetView(view));

        Assert.Equal(contentType, result.ContentType);
        Assert.True(result.FileStream.Length > 0);
        result.FileStream.Dispose();
    }

    [Theory]
    [InlineData("missing.html")]
    [InlineData("../Jellyfin.Plugin.SsoAuthentication.dll")]
    [InlineData("sso-authentication.js")]
    public void Anything_else_is_not_found(string view)
    {
        Assert.IsAssignableFrom<NotFoundObjectResult>(Controller().GetView(view));
    }
}
