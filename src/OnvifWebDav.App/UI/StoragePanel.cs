using System.Diagnostics;
using OnvifWebDav.Config;
using OnvifWebDav.WebDav;

namespace OnvifWebDav.App.UI;

/// <summary>
/// 存储设置：WebDAV 和夸克网盘二选一。
///
/// 两个后端共用同一套 IRemoteStorage 抽象，所以「云端空间守卫」只出现一次 ——
/// 它操作的是存储抽象，不认识具体后端。
///
/// 夸克这块最大的特点：**没有账号密码，只有浏览器会话**。
/// 所以界面上不是输入框，而是「获取会话」按钮 + 状态显示。
/// </summary>
internal sealed class StoragePanel : UserControl
{
    private readonly AppRuntime _runtime;

    // ---------------- 后端选择 ----------------

    private readonly RadioButton _rbDav = new() { Text = "WebDAV（NAS / Nextcloud / 自建）", AutoSize = true };
    private readonly RadioButton _rbQuark = new() { Text = "夸克网盘（会员空间）", AutoSize = true };

    // ---------------- WebDAV ----------------

    private readonly TextBox _url = FormBuilder.Text("", width: 460);
    private readonly TextBox _user = FormBuilder.Text("", width: 240);
    private readonly TextBox _pass = FormBuilder.Text("", password: true, width: 240);
    private readonly TextBox _root = FormBuilder.Text("/onvif", width: 240);
    private readonly NumericUpDown _timeout = FormBuilder.Num(100, 10, 600);
    private readonly CheckBox _ignoreCert = FormBuilder.Check("忽略证书错误（自签 HTTPS 才需要）", false);

