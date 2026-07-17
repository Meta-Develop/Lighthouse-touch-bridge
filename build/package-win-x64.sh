#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: build/package-win-x64.sh [version]

Publishes the self-contained win-x64 application and creates a portable ZIP.
If version is omitted, the Version property from Ltb.App.csproj is used.
EOF
}

if [[ ${1:-} == "-h" || ${1:-} == "--help" ]]; then
  usage
  exit 0
fi

if (( $# > 1 )); then
  usage >&2
  exit 2
fi

for command_name in dotnet git python3; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    printf 'Required packaging command is unavailable: %s\n' "$command_name" >&2
    exit 1
  fi
done

script_dir="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(CDPATH= cd -- "$script_dir/.." && pwd -P)"
project_path="$repo_root/src/Ltb.App/Ltb.App.csproj"
artifact_root="$repo_root/artifacts/package"

version="${1:-}"
if [[ -z "$version" ]]; then
  version="$(dotnet msbuild "$project_path" -nologo -getProperty:Version | tr -d '\r' | tail -n 1)"
fi

if ! python3 - "$version" <<'PY'
import re
import sys

version = sys.argv[1]
match = re.fullmatch(
    r"(0|[1-9][0-9]*)\."
    r"(0|[1-9][0-9]*)\."
    r"(0|[1-9][0-9]*)"
    r"(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?"
    r"(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?",
    version,
)
if match is None:
    raise SystemExit(1)
prerelease = match.group(4)
if prerelease is not None:
    for identifier in prerelease.split("."):
        if identifier.isdigit() and len(identifier) > 1 and identifier.startswith("0"):
            raise SystemExit(1)
PY
then
  printf 'Version must be strict SemVer, for example 0.1.0, 0.1.0-preview.1, or 0.1.0+build.7: %s\n' "$version" >&2
  exit 2
fi

safe_version="${version//+/_}"
package_name="LighthouseTouchBridge-$safe_version-win-x64"
archive_name="$package_name.zip"
archive_path="$artifact_root/$archive_name"
checksum_path="$archive_path.sha256"

mkdir -p -- "$artifact_root"
if [[ -e "$archive_path" || -e "$checksum_path" ]]; then
  printf 'Refusing to overwrite an existing package. Remove or archive it after review: %s\n' "$archive_path" >&2
  exit 1
fi

stage_root="$(mktemp -d "$artifact_root/.ltb-package.XXXXXX")"
cleanup() {
  if [[ -n ${stage_root:-} && "$stage_root" == "$artifact_root"/.ltb-package.* && -d "$stage_root" ]]; then
    rm -rf -- "$stage_root"
  fi
}
trap cleanup EXIT

package_root="$stage_root/$package_name"
mkdir -p -- "$package_root/docs"

commit_id="$(git -C "$repo_root" rev-parse --verify HEAD)"
source_tree_dirty="false"
if [[ -n "$(git -C "$repo_root" status --porcelain --untracked-files=normal -- \
  . \
  ':(exclude).maco' \
  ':(exclude).maco/**' \
  ':(exclude)artifacts' \
  ':(exclude)artifacts/**')" ]]; then
  source_tree_dirty="true"
fi
printf 'Publishing Lighthouse Touch Bridge %s for win-x64...\n' "$version"
dotnet publish "$project_path" \
  -p:PublishProfile=win-x64 \
  -p:PublishDir="$package_root/" \
  -p:Version="$version" \
  -p:InformationalVersion="$version"

dotnet_sdk_version="$(dotnet --version)"
python_version="$(python3 -c 'import platform; print(platform.python_version())')"
read -r zlib_build_version zlib_runtime_version < <(
  python3 -c 'import zlib; print(zlib.ZLIB_VERSION, zlib.ZLIB_RUNTIME_VERSION)'
)
expected_runtime_pack_version="8.0.28"
runtime_pack_version="$(python3 - "$package_root/Ltb.App.deps.json" <<'PY'
import json
import pathlib
import sys

deps_path = pathlib.Path(sys.argv[1])
with deps_path.open("r", encoding="utf-8-sig") as stream:
    document = json.load(stream)
prefix = "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/"
matches = sorted(key for key in document.get("libraries", {}) if key.startswith(prefix))
if len(matches) != 1:
    raise SystemExit(
        f"Expected one win-x64 .NET runtime pack in {deps_path}, found {matches!r}"
    )
print(matches[0][len(prefix):])
PY
)"
if [[ "$runtime_pack_version" != "$expected_runtime_pack_version" ]]; then
  printf 'Published runtime pack mismatch. Expected %s, got %s.\n' \
    "$expected_runtime_pack_version" "$runtime_pack_version" >&2
  exit 1
fi

while IFS= read -r -d '' symbol_file; do
  rm -f -- "$symbol_file"
done < <(find "$package_root" -type f -name '*.pdb' -print0)

required_publish_files=(
  "$package_root/Ltb.App.exe"
  "$package_root/openvr_api.dll"
  "$package_root/licenses/Valve.OpenVR.LICENSE.txt"
)
for required_file in "${required_publish_files[@]}"; do
  if [[ ! -f "$required_file" ]]; then
    printf 'Required publish file is missing: %s\n' "$required_file" >&2
    exit 1
  fi
done

expected_openvr_hash="bab8ac6ef64e68a9ca53315b0014d131088584b2efdfa6db511d67ec03cfcb4a"
mapfile -t integrity_hashes < <(python3 - \
  "$package_root/openvr_api.dll" \
  "$repo_root/src/Ltb.OpenVr/ThirdParty/OpenVR/LICENSE" \
  "$package_root/licenses/Valve.OpenVR.LICENSE.txt" <<'PY'
import hashlib
import pathlib
import sys

def sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()

openvr_path = pathlib.Path(sys.argv[1])
source_license_path = pathlib.Path(sys.argv[2])
published_license_path = pathlib.Path(sys.argv[3])
source_license = source_license_path.read_bytes()
published_license = published_license_path.read_bytes()
if published_license != source_license:
    raise SystemExit("Published Valve license differs from the vendored source license.")
print(sha256(openvr_path))
print(hashlib.sha256(source_license).hexdigest())
PY
)
actual_openvr_hash="${integrity_hashes[0]:-}"
valve_license_hash="${integrity_hashes[1]:-}"
if [[ "$actual_openvr_hash" != "$expected_openvr_hash" ]]; then
  printf 'openvr_api.dll SHA-256 mismatch. Expected %s, got %s.\n' \
    "$expected_openvr_hash" "$actual_openvr_hash" >&2
  exit 1
fi

install -m 0644 "$repo_root/LICENSE" "$package_root/LICENSE.txt"
for document_name in setup troubleshooting architecture calibration windows-verification specification; do
  install -m 0644 \
    "$repo_root/docs/$document_name.md" \
    "$package_root/docs/$document_name.md"
done

{
  printf 'product=Lighthouse Touch Bridge\n'
  printf 'version=%s\n' "$version"
  printf 'source_commit=%s\n' "$commit_id"
  printf 'source_tree_dirty=%s\n' "$source_tree_dirty"
  printf 'target_framework=net8.0\n'
  printf 'runtime_identifier=win-x64\n'
  printf 'runtime_framework_version=%s\n' "$expected_runtime_pack_version"
  printf 'runtime_pack_version=%s\n' "$runtime_pack_version"
  printf 'dotnet_sdk_version=%s\n' "$dotnet_sdk_version"
  printf 'python_version=%s\n' "$python_version"
  printf 'zlib_build_version=%s\n' "$zlib_build_version"
  printf 'zlib_runtime_version=%s\n' "$zlib_runtime_version"
  printf 'self_contained=true\n'
  printf 'publish_single_file=false\n'
  printf 'publish_trimmed=false\n'
  printf 'openvr_api_sha256=%s\n' "$actual_openvr_hash"
  printf 'valve_license_sha256=%s\n' "$valve_license_hash"
} > "$package_root/release-manifest.txt"

temporary_archive="$stage_root/$archive_name"
python3 - "$stage_root" "$package_name" "$temporary_archive" <<'PY'
import pathlib
import stat
import sys
import zipfile

stage_root = pathlib.Path(sys.argv[1])
package_name = sys.argv[2]
archive_path = pathlib.Path(sys.argv[3])
package_root = stage_root / package_name

with zipfile.ZipFile(
    archive_path,
    mode="w",
    compression=zipfile.ZIP_DEFLATED,
    compresslevel=9,
) as archive:
    for path in sorted(package_root.rglob("*")):
        if not path.is_file():
            continue
        relative_path = pathlib.PurePosixPath(package_name) / path.relative_to(package_root)
        info = zipfile.ZipInfo(str(relative_path), date_time=(1980, 1, 1, 0, 0, 0))
        mode = 0o755 if path.name == "Ltb.App.exe" else 0o644
        info.external_attr = (stat.S_IFREG | mode) << 16
        info.compress_type = zipfile.ZIP_DEFLATED
        with path.open("rb") as stream:
            archive.writestr(info, stream.read(), compresslevel=9)
PY

python3 - \
  "$temporary_archive" \
  "$package_name" \
  "$version" \
  "$commit_id" \
  "$source_tree_dirty" \
  "$dotnet_sdk_version" \
  "$runtime_pack_version" \
  "$python_version" \
  "$zlib_build_version" \
  "$zlib_runtime_version" \
  "$actual_openvr_hash" \
  "$valve_license_hash" <<'PY'
import hashlib
import pathlib
import posixpath
import re
import sys
import urllib.parse
import zipfile

(
    archive_value,
    package_name,
    version,
    commit_id,
    source_tree_dirty,
    dotnet_sdk_version,
    runtime_pack_version,
    python_version,
    zlib_build_version,
    zlib_runtime_version,
    openvr_hash,
    valve_license_hash,
) = sys.argv[1:]
archive_path = pathlib.Path(archive_value)
root = f"{package_name}/"
required = {
    root + "Ltb.App.exe",
    root + "Ltb.App.dll",
    root + "Ltb.App.deps.json",
    root + "Ltb.App.runtimeconfig.json",
    root + "coreclr.dll",
    root + "hostfxr.dll",
    root + "openvr_api.dll",
    root + "licenses/Valve.OpenVR.LICENSE.txt",
    root + "LICENSE.txt",
    root + "release-manifest.txt",
    root + "docs/setup.md",
    root + "docs/troubleshooting.md",
    root + "docs/architecture.md",
    root + "docs/calibration.md",
    root + "docs/windows-verification.md",
    root + "docs/specification.md",
}
expected_manifest = {
    "product": "Lighthouse Touch Bridge",
    "version": version,
    "source_commit": commit_id,
    "source_tree_dirty": source_tree_dirty,
    "target_framework": "net8.0",
    "runtime_identifier": "win-x64",
    "runtime_framework_version": "8.0.28",
    "runtime_pack_version": runtime_pack_version,
    "dotnet_sdk_version": dotnet_sdk_version,
    "python_version": python_version,
    "zlib_build_version": zlib_build_version,
    "zlib_runtime_version": zlib_runtime_version,
    "self_contained": "true",
    "publish_single_file": "false",
    "publish_trimmed": "false",
    "openvr_api_sha256": openvr_hash,
    "valve_license_sha256": valve_license_hash,
}

with zipfile.ZipFile(archive_path, "r") as archive:
    infos = archive.infolist()
    names = {info.filename for info in infos}
    if len(names) != len(infos):
        raise SystemExit("Package contains duplicate ZIP entry names.")
    missing = sorted(required - names)
    if missing:
        raise SystemExit(f"Package is missing required entries: {missing!r}")
    if any(info.date_time != (1980, 1, 1, 0, 0, 0) for info in infos):
        raise SystemExit("Package contains a non-deterministic ZIP timestamp.")
    forbidden_tokens = (
        ".ltb-backup",
        "steamvr.vrsettings",
        "/backups/",
        "/logs/",
        "/recordings/",
        "/.agents/",
        "/.maco/",
    )
    forbidden = sorted(
        name
        for name in names
        if name.lower().endswith((".pdb", ".log", ".bak", ".backup", ".vrsettings"))
        or any(token in name.lower() for token in forbidden_tokens)
    )
    if forbidden:
        raise SystemExit(f"Package contains forbidden entries: {forbidden!r}")

    manifest_lines = archive.read(root + "release-manifest.txt").decode("utf-8").splitlines()
    manifest = {}
    for line in manifest_lines:
        if "=" not in line:
            raise SystemExit(f"Malformed manifest line: {line!r}")
        key, value = line.split("=", 1)
        if key in manifest:
            raise SystemExit(f"Duplicate manifest key: {key}")
        manifest[key] = value
    if manifest != expected_manifest:
        raise SystemExit(
            f"Manifest mismatch. Expected {expected_manifest!r}, got {manifest!r}"
        )
    if hashlib.sha256(archive.read(root + "openvr_api.dll")).hexdigest() != openvr_hash:
        raise SystemExit("Archived openvr_api.dll hash does not match the manifest.")
    if hashlib.sha256(
        archive.read(root + "licenses/Valve.OpenVR.LICENSE.txt")
    ).hexdigest() != valve_license_hash:
        raise SystemExit("Archived Valve license hash does not match the manifest.")

    link_pattern = re.compile(r"\[[^\]]*\]\(([^)]+)\)")
    for name in sorted(entry for entry in names if entry.endswith(".md")):
        markdown = archive.read(name).decode("utf-8")
        for raw_target in link_pattern.findall(markdown):
            target = raw_target.strip().strip("<>")
            if re.match(r"^[A-Za-z][A-Za-z0-9+.-]*:", target) or target.startswith("//"):
                continue
            target_path = urllib.parse.unquote(target.split("#", 1)[0].split("?", 1)[0])
            if not target_path:
                continue
            resolved = posixpath.normpath(posixpath.join(posixpath.dirname(name), target_path))
            if resolved.startswith("../") or resolved.startswith("/") or resolved not in names:
                raise SystemExit(
                    f"Broken packaged Markdown link in {name}: {raw_target!r} -> {resolved!r}"
                )
PY

archive_hash="$(python3 - "$temporary_archive" <<'PY'
import hashlib
import pathlib
import sys

path = pathlib.Path(sys.argv[1])
digest = hashlib.sha256()
with path.open("rb") as stream:
    for block in iter(lambda: stream.read(1024 * 1024), b""):
        digest.update(block)
print(digest.hexdigest())
PY
)"
printf '%s  %s\n' "$archive_hash" "$archive_name" > "$stage_root/$archive_name.sha256"

