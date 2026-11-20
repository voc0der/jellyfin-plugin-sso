using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SsoAuthentication.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Cryptography;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XmlSerializer = System.Xml.Serialization.XmlSerializer;

// SSOPlugin.Instance is static and every controller reads it, so tests cannot share a
// process with another test's plugin.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Jellyfin.Plugin.SsoAuthentication.Tests;

/// <summary>
/// Serializes the way Jellyfin's own IXmlSerializer does: System.Xml.Serialization,
/// one serializer per type. Plugin configuration is read and written through it, so
/// this is what decides whether an existing settings file still loads.
/// </summary>
internal sealed class TestXmlSerializer : IXmlSerializer
{
    public object? DeserializeFromStream(Type type, Stream stream) => new XmlSerializer(type).Deserialize(stream);

    public void SerializeToStream(object obj, Stream stream) => new XmlSerializer(obj.GetType()).Serialize(stream, obj);

    public void SerializeToFile(object obj, string file)
    {
        using var stream = File.Create(file);
        SerializeToStream(obj, stream);
    }

    public object? DeserializeFromFile(Type type, string file)
    {
        using var stream = File.OpenRead(file);
        return DeserializeFromStream(type, stream);
    }

    public object? DeserializeFromBytes(Type type, byte[] buffer)
    {
        using var stream = new MemoryStream(buffer, writable: false);
        return DeserializeFromStream(type, stream);
    }
}

/// <summary>
/// A constructed plugin over a throwaway data directory, installed as
/// <see cref="SSOPlugin.Instance"/> for as long as the scope lives.
/// </summary>
internal sealed class PluginScope : IDisposable
{
    /// <summary>
    /// Plugin settings live in plugins/configurations/&lt;assembly&gt;.xml.
    /// </summary>
    public const string ConfigurationFileName = "Jellyfin.Plugin.SsoAuthentication.xml";

    /// <param name="existingConfigurationXml">This plugin's settings file, as a server already has it.</param>
    /// <param name="legacyConfigurationXml">The settings file 9p4's plugin left behind, SSO-Auth.xml.</param>
    public PluginScope(string? existingConfigurationXml = null, string? legacyConfigurationXml = null)
    {
        Root = Directory.CreateTempSubdirectory("sso-auth-tests-").FullName;
        ConfigurationsPath = Path.Combine(Root, "plugins", "configurations");
        Directory.CreateDirectory(ConfigurationsPath);

        if (existingConfigurationXml is not null)
        {
            File.WriteAllText(ConfigurationFile, existingConfigurationXml);
        }

        if (legacyConfigurationXml is not null)
        {
            File.WriteAllText(LegacyConfigurationFile, legacyConfigurationXml);
        }

        var paths = Substitute.For<IApplicationPaths>();
        paths.ProgramDataPath.Returns(Root);
        paths.DataPath.Returns(Path.Combine(Root, "data"));
        paths.ConfigurationDirectoryPath.Returns(Path.Combine(Root, "config"));
        paths.PluginsPath.Returns(Path.Combine(Root, "plugins"));
        paths.PluginConfigurationsPath.Returns(ConfigurationsPath);
        paths.CachePath.Returns(Path.Combine(Root, "cache"));
        paths.TempDirectory.Returns(Path.Combine(Root, "temp"));

        Plugin = new SSOPlugin(paths, new TestXmlSerializer(), Logger);
    }

    public string Root { get; }

    public string ConfigurationsPath { get; }

    public SSOPlugin Plugin { get; }

    public RecordingLogger<SSOPlugin> Logger { get; } = new();

    public string ConfigurationFile => Path.Combine(ConfigurationsPath, ConfigurationFileName);

    public string LegacyConfigurationFile => Path.Combine(ConfigurationsPath, "SSO-Auth.xml");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is not a test failure.
        }
    }
}

/// <summary>
/// Hashes nothing: the controller only stores what it is given as the new account's
/// unusable password, so a recognisable placeholder is enough to check it was set.
/// </summary>
internal sealed class FakeCryptoProvider : ICryptoProvider
{
    public const string HashId = "PBKDF2-SHA512";

    public string DefaultHashMethod => HashId;

    public int Calls { get; private set; }

    public PasswordHash CreatePasswordHash(ReadOnlySpan<char> password)
    {
        Calls++;
        return new PasswordHash(HashId, SHA256.HashData(Encoding.UTF8.GetBytes(password.ToString())), new byte[16], new Dictionary<string, string>());
    }

    public bool Verify(PasswordHash hash, ReadOnlySpan<char> password) => false;

    public byte[] GenerateSalt() => new byte[16];

    public byte[] GenerateSalt(int length) => new byte[length];
}

internal static class TestUsers
{
    /// <summary>A user as Jellyfin's user manager creates one: default permissions and preferences.</summary>
    public static User Create(string name, Guid? id = null)
    {
        var user = new User(name, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider");
        if (id is not null)
        {
            user.Id = id.Value;
        }

        user.AddDefaultPermissions();
        user.AddDefaultPreferences();
        return user;
    }
}

/// <summary>
/// An identity provider's signing certificate and signed SAML responses, built the way a
/// real IdP signs them: an enveloped RSA-SHA256 signature over exclusive C14N.
/// </summary>
internal sealed class SamlIdentityProvider : IDisposable
{
    public const string Protocol = "urn:oasis:names:tc:SAML:2.0:protocol";
    public const string Assertion = "urn:oasis:names:tc:SAML:2.0:assertion";

