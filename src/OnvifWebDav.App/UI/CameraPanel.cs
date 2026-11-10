using OnvifWebDav.Config;
using OnvifWebDav.Media;
using OnvifWebDav.Onvif;
using OnvifWebDav.WebDav;

namespace OnvifWebDav.App.UI;

/// <summary>摄像头配置页：地址/账号/码流 + 三个自检按钮（测试连接、取 RTSP、抓快照）。</summary>
internal sealed class CameraPanel : UserControl
{
    private readonly AppRuntime _runtime;

    private readonly CheckBox _enabled = FormBuilder.Check("启用这一路（关闭后不连接、不录像）", true);
    private readonly TextBox _name = FormBuilder.Text("");
    private readonly TextBox _host = FormBuilder.Text("");
    private readonly NumericUpDown _port = FormBuilder.Num(2020, 1, 65535);
    private readonly TextBox _user = FormBuilder.Text("admin");
    private readonly TextBox _pass = FormBuilder.Text("", password: true);
    private readonly ComboBox _stream = FormBuilder.Combo(
        new[] { ("主码流（清晰，码率大）", (object)"main"), ("子码流（省带宽）", "sub") }, "main");
    private readonly TextBox _rtsp = FormBuilder.Text("", width: 420);
    private readonly NumericUpDown _rtspPort = FormBuilder.Num(554, 1, 65535);

    private readonly ComboBox _eventMode = FormBuilder.Combo(new[]
    {
        ("自动（支持订阅策略就用推送）", (object)EventMode.Auto),
        ("推送 Basic Notification（更实时）", EventMode.Basic),
        ("轮询 PullPoint（兼容性最好）", EventMode.PullPoint),
    }, EventMode.Auto);

