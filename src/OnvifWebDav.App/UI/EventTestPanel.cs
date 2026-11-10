using OnvifWebDav.Config;
using OnvifWebDav.Onvif;
using OnvifWebDav.Pipeline;

namespace OnvifWebDav.App.UI;

/// <summary>
/// 事件自检：直连摄像头订阅 2 分钟，把收到的每一条原始事件都打出来。
/// 走的是和正式录像完全相同的 MotionSourceFactory —— 设备支持 WSSubscriptionPolicySupport
/// 就用 Basic Notification（设备推给我们），否则退回 PullPoint 轮询。
/// 这也是判断"TP-Link 到底推不推运动事件"的唯一可靠手段。
/// </summary>
internal sealed class EventTestPanel : UserControl
{
    private readonly AppRuntime _runtime;

    private readonly RichTextBox _box = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BorderStyle = BorderStyle.None,
        Font = new Font("Consolas", 9F),
        BackColor = Color.FromArgb(250, 250, 251),
        WordWrap = false,
    };

    private readonly Label _summary = new()
    {
        Text = "未开始",
        AutoSize = true,
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
        Margin = new Padding(0, 0, 0, 6),
    };

    private readonly ComboBox _mode = FormBuilder.Combo(new[]
    {
        ("自动（按设备能力选）", (object)EventMode.Auto),
        ("推送 Basic Notification", EventMode.Basic),
        ("轮询 PullPoint", EventMode.PullPoint),
    }, EventMode.Auto);

    private readonly Button _start = new() { Text = "开始监听 2 分钟", AutoSize = true };
    private readonly Button _stop = new() { Text = "停止", AutoSize = true, Enabled = false };

    private CancellationTokenSource? _cts;
    private int _total;
    private int _motion;

    public EventTestPanel(AppRuntime runtime)
    {
        _runtime = runtime;

        _start.Click += async (_, _) => await RunAsync().ConfigureAwait(true);
        _stop.Click += (_, _) => { try { _cts?.Cancel(); } catch { } };
        _mode.SelectedValue = runtime.Config.PrimaryCamera?.EventMode ?? EventMode.Auto;

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        bar.Controls.Add(new Label { Text = "方式", AutoSize = true, Margin = new Padding(0, 10, 6, 0) });
        bar.Controls.Add(_mode);
        bar.Controls.Add(_start);
        bar.Controls.Add(_stop);

        var top = new Panel { Dock = DockStyle.Top, Height = 50 };
        top.Controls.Add(_summary);
        top.Controls.Add(bar);
        _summary.Location = new Point(8, 16);

        Controls.Add(_box);
        Controls.Add(top);

        Append("点「开始监听 2 分钟」，然后到摄像头前面走动一下。");
        Append("判定标准：至少收到 2 条 IsMotion=true 的事件。");
        Append("");
        Append("推送模式需要网页服务在跑（设备要 POST 回来），没跑会自动退回轮询。");
        Append("");
    }

    private void Append(string line)
    {
        if (IsDisposed) return;

        void Do()
        {
            if (_box.TextLength > 300_000) _box.Clear();
            _box.AppendText(line + Environment.NewLine);
            _box.SelectionStart = _box.TextLength;
            _box.ScrollToCaret();
        }

        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(Do)); } catch { }
        }
        else Do();
    }

    private async Task RunAsync()
    {
        var saved = _runtime.Config.PrimaryCamera;
        if (saved is null || string.IsNullOrWhiteSpace(saved.Host))
        {
            Append("✘ 还没配置摄像头地址");
            return;
        }

        // 用保存后的配置副本，方式取界面下拉框（方便不改配置就先试推送）
        var cam = new CameraConfig
        {
            Id = saved.Id,
            Name = saved.Name,
            Host = saved.Host,
            OnvifPort = saved.OnvifPort,
            Username = saved.Username,
            Password = saved.Password,
            Stream = saved.Stream,
            RtspUrl = saved.RtspUrl,
            RtspPort = saved.RtspPort,
            SnapshotUrl = saved.SnapshotUrl,
            EventMode = (EventMode)(_mode.SelectedValue ?? EventMode.Auto),
        };

        _start.Enabled = false;
        _stop.Enabled = true;
        _total = 0;
        _motion = 0;
        _summary.Text = "连接中…";
        _box.Clear();

        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = _cts.Token;

        try
        {
            Append($"连接 {cam.Host}:{cam.OnvifPort} …");
            using var onvif = await OnvifCamera.ConnectAsync(cam, _runtime.Log, ct).ConfigureAwait(true);
            Append("✔ 已连接：" + onvif.DeviceInfo);
            Append("Events 地址：" + onvif.EventsUri);

            var built = await MotionSourceFactory.CreateAsync(cam, onvif, _runtime.NotifyHub,
                _runtime.ConsumerUrl, _runtime.Log, ct).ConfigureAwait(true);

            if (built.Capabilities is { } caps)
            {
                Append("");
                Append("能力：" + caps);
                if (!caps.SubscriptionPolicySupport && cam.EventMode == EventMode.Basic)
                    Append("⚠ 设备报告不支持 WSSubscriptionPolicy，推送可能被拒");
            }
            else
            {
                Append("⚠ 没取到 Events 能力集，按默认策略处理");
            }

            Append("");
            Append("实际使用：" + built.ModeName);
            if (built.ModeName.Contains("推送", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(_runtime.ConsumerUrl))
                    Append("⚠ 网页服务没在跑，设备无处回调（应该已自动退回轮询）");
                else
                    Append("回调地址：" + _runtime.ConsumerUrl);
            }

            Append("");

            await using var source = built.Source;

            source.RawEvent += e =>
            {
                _total++;
                Append($"[{DateTime.Now:HH:mm:ss}] {e.Topic}  IsMotion={e.IsMotion}  Init={e.Initialized}");
                UpdateSummary();
            };

            source.MotionStarted += e =>
            {
                _motion++;
                Append($"[{DateTime.Now:HH:mm:ss}] ▶ 运动开始（设备时间 {e.UtcTime.ToLocalTime():HH:mm:ss}）" +
                       (string.IsNullOrEmpty(e.Topic) ? "" : Environment.NewLine + "    " + e.Topic));
                UpdateSummary();
            };

            source.MotionEnded += e =>
            {
                Append($"[{DateTime.Now:HH:mm:ss}] ■ 运动结束（设备时间 {e.UtcTime.ToLocalTime():HH:mm:ss}）");
            };

            Append($"订阅已建立（{built.ModeName}），等待事件……（保持窗口打开）");
            await source.RunAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Append("");
            Append(_total == 0
                ? "✘ 2 分钟内一条事件都没收到。请确认：ONVIF 已开启、时间校验已关闭、" +
                  "「移动侦测/运动检测」已启用并勾选了「上报」；推送模式还要检查 Windows 防火墙是否放行了网页端口。"
                : $"结束：共 {_total} 条事件，其中运动 {_motion} 次。");
        }
        catch (Exception ex)
        {
            Append("✘ 出错：" + ex.Message);
        }
        finally
        {
            _start.Enabled = true;
            _stop.Enabled = false;
            UpdateSummary();
        }
    }

    private void UpdateSummary()
    {
        var text = $"收到 {_total} 条事件 · 运动 {_motion} 次";

        if (_total == 0) _summary.ForeColor = Color.FromArgb(110, 116, 128);
        else if (_motion == 0) _summary.ForeColor = Color.FromArgb(180, 120, 0);
        else _summary.ForeColor = Color.FromArgb(5, 150, 105);

        if (InvokeRequired) { try { BeginInvoke(new Action(() => _summary.Text = text)); } catch { } }
        else _summary.Text = text;
    }
}