    private readonly RSA _key;

    public SamlIdentityProvider(string subject = "CN=SSO test identity provider")
    {
        _key = RSA.Create(2048);
        var request = new CertificateRequest(subject, _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    public X509Certificate2 Certificate { get; }

    /// <summary>Gets the certificate as the plugin configuration stores it: Base64 DER.</summary>
    public string CertificateBase64 => Convert.ToBase64String(Certificate.Export(X509ContentType.Cert));

    public static string Unsigned(string nameId, IEnumerable<string>? roles = null, DateTime? notOnOrAfter = null)
    {
        var expiry = (notOnOrAfter ?? DateTime.UtcNow.AddMinutes(5)).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        var attributes = new StringBuilder();
        foreach (var role in roles ?? Array.Empty<string>())
        {
            attributes.Append("<saml:AttributeValue>").Append(System.Security.SecurityElement.Escape(role)).Append("</saml:AttributeValue>");
        }

        return $"""
            <samlp:Response xmlns:samlp="{Protocol}" xmlns:saml="{Assertion}" ID="_response" Version="2.0" IssueInstant="2026-11-20T12:00:00Z"><saml:Issuer>https://idp.example.test</saml:Issuer><samlp:Status><samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success"/></samlp:Status><saml:Assertion ID="_assertion" Version="2.0" IssueInstant="2026-11-20T12:00:00Z"><saml:Issuer>https://idp.example.test</saml:Issuer><saml:Subject><saml:NameID>{System.Security.SecurityElement.Escape(nameId)}</saml:NameID><saml:SubjectConfirmation Method="urn:oasis:names:tc:SAML:2.0:cm:bearer"><saml:SubjectConfirmationData NotOnOrAfter="{expiry}" Recipient="https://jellyfin.example.test/sso/SAML/post/test"/></saml:SubjectConfirmation></saml:Subject><saml:AttributeStatement><saml:Attribute Name="Role">{attributes}</saml:Attribute></saml:AttributeStatement></saml:Assertion></samlp:Response>
            """;
    }

    /// <summary>Signs the element with the given ID and returns the document's XML.</summary>
    public string Sign(string xml, string signedId = "_response")
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(xml);
        var signed = (XmlElement)document.SelectSingleNode($"//*[@ID='{signedId}']")!;

        var signedXml = new SignedXml(document) { SigningKey = _key };
        signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signedXml.SignedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;

        var reference = new Reference("#" + signedId) { DigestMethod = SignedXml.XmlDsigSHA256Url };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signedXml.AddReference(reference);

        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(Certificate));
        signedXml.KeyInfo = keyInfo;
        signedXml.ComputeSignature();

        // The schema puts the signature straight after the element's Issuer.
        var issuer = signed.SelectSingleNode("*[local-name()='Issuer']");
        signed.InsertAfter(document.ImportNode(signedXml.GetXml(), true), issuer);
        return document.OuterXml;
    }

    public static string Base64(string xml) => Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));

    public void Dispose()
    {
        Certificate.Dispose();
        _key.Dispose();
    }
}

/// <summary>
/// An SSOController with every Jellyfin service substituted, answering a request for
/// the given host the way Jellyfin's pipeline would hand it over.
/// </summary>
internal sealed class ControllerHarness
{
    public IUserManager Users { get; } = Substitute.For<IUserManager>();

    public ISessionManager Sessions { get; } = Substitute.For<ISessionManager>();

    public IAuthorizationContext Authorization { get; } = Substitute.For<IAuthorizationContext>();

    public FakeCryptoProvider Crypto { get; } = new();

    public IProviderManager Providers { get; } = Substitute.For<IProviderManager>();

    public IHttpClientFactory HttpClients { get; } = Substitute.For<IHttpClientFactory>();

    public IServerConfigurationManager ServerConfiguration { get; } = Substitute.For<IServerConfigurationManager>();

    public RecordingLogger<SSOController> Log { get; } = new();

    public SSOController Create(string scheme = "https", string host = "jellyfin.example.test", string pathBase = "", string path = "/sso/test")
    {
        var controller = new SSOController(
            Log,
            NullLoggerFactory.Instance,
            Sessions,
            Users,
            Authorization,
            Crypto,
            Providers,
            HttpClients,
            ServerConfiguration);

        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        context.Request.PathBase = pathBase;
        context.Request.Path = path;
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    /// <summary>Makes the request look like it came from this user's session.</summary>
    public void SignedInAs(User user)
    {
        Authorization.GetAuthorizationInfo(Arg.Any<HttpRequest>())
            .Returns(new AuthorizationInfo { User = user, IsAuthenticated = true, Token = "user-token" });
    }
}

/// <summary>
/// Keeps what was logged, for tests of what an administrator is told.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}
