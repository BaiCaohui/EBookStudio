using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EbookToolSuite.App.Themes;

/// <summary>
/// 标题栏深色（T7-3）。WPF 的标题栏归系统画，不会跟着我们的主题变；
/// Win10 1809 起可以用 DWMWA_USE_IMMERSIVE_DARK_MODE 让系统把标题栏画成深色。
/// 老系统 / 调用失败一律静默跳过——标题栏颜色不值得挡住启动。
/// </summary>
internal static class DwmDarkMode
{
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE，Win10 1809(17763)+。</summary>
    private const int UseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>把窗口标题栏切成深色 / 浅色。窗口还没建句柄时什么也不做。</summary>
    internal static void Apply(Window? window, bool dark)
    {
        if (window is null)
        {
            return;
        }

        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var value = dark ? 1 : 0;
            var hr = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref value, sizeof(int));
            if (hr != 0)
            {
                // 老系统不认这个属性：记一次调试信息就够，不打日志免得刷屏
                System.Diagnostics.Debug.WriteLine($"DwmSetWindowAttribute 失败 hr=0x{hr:X8}");
            }
        }
        catch (Exception)
        {
            // dwmapi.dll 不存在（极老系统 / 非 Windows）：忽略
        }
    }
}
