using OnvifWebDav.Config;
using OnvifWebDav.Diagnostics;
using OnvifWebDav.Storage;

namespace OnvifWebDav.Pipeline;

/// <summary>
/// 按天数清理旧录像。
///
/// 和「云端空间守卫」互补：
///  · 空间守卫看剩余容量，撑不住时删最旧的腾地方（被动）
///  · 这里看日期，超过保留天数的一律删（主动，不管空间还剩多少）
///
/// 执行时机：每天在配置的 CheckHour 点跑一次。用 10 分钟粒度轮询，
/// 所以实际会在 CheckHour 那一小时内的某个整 10 分钟触发。
/// </summary>
public sealed class RetentionService
{
    private readonly Func<IRemoteStorage?> _storageFactory;
    private readonly Func<AppConfig> _configProvider;
    private readonly Func<string?> _cameraIdProvider;
    private readonly AppLog _log;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>上次执行的日期（本地）。同一天不重复跑。</summary>
    private DateTime _lastRunDate = DateTime.MinValue;

    public RetentionService(Func<IRemoteStorage?> storageFactory, Func<AppConfig> configProvider,
        Func<string?> cameraIdProvider, AppLog log)
    {
        _storageFactory = storageFactory;
        _configProvider = configProvider;
        _cameraIdProvider = cameraIdProvider;
        _log = log;
    }

    /// <summary>上次执行的结果，UI 可以拿来显示。</summary>
    public string LastResult { get; private set; } = "尚未执行";

    public DateTime? LastRunUtc { get; private set; }

    public void Start()
    {
        if (_loop is not null) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _loop = Task.Run(async () =>
        {
            // 启动后先等 2 分钟，避开启动时的订阅/录制高峰
            try { await Task.Delay(TimeSpan.FromMinutes(2), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.Warn("retention", "保留策略执行异常：" + ex.Message);
                }

                try { await Task.Delay(TimeSpan.FromMinutes(10), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }, token);

        _log.Info("retention", "录像保留策略已启动");
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        try { _cts.Cancel(); } catch { }

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    /// <summary>到点了就清一次。返回 true 表示这次真的执行了。</summary>
    private async Task<bool> TickAsync(CancellationToken ct)
    {
        var cfg = _configProvider();
        var keepDays = cfg.Retention.KeepDays;

        if (keepDays <= 0) return false; // 0 = 永久保留，什么都不做

        var now = DateTime.Now;

        if (now.Hour != cfg.Retention.CheckHour) return false;      // 还没到点
        if (_lastRunDate.Date == now.Date) return false;            // 今天跑过了

        // 先标记，避免同一小时内重复触发（即使本次失败，也等明天）
        _lastRunDate = now.Date;

        await RunOnceAsync(keepDays, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>立即执行一次（UI 的"立即清理"按钮也用它）。</summary>
    public async Task RunOnceAsync(int keepDays, CancellationToken ct)
    {
        if (keepDays <= 0)
        {
            _log.Info("retention", "保留天数为 0（永久保留），跳过清理");
            return;
        }

        var cameraId = _cameraIdProvider();

        if (string.IsNullOrWhiteSpace(cameraId))
        {
            _log.Warn("retention", "没有启用的摄像头，跳过清理");
            return;
        }

        using var storage = _storageFactory();

        if (storage is null)
        {
            _log.Warn("retention", "存储后端未就绪，本轮保留策略跳过");
            return;
        }

        // 保留 KeepDays 天（含今天）：设 7 就是今天 + 前 6 天，删掉更早的
        var keepFrom = DateTime.Today.AddDays(-(keepDays - 1));

        _log.Info("retention",
            $"开始清理：保留 {keepDays} 天（{keepFrom:yyyy-MM-dd} 起），摄像头 {cameraId}");

        var pruner = new RetentionPruner();
        var result = await pruner.PruneOlderThanAsync(storage, cameraId, keepFrom, _log, ct)
            .ConfigureAwait(false);

        LastRunUtc = DateTime.UtcNow;
        LastResult = result.DaysDeleted == 0
            ? $"无需清理（保留自 {keepFrom:yyyy-MM-dd}）"
            : $"已删除 {result.DaysDeleted} 天 / {result.ClipsDeleted} 段";
    }

    /// <summary>重置"今天已执行"标记，让下一次 Tick 可以再跑（改配置后调）。</summary>
    public void ResetSchedule() => _lastRunDate = DateTime.MinValue;
}
