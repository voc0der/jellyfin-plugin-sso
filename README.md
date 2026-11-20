<h1 align="center">Jellyfin SSO Plugin</h1>

<p align="center">

<img alt="Logo" src="https://raw.githubusercontent.com/voc0der/jellyfin-plugin-sso/main/icon.png"/>
<br/>
<br/>
<a href="https://github.com/voc0der/jellyfin-plugin-sso">
<img alt="GPL 3.0 License" src="https://img.shields.io/github/license/voc0der/jellyfin-plugin-sso.svg"/>
</a>
<a href="https://github.com/voc0der/jellyfin-plugin-sso/actions/workflows/release.yml">
<img alt="GitHub Actions Build Status" src="https://github.com/voc0der/jellyfin-plugin-sso/actions/workflows/release.yml/badge.svg"/>
</a>
<a href="https://github.com/voc0der/jellyfin-plugin-sso/releases">
<img alt="Current Release" src="https://img.shields.io/github/release/voc0der/jellyfin-plugin-sso.svg"/>
</a>
<a href="https://github.com/voc0der/jellyfin-plugin-sso/releases.atom">
<img alt="Release RSS Feed" src="https://img.shields.io/badge/rss-releases-ffa500?logo=rss" />
</a>
<a href="https://github.com/voc0der/jellyfin-plugin-sso/commits/main.atom">
<img alt="Main Commits RSS Feed" src="https://img.shields.io/badge/rss-commits-ffa500?logo=rss" />
</a>
</p>

