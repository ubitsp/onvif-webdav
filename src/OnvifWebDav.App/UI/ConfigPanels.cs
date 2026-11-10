using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using OnvifWebDav.Config;
using OnvifWebDav.Diagnostics;
using OnvifWebDav.Pipeline;
using OnvifWebDav.WebDav;

namespace OnvifWebDav.App.UI;

internal sealed class RecordingPanel : UserControl
{
    private readonly ComboBox _mode = FormBuilder.Combo(new[]
    {
        ("环形缓冲 + 预录（推荐）", (object)RecordingMode.RingBuffer),
        ("触发式录制（省 CPU/磁盘，无预录）", RecordingMode.TriggerOnly),
    }, RecordingMode.RingBuffer);

    private readonly NumericUpDown _preRoll = FormBuilder.Num(8, 0, 300);
    private readonly NumericUpDown _silentStop = FormBuilder.Num(5, 1, 120);
    private readonly NumericUpDown _postRoll = FormBuilder.Num(3, 0, 120);
    private readonly NumericUpDown _minSeconds = FormBuilder.Num(5, 1, 60);
    private readonly NumericUpDown _maxSeconds = FormBuilder.Num(300, 30, 3600);
    private readonly NumericUpDown _segment = FormBuilder.Num(2, 1, 10);
    private readonly NumericUpDown _ringKeep = FormBuilder.Num(600, 30, 86400);
    private readonly NumericUpDown _ringLimitMb = FormBuilder.Num(2048, 0, 1048576);
    private readonly TextBox _topicFilter = FormBuilder.Text("", width: 300);

    // 环形缓冲目录（可指到内存盘）
    private readonly TextBox _ringDir = FormBuilder.Text("", width: 380);
    private readonly Button _ringDirBrowse = new() { Text = "浏览…", AutoSize = true };
    private readonly Label _ringDirInfo = FormBuilder.Hint("");

