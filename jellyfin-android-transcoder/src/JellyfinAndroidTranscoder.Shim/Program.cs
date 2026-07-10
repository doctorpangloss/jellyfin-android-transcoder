using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace JellyfinAndroidTranscoder.Shim;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var config = ShimConfig.Load();
        args = FfmpegArgumentParser.Normalize(args);
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
            if (remoteExit == 0 || IsForwardedProcessSignalExit(remoteExit))
            {
                return remoteExit;
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

    private static bool IsForwardedProcessSignalExit(int exitCode) =>
        exitCode is 129 or 130 or 131 or 143;
}

public static class FfmpegArgumentParser
{
    public static string[] Normalize(string[] args)
    {
        if (args.Length != 1 || !LooksLikeSingleCommandLine(args[0]))
        {
            return args;
        }

        return Split(args[0]).ToArray();
    }

    private static bool LooksLikeSingleCommandLine(string value) =>
        value.Contains(" -", StringComparison.Ordinal) ||
        value.Contains(" file:", StringComparison.Ordinal) ||
        value.Contains(" \"", StringComparison.Ordinal);

    public static IReadOnlyList<string> Split(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escaped = false;

        foreach (var c in commandLine)
        {
            if (escaped)
            {
                current.Append(c);
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                if (inSingleQuote)
                {
                    current.Append(c);
                }
                else
                {
                    escaped = true;
                }
                continue;
            }

            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }

            if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inSingleQuote && !inDoubleQuote)
            {
                Flush();
                continue;
            }

            current.Append(c);
        }

        if (escaped)
        {
            current.Append('\\');
        }
        Flush();
        return result;

        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }
            result.Add(current.ToString());
            current.Clear();
        }
    }
}

public sealed record ShimConfig(
    bool Enabled,
    string AndroidBaseUrl,
    string Token,
    string RealFfmpegPath,
    string RealFfprobePath,
    int MaxBitrate,
    bool? UseHardwareCodecs,
    string? JellyfinBaseUrl = null,
    string? SourceSecret = null)
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
                "/usr/lib/jellyfin-ffmpeg/ffprobe", 6_000_000, true);
        }

        var json = JsonSerializer.Deserialize<ShimConfig>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return json ?? throw new InvalidOperationException($"Invalid config: {path}");
    }

    public bool HardwareCodecsEnabled => UseHardwareCodecs != false;

    private static string FirstExisting(params string[] paths) =>
        paths.FirstOrDefault(File.Exists) ?? paths[0];
}

public sealed class FfmpegCommand
{
    public required IReadOnlyList<string> Args { get; init; }
    public string? InputPath { get; init; }
    public string? OutputPath { get; init; }
    public string? SeekBeforeInput { get; init; }
    public IReadOnlyList<string> InputOptions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PositiveMaps { get; init; } = Array.Empty<string>();
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
    public bool AudioRequested { get; init; }
    public string? FilterComplex { get; init; }
    public TonemapxOptions? Tonemapx { get; init; }
    public string? OutputFormat { get; init; }

    public bool CanConsiderRouting =>
        InputPath is not null &&
        OutputPath is not null &&
        OutputFormat == "hls" &&
        (ValueAfter("-codec:v:0") == "libx264" || ValueAfter("-c:v:0") == "libx264") &&
        !Args.Any(a => a.Contains("subtitles=", StringComparison.OrdinalIgnoreCase));

    public static FfmpegCommand Parse(IReadOnlyList<string> args)
    {
        var inputIndex = IndexOf(args, "-i");
        var input = inputIndex >= 0 && inputIndex + 1 < args.Count ? args[inputIndex + 1].Trim() : null;
        if (input is not null && input.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            input = input[5..];
        }

        var output = args.LastOrDefault(a => a.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase));
        var outputFormatIndex = LastIndexOf(args, "-f");
        var hlsStart = IndexOf(args, "-copyts");
        if (hlsStart < 0)
        {
            hlsStart = outputFormatIndex;
        }

