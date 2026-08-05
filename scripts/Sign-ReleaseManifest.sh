#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: Sign-ReleaseManifest.sh <unsigned-manifest.json> <signed-output.json>" >&2
  exit 2
fi

script_dir="$(cd "$(dirname "$0")" && pwd)"
project_root="$(cd "$script_dir/.." && pwd)"
private_key="/Users/mattlixinchen/Library/Application Support/MLCCS/VideoSearch/Signing/manifest-v1-private.pem"
if [[ ! -f "$private_key" ]]; then
  echo "Production encrypted PKCS#8 key is missing." >&2
  exit 1
fi

security find-generic-password -s "MLCCS.VideoSearch.ManifestSigning" -a "manifest-v1" -w |
  dotnet run --project "$project_root/tools/MLCCS.VideoSearch.SigningTool" -- \
    sign "$1" "$private_key" "$2"
