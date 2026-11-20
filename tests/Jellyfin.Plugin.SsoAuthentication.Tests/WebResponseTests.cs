namespace Jellyfin.Plugin.SsoAuthentication.Tests;

/// <summary>
/// The page served after the provider sends the browser back. It signs Jellyfin Web in
/// with the session the plugin hands it, so the addresses it calls have to be right.
/// </summary>
public sealed class WebResponseTests
{
    [Fact]
    public void The_page_completes_the_sign_in_against_the_providers_endpoint()
    {
        var page = WebResponse.Generator("state-123", "authelia", "https://jellyfin.example.test", "OID");

        Assert.Contains("var data = 'state-123';", page, StringComparison.Ordinal);
        Assert.Contains("'https://jellyfin.example.test/sso/OID/Auth/authelia'", page, StringComparison.Ordinal);
        Assert.Contains("'https://jellyfin.example.test/web/index.html'", page, StringComparison.Ordinal);
        Assert.Contains("if (false) await link(request);", page, StringComparison.Ordinal);
    }

    [Fact]
    public void A_linking_page_links_before_signing_in()
    {
        var page = WebResponse.Generator("data", "keycloak", "https://jellyfin.example.test", "SAML", isLinking: true);

        Assert.Contains("if (true) await link(request);", page, StringComparison.Ordinal);
        Assert.Contains("'https://jellyfin.example.test/sso/SAML/Link/keycloak/'", page, StringComparison.Ordinal);
    }

    [Fact]
    public void A_server_under_a_path_keeps_it()
    {
        var page = WebResponse.Generator("data", "authelia", "https://example.test/jellyfin", "OID");

        Assert.Contains("'https://example.test/jellyfin/sso/OID/Auth/authelia'", page, StringComparison.Ordinal);
        Assert.Contains("'https://example.test/jellyfin/web/index.html'", page, StringComparison.Ordinal);
    }

    [Fact]
    public void An_international_domain_is_written_as_punycode()
    {
        var page = WebResponse.Generator("data", "authelia", "https://jellyfin.bücher.example", "OID");

        Assert.Contains("https://jellyfin.xn--bcher-kva.example/sso/OID/Auth/authelia", page, StringComparison.Ordinal);
        Assert.DoesNotContain("bücher", page, StringComparison.Ordinal);
    }
}
