# Contributing

This fork starts from [9p4/jellyfin-plugin-sso](https://github.com/9p4/jellyfin-plugin-sso)
at commit `f1e157bac29e6d27a0844378f3e1d4a4fdaa577a`. The upstream history and GPL-3.0
license are retained. Open issues and pull requests in **voc0der/jellyfin-plugin-sso**.
Report security issues using [SECURITY.md](SECURITY.md).

## Build and validate

Install the .NET SDK selected by `global.json`, Node.js 24 or newer, and Python 3.
The plugin targets Jellyfin 10.11 and .NET 9; the build SDK is .NET 10.

```sh
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore --warnaserror
dotnet format whitespace --verify-no-changes --no-restore
dotnet format style --verify-no-changes --no-restore --severity warn
python3 scripts/plugin.py validate
python3 -m unittest discover -s tests/tooling -v
npm ci --ignore-scripts
npm run format:check
npx playwright install --with-deps chromium
npm test
```

After intentionally changing NuGet versions, run `dotnet restore --force-evaluate`
and commit the updated `packages.lock.json` files. `npm install --package-lock-only`
refreshes the npm lockfile. Run `npx prettier --write .` to format web assets and docs.

The browser harness exercises the actual settings page with a stubbed Jellyfin API.
It covers loading, saving, and deleting providers without real secrets. It does not
exercise authentication against a live identity provider. The Python tests cover
release gates, metadata, runtime packaging, and manifest generation.

## Packaging

```sh
dotnet publish SSO-Auth/SSO-Auth.csproj --configuration Release --no-restore --output artifacts/publish
python3 scripts/plugin.py package --version 4.0.0.4 --output artifacts/SSO_Authentication_local.zip
```

The ZIP contains a versioned plugin directory, the GPL license, the plugin's F# helper,
FSharp.Core, and the Duende libraries. Jellyfin's own assemblies and the ASP.NET Core
shared framework are supplied by the server and must not be bundled. Archives have
MD5 checksums for Jellyfin's catalogue and SHA-256 checksums for manual verification.

## Automation and releases

- **Build and Release** builds changes and uploads an installable artifact. Only a
  merged PR carrying the `release` label can publish. Direct pushes and manual builds
  do not release.
- **Lint** checks .NET formatting, Prettier, metadata, and release tooling tests.
- **Settings page** runs the Chromium smoke test.
- **CodeQL** scans C#, JavaScript, and Actions using security and quality queries.
- **Dependency Review** checks PR dependency changes and audits NuGet/npm weekly.
- **PR Develop Build** publishes downloadable PR artifacts automatically; it also
  accepts a PR number through workflow dispatch to rerun that PR's existing artifact
  workflow. The build has no write permission or secrets. The dispatcher can rerun
  Actions but never checks out code, keeping PR builds in their own cache namespace.
- **Renovate** runs hourly, on main pushes, and when PR checks finish. Non-major
  updates wait three days and merge after checks pass. Major updates and Jellyfin
  packages require approval in the Dependency Dashboard.

Releases wait for all required checks on the exact main commit. The next four-part
version is allocated from the fork's manifest, starting at `4.0.0.5`; upstream's
`nightly` tag cannot affect numbering. The workflow builds versioned assemblies,
uploads assets, publishes the release, updates `manifest.json`, and sends a
`plugin-updated` dispatch to `voc0der/jellyfin-plugins`. It serializes publishing
and rebases only the manifest update when main advances. If publishing stops after
creating a release, reconcile that release and manifest before running another release.

Repository configuration:

| Name                       | Kind             | Purpose                                           |
| -------------------------- | ---------------- | ------------------------------------------------- |
| `RENOVATE_APP_CLIENT_ID`   | Actions variable | Renovate GitHub App client ID                     |
| `RENOVATE_APP_PRIVATE_KEY` | Actions secret   | Renovate GitHub App private key                   |
| `PLUGIN_REPO_TOKEN`        | Actions secret   | Dispatch access to the combined plugin repository |
| `icon.png`                 | Root asset       | Plugin catalogue and README icon                  |

The default Actions token has read permission. Release publication, CodeQL uploads,
and the PR workflow dispatcher receive only their required write permissions.
Actions are pinned to commit hashes and updated
by Renovate. The Renovate workflow never checks out pull request code.

The original plugin GUID is retained for replacement compatibility. Do not install
both forks together or subscribe to competing catalogues for that GUID. Do not bump
the Jellyfin ABI, change that identity, or publish compatibility claims without
validating against the intended server.
