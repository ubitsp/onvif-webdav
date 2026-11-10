using System.Globalization;

namespace OnvifWebDav.Media;

/// <summary>环形缓冲里的一段 TS。StartLocal 来自文件名，即这一段的起始时间。</summary>
public sealed record RingSegment(string Path, DateTime StartLocal, string Name);

/// <summary>
/// TS 分段的环形缓冲管理。
/// ffmpeg 用 -strftime 1 写出 yyyyMMdd_HHmmss.ts，文件名即段的起始本地时间。
///
/// 关键设计一：保护标记必须存在实例字段里，不能挂在每次 Scan 新建的对象上 ——
/// 否则下一轮 Scan 就丢了，旧段会被 Trim 删掉，正在录的片段就成了碎片。
///
/// 关键设计二：保护用「令牌制」而不是单个 DateTime。上一段正在合成、下一段已经开始录的时候，
/// 会同时存在两个活动保护。单个字段会被下一段覆盖（保护提前），或者被上一段收尾时清空（保护丢失），
/// 两种都真实发生过，后者会让正在录的分段被 Trim 删掉，合出来就是残片。
/// 令牌制下各自释放自己的，Trim 取所有活动令牌里最早的那个。
/// </summary>
public sealed class SegmentRing
{
    private readonly object _gate = new();
    private readonly string _dir;
    private readonly int _segmentSeconds;

    /// <summary>token → 保护起点。多个 token 表示有多段同时在进行（合成中 + 录制中）。</summary>
    private readonly Dictionary<long, DateTime> _protections = new();
    private long _nextToken;

    private sealed record Seg(string Path, DateTime StartLocal, string Name);

    public SegmentRing(string directory, int segmentSeconds)
    {
        _dir = directory;
        _segmentSeconds = segmentSeconds;
        System.IO.Directory.CreateDirectory(_dir);
    }

    public string DirPath => _dir;

