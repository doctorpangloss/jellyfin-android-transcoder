# Jellyfin Android Transcoder

Android MediaCodec transcode bridge for Jellyfin.

This repository contains:

- `android-transcoder/`: Android APK that runs the patched Android FFmpeg build as a foreground HTTP service.
- `jellyfin-android-transcoder/`: Jellyfin plugin plus `jfat-ffmpeg` shim. Jellyfin calls the shim as FFmpeg; supported HEVC/AV1 video transcodes are routed to Android hardware encode, while unsupported commands fall back to Jellyfin's normal FFmpeg.

Build:

```bash
dotnet build JellyfinAndroidTranscoder.sln
dotnet test JellyfinAndroidTranscoder.sln
cd android-transcoder
ANDROID_HOME="$HOME/Android/Sdk" JAVA_HOME=/usr/lib/jvm/java-21-openjdk-amd64 ./gradlew assembleDebug
```

Build the Android App Bundle:

```bash
FFMPEG_SRC=/path/to/forks-ffmpeg-android \
  ANDROID_NDK_ROOT=/path/to/android-ndk-r27d \
  scripts/build-android-ffmpeg.sh

cd android-transcoder
ANDROID_HOME="$HOME/Android/Sdk" JAVA_HOME=/usr/lib/jvm/java-21-openjdk-amd64 ./gradlew bundleDebug
ANDROID_HOME="$HOME/Android/Sdk" JAVA_HOME=/usr/lib/jvm/java-21-openjdk-amd64 ./gradlew bundleRelease
```

The bundles are written to:

```text
android-transcoder/app/build/outputs/bundle/debug/app-debug.aab
android-transcoder/app/build/outputs/bundle/release/app-release.aab
```

The AAB includes native splits for `arm64-v8a`, `armeabi-v7a`, `x86`, and
`x86_64`. This is the Google-preferred publishing format. For direct installs,
generate APK sets with `bundletool`; users do not install an `.aab` directly.
The FFmpeg payload is a single PIE executable per ABI packaged through the
normal Android native-library layout. FFmpeg libraries are linked into that
executable, so the app does not depend on `LD_LIBRARY_PATH` or private sibling
shared-library lookup. The build uses 16 KB page-size linker flags. ARM and
`x86_64` builds keep assembly optimizations; 32-bit x86 uses a C fallback.

Test the bridge:

```bash
dotnet test JellyfinAndroidTranscoder.sln
cd android-transcoder
ANDROID_HOME="$HOME/Android/Sdk" JAVA_HOME=/usr/lib/jvm/java-21-openjdk-amd64 ./gradlew assembleDebug assembleDebugAndroidTest
ANDROID_HOME="$HOME/Android/Sdk" JAVA_HOME=/usr/lib/jvm/java-21-openjdk-amd64 ./gradlew connectedDebugAndroidTest
```

The .NET tests mock the Android HTTP service and verify the Jellyfin FFmpeg shim
posts the expected request. The Android instrumentation tests install the APK on
a connected device, start the foreground service, and make Jellyfin-style HTTP
calls to `127.0.0.1:8098`.

The APK is written to:

```text
android-transcoder/app/build/outputs/apk/debug/app-debug.apk
```

The Android app shows the host, port, and bearer-token JSON needed by the Jellyfin plugin.
