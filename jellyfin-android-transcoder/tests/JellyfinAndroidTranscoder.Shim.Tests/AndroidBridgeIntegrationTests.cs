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
        Assert.Contains("\"-output_width\",\"320\"", request.RemoteArgs);
        Assert.Contains("\"-output_height\",\"180\"", request.RemoteArgs);
        Assert.Contains("\"-g\",\"24\"", request.RemoteArgs);
        Assert.Contains("\"-hls_time\",\"1\"", request.RemoteArgs);
        Assert.Contains("\"-t\",\"12.345\"", request.RemoteArgs);
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
        "-hls_segment_filename", Path.Combine(Path.GetDirectoryName(output)!, "segment%d.ts"),
        "-y", output
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
        private readonly List<AndroidRequest> _requests = [];
        private readonly Task _serverTask;

        private MockAndroidService(HttpListener listener, string baseUrl, IReadOnlyDictionary<string, byte[]> files)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _files = files;
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

        public static Task<MockAndroidService> Start(IReadOnlyDictionary<string, byte[]> files)
        {
            var port = GetFreePort();
            var baseUrl = $"http://127.0.0.1:{port}";
            var listener = new HttpListener();
            listener.Prefixes.Add($"{baseUrl}/");
            listener.Start();
            return Task.FromResult(new MockAndroidService(listener, baseUrl, files));
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
                var request = new AndroidRequest(
                    context.Request.Url?.AbsolutePath ?? "",
                    context.Request.Headers["Authorization"] ?? "",
                    context.Request.ContentType ?? "",
                    context.Request.Headers["X-Remote-Executable"] ?? "",
                    DecodeArgs(context.Request.Headers["X-Remote-Args"] ?? ""),
                    ParseQuery(context.Request.Url?.Query ?? ""),
                    0,
                    []);
                lock (_requests)
                {
                    _requests.Add(request);
                }

                var requestIndex = RequestCount - 1;
                var drainTask = DrainRequest(context.Request.InputStream, requestIndex);
                context.Response.StatusCode = 200;
                var boundary = "test-boundary";
                context.Response.ContentType = "multipart/mixed; boundary=" + boundary;
                context.Response.SendChunked = true;
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
                await drainTask;
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
