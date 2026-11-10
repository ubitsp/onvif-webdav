using Microsoft.Win32;

namespace OnvifWebDav.App.Infrastructure;

/// <summary>
/// 开机自启：写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run，
/// 不需要管理员权限（对比写 HKLM 或放启动文件夹，这个最省事）。
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OnvifWebDav";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrEmpty(value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>返回是否成功。失败时给用户一句中文原因。</summary>
    public static bool TryApply(bool enabled, out string message)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (key is null)
            {
                message = "无法写入注册表 Run 项";
                return false;
            }

            if (enabled)
            {
                var exe = Environment.ProcessPath
                          ?? Path.Combine(AppContext.BaseDirectory, "OnvifWebDav.App.exe");
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
                message = "已设置开机自启（启动后直接进托盘）";
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                message = "已取消开机自启";
            }

            return true;
        }
        catch (Exception ex)
        {
            message = "设置失败：" + ex.Message;
            return false;
        }
    }
}