Independently maintained fork of [9p4/jellyfin-plugin-sso](https://github.com/9p4/jellyfin-plugin-sso), preserving its source history and GPL-3.0 license.

Sign in to Jellyfin using an OpenID Connect or SAML provider. Existing users can link
or remove SSO accounts through `/SSOViews/linking`.

## Status and compatibility

This initial scaffold retains upstream authentication behavior and targets **Jellyfin
10.11 / .NET 9**. CI uses the .NET 10 SDK. Authentication changes and newer server
compatibility will be developed separately. The supported clients remain Jellyfin Web
and clients using [Quick Connect](https://jellyfin.org/docs/general/server/quick-connect).

The fork has no published release yet. `manifest.json` intentionally contains no
versions until the first release is built and verified.

## Tested Providers

[Find provider specific documentation in providers.md](providers.md)

- Authelia
- authentik
- Keycloak
  - OIDC & SAML
- Pocket ID
- Kanidm
- Google OpenID: Works, but usernames are all numeric

## Supported Protocols

- [OpenID](https://openid.net/developers/how-connect-works/)
- [SAML](https://www.cloudflare.com/learning/access-management/what-is-saml/)

## Security

See [SECURITY.md](SECURITY.md) for private vulnerability reporting and the automated
security checks used by this fork. Those checks do not replace an authentication audit.

## Installing

Once a release is published, add this repository URL under **Dashboard > Plugins > Repositories**:

```text
https://raw.githubusercontent.com/voc0der/jellyfin-plugin-sso/main/manifest.json
```

This plugin is also registered in the combined [plugin repository](https://github.com/voc0der/jellyfin-plugins).
Published releases will appear there automatically. Use only one catalogue source.

This fork retains upstream's plugin GUID. It replaces the original SSO plugin;
do not install both or keep both repositories enabled. Back up your SSO configuration
before switching. The current scaffold does not publish nightly builds.

PR builds are available from **Actions > PR Develop Build**. Local build and package
instructions are in [CONTRIBUTING.md](CONTRIBUTING.md).

## Roadmap

- [x] Admin page
- [x] Build, release tooling, and settings-page smoke tests
- [ ] Authentication integration tests
- [x] Add role/claims support
- [x] Use canonical usernames instead of preferred usernames
- [x] Add user self-service
- [ ] Finalize RBAC access for all user properties

## Examples

### Creating A Login Button On The Main Page

In the Jellyfin administration UI, under "General", there is a "Branding" section. In that section, add the following code in the "Login disclaimer" block (replacing `PROVIDER_NAME` and the domain):

```html
<form action="https://jellyfin.example.com/sso/OID/start/PROVIDER_NAME">
  <button class="raised block emby-button button-submit">
    Sign in with SSO
  </button>
</form>
```

Then, add the following code in the "Custom CSS code" section:

```css
a.raised.emby-button {
  padding: 0.9em 1em;
  color: inherit !important;
}

.disclaimerContainer {
  display: block;
}
```

![screenshot of the configuration page with the same code](img/custom-button.png)

For more information, refer to [issue #16](https://github.com/9p4/jellyfin-plugin-sso/issues/16).

### SAML

Example for adding a SAML configuration with the API using [curl](https://curl.se/):

`curl -v -X POST -H "Content-Type: application/json" -d '{"samlEndpoint": "https://keycloak.example.com/realms/test/protocol/saml", "samlClientId": "jellyfin-saml", "samlCertificate": "Very long base64 encoded string here", "enabled": true, "enableAuthorization": true, "enableAllFolders": false, "enabledFolders": [], "adminRoles": ["jellyfin-admin"], "roles": ["allowed-to-use-jellyfin"], "enableFolderRoles": true, "folderRoleMapping": [{"role": "allowed-to-watch-movies", "folders": ["cc7df17e2f3509a4b5fc1d1ff0a6c4d0", "f137a2dd21bbc1b99aa5c0f6bf02a805"]}]}' "https://myjellyfin.example.com/sso/SAML/Add/PROVIDER_NAME?api_key=API_KEY_HERE"`

Make sure that the JSON is the same as the configuration you would like.

The SAML provider must have the following configuration (I am using Keycloak, and I cannot speak for whatever you will see):

- Sign Documents on
- Sign Assertions off
- Client Signature Required off
- Redirect URI: [https://myjellyfin.example.com/sso/SAML/post/PROVIDER_NAME](https://myjellyfin.example.com/sso/SAML/start/PROVIDER_NAME)
- Base URL: [https://myjellyfin.example.com](https://myjellyfin.example.com)
- Master SAML processing URL: [https://myjellyfin.example.com/sso/SAML/start/PROVIDER_NAME](https://myjellyfin.example.com/sso/SAML/start/PROVIDER_NAME)

Make sure that `clientid` is replaced with the actual client ID and `PROVIDER_NAME` is replaced with the chosen provider name!

### OpenID

Example for adding an OpenID configuration with the API using [curl](https://curl.se/)

`curl -v -X POST -H "Content-Type: application/json" -d '{"oidEndpoint": "https://keycloak.example.com/realms/test", "oidClientId": "jellyfin-oid", "oidSecret": "short secret here", "enabled": true, "enableAuthorization": true, "enableAllFolders": false, "enabledFolders": [], "adminRoles": ["jellyfin-admin"], "roles": ["allowed-to-use-jellyfin"], "enableFolderRoles": true, "folderRoleMapping": [{"role": "allowed-to-watch-movies", "folders": ["cc7df17e2f3509a4b5fc1d1ff0a6c4d0", "f137a2dd21bbc1b99aa5c0f6bf02a805"]}], "roleClaim": "realm_access", "oidScopes" : [""]}' "https://myjellyfin.example.com/sso/OID/Add/PROVIDER_NAME?api_key=API_KEY_HERE"`

The OpenID provider must have the following configuration (again, I am using Keycloak)

- Access Type: Confidential
- Standard Flow Enabled
- Redirect URI: [https://myjellyfin.example.com/sso/OID/redirect/PROVIDER_NAME](https://myjellyfin.example.com/sso/OID/redirect/PROVIDER_NAME)
- Base URL: [https://myjellyfin.example.com](https://myjellyfin.example.com)

Make sure that `clientid` is replaced with the actual client ID and `PROVIDER_NAME` is replaced with the chosen provider name!

## API Endpoints

The API is all done from a base URL of `/sso/`

### SAML

#### Flow

- POST `SAML/start/PROVIDER_NAME`: This is the SAML POST endpoint. It accepts a form response from the SAML provider and returns HTML and JavaScript for the client to login with a given provider name.
- GET `SAML/start/PROVIDER_NAME`: This is the SAML initiator: it will begin the authorization flow for SAML with a given provider name.
- POST `SAML/Auth/PROVIDER_NAME`: This is the SAML client-side API: the HTML and JavaScript client will call this endpoint to receive Jellyfin credentials given a provider name. Post format is in JSON with the following keys:
  - `deviceId`: string. Device ID.
  - `deviceName`: string. Device name.
  - `appName`: string. App name.
  - `appVersion`: string. App version.
  - `data`: string. The signed SAML XML request. Used to verify a request.

#### Configuration

These all require authorization. Append an API key to the end of the request: `curl "http://myjellyfin.example.com/sso/SAML/Get?api_key=API_KEY_HERE"`

- POST `SAML/Add/PROVIDER_NAME`: This adds or overwrites a configuration for SAML for the given provider name. It accepts JSON with the following keys and format:
  - `samlEndpoint`: string. The SAML endpoint.
  - `samlClientId`: string. The SAML client ID.
  - `samlCertificate`: string. The base64 encoded SAML certificate.
  - `enabled`: boolean. Determines if the provider is enabled or not.
  - `enableAuthorization`: boolean: Determines if the plugin sets permissions for the user. If false, the user will start with no permissions and an administrator will add permissions. If disabled, then the permissions of users will not be modified and the Jellyfin defaults will be used instead.
  - `enableAllFolders`: boolean. Determines if the client logging in is allowed access to all folders.
  - `enabledFolders`: array of strings. If `enableAllFolders` is set to false, then this will be used to determine what folders the users who log in through this provider are allowed to use.
  - `roles`: array of strings. This validates the SAML response against the `Role` attribute. If a user has any of these roles, then the user is authenticated. Leave blank to disable role checking.
  - `adminRoles`: array of strings. This uses SAML response's `Role` attributes. If a user has any of these roles, then the user is an admin. Leave blank to disable (default is to not enable admin permissions).
  - `enableFolderRoles`: boolean. Determines if role-based folder access should be used.
  - `folderRoleMapping`: object in the format "role": string and "folders": array of strings. The user with this role will have access to the following folders if `enableFolderRoles` is enabled. To get the IDs of the folders, GET the `/Library/MediaFolders` URL with an API key. Look for the `Id` attribute.
  - `enableLiveTvRoles`: boolean. Determines if role-based Live TV access should be used.
  - `liveTvRoles`: array of strings. If `enableLiveTvRoles` is enabled, then the user's roles will be checked against these. If the user is granted permission, then the user will be able to view Live TV.
  - `liveTvManagementRoles`: array of strings. If `enableLiveTvRoles` is enabled, then the user's roles will be checked against these. If the user is granted permission, then the user will be able to manage Live TV.
  - `enableLiveTv`: boolean. Whether to allow Live TV by default. This applies even if `enableLiveTvRoles` is enabled.
  - `enableLiveTvManagement`: boolean. Whether to allow Live TV management by default. This applies even if `enableLiveTvRoles` is enabled.
  - `defaultProvider`: string. The set provider then gets assigned to the user after they have logged in. If it is not set, nothing is changed. With this, a user can login with SSO but is still able to log in via other providers later. See the `Unregister` endpoint.
  - `schemeOverride`: string. Sets the scheme for URLs used. Can be useful if the plugin refuses to use HTTPS URLs.
- GET `SAML/Del/PROVIDER_NAME`: This removes a configuration for SAML for a given provider name.
- GET `SAML/Get`: Lists the configurations currently available.

### OpenID

#### Flow

- GET `OID/redirect/PROVIDER_NAME`: This is the OpenID callback path. This will return HTML and JavaScript for the client to login with a given provider name.
- GET `OID/start/PROVIDER_NAME`: This is the OpenID initiator: it will begin the authorization flow for OpenID with a given provider name.
- POST `OID/Auth/PROVIDER_NAME`: This is the OpenID client-side API: the HTML and JavaScript client will call this endpoint to receive Jellyfin credentials for a given provider name. Post format is in JSON with the following keys:
  - `deviceId`: string. Device ID.
  - `deviceName`: string. Device name.
  - `appName`: string. App name.
  - `appVersion`: string. App version.
  - `data`: string. The OpenID state. Used to verify a request.

#### Configuration

These all require authorization. Append an API key to the end of the request: `curl "http://myjellyfin.example.com/sso/OID/Get?api_key=9c6e5fae4ae145669e6b7a3942f813b7"`

- POST `OID/Add/PROVIDERNAME`: This adds or overwrites a configuration for OpenID with a given provider name. It accepts JSON with the following keys and format:
  - `oidEndpoint`: string. The OpenID endpoint. Must have a `.well-known` path available.
  - `oidClientId`: string. The OpenID client ID.
  - `oidSecret`: string. The OpenID secret.
  - `enabled`: boolean. Determines if the provider is enabled or not.
  - `enableAuthorization`: boolean: Determines if the plugin sets permissions for the user. If false, the user will start with no permissions and an administrator will add permissions. If disabled, then the permissions of users will not be modified and the Jellyfin defaults will be used instead.
  - `enableAllFolders`: boolean. Determines if the client logging in is allowed access to all folders.
  - `enabledFolders`: array of strings. If `enableAllFolders` is set to false, then this will be used to determine what folders the users who log in through this provider are allowed to use.
  - `roles`: array of strings. This validates the OpenID response against the claim set in `roleClaim`. If a user has any of these roles, then the user is authenticated. Leave blank to disable role checking. This currently only works for Keycloak (to my knowledge).
  - `adminRoles`: array of strings. This uses the OpenID response against the claim set in `roleClaim`. If a user has any of these roles, then the user is an admin. Leave blank to disable (default is to not enable admin permissions).
  - `enableFolderRoles`: boolean. Determines if role-based folder access should be used.
  - `folderRoleMapping`: object in the format "role": string and "folders": array of strings. The user with this role will have access to the following folders if `enableFolderRoles` is enabled. To get the IDs of the folders, GET the `/Library/MediaFolders` URL with an API key. Look for the `Id` attribute.
  - `enableLiveTvRoles`: boolean. Determines if role-based Live TV access should be used.
  - `liveTvRoles`: array of strings. If `enableLiveTvRoles` is enabled, then the user's roles will be checked against these. If the user is granted permission, then the user will be able to view Live TV.
  - `liveTvManagementRoles`: array of strings. If `enableLiveTvRoles` is enabled, then the user's roles will be checked against these. If the user is granted permission, then the user will be able to manage Live TV.
  - `enableLiveTv`: boolean. Whether to allow Live TV by default. This applies even if `enableLiveTvRoles` is enabled.
  - `enableLiveTvManagement`: boolean. Whether to allow Live TV management by default. This applies even if `enableLiveTvRoles` is enabled.
  - `roleClaim`: string. This is the value in the OpenID response to check for roles. For Keycloak, it is `realm_access.roles` by default. The first element is the claim type, the subsequent values are to parse the JSON of the claim value. Use a "\\." to denote a literal ".". This expects a list of strings from the OIDC server.
  - `oidScopes` : array of strings. Each contains an additional scope name to include in the OIDC request.
    - For some OIDC providers (For example, [authelia](https://github.com/9p4/jellyfin-plugin-sso/issues/23#issuecomment-1112237616)), additional scopes may be required in order to validate group membership in role claim.
    - Leave empty to only request the default scopes.
  - `defaultProvider`: string. The set provider then gets assigned to the user after they have logged in. If it is not set, nothing is changed. With this, a user can login with SSO but is still able to log in via other providers later. See the `Unregister` endpoint.
  - `defaultUsernameClaim`: string. The provider will use the claim to create the users' usernames. If not set, it fallbacks to `preferred_username`.
  - `avatarUrlFormat`: string. The URL format for the users avatars. OIDC claims can be used by using the `@{claim_type}` syntax. If not set, the avatars won't change.
  - `disableHttps`: boolean. Determines whether the OpenID discovery endpoint requires HTTPS.
  - `doNotValidateEndpoints`: boolean. Determines whether the OpenID discovery process will validate endpoints. This may be required for Google.
  - `doNotValidateIssuerName`: boolean. Determines whether the OpenID discovery process will validate the OpenID issuer name.
  - `schemeOverride`: string. Sets the scheme for URLs used. Can be useful if the plugin refuses to use HTTPS URLs.
- GET `OID/Del/PROVIDER_NAME`: This removes a configuration for OpenID for a given provider name.
- GET `OID/Get`: Lists the configurations currently available.
- GET `OID/States`: Lists currently active OpenID flows in progress.

### Misc

- POST `Unregister/username`: This "unregisters" a user from SSO. A JSON-formatted string must be posted with the new authentication provider. To reset to the default provider, use `Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider` like so: `curl -X POST -H "Content-Type: application/json" -d '"Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider"' "https://myjellyfin.example.com/sso/Unregister/username?api_key=API_KEY`

## Limitations

Logging in with an SSO account that has the same username as an existing Jellyfin account will override the permissions for the user. Use caution when overriding the administrator account!

~~There is no GUI to sign in. You have to make it yourself! The buttons should redirect to something like this: [https://myjellyfin.example.com/sso/SAML/start/clientid](https://myjellyfin.example.com/sso/SAML/start/clientid) replacing `clientid` with the provider client ID and `SAML` with the auth scheme (either `SAML` or `OID`).~~

~~Furthermore, there is no functional admin page (yet). PRs for this are welcome. In the meantime, you have to interact with the API to add or remove configurations.~~ Added by [strazto](https://github.com/strazto) in PR [#18](https://github.com/9p4/jellyfin-plugin-sso/pull/18) and [#27](https://github.com/9p4/jellyfin-plugin-sso/pull/27).

There is also no logout callback. Logging out of Jellyfin will log you out of Jellyfin only, instead of the SSO provider as well.

~~This only supports Jellyfin on its own domain (for now). This is because I'm using string concatenation for generating some URLs. A PR is welcome to patch this.~~ Fixed in [PR #1](https://github.com/9p4/jellyfin-plugin-sso/pull/1).

**This only works on the web UI**. ~~The user must open the Jellyfin web UI BEFORE using the SSO program to populate some values in the localStorage.~~ Fixed by implementing a comment by [Pfuenzle](https://github.com/Pfuenzle) in [Issue #5](https://github.com/9p4/jellyfin-plugin-sso/issues/5#issuecomment-1041864820).

# Contributing

## Dependencies

A Nix development shell is also available with `nix develop`; `global.json` defines the SDK used by CI.

## Building and releasing

See [CONTRIBUTING.md](CONTRIBUTING.md) for reproducible builds, checks, packaging,
repository setup, and the release-label workflow. A merge to main builds the plugin;
only a merged PR with the `release` label can publish a version after all checks pass.

## Credits and Thanks

Much thanks to the [Jellyfin LDAP plugin](https://github.com/jellyfin/jellyfin-plugin-ldapauth) for offering a base for me to start on my plugin.

I use the [AspNet SAML](https://github.com/jitbit/AspNetSaml/) library for the SAML side of things (patched to work with Base64 on non-Windows machines).

I use the [Duende IdentityModel OIDC Client](https://github.com/DuendeSoftware/foss) library for the OpenID side of things.

Thanks to these projects, without which I would have been pulling my hair out implementing these protocols from scratch.

## Something funny about the origins of this plugin

It totally slipped my mind, but I had [requested this functionality a few years back](https://github.com/jellyfin/jellyfin/issues/2012). What goes around comes around, I guess.
