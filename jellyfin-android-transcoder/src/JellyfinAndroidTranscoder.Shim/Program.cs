using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;

namespace JellyfinAndroidTranscoder.Shim;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var config = ShimConfig.Load();
        var command = FfmpegCommand.Parse(args);

        if (!config.Enabled || !command.CanConsiderRouting)
        {
            return await ProcessUtil.Run(config.RealFfmpegPath, args);
        }

        try
        {
            var probe = await MediaProbe.Probe(config.RealFfprobePath, command.InputPath!);
            var decision = RouteDecision.Decide(command, probe);
            if (!decision.Route)
            {
                Console.Error.WriteLine($"jfat: fallback: {decision.Reason}");
                return await ProcessUtil.Run(config.RealFfmpegPath, args);
            }

            Console.Error.WriteLine($"jfat: routing {probe.CodecName}/{probe.PixelFormat} through {config.AndroidBaseUrl}");
            var remoteExit = await AndroidTranscode.Run(config, command, probe);
            if (remoteExit == 0)
            {
                return 0;
            }

            Console.Error.WriteLine($"jfat: fallback after android ffmpeg exited {remoteExit}");
            return await ProcessUtil.Run(config.RealFfmpegPath, args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"jfat: fallback after error: {ex.Message}");
            return await ProcessUtil.Run(config.RealFfmpegPath, args);
        }
    }
}

public sealed record ShimConfig(
    bool Enabled,
    string AndroidBaseUrl,
    string Token,
    string RealFfmpegPath,
    string RealFfprobePath,
    int MaxBitrate)
{
    public static ShimConfig Load()
    {
        var path = Environment.GetEnvironmentVariable("JFAT_CONFIG")
                   ?? FirstExisting(
                       Path.Combine(AppContext.BaseDirectory, "shim-config.json"),
                       "/config/plugins/Jellyfin.Plugin.AndroidTranscoder/shim/shim-config.json",
                       "/config/plugins/Jellyfin.Plugin.AndroidTranscoder/shim-config.json");
        if (!File.Exists(path))
        {
            return new ShimConfig(false, "", "", "/usr/lib/jellyfin-ffmpeg/ffmpeg",
                "/usr/lib/jellyfin-ffmpeg/ffprobe", 6_000_000);
        }

        var json = JsonSerializer.Deserialize<ShimConfig>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return json ?? throw new InvalidOperationException($"Invalid config: {path}");
    }

    private static string FirstExisting(params string[] paths) =>
        paths.FirstOrDefault(File.Exists) ?? paths[0];
}

public sealed class FfmpegCommand
{
    public required IReadOnlyList<string> Args { get; init; }
    public string? InputPath { get; init; }
    public string? OutputPath { get; init; }
    public int MaxRate { get; init; }
    public int BufSize { get; init; }
    public int Height { get; init; }
    public int Width { get; init; }
    public int GopSeconds { get; init; }
    public string AudioCodec { get; init; } = "copy";
    public string? AudioBitrate { get; init; }
    public string? AudioChannels { get; init; }
    public string? AudioFilter { get; init; }
    public string? HlsSegmentFilename { get; init; }
    public IReadOnlyList<string> HlsArgs { get; init; } = Array.Empty<string>();

    public bool CanConsiderRouting =>
        InputPath is not null &&
        OutputPath is not null &&
        Args.Contains("-f") &&
        ValueAfter("-f") == "hls" &&
        (ValueAfter("-codec:v:0") == "libx264" || ValueAfter("-c:v:0") == "libx264") &&
        !Args.Any(a => a.Contains("subtitles=", StringComparison.OrdinalIgnoreCase) ||
                       a.Contains("overlay", StringComparison.OrdinalIgnoreCase));

    public static FfmpegCommand Parse(IReadOnlyList<string> args)
    {
        var input = ValueAfter(args, "-i")?.Trim();
        if (input is not null && input.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            input = input[5..];
        }

        var output = args.LastOrDefault(a => a.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase));
        var hlsStart = IndexOf(args, "-copyts");
        if (hlsStart < 0)
        {
            hlsStart = IndexOf(args, "-f");
        }

