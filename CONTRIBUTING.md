# Contributing to *jellyfin-plugin-sso*

Issues and pull requests are welcome! Report security issues privately, as
[SECURITY.md](SECURITY.md) describes, not in an issue.

## Getting Started

1. Fork the repository
2. Create a feature branch from `main`
3. Make your changes
4. Submit a pull request

## Building

```bash
dotnet build --configuration Release
./build.sh
```

`build.sh` runs the tests and writes the installable archive to `artifacts/`:
`Jellyfin.Plugin.SsoAuthentication.dll`, the two Duende libraries the OpenID sign-in uses, `meta.json` and the license.
It refuses to package any other assembly. Jellyfin provides its own, `Newtonsoft.Json` and
ASP.NET Core, and a second copy in the plugin folder would give the plugin its own copy of types
the server owns. A new runtime dependency belongs in `SHIPPED_DLLS` only if Jellyfin does not
already have it.

The settings page (`Jellyfin.Plugin.SsoAuthentication/Config`), the linking page and its scripts (`Jellyfin.Plugin.SsoAuthentication/Views`), and
the page that completes sign-in (`Jellyfin.Plugin.SsoAuthentication/WebResponse.cs`) are embedded in the DLL. A change to
any of them needs a rebuild to reach a server. There is no JavaScript build step.

`Directory.Build.props` holds the version floor, the Jellyfin version the plugin is built
against, and the plugin GUID. Everything else reads them from there.

## Testing

Three layers, all run by CI. The settings page and sign-in harnesses, and the fuzz campaigns,
only run for changes that can affect them.

```bash
dotnet test --configuration Release
(cd tests/config-page && npm ci && npm test)
(cd tests/e2e && npm ci && npx playwright test)
```

`tests/Jellyfin.Plugin.SsoAuthentication.Tests` covers SAML response validation (signatures,
tampering, signature wrapping, expiry, external entities), the SAML sign-in from the provider's
post to the Jellyfin session, account creation and the permissions roles give, locking accounts
that would take an empty password, account linking and who may change whose links, the settings
API and which endpoints need an administrator, the plugin's identity and embedded pages,
importing a settings file in the format 9p4's 4.0.0.4 writes, and which values of
`JELLYFIN_ENABLE_PASSWORD_LOGIN` turn password sign-in off and which requests that refuses. Its user manager behaves as
Jellyfin 12's does where that matters: a lookup returns a fresh copy of the account, so a change
only counts once it is saved.

The OpenID flow tests use an in-memory HTTP provider with discovery, signed ID tokens, and
userinfo responses. They run the real Duende client through PKCE and token exchange, role and
username claims, linking, session creation, and provider failures without a listening port or
external service. The suite also checks SAML attribute aliases, XML dictionary behavior and
configuration import failures.

`tests/config-page` loads the settings page in a browser against a stubbed `ApiClient` and checks
that saving keeps what the page does not show, account links above all.

`tests/e2e` signs in to a real Jellyfin 12 through a real Authelia, with the archive `build.sh`
makes. It is the only test of what Jellyfin Web does with the session the plugin hands it, of the
proxy in between, and of a standards-following provider's answers. Run it after any change under
`Jellyfin.Plugin.SsoAuthentication/`; see [tests/e2e/README.md](tests/e2e/README.md).

## Authentication fuzzing

`SecurityFuzzTests` feeds the SAML parser and the sign-in and linking endpoints malformed, forged
and tampered inputs: mutations of broken Base64 and XML, injection strings, Unicode and long
values, and of signed SAML responses. A signature from anyone but the configured certificate
must not validate, and a signed response that still validates after a mutation must name the
same user with the same roles. Tampered SAML, OpenID states that were never issued or never
validated, and requests to change another user's links must be refused without creating an
account, a session or a link. Valid signed controls keep the parser from passing by refusing
everything.

Malformed SAML may make the parser throw `FormatException`, `XmlException` or
`CryptographicException`; any other exception fails the test. Inputs are at most 32 KiB. These
are seeded mutation tests of the real parser and controllers with Jellyfin's services
substituted, not coverage-guided fuzzing: HTTP routing, middleware and the browser remain the
sign-in harness's job.

`dotnet test` runs 64 inputs per test. The **Fuzz pentest** workflow runs 2,000 per test for
pull requests and pushes to `main` that change the plugin or its tests, 10,000 weekly, and on
demand, in two campaigns: `regression`, always seed 7331, and `exploration`, a new seed each
time. CI stops a test after two minutes and a job after 15.

Each campaign uploads `fuzz-pentest-regression` or `fuzz-pentest-exploration`: its seed, the TRX
results and, for a failure, a JSON file with the test, the iteration, the exact input in Base64
and the signing certificate. The job summary has the command that replays the campaign:

```bash
FUZZ_SEED=7331 FUZZ_ITERATIONS=2000 FUZZ_ARTIFACTS="$PWD/artifacts/fuzz" \
  dotnet test tests/Jellyfin.Plugin.SsoAuthentication.Tests --configuration Release \
  --filter 'Category=Fuzz' --logger 'console;verbosity=normal'
```

The seed replays the same mutations, but the test signing keys are new on every run, so a
failure in a signed response may need the saved input and certificate instead. When fixing a
finding, keep its input as a test: in `FuzzInputs.Corpus`, or a test of its own, as
`SamlResponseTests` has for signature references that are not `#` and an ID.

## Coverage

