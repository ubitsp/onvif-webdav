using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using OnvifWebDav.App.UI;
using OnvifWebDav.Config;
using OnvifWebDav.Web;

namespace OnvifWebDav.App;

internal sealed class MainForm : Form
{
    private readonly AppRuntime _runtime;

    private readonly StatusPanel _statusPanel;
    private readonly CameraPanel _cameraPanel;
    private readonly StoragePanel _storagePanel;
    private readonly RecordingPanel _recordingPanel;
    private readonly WebPanel _webPanel;
    private readonly EventTestPanel _eventPanel;
    private readonly LogPanel _logPanel;

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly NotifyIcon _tray;
    private readonly Button _btnPause = new() { Text = "暂停录制", AutoSize = true };

    private WebApplication? _webApp;
    private bool _exitRequested;
    private bool _trayTipShown;

    public MainForm(AppRuntime runtime, WebApplication? webApp)
    {
        _runtime = runtime;
        _webApp = webApp;

        Text = "ONVIF 运动录像 → WebDAV";
        MinimumSize = new Size(760, 560);
        Size = new Size(880, 680);
        StartPosition = FormStartPosition.CenterScreen;

        _statusPanel = new StatusPanel();
        _cameraPanel = new CameraPanel(runtime);
        _storagePanel = new StoragePanel(runtime);
        _recordingPanel = new RecordingPanel();
        _webPanel = new WebPanel();
        _eventPanel = new EventTestPanel(runtime);
        _logPanel = new LogPanel(runtime);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };
        tabs.TabPages.Add(Wrap("状态", _statusPanel));
        tabs.TabPages.Add(Wrap("摄像头", _cameraPanel));
        tabs.TabPages.Add(Wrap("存储", _storagePanel));
        tabs.TabPages.Add(Wrap("录制", _recordingPanel));
        tabs.TabPages.Add(Wrap("网页", _webPanel));
        tabs.TabPages.Add(Wrap("事件自检", _eventPanel));
        tabs.TabPages.Add(Wrap("日志", _logPanel));

        var bottom = BuildBottomBar();

        Controls.Add(tabs);
        Controls.Add(bottom);

        _tray = BuildTray();

        BindAll();

        _timer.Tick += (_, _) => _statusPanel.Refresh(_runtime.Status);
        _timer.Start();
        _statusPanel.Refresh(_runtime.Status);

        FormClosing += OnFormClosing;
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized) HideToTray();
        };
    }

    private static TabPage Wrap(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(0) };
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        return page;
    }

    private Control BuildBottomBar()
    {
        var save = new Button { Text = "保存并应用", AutoSize = true };
        var start = new Button { Text = "启动录像服务", AutoSize = true };
        var stop = new Button { Text = "停止服务", AutoSize = true };
        var restartWeb = new Button { Text = "重启网页服务", AutoSize = true };
        var hide = new Button { Text = "最小化到托盘", AutoSize = true };

        save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        start.Click += async (_, _) => await StartServiceAsync().ConfigureAwait(true);
        stop.Click += async (_, _) => await StopServiceAsync().ConfigureAwait(true);
        restartWeb.Click += async (_, _) => await RestartWebAsync().ConfigureAwait(true);
        hide.Click += (_, _) => HideToTray();
        _btnPause.Click += (_, _) => TogglePause();

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(8, 8, 8, 8),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        bar.Controls.AddRange(new Control[] { save, start, stop, _btnPause, restartWeb, hide });
        return bar;
    }

    private NotifyIcon BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主界面", null, (_, _) => ShowFromTray());
        menu.Items.Add("打开网页", null, (_, _) => OpenWebPage());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("暂停 / 恢复录制", null, (_, _) => TogglePause());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await ExitAsync().ConfigureAwait(true));

        var tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "ONVIF 运动录像",
            Visible = true,
            ContextMenuStrip = menu,
        };
        tray.DoubleClick += (_, _) => ShowFromTray();
        return tray;
    }

    private void BindAll()
    {
        _cameraPanel.Bind(_runtime.Config);
        _storagePanel.Bind(_runtime.Config);
        _recordingPanel.Bind(_runtime.Config);
        _webPanel.Bind(_runtime.Config);
    }

    private void CollectAll()
    {
        _cameraPanel.Collect(_runtime.Config);
        _storagePanel.Collect(_runtime.Config);
        _recordingPanel.Collect(_runtime.Config);
        _webPanel.Collect(_runtime.Config);
    }

    private async Task SaveAsync()
    {
        try
        {
            CollectAll();
            await _runtime.ApplyConfigAsync(_runtime.Config).ConfigureAwait(true);
            _runtime.Log.Info("app", "配置已保存并应用");
            MessageBox.Show(this, "配置已保存，录像服务已按新配置重启。", "完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task StartServiceAsync()
    {
        try
        {
            await _runtime.StartAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "启动失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task StopServiceAsync()
    {
        await _runtime.StopAsync().ConfigureAwait(true);
    }

    private void TogglePause()
    {
        var worker = _runtime.Worker;
        if (worker is null) return;

        if (worker.IsPaused) worker.Resume();
        else worker.Pause();

        _btnPause.Text = worker.IsPaused ? "恢复录制" : "暂停录制";
    }

    private async Task RestartWebAsync()
    {
        try
        {
            if (_webApp is not null)
            {
                await _webApp.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                await _webApp.DisposeAsync().ConfigureAwait(true);
                _webApp = null;
            }
            _runtime.WebRunning = false;

            _webApp = WebHostFactory.Build(_runtime, _runtime.Config.Web.Port);
            await _webApp.StartAsync().ConfigureAwait(true);
            _runtime.WebRunning = true;
            _runtime.Log.Info("web", $"网页服务已启动，端口 {_runtime.Config.Web.Port}");
            MessageBox.Show(this, $"网页已在端口 {_runtime.Config.Web.Port} 启动。", "完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "网页服务启动失败：" + ex.Message + Environment.NewLine +
                "端口可能被占用，请在「网页」页换一个端口后保存。",
                "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenWebPage()
    {
        try
        {
            var url = $"http://localhost:{_runtime.Config.Web.Port}";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            { UseShellExecute = true });
        }
        catch { }
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;

        if (_trayTipShown) return;
        _trayTipShown = true;
        try
        {
            _tray.ShowBalloonTip(3000, "仍在后台录像",
                "程序已最小化到右下角托盘。双击图标可恢复窗口，右键可退出。", ToolTipIcon.Info);
        }
        catch { }
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exitRequested) return;

        // 关窗口 = 收进托盘，不退出（否则用户会以为还在录，其实已经停了）
        e.Cancel = true;
        HideToTray();
    }

    private async Task ExitAsync()
    {
        _exitRequested = true;
        _timer.Stop();
        _tray.Visible = false;

        _runtime.Log.Info("app", "用户请求退出，正在停止服务…");

        try { await _runtime.StopAsync().ConfigureAwait(true); } catch { }

        if (_webApp is not null)
        {
            try { await _webApp.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true); } catch { }
            try { await _webApp.DisposeAsync().ConfigureAwait(true); } catch { }
            _webApp = null;
        }

        _tray.Dispose();
        Close();
        Application.Exit();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // 开了开机自启 + --minimized 时直接进托盘
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
            HideToTray();
    }
}
