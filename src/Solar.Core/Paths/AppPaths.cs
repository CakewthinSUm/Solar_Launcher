namespace Solar.Core.Paths;

/// <summary>
/// 应用用到的所有目录。全部位于一个可搬移的根目录下，符合便携版的要求。
/// 默认根目录为可执行文件所在目录（开发时为 bin/.../SolarLauncher.exe）。
/// </summary>
public sealed class AppPaths
{
    /// <summary>数据根目录，如 <c>D:\Games\SolarLauncher</c>。</summary>
    public string Root { get; }

    /// <summary>配置文件、账户缓存、日志。</summary>
    public string Data => Path.Combine(Root, "SolarData");

    /// <summary>共享资源（assets、libraries、versions、java），可被多个游戏目录共用。</summary>
    public string Shared => Path.Combine(Root, "SolarShared");

    /// <summary>
    /// 默认游戏目录：程序目录下的 <c>.minecraft</c>。
    /// 用户可在设置里改到别处（例如接管官方启动器的目录），此时以设置为准。
    /// </summary>
    public string DefaultMinecraft => Path.Combine(Root, ".minecraft");

    /// <summary>每个“游戏目录/实例”的父目录。</summary>
    public string Instances => Path.Combine(Root, "SolarInstances");

    public string Versions => Path.Combine(Shared, "versions");
    public string Libraries => Path.Combine(Shared, "libraries");
    public string Assets => Path.Combine(Shared, "assets");
    public string Runtimes => Path.Combine(Shared, "runtime");
    public string Logs => Path.Combine(Data, "logs");
    public string SettingsFile => Path.Combine(Data, "settings.json");
    public string AccountsFile => Path.Combine(Data, "accounts.json");
    public string InstanceIndexFile => Path.Combine(Data, "instances.json");

    public AppPaths(string? root = null)
    {
        Root = Path.GetFullPath(root ?? AppContext.BaseDirectory);
    }

    /// <summary>确保所有目录都存在。</summary>
    public void EnsureCreated()
    {
        foreach (var dir in new[] { Data, Shared, Instances, Versions, Libraries, Assets, Runtimes, Logs })
        {
            Directory.CreateDirectory(dir);
        }
    }

    public string GetInstanceDirectory(string instanceId) => Path.Combine(Instances, instanceId);

    /// <summary>
    /// 解析实际使用的游戏目录（.minecraft）。设置为空时用默认的
    /// <see cref="DefaultMinecraft"/>；支持 <c>%APPDATA%</c> 这类环境变量；
    /// 相对路径按程序目录解析，而不是随进程工作目录变化，保证便携版行为一致。
    /// </summary>
    public string ResolveMinecraftDirectory(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultMinecraft;
        }

        var expanded = Environment.ExpandEnvironmentVariables(configured.Trim());
        return Path.GetFullPath(expanded, Root);
    }

    public string GetInstanceFile(string instanceId, string fileName) =>
        Path.Combine(GetInstanceDirectory(instanceId), fileName);
}
