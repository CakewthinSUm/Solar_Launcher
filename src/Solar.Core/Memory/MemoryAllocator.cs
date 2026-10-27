namespace Solar.Core.Memory;

/// <summary>堆内存的分配方式。</summary>
public enum MemoryAllocationMode
{
    /// <summary>按物理内存、当前可用内存与模组数量自动计算。</summary>
    Automatic = 0,

    /// <summary>使用用户设定的固定值。</summary>
    Manual,
}

/// <summary>
/// 堆上限（<c>-Xmx</c>）的计算规则。纯函数，不读系统状态，便于单测；
/// 系统内存由 <see cref="ISystemMemoryInfo"/> 提供。
/// </summary>
public static class MemoryAllocator
{
    /// <summary>低于这个值，现代版本连原版都难以启动。</summary>
    public const int MinimumMb = 512;

    /// <summary>JVM 用这么大的堆再大也没什么意义，反而拖慢 GC。</summary>
    public const int MaximumMb = 32768;

    /// <summary>原版所需的基础堆内存。1.21 原版 2GB 足够，堆开太大反而拖慢 GC。</summary>
    private const int VanillaMb = 2048;

    /// <summary>每多少个模组增加一档内存。</summary>
    private const int ModsPerStep = 25;

    /// <summary>每档增加的堆内存。</summary>
    private const int StepMb = 512;

    /// <summary>自动分配的上限：再多也不如留给系统。</summary>
    private const int MaxAutomaticMb = 16384;

    /// <summary>系统自身至少需要的余量。</summary>
    private const int SystemReserveMb = 1024;

    /// <summary>结果按 512MB 取整，避免出现 3872 这种别扭的数字。</summary>
    private const int RoundToMb = 512;

    /// <summary>限制到允许的取值范围。</summary>
    public static int Clamp(int memoryMb) => Math.Clamp(memoryMb, MinimumMb, MaximumMb);

    /// <summary>
    /// 自动分配：先按模组数量估算需求，再用「物理内存扣除系统余量」封顶。
    /// </summary>
    public static int Recommend(int totalMb, int availableMb, int modCount)
    {
        // 探测失败时给一个保守值，而不是 512MB 这种没法玩的数字
        if (totalMb <= 0)
        {
            return 4096;
        }

        // 封顶值的依据。
        // 不能直接用「当前可用」：Windows 的可用物理内存不含可回收的待机缓存，
        // 一台 16GB 的机器开着一堆程序时可能只报几百 MB，但那些缓存随要随让。
        // 按它封顶会在最不该小气的时候给出 512MB，游戏连加载都过不去。
        // 因此取「可用量」与「总量的一半」中的较大者，保证任何情况下都能拿到像样的堆。
        var basis = Math.Max(availableMb, totalMb / 2);

        // 再给系统留 1GB：换页压力主要来自这里，而不是那个被缓存吃掉的可用量
        var ceiling = Math.Clamp(basis - SystemReserveMb, MinimumMb, MaxAutomaticMb);

        // 需求随模组数量增长：原版 2GB，每 25 个模组 +512MB
        var demand = VanillaMb + (Math.Max(modCount, 0) / ModsPerStep) * StepMb;
        demand = Math.Min(demand, MaxAutomaticMb);

        var result = Math.Min(demand, ceiling);
        return Math.Clamp(result / RoundToMb * RoundToMb, MinimumMb, MaximumMb);
    }
}
