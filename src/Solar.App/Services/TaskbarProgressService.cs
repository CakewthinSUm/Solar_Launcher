using System.Runtime.InteropServices;

namespace Solar.App.Services;

/// <summary>
/// 把下载进度画到任务栏图标上（Windows 10/11 的任务栏进度条）。
/// <para>
/// 走 <c>ITaskbarList3</c>：这是 shell 唯一公开的任务栏进度接口，
/// Windows App SDK 没有对应的 WinRT 投影，所以只能自己声明 COM 接口。
/// 接口方法必须按前置接口（ITaskbarList → ITaskbarList2 → ITaskbarList3）
/// 的完整顺序声明，漏一个后面的偏移就全错了。
/// </para>
/// </summary>
public sealed class TaskbarProgressService
{
    /// <summary>进度条的分母。shell 只关心比例，1000 足够细。</summary>
    private const ulong ProgressMax = 1000;

    private readonly ITaskbarList3? _taskbar;
    private nint _hwnd;

    public TaskbarProgressService()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _taskbar = (ITaskbarList3)new TaskbarList();
            _taskbar.HrInit();
        }
        catch (Exception)
        {
            // 任务栏进度只是锦上添花，拿不到接口就不显示，不能让启动器起不来
            _taskbar = null;
        }
    }

    /// <summary>绑定主窗口。窗口创建后调用一次。</summary>
    public void Attach(nint hwnd) => _hwnd = hwnd;

    /// <summary>
    /// 更新进度。<paramref name="ratio"/> 取 0~1；传 null 表示总量未知，显示滚动的不确定进度。
    /// </summary>
    public void Report(double? ratio)
    {
        if (_taskbar is null || _hwnd == 0)
        {
            return;
        }

        try
        {
            if (ratio is null)
            {
                _taskbar.SetProgressState(_hwnd, TaskbarProgressState.Indeterminate);
                return;
            }

            var value = (ulong)(Math.Clamp(ratio.Value, 0, 1) * ProgressMax);
            _taskbar.SetProgressValue(_hwnd, value, ProgressMax);
            _taskbar.SetProgressState(_hwnd, TaskbarProgressState.Normal);
        }
        catch (Exception)
        {
            // 窗口已销毁或 shell 未就绪时忽略
        }
    }

    /// <summary>
    /// 闪烁任务栏按钮，提示下载完成。
    /// <para>
    /// <c>FLASHW_TIMERNOFG</c> 让它一直闪到用户把窗口切回前台为止，
    /// 不需要手动停止——窗口一旦激活，系统自己会停。
    /// 配 <c>FLASHW_TRAY</c> 只闪任务栏图标，不动标题栏。
    /// </para>
    /// </summary>
    public void Flash()
    {
        if (!OperatingSystem.IsWindows() || _hwnd == 0)
        {
            return;
        }

        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = _hwnd,
            Flags = FlashTray | FlashTimerNoForeground,
            // TIMERNOFG 模式下次数被忽略，填 0
            Count = 0,
            Timeout = 0,
        };

        try
        {
            FlashWindowEx(ref info);
        }
        catch (Exception)
        {
            // 与进度条一样，提示失败不影响主流程
        }
    }

    /// <summary>下载结束（成功、失败或取消）后清掉进度条。</summary>
    public void Reset()
    {
        if (_taskbar is null || _hwnd == 0)
        {
            return;
        }

        try
        {
            _taskbar.SetProgressState(_hwnd, TaskbarProgressState.NoProgress);
        }
        catch (Exception)
        {
            // ignore
        }
    }

    /// <summary><c>FLASHW_TRAY</c>：只闪任务栏按钮。</summary>
    private const uint FlashTray = 0x00000002;

    /// <summary><c>FLASHW_TIMERNOFG</c>：持续闪烁，直到窗口回到前台。</summary>
    private const uint FlashTimerNoForeground = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}

/// <summary>任务栏进度状态，对应 <c>TBPF_*</c>。</summary>
public enum TaskbarProgressState : uint
{
    NoProgress = 0,
    Indeterminate = 1,
    Normal = 2,
    Error = 4,
    Paused = 8,
}

/// <summary>
/// TaskbarList 的 coclass。注意尾段是 <c>006097C9A090</c>，
/// 而不是常见文档里 IID 那串 <c>...CF9A24</c> 的结尾——抄错就会 REGDB_E_CLASSNOTREG。
/// 实现在 <c>explorerframe.dll</c>。
/// </summary>
[ComImport]
[Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
internal class TaskbarList
{
}

[ComImport]
[Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskbarList3
{
    // ITaskbarList
    void HrInit();

    void AddTab(nint hwnd);

    void DeleteTab(nint hwnd);

    void ActivateTab(nint hwnd);

    void SetActiveAlt(nint hwnd);

    // ITaskbarList2
    void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

    // ITaskbarList3
    void SetProgressValue(nint hwnd, ulong completed, ulong total);

    void SetProgressState(nint hwnd, TaskbarProgressState state);
}
