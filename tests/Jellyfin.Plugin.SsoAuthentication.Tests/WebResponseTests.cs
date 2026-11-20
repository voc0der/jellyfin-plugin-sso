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
        Assert.Contains("if (false) await link(request, account);", page, StringComparison.Ordinal);
    }

    [Fact]
    public void A_linking_page_links_before_signing_in()
    {
        var page = WebResponse.Generator("data", "keycloak", "https://jellyfin.example.test", "SAML", isLinking: true);

        Assert.Contains("if (true) await link(request, account);", page, StringComparison.Ordinal);
        Assert.Contains("'https://jellyfin.example.test/sso/SAML/Link/keycloak/'", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Linking_reads_the_signed_in_account_before_the_stored_credentials_are_reset()
    {
        // The page clears jellyfin_credentials so Jellyfin Web rebuilds them; read afterwards,
        // there is no account left to link to, and linking silently does nothing.
        var page = WebResponse.Generator("data", "keycloak", "https://jellyfin.example.test", "SAML", isLinking: true);
        var main = page[page.IndexOf("async function main()", StringComparison.Ordinal)..];

        var read = main.IndexOf("signedInAccount()", StringComparison.Ordinal);
        var reset = main.IndexOf("localStorage.removeItem('jellyfin_credentials')", StringComparison.Ordinal);
        Assert.True(read >= 0 && reset > read, "the account must be read before the reset");
    }

    [Fact]
    public void Linking_authenticates_with_the_header_Jellyfin_12_accepts()
    {
        // Jellyfin 12 refuses X-Emby-Authorization unless legacy authorization is switched on.
        var page = WebResponse.Generator("data", "keycloak", "https://jellyfin.example.test", "SAML", isLinking: true);

        Assert.Contains("xhr.setRequestHeader(\n           'Authorization',", page.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("'X-Emby-Authorization'", page, StringComparison.Ordinal);
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
