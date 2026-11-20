#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

: "${GITHUB_REPOSITORY:?}"
: "${GITHUB_SHA:?}"
: "${GH_TOKEN:?}"

if [ "$GITHUB_REPOSITORY" != "voc0der/jellyfin-plugin-sso" ]; then
  echo "Release publishing is configured for voc0der/jellyfin-plugin-sso." >&2
  exit 1
fi

# The release job checks out the triggering commit. Read the latest catalogue so
# queued releases cannot allocate the same version after another manifest push.
git fetch origin main
git restore --source=origin/main -- manifest.json
version=$(python3 scripts/plugin.py next-version)
previous=$(jq -r '.[0].versions[0].version // empty' manifest.json)
notes=artifacts/CHANGELOG.md
mkdir -p artifacts
{
  echo "## What's Changed"
  echo
  if [ -n "$previous" ]; then
    git log "${previous}..${GITHUB_SHA}" --pretty=format:'- %s (%h)' --no-merges
    echo
    echo
    echo "**Full Changelog**: https://github.com/${GITHUB_REPOSITORY}/compare/${previous}...${version}"
  else
    echo "Initial release of the independently maintained SSO fork, based on upstream 4.0.0.4."
  fi
} > "$notes"

dotnet publish SSO-Auth/SSO-Auth.csproj --configuration Release --no-restore \
  --output artifacts/publish -p:AssemblyVersion="$version" -p:FileVersion="$version" -p:Version="$version"
archive="artifacts/SSO_Authentication_${version}.zip"
python3 scripts/plugin.py package --version "$version" --output "$archive"
python3 scripts/plugin.py manifest --version "$version" --archive "$archive" --changelog "$notes"

# Upload as a draft, then make the assets visible before advertising their URL.
gh release create "$version" "$archive" "${archive}.md5" "${archive}.sha256" \
  --draft --target "$GITHUB_SHA" --title "Release $version" --notes-file "$notes"
gh release edit "$version" --draft=false --latest

git config user.name "voc0der"
git config user.email "contact@netspace.in"
git add manifest.json
git commit -m "Update manifest.json for version ${version} [skip ci]"
# A concurrent source push must be preserved. Only the generated manifest commit
# is rebased; the release tag continues to identify the code actually compiled.
for attempt in 1 2 3; do
  git fetch origin main
  git rebase origin/main
  if git push origin HEAD:main; then
    exit 0
  fi
  sleep "$attempt"
done
echo "Release exists, but its manifest could not be pushed; reconcile before releasing again." >&2
exit 1
