using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Plugin.SsoAuthentication.Tests;

/// <summary>
/// Discovery, token and userinfo responses for the real Duende client, served entirely in memory.
/// No external provider, listening port or replacement token validator is needed.
/// </summary>
internal sealed class OidIdentityProvider : HttpMessageHandler
{
    public const string Authority = "https://auth.example.test";
    private readonly RSA _key = RSA.Create(2048);
    private readonly Dictionary<string, Dictionary<string, string>> _codes = new();

    public Dictionary<string, object> Claims { get; } = new()
    {
        ["sub"] = "subject-alice",
        ["preferred_username"] = "alice",
        ["groups"] = new[] { "members", "admins", "kids", "tv", "tv-admins" },
    };

    public List<(string Path, string UserAgent)> Requests { get; } = new();

    public Dictionary<string, string>? TokenRequest { get; private set; }

    public bool OfferPushedAuthorization { get; set; }

    public bool RejectPushedAuthorization { get; set; }

    public bool RejectToken { get; set; }

    public bool RejectUserInfo { get; set; }

    public string? UserInfoSubject { get; set; }

    public Dictionary<string, string> Authorize(string startUrl)
        => QueryHelpers.ParseQuery(new Uri(startUrl).Query).ToDictionary(pair => pair.Key, pair => pair.Value.ToString());

    public string Callback(Dictionary<string, string> authorization)
    {
        var code = Guid.NewGuid().ToString("N");
        _codes.Add(code, authorization);
        return QueryHelpers.AddQueryString(string.Empty, new Dictionary<string, string?>
        {
            ["code"] = code,
            ["state"] = authorization["state"],
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add((path, request.Headers.UserAgent.ToString()));
        switch (path)
        {
            case "/.well-known/openid-configuration":
                var discovery = new Dictionary<string, object>
                {
                    ["issuer"] = Authority,
                    ["authorization_endpoint"] = Authority + "/authorize",
                    ["token_endpoint"] = Authority + "/token",
                    ["userinfo_endpoint"] = Authority + "/userinfo",
                    ["jwks_uri"] = Authority + "/keys",
                    ["response_types_supported"] = new[] { "code" },
                    ["subject_types_supported"] = new[] { "public" },
                    ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                    ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_post", "client_secret_basic" },
                    ["code_challenge_methods_supported"] = new[] { "S256" },
                };
                if (OfferPushedAuthorization)
                {
                    discovery["pushed_authorization_request_endpoint"] = Authority + "/par";
                }

                return Json(discovery);
            case "/keys":
                var key = _key.ExportParameters(false);
                return Json(new { keys = new[] { new { kty = "RSA", use = "sig", kid = "test-key", alg = "RS256", n = Base64Url(key.Modulus!), e = Base64Url(key.Exponent!) } } });
            case "/par":
                return RejectPushedAuthorization
                    ? Json(new { error = "invalid_request", error_description = "Pushed request rejected" }, HttpStatusCode.BadRequest)
                    : Json(new { request_uri = "urn:example:request", expires_in = 60 }, HttpStatusCode.Created);
            case "/token":
                Assert.Equal(HttpMethod.Post, request.Method);
                TokenRequest = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken))
                    .ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
                Assert.Equal("authorization_code", TokenRequest["grant_type"]);
                Assert.Equal("jellyfin", TokenRequest["client_id"]);
                Assert.Equal("test-secret", TokenRequest["client_secret"]);
                Assert.True(_codes.Remove(TokenRequest["code"], out var authorization), "An authorization code must be issued and used only once.");
                Assert.Equal(authorization["redirect_uri"], TokenRequest["redirect_uri"]);
                Assert.Equal("S256", authorization["code_challenge_method"]);
                Assert.Equal(authorization["code_challenge"], Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(TokenRequest["code_verifier"]))));
                if (RejectToken)
                {
                    return Json(new { error = "invalid_grant", error_description = "Code rejected" }, HttpStatusCode.BadRequest);
                }

                var payload = new Dictionary<string, object>(Claims)
                {
                    ["iss"] = Authority,
                    ["aud"] = "jellyfin",
                    ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
                };
                if (authorization.TryGetValue("nonce", out var nonce))
                {
                    payload["nonce"] = nonce;
                }

                var unsigned = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "test-key", typ = "JWT" }))
                    + "." + Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
                var token = unsigned + "." + Base64Url(_key.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                return Json(new { access_token = "test-access-token", token_type = "Bearer", expires_in = 300, id_token = token });
            case "/userinfo":
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("test-access-token", request.Headers.Authorization?.Parameter);
                return RejectUserInfo
                    ? Json(new { error = "invalid_token" }, HttpStatusCode.Unauthorized)
                    : Json(new Dictionary<string, object>(Claims) { ["sub"] = UserInfoSubject ?? (string)Claims["sub"] });
            default:
                throw new InvalidOperationException("Unexpected provider request: " + request.RequestUri);
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _key.Dispose();
        }

        base.Dispose(disposing);
    }
}
