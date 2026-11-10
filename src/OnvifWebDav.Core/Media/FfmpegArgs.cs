namespace OnvifWebDav.Media;

/// <summary>
/// 所有 ffmpeg 命令行集中在这里，现场要调参数只改这个文件。
/// 路径统一转正斜杠 + 双引号（Windows 路径里的 ':' 不包引号会被当成协议解析）。
/// </summary>
public static class FfmpegArgs
{
    public static string Q(string path) => "\"" + path.Replace('\\', '/') + "\"";

    /// <summary>环形缓冲：常驻录制 TS 分段，2 秒一片。不要加 -nostdin（要发 q 优雅停）。</summary>
    public static string RingRecord(string rtspUrl, string outPattern, int segmentSeconds, bool withAudio)
    {
        var audio = withAudio ? "-map 0:a:0? " : "";
        var audioCodec = withAudio ? "" : "-an ";

        return "-hide_banner -nostats -loglevel warning -progress pipe:1 " +
               "-rtsp_transport tcp -timeout 10000000 -fflags +genpts -i " + Q(rtspUrl) + " " +
               "-map 0:v:0 " + audio +
               "-c copy " + audioCodec +
               "-f segment -segment_format mpegts -segment_time " + segmentSeconds + " " +
               "-segment_atclocktime 1 -reset_timestamps 1 -strftime 1 " +
               Q(outPattern);
    }

    /// <summary>触发式录制：直接写一个 MP4（无预录）。MP4 装不下 G.711，音频必须转 AAC 或直接丢。</summary>
    public static string TriggerRecord(string rtspUrl, string outFile, bool withAudio, bool forceH264)
    {
        var audio = withAudio ? "-map 0:a:0? -c:a aac -b:a 64k -ar 16000 -ac 1" : "-an";
        var video = forceH264
            ? "-c:v libx264 -preset veryfast -crf 26 -pix_fmt yuv420p -profile:v main -g 50"
            : "-c:v copy";

        return "-hide_banner -nostats -loglevel warning -progress pipe:1 " +
               "-rtsp_transport tcp -timeout 10000000 -fflags +genpts -i " + Q(rtspUrl) + " " +
               "-map 0:v:0 " + audio + " " + video + " " +
               "-movflags +faststart -f mp4 -y " + Q(outFile);
    }

    /// <summary>
    /// concat 合成 MP4。不转码时直接 copy 视频流（快、无损）。
    ///
    /// 音频三种走法（顺序很重要）：
    ///  · withAudio=false            → -an，彻底不要音轨
    ///  · copyAudio=true（源是 AAC） → -c:a copy，MP4 原生支持，不需要声道/采样率信息
    ///  · 其它（G.711 等）           → 转 AAC
    ///
    /// -analyzeduration/-probesize 调大是必须的：环形缓冲第一个分段经常"PMT 里声明了音频 PID
    /// 但没有音频包"（音频 PTS 比视频晚、或分段正好切在音频间隙），此时 ffmpeg 只探第一个文件
    /// 会报 "Could not find codec parameters ... 0 channels" 并直接放弃整段合成。
    /// 给足探测余量它会继续往后几个文件找，找不到再走"无音频"重试（ClipComposer 里）。
    /// </summary>
    public static string Compose(string concatListFile, string outMp4, bool transcodeH264, bool withAudio,
        bool hvc1Tag, bool copyAudio = false)
    {
        var audio = !withAudio
            ? "-an"
            : copyAudio
                ? "-map 0:a:0? -c:a copy"
                : "-map 0:a:0? -c:a aac -b:a 64k -ar 16000 -ac 1";

        string video;
        if (transcodeH264)
            video = "-c:v libx264 -preset veryfast -crf 26 -pix_fmt yuv420p -profile:v main -level 4.0 " +
                    "-g 50 -keyint_min 50 -sc_threshold 0";
        else
            video = "-c:v copy" + (hvc1Tag ? " -tag:v hvc1" : "");

        return "-hide_banner -y -loglevel warning -stats " +
               "-analyzeduration 50M -probesize 50M " +
               "-f concat -safe 0 -i " + Q(concatListFile) + " " +
               "-map 0:v:0 " + audio + " " + video + " " +
               "-movflags +faststart -avoid_negative_ts make_zero -f mp4 " + Q(outMp4);
    }

    public static string Thumbnail(string inMp4, string outJpg) =>
        "-hide_banner -y -loglevel error -ss 1 -i " + Q(inMp4) +
        " -frames:v 1 -vf scale=320:-2 -q:v 5 " + Q(outJpg);

    public static string ProbeStream(string rtspUrl) =>
        "-hide_banner -v error -rtsp_transport tcp -timeout 8000000 -select_streams v:0 " +
        "-show_entries stream=codec_name,width,height,avg_frame_rate -of json " + Q(rtspUrl);

    /// <summary>
    /// 探测本地文件。多给一点 analyzeduration/probesize：环形缓冲的段首经常只有视频包，
    /// 音频要往后几秒才出现，默认探测长度会报 "aac, 0 channels"。
    /// </summary>
    public static string ProbeFile(string file) =>
        "-hide_banner -v error -analyzeduration 5M -probesize 5M " +
        "-show_entries stream=codec_type,codec_name,width,height,channels,sample_rate " +
        "-show_entries format=duration -of json " + Q(file);

    /// <summary>生成 concat 清单。路径必须正斜杠；单引号按 ffconcat 规则转义。</summary>
    public static string BuildConcatList(IEnumerable<string> segmentPaths)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("ffconcat version 1.0");
        foreach (var path in segmentPaths)
        {
            var normalized = path.Replace('\\', '/');
            // ffconcat 里单引号包住路径，内部单引号需要 '\'' 形式
            var escaped = normalized.Replace("'", @"'\''");
            sb.AppendLine($"file '{escaped}'");
        }
        return sb.ToString();
    }
}
