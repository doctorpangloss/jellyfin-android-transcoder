#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${VERSION:-1.0.0}"
DIST="$ROOT/dist"
WORK="$ROOT/.work/release-$VERSION"
ANDROID_HOME="${ANDROID_HOME:-$HOME/Android/Sdk}"

rm -rf "$DIST" "$WORK"
mkdir -p "$DIST" "$WORK/plugin/Android Transcoder_$VERSION/shim-payload"

dotnet test "$ROOT/JellyfinAndroidTranscoder.sln" --nologo
dotnet publish "$ROOT/jellyfin-android-transcoder/src/JellyfinAndroidTranscoder.Shim/JellyfinAndroidTranscoder.Shim.csproj" \
  -c Release \
  -o "$WORK/shim"
dotnet publish "$ROOT/jellyfin-android-transcoder/src/Jellyfin.Plugin.AndroidTranscoder/Jellyfin.Plugin.AndroidTranscoder.csproj" \
  -c Release \
  -o "$WORK/plugin-publish"

cp "$WORK/plugin-publish/Jellyfin.Plugin.AndroidTranscoder.dll" "$WORK/plugin/Android Transcoder_$VERSION/Jellyfin.Plugin.AndroidTranscoder.dll"
cp "$WORK/plugin-publish/QRCoder.dll" "$WORK/plugin/Android Transcoder_$VERSION/QRCoder.dll"
cp "$WORK/shim/jfat-ffmpeg" "$WORK/plugin/Android Transcoder_$VERSION/shim-payload/jfat-ffmpeg"
chmod 755 "$WORK/plugin/Android Transcoder_$VERSION/shim-payload/jfat-ffmpeg"
cat > "$WORK/plugin/Android Transcoder_$VERSION/meta.json" <<JSON
{
  "category": "Transcoding",
  "changelog": "Initial public release.",
  "description": "Routes eligible Jellyfin transcodes to an Android MediaCodec FFmpeg worker.",
  "guid": "7fd06ed7-4044-4d38-a6e3-8b4432ff8f8c",
  "name": "Android Transcoder",
  "overview": "Android MediaCodec transcode bridge for Jellyfin.",
  "owner": "doctorpangloss",
  "targetAbi": "10.11.6.0",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%S.0000000Z)",
  "version": "$VERSION.0",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": []
}
JSON

(cd "$WORK/plugin" && zip -qr "$DIST/Jellyfin.Plugin.AndroidTranscoder-$VERSION.zip" "Android Transcoder_$VERSION")

(cd "$ROOT/android-transcoder" && ANDROID_HOME="$ANDROID_HOME" ./gradlew :app:assembleVanilla :app:bundleVanilla)
cp "$ROOT/android-transcoder/app/build/outputs/apk/vanilla/app-vanilla.apk" "$DIST/jellyfin-android-transcoder-$VERSION.apk"
cp "$ROOT/android-transcoder/app/build/outputs/bundle/vanilla/app-vanilla.aab" "$DIST/jellyfin-android-transcoder-$VERSION.aab"

plugin_sha="$(sha256sum "$DIST/Jellyfin.Plugin.AndroidTranscoder-$VERSION.zip" | awk '{print $1}')"
plugin_size="$(stat -c '%s' "$DIST/Jellyfin.Plugin.AndroidTranscoder-$VERSION.zip")"
cat > "$DIST/manifest.json" <<JSON
[
  {
    "guid": "7fd06ed7-4044-4d38-a6e3-8b4432ff8f8c",
    "name": "Android Transcoder",
    "description": "Routes eligible Jellyfin transcodes to an Android MediaCodec FFmpeg worker.",
    "overview": "Android MediaCodec transcode bridge for Jellyfin.",
    "owner": "doctorpangloss",
    "category": "Transcoding",
    "versions": [
      {
        "version": "$VERSION.0",
        "changelog": "Initial public release.",
        "targetAbi": "10.11.6.0",
        "sourceUrl": "https://github.com/doctorpangloss/jellyfin-android-transcoder/releases/download/v$VERSION/Jellyfin.Plugin.AndroidTranscoder-$VERSION.zip",
        "checksum": "$plugin_sha",
        "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
      }
    ]
  }
]
JSON

(cd "$DIST" && sha256sum * > SHA256SUMS)

printf 'Release artifacts written to %s\n' "$DIST"
find "$DIST" -maxdepth 1 -type f -printf '%s %p\n' | sort -nr