    private readonly FlowLayoutPanel _ringDirRow = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = false,
        Margin = new Padding(0, 0, 0, 4),
    };
    private readonly CheckBox _autoHevc = FormBuilder.Check("探到 H.265 自动转成 H.264（浏览器才放得了）", true);
    private readonly CheckBox _forceH264 = FormBuilder.Check("无条件转码成 H.264（最兼容，最费 CPU）", false);
    private readonly CheckBox _audio = FormBuilder.Check("录制声音（G.711 会转成 AAC）", true);

    public RecordingPanel()
    {
        _ringDir.Margin = new Padding(0, 0, 6, 0);
        _ringDirRow.Controls.Add(_ringDir);
        _ringDirRow.Controls.Add(_ringDirBrowse);

        _ringDirBrowse.Click += (_, _) => BrowseRingDir();
        _ringDir.Leave += (_, _) => UpdateRingDirInfo();

        Controls.Add(FormBuilder.Scroll(
            FormBuilder.Head("录制"),
            FormBuilder.Hint("环形缓冲 = 常驻一路 -c copy 录制，边写边删。事件到达时从这段缓冲里切出片段，" +
                             "所以「事件发生前多久」由「预录」决定，能回看多久由「环形保留」决定。" +
                             "缓冲区只在本机，不会上传；云端永远只存切出来的片段。"),
            FormBuilder.Form(
                ("录制模式", _mode),
                ("预录（秒）", _preRoll),
                ("静默多久结束（秒）", _silentStop),
                ("片尾余量（秒）", _postRoll),
                ("最短时长（秒）", _minSeconds),
                ("单段最长（秒）", _maxSeconds),
                ("分段长度（秒）", _segment),
                ("环形目录", _ringDirRow),
                ("环形保留（秒）", _ringKeep),
                ("环形磁盘上限（MB）", _ringLimitMb),
                ("事件过滤", _topicFilter),
                ("H.265", _autoHevc),
                ("强制转码", _forceH264),
                ("音频", _audio)),
            _ringDirInfo,
            FormBuilder.Hint("「环形目录」留空 = 数据目录下的 spool\\ring（物理盘）。" +
                             "想不写物理盘就指到内存盘（如 R:////onvif-ring）：连续录像全程在内存里滚，" +
                             "物理盘只剩「事件片段」那点写入。留 10% 余量，别把内存盘占满。"),
            FormBuilder.Hint("预录可以放到 30~60 秒（环形保留够大就行），这样事件延迟多久都不怕。" +
                             "环形保留默认 600 秒（10 分钟，约 120~300MB @2~4Mbps）；调大它就等于「几乎一直有录像可切」。" +
                             "磁盘上限是兜底，0 = 只按时间淘汰；实际还会被所在盘的剩余空间压一层。"),
            FormBuilder.Hint("事件过滤留空 = 所有运动事件都录；想只录人形就填 PersonDetector 之类的 topic 子串，" +
                             "多个用逗号分隔。设备到底上报哪些 topic，用「事件自检」页走过去看。" +
                             "单段最长是为了防雨雪/夜间噪点触发无限录制，超过就自动切片。")));
    }

    public void Bind(AppConfig cfg)
    {
        var r = cfg.Recording;
        _mode.SelectedValue = r.Mode;
        _preRoll.Value = Math.Clamp(r.PreRollSeconds, 0, 300);
        _silentStop.Value = Math.Clamp(r.SilentStopSeconds, 1, 120);
        _postRoll.Value = Math.Clamp(r.PostRollSeconds, 0, 120);
        _minSeconds.Value = Math.Clamp(r.MinSeconds, 1, 60);
        _maxSeconds.Value = Math.Clamp(r.MaxSeconds, 30, 3600);
        _segment.Value = Math.Clamp(r.SegmentSeconds, 1, 10);
        _ringKeep.Value = Math.Clamp(r.RingKeepSeconds, 30, 86400);
        _ringLimitMb.Value = Math.Clamp(r.RingLimitMb, 0, 1048576);
        _topicFilter.Text = r.TopicFilter ?? "";
        _ringDir.Text = r.RingDirectory ?? "";
        UpdateRingDirInfo();
        _autoHevc.Checked = r.AutoTranscodeHevc;
        _forceH264.Checked = r.TranscodeToH264;
        _audio.Checked = r.RecordAudio;
    }

    public void Collect(AppConfig cfg)
    {
        var r = cfg.Recording;
        r.Mode = (RecordingMode)(_mode.SelectedValue ?? RecordingMode.RingBuffer);
        r.PreRollSeconds = (int)_preRoll.Value;
        r.SilentStopSeconds = (int)_silentStop.Value;
        r.PostRollSeconds = (int)_postRoll.Value;
        r.MinSeconds = (int)_minSeconds.Value;
        r.MaxSeconds = (int)_maxSeconds.Value;
        r.SegmentSeconds = (int)_segment.Value;
        r.RingKeepSeconds = (int)_ringKeep.Value;
        r.RingLimitMb = (int)_ringLimitMb.Value;
        r.TopicFilter = _topicFilter.Text.Trim();
        r.RingDirectory = _ringDir.Text.Trim();
        r.AutoTranscodeHevc = _autoHevc.Checked;
        r.TranscodeToH264 = _forceH264.Checked;
        r.RecordAudio = _audio.Checked;
    }

    private void BrowseRingDir()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择环形缓冲目录（内存盘请选它的盘符，如 R:////onvif-ring）",
            ShowNewFolderButton = true,
        };

        if (_ringDir.Text.Trim().Length > 0 && Directory.Exists(_ringDir.Text.Trim()))
            dlg.SelectedPath = _ringDir.Text.Trim();

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _ringDir.Text = dlg.SelectedPath;
        UpdateRingDirInfo();
    }

    /// <summary>实时显示目标盘的剩余空间 —— 填内存盘路径时最该看这个数字。</summary>
    private void UpdateRingDirInfo()
    {
        var raw = _ringDir.Text.Trim();

        if (raw.Length == 0)
        {
            _ringDirInfo.ForeColor = Color.FromArgb(110, 116, 128);
            _ringDirInfo.Text = "留空 = 数据目录\\spool\\ring（物理盘）";
            return;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(raw));
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                throw new DirectoryNotFoundException("盘符不存在：" + root);

            var di = new DriveInfo(root);
            var freeGb = di.AvailableFreeSpace / 1024.0 / 1024 / 1024;
            var totalGb = di.TotalSize / 1024.0 / 1024 / 1024;

            // 1GB 内存盘按 4Mbps 只能存约 30 分钟，低于 0.5GB 就该提醒
            _ringDirInfo.ForeColor = freeGb < 0.5 ? Color.Firebrick : Color.SeaGreen;
            _ringDirInfo.Text = $"{(freeGb < 0.5 ? "⚠" : "✔")} {root} 剩余 {freeGb:F2} GB / 共 {totalGb:F1} GB" +
                                $"（{di.DriveFormat}）";
        }
        catch (Exception ex)
        {
            _ringDirInfo.ForeColor = Color.Firebrick;
            _ringDirInfo.Text = "✘ " + ex.Message;
        }
    }
}

