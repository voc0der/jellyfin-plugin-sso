using System.Net;
using System.Text;
using Duende.IdentityModel.OidcClient;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SsoAuthentication.Api;
using Jellyfin.Plugin.SsoAuthentication.Config;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Jellyfin.Plugin.SsoAuthentication.Tests;

/// <summary>
/// The picture an OpenID provider's claims point at, which sign-in makes the account's profile
/// picture. It is a nicety: nothing about it may turn someone away at the door.
/// </summary>
public sealed class ProfilePictureTests : IDisposable
{
    private const string PictureUrl = "https://auth.example.test/pictures/alice";

    // A PNG's signature. The plugin never decodes the picture, only stores what it was sent.
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private readonly PluginScope _scope = new();
    private readonly ControllerHarness _harness = new();
    private readonly PictureServer _server = new();
    private readonly List<string> _states = new();
    private readonly List<(string MimeType, string Path, byte[] Bytes)> _stored = new();
    private readonly List<string?> _savedPictures = new();
    private User? _account;

    public ProfilePictureTests()
    {
        _scope.Plugin.Configuration.OidConfigs["authelia"] = new OidConfig
        {
            OidEndpoint = "https://auth.example.test",
            OidClientId = "jellyfin",
            OidSecret = "secret",
            Enabled = true,
            OidScopes = Array.Empty<string>(),
            Roles = Array.Empty<string>(),
        };

        _harness.Users.CreateUserAsync(Arg.Any<string>()).Returns(call =>
        {
            _account = TestUsers.Create(call.Arg<string>(), Guid.NewGuid());
            _harness.Users.GetUserById(_account.Id).Returns(_account);
            return Task.FromResult(_account);
        });
        _harness.Users.UpdateUserAsync(Arg.Do<User>(user => _savedPictures.Add(user.ProfileImage?.Path)));
        _harness.Sessions.AuthenticateDirect(Arg.Any<AuthenticationRequest>())
            .Returns(new AuthenticationResult { AccessToken = "session-token" });
        _harness.ServerConfiguration.ApplicationPaths.UserConfigurationDirectoryPath.Returns(UsersPath);
        _harness.HttpClients.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_server, disposeHandler: false));
        _harness.Providers
            .When(providers => providers.SaveImage(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>()))
            .Do(call =>
            {
                using var copy = new MemoryStream();
                call.ArgAt<Stream>(0).CopyTo(copy);
                _stored.Add((call.ArgAt<string>(1), call.ArgAt<string>(2), copy.ToArray()));
            });
    }

    public void Dispose()
    {
        // Sign-ins in progress live in the controller, shared by every test: leave none behind.
        var states = States();
        foreach (var state in _states)
        {
            states.Remove(state);
        }

        _scope.Dispose();
        _server.Dispose();
    }

    private string UsersPath => Path.Join(_scope.Root, "config", "users");

    private IDictionary<string, TimedAuthorizeState> States()
        => Assert.IsAssignableFrom<IDictionary<string, TimedAuthorizeState>>(Assert.IsType<OkObjectResult>(_harness.Create().OidStates()).Value);

    /// <summary>
    /// Signs alice in as the plugin's page does once the provider has sent her back, with the
    /// picture her claims gave.
    /// </summary>
    private async Task SignIn(string? pictureUrl = PictureUrl)
    {
        var state = "state-" + Guid.NewGuid().ToString("N");
        _states.Add(state);
        States()[state] = new TimedAuthorizeState(new AuthorizeState { State = state }, DateTime.Now)
        {
            Valid = true,
            Username = "alice",
            Folders = new List<string>(),
            AvatarURL = pictureUrl,
        };

        var result = await _harness.Create().OidAuth("authelia", new AuthResponse { Data = state, DeviceID = "d", DeviceName = "n", AppName = "a", AppVersion = "1" });

        var session = Assert.IsType<AuthenticationResult>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("session-token", session.AccessToken);
    }

    private static HttpResponseMessage Picture(string contentType, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new ByteArrayContent(Png);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(status) { Content = content };
    }

    private void AssertNotSetButLogged()
    {
        Assert.Contains(_harness.Log.Entries, entry => entry.Level == LogLevel.Error && entry.Message == "Could not set the profile picture of alice");
    }

    [Theory]
    [InlineData("image/png", "profile.png")]
    [InlineData("image/jpeg", "profile.jpg")]
    public async Task The_picture_the_claims_point_at_becomes_the_profile_picture(string contentType, string fileName)
    {
        _server.Respond = _ => Picture(contentType);

        await SignIn();

        // Named the way Jellyfin names a picture uploaded to it.
        var expected = Path.Join(UsersPath, "alice", fileName);
        var stored = Assert.Single(_stored);
        Assert.Equal((contentType, expected), (stored.MimeType, stored.Path));
        Assert.Equal(Png, stored.Bytes);
        Assert.Equal(new Uri(PictureUrl), Assert.Single(_server.Requests));

        // Saved with the account: on Jellyfin 12, a change to an account lasts only once saved.
        Assert.Equal(expected, _savedPictures[^1]);
    }

    [Fact]
    public async Task A_sign_in_whose_claims_give_no_picture_fetches_nothing()
    {
        await SignIn(pictureUrl: null);

        Assert.Empty(_server.Requests);
        Assert.Empty(_stored);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "text/html")]
    [InlineData(HttpStatusCode.NotFound, "image/png")]
    [InlineData(HttpStatusCode.OK, "application/octet-stream")]
    public async Task An_answer_that_is_not_a_picture_leaves_the_one_the_account_has(HttpStatusCode status, string contentType)
    {
        _server.Respond = _ => Picture("image/png");
        await SignIn();
        var before = _account!.ProfileImage!.Path;

        // Say the provider now sends its sign-in page, or an error, where the picture was.
        _server.Respond = _ => Picture(contentType, status);
        await SignIn();

        Assert.Equal(before, _account.ProfileImage!.Path);
        Assert.Single(_stored);
        await _harness.Users.DidNotReceive().ClearProfileImageAsync(Arg.Any<User>());
        Assert.Contains(_harness.Log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("not as an image", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_provider_that_cannot_be_reached_for_the_picture_still_lets_the_person_in()
    {
        _server.Respond = _ => throw new HttpRequestException("Connection refused");

        await SignIn();

        Assert.Empty(_stored);
        Assert.Null(_account!.ProfileImage);
        AssertNotSetButLogged();
    }

    [Fact]
    public async Task A_picture_that_takes_too_long_still_lets_the_person_in()
    {
        _server.Respond = _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");

        await SignIn();

        Assert.Empty(_stored);
        AssertNotSetButLogged();
    }

    [Fact]
    public async Task A_picture_the_disk_refuses_leaves_the_one_the_account_has()
    {
        _server.Respond = _ => Picture("image/png");
        await SignIn();
        var before = _account!.ProfileImage!.Path;

        _server.Respond = _ => Picture("image/jpeg");
        _harness.Providers.SaveImage(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromException(new IOException("No space left on device")));
        await SignIn();

        // Not pointed at a file that was never written.
        Assert.Equal(before, _account.ProfileImage!.Path);
        Assert.Equal(before, _savedPictures[^1]);
        await _harness.Users.DidNotReceive().ClearProfileImageAsync(Arg.Any<User>());
        AssertNotSetButLogged();
    }

    [Fact]
    public async Task Any_failure_at_all_with_the_picture_still_lets_the_person_in()
    {
        _server.Respond = _ => Picture("image/png");
        await SignIn();

        // Not one of the ways a download fails: Jellyfin's database raises its own exceptions.
        _harness.Users.ClearProfileImageAsync(Arg.Any<User>())
            .Returns(Task.FromException(new Exception("database is locked")));
        await SignIn();

        AssertNotSetButLogged();
    }

    /// <summary>Answers the plugin's request for the picture as the test says to.</summary>
    private sealed class PictureServer : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(Respond(request));
        }
    }
}
