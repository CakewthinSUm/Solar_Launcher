namespace Solar.Core.Paths;

/// <summary>
/// 应用用到的所有目录。
/// <para>
/// 正式运行时分成两处：游戏资源（<see cref="Shared"/>、<see cref="Instances"/>、
/// <see cref="DefaultMinecraft"/>）仍在程序目录下，可以随程序一起搬移；
/// 配置、账户、日志与缓存放在 <c>%APPDATA%\SolarLauncher</c>，
/// 换一份程序副本、删掉程序目录都不会丢登录状态。
/// </para>
/// <para>
/// 显式传入 <c>root</c> 时按完全便携的布局处理：数据目录就在该根目录下的
/// <c>SolarData</c>。测试与便携部署用这个形态，正式运行不传参数。
/// </para>
/// </summary>
public sealed class AppPaths
{
    private const string DataDirectoryName = "SolarData";
    private const string AppFolderName = "SolarLauncher";

    /// <summary>程序目录，如 <c>D:\Games\SolarLauncher</c>。</summary>
    public string Root { get; }

    /// <summary>
    /// 配置文件、账户缓存、日志与下载缓存。
    /// 默认 <c>%APPDATA%\SolarLauncher</c>；显式指定根目录时为根目录下的 <c>SolarData</c>。
    /// </summary>
    public string Data { get; }

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

    public AppPaths(string? root = null, string? dataRoot = null)
    {
        Root = Path.GetFullPath(root ?? ResolveDefaultRoot());

        if (dataRoot is not null)
        {
            Data = Path.GetFullPath(dataRoot);
            return;
        }

        if (root is not null)
        {
            // 显式给了根目录 = 便携布局，数据也留在根目录下
            Data = Path.Combine(Root, DataDirectoryName);
            return;
        }

        Data = DefaultDataDirectory();

        // 旧版本把 SolarData 写在 exe 旁边，更早的单文件版还写在临时解压目录里。
        // 升级后整份搬到 %APPDATA%，否则设置和登录状态看起来就“丢了”。
        MigrateDataDirectory(Path.Combine(Root, DataDirectoryName), Data);
        MigrateDataDirectory(Path.Combine(AppContext.BaseDirectory, DataDirectoryName), Data);
    }

    /// <summary>正式运行时的数据目录：<c>%APPDATA%\SolarLauncher</c>。</summary>
    private static string DefaultDataDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            // 极端情况下拿不到 %APPDATA%，退到程序目录，至少还能用
            appData = AppContext.BaseDirectory;
        }

        return Path.Combine(appData, AppFolderName);
    }

    /// <summary>
    /// 把旧数据目录整体搬到新位置。
    /// <para>
    /// 目标已有内容时一律不动，避免覆盖用户当前的数据；同卷直接改名，
    /// 跨卷退化成复制后删除，都失败就保持原样，不影响本次运行。
    /// </para>
    /// </summary>
    private static void MigrateDataDirectory(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            return;
        }

        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any())
        {
            return;
        }

        try
        {
            if (!Directory.Exists(to))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                Directory.Move(from, to);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 跨卷改名会失败：下面走复制
        }

        try
        {
            CopyDirectory(from, to);
            Directory.Delete(from, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 搬不了就让数据留在原处，至少不丢
        }
    }

    /// <summary>
    /// 默认根目录取 exe 所在目录。
    /// <para>
    /// 单文件发布会把整个程序解压到 <c>%TEMP%\.net\...</c> 再运行，
    /// 这时 <see cref="AppContext.BaseDirectory"/> 指向解压目录而不是 exe 所在目录：
    /// 用它当根目录，数据会随临时目录清理丢失，换一个构建（解压目录哈希变了）
    /// 也等于换了一份新数据——登录状态、实例列表全都没了。
    /// </para>
    /// </summary>
    private static string ResolveDefaultRoot()
    {
        var processPath = Environment.ProcessPath;
        // 通过 dotnet <dll> 启动时 ProcessPath 是 dotnet 自己，不能拿它当根目录
        if (string.IsNullOrEmpty(processPath)
            || string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            return AppContext.BaseDirectory;
        }

        var directory = Path.GetDirectoryName(processPath);
        if (string.IsNullOrEmpty(directory))
        {
            return AppContext.BaseDirectory;
        }

        var root = Path.GetFullPath(directory);
        var extractionDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        if (!string.Equals(root, extractionDirectory, StringComparison.OrdinalIgnoreCase))
        {
            MigrateLegacyData(extractionDirectory, root);
        }

        return root;
    }

    /// <summary>
    /// 留在程序目录下的数据目录，迁移时整体搬走。
    /// <para>
    /// 不含 <c>SolarData</c>：配置与账户已经改放 <c>%APPDATA%</c>，
    /// 由构造函数里的 <see cref="MigrateDataDirectory"/> 单独处理。
    /// </para>
    /// </summary>
    private static readonly string[] DataDirectoryNames =
        ["SolarShared", "SolarInstances", ".minecraft"];

    /// <summary>
    /// 把旧版本留在临时解压目录里的数据搬到新根目录（exe 旁边）。
    /// <para>
    /// 只在单文件发布时发生（<see cref="AppContext.BaseDirectory"/> 与 exe 目录不同），
    /// 并且目标目录还没有数据时才动手，避免覆盖用户现有的数据。
    /// 同卷直接改名，跨卷（临时目录常在 C:，程序可能在别的盘）退化成复制后删除。
    /// </para>
    /// </summary>
    private static void MigrateLegacyData(string legacyRoot, string newRoot)
    {
        if (HasLauncherData(newRoot) || !HasLauncherData(legacyRoot))
        {
            return;
        }

        foreach (var name in DataDirectoryNames)
        {
            var from = Path.Combine(legacyRoot, name);
            var to = Path.Combine(newRoot, name);
            if (!Directory.Exists(from) || Directory.Exists(to))
            {
                continue;
            }

            try
            {
                Directory.Move(from, to);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 跨卷改名会失败：复制一份再删旧的，失败就保持原样，不影响本次运行
                try
                {
                    CopyDirectory(from, to);
                    Directory.Delete(from, recursive: true);
                }
                catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException)
                {
                    // 迁移不了就让数据留在原处，至少不丢
                }
            }
        }
    }

    private static bool HasLauncherData(string root) =>
        DataDirectoryNames.Any(name => Directory.Exists(Path.Combine(root, name)));

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
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
    /// 实例的 natives 解压目录。
    /// <para>
    /// 路径里有非 ASCII 字符时回退到 <c>%LOCALAPPDATA%\SolarLauncher\natives</c>：
    /// 部分原生库按 ANSI 路径加载，中文目录下会直接加载失败（PCL 同样做了这个回退）。
    /// 实例名本身也可能带中文，所以回退目录用哈希而不是实例名。
    /// </para>
    /// </summary>
    public string GetNativesDirectory(string instanceId)
    {
        var primary = Path.Combine(GetInstanceDirectory(instanceId), "natives");
        if (primary.All(char.IsAscii))
        {
            return primary;
        }

        var safeName = instanceId.All(char.IsAscii)
            ? instanceId
            : Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(instanceId)))[..12].ToLowerInvariant();

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SolarLauncher",
            "natives",
            safeName);
    }

    /// <summary>开启版本隔离的实例所用的游戏目录（.minecraft）。</summary>
    public string GetInstanceMinecraftDirectory(string instanceId) =>
        Path.Combine(GetInstanceDirectory(instanceId), ".minecraft");

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
