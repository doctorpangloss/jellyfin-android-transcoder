using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using JellyfinAndroidTranscoder.Shim;

namespace JellyfinAndroidTranscoder.Shim.Tests;

public sealed class AndroidBridgeIntegrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"jfat-tests-{Guid.NewGuid():N}");
    private readonly string? _previousConfig = Environment.GetEnvironmentVariable("JFAT_CONFIG");

    public AndroidBridgeIntegrationTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task JellyfinStyleTranscodePostsVideoToAndroidAndPackagesResponse()
    {
        var ffmpeg = WriteExecutable("fake-ffmpeg.sh", """
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$JFAT_FAKE_FFMPEG_LOG"
if [[ "$*" == *"-f matroska pipe:1"* ]]; then
  printf 'producer-matroska'
  exit 0
fi
cat > "$JFAT_PACKAGER_STDIN"
printf 'packaged-output' > "${@: -1}"
exit 0
""");
        var ffprobe = WriteExecutable("fake-ffprobe.sh", """
#!/usr/bin/env bash
set -euo pipefail
cat <<'JSON'
{"streams":[{"codec_name":"hevc","pix_fmt":"yuv420p10le","color_space":"bt2020nc","color_transfer":"smpte2084","color_primaries":"bt2020"}]}
JSON
""");
        var ffmpegLog = Path.Combine(_tempDir, "ffmpeg.log");
        var packagerStdin = Path.Combine(_tempDir, "packager.stdin");
        Environment.SetEnvironmentVariable("JFAT_FAKE_FFMPEG_LOG", ffmpegLog);
        Environment.SetEnvironmentVariable("JFAT_PACKAGER_STDIN", packagerStdin);

        var input = Path.Combine(_tempDir, "movie.mkv");
        await File.WriteAllTextAsync(input, "placeholder");
        var output = Path.Combine(_tempDir, "segment.m3u8");

        await using var android = await MockAndroidService.Start(["mpegts-from-android"u8.ToArray()]);
        WriteConfig(android.BaseUrl, android.Token, ffmpeg, ffprobe, maxBitrate: 6_000_000);

        var exitCode = await Program.Main(JellyfinArgs(input, output));

        Assert.Equal(0, exitCode);
        Assert.Equal("packaged-output", await File.ReadAllTextAsync(output));
        Assert.Equal("mpegts-from-android", await File.ReadAllTextAsync(packagerStdin));

        var request = await android.GetSingleRequest();
        Assert.Equal("/api/v1/transcode", request.Path);
        Assert.Equal("Bearer test-token", request.Authorization);
        Assert.Equal("video/x-matroska", request.ContentType);
        Assert.Equal("producer-matroska", Encoding.UTF8.GetString(request.Body));
        Assert.Equal("h264", request.Query["codec"]);
        Assert.Equal("1920", request.Query["width"]);
        Assert.Equal("1080", request.Query["height"]);
        Assert.Equal("6000000", request.Query["bitrate"]);
        Assert.Equal("12000000", request.Query["bufsize"]);
        Assert.Equal("120", request.Query["gop"]);
        Assert.Equal("1", request.Query["toneMap"]);

        var invocations = await File.ReadAllLinesAsync(ffmpegLog);
        Assert.Contains(invocations, line => line.Contains("-f matroska pipe:1", StringComparison.Ordinal));
        Assert.Contains(invocations, line => line.Contains("-i pipe:0", StringComparison.Ordinal) &&
                                             line.Contains("-codec:v:0 copy", StringComparison.Ordinal) &&
                                             line.Contains("-codec:a:0 libfdk_aac", StringComparison.Ordinal) &&
                                             line.EndsWith(output, StringComparison.Ordinal));
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
echo '{"streams":[{"codec_name":"hevc","pix_fmt":"yuv420p10le"}]}'
""");
        var ffmpegLog = Path.Combine(_tempDir, "fallback-ffmpeg.log");
        Environment.SetEnvironmentVariable("JFAT_FAKE_FFMPEG_LOG", ffmpegLog);

        await using var android = await MockAndroidService.Start(["unused"u8.ToArray()]);
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
        Environment.SetEnvironmentVariable("JFAT_PACKAGER_STDIN", null);
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
        "-hls_segment_filename", Path.Combine(Path.GetDirectoryName(output)!, "segment%d.mp4"),
        "-y", output
    ];

    private sealed class MockAndroidService : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly IReadOnlyList<byte[]> _responses;
        private readonly List<AndroidRequest> _requests = [];
        private readonly Task _serverTask;

        private MockAndroidService(HttpListener listener, string baseUrl, IReadOnlyList<byte[]> responses)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _responses = responses;
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

        public static Task<MockAndroidService> Start(IReadOnlyList<byte[]> responses)
        {
            var port = GetFreePort();
            var baseUrl = $"http://127.0.0.1:{port}";
            var listener = new HttpListener();
            listener.Prefixes.Add($"{baseUrl}/");
            listener.Start();
            return Task.FromResult(new MockAndroidService(listener, baseUrl, responses));
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
                await using var body = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(body);
                var request = new AndroidRequest(
                    context.Request.Url?.AbsolutePath ?? "",
                    context.Request.Headers["Authorization"] ?? "",
                    context.Request.ContentType ?? "",
                    ParseQuery(context.Request.Url?.Query ?? ""),
                    body.ToArray());
                lock (_requests)
                {
                    _requests.Add(request);
                }

                var responseBody = _responses[Math.Min(_requests.Count - 1, _responses.Count - 1)];
                context.Response.StatusCode = 200;
                context.Response.ContentType = "video/MP2T";
                context.Response.ContentLength64 = responseBody.Length;
                await context.Response.OutputStream.WriteAsync(responseBody);
                context.Response.Close();
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
    }

    private sealed record AndroidRequest(
        string Path,
        string Authorization,
        string ContentType,
        IReadOnlyDictionary<string, string> Query,
        byte[] Body);
}
