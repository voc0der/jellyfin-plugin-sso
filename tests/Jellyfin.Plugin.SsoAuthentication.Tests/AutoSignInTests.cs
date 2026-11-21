using System.IO.Compression;
using System.Text;
using Jellyfin.Plugin.SsoAuthentication.Api;
using Jellyfin.Plugin.SsoAuthentication.Config;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Jellyfin.Plugin.SsoAuthentication.Tests;

/// <summary>
/// Automatic sign-in adds a script to Jellyfin Web's page while it is on, and leaves the page, and
/// everything else, as Jellyfin serves it otherwise. What the script then does in a browser is
/// checked by tests/config-page against a stand-in for Jellyfin Web, and by tests/e2e against a
/// real Jellyfin Web and a real provider.
/// </summary>
public sealed class AutoSignInTests : IDisposable
{
    // Jellyfin Web 12.4's page, cut down: its scripts are deferred, in its head.
    private const string JellyfinWebPage = "<!doctype html><html dir=\"ltr\"><head><meta charset=\"utf-8\"><title>Jellyfin</title>"
        + "<style>html{background-color:#101010}</style>"
        + "<script defer=\"defer\" src=\"runtime.bundle.js?c4b2af8a2931aff1f2de\"></script>"
        + "<script defer=\"defer\" src=\"main.jellyfin.bundle.js?c4b2af8a2931aff1f2de\"></script>"
        + "</head><body dir=\"ltr\"><div id=\"reactRoot\"><div class=\"splashLogo\"></div></div></body></html>";

    private const string FirstScript = "<script defer=\"defer\" src=\"runtime.bundle.js";

    private readonly PluginScope _scope = new();

    public void Dispose() => _scope.Dispose();

    private static string Tag(string start) => AutoSignIn.ScriptTag(start);

    /// <summary>The settings, with an enabled and a disabled OpenID provider and a SAML one.</summary>
    private PluginConfiguration Settings(bool on = true, string? provider = "authelia", string? protocol = null)
    {
        var configuration = _scope.Plugin.Configuration;
        configuration.OidConfigs["authelia"] = new OidConfig { Enabled = true };
        configuration.OidConfigs["disabled"] = new OidConfig { Enabled = false };
        configuration.SamlConfigs["keycloak"] = new SamlConfig { Enabled = true };
        configuration.EnableAutoSignIn = on;
        configuration.AutoSignInProvider = provider!;
        configuration.AutoSignInProtocol = protocol!;
        return configuration;
    }

