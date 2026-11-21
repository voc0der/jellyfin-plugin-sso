using System.Security.Cryptography;
using System.Xml;
using Duende.IdentityModel.OidcClient;
using Jellyfin.Plugin.SsoAuthentication.Api;
using Jellyfin.Plugin.SsoAuthentication.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Jellyfin.Plugin.SsoAuthentication.Tests;

/// <summary>
/// Mutation fuzzing and authorization probes against the real parser and controller.
/// These are in-process checks; Jellyfin services are substituted by ControllerHarness.
/// </summary>
[Trait("Category", "Fuzz")]
public sealed class SecurityFuzzTests(ITestOutputHelper output)
{
    private static readonly DateTime Expiry = new(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Untrusted_saml_inputs_cannot_validate()
    {
        using var trusted = new SamlIdentityProvider();
        using var attacker = new SamlIdentityProvider("CN=Untrusted fuzz signer");
        var unsigned = SamlIdentityProvider.Unsigned("attacker", ["jellyfin-admins"], Expiry);
        var untrusted = attacker.Sign(unsigned);
        var response = new Response(trusted.CertificateBase64);

        await new FuzzCampaign(output).Run("saml-untrusted", (random, iteration) =>
        {
            if (iteration < FuzzInputs.Corpus.Length)
            {
                return iteration % 2 == 0 ? FuzzInputs.Corpus[iteration] : SamlIdentityProvider.Base64(FuzzInputs.Corpus[iteration]);
            }

            return (iteration % 4) switch
            {
                0 => FuzzInputs.Text(random, iteration),
                1 => SamlIdentityProvider.Base64(FuzzInputs.Mutate(random, unsigned)),
                2 => SamlIdentityProvider.Base64(FuzzInputs.Mutate(random, untrusted)),
                _ => SamlIdentityProvider.Base64(SamlIdentityProvider.Unsigned(FuzzInputs.XmlText(random), [FuzzInputs.XmlText(random)], Expiry)),
            };
        }, input =>
        {
            Assert.False(Valid(response, input), "An input without a trusted signature was accepted.");
            return Task.CompletedTask;
        }, trusted.CertificateBase64);
    }

    [Theory]
    [InlineData("_response")]
    [InlineData("_assertion")]
    public async Task Mutating_signed_saml_cannot_change_the_authenticated_identity_or_roles(string signedId)
    {
        using var idp = new SamlIdentityProvider();
        var signed = idp.Sign(SamlIdentityProvider.Unsigned("alice", ["jellyfin-users"], Expiry), signedId);
        var response = new Response(idp.CertificateBase64);
        Assert.True(Valid(response, SamlIdentityProvider.Base64(signed)), "The valid control must be accepted.");

        await new FuzzCampaign(output).Run("saml-signed" + signedId, (random, iteration) =>
            SamlIdentityProvider.Base64(iteration % 2 == 0
                ? Tamper(signed, random, iteration / 2)
                : FuzzInputs.Mutate(random, signed)), input =>
        {
            if (Valid(response, input))
            {
                // Whitespace and changes outside a signed assertion can be legitimate.
                // Any accepted mutation must still describe the original identity and roles.
                Assert.Equal("alice", response.GetNameID());
                Assert.Equal(new[] { "jellyfin-users" }, response.GetCustomAttributes("Role"));
            }

            return Task.CompletedTask;
        }, idp.CertificateBase64);
    }

    [Fact]
    public async Task Tampered_saml_cannot_sign_in_or_link_an_account()
    {
        using var scope = new PluginScope();
        using var idp = new SamlIdentityProvider();
        var harness = new ControllerHarness();
        var user = TestUsers.Create("alice");
        harness.SignedInAs(user);
        var config = new SamlConfig { Enabled = true, SamlCertificate = idp.CertificateBase64, Roles = ["jellyfin-users"] };
        scope.Plugin.Configuration.SamlConfigs["test"] = config;
        var controller = harness.Create();
        var signed = idp.Sign(SamlIdentityProvider.Unsigned("alice", ["jellyfin-users"], Expiry));

        await new FuzzCampaign(output).Run("saml-endpoints", (random, iteration) =>
            SamlIdentityProvider.Base64(Tamper(signed, random, iteration)), async input =>
        {
            controller.Request.Form = new FormCollection(new Dictionary<string, StringValues> { ["SAMLResponse"] = input });
            Refused(controller.SamlPost("test"));
            Refused(await controller.SamlAuth("test", Browser(input)));
            Refused(await controller.AddCanonicalLink("saml", "test", user.Id, Browser(input)));
            Assert.Empty(config.CanonicalLinks);
            Assert.Empty(harness.Users.ReceivedCalls());
            Assert.Empty(harness.Sessions.ReceivedCalls());
            harness.Log.Entries.Clear();
        }, idp.CertificateBase64);
    }

    [Fact]
    public async Task Forged_and_unvalidated_openid_states_cannot_sign_in_or_link()
    {
        using var scope = new PluginScope();
        var harness = new ControllerHarness();
        var user = TestUsers.Create("alice");
        harness.SignedInAs(user);
        var config = new OidConfig { Enabled = true };
        scope.Plugin.Configuration.OidConfigs["test"] = config;
        var controller = harness.Create();
        var states = Assert.IsAssignableFrom<IDictionary<string, TimedAuthorizeState>>(Assert.IsType<OkObjectResult>(controller.OidStates()).Value);
        states.Clear();
        try
        {
            await new FuzzCampaign(output).Run("openid-state", FuzzInputs.Text, async input =>
            {
                Assert.IsType<BadRequestObjectResult>(await controller.OidPost("test", input));
                Refused(await controller.OidAuth("test", Browser(input)));
                Refused(await controller.AddCanonicalLink("oid", "test", user.Id, Browser(input)));

                // Knowing an issued state must not bypass the provider's validation.
                var pending = new TimedAuthorizeState(new AuthorizeState { State = input }, DateTime.UtcNow) { Username = "admin" };
                states.Add("pending", pending);
                Refused(await controller.OidAuth("test", Browser(input)));
                Refused(await controller.AddCanonicalLink("oid", "test", user.Id, Browser(input)));
                Assert.False(pending.Valid);
                Assert.Single(states);
                states.Clear();

                Assert.Empty(config.CanonicalLinks);
                Assert.Empty(harness.Users.ReceivedCalls());
                Assert.Empty(harness.Sessions.ReceivedCalls());
                Assert.Empty(harness.HttpClients.ReceivedCalls());
            });
        }
        finally
        {
            states.Clear();
        }
    }

    [Theory]
    [InlineData("saml")]
    [InlineData("oid")]
    public async Task Fuzzed_link_requests_cannot_modify_another_users_account(string mode)
    {
        using var scope = new PluginScope();
        var harness = new ControllerHarness();
        var alice = TestUsers.Create("alice", new Guid("11111111-1111-1111-1111-111111111111"));
        var bob = TestUsers.Create("bob", new Guid("22222222-2222-2222-2222-222222222222"));
        harness.SignedInAs(alice);
        scope.Plugin.Configuration.SamlConfigs["test"] = new SamlConfig { CanonicalLinks = new() { ["bob"] = bob.Id } };
        scope.Plugin.Configuration.OidConfigs["test"] = new OidConfig { CanonicalLinks = new() { ["bob"] = bob.Id } };
        var controller = harness.Create();

        await new FuzzCampaign(output).Run(mode + "-link-ownership", FuzzInputs.Text, async input =>
        {
            Assert.Equal(StatusCodes.Status403Forbidden, Status(await controller.AddCanonicalLink(mode, "test", bob.Id, Browser(input))));
            Assert.Equal(StatusCodes.Status403Forbidden, Status(await controller.DeleteCanonicalLink(mode, "test", bob.Id, input)));
            var samlLinks = scope.Plugin.Configuration.SamlConfigs["test"].CanonicalLinks;
            var oidLinks = scope.Plugin.Configuration.OidConfigs["test"].CanonicalLinks;
            Assert.Equal(bob.Id, Assert.Single(samlLinks).Value);
            Assert.Equal(bob.Id, Assert.Single(oidLinks).Value);
            Assert.Empty(harness.Users.ReceivedCalls());
            Assert.Empty(harness.Sessions.ReceivedCalls());
        });
    }

    private static AuthResponse Browser(string data) => new() { Data = data, DeviceID = "fuzz", DeviceName = "fuzz", AppName = "fuzz", AppVersion = "1" };

    /// <summary>The status code a result answers with; a result without one answers 200.</summary>
    private static int Status(IActionResult result)
        => Assert.IsAssignableFrom<IStatusCodeActionResult>(result).StatusCode ?? StatusCodes.Status200OK;

    private static void Refused(IActionResult result)
        => Assert.InRange(Status(result), 400, 599);

    private static bool Valid(Response response, string input)
    {
        try
        {
            response.LoadXmlFromBase64(input);
            return response.IsValid();
        }
        catch (Exception error) when (error is FormatException or XmlException or CryptographicException)
        {
            // These are the parser's documented rejection mechanisms. Other exceptions,
            // crashes, hangs and successful authentication with changed claims fail the run.
            return false;
        }
    }

    private static string Tamper(string signed, Random random, int iteration)
    {
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        document.LoadXml(signed);
        var assertion = (XmlElement)document.GetElementsByTagName("Assertion", SamlIdentityProvider.Assertion)[0]!;
        switch (iteration % 4)
        {
            case 0:
                document.GetElementsByTagName("NameID", SamlIdentityProvider.Assertion)[0]!.InnerText = FuzzInputs.XmlText(random);
                break;
            case 1:
                document.GetElementsByTagName("AttributeValue", SamlIdentityProvider.Assertion)[0]!.InnerText = "jellyfin-admins-" + FuzzInputs.XmlText(random);
                break;
            case 2:
                ((XmlElement)document.GetElementsByTagName("SubjectConfirmationData", SamlIdentityProvider.Assertion)[0]!).SetAttribute("NotOnOrAfter", "2000-01-01T00:00:00Z");
                break;
            default:
                var forged = (XmlElement)assertion.CloneNode(true);
                forged.SetAttribute("ID", "_forged-" + random.Next());
                foreach (XmlNode signature in forged.GetElementsByTagName("Signature", "http://www.w3.org/2000/09/xmldsig#").Cast<XmlNode>().ToArray())
                {
                    signature.ParentNode!.RemoveChild(signature);
                }

                forged.GetElementsByTagName("NameID", SamlIdentityProvider.Assertion)[0]!.InnerText = FuzzInputs.XmlText(random);
                assertion.ParentNode!.InsertBefore(forged, assertion);
                break;
        }

        return document.OuterXml;
    }
}
