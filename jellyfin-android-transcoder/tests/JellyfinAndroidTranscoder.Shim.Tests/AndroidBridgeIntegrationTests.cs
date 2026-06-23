using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using JellyfinAndroidTranscoder.Shim;

namespace JellyfinAndroidTranscoder.Shim.Tests;

public sealed class AndroidBridgeIntegrationTests : IDisposable
{
    private static readonly SemaphoreSlim PublishLock = new(1, 1);
    private static string? s_shimExecutable;

    private readonly string _tempDir = Path.Combine(RepositoryRoot(), ".work", "shim-tests", Guid.NewGuid().ToString("N"));
    private readonly string? _previousConfig = Environment.GetEnvironmentVariable("JFAT_CONFIG");
    private readonly string? _previousStartupTimeout = Environment.GetEnvironmentVariable("JFAT_REMOTE_STARTUP_TIMEOUT_SECONDS");

    public AndroidBridgeIntegrationTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task JellyfinStyleTranscodeStartsRemoteProcessAndMaterializesMultipartFiles()
    {
        var ffmpeg = WriteExecutable("fake-ffmpeg.sh", """
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$JFAT_FAKE_FFMPEG_LOG"
exit 99
""");
        var ffprobe = WriteExecutable("fake-ffprobe.sh", """
#!/usr/bin/env bash
set -euo pipefail
cat <<'JSON'
{"streams":[{"codec_name":"hevc","pix_fmt":"yuv420p10le","width":320,"height":180,"bit_rate":"6000000","color_space":"bt2020nc","color_transfer":"smpte2084","color_primaries":"bt2020"}],"format":{"duration":"12.345"}}
JSON
""");
        var ffmpegLog = Path.Combine(_tempDir, "ffmpeg.log");
        Environment.SetEnvironmentVariable("JFAT_FAKE_FFMPEG_LOG", ffmpegLog);
        await File.WriteAllTextAsync(ffmpegLog, "");

        var input = Path.Combine(_tempDir, "movie.mkv");
        await File.WriteAllTextAsync(input, "placeholder-matroska");
        var output = Path.Combine(_tempDir, "segment.m3u8");

        await using var android = await MockAndroidService.Start(new Dictionary<string, byte[]>
        {
            ["segment0.ts"] = "mpegts-from-android"u8.ToArray(),
            ["segment.m3u8"] = "#EXTM3U\n#EXTINF:3.000,\nsegment0.ts\n#EXT-X-ENDLIST\n"u8.ToArray()
        });
        WriteConfig(android.BaseUrl, android.Token, ffmpeg, ffprobe, maxBitrate: 6_000_000);

        var exitCode = await Program.Main(JellyfinArgs(input, output));

        Assert.Equal(0, exitCode);
        Assert.Equal("", await File.ReadAllTextAsync(ffmpegLog));
        Assert.Contains("#EXTM3U", await File.ReadAllTextAsync(output));
        Assert.Contains("segment0.ts", await File.ReadAllTextAsync(output));
        Assert.Equal("mpegts-from-android", await File.ReadAllTextAsync(Path.Combine(_tempDir, "segment0.ts")));

        var request = await android.GetSingleRequest();
        Assert.Equal("/api/v1/remoteprocesses", request.Path);
        Assert.Equal("Bearer test-token", request.Authorization);
        Assert.Equal("application/octet-stream", request.ContentType);
        Assert.Equal("ffmpeg", request.Executable);
        Assert.Contains("{outputRoot}/segment%d.ts", request.RemoteArgs);
        Assert.Contains("{outputRoot}/segment.m3u8", request.RemoteArgs);
        Assert.Contains("\"-vf\",\"scale=320:180:flags=fast_bilinear\"", request.RemoteArgs);
        Assert.Contains("\"-g\",\"72\"", request.RemoteArgs);
        Assert.Contains("\"-hls_time\",\"3\"", request.RemoteArgs);
        Assert.Contains("\"-hls_flags\",\"temp_file\"", request.RemoteArgs);
        Assert.Contains("\"-hls_segment_type\",\"fmp4\"", request.RemoteArgs);
        Assert.Contains("\"-hls_playlist_type\",\"vod\"", request.RemoteArgs);
        Assert.Contains("\"-hls_list_size\",\"0\"", request.RemoteArgs);
        Assert.Equal(1, android.StatusRequestCount);
        Assert.Equal(2, CountOccurrences(request.RemoteArgs, "\"-t\",\"12.345\""));
        Assert.Equal("placeholder-matroska".Length, request.BodyLength);
        Assert.Equal("placeholder-matroska", Encoding.UTF8.GetString(request.BodyPrefix));

    }

