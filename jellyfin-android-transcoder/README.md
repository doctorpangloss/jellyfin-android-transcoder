# Jellyfin Android Transcoder

This is the Jellyfin side of the Android hardware-transcode bridge.

Components:

- `JellyfinAndroidTranscoder.Shim`: a self-contained Linux x64 executable named `jfat-ffmpeg`. Jellyfin calls it as its FFmpeg binary. It inspects Jellyfin's FFmpeg arguments and routes supported HEVC/AV1 video transcodes to the Android service, while local FFmpeg still handles probing, audio packaging, HLS output, and unsupported fallbacks.
- `Jellyfin.Plugin.AndroidTranscoder`: a Jellyfin 10.11 plugin with an admin page for the Android endpoint, bearer token, shim installation, and FFmpeg path switching.
- `shim-payload/jfat-ffmpeg`: the published shim executable bundled into the plugin project so the plugin can install it into Jellyfin's config volume.

Expected Android endpoint:

```http
GET /api/v1/status
POST /api/v1/transcode?codec=h264&width=1920&height=1080&bitrate=6000000
Authorization: Bearer <token>
```

The Android service receives a video-only Matroska stream and returns H.264
MPEG-TS. The shim then remuxes that video with Jellyfin-selected audio into the
HLS/DASH output Jellyfin requested.

Build and test:

```bash
dotnet build ../JellyfinAndroidTranscoder.sln
dotnet test ../JellyfinAndroidTranscoder.sln
dotnet publish src/JellyfinAndroidTranscoder.Shim/JellyfinAndroidTranscoder.Shim.csproj -c Release -r linux-x64 --self-contained true -o /tmp/jfat-shim-publish
```

The plugin currently targets Jellyfin 10.11.6 package APIs.