        var scaleWidth = ParseScaleWidth(ValueAfter(args, "-vf"));
        return new FfmpegCommand
        {
            Args = args.ToArray(),
            InputPath = string.IsNullOrWhiteSpace(input) ? null : input,
            OutputPath = output,
            MaxRate = ParseBitrate(ValueAfter(args, "-maxrate"), 6_000_000),
            BufSize = ParseBitrate(ValueAfter(args, "-bufsize"), 12_000_000),
            Width = scaleWidth ?? 1920,
            Height = ParseScaleHeight(ValueAfter(args, "-vf"), scaleWidth) ?? 1080,
            GopSeconds = ParseForceKeySeconds(ValueAfter(args, "-force_key_frames:0")) ?? 3,
            AudioCodec = ValueAfter(args, "-codec:a:0") ?? "copy",
            AudioBitrate = ValueAfter(args, "-ab"),
            AudioChannels = ValueAfter(args, "-ac"),
            AudioFilter = ValueAfter(args, "-af"),
            HlsSegmentFilename = ValueAfter(args, "-hls_segment_filename"),
            HlsArgs = hlsStart >= 0 ? args.Skip(hlsStart).Take(args.Count - hlsStart - 1).ToArray() : Array.Empty<string>()
        };
    }

    public string? ValueAfter(string key) => ValueAfter(Args, key);

    private static string? ValueAfter(IReadOnlyList<string> args, string key)
    {
        var index = IndexOf(args, key);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    private static int IndexOf(IReadOnlyList<string> args, string key)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == key)
            {
                return i;
            }
        }
        return -1;
    }

    private static int ParseBitrate(string? value, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        if (int.TryParse(value, out var numeric))
        {
            return numeric;
        }
        var match = Regex.Match(value, "^([0-9]+)([kKmM])?$");
        if (!match.Success)
        {
            return fallback;
        }
        var result = int.Parse(match.Groups[1].Value);
        return match.Groups[2].Value.ToLowerInvariant() switch
        {
            "k" => result * 1000,
            "m" => result * 1000 * 1000,
            _ => result
        };
    }

    private static int? ParseScaleWidth(string? vf)
    {
        if (vf is null)
        {
            return null;
        }
        var match = Regex.Match(vf, @"min\(max\(iw\\,ih\*a\)\\,([0-9]+)\)");
        if (!match.Success)
        {
            match = Regex.Match(vf, @"min\(max\(iw\\,ih\*a\)\\,min\(([0-9]+)\\,");
        }
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private static int? ParseScaleHeight(string? vf, int? width)
    {
        if (vf is null)
        {
            return null;
        }
        if (width is not null)
        {
            return width.Value / 16 * 9;
        }
        return vf.Contains("1920", StringComparison.Ordinal) ? 1080 : null;
    }

    private static int? ParseForceKeySeconds(string? forceKey)
    {
        if (forceKey is null)
        {
            return null;
        }
        var match = Regex.Match(forceKey, @"n_forced\*([0-9]+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }
}

public sealed record MediaProbe(
    string CodecName,
    string PixelFormat,
    int Width,
    int Height,
    double DurationSeconds,
    long BitRate,
    string? ColorSpace,
    string? ColorTransfer,
    string? ColorPrimaries)
{
    public bool IsHdr =>
        string.Equals(ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(ColorPrimaries, "bt2020", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(ColorSpace, "bt2020nc", StringComparison.OrdinalIgnoreCase);

    public static async Task<MediaProbe> Probe(string ffprobePath, string inputPath)
    {
        var args = new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=codec_name,pix_fmt,width,height,bit_rate,color_space,color_transfer,color_primaries:format=duration",
            "-of", "json",
            inputPath
        };
        var output = await ProcessUtil.Capture(ffprobePath, args);
        using var document = JsonDocument.Parse(output);
        var stream = document.RootElement.GetProperty("streams")[0];
        var format = document.RootElement.TryGetProperty("format", out var formatElement)
            ? formatElement
            : default;
        return new MediaProbe(
            GetString(stream, "codec_name") ?? "",
            GetString(stream, "pix_fmt") ?? "",
            GetInt(stream, "width"),
            GetInt(stream, "height"),
            GetDouble(format, "duration"),
            GetLong(stream, "bit_rate"),
            GetString(stream, "color_space"),
            GetString(stream, "color_transfer"),
            GetString(stream, "color_primaries"));
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int GetInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.TryGetInt32(out var result)
            ? result
            : 0;

    private static double GetDouble(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value))
        {
            return 0;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var numeric))
        {
            return numeric;
        }
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return 0;
    }

    private static long GetLong(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value))
        {
            return 0;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numeric))
        {
            return numeric;
        }
        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return 0;
    }
}

