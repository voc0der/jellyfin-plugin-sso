#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

guid_re='[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}'

own_slug="voc0der/jellyfin-plugin-sso"

lower() {
  tr '[:upper:]' '[:lower:]'
}

manifest_guid="$(jq -r '.[0].guid // empty' manifest.json | lower)"

if ! [[ "${manifest_guid}" =~ ^${guid_re}$ ]]; then
  echo "manifest.json does not contain a valid plugin GUID." >&2
  exit 1
fi

# Every file that names the GUID must name exactly this one.
sources=(
  Directory.Build.props
  Jellyfin.Plugin.SsoAuthentication/SSOPlugin.cs
  Jellyfin.Plugin.SsoAuthentication/Config/config.js
  Jellyfin.Plugin.SsoAuthentication/Config/linking.js
)

for source in "${sources[@]}"; do
  found="$(grep -Eoh "${guid_re}" "${source}" | lower | sort -u)"
  if [ "$(printf '%s\n' "${found}" | sed '/^$/d' | wc -l)" -ne 1 ]; then
    echo "${source} must contain exactly one unique plugin GUID." >&2
    printf '%s\n' "${found}" >&2
    exit 1
  fi
  if [ "${found}" != "${manifest_guid}" ]; then
    echo "Plugin GUID mismatch:" >&2
    echo "  manifest.json: ${manifest_guid}" >&2
    echo "  ${source}: ${found}" >&2
    exit 1
  fi
done

known_taken_guids=(
  "eb5d7894-8eef-4b36-aa6f-5d124e828ce1" # Jellyfin plugin template
  "c3d840e2-b80d-4f3f-93c7-8b246d01ce0f" # Arr Calendar
  "f5a3c7e1-9b2d-4f6a-8e0c-1d3b5a7c9e2f" # IMDb Ratings
  "6b416775-6a90-436f-a034-796c52f5a317" # Restore User Data After Move
  "c937e280-0ac7-4250-a620-8c5892e628fe" # Seerr Integration
  "1ac3cf0f-f0f9-443a-be08-be38e48ff683" # Seerr Proxy
  "8d4bcbe8-ddd2-4c3a-ba8f-a7b500943e6b" # Session Provisioning
  "9f276cce-98c3-4554-b495-61cf673cf34c" # Torrent Throttle
  "2ce6b237-2610-4dfd-a34b-3f4c2ac64a88" # Transcode Guard
  "fdcf27a6-bd64-42e3-9f57-41880552bf83" # Transcode Nag
  "a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d" # K3ntas Ratings
  # 9p4/jellyfin-plugin-sso, which this continues under its own GUID: sharing it would make
  # Jellyfin update a 9p4 installation into this plugin.
  "505ce9d1-d916-42fa-86ca-673ef241d7df" # SSO Authentication (9p4)
)

for taken_guid in "${known_taken_guids[@]}"; do
  if [ "${manifest_guid}" = "${taken_guid}" ]; then
    echo "Plugin GUID ${manifest_guid} is known to belong to another plugin." >&2
    exit 1
  fi
done

if [ "${CHECK_REMOTE_GUIDS:-0}" = "1" ]; then
  # The catalogue publishes this plugin too, so an entry there that points back at this
  # repository is this plugin, not a collision. Matched by the repository named in the
  # manifest, the same way the catalogue matches its sources.
  manifest_slug="$(jq -r '.[0].imageUrl // empty' manifest.json \
    | sed -nE 's#^https://raw\.githubusercontent\.com/([^/]+/[^/]+)/.*#\1#p')"
  if [ "${manifest_slug}" != "${own_slug}" ]; then
    echo "manifest.json imageUrl does not name ${own_slug}." >&2
    exit 1
  fi

  remote_manifests=(
    "https://repo.jellyfin.org/files/plugin/manifest.json"
    "https://raw.githubusercontent.com/voc0der/jellyfin-plugins/main/manifest.json"
    "https://raw.githubusercontent.com/n00bcodr/jellyfin-plugins/main/manifest.json"
    "https://raw.githubusercontent.com/K3ntas/jellyfin-plugin-ratings/main/manifest.json"
    "https://raw.githubusercontent.com/9p4/jellyfin-plugin-sso/manifest-release/manifest.json"
  )

  for manifest_url in "${remote_manifests[@]}"; do
    if curl -fsSL "${manifest_url}" \
      | jq -e --arg guid "${manifest_guid}" --arg own "${own_slug}" '
          any(.[]; (.guid | ascii_downcase) == $guid
            and ([.imageUrl // "", (.versions // [])[].sourceUrl // ""] | join(" ") | contains($own) | not))' >/dev/null; then
      echo "Plugin GUID ${manifest_guid} already appears in ${manifest_url} for another plugin." >&2
      exit 1
    fi
  done
fi

echo "Plugin GUID ${manifest_guid} is internally consistent and not in the known-taken list."
