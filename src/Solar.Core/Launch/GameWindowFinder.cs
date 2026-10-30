using System.Runtime.InteropServices;
using System.Text;

namespace Solar.Core.Launch;

/// <summary>
/// 找游戏窗口。
/// <para>
/// 不能只看 <see cref="System.Diagnostics.Process.MainWindowHandle"/>：Java 启动失败时
/// 会弹一个「Java Virtual Machine Launcher」对话框，它同样是进程的主窗口，
/// 只看句柄会把启动失败当成启动成功。这里按窗口类名区分：
/// 游戏的 GLFW 窗口是 <c>GLFW30</c>（LWJGL 3）或 <c>LWJGL</c>（LWJGL 2），
/// 而 Java 的报错框是标准对话框 <c>#32770</c>。
/// </para>
/// </summary>
internal static class GameWindowFinder
{
    public enum WindowKind
    {
        /// <summary>还没有窗口。</summary>
        None,

        /// <summary>只出现了模组加载器的窗口（FML / Quilt Loader），游戏本体还没起来。</summary>
        Loader,

        /// <summary>游戏窗口已经出现。</summary>
        Game,

        /// <summary>Java 自己弹出了报错对话框，游戏不会起来了。</summary>
        JavaError,
    }

    public static WindowKind Probe(int processId, out string title)
    {
        var kind = WindowKind.None;
        var foundTitle = "";

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != processId)
            {
                return true;
            }

            var className = GetClassName(hwnd);

            // 标准对话框：Java 的启动错误（Unrecognized option / Could not create the JVM）
            if (string.Equals(className, "#32770", StringComparison.Ordinal))
            {
                var dialogTitle = GetWindowText(hwnd);
                if (dialogTitle.Contains("Java", StringComparison.OrdinalIgnoreCase))
                {
                    kind = WindowKind.JavaError;
                    foundTitle = dialogTitle;
                    return false;
                }

                return true;
            }

            // LWJGL 3 的窗口类是 GLFW30（个别版本是 GLFW32），LWJGL 2 是 LWJGL，
            // 少数版本用 SDL（SDL_app）或 AWT（SunAwtFrame）
            var isGameClass =
                className.StartsWith("GLFW", StringComparison.Ordinal)
                || string.Equals(className, "LWJGL", StringComparison.Ordinal)
                || string.Equals(className, "SDL_app", StringComparison.Ordinal)
                || string.Equals(className, "SunAwtFrame", StringComparison.Ordinal);

            if (!isGameClass)
            {
                return true;
            }

            var windowTitle = GetWindowText(hwnd);

            // 排除 GLFW 的消息窗口、隐藏工具窗口与 SDL 的占位窗口
            if (string.IsNullOrWhiteSpace(windowTitle)
                || string.Equals(windowTitle, "SDL_app", StringComparison.Ordinal)
                || string.Equals(windowTitle, "PopupMessageWindow", StringComparison.Ordinal)
                || windowTitle.StartsWith("GLFW", StringComparison.Ordinal)
                || windowTitle.EndsWith("Hidden Utility Window", StringComparison.Ordinal))
            {
                return true;
            }

            // 模组加载器的窗口先于游戏窗口出现，只算"有窗口了"，继续找真正的游戏窗口
            if (windowTitle.StartsWith("FML", StringComparison.Ordinal)
                || windowTitle.StartsWith("Quilt Loader", StringComparison.Ordinal))
            {
                kind = WindowKind.Loader;
                foundTitle = windowTitle;
                return true;
            }

            kind = WindowKind.Game;
            foundTitle = windowTitle;
            return false;
        }, IntPtr.Zero);

        title = foundTitle;
        return kind;
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    private static string GetWindowText(IntPtr hwnd)
    {
        var buffer = new StringBuilder(512);
        return GetWindowTextW(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW", SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW", SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder buffer, int maxCount);
}
