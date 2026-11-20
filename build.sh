#!/usr/bin/env bash
# Builds the installable archive: this plugin's assembly, the two Duende libraries it
# signs in with (Jellyfin does not provide them), meta.json and the license. Jellyfin's
# own assemblies, Newtonsoft.Json and the ASP.NET Core framework are compile-time
# references and must not ship.
#
# VERSION may be overridden by CI: VERSION=1.0.0.1 ./build.sh
# BUILD_TAG, when set, is written to BUILD_TAG.txt in the archive (PR builds).
set -euo pipefail

cd "$(dirname "$0")"

VERSION="${VERSION:-$(grep -oP '(?<=<Version>)[^<]+' Directory.Build.props | head -1)}"
SERVER="$(grep -oP '(?<=<JellyfinPackageVersion>)[^<]+' Directory.Build.props | head -1)"
ABI="$(grep -oP '(?<=<JellyfinTargetAbi>)[^<]+' Directory.Build.props | head -1)"
GUID="$(grep -oP '(?<=<PluginGuid>)[^<]+' Directory.Build.props | head -1)"
PROJECT="Jellyfin.Plugin.SsoAuthentication/Jellyfin.Plugin.SsoAuthentication.csproj"
OUT="artifacts"

# Exactly what the plugin needs at runtime, and nothing else.
SHIPPED_DLLS=(
    "Jellyfin.Plugin.SsoAuthentication.dll"
    "Duende.IdentityModel.dll"
    "Duende.IdentityModel.OidcClient.dll"
)

if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "ERROR: VERSION must have four numeric parts, got '$VERSION'." >&2
    exit 1
fi

rm -rf "$OUT"
mkdir -p "$OUT"

dotnet test --configuration Release --nologo -v q

# The archive contains a single top-level folder, which is what Jellyfin's installer
# extracts into the plugins directory.
folder="SSO_Authentication_${VERSION}"
staging="$OUT/staging/$folder"

echo
echo "==> Jellyfin $SERVER (abi $ABI)"

dotnet publish "$PROJECT" -c Release -o "$staging" -p:Version="$VERSION" --nologo -v q

# Anything from the host is a packaging bug, not a dependency: a second copy of a Jellyfin
# assembly would give the plugin its own copy of types the server owns.
for dll in "$staging"/*.dll; do
    name="$(basename "$dll")"
    if [[ ! " ${SHIPPED_DLLS[*]} " =~ " ${name} " ]]; then
        echo "ERROR: unexpected assembly in the plugin archive: ${name}" >&2
        echo "Add it to SHIPPED_DLLS only if Jellyfin does not already provide it." >&2
        exit 1
    fi
done

for name in "${SHIPPED_DLLS[@]}"; do
    if [ ! -f "$staging/$name" ]; then
        echo "ERROR: ${name} is missing from the publish output." >&2
        exit 1
    fi
done

if [ ! -f "$staging/meta.json" ]; then
    echo "ERROR: meta.json was not generated." >&2
    exit 1
fi

# meta.json is assembled from MSBuild items, where a semicolon in any value is an item
# separator: it splits the line in two and produces JSON Jellyfin refuses to deserialize.
# The archive is the only place that is visible, so check what goes into the archive.
python3 - "$staging/meta.json" "$GUID" "$VERSION" "$ABI" <<'EOF' || exit 1
import json, sys
path, guid, version, abi = sys.argv[1:]
try:
    meta = json.load(open(path))
except ValueError as error:
    sys.exit(f"ERROR: meta.json is not valid JSON: {error}")
for key, want in (("guid", guid), ("version", version), ("targetAbi", abi), ("name", "SSO Authentication")):
    if meta.get(key) != want:
        sys.exit(f"ERROR: meta.json {key} is {meta.get(key)!r}, expected {want!r}.")
EOF

# The plugin is GPL-3.0; its license travels with the binaries.
cp LICENSE "$staging/LICENSE"
if [ -n "${BUILD_TAG:-}" ]; then
    echo "${BUILD_TAG}" > "$staging/BUILD_TAG.txt"
fi

find "$staging" -maxdepth 1 -type f ! -name '*.dll' ! -name 'meta.json' ! -name 'LICENSE' ! -name 'BUILD_TAG.txt' -delete

archive="${folder}.zip"
(cd "$OUT/staging" && zip -qr "../$archive" "$folder")
rm -rf "$OUT/staging"

echo "    $OUT/$archive"
unzip -l "$OUT/$archive" | sed 's/^/    /'

echo
echo "Install: unzip into <jellyfin data>/plugins/ and restart."
