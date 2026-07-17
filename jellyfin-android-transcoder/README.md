# Jellyfin Plugin And Shim

This directory contains the Jellyfin side of the Android transcoder bridge.

Projects:

- `src/Jellyfin.Plugin.AndroidTranscoder`: Jellyfin 10.11 plugin with configuration page and shim installer.
- `src/JellyfinAndroidTranscoder.Shim`: self-contained Linux x64 executable named `jfat-ffmpeg`.
- `tests/JellyfinAndroidTranscoder.Shim.Tests`: shim unit/contract tests.

The plugin zip is built by the repository-level release script:

```bash
VERSION=1.1.13 ./scripts/package-release.sh
```

Release zip:

```text
dist/Jellyfin.Plugin.AndroidTranscoder-1.1.13.zip
```

Install that zip through Jellyfin's plugin catalog manifest or manually unpack it into Jellyfin's `/config/plugins` volume.

The shim receives Jellyfin's FFmpeg arguments, preserves Jellyfin's HLS playlist/list options, injects `hls_flags=temp_file`, and routes eligible HEVC/AV1 video transcodes to the Android app through `/api/v1/remoteprocesses`.

## Configure In Jellyfin

1. Install and open the Android Transcoder APK.
2. Open **Dashboard -> Plugins -> Android Transcoder** in Jellyfin.
3. Tap **Pair from QR** on the phone and scan the code shown by Jellyfin.
4. Click **Refresh status** and confirm **Connected**.

The illustrated install is in the repository-level README.
