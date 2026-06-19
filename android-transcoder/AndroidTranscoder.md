# HiddenSwitch Android Transcoder

This APK runs the patched Android FFmpeg build as a foreground service and
exposes a small token-protected HTTP API for Jellyfin.

Build:

```bash
./gradlew assembleDebug
```

Install:

```bash
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

Open the app, start the service, then copy the JSON connection block into the
Jellyfin Android Transcoder plugin page.