    private List<Seg> Scan()
    {
        var list = new List<Seg>();
        if (!System.IO.Directory.Exists(_dir)) return list;

        foreach (var file in System.IO.Directory.EnumerateFiles(_dir, "*.ts"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!DateTime.TryParseExact(name, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var start))
                continue;

            list.Add(new Seg(file, start, name));
        }

        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    /// <summary>
    /// 从某一时刻起保护，返回令牌。片段收尾时必须用同一个令牌 Release。
    /// 多个令牌并存时，Trim 以「最早的那个」为准。
    /// </summary>
    public long ProtectFrom(DateTime localStart)
    {
        lock (_gate)
        {
            var token = ++_nextToken;
            _protections[token] = localStart.AddSeconds(-_segmentSeconds);
            return token;
        }
    }

    /// <summary>只释放自己那个令牌，不会影响别的片段。</summary>
    public void Release(long token)
    {
        if (token == 0) return;
        lock (_gate) _protections.Remove(token);
    }

    /// <summary>调用方必须已持有 _gate。</summary>
    private DateTime? EarliestProtectionLocked()
    {
        DateTime? earliest = null;

        foreach (var start in _protections.Values)
            if (earliest is null || start < earliest)
                earliest = start;

        return earliest;
    }

    /// <summary>当前有几个片段正占用环形缓冲（正常是 0 或 1，收尾与新片段交替时可能是 2）。</summary>
    public int ActiveProtections
    {
        get { lock (_gate) return _protections.Count; }
    }

    /// <summary>取 [start, end] 窗口内的段（升序）。自动排除当前正在写的最后一个文件。</summary>
    public List<RingSegment> Collect(DateTime startLocal, DateTime endLocal)
    {
        lock (_gate)
        {
            var all = Scan();
            if (all.Count <= 1) return new List<RingSegment>();

            // 最后一个文件可能仍在写入，先排除
            var usable = all.Take(all.Count - 1).ToList();

            // 容差按真实段长算：段长 12 秒、窗口 7 秒时，起点略早于窗口的那一段必须带上，
            // 否则会整段录像丢掉（线上日志里"窗口内没有可用分段"就是这么来的）。
            var tolerance = EstimateSegmentSeconds(usable, _segmentSeconds);

            var matched = usable
                .Where(s => s.StartLocal >= startLocal.AddSeconds(-tolerance)
                            && s.StartLocal <= endLocal)
                .Select(s => new RingSegment(s.Path, s.StartLocal, s.Name))
                .ToList();

            if (matched.Count > 0) return matched;

            // 退化：窗口整个落在某一段内部（段长远大于窗口），取包含 endLocal 的那一段。
            var covering = usable.LastOrDefault(s => s.StartLocal <= endLocal);
            if (covering is null) return matched;

            return new List<RingSegment> { new(covering.Path, covering.StartLocal, covering.Name) };
        }
    }

    /// <summary>
    /// 实际观测到的段长（秒），用相邻段起点差的中位数估计。
    ///
    /// 为什么不能直接用配置的 SegmentSeconds：`-f segment` 只能按关键帧切，
    /// 摄像头 GOP 是 12 秒时，配置写 2 秒也会切出 12 秒的段。
    /// 窗口算法、等待超时都必须按真实段长来算，否则会"永远等不到 / 永远切不到"。
    /// </summary>
    public int ObservedSegmentSeconds()
    {
        lock (_gate) return EstimateSegmentSeconds(Scan(), _segmentSeconds);
    }

    private static int EstimateSegmentSeconds(List<Seg> all, int fallback)
    {
        if (all.Count < 2) return fallback;

        var deltas = new List<double>(all.Count - 1);
        for (var i = 1; i < all.Count; i++)
            deltas.Add((all[i].StartLocal - all[i - 1].StartLocal).TotalSeconds);

        if (deltas.Count == 0) return fallback;

        // 用中位数：中间被 Trim 掉一段时，个别巨大的间隔不会把估计值带偏
        deltas.Sort();
        var median = deltas[deltas.Count / 2];

        return Math.Max(fallback, (int)Math.Round(median));
    }

    /// <summary>
    /// 包含 <paramref name="local"/> 时刻的那一段是否已经写完。
    /// 判据：存在一个起点晚于它的分段（说明它已经被切掉、不再写入）。
    /// 比"最后一个完整段的起点 >= t"精确得多 —— 段长 12 秒时能早 10 秒拿到数据。
    /// </summary>
    public bool IsSegmentClosed(DateTime local)
    {
        lock (_gate)
        {
            var all = Scan();
            return all.Count >= 2 && all[^1].StartLocal > local;
        }
    }

    /// <summary>最后一个"已经写完"的段的起始时间。判断环形缓冲是否已覆盖到某个时间点。</summary>
    public DateTime? LastCompleteSegmentStart()
    {
        lock (_gate)
        {
            var all = Scan();
            return all.Count <= 1 ? null : all[^2].StartLocal;
        }
    }

    /// <summary>当前环形目录占用的字节数（给界面显示 / 判断磁盘压力）。</summary>
    public long TotalBytes()
    {
        lock (_gate)
        {
            long total = 0;
            foreach (var seg in Scan())
            {
                try { total += new FileInfo(seg.Path).Length; } catch { }
            }

            return total;
        }
    }

    /// <summary>
    /// 淘汰未保护的旧段，保留 keepSeconds 秒的缓冲。
    /// <paramref name="maxBytes"/> &gt; 0 时再按磁盘占用兜底 —— 按时间淘汰算错了也不会吃满盘。
    /// </summary>
    public void Trim(int keepSeconds, long maxBytes = 0)
    {
        lock (_gate)
        {
            var all = Scan();
            if (all.Count <= 2) return;

            var cutoff = DateTime.Now.AddSeconds(-Math.Max(keepSeconds, _segmentSeconds * 2));
            var protectFrom = EarliestProtectionLocked();

            // 最后一个（正在写）永不删
            foreach (var seg in all.Take(all.Count - 1))
            {
                if (protectFrom is not null && seg.StartLocal >= protectFrom) continue;
                if (seg.StartLocal > cutoff) continue;
                try { File.Delete(seg.Path); } catch { /* 可能被占用，下轮再删 */ }
            }

            if (maxBytes > 0) TrimByBytesLocked(maxBytes);
        }
    }

    /// <summary>
    /// 按字节从最旧的开始删，只删没保护的（正在录/正在合成的段一律不动）。
    /// 极端情况下"受保护的段加起来就超过上限"，这时删不动是正常的 —— 上层要告警。
    /// </summary>
    private void TrimByBytesLocked(long maxBytes)
    {
        var all = Scan();
        if (all.Count <= 1) return;

        long total = 0;
        var sizes = new Dictionary<string, long>(all.Count);

        foreach (var seg in all)
        {
            long len = 0;
            try { len = new FileInfo(seg.Path).Length; } catch { }
            sizes[seg.Name] = len;
            total += len;
        }

        if (total <= maxBytes) return;

        var protectFrom = EarliestProtectionLocked();

        foreach (var seg in all.Take(all.Count - 1))
        {
            if (total <= maxBytes) break;
            if (protectFrom is not null && seg.StartLocal >= protectFrom) continue;

            try
            {
                File.Delete(seg.Path);
                total -= sizes.GetValueOrDefault(seg.Name);
            }
            catch { }
        }
    }

    /// <summary>清掉上次残留的孤儿段（进程被强杀后会有）。</summary>
    public void CleanAll()
    {
        lock (_gate)
        {
            foreach (var seg in Scan())
            {
                try { File.Delete(seg.Path); } catch { }
            }

            _protections.Clear();
        }
    }
}