    private readonly TextBox _result = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Width = 620,
        Height = 150,
        Font = new Font("Consolas", 9F),
        BackColor = Color.FromArgb(248, 249, 251),
    };

    private readonly PictureBox _preview = new()
    {
        Width = 320,
        Height = 180,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.Black,
        BorderStyle = BorderStyle.FixedSingle,
    };

    /// <summary>ONVIF 声明不支持快照（SnapshotUri=false）时把这个置 false，按钮直接禁用。</summary>
    private bool _snapshotAllowed = true;

    private readonly Button _btnTest = new() { Text = "测试 ONVIF 连接", AutoSize = true };
    private readonly Button _btnRtsp = new() { Text = "获取 RTSP 地址", AutoSize = true };
    private readonly Button _btnSnap = new() { Text = "抓一帧快照", AutoSize = true };
    private readonly Button _btnProbe = new() { Text = "探测编码（H.264/H.265）", AutoSize = true };

    public CameraPanel(AppRuntime runtime)
    {
        _runtime = runtime;

        _btnTest.Click += async (_, _) => await TestAsync().ConfigureAwait(true);
        _btnRtsp.Click += async (_, _) => await FetchRtspAsync().ConfigureAwait(true);
        _btnSnap.Click += async (_, _) => await SnapshotAsync().ConfigureAwait(true);
        _btnProbe.Click += async (_, _) => await ProbeAsync().ConfigureAwait(true);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 10),
        };
        buttons.Controls.AddRange(new Control[] { _btnTest, _btnRtsp, _btnProbe, _btnSnap });

        var form = FormBuilder.Form(
            ("启用", _enabled),
            ("名称", _name),
            ("摄像头地址", _host),
            ("ONVIF 端口", _port),
            ("ONVIF 用户名", _user),
            ("ONVIF 密码", _pass),
            ("录像码流", _stream),
            ("RTSP 地址", _rtsp),
            ("RTSP 端口", _rtspPort),
            ("事件方式", _eventMode));

        var root = FormBuilder.Scroll(
            FormBuilder.Head("摄像头"),
            FormBuilder.Hint("摄像头默认是关闭 ONVIF 的，需要先在摄像头网页里打开：" +
                             "设置 → 网络设置 → 网络服务 → ONVIF。TP-Link 固件 1.6+ 端口用 80，旧版用 2020，" +
                             "测试时两个都会自动试。"),
            form,
            FormBuilder.Hint("RTSP 地址留空即可，程序会通过 ONVIF 自动获取；拿不到时退回 TP-Link 默认路径 " +
                             "rtsp://用户名:密码@地址:554/stream1。"),
            FormBuilder.Hint("事件方式：走「推送」时设备会 POST 到本机网页服务的 /onvif/notify，" +
                             "需要网页服务在跑（端口能被摄像头访问）且防火墙放行；订阅失败会自动退回轮询。"),
            buttons,
            FormBuilder.Group("测试结果", _result),
            FormBuilder.Group("快照预览", _preview));

        Controls.Add(root);
    }

    public void Bind(AppConfig cfg)
    {
        var cam = cfg.Cameras[0];
        _enabled.Checked = cam.Enabled;
        _name.Text = cam.Name;
        _host.Text = cam.Host;
        _port.Value = Math.Clamp(cam.OnvifPort, 1, 65535);
        _user.Text = cam.Username;
        _pass.Text = cam.Password;
        _stream.SelectedValue = cam.Stream;
        _rtsp.Text = cam.RtspUrl;
        _rtspPort.Value = Math.Clamp(cam.RtspPort, 1, 65535);
        _eventMode.SelectedValue = cam.EventMode;
    }

    public void Collect(AppConfig cfg)
    {
        var cam = cfg.Cameras[0];
        cam.Enabled = _enabled.Checked;
        cam.Name = string.IsNullOrWhiteSpace(_name.Text) ? "摄像头1" : _name.Text.Trim();
        cam.Host = _host.Text.Trim();
        cam.OnvifPort = (int)_port.Value;
        cam.Username = _user.Text.Trim();
        cam.Password = _pass.Text;
        cam.Stream = (string?)_stream.SelectedValue ?? "main";
        cam.RtspUrl = _rtsp.Text.Trim();
        cam.RtspPort = (int)_rtspPort.Value;
        cam.EventMode = (EventMode)(_eventMode.SelectedValue ?? EventMode.Auto);
    }

    private CameraConfig Snapshot()
    {
        var cfg = new CameraConfig
        {
            Id = "test",
            Enabled = true,
            Name = _name.Text.Trim(),
            Host = _host.Text.Trim(),
            OnvifPort = (int)_port.Value,
            Username = _user.Text.Trim(),
            Password = _pass.Text,
            Stream = (string?)_stream.SelectedValue ?? "main",
            RtspUrl = _rtsp.Text.Trim(),
            RtspPort = (int)_rtspPort.Value,
        };
        return cfg;
    }

    private void Busy(bool busy, string message)
    {
        _btnTest.Enabled = _btnRtsp.Enabled = _btnProbe.Enabled = !busy;
        _btnSnap.Enabled = !busy && _snapshotAllowed;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        if (message.Length > 0) SetResult(message);
    }

    private void SetResult(string text)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => _result.Text = text)); return; }
        _result.Text = string.Join(Environment.NewLine, text);
    }

    private void Append(string line)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => Append(line))); return; }
        _result.Text += (_result.TextLength > 0 ? Environment.NewLine : "") + line;
    }

    private async Task TestAsync()
    {
        if (string.IsNullOrWhiteSpace(_host.Text))
        {
            SetResult("请先填摄像头地址");
            return;
        }

        Busy(true, "正在连接…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            using var onvif = await OnvifCamera.ConnectAsync(Snapshot(), _runtime.Log, cts.Token);

            var lines = new List<string>
            {
                "✔ ONVIF 连接成功",
                "型号：" + onvif.DeviceInfo,
                $"时钟偏差：{onvif.ClockOffset.TotalSeconds:F0} 秒",
            };

            if (onvif.Capabilities is { } caps)
            {
                _snapshotAllowed = onvif.SnapshotSupported;

                lines.Add("");
                lines.Add("— 能力 —");
                lines.AddRange(caps.Describe().Split(new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries));

                if (!onvif.SnapshotSupported)
                    lines.Add("（快照按钮已禁用：ONVIF 声明 SnapshotUri=false，不影响录像）");
            }

            if (Math.Abs(onvif.ClockOffset.TotalSeconds) > 30)
                lines.Add("⚠ 时间差超过 30 秒，ONVIF 鉴权可能失败：请在摄像头网页开启 NTP，" +
                          "或关闭「时间校验 / Time Verification」。");

            lines.Add("可用 profile：");
            foreach (var p in onvif.Profiles) lines.Add("  · " + p);

            var rtsp = await onvif.GetRtspUrlAsync(cts.Token);
            lines.Add("RTSP：" + Mask(rtsp));
            SetResult(string.Join(Environment.NewLine, lines));
        }
        catch (Exception ex)
        {
            SetResult("✘ 连接失败：" + ex.Message + Environment.NewLine +
                      "排查：1) 摄像头是否开启了 ONVIF；2) 端口是 80 还是 2020；3) 账号密码是否正确；" +
                      "4) 摄像头时间是否和电脑差太多。");
        }
        finally
        {
            Busy(false, "");
            _btnSnap.Enabled = _snapshotAllowed;
        }
    }

    private async Task FetchRtspAsync()
    {
        Busy(true, "正在获取 RTSP 地址…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            using var onvif = await OnvifCamera.ConnectAsync(Snapshot(), _runtime.Log, cts.Token);
            var rtsp = await onvif.GetRtspUrlAsync(cts.Token);

            _rtsp.Text = StripCredentials(rtsp);
            SetResult("RTSP 地址：" + Mask(rtsp) + Environment.NewLine +
                      "已填入上方输入框（不含账号密码，程序会自动用上面的 ONVIF 账号登录）。");
        }
        catch (Exception ex)
        {
            SetResult("✘ 获取失败：" + ex.Message);
        }
        finally
        {
            Busy(false, "");
        }
    }

    private async Task ProbeAsync()
    {
        Busy(true, "正在用 ffprobe 探测码流…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            using var onvif = await OnvifCamera.ConnectAsync(Snapshot(), _runtime.Log, cts.Token);
            var rtsp = await onvif.GetRtspUrlAsync(cts.Token);

            var composer = new ClipComposer(_runtime.Ffmpeg, _runtime.Log);
            var info = await composer.ProbeStreamAsync(rtsp, cts.Token);

            if (info is null)
            {
                SetResult("✘ ffprobe 没取到信息（摄像头的 RTSP 可能连不上，或 ffmpeg 没安装）");
                return;
            }

            var lines = new List<string>
            {
                "编码：" + info.VideoCodec.ToUpperInvariant(),
                $"分辨率：{info.Width} x {info.Height}",
                "音频：" + (info.HasAudio ? info.AudioCodec ?? "有" : "无"),
            };

            if (info.IsHevc)
                lines.Add("⚠ 这是 H.265。浏览器基本放不了。建议在摄像头网页把主码流编码改成 H.264；" +
                          "否则请在「录制」页打开「H.265 自动转码」（会占 20~40% CPU）。");

            SetResult(string.Join(Environment.NewLine, lines));
        }
        catch (Exception ex)
        {
            SetResult("✘ 探测失败：" + ex.Message);
        }
        finally
        {
            Busy(false, "");
        }
    }

    private async Task SnapshotAsync()
    {
        if (!_snapshotAllowed)
        {
            SetResult("这台摄像头声明不支持 ONVIF 快照（SnapshotUri=false），抓图用不了。" +
                      Environment.NewLine + "想确认画面请直接看 RTSP 或网页播放，录像功能不受影响。");
            return;
        }

        Busy(true, "正在抓取快照…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var onvif = await OnvifCamera.ConnectAsync(Snapshot(), _runtime.Log, cts.Token);
            var jpeg = await onvif.GetSnapshotJpegAsync(cts.Token);

            using var ms = new MemoryStream(jpeg);
            using var bmp = new Bitmap(ms);
            var copy = new Bitmap(bmp);
            var old = _preview.Image;
            _preview.Image = copy;
            old?.Dispose();

            SetResult($"✔ 快照 {bmp.Width}x{bmp.Height}，{jpeg.Length / 1024} KB");
        }
        catch (Exception ex)
        {
            SetResult("✘ 抓快照失败：" + ex.Message + Environment.NewLine +
                      "有的固件快照地址要求 Digest 认证，程序暂不支持；可改用「探测编码」确认 RTSP 是否可用。");
        }
        finally
        {
            Busy(false, "");
        }
    }

    private static string Mask(string rtsp)
    {
        if (!Uri.TryCreate(rtsp, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.UserInfo))
            return rtsp;
        var b = new UriBuilder(u) { Password = "******" };
        return b.Uri.ToString();
    }

    /// <summary>去掉 URL 里的账号密码：配置里只存裸地址，凭据由程序在运行时拼，避免密码落到配置文件里两处。</summary>
    private static string StripCredentials(string rtsp)
    {
        if (!Uri.TryCreate(rtsp, UriKind.Absolute, out var u)) return rtsp;
        if (string.IsNullOrEmpty(u.UserInfo)) return rtsp;
        var b = new UriBuilder(u) { UserName = "", Password = "" };
        return b.Uri.ToString();
    }
}
