# Jellyfin Android Transcoder

Android MediaCodec transcode bridge for Jellyfin.

This repository contains the deployable Android worker app and Jellyfin plugin/shim. The patched FFmpeg source lives in a separate public fork, and the full Jellyfin + Android emulator validation lives in a separate integration repository.

Related repositories:

- Android worker + Jellyfin plugin: https://github.com/doctorpangloss/jellyfin-android-transcoder
- Patched FFmpeg fork: https://github.com/doctorpangloss/forks-ffmpeg-android
- Integration tests: https://github.com/doctorpangloss/jellyfin-android-transcoder-integration

## What It Does

Jellyfin still invokes an FFmpeg-shaped executable. The plugin installs `jfat-ffmpeg` as a shim, and the shim preserves Jellyfin's normal HLS output contract while routing eligible HEVC/AV1 video transcodes to an Android foreground service.

The Android service exposes:

- `GET /api/v1/status`
- `POST /api/v1/remoteprocesses`

The remote process endpoint starts bundled patched FFmpeg, streams input into FFmpeg stdin, and streams completed HLS files back to the shim as `multipart/mixed`. Unsupported Jellyfin commands fall back to the configured real FFmpeg path.

## Release Assets

The `v1.0.0` release publishes:

- `jellyfin-android-transcoder-1.0.0.apk`: direct sideload APK.
- `jellyfin-android-transcoder-1.0.0.aab`: Android App Bundle for bundletool/Play-style installs.
- `Jellyfin.Plugin.AndroidTranscoder-1.0.0.zip`: Jellyfin plugin zip.
- `manifest.json`: Jellyfin plugin repository manifest.
- `SHA256SUMS`: release checksums.

The Android artifact includes native FFmpeg payloads for `arm64-v8a`, `armeabi-v7a`, `x86`, and `x86_64`.

## Android Install

### Direct Phone Install

Download the APK on the Android phone. This is the file to install:

```text
https://github.com/doctorpangloss/jellyfin-android-transcoder/releases/latest/download/jellyfin-android-transcoder-1.0.0.apk
```

Scan this QR code on the phone to open the APK download:

![QR code for latest Android APK](docs/assets/android-apk-latest-qr.png)

After the APK downloads:

1. Open the downloaded `jellyfin-android-transcoder-1.0.0.apk` from the browser downloads list or the Android **Files** app.
2. If Android says the browser or Files app is not allowed to install unknown apps, tap **Settings** on that prompt.
3. Enable **Allow from this source** for the app you used to open the APK, such as Chrome, Firefox, GitHub, or Files.
4. Go back to the APK installer.
5. If Play Protect appears, expand **More details** if needed and choose **Install anyway** or **Continue**. A message like **Play Protect is already turned on** is not the final install confirmation.
6. Tap **Install**.
7. After installation, open **Android Transcoder**.
8. Enable **Start on boot** and **Keep screen and Wi-Fi awake**.
9. Tap **Start Service**.
10. Confirm Jellyfin can reach the phone by opening `http://PHONE_IP:8098/api/v1/status` from the Jellyfin server or container network.

If Android returns to the downloads screen without showing **App installed**, the APK was not installed. Reopen the APK and finish the prompts above.

ADB install:

```bash
adb install -r jellyfin-android-transcoder-1.0.0.apk
adb shell monkey -p com.hiddenswitch.androidtranscoder 1
```

Bundletool install from the AAB:

```bash
java -jar bundletool-all-1.18.3.jar build-apks \
  --bundle jellyfin-android-transcoder-1.0.0.aab \
  --output jellyfin-android-transcoder-1.0.0.apks \
  --mode universal \
  --overwrite

java -jar bundletool-all-1.18.3.jar install-apks \
  --apks jellyfin-android-transcoder-1.0.0.apks \
  --device-id <adb-device-id>
```

The app must be reachable from the Jellyfin container. On Tailscale, use the phone's Tailscale IP in the plugin JSON or Jellyfin plugin settings.

## Jellyfin Docker Compose Example

```yaml
services:
  jellyfin:
    image: jellyfin/jellyfin:10.11.6
    container_name: jellyfin
    network_mode: bridge
    extra_hosts:
      - "host.docker.internal:host-gateway"
    ports:
      - "8096:8096"
    volumes:
      - ./jellyfin-config:/config
      - ./jellyfin-cache:/cache
      - /path/to/media:/media:ro
    restart: unless-stopped
```

Install the plugin:

1. In Jellyfin, go to **Dashboard -> Plugins -> Repositories**.
2. Add the repository URL:

   ```text
   https://github.com/doctorpangloss/jellyfin-android-transcoder/releases/download/v1.0.0/manifest.json
   ```

3. Install **Android Transcoder** from the catalog, or manually unpack `Jellyfin.Plugin.AndroidTranscoder-1.0.0.zip` into:

   ```text
   ./jellyfin-config/plugins/Android Transcoder_1.0.0/
   ```

4. Restart Jellyfin.
5. Open **Dashboard -> Plugins -> Android Transcoder**.
6. Paste the Android app JSON and save.
7. Click **Test Connection**.
8. Click **Install Shim**.
9. Click **Use Shim FFmpeg**.
10. Restart Jellyfin once more after changing the FFmpeg path.

The plugin writes the shim to:

```text
/config/plugins/Jellyfin.Plugin.AndroidTranscoder/shim/jfat-ffmpeg
```

and writes shim config beside it as `shim-config.json`.

## Build Locally

Prerequisites:

- .NET SDK 9
- JDK 21
- Android SDK
- Android NDK r27d if rebuilding FFmpeg
- `zip`

Build patched FFmpeg payloads:

```bash
FFMPEG_SRC=/home/administrator/Documents/forks-ffmpeg-android \
ANDROID_NDK_ROOT=/home/administrator/android-ndk/android-ndk-r27d \
./scripts/build-android-ffmpeg.sh
```

Build release assets:

```bash
VERSION=1.0.0 ANDROID_HOME="$HOME/Android/Sdk" ./scripts/package-release.sh
```

Artifacts are written to `dist/`.

## Test

Component tests:

```bash
dotnet test JellyfinAndroidTranscoder.sln --nologo
cd android-transcoder
ANDROID_HOME="$HOME/Android/Sdk" ./gradlew :app:connectedVanillaAndroidTest
```

Full integration tests are in:

```text
https://github.com/doctorpangloss/jellyfin-android-transcoder-integration
```

They start Jellyfin via Testcontainers, start an Android emulator, install the app, install/configure the plugin, add a 1 GiB HEVC fixture, and fetch browser-visible HLS through Jellyfin.
