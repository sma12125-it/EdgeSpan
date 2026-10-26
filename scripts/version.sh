#!/usr/bin/env bash
# Prints the product version from its single source of truth.
#   scripts/version.sh windows  -> <Version> in Directory.Build.props
#   scripts/version.sh android  -> versionName in android/app/build.gradle.kts
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
case "${1:-windows}" in
  windows) v="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -1)" ;;
  android) v="$(sed -n 's:.*versionName *= *"\([^"]*\)".*:\1:p' "$root/android/app/build.gradle.kts" | head -1)" ;;
  *) echo "usage: $0 windows|android" >&2; exit 2 ;;
esac
if [[ -z "$v" ]]; then echo "version not found for ${1:-windows}" >&2; exit 1; fi
printf '%s\n' "$v"
