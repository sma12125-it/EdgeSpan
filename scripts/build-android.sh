#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-}}"
if [[ -z "$sdk" || ! -d "$sdk" ]]; then
  echo "ANDROID_HOME is not set, so the APK and UHID server were not built." >&2
  exit 1
fi
cd "$root/android"
ks_pass="${EDGESPAN_KS_PASS:-edgestpan-dev}"
key_pass="${EDGESPAN_KEY_PASS:-$ks_pass}"
ks_alias="${EDGESPAN_KEY_ALIAS:-edgestpan}"
ks="$root/android/keystore/edgestpan-dev.keystore"
if [[ ! -f "$ks" ]]; then
  mkdir -p "$root/android/keystore"
  keytool -genkeypair -keystore "$ks" \
    -storepass "$ks_pass" -keypass "$key_pass" -alias "$ks_alias" \
    -keyalg RSA -keysize 2048 -validity 10000 \
    -dname "CN=EdgeSpan Dev, OU=EdgeSpan, O=EdgeSpan, L=Test, ST=Test, C=US"
fi
./gradlew :protocol:test :server-core:test :server-core:jar --no-daemon

d8=""
if command -v d8 >/dev/null 2>&1; then
  d8="$(command -v d8)"
else
  d8="$(find "$sdk/build-tools" -type f -name d8 | sort | tail -1)"
fi
if [[ -z "$d8" ]]; then
  echo "d8 was not found under $sdk/build-tools" >&2
  exit 1
fi
stdlib="$(find "$HOME/.gradle/caches/modules-2/files-2.1/org.jetbrains.kotlin/kotlin-stdlib" -name 'kotlin-stdlib-*.jar' ! -name '*sources*' ! -name '*javadoc*' | sort | tail -1)"
if [[ -z "$stdlib" ]]; then
  echo "kotlin-stdlib was not found in the Gradle cache" >&2
  exit 1
fi
server_dir="$root/publish/android-server"
mkdir -p "$server_dir" "$root/android/app/src/main/assets"
rm -rf "$server_dir/dex"
mkdir -p "$server_dir/dex"
"$d8" --min-api 24 --lib "$sdk/platforms/android-34/android.jar" \
  --output "$server_dir/dex" \
  "$root/android/server-core/build/libs/server-core.jar" \
  "$stdlib"
cp "$server_dir/dex/classes.dex" "$server_dir/edgestpan-server.dex"
# A zip entry whose name ends in .dex is dexopted by some package installers,
# which stalls on "Installing...". Ship the same bytes under a non-dex name.
rm -f "$root/android/app/src/main/assets/edgestpan-server.dex"
cp "$server_dir/edgestpan-server.dex" "$root/android/app/src/main/assets/edgestpan-server.bin"

./gradlew :app:assembleRelease --no-daemon
apk="$(find "$root/android/app/build/outputs/apk/release" -name '*.apk' ! -name '*unsigned*' | head -1)"
bt="$(find "$sdk/build-tools" -type f -name apksigner | sort | tail -1)"
align="$(find "$sdk/build-tools" -type f -name zipalign | sort | tail -1)"
# Gradle's v1 block is not the first ZIP entry, so JarInputStream (used by
# some file managers) never sees the manifest and the install sits forever.
# apksigner rewrites v1+v2+v3 with the manifest first and keeps alignment.
signed="$server_dir/EdgeSpan-Android.apk"
"$bt" sign \
  --ks "$ks" \
  --ks-pass pass:"$ks_pass" \
  --key-pass pass:"$key_pass" \
  --ks-key-alias "$ks_alias" \
  --v1-signing-enabled true \
  --v2-signing-enabled true \
  --v3-signing-enabled true \
  --out "$signed" \
  "$apk"
rm -f "$signed.idsig"
# v1 is present for older package installers. apksigner only reports it
# when the checked range includes API levels below 24, which is where v1 is used.
"$bt" verify --verbose --min-sdk-version 21 "$signed" | tee "$server_dir/apksigner-verify.txt" | awk '/Verified using v[123] /'
grep -q "Verified using v1 scheme (JAR signing): true" "$server_dir/apksigner-verify.txt"
grep -q "Verified using v2 scheme (APK Signature Scheme v2): true" "$server_dir/apksigner-verify.txt"
grep -q "Verified using v3 scheme (APK Signature Scheme v3): true" "$server_dir/apksigner-verify.txt"
"$bt" verify --verbose "$signed" | awk '/Verified using v[123] /'
"$align" -c 4 "$signed"
echo "APK: $signed"
echo "UHID server dex: $server_dir/edgestpan-server.dex"
