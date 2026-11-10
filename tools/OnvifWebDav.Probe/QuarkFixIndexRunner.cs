using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OnvifWebDav.Diagnostics;
using OnvifWebDav.Quark;
using OnvifWebDav.Storage;

namespace OnvifWebDav.Probe;

/// <summary>
/// 修复"同名不覆盖"造成的索引损坏。
///
/// 现象：index 目录里同一天有 `2026-11-11.json`、`2026-11-11(1).json` … `(37).json`。
///
/// 根因链：
///  1. 夸克同名上传不覆盖，而是生成 `name(N).ext` 副本 → 主文件永远停在第一次写入的内容
///  2. AddAsync 读的是固定路径（主文件）→ 永远读到"第一条"
///  3. 于是每次写入 = 主文件的第 1 条 + 当前这条 = 2 条 → 存成新副本
///
/// 所以**每个副本里都藏着一条独特的记录**，只取最新那份会丢掉其余所有。
/// 正确做法：把该组所有文件的 clips 合并、按 id 去重、按开始时间排序，再写回主文件名。
///
/// 默认只预览，加 --apply 才真正改动。
/// </summary>
internal static class QuarkFixIndexRunner
{
    /// <summary>把 `2026-11-10(3).json` 还原成 `2026-11-10`。</summary>
    private static readonly Regex CopySuffix =
        new(@"^(?<base>.+?)\((?<n>\d+)\)$", RegexOptions.Compiled);

