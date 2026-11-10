using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using OnvifWebDav.App.Infrastructure;
using OnvifWebDav.Web;

namespace OnvifWebDav.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 高 DPI / 默认字体必须在任何窗口（包括 MessageBox）之前设置
        ApplicationConfiguration.Initialize();

        using var single = new SingleInstance();
        if (!single.IsFirst)
        {
            // 已经有一个实例在跑：叫它把窗口弹出来，然后自己退出
            single.SignalExisting();
            return;
        }

        AppRuntime? runtime = null;

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => WriteCrash(runtime, e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(runtime, e.ExceptionObject as Exception);

        runtime = new AppRuntime();

        WebApplication? web = null;
        try
        {
            web = WebHostFactory.Build(runtime, runtime.Config.Web.Port);
            web.Start();
            runtime.WebRunning = true;
            runtime.Log.Info("web",
                $"网页服务已启动：http://0.0.0.0:{runtime.Config.Web.Port}" +
                (runtime.ConsumerUrl is { } cb ? $"（事件回调 {cb}）" : ""));
        }
        catch (Exception ex)
        {
            runtime.Log.Error("web", "网页服务启动失败", ex);
            MessageBox.Show(
                "网页服务启动失败：" + ex.Message + Environment.NewLine + Environment.NewLine +
                $"端口 {runtime.Config.Web.Port} 可能被占用。程序仍会继续录像，请到「网页」页换个端口后点「保存并应用」。",
                "网页服务启动失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        try
        {
            runtime.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            runtime.Log.Error("app", "录像服务启动失败", ex);
        }

        var form = new MainForm(runtime, web);
        single.ListenForShow(() =>
        {
            try { form.BeginInvoke(new Action(form.ShowFromTray)); } catch { }
        });

        Application.Run(form);

        // 正常退出后的兜底收尾（MainForm.ExitAsync 已经停过一次，这里防止异常路径漏掉）
        try { runtime.StopAsync().GetAwaiter().GetResult(); } catch { }

        if (web is not null)
        {
            try { web.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); } catch { }
            try { web.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        }

        runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void WriteCrash(AppRuntime? runtime, Exception? ex)
    {
        if (ex is null) return;

        try { runtime?.Log.Error("crash", "未捕获异常", ex); } catch { }

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OnvifWebDav", "logs");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt"),
                ex.ToString());
        }
        catch { }
    }
}
