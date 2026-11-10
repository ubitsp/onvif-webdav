namespace OnvifWebDav.Media;

/// <summary>
/// 找 ffmpeg / ffprobe。搜索顺序：
/// 1) exe 目录 \ffmpeg\ffmpeg.exe   2) exe 目录 \ffmpeg.exe
/// 3) exe 目录 \ffmpeg\bin\ffmpeg.exe  4) PATH
/// 5) 向上找仓库里的 third_party\ffmpeg（开发期免复制）
/// </summary>
public sealed class FfmpegLocator
{
    public string? FfmpegPath { get; }
    public string? FfprobePath { get; }
    public bool IsAvailable => FfmpegPath is not null;

    public string? SearchedHint { get; }

    public FfmpegLocator()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(baseDir, "ffmpeg.exe"),
            Path.Combine(baseDir, "ffmpeg", "bin", "ffmpeg.exe"),
        };

        // 开发期：从输出目录往上找 third_party
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, "third_party", "ffmpeg", "bin", "ffmpeg.exe"));
            candidates.Add(Path.Combine(dir.FullName, "third_party", "ffmpeg", "ffmpeg.exe"));
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(p.Trim(), "ffmpeg.exe")); } catch { }
        }

        FfmpegPath = candidates.FirstOrDefault(File.Exists);

        if (FfmpegPath is not null)
        {
            var dirName = Path.GetDirectoryName(FfmpegPath)!;
            var probe = Path.Combine(dirName, "ffprobe.exe");
            FfprobePath = File.Exists(probe) ? probe : null;
        }

        SearchedHint = string.Join("；", candidates.Take(6));
    }

    /// <summary>没有 ffmpeg 时给用户看的中文提示。</summary>
    public string MissingMessage =>
        "找不到 ffmpeg.exe。请运行仓库里的 scripts\\get-ffmpeg.ps1 自动下载，" +
        "或手动把 ffmpeg.exe / ffprobe.exe 放到程序目录下的 ffmpeg 文件夹里。" +
        Environment.NewLine + "已尝试路径：" + Environment.NewLine + SearchedHint;
}
