using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

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
            return await AndroidTranscode.Run(config, command, probe);
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
                   ?? "/config/plugins/Jellyfin.Plugin.AndroidTranscoder/shim-config.json";
        if (!File.Exists(path))
        {
            return new ShimConfig(false, "", "", "/usr/lib/jellyfin-ffmpeg/ffmpeg",
                "/usr/lib/jellyfin-ffmpeg/ffprobe", 6_000_000);
        }

        var json = JsonSerializer.Deserialize<ShimConfig>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return json ?? throw new InvalidOperationException($"Invalid config: {path}");
    }
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

        return new FfmpegCommand
        {
            Args = args.ToArray(),
            InputPath = string.IsNullOrWhiteSpace(input) ? null : input,
            OutputPath = output,
            MaxRate = ParseBitrate(ValueAfter(args, "-maxrate"), 6_000_000),
            BufSize = ParseBitrate(ValueAfter(args, "-bufsize"), 12_000_000),
            Width = ParseScaleWidth(ValueAfter(args, "-vf")) ?? 1920,
            Height = ParseScaleHeight(ValueAfter(args, "-vf")) ?? 1080,
            GopSeconds = ParseForceKeySeconds(ValueAfter(args, "-force_key_frames:0")) ?? 3,
            AudioCodec = ValueAfter(args, "-codec:a:0") ?? "copy",
            AudioBitrate = ValueAfter(args, "-ab"),
            AudioChannels = ValueAfter(args, "-ac"),
            AudioFilter = ValueAfter(args, "-af"),
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
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private static int? ParseScaleHeight(string? vf)
    {
        if (vf is null)
        {
            return null;
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
            "-show_entries", "stream=codec_name,pix_fmt,color_space,color_transfer,color_primaries",
            "-of", "json",
            inputPath
        };
        var output = await ProcessUtil.Capture(ffprobePath, args);
        using var document = JsonDocument.Parse(output);
        var stream = document.RootElement.GetProperty("streams")[0];
        return new MediaProbe(
            GetString(stream, "codec_name") ?? "",
            GetString(stream, "pix_fmt") ?? "",
            GetString(stream, "color_space"),
            GetString(stream, "color_transfer"),
            GetString(stream, "color_primaries"));
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
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
    public static async Task<int> Run(ShimConfig config, FfmpegCommand command, MediaProbe probe)
    {
        var producer = StartProducer(config.RealFfmpegPath, command.InputPath!);
        await using var producerGuard = new ProcessGuard(producer);

        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var uri = BuildUri(config, command, probe);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        request.Content = new StreamContent(producer.StandardOutput.BaseStream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("video/x-matroska");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var packager = StartPackager(config.RealFfmpegPath, command);
        await using var packagerGuard = new ProcessGuard(packager);
        await using (var video = await response.Content.ReadAsStreamAsync())
        {
            await video.CopyToAsync(packager.StandardInput.BaseStream);
            packager.StandardInput.Close();
        }

        var producerExit = await producer.WaitForExitAsync().ContinueWith(_ => producer.ExitCode);
        var packagerExit = await packager.WaitForExitAsync().ContinueWith(_ => packager.ExitCode);
        return producerExit == 0 ? packagerExit : producerExit;
    }

    private static Uri BuildUri(ShimConfig config, FfmpegCommand command, MediaProbe probe)
    {
        var toneMap = probe.IsHdr ? 1 : 0;
        var bitrate = Math.Min(command.MaxRate, config.MaxBitrate);
        var query = $"codec=h264&width={command.Width}&height={command.Height}&bitrate={bitrate}&maxrate={bitrate}&bufsize={Math.Max(command.BufSize, bitrate * 2)}&gop={command.GopSeconds * 40}&toneMap={toneMap}";
        return new Uri($"{config.AndroidBaseUrl.TrimEnd('/')}/api/v1/transcode?{query}");
    }

    private static Process StartProducer(string ffmpegPath, string inputPath)
    {
        var args = new[] { "-hide_banner", "-loglevel", "warning", "-i", $"file:{inputPath}", "-map", "0:v:0", "-c", "copy", "-f", "matroska", "pipe:1" };
        return ProcessUtil.Start(ffmpegPath, args, redirectInput: false, redirectOutput: true);
    }

    private static Process StartPackager(string ffmpegPath, FfmpegCommand command)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning",
            "-i", "pipe:0",
            "-i", $"file:{command.InputPath}",
            "-map_metadata", "-1",
            "-map_chapters", "-1",
            "-map", "0:v:0",
            "-map", "1:a:0?",
            "-map", "-1:s",
            "-codec:v:0", "copy",
            "-codec:a:0", command.AudioCodec
        };
        if (command.AudioChannels is not null)
        {
            args.AddRange(["-ac", command.AudioChannels]);
        }
        if (command.AudioBitrate is not null)
        {
            args.AddRange(["-ab", command.AudioBitrate]);
        }
        if (command.AudioFilter is not null)
        {
            args.AddRange(["-af", command.AudioFilter]);
        }
        args.AddRange(command.HlsArgs);
        args.Add(command.OutputPath!);
        return ProcessUtil.Start(ffmpegPath, args, redirectInput: true, redirectOutput: false);
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
