namespace Solar.Core.Addons;

/// <summary>
/// 一个 OptiFine 构建。<c>Type</c> + <c>Patch</c> 是下载路径的两段（如 <c>HD_U</c> / <c>I6</c>），
/// <c>Version</c> 是写进版本 JSON 的完整构建名（如 <c>1.20.1_HD_U_I6</c>）。
/// </summary>
public sealed record OptiFineBuild(
    string MinecraftVersion,
    string Type,
    string Patch,
    string FileName,
    string? ForgeVersion)
{
    /// <summary>构建名，与 OptiFine 安装器写在 <c>optifine:OptiFine:&lt;ver&gt;</c> 里的一致。</summary>
    public string Version => $"{MinecraftVersion}_{Type}_{Patch}";

    /// <summary>界面上显示的简短版本，去掉 MC 版本号前缀。</summary>
    public string DisplayVersion => $"{Type}_{Patch}";

    public override string ToString() => DisplayVersion;
}

/// <summary>Modrinth 上的一个 Mod 版本（Fabric API / OptiFabric）。</summary>
public sealed record AddonModVersion(
    string Id,
    string VersionNumber,
    string FileName,
    string Url,
    string? Sha1)
{
    public override string ToString() => VersionNumber;
}

/// <summary>LiteLoader 的一个构建。</summary>
/// <param name="MinecraftVersion">所属的 MC 版本。</param>
/// <param name="Version">安装器里的版本号，如 <c>1.12.2-SNAPSHOT</c>。</param>
/// <param name="TweakClass">启动时要传给 launchwrapper 的 tweakClass。</param>
/// <param name="Libraries">该构建依赖的库（maven 坐标）。</param>
public sealed record LiteLoaderBuild(
    string MinecraftVersion,
    string Version,
    string TweakClass,
    IReadOnlyList<string> Libraries)
{
    public override string ToString() => Version;
}

/// <summary>OptiFine 安装器里取出的、生成版本 profile 所需的信息。</summary>
/// <param name="OptiFineVersion">如 <c>1.20.1_HD_U_I6</c>。</param>
/// <param name="LaunchWrapperVersion">内嵌的 launchwrapper 版本，如 <c>2.3</c>。</param>
public sealed record OptiFineProfileInfo(string OptiFineVersion, string LaunchWrapperVersion);
