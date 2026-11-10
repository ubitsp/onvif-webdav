using System.Text.Json;
using OnvifWebDav.Diagnostics;

namespace OnvifWebDav.Media;

public sealed record ProbeInfo(
    string VideoCodec, int Width, int Height, double DurationSeconds, bool HasAudio, string? AudioCodec,
    int AudioChannels = 0, int AudioSampleRate = 0)
{
    public bool IsHevc => VideoCodec.Contains("hevc", StringComparison.OrdinalIgnoreCase)
                          || VideoCodec.Contains("h265", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 音频参数是否够用来重采样。0 声道时 ffmpeg 会报
    /// "Neither number of channels nor channel layout specified" 然后整段合成失败，
    /// 所以这种情况必须提前判掉。
    /// </summary>
    public bool AudioUsable => HasAudio && AudioChannels > 0 && AudioSampleRate > 0;

    /// <summary>源音频已经是 AAC（MP4 原生支持，能直接 copy，不用重编码也不需要声道信息）。</summary>
    public bool IsAacAudio => AudioCodec?.Contains("aac", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// 把环形缓冲里的 TS 段合成一个可 seek 的 MP4，并出缩略图。
/// 编码判定：源是 H.265 且用户开了转码，或者用户强制转码 → libx264；否则 -c:v copy（H.265 直封加 hvc1 tag）。
/// </summary>
public sealed class ClipComposer
{
    private readonly FfmpegLocator _locator;
    private readonly AppLog? _log;

    public ClipComposer(FfmpegLocator locator, AppLog? log = null)
    {
        _locator = locator;
        _log = log;
    }

    public async Task<ProbeInfo?> ProbeAsync(string file, CancellationToken ct)
    {
        if (_locator.FfprobePath is null) return null;

        var result = await FfmpegRunner.RunAsync(_locator.FfprobePath, FfmpegArgs.ProbeFile(file),
            TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

        return result.Ok ? ParseProbeJson(result.StdOut) : null;
    }

    /// <summary>连 RTSP 探一下编码（判断是不是 H.265，决定要不要转码、要不要提示用户改摄像头设置）。</summary>
    public async Task<ProbeInfo?> ProbeStreamAsync(string rtspUrl, CancellationToken ct)
    {
        if (_locator.FfprobePath is null) return null;

        var result = await FfmpegRunner.RunAsync(_locator.FfprobePath, FfmpegArgs.ProbeStream(rtspUrl),
            TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

        return result.Ok ? ParseProbeJson(result.StdOut) : null;
    }

    private ProbeInfo? ParseProbeJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            string codec = "", audioCodec = "";
            int w = 0, h = 0;
            int channels = 0, sampleRate = 0;
            bool hasAudio = false;
            double duration = 0;

            if (doc.RootElement.TryGetProperty("streams", out var streams))
            {
                foreach (var s in streams.EnumerateArray())
                {
                    var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                    var cn = s.TryGetProperty("codec_name", out var c) ? c.GetString() : null;

                    if (type == "video" && string.IsNullOrEmpty(codec))
                    {
                        codec = cn ?? "";
                        w = s.TryGetProperty("width", out var we) ? we.GetInt32() : 0;
                        h = s.TryGetProperty("height", out var he) ? he.GetInt32() : 0;
                    }
                    else if (type == "audio")
                    {
                        hasAudio = true;
                        audioCodec = cn ?? "";
                        channels = TryInt(s, "channels");
                        sampleRate = TryInt(s, "sample_rate");
                    }
                }
            }

            if (doc.RootElement.TryGetProperty("format", out var fmt) &&
                fmt.TryGetProperty("duration", out var d) &&
                double.TryParse(d.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var dv))
                duration = dv;

            if (string.IsNullOrEmpty(codec)) return null;

            return new ProbeInfo(codec, w, h, duration, hasAudio, hasAudio ? audioCodec : null, channels,
                sampleRate);
        }
        catch (Exception ex)
        {
            _log?.Warn("ffprobe", $"解析探测结果失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>ffprobe JSON 里 channels 是数字、sample_rate 是字符串，两种都接住。</summary>
    private static int TryInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var n) ? n : 0,
            JsonValueKind.String => int.TryParse(v.GetString(), out var m) ? m : 0,
            _ => 0,
        };
    }

    public sealed record ComposeResult(bool Ok, string? Error, ProbeInfo? Info);

    /// <summary>合成 MP4。segments 必须是升序、互不重叠的 TS 文件。</summary>
    public async Task<ComposeResult> ComposeAsync(IReadOnlyList<string> segments, string workDir, string outMp4,
        bool forceTranscode, bool withAudio, CancellationToken ct)
    {
        if (_locator.FfmpegPath is null)
            return new ComposeResult(false, _locator.MissingMessage, null);

        if (segments.Count == 0)
            return new ComposeResult(false, "没有可用的录像分段（预录缓冲可能还没生成）", null);

        Directory.CreateDirectory(workDir);
        var concatFile = Path.Combine(workDir, "concat.txt");
        await File.WriteAllTextAsync(concatFile, FfmpegArgs.BuildConcatList(segments),
            new System.Text.UTF8Encoding(false), ct).ConfigureAwait(false);

        // 先探第一个段，判定是否需要转码 / 是否需要 hvc1 tag / 音频能不能用
        var probe = await ProbeAsync(segments[0], ct).ConfigureAwait(false);

        // 首段"有音频轨但解析不出声道"是很常见的（音频包不在段首）。
        // 换最后一段再探一次：只要那一段能解析出来，就说明整条流是有音频的，
        // 剩下的交给 -analyzeduration 让 ffmpeg 自己往后找，不必白白丢掉音轨。
        if (withAudio && probe is { HasAudio: true, AudioUsable: false } && segments.Count > 1)
        {
            var tail = await ProbeAsync(segments[^1], ct).ConfigureAwait(false);
            if (tail is { AudioUsable: true })
            {
                probe = tail;
                _log?.Info("ffmpeg",
                    $"首段音频参数缺失，改用末段探测结果（{tail.AudioCodec} {tail.AudioChannels}声道 " +
                    $"{tail.AudioSampleRate}Hz）");
            }
        }

        var needsTranscode = forceTranscode;
        var hvc1 = !needsTranscode && probe?.IsHevc == true;

        var (useAudio, copyAudio, audioNote) = DecideAudio(probe, withAudio);

        var timeout = TimeSpan.FromSeconds(Math.Clamp(segments.Count * 15 + 60, 60, 600));
        var audioDesc = !useAudio ? "无" : copyAudio ? "copy(aac)" : "转aac";

        _log?.Info("ffmpeg",
            $"开始合成 {segments.Count} 个分段 → {Path.GetFileName(outMp4)}" +
            $"（转码={needsTranscode}，音频={audioDesc}）");

        if (audioNote is not null) _log?.Warn("ffmpeg", audioNote);

        var args = FfmpegArgs.Compose(concatFile, outMp4, needsTranscode, useAudio, hvc1, copyAudio);
        var run = await FfmpegRunner.RunAsync(_locator.FfmpegPath, args, timeout, ct).ConfigureAwait(false);

        // 兜底：音频把整段合成搞挂了（首段音频参数解析不出、ADTS 头损坏等）。
        // 宁可丢声音也不能丢录像 —— 重试一次，这次彻底不要音轨。
        if (!Produced(run, outMp4) && useAudio)
        {
            var firstErr = string.IsNullOrWhiteSpace(run.StdErr) ? "（无输出）" : run.StdErr;
            _log?.Warn("ffmpeg",
                "带音频合成失败，改为无音频重试一次。原始错误：" + Shorten(firstErr, 600));

            var argsNoAudio = FfmpegArgs.Compose(concatFile, outMp4, needsTranscode, false, hvc1, false);
            run = await FfmpegRunner.RunAsync(_locator.FfmpegPath, argsNoAudio, timeout, ct)
                .ConfigureAwait(false);
        }

        if (!Produced(run, outMp4))
        {
            var err = string.IsNullOrWhiteSpace(run.StdErr) ? "ffmpeg 合成失败（无错误输出）" : run.StdErr;
            _log?.Error("ffmpeg", "合成失败：" + err);
            return new ComposeResult(false, err, null);
        }

        var info = await ProbeAsync(outMp4, ct).ConfigureAwait(false);

        if (info is not null && Math.Abs(info.DurationSeconds) < 0.1)
        {
            return new ComposeResult(false, "合成出来的 MP4 时长为 0，可能是分段内容损坏", info);
        }

        _log?.Info("ffmpeg",
            $"合成完成：{Path.GetFileName(outMp4)} {new FileInfo(outMp4).Length / 1024.0 / 1024.0:F1}MB " +
            $"{info?.Width}x{info?.Height} {info?.VideoCodec} {info?.DurationSeconds:F1}s");

        return new ComposeResult(true, null, info);
    }

    /// <summary>
    /// 决定这次合成怎么处理音频。返回 (是否要音轨, 是否直接 copy, 需要记日志的说明)。
    ///
    /// 关键判断：首段"声明了音频但解析不出声道/采样率"时（环形缓冲切在音频间隙里很常见），
    /// 重采样会直接失败并让整段录像报废，所以宁可不带音频。
    /// </summary>
    private static (bool UseAudio, bool CopyAudio, string? Note) DecideAudio(ProbeInfo? probe, bool withAudio)
    {
        if (!withAudio) return (false, false, null);

        // 探不出结果时保守维持原行为（带音频、转码），失败了还有一键"无音频重试"
        if (probe is null) return (true, false, null);

        if (!probe.HasAudio)
            return (false, false, "首段没有音频轨，按无音频合成");

        if (!probe.AudioUsable)
            return (false, false,
                $"首段音频参数不全（{probe.AudioCodec}，{probe.AudioChannels} 声道 / {probe.AudioSampleRate}Hz），" +
                "按无音频合成");

        return probe.IsAacAudio ? (true, true, null) : (true, false, null);
    }

    private static bool Produced(FfmpegRunner.RunResult run, string path) =>
        run.Ok && File.Exists(path) && new FileInfo(path).Length > 0;

    private static string Shorten(string text, int max)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (flat.Contains("  ", StringComparison.Ordinal)) flat = flat.Replace("  ", " ");
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    public async Task<string?> MakeThumbnailAsync(string mp4, string outJpg, CancellationToken ct)
    {
        if (_locator.FfmpegPath is null) return null;

        var run = await FfmpegRunner.RunAsync(_locator.FfmpegPath, FfmpegArgs.Thumbnail(mp4, outJpg),
            TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

        return run.Ok && File.Exists(outJpg) ? outJpg : null;
    }
}
