using System.Runtime.InteropServices;

namespace Solar.Core.Paths;

/// <summary>
/// 创建硬链接。用于把其它启动器已经下好的资源、库文件直接"接"过来，
/// 不必复制一份也不必重新下载。
/// </summary>
internal static class HardLink
{
    /// <summary>
    /// 创建硬链接；卷或文件系统不支持时返回 false，由调用方退回复制。
    /// </summary>
    public static bool TryCreate(string linkPath, string existingPath)
    {
        try
        {
            return CreateHardLink(linkPath, existingPath, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 非 Windows 或不支持该 API：交给调用方复制
            return false;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string newFileName,
        string existingFileName,
        IntPtr securityAttributes);
}
