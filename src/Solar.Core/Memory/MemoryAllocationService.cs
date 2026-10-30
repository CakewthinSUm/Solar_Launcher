using Microsoft.Extensions.Logging;
using Solar.Core.Paths;
using Solar.Core.Settings;
using Solar.Core.Versions;

namespace Solar.Core.Memory;

/// <summary>决定某个版本实际使用多大的堆内存。</summary>
public interface IMemoryAllocationService
{
    /// <summary>
    /// 版本级覆盖优先；否则按设置里的分配模式计算：
    /// 自动模式看物理内存、当前可用内存与版本的模组数量，手动模式用设定值。
    /// </summary>
    int Resolve(GameVersion instance);

    /// <summary>自动模式此刻会给出的值（忽略版本覆盖），供界面预览。</summary>
    int Recommend(int modCount);

    SystemMemoryInfo GetSystemMemory();
}

public sealed class MemoryAllocationService(
    ISettingsService settings,
    MinecraftPaths paths,
    ISystemMemoryInfo memoryInfo,
    ILogger<MemoryAllocationService> logger) : IMemoryAllocationService
{
    public int Resolve(GameVersion instance)
    {
        // 版本级覆盖是一个明确的固定值，不再参与自动计算
        if (instance.MaxMemoryMb is { } overridden)
        {
            return MemoryAllocator.Clamp(overridden);
        }

        var current = settings.Current;
        if (current.MemoryAllocationMode == MemoryAllocationMode.Manual)
        {
            return MemoryAllocator.Clamp(current.MaxMemoryMb);
        }

        var mods = CountMods(paths.ResolveGameDirectory(instance.Id, instance.VersionIsolation));
        var memory = memoryInfo.GetCurrent();
        var recommended = MemoryAllocator.Recommend(memory.TotalMb, memory.AvailableMb, mods);

        logger.LogInformation(
            "自动分配内存: 版本 {Instance}, 模组 {Mods} 个, 物理内存 {Total}MB, 可用 {Available}MB → {Memory}MB",
            instance.Name,
            mods,
            memory.TotalMb,
            memory.AvailableMb,
            recommended);

        return recommended;
    }

    public int Recommend(int modCount)
    {
        var memory = memoryInfo.GetCurrent();
        return MemoryAllocator.Recommend(memory.TotalMb, memory.AvailableMb, modCount);
    }

    public SystemMemoryInfo GetSystemMemory() => memoryInfo.GetCurrent();

    /// <summary>
    /// 数一下模组目录里的 jar。禁用中的模组（<c>.jar.disabled</c>）不占游戏内存，不计入。
    /// 目录不存在或读不了时按 0 处理——读不到不该让启动失败。
    /// </summary>
    public static int CountMods(string gameDirectory)
    {
        var modsDirectory = Path.Combine(gameDirectory, "mods");
        if (!Directory.Exists(modsDirectory))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly).Count();
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