    /// <summary>索引文件的序列化参数，必须和 RemoteClipIndex 保持一致（camelCase）。</summary>
    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<int> RunAsync(Dictionary<string, string> o)
    {
        var port = int.TryParse(o.GetValueOrDefault("port", "9222"), out var p) && p > 0 ? p : 9222;
        var rootOpt = o.GetValueOrDefault("root", "/onvif");
        var dir = o.GetValueOrDefault("dir", "cam1/index");
        var apply = o.ContainsKey("apply");

        var log = new AppLog(Path.Combine(Path.GetTempPath(), "owd-probe-logs"));
        using var cdp = new QuarkCdpClient(port, log);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));

        var session = await cdp.TryGetSessionAsync(cts.Token).ConfigureAwait(false);
        if (session is null)
        {
            Console.WriteLine("✘ 拿不到会话。先跑 quark-cdp 确认客户端在跑且已登录。");
            return 1;
        }

        using var api = new QuarkApiClient(session, log);
        using var storage = new QuarkStorage(api, log, null, rootOpt);

        Console.WriteLine($"索引目录：{rootOpt?.TrimEnd('/')}/{dir}");
        Console.WriteLine(apply ? "模式：执行（--apply）" : "模式：预览（加 --apply 才真正改动）");
        Console.WriteLine();

        var entries = await storage.ListAsync(dir, 1, cts.Token).ConfigureAwait(false);

        if (entries.Count == 0)
        {
            Console.WriteLine("目录为空，没什么可修的。");
            return 0;
        }

        var groups = entries
            .Where(e => !e.IsCollection && e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => StripSuffix(e.Name))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var needFix = groups.Where(g => g.Count() > 1).ToList();

        Console.WriteLine($"共 {groups.Count} 天，其中 {needFix.Count} 天有副本需要合并");
        Console.WriteLine();

        if (needFix.Count == 0)
        {
            Console.WriteLine("✅ 没有副本，索引是干净的");
            return 0;
        }

        var fixedDays = 0;
        var deleted = 0;
        var failed = 0;

        foreach (var group in needFix)
        {
            var main = group.Key + ".json";
            Console.WriteLine($"[{group.Key}] {group.Count()} 个文件");

            // ---- 合并所有文件里的 clips，按 id 去重 ----
            JsonObject? template = null;
            var merged = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            var readFailed = 0;

            foreach (var e in group.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                cts.Token.ThrowIfCancellationRequested();

                try
                {
                    var (text, _) = await storage
                        .GetStringWithEtagAsync(dir.TrimEnd('/') + "/" + e.Name, cts.Token)
                        .ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(text)) continue;

                    if (JsonNode.Parse(text) is not JsonObject obj) continue;

                    // 第一个能解析的当模板（保留 version / cameraId / date 这些字段）
                    template ??= (JsonObject)obj.DeepClone();

                    if (obj["clips"] is not JsonArray clips) continue;

                    foreach (var clip in clips)
                    {
                        var id = clip?["id"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(id)) continue;

                        merged[id] = clip!.DeepClone();
                    }
                }
                catch (Exception ex)
                {
                    readFailed++;
                    Console.WriteLine($"    ⚠ 读 {e.Name} 失败：{ex.GetType().Name}");
                }
            }

            if (template is null || merged.Count == 0)
            {
                Console.WriteLine("    ✘ 一份都读不出来，跳过这一天");
                failed++;
                continue;
            }

            // ---- 组装合并结果 ----
            var ordered = merged
                .OrderBy(kv => kv.Value?["startUtc"]?.GetValue<string>() ?? "", StringComparer.Ordinal)
                .Select(kv => kv.Value!.DeepClone())
                .ToArray();

            var outObj = (JsonObject)template.DeepClone();
            outObj["clips"] = new JsonArray(ordered);
            outObj["updatedUtc"] = DateTime.UtcNow.ToString("O");

            var json = outObj.ToJsonString(WriteOpts);

            Console.WriteLine($"    合并后 {merged.Count} 条记录{Describe(ordered)}" +
                              (readFailed > 0 ? $"（{readFailed} 份读取失败）" : ""));

            if (!apply)
            {
                Console.WriteLine($"    → 会写成 {main}，然后删掉其余 {group.Count() - 1} 个");
                continue;
            }

            try
            {
                // 写回主名。QuarkStorage 会先删同名再传，不会再产生副本
                await storage.PutStringAsync(dir.TrimEnd('/') + "/" + main, json, null, cts.Token)
                    .ConfigureAwait(false);

                Console.WriteLine($"    ✔ 已写回 {main}（{json.Length} 字符，{merged.Count} 条）");

                foreach (var e in group)
                {
                    if (e.Name == main) continue;

                    await storage.DeleteAsync(dir.TrimEnd('/') + "/" + e.Name, cts.Token).ConfigureAwait(false);
                    deleted++;
                }

                Console.WriteLine($"    ✔ 已删除 {group.Count() - 1} 个副本");
                fixedDays++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    ✘ 写入失败：{ex.GetType().Name} {ex.Message}");
                failed++;
            }
        }

        Console.WriteLine();

        if (!apply)
        {
            Console.WriteLine("预览结束。确认条数合理后加 --apply 执行。");
            return 0;
        }

        Console.WriteLine($"完成：修复 {fixedDays} 天，删除 {deleted} 个副本" +
                          (failed > 0 ? $"，{failed} 天失败" : ""));
        Console.WriteLine("建议停顿几秒再刷新网页 —— 夸克的目录索引有延迟。");

        return failed > 0 ? 1 : 0;
    }

    /// <summary>把合并后的 clips 概括成"时间范围"。</summary>
    private static string Describe(JsonNode?[] clips)
    {
        var times = clips
            .Select(c => c?["startUtc"]?.GetValue<string>())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => DateTime.TryParse(s, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : (DateTime?)null)
            .Where(d => d is not null)
            .Select(d => d!.Value.ToLocalTime())
            .OrderBy(d => d)
            .ToList();

        return times.Count == 0 ? "" : $"，{times[0]:HH:mm} → {times[^1]:HH:mm}";
    }

    /// <summary>去掉 `(数字)` 后缀和 `.json` 扩展名，得到"这是哪一天"。</summary>
    private static string StripSuffix(string name)
    {
        var stem = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
        var m = CopySuffix.Match(stem);
        return m.Success ? m.Groups["base"].Value : stem;
    }
}
