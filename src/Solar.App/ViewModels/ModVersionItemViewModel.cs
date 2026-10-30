using Solar.App.Formatting;
using Solar.Core.Mods;

namespace Solar.App.ViewModels;

/// <summary>版本列表里的一行。文本在这里格式化好，模板只负责排版。</summary>
public sealed class ModVersionItemViewModel(ModVersion version, bool compatible)
{
    public ModVersion Version { get; } = version;

    public string DisplayName => string.IsNullOrWhiteSpace(Version.VersionNumber)
        ? Version.Name
        : Version.VersionNumber;

    /// <summary>版本号之外的名字，与版本号相同时不重复显示。</summary>
    public string Subtitle => string.Equals(DisplayName, Version.Name, StringComparison.OrdinalIgnoreCase)
        ? Version.File.FileName
        : $"{Version.Name} · {Version.File.FileName}";

    public string ReleaseLabel => ModDisplay.ReleaseLabel(Version.ReleaseType);

    public bool IsPrerelease => ReleaseLabel.Length > 0;

    public string SizeText => ModDisplay.FileSize(Version.File.Size);

    public string PublishedText => ModDisplay.RelativeTime(Version.PublishedAt);

    /// <summary>必选前置的数量。可选依赖不在这里算，安装清单也只展开必选的。</summary>
    public int RequiredDependencyCount =>
        Version.Dependencies.Count(d => d.Kind == ModDependencyKind.Required);

    public string DependencyText => RequiredDependencyCount == 0 ? "" : $"{RequiredDependencyCount} 个前置";

    public bool HasDependencies => RequiredDependencyCount > 0;

    /// <summary>是否适配当前游戏版本与加载器。关掉「只看兼容」后仍要能一眼看出区别。</summary>
    public bool Compatible { get; } = compatible;
}
