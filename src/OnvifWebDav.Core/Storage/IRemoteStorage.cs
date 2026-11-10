namespace OnvifWebDav.Storage;

/// <summary>
/// 远端存储的统一抽象。
///
/// 原本只有 WebDAV 一种后端，所有调用点都直接依赖 <c>WebDavClient</c>。
/// 要接第二个后端（夸克网盘）就必须先把"存储能做什么"固定下来，
/// 否则每加一个后端就得改一遍 UploadQueue / RemoteClipIndex / MediaCache。
///
/// 约定：所有路径都是**相对存储根目录**的（"/cam1/2026-11-10/a.mp4"），
/// 不含配置里的 RootPath，根目录由各实现自己拼。
/// </summary>
public interface IRemoteStorage : IDisposable
{
    /// <summary>日志和 UI 里显示的名字，如 "WebDAV"、"夸克网盘"。</summary>
    string DisplayName { get; }

    /// <summary>
    /// 是否支持服务端原子改名（WebDAV 的 MOVE）。
    /// 不支持时上传必须先写临时名再改名这一步就不成立，只能直接写最终路径。
    /// </summary>
    bool SupportsMove { get; }

    /// <summary>
    /// 删除后配额是否**立即**释放。
    /// 网盘类后端（夸克）删除只是进回收站，配额要等回收站清理才回来 ——
    /// 配额守卫如果按"删了就有空间"来算，会把录像删光却还是放不下新片段。
    /// </summary>
    bool QuotaReleasesImmediately { get; }

    /// <summary>确保目录存在（含所有父级）。已存在不算错。</summary>
    Task EnsureDirectoryAsync(string relativeDir, CancellationToken ct);

    /// <summary>上传本地文件到指定路径（覆盖）。</summary>
    Task PutFileAsync(string relativePath, string localFile, IProgress<long>? progress, CancellationToken ct);

    /// <summary>服务端改名。返回 false 表示后端不支持或失败（调用方要能降级）。</summary>
    Task<bool> MoveAsync(string fromRel, string toRel, bool overwrite, CancellationToken ct);

    /// <summary>查询配额。不支持的后端返回 null。</summary>
    Task<StorageQuota?> GetQuotaAsync(CancellationToken ct);

    /// <summary>取远端文件长度（HEAD）。不存在返回 null。</summary>
    Task<long?> HeadLengthAsync(string relativePath, CancellationToken ct);

    /// <summary>读文本 + 版本标识（WebDAV 是 ETag，夸克是合成版本号）。不存在返回 (null, null)。</summary>
    Task<(string? Content, string? ETag)> GetStringWithEtagAsync(string relativePath, CancellationToken ct);

    /// <summary>
    /// 条件写文本（乐观并发）。ifMatch 为 null 表示不校验。
    /// 返回 false 表示版本冲突（412）或后端不支持条件写。
    /// </summary>
    Task<bool> PutStringAsync(string relativePath, string content, string? ifMatch, CancellationToken ct);

    /// <summary>下载到本地文件。</summary>
    Task DownloadToFileAsync(string relativePath, string localFile, IProgress<long>? progress, CancellationToken ct);

    /// <summary>
    /// 列目录。depth=0 只列自身，depth=1 列直接子项。
    /// 返回项的 Href 是相对路径，Name 已由实现填好。
    /// </summary>
    Task<List<RemoteEntry>> ListAsync(string relativePath, int depth, CancellationToken ct);

    /// <summary>删除文件或目录。已不存在不算错。</summary>
    Task DeleteAsync(string relativePath, CancellationToken ct);
}

/// <summary>
/// 远端目录项。原来叫 DavEntry，接第二个后端后改名 —— 它不该带厂商色彩。
/// </summary>
/// <param name="Id">
/// 后端内部标识。WebDAV 不需要（路径即标识），夸克必须用 fid ——
/// 否则每次拿文件元信息都要再列一遍父目录去反查。
/// </param>
public sealed record RemoteEntry(
    string Href, bool IsCollection, long Length, DateTimeOffset? LastModified, string? ETag,
    string? Id = null)
{
    /// <summary>Href 的最后一段（已 URL 解码）。</summary>
    public string Name
    {
        get
        {
            var path = Href.TrimEnd('/');
            var idx = path.LastIndexOf('/');
            var raw = idx < 0 ? path : path[(idx + 1)..];
            return Uri.UnescapeDataString(raw);
        }
    }
}

/// <summary>云端配额（RFC 4331）。aliyundrive-webdav 支持，有些服务端不返回。</summary>
public sealed record StorageQuota(long? UsedBytes, long? AvailableBytes, DateTime CheckedUtc)
{
    private const double Gb = 1024d * 1024d * 1024d;

    public long? TotalBytes => UsedBytes is { } u && AvailableBytes is { } a ? u + a : null;
    public double UsedGb => UsedBytes is { } u ? u / Gb : -1;
    public double AvailableGb => AvailableBytes is { } a ? a / Gb : -1;
    public double TotalGb => TotalBytes is { } t ? t / Gb : -1;

    public string Describe()
    {
        if (AvailableBytes is null && UsedBytes is null) return "未知";

        var parts = new List<string>(3);
        if (TotalBytes is not null) parts.Add($"总 {TotalGb:F0} GB");
        if (UsedBytes is not null) parts.Add($"已用 {UsedGb:F1} GB");
        if (AvailableBytes is not null) parts.Add($"剩余 {AvailableGb:F1} GB");

        return string.Join(" / ", parts) + $"（{CheckedUtc.ToLocalTime():HH:mm} 查询）";
    }
}