internal sealed class WebPanel : UserControl
{
    private readonly NumericUpDown _port = FormBuilder.Num(8080, 1, 65535);
    private readonly TextBox _pass = FormBuilder.Text("", width: 240);
    private readonly NumericUpDown _cacheGb = FormBuilder.Num(2, 1, 100);
    private readonly NumericUpDown _sessionDays = FormBuilder.Num(30, 1, 365);

    private readonly TextBox _urls = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Width = 460,
        Height = 76,
        Font = new Font("Consolas", 9.5F),
        BackColor = Color.FromArgb(248, 249, 251),
    };

    private readonly Button _btnRefresh = new() { Text = "刷新地址", AutoSize = true };
    private readonly Button _btnOpen = new() { Text = "打开网页", AutoSize = true };
    private readonly Button _btnFirewall = new() { Text = "打开防火墙设置", AutoSize = true };

    public WebPanel()
    {
        _btnRefresh.Click += (_, _) => ShowUrls();
        _btnOpen.Click += (_, _) => OpenFirst();
        _btnFirewall.Click += (_, _) => OpenFirewall();

        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        buttons.Controls.AddRange(new Control[] { _btnRefresh, _btnOpen, _btnFirewall });

        Controls.Add(FormBuilder.Scroll(
            FormBuilder.Head("网页"),
            FormBuilder.Hint("手机和电脑在同一局域网就能看。第一次启动 Windows 会弹防火墙提示，" +
                             "请选「专用网络」并允许；如果没弹或手滑点了取消，点下面的「打开防火墙设置」手动放行。"),
            FormBuilder.Form(
                ("监听端口", _port),
                ("访问密码", _pass),
                ("播放缓存（GB）", _cacheGb),
                ("登录有效期（天）", _sessionDays)),
            FormBuilder.Hint("访问密码留空 = 局域网内任何人都能看录像。播放缓存是把看过的片段临时下到本地，" +
                             "拖动进度才流畅；不会长期保留。"),
            FormBuilder.Group("局域网地址", _urls),
            buttons));
    }

    public void Bind(AppConfig cfg)
    {
        _port.Value = Math.Clamp(cfg.Web.Port, 1, 65535);
        _pass.Text = cfg.Web.Password;
        _cacheGb.Value = Math.Clamp(cfg.Web.CacheGb, 1, 100);
        _sessionDays.Value = Math.Clamp(cfg.Web.SessionDays, 1, 365);
        ShowUrls();
    }

    public void Collect(AppConfig cfg)
    {
        cfg.Web.Port = (int)_port.Value;
        cfg.Web.Password = _pass.Text;
        cfg.Web.CacheGb = (int)_cacheGb.Value;
        cfg.Web.SessionDays = (int)_sessionDays.Value;
    }

    public void ShowUrls()
    {
        var port = (int)_port.Value;
        var urls = LocalAddresses().Select(ip => $"http://{ip}:{port}").ToList();
        _urls.Text = urls.Count > 0
            ? string.Join(Environment.NewLine, urls)
            : "没有检测到局域网地址（电脑可能没连网）";
    }

    private void OpenFirst()
    {
        var url = LocalAddresses().Select(ip => $"http://{ip}:{(int)_port.Value}").FirstOrDefault()
                  ?? $"http://localhost:{(int)_port.Value}";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private static void OpenFirewall()
    {
        try
        {
            Process.Start(new ProcessStartInfo("control.exe", "/name Microsoft.WindowsFirewall")
            { UseShellExecute = true });
        }
        catch { }
    }

    private static List<string> LocalAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = addr.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue;
                    list.Add(ip);
                }
            }
        }
        catch { }
        return list.Distinct().ToList();
    }
}