    [Fact]
    public void It_is_off_until_turned_on()
    {
        Assert.False(new PluginConfiguration().EnableAutoSignIn);
        Assert.Null(AutoSignIn.StartPath(new PluginConfiguration()));
        Assert.Null(AutoSignIn.StartPath(null!));
        Assert.Null(AutoSignIn.StartPath(Settings(on: false)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OID")]
    [InlineData("oid")]
    public void An_openid_provider_is_sent_to_its_start_address(string? protocol)
    {
        Assert.Equal("../sso/OID/start/authelia", AutoSignIn.StartPath(Settings(protocol: protocol)));
    }

    [Theory]
    [InlineData("SAML")]
    [InlineData("saml")]
    public void A_saml_provider_is_sent_to_its_start_address(string protocol)
    {
        Assert.Equal("../sso/SAML/start/keycloak", AutoSignIn.StartPath(Settings(provider: "keycloak", protocol: protocol)));
    }

    [Theory]
    [InlineData("disabled", null)]
    [InlineData("missing", null)]
    [InlineData("keycloak", null)]
    [InlineData("authelia", "SAML")]
    [InlineData("authelia", "LDAP")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_provider_that_is_disabled_missing_or_of_another_kind_sends_no_one_anywhere(string? provider, string? protocol)
    {
        Assert.Null(AutoSignIn.StartPath(Settings(provider: provider, protocol: protocol)));
    }

    [Fact]
    public void Settings_with_no_providers_at_all_send_no_one_anywhere()
    {
        var configuration = new PluginConfiguration { EnableAutoSignIn = true, AutoSignInProvider = "authelia", OidConfigs = null!, SamlConfigs = null! };

        Assert.Null(AutoSignIn.StartPath(configuration));
        configuration.AutoSignInProtocol = "SAML";
        Assert.Null(AutoSignIn.StartPath(configuration));
    }

    [Fact]
    public void The_providers_name_is_one_segment_of_the_address()
    {
        var configuration = Settings(provider: "Home Media/ü?");
        configuration.OidConfigs["Home Media/ü?"] = new OidConfig { Enabled = true };

        Assert.Equal("../sso/OID/start/Home%20Media%2F%C3%BC%3F", AutoSignIn.StartPath(configuration));
    }

    [Theory]
    [InlineData("/web/", true)]
    [InlineData("/web/index.html", true)]
    [InlineData("/WEB/Index.HTML", true)]
    [InlineData("/jellyfin/web/", true)]
    [InlineData("/jellyfin/web/index.html", true)]
    [InlineData("/web", false)]
    [InlineData("/", false)]
    [InlineData("", false)]
    [InlineData("/web/main.jellyfin.bundle.js", false)]
    [InlineData("/web/index.html.map", false)]
    [InlineData("/SSOViews/linking", false)]
    [InlineData("/sso/OID/start/authelia", false)]
    public void Only_jellyfin_webs_page_is_touched(string path, bool expected)
    {
        Assert.Equal(expected, AutoSignIn.IsJellyfinWeb(new PathString(path)));
    }

    [Fact]
    public void A_request_with_no_path_is_not_the_page()
    {
        Assert.False(AutoSignIn.IsJellyfinWeb(default));
    }

    [Theory]
    [InlineData("GET", "/sso/OID/start/authelia", "text/html", true)]
    [InlineData("GET", "/jellyfin/sso/oid/redirect/authelia", "text/html,application/xhtml+xml", true)]
    [InlineData("POST", "/sso/SAML/post/keycloak", "text/html", true)]
    [InlineData("GET", "/sso/OID/r/authelia", "text/html", true)]
    [InlineData("GET", "/sso/SAML/p/keycloak", "text/html", true)]
    [InlineData("DELETE", "/sso/OID/start/authelia", "text/html", false)]
    [InlineData("GET", "/sso/OID/start/authelia", "application/json", false)]
    [InlineData("GET", "/web/", "text/html", false)]
    [InlineData("GET", "/sso/OID/redirect", "text/html", false)]
    [InlineData("GET", "/sso/OID/redirect/", "text/html", false)]
    [InlineData("GET", "/sso/LDAP/start/provider", "text/html", false)]
    [InlineData("POST", "/sso/OID/Auth/authelia", "text/html", false)]
    [InlineData("POST", "/sso/OID/Link/authelia/user", "text/html", false)]
    public void Only_browser_sign_in_navigations_get_failure_pages(string method, string path, string accept, bool expected)
    {
        var context = Request(path);
        context.Request.Method = method;
        context.Request.Headers.Accept = accept;

        Assert.Equal(expected, AutoSignIn.IsSignIn(context.Request));
    }

    [Fact]
    public async Task A_failed_browser_sign_in_keeps_its_status_and_gets_a_safe_login_fallback()
    {
        Settings();
        var context = Request("/sso/OID/redirect/authelia");
        context.Request.PathBase = "/jellyfin";
        context.Request.Headers.Accept = "text/html";

        await Middleware(Serves("<script>alert('untrusted provider error')</script>", "text/plain", 401)).InvokeAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
        Assert.Equal("text/html", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal(Encoding.UTF8.GetByteCount(Body(context)), context.Response.ContentLength);
        Assert.Contains("data-sso-auto-fallback=\"../../../web/index.html#/login\"", Body(context), StringComparison.Ordinal);
        Assert.Contains("src=\"../../../SSOViews/auto-sign-in.js?v=", Body(context), StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", Body(context), StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert", Body(context), StringComparison.Ordinal);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        Assert.False(context.Response.Headers.ContainsKey("Last-Modified"));
    }

    [Fact]
    public async Task A_successful_callback_is_not_given_the_automatic_start_script()
    {
        Settings();
        var context = Request("/sso/OID/redirect/authelia");
        context.Request.Headers.Accept = "text/html";

        await Middleware(Serves(JellyfinWebPage)).InvokeAsync(context);

        Assert.Equal(JellyfinWebPage, Body(context));
    }

    [Fact]
    public void The_tag_is_deferred_versioned_and_escapes_the_address()
    {
        var version = typeof(SSOPlugin).Assembly.GetName().Version;

        Assert.Equal(
            $"<script defer src=\"../SSOViews/auto-sign-in.js?v={version}\" data-sso-auto-sign-in=\"../sso/OID/start/a&quot;b&lt;c&gt;&amp;d\"></script>",
            Tag("../sso/OID/start/a\"b<c>&d"));
    }

    [Fact]
    public void The_tag_goes_ahead_of_jellyfin_webs_first_script()
    {
        var page = AutoSignIn.Inject(Encoding.UTF8.GetBytes(JellyfinWebPage), "<tag>");

        Assert.Equal(JellyfinWebPage.Replace(FirstScript, "<tag>" + FirstScript, StringComparison.Ordinal), Encoding.UTF8.GetString(page));
    }

    [Theory]
    [InlineData("<html><head><title>x</title></head><body><script src=\"a.js\"></script></body></html>", "<html><head><title>x</title><tag></head><body><script src=\"a.js\"></script></body></html>")]
    [InlineData("<HTML><HEAD></HEAD></HTML>", "<HTML><HEAD><tag></HEAD></HTML>")]
    public void A_page_with_no_script_in_its_head_gets_the_tag_at_the_end_of_it(string page, string expected)
    {
        Assert.Equal(expected, Encoding.UTF8.GetString(AutoSignIn.Inject(Encoding.UTF8.GetBytes(page), "<tag>")));
    }

    [Fact]
    public void A_page_with_no_head_or_not_in_utf8_is_left_alone()
    {
        Assert.Null(AutoSignIn.Inject(Encoding.UTF8.GetBytes("<html><body>Jellyfin</body></html>"), "<tag>"));
        Assert.Null(AutoSignIn.Inject([0x3C, 0x68, 0x65, 0x61, 0x64, 0x3E, 0xC3, 0x28, 0x3C, 0x2F, 0x68, 0x65, 0x61, 0x64, 0x3E], "<tag>"));
    }

    [Fact]
    public void A_byte_order_mark_is_kept()
    {
        var page = AutoSignIn.Inject([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("<head></head>")], "<tag>");

        byte[] expected = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("<head><tag></head>")];
        Assert.Equal(expected, page);
    }

    [Fact]
    public async Task Off_the_page_and_its_request_pass_through_untouched()
    {
        Settings(on: false);
        var context = Request("/web/", "gzip", "\"1dc0ffee\"");

        await Middleware(Serves(JellyfinWebPage)).InvokeAsync(context);

        Assert.Equal(JellyfinWebPage, Body(context));
        Assert.Equal("gzip", context.Request.Headers.AcceptEncoding);
        Assert.Equal("\"1dc0ffee\"", context.Request.Headers.IfNoneMatch);
        Assert.Equal("\"1dc0ffee\"", context.Response.Headers.ETag);
    }

    [Theory]
    [InlineData("/web/")]
    [InlineData("/web/index.html")]
    [InlineData("/jellyfin/web/")]
    public async Task On_the_script_goes_into_the_page_whose_validators_go(string path)
    {
        Settings();
        var context = Request(path);

        await Middleware(Serves(JellyfinWebPage)).InvokeAsync(context);

        var page = JellyfinWebPage.Replace(FirstScript, Tag("../sso/OID/start/authelia") + FirstScript, StringComparison.Ordinal);
        Assert.Equal(page, Body(context));
        Assert.Equal(Encoding.UTF8.GetByteCount(page), context.Response.ContentLength);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        Assert.False(context.Response.Headers.ContainsKey("Last-Modified"));
        Assert.False(context.Response.Headers.ContainsKey("Accept-Ranges"));

        // Jellyfin's own: the browser asks again at every load, and gets the page whole.
        Assert.Equal("no-cache", context.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task On_the_whole_page_is_asked_for_uncompressed_and_unconditionally()
    {
        Settings();
        var context = Request("/web/", "gzip, br", "\"1dc0ffee\"");
        context.Request.Headers.IfModifiedSince = "Mon, 26 Oct 2026 17:19:00 GMT";
        context.Request.Headers.Range = "bytes=0-10";
        context.Request.Headers.IfRange = "\"1dc0ffee\"";
        IHeaderDictionary? asked = null;

        await Middleware(next =>
        {
            asked = next.Request.Headers;
            return Serves(JellyfinWebPage)(next);
        }).InvokeAsync(context);

        Assert.NotNull(asked);
        foreach (var header in new[] { "Accept-Encoding", "If-None-Match", "If-Modified-Since", "Range", "If-Range" })
        {
            Assert.False(asked!.ContainsKey(header), header + " reached Jellyfin");
        }
    }

    [Theory]
    [InlineData("HEAD", "/web/")]
    [InlineData("POST", "/web/")]
    [InlineData("GET", "/web/main.jellyfin.bundle.js")]
    [InlineData("GET", "/sso/OID/start/authelia")]
    public async Task Every_other_request_passes_through_untouched(string method, string path)
    {
        Settings();
        var context = Request(path, "gzip", "\"1dc0ffee\"");
        context.Request.Method = method;

        await Middleware(Serves(JellyfinWebPage)).InvokeAsync(context);

        Assert.Equal(JellyfinWebPage, Body(context));
        Assert.Equal("gzip", context.Request.Headers.AcceptEncoding);
        Assert.Equal("\"1dc0ffee\"", context.Response.Headers.ETag);
    }

    [Theory]
    [InlineData(StatusCodes.Status302Found, "text/html")]
    [InlineData(StatusCodes.Status304NotModified, "text/html")]
    [InlineData(StatusCodes.Status404NotFound, "text/html")]
    [InlineData(StatusCodes.Status200OK, "text/plain")]
    [InlineData(StatusCodes.Status200OK, null)]
    public async Task An_answer_that_is_not_the_page_is_passed_on_as_it_is(int status, string? contentType)
    {
        Settings();
        var context = Request("/web/");

        await Middleware(Serves(JellyfinWebPage, contentType, status)).InvokeAsync(context);

        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(JellyfinWebPage, Body(context));
        Assert.Equal("\"1dc0ffee\"", context.Response.Headers.ETag);
    }

    [Fact]
    public async Task A_page_already_compressed_is_passed_on_as_it_is()
    {
        Settings();
        var context = Request("/web/");

        await Middleware(next =>
        {
            next.Response.Headers.ContentEncoding = "br";
            return Serves(JellyfinWebPage)(next);
        }).InvokeAsync(context);

        Assert.Equal(JellyfinWebPage, Body(context));
    }

    [Fact]
    public async Task A_page_the_script_cannot_go_into_is_served_as_it_is_with_one_warning()
    {
        Settings();
        var log = new RecordingLogger<AutoSignInMiddleware>();
        var middleware = new AutoSignInMiddleware(Serves("<html><body>Jellyfin</body></html>"), log);

        for (var load = 0; load < 2; load++)
        {
            var context = Request("/web/");
            await middleware.InvokeAsync(context);
            Assert.Equal("<html><body>Jellyfin</body></html>", Body(context));
        }

        var (_, message) = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("cannot add its script", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_in_jellyfin_reaches_jellyfin_with_nothing_sent()
    {
        Settings();
        var context = Request("/web/");
        var body = context.Response.Body;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Middleware(next =>
        {
            next.Response.Body.Write("<!doctype"u8);
            throw new InvalidOperationException("disk gone");
        }).InvokeAsync(context));

        Assert.Equal("disk gone", failure.Message);
        Assert.Same(body, context.Response.Body);
        Assert.Equal(0, body.Length);
    }

    [Fact]
    public async Task Settings_that_cannot_be_read_leave_jellyfin_web_alone_and_are_logged_once()
    {
        using var broken = new PluginScope(blockConfigurationFile: true);
        var log = new RecordingLogger<AutoSignInMiddleware>();
        var middleware = new AutoSignInMiddleware(Serves(JellyfinWebPage), log);

        for (var load = 0; load < 2; load++)
        {
            var context = Request("/web/");
            await middleware.InvokeAsync(context);
            Assert.Equal(JellyfinWebPage, Body(context));
        }

        var (level, message) = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, level);
        Assert.Equal("Automatic sign-in is off until the plugin's settings can be read", message);
    }

    [Fact]
    public async Task Before_the_plugin_has_started_nothing_is_added()
    {
        var instance = typeof(SSOPlugin).GetProperty(nameof(SSOPlugin.Instance))!;
        var previous = SSOPlugin.Instance;
        try
        {
            instance.SetValue(null, null);
            var context = Request("/web/");

            await Middleware(Serves(JellyfinWebPage)).InvokeAsync(context);

            Assert.Equal(JellyfinWebPage, Body(context));
        }
        finally
        {
            instance.SetValue(null, previous);
        }
    }

    [Fact]
    public async Task The_log_says_what_automatic_sign_in_does_whenever_that_changes()
    {
        var log = new RecordingLogger<AutoSignInMiddleware>();
        var middleware = new AutoSignInMiddleware(Serves(JellyfinWebPage), log);
        async Task<IList<(LogLevel Level, string Message)>> Load()
        {
            var before = log.Entries.Count;
            await middleware.InvokeAsync(Request("/web/"));
            return log.Entries.Skip(before).ToList();
        }

        // Off from the start is how Jellyfin always was: nothing to say.
        Settings(on: false);
        Assert.Empty(await Load());

        Settings();
        Assert.Equal(new[] { (LogLevel.Information, "Automatic sign-in is on: Jellyfin Web sends anyone who is not signed in to the OID provider authelia") }, await Load());
        Assert.Empty(await Load());

        Settings(provider: "disabled");
        Assert.Equal(new[] { (LogLevel.Warning, "Automatic sign-in is on, but no enabled OID provider is named disabled, so Jellyfin Web shows its login page") }, await Load());
        Assert.Empty(await Load());

        Settings(provider: "keycloak", protocol: "SAML");
        Assert.Equal(new[] { (LogLevel.Information, "Automatic sign-in is on: Jellyfin Web sends anyone who is not signed in to the SAML provider keycloak") }, await Load());

        Settings(on: false);
        Assert.Equal(new[] { (LogLevel.Information, "Automatic sign-in is off: Jellyfin Web shows its login page") }, await Load());
        Assert.Empty(await Load());
    }

    [Fact]
    public async Task In_jellyfins_pipeline_the_page_comes_whole_with_the_script()
    {
        using var web = new WebFolder();
        var jellyfin = Jellyfin(web.Path);

        // A browser that kept the page from before automatic sign-in was turned on, which
        // Jellyfin would answer with 304 Not Modified, and so leave without the script.
        Settings(on: false);
        var before = Request("/web/");
        await jellyfin(before);
        var etag = before.Response.Headers.ETag.ToString();
        Assert.NotEmpty(etag);
        Settings();
        var context = Request("/web/", "gzip, br", etag);
        context.Request.Headers.IfModifiedSince = before.Response.Headers.LastModified;

        await jellyfin(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Content-Encoding"));
        var page = JellyfinWebPage.Replace(FirstScript, Tag("../sso/OID/start/authelia") + FirstScript, StringComparison.Ordinal);
        Assert.Equal(page, Body(context));
        Assert.Equal(Encoding.UTF8.GetByteCount(page), context.Response.ContentLength);
        Assert.Equal("no-cache", context.Response.Headers.CacheControl);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
    }

    [Fact]
    public async Task In_jellyfins_pipeline_with_it_off_the_page_is_jellyfins_own()
    {
        Settings(on: false);
        using var web = new WebFolder();
        var jellyfin = Jellyfin(web.Path);

        var first = Request("/web/index.html");
        await jellyfin(first);
        Assert.Equal(JellyfinWebPage, Body(first));
        var etag = first.Response.Headers.ETag.ToString();
        Assert.NotEmpty(etag);

        // Jellyfin's caching and compression work as they would without the plugin.
        var again = Request("/web/index.html", ifNoneMatch: etag);
        await jellyfin(again);
        Assert.Equal(StatusCodes.Status304NotModified, again.Response.StatusCode);

        var compressed = Request("/web/", "gzip");
        await jellyfin(compressed);
        Assert.Equal("gzip", compressed.Response.Headers.ContentEncoding);
        using var gzip = new GZipStream(new MemoryStream(((MemoryStream)compressed.Response.Body).ToArray()), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        Assert.Equal(JellyfinWebPage, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Under_a_base_url_the_page_is_found_there_and_only_there()
    {
        Settings();
        using var web = new WebFolder();
        var jellyfin = Jellyfin(web.Path, "/jellyfin");

        var context = Request("/jellyfin/web/");
        await jellyfin(context);
        Assert.Contains("data-sso-auto-sign-in=\"../sso/OID/start/authelia\"", Body(context), StringComparison.Ordinal);

        var outside = Request("/web/");
        await jellyfin(outside);
        Assert.Equal(StatusCodes.Status404NotFound, outside.Response.StatusCode);
        Assert.Equal(string.Empty, Body(outside));
    }

    [Fact]
    public void Jellyfin_always_registers_the_page_filter()
    {
        var services = new ServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, Substitute.For<IServerApplicationHost>());

        var filter = Assert.Single(services, service => service.ServiceType == typeof(IStartupFilter));
        Assert.Equal(typeof(AutoSignInStartupFilter), filter.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, filter.Lifetime);
    }

    private static AutoSignInMiddleware Middleware(RequestDelegate next) => new(next, NullLogger<AutoSignInMiddleware>.Instance);

    private static DefaultHttpContext Request(string path, string? acceptEncoding = null, string? ifNoneMatch = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("jellyfin.example.test");
        context.Request.Path = path;
        if (acceptEncoding is not null)
        {
            context.Request.Headers.AcceptEncoding = acceptEncoding;
        }

        if (ifNoneMatch is not null)
        {
            context.Request.Headers.IfNoneMatch = ifNoneMatch;
        }

        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string Body(HttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    /// <summary>Jellyfin's static file middleware, as far as this matters: the page with its validators.</summary>
    private static RequestDelegate Serves(string page, string? contentType = "text/html", int status = StatusCodes.Status200OK) => context =>
    {
        var bytes = Encoding.UTF8.GetBytes(page);
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.ETag = "\"1dc0ffee\"";
        context.Response.Headers.LastModified = "Mon, 26 Oct 2026 17:19:00 GMT";
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers.CacheControl = "no-cache";
        return context.Response.Body.WriteAsync(bytes).AsTask();
    };

    /// <summary>
    /// Jellyfin 12.4's pipeline for Jellyfin Web, as its Startup builds it, with the plugin's filter
    /// in front as Jellyfin's host puts it: everything under the base URL, compression, then the
    /// web folder's default page and files, the page marked no-cache.
    /// </summary>
    private static RequestDelegate Jellyfin(string webPath, string baseUrl = "")
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddResponseCompression()
            .AddSingleton(Substitute.For<IWebHostEnvironment>())
            .BuildServiceProvider();
        var app = new ApplicationBuilder(services);

        new AutoSignInStartupFilter().Configure(host => host.Map(baseUrl, main =>
        {
            main.UseResponseCompression();
            main.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new PhysicalFileProvider(webPath), RequestPath = "/web" });
            main.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(webPath),
                RequestPath = "/web",
                ContentTypeProvider = new FileExtensionContentTypeProvider(),
                OnPrepareResponse = context =>
                {
                    if (Path.GetFileName(context.File.Name).Equals("index.html", StringComparison.Ordinal))
                    {
                        context.Context.Response.Headers.CacheControl = new StringValues("no-cache");
                    }
                }
            });
        }))(app);

        return app.Build();
    }

    /// <summary>A web folder holding Jellyfin Web's page, removed with the test.</summary>
    private sealed class WebFolder : IDisposable
    {
        public WebFolder()
        {
            Path = Directory.CreateTempSubdirectory("sso-auth-web-").FullName;
            File.WriteAllText(System.IO.Path.Join(Path, "index.html"), JellyfinWebPage);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