CI requires **100% C# line coverage** from a Release test run with coverlet and no
exclusions. It uploads the Cobertura report as `csharp-coverage`, and prints line and branch
coverage separately. The README badge is still **manually updated**; refresh it in the same
commit as any change that moves the rounded percentage meaningfully. JavaScript and browser
coverage are not part of this number.

```bash
# Use a fresh directory so an earlier report cannot accidentally satisfy the check.
coverage_dir=$(mktemp -d)
dotnet test tests/Jellyfin.Plugin.SsoAuthentication.Tests \
  --configuration Release \
  --collect:"XPlat Code Coverage" \
  --results-directory "$coverage_dir"
python3 scripts/check-coverage.py "$coverage_dir"
```

The gate uses the report's top-level `lines-covered` and `lines-valid` counts, without rounding,
so even one uncovered line fails. The badge rounds that percentage to the nearest whole number. Missing,
empty, invalid or multiple reports fail the check. The gate's own tests run with:

```bash
python3 -B -m unittest discover -s tests/coverage
```

Badge colours: `brightgreen` at or above 95, `green` under 95, `yellowgreen` under 85, `yellow`
under 75, `orange` under 60.

The page `WebResponse.cs` generates is one string the tests render whole, so its nearly 500 lines
all count as covered. This does not measure execution of the generated JavaScript; the browser
suites check the settings page and real sign-in behavior separately. Line coverage and branch
coverage are different: executing every line does not necessarily exercise every outcome of
each condition. Branch coverage is reported but is not subject to the 100% line coverage gate.

## Plugin GUID Safety

Verify that the plugin GUID is consistent and not already used by a known catalog before the
first release, and any time plugin metadata changes:

```bash
bash scripts/verify-plugin-guid.sh
CHECK_REMOTE_GUIDS=1 bash scripts/verify-plugin-guid.sh
```

The GUID is this plugin's own. Do not use the one 9p4's plugin published: Jellyfin would then
update a 9p4 installation into this plugin. The script refuses it.

## Names that must not change

Released, these are paths and stored values in people's installations, not labels:

- The assembly name, `Jellyfin.Plugin.SsoAuthentication`, names the settings file,
  `plugins/configurations/Jellyfin.Plugin.SsoAuthentication.xml`.
- The manifest name and the plugin's `Name`, `SSO Authentication`, name the install folder,
  `plugins/<name>_<version>`, and the settings page the dashboard links to.
- `Jellyfin.Plugin.SsoAuthentication.Api.SSOController` is stored on every account sign-in made
  or locked, as its authentication provider.
- `SSO-Auth.xml`, the file 9p4's plugin kept its settings in, is what the first start imports.

`tests/Jellyfin.Plugin.SsoAuthentication.Tests/PluginTests.cs` fails if the assembly name, the
plugin's `Name` or the controller's type name changes, and `build.sh` if the manifest name does.
The pages the settings page loads (`sso-authentication.js` and the rest) are only internal, but
the markup asks for them by name; `PluginTests` checks every name it asks for is served.

## Linting

Run lint checks locally before opening a PR:

```bash
dotnet format whitespace --verify-no-changes
dotnet format style --verify-no-changes --severity warn
for script in Config/config.js Config/linking.js Views/apiClient.js; do
  node --input-type=module --check < "Jellyfin.Plugin.SsoAuthentication/$script"
done
```

## Reporting Issues

- Search existing issues before opening a new one
- Include the Jellyfin and plugin versions, the identity provider and its version, and whether it
  is OpenID or SAML
- Include the provider's settings with the secret, certificate and any personal data removed, and
  the Jellyfin log lines around the sign-in

## Rules

- Keep branches, commits, and PRs focused. Do not mix unrelated local changes into the same PR.
- Use semantic names by default.

## Naming

- Branches: `fix/<scope>-<summary>`, `feat/<scope>-<summary>`, `refactor/<scope>-<summary>`
- Commits: `fix(scope): summary`, `feat(scope): summary`, `refactor(scope): summary`
- PR titles: `fix(scope): summary`, `feat(scope): summary`, `refactor(scope): summary`

## Pull Requests

- Keep changes focused and minimal
- Test against a running Jellyfin instance before submitting
- Describe what your PR changes and why
- Merging to `main` does not publish anything by itself. Add the `release` label before merging to cut a new tag and release; unlabelled changes go out with the next labelled merge

**PR Develop Build** (Actions) builds the archive for every pull request, for testing on a server
before it merges: it is attached to the pull request's run as `SSO_Authentication_pr-<number>_develop`.

## Release Metadata

- `manifest.json` is updated by the release workflow when a new version is published, and the
  [combined catalogue](https://github.com/voc0der/jellyfin-plugins) is told to rebuild.
- Releases are tagged `1.0.0.1` and so on. Each bumps the revision of the latest release tag, or of
  the version in `Directory.Build.props` if that is newer; raise it there to start a new line.

This repository only runs actions pinned to a full commit SHA, so workflows reference them as
`owner/action@<sha> # <version>`; Renovate keeps the pins current.

The workflows need `PLUGIN_REPO_TOKEN` (a secret that can dispatch to the combined catalogue) and
the Renovate GitHub App's `RENOVATE_APP_CLIENT_ID` (a variable) and `RENOVATE_APP_PRIVATE_KEY` (a
secret).

## LLM Disclosure

This project uses LLM-assisted development. Contributions generated with AI assistance are
welcome, but please review and test all code before submitting.