    [Fact]
    public async Task JellyfinStyleTranscodeExecutableCreatesHlsFilesLikeFfmpeg()
    {
        var ffmpeg = WriteExecutable("fake-ffmpeg.sh", """
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$JFAT_FAKE_FFMPEG_LOG"
exit 99
""");
        var ffprobe = WriteExecutable("fake-ffprobe.sh", """
#!/usr/bin/env bash
set -euo pipefail
cat <<'JSON'
{"streams":[{"codec_name":"hevc","pix_fmt":"yuv420p10le","width":3840,"height":2160,"bit_rate":"60000000","color_space":"bt2020nc","color_transfer":"smpte2084","color_primaries":"bt2020"}],"format":{"duration":"7200"}}
JSON
""");
        var ffmpegLog = Path.Combine(_tempDir, "process-ffmpeg.log");
        Environment.SetEnvironmentVariable("JFAT_FAKE_FFMPEG_LOG", ffmpegLog);
        await File.WriteAllTextAsync(ffmpegLog, "");

        var input = Path.Combine(_tempDir, "movie.mkv");
        await File.WriteAllTextAsync(input, string.Concat(Enumerable.Repeat("streaming-input-", 1024)));
        var output = Path.Combine(_tempDir, "playlist.m3u8");

        await using var android = await MockAndroidService.Start(new Dictionary<string, byte[]>
        {
            ["playlist.m3u8"] = "#EXTM3U\n#EXTINF:3.000,\nsegment0.ts\n#EXT-X-ENDLIST\n"u8.ToArray(),
            ["segment0.ts"] = "mpegts-chunk-ampegts-chunk-b"u8.ToArray()
        });
        WriteConfig(android.BaseUrl, android.Token, ffmpeg, ffprobe, maxBitrate: 6_000_000);

        var shimExecutable = await BuildShimExecutable();
        using var process = ProcessUtil.Start(shimExecutable, JellyfinArgs(input, output), redirectInput: false, redirectOutput: false);
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", await File.ReadAllTextAsync(ffmpegLog));

        var segment = Path.Combine(_tempDir, "segment0.ts");
        Assert.True(File.Exists(output), "The shim must create Jellyfin's requested HLS playlist path.");
        Assert.True(File.Exists(segment), "The shim must create Jellyfin's requested HLS segment path.");
        Assert.Contains("#EXTM3U", await File.ReadAllTextAsync(output));
        Assert.Equal("mpegts-chunk-ampegts-chunk-b", await File.ReadAllTextAsync(segment));

        var request = await android.GetSingleRequest();
        Assert.Equal(File.ReadAllText(input).Length, request.BodyLength);
        Assert.StartsWith("streaming-input-", Encoding.UTF8.GetString(request.BodyPrefix));
    }