/// <summary>状态页：一眼看到在不在录、队列积压多少、最近一次运动和上传。</summary>
internal sealed class StatusPanel : UserControl
{
    private readonly Label _state = new()
    {
        Text = "未启动",
        AutoSize = true,
        Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold),
        Margin = new Padding(0, 0, 0, 4),
    };

    private readonly Label _message = new() { Text = "", AutoSize = true, ForeColor = Color.FromArgb(110, 116, 128) };

    private readonly Label _grid = new()
    {
        AutoSize = true,
        Font = new Font("Microsoft YaHei UI", 9.5F),
        Margin = new Padding(0, 10, 0, 0),
    };

    private readonly Label _error = new()
    {
        AutoSize = true,
        MaximumSize = new Size(620, 0),
        ForeColor = Color.FromArgb(200, 40, 40),
        Margin = new Padding(0, 10, 0, 0),
    };

    public StatusPanel()
    {
        Controls.Add(FormBuilder.Scroll(_state, _message, _grid, _error,
            FormBuilder.Hint("提示：关闭窗口不会退出程序，只会在右下角托盘继续录像。真要退出请右键托盘图标选「退出」。")));
    }

    public void Refresh(AppStatus s)
    {
        _state.Text = s.StateText;
        _state.ForeColor = s.State switch
        {
            WorkerState.Watching => Color.FromArgb(5, 150, 105),
            WorkerState.Recording => Color.FromArgb(220, 38, 38),
            WorkerState.Error => Color.FromArgb(220, 38, 38),
            WorkerState.Paused => Color.FromArgb(180, 120, 0),
            _ => Color.FromArgb(60, 64, 72),
        };

        _message.Text = s.Message;

        var spoolMb = s.SpoolBytes / 1024.0 / 1024.0;
        _grid.Text = string.Join(Environment.NewLine, new[]
        {
            $"事件方式：{(string.IsNullOrEmpty(s.EventMode) ? "未订阅" : s.EventMode)}",
            $"码流：{(string.IsNullOrEmpty(s.Codec) ? "—" : s.Codec)}",
            $"ffmpeg：{(s.FfmpegAlive ? "运行中" : "未运行")}",
            $"待上传队列：{s.QueueCount} 个 · 本地积压 {spoolMb:F1} MB",
            $"云端空间：{(s.Quota is null ? "未知（服务端未返回配额）" : s.Quota.Describe())}",
            $"环形缓冲：{s.RingMb:F1} MB{(string.IsNullOrEmpty(s.RingPath) ? "" : " · " + s.RingPath)}",
            $"今日新增片段：{s.TodayClips} 段",
            $"最近一次运动：{(s.LastMotion?.ToString("HH:mm:ss") ?? "—")}",
            $"最近一次上传成功：{(s.LastUpload?.ToString("HH:mm:ss") ?? "—")}",
            $"累计收到事件：{s.EventCount} 条{(string.IsNullOrEmpty(s.LastTopic) ? "" : " · " + s.LastTopic)}",
        });

        _error.Text = string.IsNullOrEmpty(s.LastError) ? "" : "最近错误：" + s.LastError;
    }
}

