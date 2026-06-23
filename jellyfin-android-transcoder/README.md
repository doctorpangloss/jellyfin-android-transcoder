# Jellyfin Plugin And Shim

This directory contains the Jellyfin side of the Android transcoder bridge.

Projects:

- `src/Jellyfin.Plugin.AndroidTranscoder`: Jellyfin 10.11 plugin with configuration page and shim installer.
- `src/JellyfinAndroidTranscoder.Shim`: self-contained Linux x64 executable named `jfat-ffmpeg`.
- `tests/JellyfinAndroidTranscoder.Shim.Tests`: shim unit/contract tests.

The plugin zip is built by the repository-level release script:

```bash
VERSION=1.0.0 ./scripts/package-release.sh
```

Release zip:

```text
dist/Jellyfin.Plugin.AndroidTranscoder-1.0.0.zip
```

Install that zip through Jellyfin's plugin catalog manifest or manually unpack it into Jellyfin's `/config/plugins` volume.

The shim receives Jellyfin's FFmpeg arguments, preserves Jellyfin's HLS playlist/list options, injects `hls_flags=temp_file`, and routes eligible HEVC/AV1 video transcodes to the Android app through `/api/v1/remoteprocesses`.

## Configure In Jellyfin

After installing the plugin and restarting Jellyfin, open **Dashboard -> Plugins -> Android Transcoder**. The plugin page redirects to `/AndroidTranscoder/Page`, which renders the current QR code and tests the Android worker with a 1 second health check.

Normal setup:

1. Install and open the Android Transcoder APK on the phone.
2. In Jellyfin, open the Android Transcoder plugin page.
3. On the phone, tap **Pair from QR**.
4. Scan the QR code shown by Jellyfin.
5. Click **Refresh status** in Jellyfin and confirm the page shows **Connected**.

Manual setup:

1. In the Android app, tap **Copy setup URL**.
2. Paste the single URL into the Jellyfin **Manual setup** field. It includes the phone address and token, for example `http://PHONE_IP:8098/?token=1234`.
3. Click **Use setup URL**.
4. Click **Refresh status** and confirm **Connected**.

The only user-facing options are:

- **Use this phone for video transcodes**
- **Use Android hardware codecs**

Screenshots are in the repository-level `docs/assets/` directory.
