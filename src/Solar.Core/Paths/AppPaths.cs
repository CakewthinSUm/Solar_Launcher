namespace Solar.Core.Paths;

/// <summary>
/// 启动器自己的目录：程序目录、配置/账户/日志目录。
/// <para>
/// 游戏资源与版本数据不在这里，由 <see cref="MinecraftPaths"/> 按 MC 文件夹解析。
/// 配置、账户与日志放在 <c>%APPDATA%\SolarLauncher</c>，换一份程序副本、
/// 删掉程序目录都不会丢登录状态。
/// </para>
/// <para>
/// 显式传入 <c>root</c> 时按完全便携的布局处理：数据目录就在该根目录下的
/// <c>SolarData</c>。测试与便携部署用这个形态，正式运行不传参数。
/// </para>
/// </summary>
public sealed class AppPaths
{
    private const string PortableDataName = "SolarData";
    private const string AppFolderName = "SolarLauncher";

    /// <summary>程序目录，如 <c>D:\Games\SolarLauncher</c>。</summary>
    public string Root { get; }

    /// <summary>
    /// 配置文件、账户缓存、日志与下载缓存。
    /// 默认 <c>%APPDATA%\SolarLauncher</c>；显式指定根目录时为根目录下的 <c>SolarData</c>。
    /// </summary>
    public string Data { get; }

    /// <summary>默认 MC 文件夹：程序目录下的 <c>.minecraft</c>。</summary>
    public string DefaultMinecraft => Path.Combine(Root, ".minecraft");

    public string Logs => Path.Combine(Data, "logs");
    public string SettingsFile => Path.Combine(Data, "settings.json");
    public string AccountsFile => Path.Combine(Data, "accounts.json");

    public AppPaths(string? root = null, string? dataRoot = null)
    {
        Root = Path.GetFullPath(root ?? ResolveDefaultRoot());

        if (dataRoot is not null)
        {
            Data = Path.GetFullPath(dataRoot);
            return;
        }

        // 显式给了根目录 = 便携布局，数据也留在根目录下
        Data = root is not null
            ? Path.Combine(Root, PortableDataName)
            : DefaultDataDirectory();
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
    /// 默认根目录取 exe 所在目录。
    /// <para>
    /// 单文件发布会把整个程序解压到 <c>%TEMP%\.net\...</c> 再运行，
    /// 这时 <see cref="AppContext.BaseDirectory"/> 指向解压目录而不是 exe 所在目录。
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
        return string.IsNullOrEmpty(directory)
            ? AppContext.BaseDirectory
            : Path.GetFullPath(directory);
    }

    /// <summary>
    /// 解析实际使用的 MC 文件夹。设置为空时用默认的 <see cref="DefaultMinecraft"/>；
    /// 支持 <c>%APPDATA%</c> 这类环境变量；相对路径按程序目录解析，
    /// 而不是随进程工作目录变化，保证便携版行为一致。
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

    /// <summary>确保启动器自己的目录都存在。</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Logs);
    }
}
