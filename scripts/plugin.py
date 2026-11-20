#!/usr/bin/env python3
"""Validate plugin identity and build installable Jellyfin archives."""

import argparse
import hashlib
import json
import re
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = "voc0der/jellyfin-plugin-sso"


def build_metadata(root=ROOT):
    # These fields intentionally use plain, quoted scalars in build.yaml.
    text = (root / "build.yaml").read_text()
    fields = dict(re.findall(r'^([A-Za-z]+): "([^"\n]+)"$', text, re.MULTILINE))
    artifacts = re.search(r"^artifacts:\n((?:  - .+\n)+)", text, re.MULTILINE)
    if not artifacts:
        raise ValueError("build.yaml must list its runtime artifacts")
    fields["artifacts"] = re.findall(r'  - "([^"\n]+)"', artifacts[1])
    return fields


def version_tuple(version):
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+", version):
        raise ValueError(f"Invalid four-part plugin version: {version}")
    parts = tuple(int(part) for part in version.split("."))
    if any(part > 65534 for part in parts):
        raise ValueError("Plugin versions must fit .NET assembly version fields")
    return parts


def validate(root=ROOT):
    metadata = build_metadata(root)
    plugin, = json.loads((root / "manifest.json").read_text())
    for key in ("guid", "name", "owner", "imageUrl", "category"):
        if metadata[key] != plugin[key]:
            raise ValueError(f"build.yaml and manifest.json disagree on {key}")
    guid = plugin["guid"]
    if not re.fullmatch(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}", guid):
        raise ValueError("Invalid plugin GUID")
    for file in ("SSO-Auth/SSOPlugin.cs", "SSO-Auth/Config/config.js", "SSO-Auth/Config/linking.js"):
        if guid not in (root / file).read_text():
            raise ValueError(f"Plugin GUID is inconsistent in {file}")
    for file in ("SSO-Auth/SSO-Auth.csproj", "SSO-Auth/Lib/Lib.fsproj"):
        project = ET.parse(root / file)
        if project.findtext(".//TargetFramework") != metadata["framework"]:
            raise ValueError(f"Target framework is inconsistent in {file}")
    project = ET.parse(root / "SSO-Auth/SSO-Auth.csproj")
    for field in ("AssemblyVersion", "FileVersion", "Version"):
        if project.findtext(f".//{field}") != metadata["version"]:
            raise ValueError(f"{field} must match build.yaml")
    for package in project.findall(".//PackageReference"):
        if package.attrib["Include"].startswith("Jellyfin."):
            if package.attrib["Version"] + ".0" != metadata["targetAbi"]:
                raise ValueError("Jellyfin package versions must match targetAbi")
    version_tuple(metadata["version"])
    expected_image = f"https://raw.githubusercontent.com/{REPOSITORY}/main/icon.png"
    if plugin["owner"] != "voc0der" or plugin["imageUrl"] != expected_image:
        raise ValueError("Manifest must point at this fork")
    artifacts = metadata["artifacts"]
    if len(set(artifacts)) != len(artifacts) or "SSO-Auth.dll" not in artifacts:
        raise ValueError("Runtime artifacts must be unique and include the plugin")
    for name in artifacts:
        if Path(name).name != name or not name.endswith(".dll"):
            raise ValueError(f"Invalid runtime artifact: {name}")
    versions = [version_tuple(entry["version"]) for entry in plugin["versions"]]
    if versions != sorted(set(versions), reverse=True):
        raise ValueError("Manifest versions must be unique and newest first")
    return metadata, plugin


def package(publish, output, version, build_tag=None, root=ROOT):
    metadata, _ = validate(root)
    version_tuple(version)
    names = metadata["artifacts"]
    for name in names:
        if not (publish / name).is_file():
            raise ValueError(f"Missing runtime dependency: {name}")
    output.parent.mkdir(parents=True, exist_ok=True)
    folder = f"SSO_Authentication_{version}"
    with ZipFile(output, "w", ZIP_DEFLATED) as archive:
        for name in names:
            archive.write(publish / name, f"{folder}/{name}")
        archive.write(root / "LICENSE.txt", f"{folder}/LICENSE.txt")
        if build_tag:
            archive.writestr(f"{folder}/BUILD_TAG.txt", build_tag + "\n")
    for algorithm in ("md5", "sha256"):
        digest = hashlib.new(algorithm, output.read_bytes()).hexdigest()
        output.with_suffix(output.suffix + f".{algorithm}").write_text(f"{digest}  {output.name}\n")


def next_version(root=ROOT):
    metadata, plugin = validate(root)
    candidates = [metadata["version"]] + [item["version"] for item in plugin["versions"]]
    parts = list(max(version_tuple(version) for version in candidates))
    parts[-1] += 1
    version = ".".join(map(str, parts))
    version_tuple(version)
    return version


def update_manifest(version, archive, changelog, root=ROOT):
    metadata, plugin = validate(root)
    version_tuple(version)
    if version != next_version(root):
        raise ValueError("Release version must follow the existing catalogue")
    if archive.name != f"SSO_Authentication_{version}.zip":
        raise ValueError("Unexpected release archive name")
    plugin["versions"].insert(0, {
        "version": version,
        "changelog": changelog.read_text(),
        "targetAbi": metadata["targetAbi"],
        "sourceUrl": f"https://github.com/{REPOSITORY}/releases/download/{version}/{archive.name}",
        "checksum": hashlib.md5(archive.read_bytes()).hexdigest(),
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    })
    (root / "manifest.json").write_text(json.dumps([plugin], indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("validate")
    commands.add_parser("next-version")
    pack = commands.add_parser("package")
    pack.add_argument("--publish", type=Path, default=ROOT / "artifacts/publish")
    pack.add_argument("--output", type=Path, required=True)
    pack.add_argument("--version", required=True)
    pack.add_argument("--build-tag")
    manifest = commands.add_parser("manifest")
    manifest.add_argument("--version", required=True)
    manifest.add_argument("--archive", type=Path, required=True)
    manifest.add_argument("--changelog", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "validate":
        validate()
        print("Plugin identity, ABI, version and package metadata are consistent.")
    elif args.command == "next-version":
        print(next_version())
    elif args.command == "package":
        package(args.publish, args.output, args.version, args.build_tag)
    else:
        update_manifest(args.version, args.archive, args.changelog)


if __name__ == "__main__":
    main()