        var videoFilter = ValueAfter(args, "-vf");
        var filterComplex = ValueAfter(args, "-filter_complex");
        var scaleWidth = ParseScaleWidth(videoFilter ?? filterComplex);
        return new FfmpegCommand
        {
            Args = args.ToArray(),
            InputPath = string.IsNullOrWhiteSpace(input) ? null : input,
            OutputPath = output,
            SeekBeforeInput = ValueBefore(args, "-ss", inputIndex),
            InputOptions = inputIndex > 0 ? args.Take(inputIndex).ToArray() : Array.Empty<string>(),
            PositiveMaps = PositiveMapValues(args).ToArray(),
            MaxRate = ParseBitrate(ValueAfter(args, "-maxrate"), 6_000_000),
            BufSize = ParseBitrate(ValueAfter(args, "-bufsize"), 12_000_000),
            Width = scaleWidth ?? 1920,
            Height = ParseScaleHeight(videoFilter ?? filterComplex, scaleWidth) ?? 1080,
            GopSeconds = ParseForceKeySeconds(ValueAfter(args, "-force_key_frames:0")) ?? 3,
            AudioCodec = ValueAfter(args, "-codec:a:0") ?? ValueAfter(args, "-c:a:0") ?? "copy",
            AudioBitrate = ValueAfter(args, "-ab"),
            AudioChannels = ValueAfter(args, "-ac"),
            AudioFilter = ValueAfter(args, "-af"),
            HlsSegmentFilename = ValueAfter(args, "-hls_segment_filename"),
            HlsArgs = hlsStart >= 0 ? args.Skip(hlsStart).Take(args.Count - hlsStart - 1).ToArray() : Array.Empty<string>(),
            AudioRequested = !args.Contains("-an") &&
                (IndexOf(args, "-codec:a:0") >= 0 || IndexOf(args, "-c:a:0") >= 0 || IndexOf(args, "-codec:a") >= 0 || IndexOf(args, "-c:a") >= 0),
            FilterComplex = filterComplex,
            Tonemapx = TonemapxOptions.Parse(videoFilter) ?? TonemapxOptions.Parse(filterComplex),
            OutputFormat = outputFormatIndex >= 0 && outputFormatIndex + 1 < args.Count ? args[outputFormatIndex + 1] : null
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

    private static int LastIndexOf(IReadOnlyList<string> args, string key)
    {
        for (var i = args.Count - 1; i >= 0; i--)
        {
            if (args[i] == key)
            {
                return i;
            }
        }
        return -1;
    }

    private static IEnumerable<string> PositiveMapValues(IReadOnlyList<string> args)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == "-map" && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                yield return args[i + 1];
                i++;
            }
        }
    }

    private static string? ValueBefore(IReadOnlyList<string> args, string key, int beforeIndex)
    {
        if (beforeIndex <= 0)
        {
            return null;
        }
        for (var i = 0; i < beforeIndex - 1; i++)
        {
            if (args[i] == key)
            {
                return args[i + 1];
            }
        }
        return null;
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

public sealed record TonemapxOptions(
    string Algorithm,
    string Transfer,
    string Matrix,
    string Primaries,
    string Range,
    string Format,
    string? Param,
    string Desat,
    string? Peak,
    bool ApplyDovi)
{
    public static TonemapxOptions Default { get; } =
        new("bt2390", "bt709", "bt709", "bt709", "tv", "same", null, "0", null, true);

    public static TonemapxOptions? Parse(string? filterGraph)
    {
        if (string.IsNullOrWhiteSpace(filterGraph))
        {
            return null;
        }

        var match = Regex.Match(filterGraph, @"(?:^|[,;\]])tonemapx(?:=(?<options>[^\[,\];]+))?", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var options = match.Groups["options"].Success ? match.Groups["options"].Value : "";
        foreach (var token in options.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = token.IndexOf('=');
            if (separator < 0)
            {
                values["tonemap"] = token;
                continue;
            }

            values[token[..separator]] = token[(separator + 1)..];
        }

        var defaults = Default;
        return defaults with
        {
            Algorithm = Value(values, "tonemap", defaults.Algorithm),
            Transfer = Value(values, "t", Value(values, "transfer", defaults.Transfer)),
            Matrix = Value(values, "m", Value(values, "matrix", defaults.Matrix)),
            Primaries = Value(values, "p", Value(values, "primaries", defaults.Primaries)),
            Range = Value(values, "r", Value(values, "range", defaults.Range)),
            Format = Value(values, "format", defaults.Format),
            Param = values.TryGetValue("param", out var param) ? param : defaults.Param,
            Desat = Value(values, "desat", defaults.Desat),
            Peak = values.TryGetValue("peak", out var peak) ? peak : defaults.Peak,
            ApplyDovi = !values.TryGetValue("apply_dovi", out var applyDovi) || applyDovi != "0"
        };
    }

    private static string Value(Dictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
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
    private static TimeSpan RemoteStartupTimeout => TimeSpan.FromSeconds(ReadPositiveInt("JFAT_REMOTE_STARTUP_TIMEOUT_SECONDS", 45));

    private static int ReadPositiveInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;

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
        using var remoteStop = new CancellationTokenSource();
        using var controlClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var uploadClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var stdoutClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var baseUri = config.AndroidBaseUrl.TrimEnd('/');
        var sourceUrl = SourceUrlSigner.TryCreate(config, command.InputPath!, command.SeekBeforeInput);
        var useRemoteStdout = ShouldUseRemoteVideoStdout(command);
        var remoteArgs = useRemoteStdout
            ? BuildRemoteMpegtsArgs(config, command, probe, sourceUrl)
            : BuildRemoteHlsArgs(config, command, probe, sourceUrl);
        Console.Error.WriteLine("jfat: remote ffmpeg args " + JsonSerializer.Serialize(remoteArgs));
        var job = await StartRemoteProcess(controlClient, baseUri, config.Token, remoteArgs, startupTimeout.Token);
        var stopRequest = new TaskCompletionSource<ProcessStopRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdinControlTask = WaitForFfmpegQuitCommand(stopRequest, remoteStop.Token);
        using var signalHandlers = RegisterProcessSignalHandlers(stopRequest);

        try
        {
            if (!useRemoteStdout && string.IsNullOrWhiteSpace(job.FilesUrl))
            {
                throw new InvalidOperationException("Android did not return filesUrl.");
            }
            if (useRemoteStdout && string.IsNullOrWhiteSpace(job.StdoutUrl))
            {
                throw new InvalidOperationException("Android did not return stdoutUrl.");
            }

            Task<HttpResponseMessage> outputResponseTask;
            HttpRequestMessage outputRequest;
            if (useRemoteStdout)
            {
                outputRequest = new HttpRequestMessage(HttpMethod.Get, baseUri + job.StdoutUrl);
            }
            else
            {
                outputRequest = new HttpRequestMessage(HttpMethod.Get, baseUri + job.FilesUrl);
            }
            outputRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
            outputResponseTask = stdoutClient.SendAsync(outputRequest, HttpCompletionOption.ResponseHeadersRead, remoteStop.Token);

            Task<HttpResponseMessage>? stdinTask = null;
            FileStream? input = null;
            RateLimitedReadStream? limitedInput = null;
            HttpRequestMessage? stdinRequest = null;
            if (sourceUrl is null)
            {
                input = File.OpenRead(command.InputPath!);
                limitedInput = new RateLimitedReadStream(input, RemoteInputBytesPerSecond);
                stdinRequest = new HttpRequestMessage(HttpMethod.Put, baseUri + job.StdinUrl);
                stdinRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
                stdinRequest.Content = new StreamContent(limitedInput);
                stdinRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                stdinTask = uploadClient.SendAsync(stdinRequest, HttpCompletionOption.ResponseHeadersRead, remoteStop.Token);
            }

            if (await Task.WhenAny(outputResponseTask, stopRequest.Task) == stopRequest.Task)
            {
                return await StopRemoteProcess(controlClient, baseUri, config.Token, job.Id, remoteStop, await stopRequest.Task);
            }
            using var outputResponse = await outputResponseTask;
            outputResponse.EnsureSuccessStatusCode();

            await using (var output = await outputResponse.Content.ReadAsStreamAsync(remoteStop.Token))
            {
                var outputTask = useRemoteStdout
                    ? RemuxRemoteMpegtsToLocalHls(config, command, output, remoteStop.Token)
                    : MultipartUtil.MaterializeFiles(output, MultipartUtil.GetBoundary(outputResponse.Content.Headers.ContentType?.ToString() ?? ""), command);
                if (await Task.WhenAny(outputTask, stopRequest.Task) == stopRequest.Task)
                {
                    return await StopRemoteProcess(controlClient, baseUri, config.Token, job.Id, remoteStop, await stopRequest.Task);
                }
                var exit = await outputTask;
                remoteStop.Cancel();
                if (stdinTask is not null)
                {
                    try
                    {
                        using var stdinResponse = await stdinTask;
                        stdinResponse.EnsureSuccessStatusCode();
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (HttpRequestException) when (exit == 0)
                    {
                    }
                }
                stdinRequest?.Dispose();
                limitedInput?.Dispose();
                input?.Dispose();
                return exit;
            }
        }
        catch
        {
            await CancelRemoteProcess(controlClient, baseUri, config.Token, job.Id);
            throw;
        }
        finally
        {
            remoteStop.Cancel();
        }
    }

    private static bool IsMpegtsHls(FfmpegCommand command) =>
        string.Equals(command.ValueAfter("-hls_segment_type"), "mpegts", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldUseRemoteVideoStdout(FfmpegCommand command) =>
        command.AudioRequested ||
        IsMpegtsHls(command) ||
        !string.IsNullOrWhiteSpace(command.FilterComplex);

    private static async Task WaitForFfmpegQuitCommand(TaskCompletionSource<ProcessStopRequest> stopRequest, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            try
            {
                var debug = Environment.GetEnvironmentVariable("JFAT_DEBUG_STDIN") == "1";
                if (debug)
                {
                    Console.Error.WriteLine($"jfat: stdin redirected={Console.IsInputRedirected}");
                }
                if (!Console.IsInputRedirected)
                {
                    return;
                }
                using var stdin = Console.OpenStandardInput();
                while (!cancellationToken.IsCancellationRequested)
                {
                    var value = stdin.ReadByte();
                    if (value < 0)
                    {
                        return;
                    }
                    if (value == 'q')
                    {
                        if (debug)
                        {
                            Console.Error.WriteLine("jfat: stdin received q");
                        }
                        stopRequest.TrySetResult(new ProcessStopRequest("stdin-q", 0));
                        return;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }, CancellationToken.None);
    }

    private static IDisposable RegisterProcessSignalHandlers(TaskCompletionSource<ProcessStopRequest> stopRequest)
    {
        if (OperatingSystem.IsWindows())
        {
            Console.CancelKeyPress += OnCancelKeyPress;
            return new DelegateDisposable(() => Console.CancelKeyPress -= OnCancelKeyPress);
        }

        var registrations = new List<IDisposable>
        {
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
            {
                ctx.Cancel = true;
                stopRequest.TrySetResult(new ProcessStopRequest("SIGTERM", 143));
            }),
            PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
            {
                ctx.Cancel = true;
                stopRequest.TrySetResult(new ProcessStopRequest("SIGINT", 130));
            }),
            PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx =>
            {
                ctx.Cancel = true;
                stopRequest.TrySetResult(new ProcessStopRequest("SIGHUP", 129));
            }),
            PosixSignalRegistration.Create(PosixSignal.SIGQUIT, ctx =>
            {
                ctx.Cancel = true;
                stopRequest.TrySetResult(new ProcessStopRequest("SIGQUIT", 131));
            })
        };
        return new DelegateDisposable(() =>
        {
            foreach (var registration in registrations)
            {
                registration.Dispose();
            }
        });

        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            stopRequest.TrySetResult(new ProcessStopRequest("CTRL", 130));
        }
    }

    private static async Task<int> StopRemoteProcess(HttpClient controlClient, string baseUri, string token, string jobId, CancellationTokenSource remoteStop, ProcessStopRequest request)
    {
        Console.Error.WriteLine($"jfat: stopping remote job {jobId} after {request.Reason}");
        remoteStop.Cancel();
        await CancelRemoteProcess(controlClient, baseUri, token, jobId);
        return request.ExitCode;
    }

    private static async Task<RemoteJob> StartRemoteProcess(HttpClient client, string baseUri, string token, IReadOnlyList<string> remoteArgs, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUri + "/api/v1/remoteprocesses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new RemoteProcessRequest("ffmpeg", remoteArgs));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var job = await response.Content.ReadFromJsonAsync<RemoteJob>(cancellationToken);
        return job ?? throw new InvalidOperationException("Android did not return a remote job.");
    }

    private static async Task CancelRemoteProcess(HttpClient client, string baseUri, string token, string jobId)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, baseUri + "/api/v1/remoteprocesses/" + Uri.EscapeDataString(jobId));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch
        {
            // Best-effort cleanup so the next Jellyfin segment invocation can start.
        }
    }

    private static async Task<int> RemuxRemoteMpegtsToLocalHls(ShimConfig config, FfmpegCommand command, Stream remoteStdout, CancellationToken cancellationToken)
    {
        using var process = ProcessUtil.Start(config.RealFfmpegPath, BuildLocalHlsRemuxArgs(command), redirectInput: true, redirectOutput: false);
        using var cancellation = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        });
        try
        {
            await remoteStdout.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
        }
        catch (IOException)
        {
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch
            {
            }
        }
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    private static IReadOnlyList<string> BuildLocalHlsRemuxArgs(FfmpegCommand command)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-f", "mpegts",
            "-i", "pipe:0"
        };
        if (command.AudioRequested && !string.IsNullOrWhiteSpace(command.InputPath))
        {
            if (!string.IsNullOrWhiteSpace(command.SeekBeforeInput))
            {
                args.Add("-ss");
                args.Add(command.SeekBeforeInput);
            }
            args.Add("-i");
            args.Add(command.InputPath);
        }

        args.AddRange(["-map", "0:v:0"]);
        args.AddRange(["-codec:v:0", "copy"]);
        if (command.AudioRequested && !string.IsNullOrWhiteSpace(command.InputPath))
        {
            args.AddRange(["-map", "1:a:0"]);
            args.AddRange(["-codec:a:0", command.AudioCodec]);
            if (!string.IsNullOrWhiteSpace(command.AudioChannels))
            {
                args.Add("-ac");
                args.Add(command.AudioChannels);
            }
            if (!string.IsNullOrWhiteSpace(command.AudioBitrate))
            {
                args.Add("-ab");
                args.Add(command.AudioBitrate);
            }
            if (!string.IsNullOrWhiteSpace(command.AudioFilter))
            {
                args.Add("-af");
                args.Add(command.AudioFilter);
            }
        }
        else
        {
            args.Add("-an");
        }
        args.AddRange(["-sn", "-dn"]);
        args.AddRange(HlsArgsWithTempFile(command));
        args.Add(command.OutputPath!);
        return args;
    }

    private static IReadOnlyList<string> HlsArgsWithTempFile(FfmpegCommand command)
    {
        var hlsArgs = command.HlsArgs.ToList();
        if (string.Equals(command.ValueAfter("-hls_playlist_type"), "vod", StringComparison.OrdinalIgnoreCase))
        {
            RemoveOptionWithValue(hlsArgs, "-hls_playlist_type");
            return HlsArgsWithoutTempFile(hlsArgs);
        }

        var flagsIndex = hlsArgs.IndexOf("-hls_flags");
        if (flagsIndex >= 0 && flagsIndex + 1 < hlsArgs.Count)
        {
            hlsArgs[flagsIndex + 1] = HlsFlagsWithTempFile(command);
        }
        else
        {
            var fIndex = hlsArgs.IndexOf("-f");
            var insertAt = fIndex >= 0 ? Math.Min(fIndex + 2, hlsArgs.Count) : 0;
            hlsArgs.InsertRange(insertAt, ["-hls_flags", "temp_file"]);
        }
        return hlsArgs;
    }

    private static void RemoveOptionWithValue(List<string> args, string option)
    {
        var index = args.IndexOf(option);
        if (index >= 0)
        {
            args.RemoveRange(index, index + 1 < args.Count ? 2 : 1);
        }
    }

    private static IReadOnlyList<string> HlsArgsWithoutTempFile(List<string> hlsArgs)
    {
        var flagsIndex = hlsArgs.IndexOf("-hls_flags");
        if (flagsIndex < 0 || flagsIndex + 1 >= hlsArgs.Count)
        {
            return hlsArgs;
        }

        var flags = hlsArgs[flagsIndex + 1]
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(flag => !string.Equals(flag, "temp_file", StringComparison.Ordinal))
            .ToArray();
        if (flags.Length == 0)
        {
            hlsArgs.RemoveRange(flagsIndex, 2);
        }
        else
        {
            hlsArgs[flagsIndex + 1] = string.Join("+", flags);
        }
        return hlsArgs;
    }

    private static IReadOnlyList<string> BuildRemoteHlsArgs(ShimConfig config, FfmpegCommand command, MediaProbe probe, string? sourceUrl)
    {
        var bitrate = Math.Min(command.MaxRate, config.MaxBitrate);
        var (outputWidth, outputHeight) = OutputDimensions(command, probe);
        var hlsTime = command.ValueAfter("-hls_time") ?? command.GopSeconds.ToString(CultureInfo.InvariantCulture);
        var hlsSeconds = ParsePositiveDouble(hlsTime, command.GopSeconds);
        var gopFrames = Math.Max(1, (int)Math.Round(hlsSeconds * 24));
        var useHardwareDecoder = config.HardwareCodecsEnabled;
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning"
        };
        if (useHardwareDecoder)
        {
            args.AddRange([
                "-init_hw_device", "mediacodec=mc,create_window=1,surface_processor=1",
                "-hwaccel", "mediacodec",
                "-hwaccel_device", "mc",
                "-hwaccel_output_format", "mediacodec",
                "-c:v", "hevc_mediacodec",
                "-ndk_codec", "1"
            ]);
        }
        AddPreservedInputOptions(args, command, sourceUrl is not null);
        AddInputStreamDisables(args);
        args.AddRange(["-i", sourceUrl ?? "{input}"]);

        AddPreservedMaps(args, command);
        AddPreservedOutputOptions(args, command);

        if (useHardwareDecoder)
        {
            var tonemap = command.Tonemapx ?? TonemapxOptions.Default;
            args.AddRange([
                "-c:v", "h264_mediacodec",
                "-pix_fmt", "mediacodec",
                "-output_width", outputWidth.ToString(CultureInfo.InvariantCulture),
                "-output_height", Align16(outputHeight).ToString(CultureInfo.InvariantCulture),
                "-surface_tonemap", probe.IsHdr ? "1" : "0",
                "-surface_tonemap_algorithm", tonemap.Algorithm,
                "-surface_tonemap_transfer", tonemap.Transfer,
                "-surface_tonemap_matrix", tonemap.Matrix,
                "-surface_tonemap_primaries", tonemap.Primaries,
                "-surface_tonemap_range", tonemap.Range,
                "-surface_tonemap_format", tonemap.Format,
                "-surface_tonemap_desat", tonemap.Desat
            ]);
            if (!string.IsNullOrWhiteSpace(tonemap.Param))
            {
                args.AddRange(["-surface_tonemap_param", tonemap.Param]);
            }
            if (!string.IsNullOrWhiteSpace(tonemap.Peak))
            {
                args.AddRange(["-surface_tonemap_peak", tonemap.Peak]);
            }
        }
        else
        {
            args.AddRange([
                "-vf", $"scale={outputWidth}:{outputHeight}:flags=fast_bilinear,format=yuv420p",
                "-c:v", "h264_mediacodec",
                "-pix_fmt", "yuv420p"
            ]);
        }

        args.AddRange([
            "-b:v", bitrate.ToString(),
            "-maxrate", bitrate.ToString(),
            "-bufsize", (bitrate * 2).ToString(),
            "-g", gopFrames.ToString(CultureInfo.InvariantCulture)
        ]);
        AddPreservedEncoderOptions(args, command);
        AddKeyframeControls(args, command);
        if (command.AudioRequested)
        {
            args.AddRange(["-codec:a:0", RemoteAudioCodec(command.AudioCodec)]);
            if (!string.IsNullOrWhiteSpace(command.AudioChannels))
            {
                args.Add("-ac");
                args.Add(command.AudioChannels);
            }
            if (!string.IsNullOrWhiteSpace(command.AudioBitrate))
            {
                args.Add("-ab");
                args.Add(command.AudioBitrate);
            }
            if (!string.IsNullOrWhiteSpace(command.AudioFilter))
            {
                args.Add("-af");
                args.Add(command.AudioFilter);
            }
        }
        else
        {
            args.Add("-an");
        }

        args.AddRange(["-sn", "-dn"]);
        args.AddRange(RemoteHlsArgs(command));
        args.Add("{outputRoot}/" + Path.GetFileName(command.OutputPath!));
        return args;
    }

    private static IReadOnlyList<string> RemoteHlsArgs(FfmpegCommand command)
    {
        var hlsArgs = command.HlsArgs.ToList();
        hlsArgs.RemoveAll(arg => string.Equals(arg, "-copyts", StringComparison.Ordinal));
        hlsArgs.RemoveAll(arg => string.Equals(arg, "-start_at_zero", StringComparison.Ordinal));
        RemoveOptionWithValue(hlsArgs, "-avoid_negative_ts");
        RemoveOptionWithValue(hlsArgs, "-hls_playlist_type");
        var segmentIndex = hlsArgs.IndexOf("-hls_segment_filename");
        if (segmentIndex >= 0 && segmentIndex + 1 < hlsArgs.Count)
        {
            hlsArgs[segmentIndex + 1] = "{outputRoot}/" + Path.GetFileName(hlsArgs[segmentIndex + 1]);
        }
        var initIndex = hlsArgs.IndexOf("-hls_fmp4_init_filename");
        if (initIndex >= 0 && initIndex + 1 < hlsArgs.Count)
        {
            hlsArgs[initIndex + 1] = Path.GetFileName(hlsArgs[initIndex + 1]);
        }
        return HlsArgsWithoutTempFile(hlsArgs);
    }

    private static string RemoteAudioCodec(string codec) =>
        string.Equals(codec, "libfdk_aac", StringComparison.OrdinalIgnoreCase) ? "aac" : codec;

    private static IReadOnlyList<string> BuildRemoteMpegtsArgs(ShimConfig config, FfmpegCommand command, MediaProbe probe, string? sourceUrl)
    {
        var bitrate = Math.Min(command.MaxRate, config.MaxBitrate);
        var (outputWidth, outputHeight) = OutputDimensions(command, probe);
        var hlsTime = command.ValueAfter("-hls_time") ?? command.GopSeconds.ToString(CultureInfo.InvariantCulture);
        var hlsSeconds = ParsePositiveDouble(hlsTime, command.GopSeconds);
        var gopFrames = Math.Max(1, (int)Math.Round(hlsSeconds * 24));
        var useHardwareDecoder = config.HardwareCodecsEnabled;
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning"
        };
        if (useHardwareDecoder)
        {
            args.AddRange([
                "-init_hw_device", "mediacodec=mc,create_window=1,surface_processor=1",
                "-hwaccel", "mediacodec",
                "-hwaccel_device", "mc",
                "-hwaccel_output_format", "mediacodec",
                "-c:v", "hevc_mediacodec",
                "-ndk_codec", "1"
            ]);
        }
        AddPreservedInputOptions(args, command, sourceUrl is not null);
        AddInputStreamDisables(args);
        args.AddRange(["-i", sourceUrl ?? "{input}"]);
        AddPreservedVideoMap(args, command);
        AddPreservedOutputOptions(args, command);
        if (useHardwareDecoder)
        {
            var tonemap = command.Tonemapx ?? TonemapxOptions.Default;
            args.AddRange([
                "-c:v", "h264_mediacodec",
                "-pix_fmt", "mediacodec",
                "-output_width", outputWidth.ToString(CultureInfo.InvariantCulture),
                "-output_height", Align16(outputHeight).ToString(CultureInfo.InvariantCulture),
                "-surface_tonemap", probe.IsHdr ? "1" : "0",
                "-surface_tonemap_algorithm", tonemap.Algorithm,
                "-surface_tonemap_transfer", tonemap.Transfer,
                "-surface_tonemap_matrix", tonemap.Matrix,
                "-surface_tonemap_primaries", tonemap.Primaries,
                "-surface_tonemap_range", tonemap.Range,
                "-surface_tonemap_format", tonemap.Format,
                "-surface_tonemap_desat", tonemap.Desat
            ]);
            if (!string.IsNullOrWhiteSpace(tonemap.Param))
            {
                args.AddRange(["-surface_tonemap_param", tonemap.Param]);
            }
            if (!string.IsNullOrWhiteSpace(tonemap.Peak))
            {
                args.AddRange(["-surface_tonemap_peak", tonemap.Peak]);
            }
        }
        else
        {
            args.AddRange([
                "-vf", $"scale={outputWidth}:{outputHeight}:flags=fast_bilinear,format=yuv420p",
                "-c:v", "h264_mediacodec",
                "-pix_fmt", "yuv420p"
            ]);
        }
        args.AddRange([
            "-b:v", bitrate.ToString(),
            "-maxrate", bitrate.ToString(),
            "-bufsize", (bitrate * 2).ToString()
        ]);
        AddPreservedEncoderOptions(args, command);
        AddKeyframeControls(args, command);
        args.AddRange([
            "-g", gopFrames.ToString(CultureInfo.InvariantCulture),
            "-an",
            "-sn",
            "-dn",
            "-muxpreload", "0",
            "-muxdelay", "0",
            "-f", "mpegts",
            "pipe:1"
        ]);
        return args;
    }

    private static double ParsePositiveDouble(string value, double fallback)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;
    }

    private static void AddKeyframeControls(List<string> args, FfmpegCommand command)
    {
        var forceKeyFrames = command.ValueAfter("-force_key_frames:0") ?? command.ValueAfter("-force_key_frames");
        if (!string.IsNullOrWhiteSpace(forceKeyFrames))
        {
            args.Add("-force_key_frames");
            args.Add(forceKeyFrames);
        }
    }

    private static void AddPreservedInputOptions(List<string> args, FfmpegCommand command, bool seekHandledBySourceUrl)
    {
        for (var i = 0; i < command.InputOptions.Count; i++)
        {
            var option = command.InputOptions[i];
            if (seekHandledBySourceUrl && (option == "-ss" || option == "-noaccurate_seek"))
            {
                if (OptionTakesValue(option) && i + 1 < command.InputOptions.Count)
                {
                    i++;
                }
                continue;
            }

            if (PreservedInputOptions.Contains(option))
            {
                args.Add(option);
                if (OptionTakesValue(option) && i + 1 < command.InputOptions.Count)
                {
                    args.Add(command.InputOptions[++i]);
                }
            }
        }
    }

    private static void AddInputStreamDisables(List<string> args)
    {
        args.Add("-sn");
        args.Add("-dn");
    }

    private static void AddPreservedMaps(List<string> args, FfmpegCommand command)
    {
        if (command.PositiveMaps.Count == 0)
        {
            args.AddRange(["-map", "0:v:0"]);
            if (command.AudioRequested)
            {
                args.AddRange(["-map", "0:a:0?"]);
            }
            return;
        }

        foreach (var map in command.PositiveMaps)
        {
            args.Add("-map");
            args.Add(map);
        }
    }

    private static void AddPreservedVideoMap(List<string> args, FfmpegCommand command)
    {
        var videoMap = command.PositiveMaps.FirstOrDefault(map => map.StartsWith("0:", StringComparison.Ordinal));
        args.Add("-map");
        args.Add(videoMap ?? "0:v:0");
    }

    private static void AddPreservedOutputOptions(List<string> args, FfmpegCommand command)
    {
        AddFirstExistingOption(args, command, "-map_metadata");
        AddFirstExistingOption(args, command, "-map_chapters");
        AddFirstExistingOption(args, command, "-threads");
    }

    private static void AddPreservedEncoderOptions(List<string> args, FfmpegCommand command)
    {
        AddFirstExistingOption(args, command, "-profile:v:0", "-profile:v");
        AddFirstExistingOption(args, command, "-level:v:0", "-level:v", "-level");
        AddFirstExistingOption(args, command, "-g:v:0", "-g:v", "-g");
        AddFirstExistingOption(args, command, "-keyint_min:v:0", "-keyint_min:v", "-keyint_min");
    }

    private static void AddFirstExistingOption(List<string> args, FfmpegCommand command, params string[] options)
    {
        foreach (var option in options)
        {
            var value = command.ValueAfter(option);
            if (!string.IsNullOrWhiteSpace(value))
            {
                args.Add(option);
                args.Add(value);
                return;
            }
        }
    }

    private static void AddNoValueOptionIfPresent(List<string> args, FfmpegCommand command, string option)
    {
        if (command.Args.Contains(option) && !args.Contains(option))
        {
            args.Add(option);
        }
    }

    private static bool OptionTakesValue(string option) =>
        option is not "-noaccurate_seek" and not "-re";

    private static readonly HashSet<string> PreservedInputOptions = new(StringComparer.Ordinal)
    {
        "-analyzeduration",
        "-probesize",
        "-ss",
        "-noaccurate_seek",
        "-user_agent",
        "-headers",
        "-referer",
        "-rtsp_transport",
        "-rtsp_flags",
        "-async",
        "-fps_mode",
        "-vsync",
        "-re",
        "-readrate",
        "-fflags",
        "-f",
        "-stream_loop",
        "-reconnect_at_eof",
        "-reconnect_streamed",
        "-reconnect_delay_max"
    };

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
    private static int Align16(int value) => Math.Max(16, (value + 15) / 16 * 16);

    private static string EncodeRemoteArgs(IReadOnlyList<string> args)
    {
        var json = JsonSerializer.Serialize(args);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

}

