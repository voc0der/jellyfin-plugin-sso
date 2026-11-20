---
name: Bug report
about: Something does not work as described
title: ""
labels: bug
assignees: ""
---

**What happened**
A clear and concise description of what went wrong.

**Steps to reproduce**

1. Go to '...'
2. Sign in with '...'
3. See the error

**What you expected**
What should have happened instead.

**Identity provider**
Which provider (for example Authelia, authentik, Keycloak), its version, and whether you use
OpenID Connect or SAML.

**Configuration**
The provider's settings in the plugin, with the client secret, certificate, API keys, tokens and
personal data removed. Never upload the whole settings file. Report suspected vulnerabilities
privately instead: https://github.com/voc0der/jellyfin-plugin-sso/security/advisories/new

**Versions**

- Jellyfin: [e.g. 12.4.0]
- Plugin: [e.g. 1.0.0.1, or the PR number of a test build]
- Browser or app: [e.g. Firefox 145, Jellyfin Android]

**Logs**
The Jellyfin log lines around the sign-in, and any errors in the browser's console.
