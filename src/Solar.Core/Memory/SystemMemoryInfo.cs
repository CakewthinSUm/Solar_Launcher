using System.Runtime.InteropServices;

namespace Solar.Core.Memory;

/// <summary>物理内存概况，单位 MB。</summary>
public readonly record struct SystemMemoryInfo(int TotalMb, int AvailableMb);

public interface ISystemMemoryInfo
{
    /// <summary>读取当前物理内存总量与可用量；探测失败时总量为 0。</summary>
    SystemMemoryInfo GetCurrent();
}

/// <summary>
/// 用 <c>GlobalMemoryStatusEx</c> 读取物理内存。
/// <c>GC.GetGCMemoryInfo</c> 拿到的是 .NET 认为可用的上限（可能受容器或 GC 配置限制），
/// 对「还能给游戏多少内存」这个问题不准确，只作为调用失败时的回退。
/// </summary>
public sealed class WindowsSystemMemoryInfo : ISystemMemoryInfo
{
    private const ulong BytesPerMb = 1024 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    public SystemMemoryInfo GetCurrent()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                return new SystemMemoryInfo(ToMb(status.TotalPhys), ToMb(status.AvailPhys));
            }
        }
        catch (Exception)
        {
            // 非 Windows 或调用失败：走下面的回退
        }

        var fallback = ToMb((ulong)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        return new SystemMemoryInfo(fallback, fallback);
    }

    private static int ToMb(ulong bytes) => (int)Math.Min(bytes / BytesPerMb, int.MaxValue);
}