public sealed record RemoteProcessRequest(
    [property: JsonPropertyName("executable")] string Executable,
    [property: JsonPropertyName("args")] IReadOnlyList<string> Args);
public sealed record RemoteJob(string Id, string StdinUrl, string FilesUrl, string? StdoutUrl);

public sealed record ProcessStopRequest(string Reason, int ExitCode);

public sealed class DelegateDisposable(Action dispose) : IDisposable
{
    public void Dispose() => dispose();
}

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
            if (string.Equals(remoteEvent, "ready", StringComparison.OrdinalIgnoreCase))
            {
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
        if (fileName.EndsWith(".m3u8.tmp", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^4];
        }
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
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1));
            if (read == 0)
            {
                return bytes.Length == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            }
            var value = buffer[0];
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

public static class SourceUrlSigner
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    public static string? TryCreate(ShimConfig config, string inputPath, string? seekBeforeInput)
    {
        if (string.IsNullOrWhiteSpace(config.JellyfinBaseUrl) ||
            string.IsNullOrWhiteSpace(config.SourceSecret) ||
            string.IsNullOrWhiteSpace(inputPath) ||
            !Path.IsPathRooted(inputPath))
        {
            return null;
        }

        var payload = JsonSerializer.Serialize(new SourceTicket(
            Path.GetFullPath(inputPath),
            DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds()));
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var key = Encoding.UTF8.GetBytes(config.SourceSecret);
        var sig = HMACSHA256.HashData(key, payloadBytes);
        var ticket = Base64Url(payloadBytes) + "." + Base64Url(sig);
        var url = config.JellyfinBaseUrl.TrimEnd('/') + "/AndroidTranscoder/Source/" + ticket;
        return string.IsNullOrWhiteSpace(seekBeforeInput)
            ? url
            : url + "?ss=" + Uri.EscapeDataString(seekBeforeInput);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record SourceTicket(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("exp")] long ExpiresUnixSeconds);

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