public sealed record RouteDecision(bool Route, string Reason)
{
    public static RouteDecision Decide(FfmpegCommand command, MediaProbe probe)
    {
        if (command.InputPath is null || command.OutputPath is null)
        {
            return new RouteDecision(false, "missing input or output");
        }
        if (!File.Exists(command.InputPath))
        {
            return new RouteDecision(false, "input file does not exist");
        }
        if (probe.CodecName is not ("hevc" or "av1"))
        {
            return new RouteDecision(false, $"unsupported codec {probe.CodecName}");
        }
        return new RouteDecision(true, "ok");
    }
}

public static class AndroidTranscode
{
    private const int RemoteInputBytesPerSecond = 10_000_000;
    private const int RemoteAttempts = 2;
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RemoteStartupTimeout = TimeSpan.FromSeconds(45);

    public static async Task<int> Run(ShimConfig config, FfmpegCommand command, MediaProbe probe)
    {
        if (!await HealthCheck(config))
        {
            throw new TimeoutException($"Android health check did not respond within {HealthCheckTimeout.TotalSeconds:0} seconds");
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RunOnce(config, command, probe);
            }
            catch (OperationCanceledException) when (attempt < RemoteAttempts)
            {
                Console.Error.WriteLine($"jfat: android startup timed out, retrying ({attempt + 1}/{RemoteAttempts})");
            }
            catch (HttpRequestException ex) when (attempt < RemoteAttempts)
            {
                Console.Error.WriteLine($"jfat: android request failed during startup, retrying ({attempt + 1}/{RemoteAttempts}): {ex.Message}");
            }
            catch (IOException ex) when (attempt < RemoteAttempts)
            {
                Console.Error.WriteLine($"jfat: android stream failed during startup, retrying ({attempt + 1}/{RemoteAttempts}): {ex.Message}");
            }
        }
    }

    private static async Task<bool> HealthCheck(ShimConfig config)
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = new CancellationTokenSource(HealthCheckTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{config.AndroidBaseUrl.TrimEnd('/')}/api/v1/status");
        if (!string.IsNullOrWhiteSpace(config.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        }

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> RunOnce(ShimConfig config, FfmpegCommand command, MediaProbe probe)
    {
        using var startupTimeout = new CancellationTokenSource(RemoteStartupTimeout);
        using var controlClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var uploadClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var filesClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var baseUri = config.AndroidBaseUrl.TrimEnd('/');
        var job = await StartRemoteProcess(controlClient, baseUri, config.Token, EncodeRemoteArgs(BuildRemoteFfmpegArgs(config, command, probe)), startupTimeout.Token);

        await using var input = File.OpenRead(command.InputPath!);
        await using var limitedInput = new RateLimitedReadStream(input, RemoteInputBytesPerSecond);
        using var stdinRequest = new HttpRequestMessage(HttpMethod.Put, baseUri + job.StdinUrl);
        stdinRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        stdinRequest.Content = new StreamContent(limitedInput);
        stdinRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var stdinTask = uploadClient.SendAsync(stdinRequest, HttpCompletionOption.ResponseHeadersRead);

        using var filesRequest = new HttpRequestMessage(HttpMethod.Get, baseUri + job.FilesUrl);
        filesRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        using var filesResponse = await filesClient.SendAsync(filesRequest, HttpCompletionOption.ResponseHeadersRead, startupTimeout.Token);
        filesResponse.EnsureSuccessStatusCode();
        var boundary = MultipartUtil.GetBoundary(filesResponse.Content.Headers.ContentType?.ToString()
            ?? throw new InvalidOperationException("Missing multipart content type"));
        await using (var files = await filesResponse.Content.ReadAsStreamAsync(startupTimeout.Token))
        {
            var exit = await MultipartUtil.MaterializeFiles(files, boundary, command);
            using var stdinResponse = await stdinTask;
            stdinResponse.EnsureSuccessStatusCode();
            return exit;
        }
    }

    private static async Task<RemoteJob> StartRemoteProcess(HttpClient client, string baseUri, string token, string remoteArgs, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUri + "/api/v1/remoteprocesses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("X-Remote-Split", "1");
        request.Headers.TryAddWithoutValidation("X-Remote-Executable", "ffmpeg");
        request.Headers.TryAddWithoutValidation("X-Remote-Args", remoteArgs);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var job = await response.Content.ReadFromJsonAsync<RemoteJob>(cancellationToken);
        return job ?? throw new InvalidOperationException("Android did not return a remote job.");
    }

    private static IReadOnlyList<string> BuildRemoteFfmpegArgs(ShimConfig config, FfmpegCommand command, MediaProbe probe)
    {
        var toneMap = probe.IsHdr ? 1 : 0;
        var bitrate = Math.Min(command.MaxRate, config.MaxBitrate);
        var (outputWidth, outputHeight) = OutputDimensions(command, probe);
        const int startupSegmentSeconds = 1;
        var outputName = Path.GetFileName(command.OutputPath!);
        var segmentName = Path.GetFileName(command.HlsSegmentFilename ?? Path.ChangeExtension(command.OutputPath!, ".ts"));
        var hlsSegmentType = string.Equals(command.ValueAfter("-hls_segment_type"), "fmp4", StringComparison.OrdinalIgnoreCase)
            ? "fmp4"
            : "mpegts";
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-init_hw_device", "mediacodec=mc,create_window=1,surface_processor=1",
            "-hwaccel", "mediacodec",
            "-hwaccel_device", "mc",
            "-hwaccel_output_format", "mediacodec"
        };
        if (probe.DurationSeconds > 0)
        {
            args.Add("-t");
            args.Add(probe.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }
        args.AddRange(["-i", "{input}"]);
        if (probe.DurationSeconds > 0)
        {
            args.Add("-t");
            args.Add(probe.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }
        args.AddRange([
            "-map", "0:v:0",
            "-c:v", "h264_mediacodec",
            "-pix_fmt", "mediacodec",
            "-output_width", outputWidth.ToString(),
            "-output_height", outputHeight.ToString(),
            "-surface_tonemap", toneMap.ToString(),
            "-b:v", bitrate.ToString(),
            "-maxrate", bitrate.ToString(),
            "-bufsize", (bitrate * 2).ToString(),
            "-bitrate_mode", "cbr",
            "-g", "24",
            "-an",
            "-sn",
            "-dn",
            "-f", "hls",
            "-hls_time", startupSegmentSeconds.ToString(),
            "-hls_flags", HlsFlagsWithTempFile(command),
            "-hls_segment_type", hlsSegmentType,
            "-hls_segment_filename", "{outputRoot}/" + segmentName
        ]);
        AddPreservedOption(args, command, "-start_number");
        if (hlsSegmentType == "fmp4")
        {
            AddPreservedOption(args, command, "-hls_fmp4_init_filename", Path.GetFileName);
            AddPreservedOption(args, command, "-hls_segment_options");
        }
        AddPreservedOption(args, command, "-hls_playlist_type");
        AddPreservedOption(args, command, "-hls_list_size");
        args.AddRange(["-y", "{outputRoot}/" + outputName]);
        return args;
    }

    private static string HlsFlagsWithTempFile(FfmpegCommand command)
    {
        var flags = (command.ValueAfter("-hls_flags") ?? "")
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (!flags.Any(flag => string.Equals(flag, "temp_file", StringComparison.Ordinal)))
        {
            flags.Add("temp_file");
        }
        return string.Join("+", flags);
    }

    private static void AddPreservedOption(List<string> args, FfmpegCommand command, string option)
    {
        AddPreservedOption(args, command, option, value => value);
    }

    private static void AddPreservedOption(List<string> args, FfmpegCommand command, string option, Func<string, string> transform)
    {
        var value = command.ValueAfter(option);
        if (!string.IsNullOrWhiteSpace(value))
        {
            args.Add(option);
            args.Add(transform(value));
        }
    }

    private static (int Width, int Height) OutputDimensions(FfmpegCommand command, MediaProbe probe)
    {
        const int maxWidth = 1920;
        const int maxHeight = 1080;
        if (probe.Width <= 0 || probe.Height <= 0)
        {
            return (Math.Min(command.Width, maxWidth), Math.Min(command.Height, maxHeight));
        }
        var width = Math.Min(command.Width, maxWidth);
        var height = Math.Min(command.Height, maxHeight);
        if (width <= probe.Width && height <= probe.Height)
        {
            return (Even(width), Even(height));
        }
        return (Even(Math.Min(probe.Width, maxWidth)), Even(Math.Min(probe.Height, maxHeight)));
    }

    private static int Even(int value) => Math.Max(2, value / 2 * 2);

    private static string EncodeRemoteArgs(IReadOnlyList<string> args)
    {
        var json = JsonSerializer.Serialize(args);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

}

public sealed record RemoteJob(string Id, string StdinUrl, string FilesUrl);

public sealed class RateLimitedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _bytesPerSecond;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _bytesRead;

    public RateLimitedReadStream(Stream inner, long bytesPerSecond)
    {
        _inner = inner;
        _bytesPerSecond = bytesPerSecond;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        Throttle(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        await ThrottleAsync(read, cancellationToken);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    private void Throttle(int read)
    {
        var delay = DelayAfter(read);
        if (delay > TimeSpan.Zero)
        {
            Thread.Sleep(delay);
        }
    }

    private async Task ThrottleAsync(int read, CancellationToken cancellationToken)
    {
        var delay = DelayAfter(read);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }
    }

    private TimeSpan DelayAfter(int read)
    {
        if (read <= 0)
        {
            return TimeSpan.Zero;
        }

        _bytesRead += read;
        var expected = TimeSpan.FromSeconds(_bytesRead / (double)_bytesPerSecond);
        return expected > _clock.Elapsed ? expected - _clock.Elapsed : TimeSpan.Zero;
    }
}

public static class MultipartUtil
{
    public static string GetBoundary(string contentType)
    {
        var match = Regex.Match(contentType, "boundary=(?:\"([^\"]+)\"|([^;]+))", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Missing multipart boundary in {contentType}");
        }
        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value.Trim();
    }

    public static async Task<int> MaterializeFiles(Stream stream, string boundary, FfmpegCommand command)
    {
        var outputDirectory = Path.GetDirectoryName(command.OutputPath!)
            ?? throw new InvalidOperationException("Missing output directory");
        Directory.CreateDirectory(outputDirectory);
        var exitCode = 0;
        while (true)
        {
            var line = await ReadLine(stream);
            if (line is null)
            {
                break;
            }
            if (line.Length == 0)
            {
                continue;
            }
            if (line == "--" + boundary + "--")
            {
                break;
            }
            if (line != "--" + boundary)
            {
                continue;
            }

            var headers = await ReadHeaders(stream);
            var remoteEvent = Header(headers, "x-remote-event");
            var contentLength = int.Parse(Header(headers, "content-length") ?? "0");
            var body = await ReadExact(stream, contentLength);
            await ReadLine(stream);
            if (string.Equals(remoteEvent, "exit", StringComparison.OrdinalIgnoreCase))
            {
                using var document = JsonDocument.Parse(body);
                exitCode = document.RootElement.GetProperty("exitCode").GetInt32();
                if (exitCode != 0 &&
                    document.RootElement.TryGetProperty("stderr", out var stderr) &&
                    stderr.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(stderr.GetString()))
                {
                    Console.Error.WriteLine(stderr.GetString());
                }
                continue;
            }

            var remotePath = Header(headers, "x-remote-path")
                ?? throw new InvalidOperationException("Missing X-Remote-Path");
            var localPath = ResolveLocalPath(outputDirectory, remotePath);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            var temp = localPath + ".jfat.tmp";
            await File.WriteAllBytesAsync(temp, body);
            File.Move(temp, localPath, overwrite: true);
        }
        return exitCode;
    }

    private static string ResolveLocalPath(string outputDirectory, string remotePath)
    {
        var fileName = Path.GetFileName(remotePath.Replace('\\', '/'));
        return Path.Combine(outputDirectory, fileName);
    }

    private static async Task<Dictionary<string, string>> ReadHeaders(Stream stream)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while (!string.IsNullOrEmpty(line = await ReadLine(stream)))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }
        return headers;
    }

    private static string? Header(Dictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var value) ? value : null;

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset));
            if (read == 0)
            {
                throw new EndOfStreamException();
            }
            offset += read;
        }
        return buffer;
    }

    private static async Task<string?> ReadLine(Stream stream)
    {
        using var bytes = new MemoryStream();
        while (true)
        {
            var value = stream.ReadByte();
            if (value < 0)
            {
                return bytes.Length == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            }
            if (value == '\n')
            {
                var line = bytes.ToArray();
                if (line.Length > 0 && line[^1] == '\r')
                {
                    line = line[..^1];
                }
                return Encoding.ASCII.GetString(line);
            }
            bytes.WriteByte((byte)value);
        }
    }
}

public static class ProcessUtil
{
    public static Process Start(string fileName, IEnumerable<string> args, bool redirectInput, bool redirectOutput)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = false,
            UseShellExecute = false
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        return Process.Start(start) ?? throw new InvalidOperationException($"Failed to start {fileName}");
    }

    public static async Task<int> Run(string fileName, IEnumerable<string> args)
    {
        using var process = Start(fileName, args, redirectInput: false, redirectOutput: false);
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    public static async Task<string> Capture(string fileName, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(error);
        }
        return output;
    }
}

public sealed class ProcessGuard(Process process) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
        process.Dispose();
        return ValueTask.CompletedTask;
    }
}