    [Fact]
    public async Task RemoteTempPlaylistIsMaterializedAtJellyfinPlaylistPath()
    {
        var ffmpeg = WriteExecutable("fake-ffmpeg.sh", """
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$JFAT_FAKE_FFMPEG_LOG"
exit 99
""");
        var ffprobe = WriteExecutable("fake-ffprobe.sh", """
#!/usr/bin/env bash
set -euo pipefail
cat <<'JSON'
{"streams":[{"codec_name":"hevc","pix_fmt":"yuv420p10le","width":320,"height":180,"bit_rate":"6000000","color_space":"bt709","color_transfer":"bt709","color_primaries":"bt709"}],"format":{"duration":"60"}}
JSON
""");
        var ffmpegLog = Path.Combine(_tempDir, "temp-playlist-ffmpeg.log");
        Environment.SetEnvironmentVariable("JFAT_FAKE_FFMPEG_LOG", ffmpegLog);
        await File.WriteAllTextAsync(ffmpegLog, "");

        var input = Path.Combine(_tempDir, "movie.mkv");
        await File.WriteAllTextAsync(input, string.Concat(Enumerable.Repeat("streaming-input-", 1024)));
        var output = Path.Combine(_tempDir, "playlist.m3u8");

        await using var android = await MockAndroidService.Start(new Dictionary<string, byte[]>
        {
            ["playlist.m3u8.tmp"] = "#EXTM3U\n#EXT-X-MAP:URI=\"playlist-1.mp4\"\n#EXTINF:1.000,\nplaylist0.mp4\n"u8.ToArray(),
            ["playlist-1.mp4"] = "fmp4-init"u8.ToArray(),
            ["playlist0.mp4"] = "fmp4-media"u8.ToArray()
        });
        WriteConfig(android.BaseUrl, android.Token, ffmpeg, ffprobe, maxBitrate: 6_000_000);

        var exitCode = await Program.Main(JellyfinArgs(input, output));

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(output), "The shim must expose FFmpeg's temp playlist at Jellyfin's requested .m3u8 path.");
        Assert.Contains("#EXTM3U", await File.ReadAllTextAsync(output));
        Assert.False(File.Exists(output + ".tmp"), "The temp playlist name is an FFmpeg implementation detail and must not leak to Jellyfin.");
    }

    [Fact]
    public async Task StartupTimeoutDoesNotCancelLongRunningRemoteStreams()
    {
        Environment.SetEnvironmentVariable("JFAT_REMOTE_STARTUP_TIMEOUT_SECONDS", "1");
        var ffmpeg = WriteExecutable("fake-ffmpeg-timeout.sh", """
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$JFAT_FAKE_FFMPEG_LOG"
exit 99
""");
        var ffprobe = WriteExecutable("fake-ffprobe-timeout.sh", """
#!/usr/bin/env bash
set -euo pipefail
cat <<'JSON'
{"streams":[{"codec_name":"hevc","pix_fmt":"yuv420p10le","width":3840,"height":2160,"bit_rate":"60000000","color_space":"bt709","color_transfer":"bt709","color_primaries":"bt709"}],"format":{"duration":"7200"}}
JSON
""");
        var ffmpegLog = Path.Combine(_tempDir, "timeout-fallback.log");
        Environment.SetEnvironmentVariable("JFAT_FAKE_FFMPEG_LOG", ffmpegLog);
        await File.WriteAllTextAsync(ffmpegLog, "");

        var input = Path.Combine(_tempDir, "large-timeout-window.mkv");
        await using (var file = File.Create(input))
        {
            file.SetLength(64L * 1024 * 1024);
        }
        var output = Path.Combine(_tempDir, "timeout.m3u8");

        await using var android = await MockAndroidService.Start(new Dictionary<string, byte[]>
        {
            ["timeout0.ts"] = "mpegts-timeout-window"u8.ToArray(),
            ["timeout.m3u8"] = "#EXTM3U\n#EXTINF:3.000,\ntimeout0.ts\n#EXT-X-ENDLIST\n"u8.ToArray()
        }, delayFilesAfterReady: TimeSpan.FromMilliseconds(1500));
        WriteConfig(android.BaseUrl, android.Token, ffmpeg, ffprobe, maxBitrate: 6_000_000);

        var exitCode = await Program.Main([
            "-i", $"file:{input}", "-codec:v:0", "libx264", "-maxrate", "63810668", "-bufsize", "127621336",
            "-vf", @"scale=trunc(min(max(iw\,ih*a)\,1920)/2)*2:trunc(ow/a/2)*2",
            "-f", "hls", "-hls_time", "3", "-hls_segment_type", "mpegts",
            "-hls_segment_filename", Path.Combine(_tempDir, "timeout%d.ts"),
            "-hls_playlist_type", "vod", "-hls_list_size", "0", "-y", output
        ]);

        Assert.Equal(0, exitCode);
        Assert.Equal("", await File.ReadAllTextAsync(ffmpegLog));
        Assert.Contains("#EXTM3U", await File.ReadAllTextAsync(output));
        Assert.Equal("mpegts-timeout-window", await File.ReadAllTextAsync(Path.Combine(_tempDir, "timeout0.ts")));
    }

    [Fact]
    public async Task JellyfinStopCommandCancelsRemoteJobById()
    {
        var ffmpeg = WriteExecutable("fake-ffmpeg.sh", """
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$JFAT_FAKE_FFMPEG_LOG"
exit 99
""");
        var ffprobe = WriteExecutable("fake-ffprobe.sh", """
#!/usr/bin/env bash
set -euo pipefail
cat <<'JSON'
{"streams":[{"codec_name":"hevc","pix_fmt":"yuv420p10le","width":3840,"height":2160,"bit_rate":"60000000","color_space":"bt709","color_transfer":"bt709","color_primaries":"bt709"}],"format":{"duration":"7200"}}
JSON
""");
        var ffmpegLog = Path.Combine(_tempDir, "q-fallback.log");
        Environment.SetEnvironmentVariable("JFAT_FAKE_FFMPEG_LOG", ffmpegLog);
        await File.WriteAllTextAsync(ffmpegLog, "");

        var input = Path.Combine(_tempDir, "movie.mkv");
        await File.WriteAllTextAsync(input, string.Concat(Enumerable.Repeat("streaming-input-", 1024 * 1024)));
        var output = Path.Combine(_tempDir, "playlist.m3u8");

        await using var android = await MockAndroidService.Start(
            new Dictionary<string, byte[]>(),
            holdFilesStreamOpen: true);
        WriteConfig(android.BaseUrl, android.Token, ffmpeg, ffprobe, maxBitrate: 6_000_000);

        var shimExecutable = await BuildShimExecutable();
        using var process = StartShimForControlTest(shimExecutable, JellyfinArgs(input, output));
        var stderr = process.StandardError.ReadToEndAsync();
        await android.WaitForFilesStream();
        await process.StandardInput.WriteLineAsync("q");
        await process.StandardInput.FlushAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (TaskCanceledException ex)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"Shim did not exit after stdin q; stderr: {await stderr}; deletes: {string.Join(",", android.DeletePaths)}",
                ex);
        }

        Assert.True(
            android.DeletePaths.Contains("/api/v1/remoteprocesses/job-1"),
            $"Expected shim to cancel job-1 after stdin q; stderr: {await stderr}; fallback log: {await File.ReadAllTextAsync(ffmpegLog)}; deletes: {string.Join(",", android.DeletePaths)}");
        Assert.DoesNotContain("/api/v1/remoteprocesses/current", android.DeletePaths);
        Assert.True(
            process.ExitCode == 0 || process.ExitCode == 255,
            $"Unexpected shim exit code {process.ExitCode}; fallback log: {await File.ReadAllTextAsync(ffmpegLog)}; deletes: {string.Join(",", android.DeletePaths)}");
    }

    [Fact]
    public async Task JellyfinTerminateSignalCancelsRemoteJobByIdAndDoesNotFallback()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var ffmpeg = WriteExecutable("fake-ffmpeg-signal.sh", """
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$JFAT_FAKE_FFMPEG_LOG"
exit 99
""");
        var ffprobe = WriteExecutable("fake-ffprobe-signal.sh", """
#!/usr/bin/env bash
set -euo pipefail
cat <<'JSON'
{"streams":[{"codec_name":"hevc","pix_fmt":"yuv420p10le","width":3840,"height":2160,"bit_rate":"60000000","color_space":"bt709","color_transfer":"bt709","color_primaries":"bt709"}],"format":{"duration":"7200"}}
JSON
""");
        var ffmpegLog = Path.Combine(_tempDir, "signal-fallback.log");
        Environment.SetEnvironmentVariable("JFAT_FAKE_FFMPEG_LOG", ffmpegLog);
        await File.WriteAllTextAsync(ffmpegLog, "");

        var input = Path.Combine(_tempDir, "movie-signal.mkv");
        await File.WriteAllTextAsync(input, string.Concat(Enumerable.Repeat("streaming-input-", 1024 * 1024)));
        var output = Path.Combine(_tempDir, "playlist-signal.m3u8");

        await using var android = await MockAndroidService.Start(
            new Dictionary<string, byte[]>(),
            holdFilesStreamOpen: true);
        WriteConfig(android.BaseUrl, android.Token, ffmpeg, ffprobe, maxBitrate: 6_000_000);

        var shimExecutable = await BuildShimExecutable();
        using var process = StartShimForControlTest(shimExecutable, JellyfinArgs(input, output));
        var stderr = process.StandardError.ReadToEndAsync();
        await android.WaitForFilesStream();

        using (var kill = Process.Start("kill", ["-TERM", process.Id.ToString()])!)
        {
            await kill.WaitForExitAsync();
            Assert.Equal(0, kill.ExitCode);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (TaskCanceledException ex)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"Shim did not exit after SIGTERM; stderr: {await stderr}; deletes: {string.Join(",", android.DeletePaths)}",
                ex);
        }

        Assert.True(
            android.DeletePaths.Contains("/api/v1/remoteprocesses/job-1"),
            $"Expected shim to cancel job-1 after SIGTERM; stderr: {await stderr}; fallback log: {await File.ReadAllTextAsync(ffmpegLog)}; deletes: {string.Join(",", android.DeletePaths)}");
        Assert.DoesNotContain("/api/v1/remoteprocesses/current", android.DeletePaths);
        Assert.Equal(143, process.ExitCode);
        Assert.Equal("", await File.ReadAllTextAsync(ffmpegLog));
    }

    [Fact]
    public async Task InceptionSafariCommandRoutesAsFmp4At1080pSixMegabit()
    {
        var ffmpeg = WriteExecutable("fake-ffmpeg.sh", """
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$JFAT_FAKE_FFMPEG_LOG"
exit 99
""");
        var ffprobe = WriteExecutable("fake-ffprobe.sh", """
#!/usr/bin/env bash
set -euo pipefail
cat <<'JSON'
{"streams":[{"codec_name":"hevc","pix_fmt":"yuv420p10le","width":3840,"height":2160,"bit_rate":"60000000","color_space":"bt2020nc","color_transfer":"smpte2084","color_primaries":"bt2020"}],"format":{"duration":"9.5"}}
JSON
""");
        var ffmpegLog = Path.Combine(_tempDir, "inception-fallback.log");
        Environment.SetEnvironmentVariable("JFAT_FAKE_FFMPEG_LOG", ffmpegLog);
        await File.WriteAllTextAsync(ffmpegLog, "");

        var input = Path.Combine(_tempDir, "Inception (2010) Remux-2160p.mkv");
        await File.WriteAllTextAsync(input, string.Concat(Enumerable.Repeat("inception-hevc-data-", 1024)));
        var output = Path.Combine(_tempDir, "70e040ca627b1a5a2ecb0618aa77f67c.m3u8");
        var init = Path.Combine(_tempDir, "70e040ca627b1a5a2ecb0618aa77f67c-1.mp4");
        var segment = Path.Combine(_tempDir, "70e040ca627b1a5a2ecb0618aa77f67c0.mp4");

        await using var android = await MockAndroidService.Start(new Dictionary<string, byte[]>
        {
            ["70e040ca627b1a5a2ecb0618aa77f67c-1.mp4"] = "fmp4-init"u8.ToArray(),
            ["70e040ca627b1a5a2ecb0618aa77f67c0.mp4"] = "fmp4-media"u8.ToArray(),
            ["70e040ca627b1a5a2ecb0618aa77f67c.m3u8"] =
                "#EXTM3U\n#EXT-X-MAP:URI=\"70e040ca627b1a5a2ecb0618aa77f67c-1.mp4\"\n#EXTINF:1.000,\n70e040ca627b1a5a2ecb0618aa77f67c0.mp4\n#EXT-X-ENDLIST\n"u8.ToArray()
        });
        WriteConfig(android.BaseUrl, android.Token, ffmpeg, ffprobe, maxBitrate: 6_000_000);

        var exitCode = await Program.Main(InceptionSafariArgs(input, output));

        Assert.Equal(0, exitCode);
        Assert.Equal("", await File.ReadAllTextAsync(ffmpegLog));
        Assert.Equal("fmp4-init", await File.ReadAllTextAsync(init));
        Assert.Equal("fmp4-media", await File.ReadAllTextAsync(segment));

        var request = await android.GetSingleRequest();
        Assert.Equal(1, android.StatusRequestCount);
        Assert.Contains("\"-vf\",\"scale=1920:1080:flags=fast_bilinear\"", request.RemoteArgs);
        Assert.Contains("\"-b:v\",\"6000000\"", request.RemoteArgs);
        Assert.Contains("\"-maxrate\",\"6000000\"", request.RemoteArgs);
        Assert.Contains("\"-bufsize\",\"12000000\"", request.RemoteArgs);
        Assert.DoesNotContain("63810668", request.RemoteArgs);
        Assert.DoesNotContain("127621336", request.RemoteArgs);
        Assert.DoesNotContain("\"-hwaccel\",\"mediacodec\"", request.RemoteArgs);
        Assert.DoesNotContain("\"-hwaccel_output_format\",\"mediacodec\"", request.RemoteArgs);
        Assert.Contains("\"-c:v\",\"h264_mediacodec\"", request.RemoteArgs);
        Assert.Contains("\"-hls_segment_type\",\"fmp4\"", request.RemoteArgs);
        Assert.Contains("\"-hls_fmp4_init_filename\",\"70e040ca627b1a5a2ecb0618aa77f67c-1.mp4\"", request.RemoteArgs);
        Assert.Contains("\"-hls_segment_options\",\"movflags=\\u002Bfrag_discont\"", request.RemoteArgs);
        Assert.Contains("\"-hls_segment_filename\",\"{outputRoot}/70e040ca627b1a5a2ecb0618aa77f67c%d.mp4\"", request.RemoteArgs);
        Assert.Contains("\"-y\",\"{outputRoot}/70e040ca627b1a5a2ecb0618aa77f67c.m3u8\"", request.RemoteArgs);
    }

    [Fact]
    public async Task JellyfinFollowupSegmentSeekIsForwardedBeforeRemoteInput()
    {
        var ffmpeg = WriteExecutable("fake-ffmpeg-seek.sh", """
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$JFAT_FAKE_FFMPEG_LOG"
exit 99
""");
        var ffprobe = WriteExecutable("fake-ffprobe-seek.sh", """
#!/usr/bin/env bash
set -euo pipefail
cat <<'JSON'
{"streams":[{"codec_name":"hevc","pix_fmt":"yuv420p","width":320,"height":180,"bit_rate":"2500000","color_space":"bt709","color_transfer":"bt709","color_primaries":"bt709"}],"format":{"duration":"3600"}}
JSON
""");
        var ffmpegLog = Path.Combine(_tempDir, "seek-fallback.log");
        Environment.SetEnvironmentVariable("JFAT_FAKE_FFMPEG_LOG", ffmpegLog);
        await File.WriteAllTextAsync(ffmpegLog, "");

        var input = Path.Combine(_tempDir, "movie.mkv");
        await File.WriteAllTextAsync(input, string.Concat(Enumerable.Repeat("seek-window-input-", 4096)));
        var output = Path.Combine(_tempDir, "seek.m3u8");

        await using var android = await MockAndroidService.Start(new Dictionary<string, byte[]>
        {
            ["seek11.ts"] = "seek-media"u8.ToArray(),
            ["seek.m3u8"] = "#EXTM3U\n#EXTINF:3.000,\nseek11.ts\n#EXT-X-ENDLIST\n"u8.ToArray()
        });
        WriteConfig(android.BaseUrl, android.Token, ffmpeg, ffprobe, maxBitrate: 600_000);

        var exitCode = await Program.Main([
            "-analyzeduration", "200M", "-probesize", "1G", "-ss", "00:00:33.000", "-i", $"file:{input}",
            "-map_metadata", "-1", "-map_chapters", "-1", "-threads", "0",
            "-map", "0:0", "-map", "-0:a", "-map", "-0:s",
            "-codec:v:0", "libx264", "-preset", "veryfast", "-crf", "23",
            "-maxrate", "472000", "-bufsize", "944000",
            "-force_key_frames:0", "expr:gte(t,n_forced*3)", "-sc_threshold:v:0", "0",
            "-vf", @"scale=trunc(min(max(iw\,ih*a)\,960)/2)*2:trunc(ow/a/2)*2,format=yuv420p",
            "-copyts", "-avoid_negative_ts", "disabled", "-max_muxing_queue_size", "2048",
            "-f", "hls", "-max_delay", "5000000", "-hls_time", "3", "-hls_segment_type", "mpegts",
            "-start_number", "11", "-hls_segment_filename", Path.Combine(_tempDir, "seek%d.ts"),
            "-hls_playlist_type", "vod", "-hls_list_size", "0", "-y", output
        ]);

        Assert.Equal(0, exitCode);
        Assert.Equal("", await File.ReadAllTextAsync(ffmpegLog));
        var request = await android.GetSingleRequest();
        Assert.Contains("\"-i\",\"{input}\",\"-ss\",\"00:00:33.000\"", request.RemoteArgs);
        Assert.Contains("\"-start_number\",\"11\"", request.RemoteArgs);
        Assert.Contains("\"-hls_time\",\"3\"", request.RemoteArgs);
        Assert.Contains("\"-g\",\"72\"", request.RemoteArgs);
        Assert.DoesNotContain("\"-hwaccel\",\"mediacodec\"", request.RemoteArgs);
        Assert.Contains("\"-c:v\",\"h264_mediacodec\"", request.RemoteArgs);
    }

    [Fact]
    public async Task RemoteHttpFailureRetriesAndroidBeforeLocalFallback()
    {
        var ffmpeg = WriteExecutable("fake-ffmpeg.sh", """
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$JFAT_FAKE_FFMPEG_LOG"
exit 99
""");
        var ffprobe = WriteExecutable("fake-ffprobe.sh", """
#!/usr/bin/env bash
set -euo pipefail
cat <<'JSON'
{"streams":[{"codec_name":"hevc","pix_fmt":"yuv420p10le","width":3840,"height":2160,"bit_rate":"60000000","color_space":"bt2020nc","color_transfer":"smpte2084","color_primaries":"bt2020"}],"format":{"duration":"9.5"}}
JSON
""");
        var ffmpegLog = Path.Combine(_tempDir, "retry-fallback.log");
        Environment.SetEnvironmentVariable("JFAT_FAKE_FFMPEG_LOG", ffmpegLog);
        await File.WriteAllTextAsync(ffmpegLog, "");

        var input = Path.Combine(_tempDir, "movie.mkv");
        await File.WriteAllTextAsync(input, string.Concat(Enumerable.Repeat("large-streaming-input-", 4096)));
        var output = Path.Combine(_tempDir, "retry.m3u8");

        await using var android = await MockAndroidService.Start(new Dictionary<string, byte[]>
        {
            ["retry-1.mp4"] = "fmp4-init-after-retry"u8.ToArray(),
            ["retry0.mp4"] = "fmp4-media-after-retry"u8.ToArray(),
            ["retry.m3u8"] = "#EXTM3U\n#EXT-X-MAP:URI=\"retry-1.mp4\"\n#EXTINF:1.000,\nretry0.mp4\n#EXT-X-ENDLIST\n"u8.ToArray()
        }, failFirstRemoteRequest: true);
        WriteConfig(android.BaseUrl, android.Token, ffmpeg, ffprobe, maxBitrate: 6_000_000);

        var exitCode = await Program.Main([
            "-i", $"file:{input}", "-codec:v:0", "libx264", "-maxrate", "63810668", "-bufsize", "127621336",
            "-vf", @"scale=trunc(min(max(iw\,ih*a)\,1920)/2)*2:trunc(ow/a/2)*2",
            "-f", "hls", "-hls_time", "3", "-hls_segment_type", "fmp4",
            "-hls_fmp4_init_filename", "retry-1.mp4",
            "-hls_segment_filename", Path.Combine(_tempDir, "retry%d.mp4"),
            "-hls_playlist_type", "vod", "-hls_list_size", "0", "-y", output
        ]);

        Assert.Equal(0, exitCode);
        Assert.Equal("", await File.ReadAllTextAsync(ffmpegLog));
        Assert.Equal(2, android.RequestCount);
        Assert.Contains("#EXTM3U", await File.ReadAllTextAsync(output));
        Assert.Equal("fmp4-media-after-retry", await File.ReadAllTextAsync(Path.Combine(_tempDir, "retry0.mp4")));
    }

    [Fact]
    public async Task FailedJellyfinWindowCancelsOnlyItsOwnRemoteJob()
    {
        var ffmpeg = WriteExecutable("fake-ffmpeg.sh", """
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$JFAT_FAKE_FFMPEG_LOG"
exit 42
""");
        var ffprobe = WriteExecutable("fake-ffprobe.sh", """
#!/usr/bin/env bash
set -euo pipefail
cat <<'JSON'
{"streams":[{"codec_name":"hevc","pix_fmt":"yuv420p10le","width":3840,"height":2160,"bit_rate":"60000000","color_space":"bt2020nc","color_transfer":"smpte2084","color_primaries":"bt2020"}],"format":{"duration":"7200"}}
JSON
""");
        var ffmpegLog = Path.Combine(_tempDir, "cancel-fallback.log");
        Environment.SetEnvironmentVariable("JFAT_FAKE_FFMPEG_LOG", ffmpegLog);
        await File.WriteAllTextAsync(ffmpegLog, "");

        var input = Path.Combine(_tempDir, "movie.mkv");
        await File.WriteAllTextAsync(input, string.Concat(Enumerable.Repeat("overlap-window-input-", 4096)));
        var output = Path.Combine(_tempDir, "cancel.m3u8");

        await using var android = await MockAndroidService.Start(
            new Dictionary<string, byte[]>(),
            failFilesStream: true);
        WriteConfig(android.BaseUrl, android.Token, ffmpeg, ffprobe, maxBitrate: 6_000_000);

        var exitCode = await Program.Main(InceptionSafariArgs(input, output));

        Assert.Equal(42, exitCode);
        Assert.Contains("/api/v1/remoteprocesses/job-1", android.DeletePaths);
        Assert.DoesNotContain("/api/v1/remoteprocesses/current", android.DeletePaths);
    }

    [Fact]
    public async Task UnsupportedJellyfinCommandFallsBackWithoutCallingAndroid()
    {
        var ffmpeg = WriteExecutable("fake-ffmpeg.sh", """
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$JFAT_FAKE_FFMPEG_LOG"
exit 17
""");
        var ffprobe = WriteExecutable("fake-ffprobe.sh", """
#!/usr/bin/env bash
set -euo pipefail
echo '{"streams":[{"codec_name":"hevc","pix_fmt":"yuv420p10le","width":3840,"height":2160,"bit_rate":"60000000"}],"format":{"duration":"7200"}}'
""");
        var ffmpegLog = Path.Combine(_tempDir, "fallback-ffmpeg.log");
        Environment.SetEnvironmentVariable("JFAT_FAKE_FFMPEG_LOG", ffmpegLog);

        await using var android = await MockAndroidService.Start(new Dictionary<string, byte[]> { ["unused"] = "unused"u8.ToArray() });
        WriteConfig(android.BaseUrl, android.Token, ffmpeg, ffprobe, maxBitrate: 6_000_000);

        var input = Path.Combine(_tempDir, "movie.mkv");
        await File.WriteAllTextAsync(input, "placeholder");
        var output = Path.Combine(_tempDir, "segment.m3u8");

        var exitCode = await Program.Main(["-i", $"file:{input}", "-codec:v:0", "copy", "-f", "hls", "-y", output]);

        Assert.Equal(17, exitCode);
        Assert.Equal(0, android.RequestCount);
        Assert.Contains("-codec:v:0 copy", await File.ReadAllTextAsync(ffmpegLog));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("JFAT_CONFIG", _previousConfig);
        Environment.SetEnvironmentVariable("JFAT_FAKE_FFMPEG_LOG", null);
        Environment.SetEnvironmentVariable("JFAT_REMOTE_STARTUP_TIMEOUT_SECONDS", _previousStartupTimeout);
        Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteExecutable(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content.ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private void WriteConfig(string androidBaseUrl, string token, string ffmpeg, string ffprobe, int maxBitrate)
    {
        var path = Path.Combine(_tempDir, "shim-config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Enabled = true,
            AndroidBaseUrl = androidBaseUrl,
            Token = token,
            RealFfmpegPath = ffmpeg,
            RealFfprobePath = ffprobe,
            MaxBitrate = maxBitrate
        }));
        Environment.SetEnvironmentVariable("JFAT_CONFIG", path);
    }

    private static string[] JellyfinArgs(string input, string output) =>
    [
        "-analyzeduration", "200M", "-probesize", "1G", "-i", $"file:{input}",
        "-map_metadata", "-1", "-map_chapters", "-1", "-threads", "0",
        "-map", "0:0", "-map", "0:1", "-map", "-0:s",
        "-codec:v:0", "libx264", "-preset", "veryfast", "-crf", "23",
        "-maxrate", "8000000", "-bufsize", "12000000", "-profile:v:0", "high",
        "-level", "51", "-force_key_frames:0", "expr:gte(t,n_forced*3)",
        "-sc_threshold:v:0", "0", "-vf",
        @"setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,scale=trunc(min(max(iw\,ih*a)\,1920)/2)*2:trunc(ow/a/2)*2,tonemapx=t=bt709",
        "-codec:a:0", "libfdk_aac", "-ac", "2", "-ab", "256000", "-af", "volume=2",
        "-copyts", "-avoid_negative_ts", "disabled", "-max_muxing_queue_size", "2048",
        "-f", "hls", "-hls_time", "3", "-hls_segment_type", "fmp4",
        "-start_number", "0", "-hls_playlist_type", "vod", "-hls_list_size", "0",
        "-hls_segment_filename", Path.Combine(Path.GetDirectoryName(output)!, "segment%d.ts"),
        "-y", output
    ];

    private static string[] InceptionSafariArgs(string input, string output) =>
    [
        "-analyzeduration", "200M", "-probesize", "1G", "-i", $"file:{input}",
        "-map_metadata", "-1", "-map_chapters", "-1", "-threads", "0",
        "-map", "0:0", "-map", "-0:s", "-codec:v:0", "libx264",
        "-preset", "veryfast", "-crf", "23",
        "-maxrate", "63810668", "-bufsize", "127621336", "-profile:v:0", "high",
        "-level", "51", "-force_key_frames:0", "expr:gte(t,n_forced*3)",
        "-sc_threshold:v:0", "0", "-vf",
        @"setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,scale=trunc(min(max(iw\,ih*a)\,min(3840\,2160*a))/2)*2:trunc(min(max(iw/a\,ih)\,min(3840/a\,2160))/2)*2,tonemapx=t=bt709",
        "-copyts", "-avoid_negative_ts", "disabled", "-max_muxing_queue_size", "2048",
        "-f", "hls", "-max_delay", "5000000", "-hls_time", "3",
        "-hls_segment_type", "fmp4", "-hls_fmp4_init_filename", "70e040ca627b1a5a2ecb0618aa77f67c-1.mp4",
        "-start_number", "0", "-hls_segment_filename",
        Path.Combine(Path.GetDirectoryName(output)!, "70e040ca627b1a5a2ecb0618aa77f67c%d.mp4"),
        "-hls_playlist_type", "vod", "-hls_list_size", "0",
        "-hls_segment_options", "movflags=+frag_discont", "-y", output
    ];

    private static async Task<string> BuildShimExecutable()
    {
        if (s_shimExecutable is not null)
        {
            return s_shimExecutable;
        }

        await PublishLock.WaitAsync();
        try
        {
            if (s_shimExecutable is not null)
            {
                return s_shimExecutable;
            }

            var root = RepositoryRoot();
            var output = Path.Combine(root, ".work", "shim-contract", "publish");
            Directory.CreateDirectory(output);

            var project = Path.Combine(
                root,
                "jellyfin-android-transcoder",
                "src",
                "JellyfinAndroidTranscoder.Shim",
                "JellyfinAndroidTranscoder.Shim.csproj");
            var publish = ProcessUtil.Start(
                "dotnet",
                ["publish", project, "--configuration", "Debug", "--output", output, "--nologo"],
                redirectInput: false,
                redirectOutput: false);
            await publish.WaitForExitAsync();
            if (publish.ExitCode != 0)
            {
                throw new InvalidOperationException($"dotnet publish failed with exit code {publish.ExitCode}");
            }

            var executable = Path.Combine(output, OperatingSystem.IsWindows() ? "jfat-ffmpeg.exe" : "jfat-ffmpeg");
            if (!File.Exists(executable))
            {
                throw new FileNotFoundException("The shim publish did not produce an executable apphost.", executable);
            }

            s_shimExecutable = executable;
            return executable;
        }
        finally
        {
            PublishLock.Release();
        }
    }

    private static Process StartShimForControlTest(string shimExecutable, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(shimExecutable)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        return Process.Start(start) ?? throw new InvalidOperationException($"Failed to start {shimExecutable}");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JellyfinAndroidTranscoder.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the jellyfin-android-transcoder repository root.");
    }

    private sealed class MockAndroidService : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly IReadOnlyDictionary<string, byte[]> _files;
        private readonly bool _failFirstRemoteRequest;
        private readonly bool _failFilesStream;
        private readonly bool _holdFilesStreamOpen;
        private readonly TimeSpan _delayFilesAfterReady;
        private readonly List<AndroidRequest> _requests = [];
        private readonly List<string> _deletePaths = [];
        private readonly TaskCompletionSource _filesStreamStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _serverTask;

        private MockAndroidService(HttpListener listener, string baseUrl, IReadOnlyDictionary<string, byte[]> files, bool failFirstRemoteRequest, bool failFilesStream, bool holdFilesStreamOpen, TimeSpan delayFilesAfterReady)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _files = files;
            _failFirstRemoteRequest = failFirstRemoteRequest;
            _failFilesStream = failFilesStream;
            _holdFilesStreamOpen = holdFilesStreamOpen;
            _delayFilesAfterReady = delayFilesAfterReady;
            _serverTask = Task.Run(Serve);
        }

        public string BaseUrl { get; }
        public string Token => "test-token";
        public int RequestCount
        {
            get
            {
                lock (_requests)
                {
                    return _requests.Count;
                }
            }
        }
        public int StatusRequestCount { get; private set; }
        public IReadOnlyList<string> DeletePaths
        {
            get
            {
                lock (_deletePaths)
                {
                    return _deletePaths.ToArray();
                }
            }
        }

        public static Task<MockAndroidService> Start(IReadOnlyDictionary<string, byte[]> files, bool failFirstRemoteRequest = false, bool failFilesStream = false, bool holdFilesStreamOpen = false, TimeSpan delayFilesAfterReady = default)
        {
            var port = GetFreePort();
            var baseUrl = $"http://127.0.0.1:{port}";
            var listener = new HttpListener();
            listener.Prefixes.Add($"{baseUrl}/");
            listener.Start();
            return Task.FromResult(new MockAndroidService(listener, baseUrl, files, failFirstRemoteRequest, failFilesStream, holdFilesStreamOpen, delayFilesAfterReady));
        }

        public async Task WaitForFilesStream()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _filesStreamStarted.Task.WaitAsync(timeout.Token);
        }

        public async Task<AndroidRequest> GetSingleRequest()
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                lock (_requests)
                {
                    if (_requests.Count == 1)
                    {
                        return _requests[0];
                    }
                }
                await Task.Delay(25);
            }
            throw new TimeoutException($"Expected one Android request, saw {RequestCount}");
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _serverTask;
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
            }
            _listener.Close();
        }

        private async Task Serve()
        {
            while (_listener.IsListening)
            {
                var context = await _listener.GetContextAsync();
                if (context.Request.Url?.AbsolutePath == "/api/v1/status")
                {
                    StatusRequestCount++;
                    var body = Encoding.UTF8.GetBytes("""{"activeJobs":0,"maxJobs":255}""");
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                    continue;
                }

                var path = context.Request.Url?.AbsolutePath ?? "";
                if (path.StartsWith("/api/v1/remoteprocesses/", StringComparison.Ordinal) && context.Request.HttpMethod == "DELETE")
                {
                    lock (_deletePaths)
                    {
                        _deletePaths.Add(path);
                    }
                    var body = """{"canceled":true}"""u8.ToArray();
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                    continue;
                }

                if (path == "/api/v1/remoteprocesses" && context.Request.HttpMethod == "POST")
                {
                    using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                    var requestBody = await reader.ReadToEndAsync();
                    using var document = JsonDocument.Parse(requestBody);
                    var root = document.RootElement;
                    var executable = root.TryGetProperty("executable", out var executableElement)
                        ? executableElement.GetString() ?? ""
                        : "";
                    var remoteArgs = root.TryGetProperty("args", out var argsElement)
                        ? argsElement.GetRawText()
                        : "";
                    var request = new AndroidRequest(
                        path,
                        context.Request.Headers["Authorization"] ?? "",
                        context.Request.ContentType ?? "",
                        executable,
                        remoteArgs,
                        ParseQuery(context.Request.Url?.Query ?? ""),
                        0,
                        []);
                    lock (_requests)
                    {
                        _requests.Add(request);
                    }

                    var requestIndex = RequestCount - 1;
                    if (_failFirstRemoteRequest && requestIndex == 0)
                    {
                        context.Response.StatusCode = 503;
                        var failure = "remote process failed before start\n"u8.ToArray();
                        context.Response.ContentType = "text/plain";
                        context.Response.ContentLength64 = failure.Length;
                        await context.Response.OutputStream.WriteAsync(failure);
                        context.Response.Close();
                        continue;
                    }

                    var body = Encoding.UTF8.GetBytes("""{"id":"job-1","stdinUrl":"/api/v1/remoteprocesses/job-1/stdin","filesUrl":"/api/v1/remoteprocesses/job-1/files"}""");
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                    continue;
                }

                if (path == "/api/v1/remoteprocesses/job-1/stdin" && context.Request.HttpMethod == "PUT")
                {
                    lock (_requests)
                    {
                        if (_requests.Count > 0)
                        {
                            var request = _requests[^1];
                            _requests[^1] = request with { ContentType = context.Request.ContentType ?? "" };
                        }
                    }
                    await DrainRequest(context.Request.InputStream, Math.Max(0, RequestCount - 1));
                    var body = "{}"u8.ToArray();
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                    continue;
                }

                if (path == "/api/v1/remoteprocesses/job-1/files" && context.Request.HttpMethod == "GET")
                {
                    _filesStreamStarted.TrySetResult();
                    if (_failFilesStream)
                    {
                        context.Response.StatusCode = 503;
                        context.Response.Close();
                        continue;
                    }

                    context.Response.StatusCode = 200;
                    var boundary = "test-boundary";
                    context.Response.ContentType = "multipart/mixed; boundary=" + boundary;
                    context.Response.SendChunked = true;
                    var ready = Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Type: application/octet-stream\r\nX-Remote-Event: ready\r\nContent-Length: 0\r\n\r\n\r\n");
                    await context.Response.OutputStream.WriteAsync(ready);
                    await context.Response.OutputStream.FlushAsync();
                    if (_delayFilesAfterReady > TimeSpan.Zero)
                    {
                        await Task.Delay(_delayFilesAfterReady);
                    }
                    if (_holdFilesStreamOpen)
                    {
                        _ = Task.Run(async () =>
                        {
                            while (_listener.IsListening)
                            {
                                await Task.Delay(100);
                            }
                            context.Response.Close();
                        });
                        continue;
                    }
                    foreach (var file in _files)
                    {
                        var partHeaders = Encoding.ASCII.GetBytes(
                            $"--{boundary}\r\nContent-Type: application/octet-stream\r\nX-Remote-Event: upsert\r\nX-Remote-Path: {file.Key}\r\nContent-Length: {file.Value.Length}\r\n\r\n");
                        await context.Response.OutputStream.WriteAsync(partHeaders);
                        await context.Response.OutputStream.WriteAsync(file.Value);
                        await context.Response.OutputStream.WriteAsync("\r\n"u8.ToArray());
                        await context.Response.OutputStream.FlushAsync();
                        await Task.Delay(10);
                    }
                    var exit = Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Type: application/json\r\nX-Remote-Event: exit\r\nContent-Length: 14\r\n\r\n{{\"exitCode\":0}}\r\n--{boundary}--\r\n");
                    await context.Response.OutputStream.WriteAsync(exit);
                    context.Response.Close();
                    continue;
                }

                context.Response.StatusCode = 404;
                context.Response.Close();
            }
        }

        private async Task DrainRequest(Stream input, int requestIndex)
        {
            var buffer = new byte[8192];
            await using var prefix = new MemoryStream();
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer)) > 0)
            {
                total += read;
                if (prefix.Length < 4096)
                {
                    var take = (int)Math.Min(read, 4096 - prefix.Length);
                    await prefix.WriteAsync(buffer.AsMemory(0, take));
                }
            }

            lock (_requests)
            {
                var request = _requests[requestIndex];
                _requests[requestIndex] = request with
                {
                    BodyLength = total,
                    BodyPrefix = prefix.ToArray()
                };
            }
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            if (query.StartsWith('?'))
            {
                query = query[1..];
            }
            return query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .ToDictionary(part => WebUtility.UrlDecode(part[0]), part => part.Length == 2 ? WebUtility.UrlDecode(part[1]) : "");
        }

        private static string DecodeArgs(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }
            value = value.Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
    }

    private sealed record AndroidRequest(
        string Path,
        string Authorization,
        string ContentType,
        string Executable,
        string RemoteArgs,
        IReadOnlyDictionary<string, string> Query,
        long BodyLength,
        byte[] BodyPrefix);
}