    private readonly TextBox _result = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Width = 620,
        Height = 110,
        Font = new Font("Consolas", 9F),
        BackColor = Color.FromArgb(248, 249, 251),
    };

    private readonly Button _btnTest = new() { Text = "测试 WebDAV 连接", AutoSize = true };
    private readonly Button _btnOpen = new() { Text = "在浏览器打开", AutoSize = true };

    // ---------------- 夸克 ----------------

    private readonly TextBox _qExe = FormBuilder.Text("", width: 400);
    private readonly Button _btnQExe = new() { Text = "浏览…", AutoSize = true };
    private readonly NumericUpDown _qPort = FormBuilder.Num(9222, 1, 65535);
    private readonly TextBox _qRoot = FormBuilder.Text("/onvif", width: 240);
    private readonly TextBox _qBase = FormBuilder.Text("https://drive-pc.quark.cn", width: 340);

    private readonly Label _qSession = new()
    {
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 8),
        ForeColor = Color.FromArgb(110, 116, 128),
    };

    private readonly Button _btnQuarkSession = new() { Text = "从浏览器获取会话", AutoSize = true };
    private readonly Button _btnQuarkTest = new() { Text = "测试夸克连接", AutoSize = true };

    private readonly TextBox _qResult = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Width = 620,
        Height = 130,
        Font = new Font("Consolas", 9F),
        BackColor = Color.FromArgb(248, 249, 251),
    };

    // ---------------- 配额（两个后端共用）----------------

    private readonly CheckBox _quotaEnabled = FormBuilder.Check("启用云端空间守卫", true);
    private readonly NumericUpDown _quotaWarn = FormBuilder.Num(10, 0, 100000);
    private readonly NumericUpDown _quotaPause = FormBuilder.Num(3, 0, 100000);
    private readonly CheckBox _quotaAutoDelete = FormBuilder.Check("空间不足时自动删除最旧的录像（不可逆）", false);
    private readonly NumericUpDown _quotaTarget = FormBuilder.Num(15, 1, 100000);
    private readonly NumericUpDown _quotaProbe = FormBuilder.Num(30, 1, 1440);

    private readonly GroupBox _davBox;
    private readonly GroupBox _quarkBox;

    public StoragePanel(AppRuntime runtime)
    {
        _runtime = runtime;

        _btnTest.Click += async (_, _) => await TestDavAsync().ConfigureAwait(true);
        _btnOpen.Click += (_, _) => OpenUrl();
        _btnQuarkSession.Click += async (_, _) => await FetchQuarkSessionAsync().ConfigureAwait(true);
        _btnQExe.Click += (_, _) => BrowseQuarkExe();
        _btnQuarkTest.Click += async (_, _) => await TestQuarkAsync().ConfigureAwait(true);

        _rbDav.CheckedChanged += (_, _) => UpdateVisibility();
        _rbQuark.CheckedChanged += (_, _) => UpdateVisibility();

        // 后端单选必须放在同一个容器里才会互斥
        var backendBox = FormBuilder.Group("存储后端",
            _rbDav,
            _rbQuark,
            FormBuilder.Hint("两个后端功能等价，都支持事件录像、索引、缩略图和配额守卫。" +
                             "切换后需要「保存并应用」才会生效（会重启录像服务）。"));

        var davButtons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        davButtons.Controls.AddRange(new Control[] { _btnTest, _btnOpen });

        _davBox = FormBuilder.Group("WebDAV",
            FormBuilder.Form(
                ("服务地址", _url),
                ("用户名", _user),
                ("密码 / 应用密码", _pass),
                ("根目录", _root),
                ("超时（秒）", _timeout),
                ("证书", _ignoreCert)),
            davButtons,
            _result);

        var quarkButtons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        quarkButtons.Controls.AddRange(new Control[] { _btnQuarkSession, _btnQuarkTest });

        // 程序路径需要"输入框 + 浏览按钮"，FormBuilder.Form 的单控件行装不下，自己拼一行
        var exeRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 6),
        };
        _qExe.Margin = new Padding(0, 0, 6, 0);
        exeRow.Controls.AddRange(new Control[] { _qExe, _btnQExe });

        _quarkBox = FormBuilder.Group("夸克网盘",
            FormBuilder.Hint(
                "夸克没有账号密码，认证靠客户端的登录会话。正常流程：客户端已登录 → 点「从浏览器获取会话」即可。" +
                "如果夸克没在运行，程序会自己用调试端口把它拉起来；" +
                "但它**已经在运行**而没开调试端口时，因为单实例机制无法接管，需要你完全退出（托盘也要退）后重试。" +
                "绿色版/自定义安装位置请在上面填 quark.exe 的完整路径。"),
            FormBuilder.Form(
                ("程序路径", exeRow),
                ("调试端口", _qPort),
                ("网盘根目录", _qRoot),
                ("API 地址", _qBase),
                // 会话状态直接占一行。不要包成嵌套 GroupBox ——
                // FormBuilder.Group 用 Dock=Top + AutoSize，套在另一个 Group 里会被压成一条线
                ("会话状态", _qSession)),
            quarkButtons,
            _qResult,
            FormBuilder.Hint(
                "会话一旦取到会加密存在本机，之后不必开浏览器 —— 服务端每次响应都会下发新的 __puus，" +
                "只要程序在跑就会自动续期。真失效了再点一次按钮重取即可。"));

        Controls.Add(FormBuilder.Scroll(
            FormBuilder.Head("存储"),
            FormBuilder.Hint("录像只存在云端，本地不留副本。"),
            backendBox,
            _davBox,
            _quarkBox,
            FormBuilder.Group("云端空间守卫",
                _quotaEnabled,
                FormBuilder.Form(
                    ("告警阈值（GB）", _quotaWarn),
                    ("暂停阈值（GB）", _quotaPause),
                    ("自动清理目标（GB）", _quotaTarget),
                    ("探测间隔（分钟）", _quotaProbe)),
                _quotaAutoDelete,
                FormBuilder.Hint("每次上传前看一眼云端剩余空间：低于「告警」只提示；低于「暂停」就停止上传，" +
                                 "本地积压等处理（0 = 不暂停）。勾了自动清理会从最旧的一天开始整天删，" +
                                 "删到剩余空间达到「自动清理目标」为止。"))));

        UpdateVisibility();
    }

    /// <summary>切后端时只显示对应那一段，避免用户对着无关字段发懵。</summary>
    private void UpdateVisibility()
    {
        var quark = _rbQuark.Checked;
        _davBox.Visible = !quark;
        _quarkBox.Visible = quark;
    }

    // ==================== 绑定 ====================

    public void Bind(AppConfig cfg)
    {
        _rbQuark.Checked = cfg.StorageBackend == StorageBackend.Quark;
        _rbDav.Checked = !_rbQuark.Checked;

        var d = cfg.WebDav;
        _url.Text = d.Url;
        _user.Text = d.Username;
        _pass.Text = d.Password;
        _root.Text = d.RootPath;
        _timeout.Value = Math.Clamp(d.TimeoutSeconds, 10, 600);
        _ignoreCert.Checked = d.IgnoreCertErrors;

        var q = cfg.Quark;
        _qExe.Text = q.ExePath;
        _qPort.Value = Math.Clamp(q.CdpPort, 1, 65535);
        _qRoot.Text = q.RootPath;
        _qBase.Text = q.BaseUrl;

        var qq = cfg.Quota;
        _quotaEnabled.Checked = qq.Enabled;
        _quotaWarn.Value = Math.Clamp(qq.WarnBelowGb, 0, 100000);
        _quotaPause.Value = Math.Clamp(qq.PauseBelowGb, 0, 100000);
        _quotaAutoDelete.Checked = qq.AutoDeleteOldest;
        _quotaTarget.Value = Math.Clamp(qq.AutoDeleteTargetFreeGb, 1, 100000);
        _quotaProbe.Value = Math.Clamp(qq.ProbeMinutes, 1, 1440);

        RefreshQuarkSessionLabel();
        UpdateVisibility();
    }

    public void Collect(AppConfig cfg)
    {
        cfg.StorageBackend = _rbQuark.Checked ? StorageBackend.Quark : StorageBackend.WebDav;

        var d = cfg.WebDav;
        d.Url = _url.Text.Trim();
        d.Username = _user.Text.Trim();
        d.Password = _pass.Text;
        d.RootPath = string.IsNullOrWhiteSpace(_root.Text) ? "/onvif" : _root.Text.Trim();
        d.TimeoutSeconds = (int)_timeout.Value;
        d.IgnoreCertErrors = _ignoreCert.Checked;

        var q = cfg.Quark;
        q.ExePath = _qExe.Text.Trim();
        q.CdpPort = (int)_qPort.Value;
        q.RootPath = NormalizeRoot(_qRoot.Text);
        q.BaseUrl = string.IsNullOrWhiteSpace(_qBase.Text) ? "https://drive-pc.quark.cn" : _qBase.Text.Trim();

        var qq = cfg.Quota;
        qq.Enabled = _quotaEnabled.Checked;
        qq.WarnBelowGb = (int)_quotaWarn.Value;
        qq.PauseBelowGb = (int)_quotaPause.Value;
        qq.AutoDeleteOldest = _quotaAutoDelete.Checked;
        qq.AutoDeleteTargetFreeGb = (int)_quotaTarget.Value;
        qq.ProbeMinutes = (int)_quotaProbe.Value;
    }

    /// <summary>根目录统一成 "/a/b" 形式；空或 "/" 表示网盘根。</summary>
    private static string NormalizeRoot(string raw)
    {
        var t = (raw ?? "").Replace('\\', '/').Trim().Trim('/');
        return t.Length == 0 ? "" : "/" + t;
    }

    // ==================== WebDAV 测试 ====================

    private async Task TestDavAsync()
    {
        var cfg = new WebDavConfig
        {
            Url = _url.Text.Trim(),
            Username = _user.Text.Trim(),
            Password = _pass.Text,
            RootPath = string.IsNullOrWhiteSpace(_root.Text) ? "/onvif" : _root.Text.Trim(),
            TimeoutSeconds = (int)_timeout.Value,
            IgnoreCertErrors = _ignoreCert.Checked,
        };

        _btnTest.Enabled = false;
        _result.Text = "正在测试…";

        try
        {
            using var dav = new WebDavClient(cfg, _runtime.Log);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var result = await dav.TestAsync(cts.Token);

            var lines = new List<string> { (result.Ok ? "✔ " : "✘ ") + result.Message };

            if (result.Steps is { Count: > 0 })
            {
                lines.Add("");
                lines.AddRange(result.Steps.Select(x => "  " + x));
            }

            if (result.Ok)
            {
                // 记住 MOVE 能力：不支持时后续上传直接 PUT，省掉一次完整重复上传
                _runtime.Config.WebDav.SupportsMove = result.SupportsMove;
                _runtime.Store.Save(_runtime.Config);

                if (result.Quota is not null) _runtime.Status.Quota = result.Quota;
                lines.Add("");
                lines.Add(result.SupportsMove
                    ? "已记录：服务端支持 MOVE，上传走 .part → MOVE 原子提交。"
                    : "已记录：服务端不支持 MOVE，后续上传直接 PUT 正式文件名（省一次重复上传）。");
            }

            _result.Text = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            _result.Text = "✘ 测试失败：" + ex.Message;
        }
        finally
        {
            _btnTest.Enabled = true;
        }
    }

    private void OpenUrl()
    {
        var url = _url.Text.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // ==================== 夸克会话 ====================

    private void BrowseQuarkExe()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择 quark.exe",
            Filter = "夸克客户端 (quark.exe)|quark.exe|可执行文件 (*.exe)|*.exe",
            CheckFileExists = true,
        };

        var current = _qExe.Text.Trim();
        if (current.Length > 0)
        {
            try { dlg.InitialDirectory = Path.GetDirectoryName(current); } catch { }
        }

        if (dlg.ShowDialog(this) == DialogResult.OK) _qExe.Text = dlg.FileName;
    }

    private async Task FetchQuarkSessionAsync()
    {
        // 先把界面上的端口/根目录/API 地址收进配置，取会话时用的就是界面值
        Collect(_runtime.Config);

        _btnQuarkSession.Enabled = false;
        _qResult.Text = "正在从浏览器获取会话…（夸克必须用 --remote-debugging-port 启动）";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var ok = await _runtime.RefreshQuarkSessionAsync(cts.Token, forceBrowser: true).ConfigureAwait(true);

            if (ok)
            {
                // 会话落盘（Cookie 走 DPAPI 加密）
                _runtime.Store.Save(_runtime.Config);
                _qResult.Text = "✔ 会话已获取并保存。" + Environment.NewLine +
                                "  现在可以完全关掉夸克客户端，后续上传不再依赖它。";
            }
            else
            {
                _qResult.Text = "✘ 没拿到会话。检查：" + Environment.NewLine +
                                "  1) 夸克是否已在客户端里登录网盘" + Environment.NewLine +
                                $"  2) 夸克是不是已经在运行却没开调试端口 —— 这种情况必须先完全退出（托盘也退），程序才能接管" + Environment.NewLine +
                                $"  3) 程序路径是否填对（绿色版必须填，当前：{(_qExe.Text.Trim().Length == 0 ? "空" : _qExe.Text.Trim())}）" + Environment.NewLine +
                                $"  4) 端口 {_qPort.Value} 是否被占用：浏览器打开 http://127.0.0.1:{_qPort.Value}/json/version 应能看到版本号";
            }
        }
        catch (Exception ex)
        {
            _qResult.Text = "✘ 获取会话失败：" + ex.Message;
        }
        finally
        {
            _btnQuarkSession.Enabled = true;
            RefreshQuarkSessionLabel();
        }
    }

    private async Task TestQuarkAsync()
    {
        Collect(_runtime.Config);

        _btnQuarkTest.Enabled = false;
        _qResult.Text = "正在测试…";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

            // 没会话就先取一次（走缓存优先，缓存坏了才开浏览器）
            if (!_runtime.QuarkSessionReady &&
                !await _runtime.RefreshQuarkSessionAsync(cts.Token).ConfigureAwait(true))
            {
                _qResult.Text = "✘ 没有可用会话，先点「从浏览器获取会话」";
                return;
            }

            using var storage = _runtime.CreateStorage();

            if (storage is null)
            {
                _qResult.Text = "✘ 存储后端没建起来（会话或配置有问题，看日志）";
                return;
            }

            var lines = new List<string>();

            var quota = await storage.GetQuotaAsync(cts.Token).ConfigureAwait(true);
            lines.Add("✔ 配额：" + (quota?.Describe() ?? "（网盘未返回）"));

            var root = _qRoot.Text;
            var rootShown = NormalizeRoot(root);
            if (rootShown.Length == 0) rootShown = "/";

            var entries = await storage.ListAsync(root, 1, cts.Token).ConfigureAwait(true);
            lines.Add($"✔ 根目录 {rootShown} 可读，{entries.Count} 项");

            foreach (var e in entries.Where(x => x.IsCollection).Take(5))
                lines.Add($"    · [目录] {e.Name}");

            lines.Add("");
            lines.Add("只读检查通过。写入链路已在 quark-storage 契约测试里验证过（建目录/上传/多分片/读回/删除）。");

            _qResult.Text = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            _qResult.Text = "✘ 测试失败：" + ex.Message;
        }
        finally
        {
            _btnQuarkTest.Enabled = true;
            RefreshQuarkSessionLabel();
        }
    }

    private void RefreshQuarkSessionLabel()
    {
        var text = _runtime.QuarkSessionDescribe();
        _qSession.Text = "会话：" + text;

        _qSession.ForeColor = _runtime.QuarkSessionReady
            ? Color.FromArgb(30, 130, 76)
            : Color.FromArgb(190, 100, 60);
    }
}
