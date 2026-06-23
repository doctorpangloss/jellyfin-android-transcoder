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

Open the app and leave it running. The service starts automatically.

Recommended setup:

1. Install the Jellyfin Android Transcoder plugin on the Jellyfin server.
2. Open the plugin configuration page in Jellyfin.
3. Tap **Pair from QR** in the Android app and scan the QR code shown by Jellyfin.
4. Press **Refresh status** on the Jellyfin page to confirm the phone is connected.

Manual setup is also available. Tap the setup URL in the Android app to copy it,
then paste that single URL into the Jellyfin plugin page.
