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

The APK is written to:

```text
android-transcoder/app/build/outputs/apk/debug/app-debug.apk
```

The Android app shows the host, port, and bearer-token JSON needed by the Jellyfin plugin.