mv -- "$temporary_archive" "$archive_path"
mv -- "$stage_root/$archive_name.sha256" "$checksum_path"

if ! python3 - "$archive_path" "$checksum_path" "$archive_hash" <<'PY'
import hashlib
import pathlib
import sys

archive_path = pathlib.Path(sys.argv[1])
checksum_path = pathlib.Path(sys.argv[2])
expected_hash = sys.argv[3]
actual_hash = hashlib.sha256(archive_path.read_bytes()).hexdigest()
expected_checksum = f"{expected_hash}  {archive_path.name}\n"
if actual_hash != expected_hash:
    raise SystemExit("Final archive hash changed after move.")
if checksum_path.read_text(encoding="ascii") != expected_checksum:
    raise SystemExit("Final checksum file does not exactly match the archive hash and name.")
PY
then
  rm -f -- "$archive_path" "$checksum_path"
  printf 'Final package verification failed; invalid output was removed.\n' >&2
  exit 1
fi

printf 'Package: %s\n' "$archive_path"
printf 'SHA-256: %s\n' "$checksum_path"
printf 'Build-host verification complete: publish, runtime-pack, license, manifest, ZIP contents, links, timestamps, and checksum checks passed.\n'
printf 'Runtime verification remainder requires Windows: launch Ltb.App.exe, exercise SteamVR/ALVR/VMT and hardware checks, and evaluate signing/SmartScreen.\n'
