# Security policy

Report vulnerabilities privately through
[GitHub security advisories](https://github.com/voc0der/jellyfin-plugin-sso/security/advisories/new),
not in an issue. Include the plugin and Jellyfin versions, the identity provider, and the steps
to reproduce. Remove client secrets, tokens, private keys and personal data from anything you
attach.

The latest release is supported.

## What sign-in checks

So that you can judge it against your own setup, and report anything that falls short.

**OpenID Connect.** Sign-in uses the authorization code flow with PKCE. The state, and the
decision about the user's roles, stay on the server: the page that completes sign-in can only
redeem a state the server issued and approved, once. The ID token comes straight from the
provider's token endpoint, in exchange for the code and the client secret, so the plugin relies
on TLS for its origin, as OpenID Connect Core (§3.1.3.7) allows, and checks its nonce rather than
its signature. `disableHttps` removes that protection; leave it off.

**SAML.** A response is accepted only when signed by the provider certificate in the plugin's
settings, with the signature covering the whole response or its assertion; a forged assertion
placed beside a signed one is refused, and the assertion's `NotOnOrAfter` is enforced. XML
external entities are never resolved. Roles are checked both when the provider posts the response
and when the page redeems it. Not checked: the audience, the destination, and whether a response
was already used, so a captured response can be replayed until it expires. Keep assertion
lifetimes short.

**Accounts.** Accounts created by sign-in get a random password nobody knows and an
authentication provider Jellyfin cannot sign in with, so they can be used only through the
identity provider. An existing account that Jellyfin would open with an empty password, as 9p4's
plugin left the accounts it made on Jellyfin 12, gets the same the next time its owner signs in
through the provider, and a warning in the log. An SSO name that matches an existing Jellyfin
username signs in as that account; the README says more.

## Automated checks

CodeQL scans the C# and JavaScript on every change and weekly. Renovate opens fixes for advisories
from GitHub and osv.dev without waiting. The unit tests pin the checks above, and the sign-in
tests run against a real Jellyfin and Authelia. None of this is an audit.
