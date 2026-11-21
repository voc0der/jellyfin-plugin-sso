using System.IO.Compression;
using System.Text;
using System.Web;
using System.Xml;

namespace Jellyfin.Plugin.SsoAuthentication.Tests;

/// <summary>
/// The SAML request is what an identity provider sees: an HTTP-Redirect binding
/// AuthnRequest, deflated and Base64-encoded into the query string.
/// </summary>
public sealed class AuthRequestTests
{
    [Fact]
    public void An_unsupported_request_format_returns_no_payload()
    {
        Assert.Null(new AuthRequest("client", "https://jf.example.test/acs").GetRequest((AuthRequest.AuthRequestFormat)0));
    }

    private static XmlDocument Decode(string redirectUrl)
    {
        var query = HttpUtility.ParseQueryString(new Uri(redirectUrl).Query);
        var deflated = Convert.FromBase64String(query["SAMLRequest"]!);
        using var inflater = new DeflateStream(new MemoryStream(deflated), CompressionMode.Decompress);
        using var reader = new StreamReader(inflater, Encoding.UTF8);
        var document = new XmlDocument();
        document.LoadXml(reader.ReadToEnd());
        return document;
    }

    [Fact]
    public void The_request_names_the_client_and_where_to_post_the_answer()
    {
        var url = new AuthRequest("jellyfin-saml", "https://jellyfin.example.test/sso/SAML/post/keycloak")
            .GetRedirectUrl("https://keycloak.example.test/realms/media/protocol/saml");

        var request = Decode(url).DocumentElement!;
        Assert.Equal("AuthnRequest", request.LocalName);
        Assert.Equal("urn:oasis:names:tc:SAML:2.0:protocol", request.NamespaceURI);
        Assert.Equal("https://jellyfin.example.test/sso/SAML/post/keycloak", request.GetAttribute("AssertionConsumerServiceURL"));
        Assert.Equal("urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST", request.GetAttribute("ProtocolBinding"));
        Assert.Equal("jellyfin-saml", request["Issuer", "urn:oasis:names:tc:SAML:2.0:assertion"]!.InnerText);
        Assert.StartsWith("_", request.GetAttribute("ID"), StringComparison.Ordinal);
    }

    [Fact]
    public void Each_request_has_its_own_id()
    {
        var endpoint = "https://idp.example.test/saml";
        var first = Decode(new AuthRequest("client", "https://jf.example.test/acs").GetRedirectUrl(endpoint));
        var second = Decode(new AuthRequest("client", "https://jf.example.test/acs").GetRedirectUrl(endpoint));

        Assert.NotEqual(first.DocumentElement!.GetAttribute("ID"), second.DocumentElement!.GetAttribute("ID"));
    }

    [Fact]
    public void An_endpoint_with_a_query_string_keeps_it()
    {
        var url = new AuthRequest("client", "https://jf.example.test/acs").GetRedirectUrl("https://idp.example.test/saml?tenant=media");

        Assert.StartsWith("https://idp.example.test/saml?tenant=media&SAMLRequest=", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Linking_passes_relay_state_through_the_provider()
    {
        var url = new AuthRequest("client", "https://jf.example.test/acs").GetRedirectUrl("https://idp.example.test/saml", "linking");

        Assert.Equal("linking", HttpUtility.ParseQueryString(new Uri(url).Query)["RelayState"]);
    }

    [Fact]
    public void A_sign_in_sends_no_relay_state()
    {
        var url = new AuthRequest("client", "https://jf.example.test/acs").GetRedirectUrl("https://idp.example.test/saml");

        Assert.Null(HttpUtility.ParseQueryString(new Uri(url).Query)["RelayState"]);
    }
}
