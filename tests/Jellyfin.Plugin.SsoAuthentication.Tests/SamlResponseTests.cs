using System.Xml;

namespace Jellyfin.Plugin.SsoAuthentication.Tests;

/// <summary>
/// A SAML response is the only thing standing between a POST and a Jellyfin session, so
/// each way of forging or reusing one has to be refused. These pin the checks 4.0.0.4
/// added: a signature from the configured certificate, over the whole response or its
/// assertion, and an assertion that has not expired.
/// </summary>
public sealed class SamlResponseTests : IDisposable
{
    private readonly SamlIdentityProvider _idp = new();

    public void Dispose() => _idp.Dispose();

    private Response Load(string xml, SamlIdentityProvider? trusted = null)
        => new((trusted ?? _idp).CertificateBase64, SamlIdentityProvider.Base64(xml));

    [Fact]
    public void A_response_signed_as_a_whole_is_valid()
    {
        var response = Load(_idp.Sign(SamlIdentityProvider.Unsigned("alice", new[] { "jellyfin-users" })));

        Assert.True(response.IsValid());
        Assert.Equal("alice", response.GetNameID());
        Assert.Equal(new[] { "jellyfin-users" }, response.GetCustomAttributes("Role"));
    }

    [Fact]
    public void A_response_with_only_its_assertion_signed_is_valid()
    {
        var response = Load(_idp.Sign(SamlIdentityProvider.Unsigned("alice"), "_assertion"));

        Assert.True(response.IsValid());
    }

    [Fact]
    public void Every_role_value_is_read()
    {
        var response = Load(_idp.Sign(SamlIdentityProvider.Unsigned("alice", new[] { "jellyfin-users", "jellyfin-admins", "kids" })));

        Assert.Equal(new[] { "jellyfin-users", "jellyfin-admins", "kids" }, response.GetCustomAttributes("Role"));
        Assert.Equal("jellyfin-users", response.GetCustomAttribute("Role"));
        Assert.Null(response.GetCustomAttribute("missing"));
    }

    [Fact]
    public void An_unsigned_response_is_refused()
    {
        Assert.False(Load(SamlIdentityProvider.Unsigned("alice")).IsValid());
    }

    [Fact]
    public void A_response_signed_by_another_certificate_is_refused()
    {
        using var other = new SamlIdentityProvider("CN=Someone else");

        var response = Load(other.Sign(SamlIdentityProvider.Unsigned("alice")), trusted: _idp);

        Assert.False(response.IsValid());
    }

    [Fact]
    public void A_name_changed_after_signing_is_refused()
    {
        var signed = _idp.Sign(SamlIdentityProvider.Unsigned("alice"));

        Assert.False(Load(signed.Replace(">alice<", ">admin<", StringComparison.Ordinal)).IsValid());
    }

    [Fact]
    public void A_role_added_after_signing_is_refused()
    {
        var signed = _idp.Sign(SamlIdentityProvider.Unsigned("alice", new[] { "jellyfin-users" }));
        var forged = signed.Replace(
            "<saml:AttributeValue>jellyfin-users</saml:AttributeValue>",
            "<saml:AttributeValue>jellyfin-users</saml:AttributeValue><saml:AttributeValue>jellyfin-admins</saml:AttributeValue>",
            StringComparison.Ordinal);

        Assert.False(Load(forged).IsValid());
    }

    [Fact]
    public void A_forged_assertion_placed_ahead_of_a_signed_one_is_refused()
    {
        // Signature wrapping: the signed assertion is kept, so its signature still checks
        // out, but an unsigned one naming someone else goes first, where the name is read.
        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(_idp.Sign(SamlIdentityProvider.Unsigned("alice"), "_assertion"));
        var manager = new XmlNamespaceManager(document.NameTable);
        manager.AddNamespace("saml", SamlIdentityProvider.Assertion);
        var signed = (XmlElement)document.SelectSingleNode("//saml:Assertion", manager)!;
        var forged = (XmlElement)signed.CloneNode(deep: true);
        forged.SetAttribute("ID", "_forged");
        forged.RemoveChild(forged.SelectSingleNode("*[local-name()='Signature']")!);
        forged.SelectSingleNode("saml:Subject/saml:NameID", manager)!.InnerText = "admin";
        signed.ParentNode!.InsertBefore(forged, signed);

        var response = Load(document.OuterXml);

        Assert.Equal("admin", response.GetNameID());
        Assert.False(response.IsValid());
    }

    [Fact]
    public void A_signature_over_some_other_element_is_refused()
    {
        // The signature must cover the response or its assertion; one over the Status
        // element alone says nothing about who signed in.
        var xml = SamlIdentityProvider.Unsigned("alice").Replace("<samlp:Status>", "<samlp:Status ID=\"_status\">", StringComparison.Ordinal);
        var signed = _idp.Sign(xml.Replace("<samlp:StatusCode", "<saml:Issuer>https://idp.example.test</saml:Issuer><samlp:StatusCode", StringComparison.Ordinal), "_status");

        Assert.False(Load(signed).IsValid());
    }

    [Fact]
    public void An_expired_assertion_is_refused()
    {
        var response = Load(_idp.Sign(SamlIdentityProvider.Unsigned("alice", notOnOrAfter: DateTime.UtcNow.AddMinutes(-1))));

        Assert.False(response.IsValid());
    }

    [Fact]
    public void An_external_entity_is_never_resolved()
    {
        // XXE: an external entity would read a file off the Jellyfin server into the name.
        // The document has no resolver, so the entity stays an empty reference.
        var passwd = Path.Combine(Path.GetTempPath(), "sso-auth-xxe-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(passwd, "root:x:0:0:root:/root:/bin/sh");
        try
        {
            var xml = $"<!DOCTYPE r [<!ENTITY x SYSTEM \"{new Uri(passwd).AbsoluteUri}\">]>"
                + SamlIdentityProvider.Unsigned("&x;").Replace("&amp;x;", "&x;", StringComparison.Ordinal);

            var response = Load(xml);

            Assert.DoesNotContain("root:", response.GetNameID(), StringComparison.Ordinal);
            Assert.DoesNotContain("root:", response.Xml, StringComparison.Ordinal);
            Assert.False(response.IsValid());
        }
        finally
        {
            File.Delete(passwd);
        }
    }
}
