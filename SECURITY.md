# Security policy

Report vulnerabilities privately through [GitHub security advisories](https://github.com/voc0der/jellyfin-plugin-sso/security/advisories/new).
Include affected versions, a minimal reproduction, and the impact. Remove client secrets,
tokens, certificates containing private keys, and personal data from any attachments.
Please verify automated findings before submitting them. Coordinate public disclosure
with the maintainer after a fix is available.

The latest release of this fork is supported. Before the first release, reports against
`main` are welcome. This scaffold retains upstream authentication behavior; automated
checks do not constitute an audit of OIDC or SAML correctness.

CodeQL scans C#, JavaScript, and Actions on changes and weekly. NuGet and npm audits
reject high and critical advisories, including transitive dependencies. Dependency
review checks pull requests, and Renovate tracks both GitHub and OSV advisories.
GitHub secret scanning and push protection protect repository contributions.