internal sealed class LogPanel : UserControl
{
    private readonly RichTextBox _box = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BorderStyle = BorderStyle.None,
        Font = new Font("Consolas", 9F),
        BackColor = Color.FromArgb(250, 250, 251),
        WordWrap = false,
        DetectUrls = false,
    };

    private readonly ComboBox _level;
    private readonly AppLog _log;
    private readonly AppRuntime _runtime;

    public LogPanel(AppRuntime runtime)
    {
        _log = runtime.Log;
        _runtime = runtime;

        _level = FormBuilder.Combo(new[]
        {
            ("全部（含调试）", (object)"Debug"),
            ("常规", "Info"),
            ("只看警告", "Warn"),
            ("只看错误", "Error"),
        }, runtime.Config.System.LogLevel);

        _level.SelectedValueChanged += (_, _) =>
        {
            _log.MinLevel = ((string?)_level.SelectedValue) switch
            {
                "Debug" => LogLevel.Debug,
                "Warn" => LogLevel.Warn,
                "Error" => LogLevel.Error,
                _ => LogLevel.Info,
            };
        };

        var open = new Button { Text = "打开日志文件夹", AutoSize = true };
        open.Click += (_, _) =>
        {
            var dir = Path.Combine(runtime.DataDirectory, "logs");
            Directory.CreateDirectory(dir);
            try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); } catch { }
        };

        var openData = new Button { Text = "打开数据目录", AutoSize = true };
        openData.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(runtime.DataDirectory) { UseShellExecute = true }); } catch { }
        };

        var autoStart = new CheckBox
        {
            Text = "随 Windows 启动（启动后直接进托盘）",
            AutoSize = true,
            Checked = Infrastructure.AutoStart.IsEnabled(),
            Margin = new Padding(16, 8, 0, 0),
        };
        autoStart.CheckedChanged += (_, _) =>
        {
            var enabled = autoStart.Checked;
            if (!Infrastructure.AutoStart.TryApply(enabled, out var msg))
            {
                MessageBox.Show(this, msg, "设置失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                autoStart.Checked = Infrastructure.AutoStart.IsEnabled();
                return;
            }

            runtime.Config.System.AutoStart = enabled;
            runtime.Store.Save(runtime.Config);
            runtime.Log.Info("app", msg);
        };

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        bar.Controls.Add(new Label { Text = "日志级别", AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
        bar.Controls.Add(_level);
        bar.Controls.Add(open);
        bar.Controls.Add(openData);
        bar.Controls.Add(autoStart);

        Controls.Add(_box);
        Controls.Add(bar);

        foreach (var entry in _log.Recent) Append(entry);
        _log.Appended += Append;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _log.Appended -= Append;
        base.Dispose(disposing);
    }

    private void Append(LogEntry entry)
    {
        if (IsDisposed) return;

        void Do()
        {
            if (_box.TextLength > 400_000) _box.Clear();
            _box.SelectionStart = _box.TextLength;
            _box.SelectionColor = entry.Level switch
            {
                LogLevel.Error => Color.FromArgb(190, 30, 30),
                LogLevel.Warn => Color.FromArgb(170, 110, 0),
                LogLevel.Debug => Color.FromArgb(130, 136, 148),
                _ => Color.FromArgb(30, 34, 40),
            };
            _box.AppendText(entry + Environment.NewLine);
            _box.SelectionStart = _box.TextLength;
            _box.ScrollToCaret();
        }

        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(Do)); } catch { }
        }
        else Do();
    }
}
