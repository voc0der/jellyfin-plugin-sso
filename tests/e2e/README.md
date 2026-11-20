# End-to-end harness

Signs in to a real Jellyfin through a real Authelia, with this repository's release archive of
the plugin installed and nginx terminating TLS in front of both, all in throwaway containers.

It checks that the plugin loads; that a member of the allowed group signs in, lands on Jellyfin
Web's home screen with a new account, and gets the same account next time; that the admins group
makes an administrator; that someone outside the allowed group is turned away with no account
made; that an account made by sign-in cannot be signed in to with a password; that an account
Jellyfin would open with an empty password, as 9p4's plugin left its accounts on Jellyfin 12, is
locked to sign-in once its owner signs in; that the settings page lists the provider in the
dashboard; and that an account with a password links an Authelia identity from
`/SSOViews/linking` and then signs in with it.

## Running

Needs Docker, the .NET 10 SDK, Node, `openssl`, `zip` and `unzip`. No internet access is needed
beyond pulling the images.

```bash
cd tests/e2e
npm ci
npx playwright install chromium
npx playwright test
```

A run builds the release archive with `build.sh` (which runs the C# tests first), starts the
three containers, completes Jellyfin's setup wizard, adds the provider as an admin would, then
runs the tests and removes the containers. About a minute once the images are cached.

| Variable | Effect |
| --- | --- |
| `HARNESS_KEEP=1` | leave the containers running and print how to reach them |
| `PLUGIN_ZIP` | install this archive instead of building one, such as a published release |
| `JELLYFIN_IMAGE` | another Jellyfin image (default `jellyfin/jellyfin:12.4`) |
| `AUTHELIA_IMAGE` | another Authelia image (default `authelia/authelia:4.39`) |
| `PROXY_IMAGE` | another nginx image (default `nginx:1.27-alpine`) |

Jellyfin's admin is `admin` / `harness-admin-pw`. Authelia's users are `alice`, `dave` and
`frank` (group `jellyfin-users`), `carol` (`jellyfin-users` and `jellyfin-admins`) and `mallory`
(`visitors`), each with the password `<name>-harness-pw`. `erin` / `erin-harness-pw` is a Jellyfin
account with a password, for linking to, and `frank` a Jellyfin account with none.

## How the pieces reach each other

Jellyfin is `https://jellyfin.sso.test` and Authelia `https://auth.sso.test`, both served by the
proxy with a certificate from a CA the harness makes for each run. The containers resolve those
names through Docker's network aliases. The browser resolves them to the proxy's published port
(`--host-resolver-rules`, set in `harness/fixtures.mjs`) and accepts the certificate, so every
address the plugin builds is one the browser can follow, and Authelia sees the same issuer from
both sides. Jellyfin trusts the CA through `SSL_CERT_FILE` for its own calls to Authelia, and
trusts the proxy's `X-Forwarded-*` headers through its known proxies, as it would behind any
reverse proxy. The harness calls Jellyfin's admin API on its own published port.

The provider is set up as `providers.md` describes for Authelia, on Authelia's defaults
otherwise: `preferred_username` and `groups` reach the plugin from the userinfo endpoint, the
client authenticates with `client_secret_post`, and the plugin's pushed authorization is off,
because the plugin sends that request with Basic authentication and its token request in the
body, and Authelia allows one method per client.

Authelia's and nginx's files reach their containers through environment variables, written out
by the container itself, and the plugin and the CA are copied into Jellyfin's `/config` volume:
some rootless Docker setups cannot copy into a container that has never run, except into a
declared volume. The build goes to `.build/`, inside this folder rather than the system temp
folder, which rootless Docker cannot see.
