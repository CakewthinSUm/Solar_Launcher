using Microsoft.Extensions.Logging.Abstractions;
using Solar.Core.Addons;
using Solar.Core.Download;
using Solar.Core.Instances;
using Solar.Core.Java;
using Solar.Core.Loaders;
using Solar.Core.Minecraft;
using Solar.Core.Mods;
using Solar.Core.Paths;
using Xunit;

namespace Solar.Core.Tests;

/// <summary>
/// 创建实例的编排：用假的下载器与安装器验证步骤顺序、进度上报，
/// 以及「失败也保留记录」这条约定。
/// </summary>
public sealed class InstanceCreationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "solar-create-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppPaths _paths;
    private readonly InstanceRepository _repository;
    private readonly FakeLoaderInstaller _loaderInstaller = new();
    private readonly FakeDownloader _downloader = new();

    public InstanceCreationTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        _repository = new InstanceRepository(_paths);
    }

    private InstanceCreationService CreateService() => new(
        _paths,
        _repository,
        new FakeResolver(),
        _downloader,
        _loaderInstaller,
        new FakeOptiFine(),
        new FakeLiteLoaderInstaller(),
        new FakeModInstaller(_paths),
        new InstanceGameDirectory(_paths, new FakeSettings()),
        new FakeJavaDetector(),
        new HttpClient(new FakeHandler()),
        NullLogger<InstanceCreationService>.Instance);

    [Fact]
    public async Task VanillaInstanceIsSavedWithoutLoader()
    {
        var progress = new List<InstanceCreateProgress>();

        var instance = await CreateService().CreateAsync(
            new InstanceCreateRequest("原版实例", "1.21.4", new ComponentSelection(), false),
            new Progress<InstanceCreateProgress>(progress.Add));

        Assert.Equal("原版实例", instance.Name);
        Assert.Equal(ModLoader.Vanilla, instance.Loader);
        Assert.True(instance.LoaderInstalled);
        Assert.Equal("1.21.4", instance.EffectiveVersionId);
        Assert.False(instance.VersionIsolation);

        var saved = await _repository.GetAsync(instance.Id);
        Assert.NotNull(saved);

        Assert.Equal(1, _downloader.Calls);
        Assert.Equal(0, _loaderInstaller.Calls);

        // 阶段要覆盖准备与下载，界面上的进度条才有东西可显示
        Assert.Contains(progress, p => p.Stage == InstanceCreateStage.Preparing);
        Assert.Contains(progress, p => p.Stage == InstanceCreateStage.DownloadingGame);
    }

    [Fact]
    public async Task LoaderInstanceInstallsLoaderBeforeDownloading()
    {
        var instance = await CreateService().CreateAsync(
            new InstanceCreateRequest(
                "Fabric 实例",
                "1.21.4",
                new ComponentSelection { Loader = ModLoader.Fabric, LoaderVersion = "0.16.9" },
                true));

        Assert.Equal(ModLoader.Fabric, instance.Loader);
        Assert.Equal("0.16.9", instance.LoaderVersion);
        Assert.True(instance.LoaderInstalled);
        Assert.Equal("fabric-loader-0.16.9-1.21.4", instance.VersionId);
        Assert.True(instance.VersionIsolation);

        // 加载器安装会传真实实例 Id，natives 不该落到 installer-temp
        Assert.Equal(instance.Id, _loaderInstaller.ScratchInstanceId);
        Assert.DoesNotContain("installer-temp", _downloader.InstanceIds);
    }

    [Fact]
    public async Task FabricApiIsDownloadedIntoIsolatedModsFolder()
    {
        var instance = await CreateService().CreateAsync(
            new InstanceCreateRequest(
                "带 API",
                "1.21.4",
                new ComponentSelection
                {
                    Loader = ModLoader.Fabric,
                    LoaderVersion = "0.16.9",
                    FabricApi = new AddonModVersion(
                        "0.119.4+1.21.4",
                        new Solar.Core.Mods.ModFile(
                            "fabric-api.jar", "https://example.invalid/fabric-api.jar", null, 0)),
                },
                VersionIsolation: true));

        Assert.Contains("fabric-api.jar", instance.CompanionMods);

        var modsDirectory = Path.Combine(_paths.GetInstanceMinecraftDirectory(instance.Id), "mods");
        Assert.True(Directory.Exists(modsDirectory));
        Assert.True(File.Exists(Path.Combine(modsDirectory, "fabric-api.jar")));
    }

    [Fact]
    public async Task FailureKeepsInstanceRecordForFirstLaunchRetry()
    {
        _downloader.Throw = new InvalidOperationException("网络断了");

        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            new InstanceCreateRequest(
                "半成品",
                "1.21.4",
                new ComponentSelection { Loader = ModLoader.Fabric, LoaderVersion = "0.16.9" },
                false)));

        var all = await _repository.GetAllAsync();

        Assert.Single(all);
        Assert.Equal("半成品", all[0].Name);
    }

    [Fact]
    public async Task MissingLoaderVersionIsRejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().CreateAsync(
            new InstanceCreateRequest(
                "没有版本",
                "1.21.4",
                new ComponentSelection { Loader = ModLoader.Forge, LoaderVersion = null },
                false)));
    }

    [Fact]
    public async Task OptiFineWithLoaderGoesToModsInsteadOfProfile()
    {
        var instance = await CreateService().CreateAsync(
            new InstanceCreateRequest(
                "Forge + OptiFine",
                "1.20.1",
                new ComponentSelection
                {
                    Loader = ModLoader.Forge,
                    LoaderVersion = "47.3.0",
                    OptiFine = new OptiFineBuild("1.20.1", "HD_U", "I6", "OptiFine_1.20.1_HD_U_I6.jar", null),
                },
                VersionIsolation: true));

        // 有主加载器时不生成 OptiFine profile，版本 Id 仍是加载器的
        Assert.Equal("1.20.1-forge-47.3.0", instance.VersionId);
        Assert.Equal("1.20.1_HD_U_I6", instance.OptiFineVersion);
        Assert.Contains("OptiFine_1.20.1_HD_U_I6.jar", instance.CompanionMods);
    }

    [Fact]
    public void DisplayLoaderIncludesAddons()
    {
        var instance = new GameInstance
        {
            Loader = ModLoader.Fabric,
            LoaderVersion = "0.16.9",
            OptiFineVersion = "1.20.1_HD_U_I6",
        };

        Assert.Equal("Fabric 0.16.9 · OptiFine 1.20.1_HD_U_I6", instance.DisplayLoader);

        var optifineOnly = new GameInstance { OptiFineVersion = "1.12.2_HD_U_G5" };
        Assert.Equal("OptiFine 1.12.2_HD_U_G5", optifineOnly.DisplayLoader);

        var vanilla = new GameInstance();
        Assert.Equal("原版", vanilla.DisplayLoader);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // 测试清理失败不影响结果
        }
    }

    private sealed class FakeLoaderInstaller : ILoaderInstaller
    {
        public int Calls { get; private set; }
        public string? ScratchInstanceId { get; private set; }

        public Task<string> InstallAsync(
            ModLoader loader,
            string minecraftVersion,
            string loaderVersion,
            string javaPath,
            IProgress<LoaderInstallProgress>? progress = null,
            CancellationToken ct = default,
            string? scratchInstanceId = null)
        {
            Calls++;
            ScratchInstanceId = scratchInstanceId;
            progress?.Report(new LoaderInstallProgress("正在下载加载器库", 1, 2));

            var id = loader switch
            {
                ModLoader.Fabric => $"fabric-loader-{loaderVersion}-{minecraftVersion}",
                ModLoader.Quilt => $"quilt-loader-{loaderVersion}-{minecraftVersion}",
                ModLoader.NeoForge => $"neoforge-{loaderVersion}",
                _ => $"{minecraftVersion}-forge-{loaderVersion}",
            };

            return Task.FromResult(id);
        }
    }

    private sealed class FakeDownloader : IGameDownloader
    {
        public int Calls { get; private set; }
        public List<string> InstanceIds { get; } = new();
        public Exception? Throw { get; set; }

        public Task DownloadAsync(
            GameInstance instance,
            ResolvedVersion version,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            Calls++;
            InstanceIds.Add(instance.Id);

            if (Throw is not null)
            {
                throw Throw;
            }

            progress?.Report(new DownloadProgress(1, 1, 100, 100) { CurrentFile = "client.jar" });
            return Task.CompletedTask;
        }
    }

    private sealed class FakeResolver : IVersionResolver
    {
        public Task<ResolvedVersion> ResolveAsync(string versionId, CancellationToken ct = default) =>
            Task.FromResult(new ResolvedVersion
            {
                Detail = new Solar.Core.Models.VersionDetail
                {
                    Id = versionId,
                    MainClass = "net.minecraft.client.main.Main",
                },
                Chain = [],
            });

        public Task<Solar.Core.Models.VersionDetail> DownloadVersionJsonAsync(
            Solar.Core.Models.VersionSummary summary, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeOptiFine : IOptiFineService
    {
        public Task<IReadOnlyList<OptiFineBuild>> GetBuildsAsync(string minecraftVersion, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OptiFineBuild>>([]);

        public string GetInstallerUrl(OptiFineBuild build) => "https://example.invalid/optifine.jar";

        public Task<string> InstallAsync(
            OptiFineBuild build,
            string minecraftVersion,
            string vanillaJarPath,
            string vanillaJsonPath,
            string javaPath,
            IProgress<LoaderInstallProgress>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult($"{minecraftVersion}-OptiFine_{build.DisplayVersion}");
    }

    private sealed class FakeLiteLoaderInstaller : ILiteLoaderInstaller
    {
        public Task<string> InstallAsync(
            LiteLoaderBuild build,
            string minecraftVersion,
            IProgress<LoaderInstallProgress>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult($"{minecraftVersion}-LiteLoader");
    }

    /// <summary>
    /// 附加 Mod 现在走 <see cref="Solar.Core.Mods.IModInstaller"/>。这里照它的落点规则
    /// （实例实际使用的游戏目录下的 mods/）把文件写出去，让隔离相关的断言仍然有效。
    /// </summary>
    private sealed class FakeModInstaller(AppPaths paths) : Solar.Core.Mods.IModInstaller
    {
        private readonly InstanceGameDirectory _gameDirectories =
            new(paths, new FakeSettings());

        public string GetContentDirectory(
            GameInstance? instance,
            Solar.Core.Mods.ModProjectType type,
            string? world = null) =>
            Path.Combine(
                instance is null ? _gameDirectories.Shared : _gameDirectories.Resolve(instance),
                type.ContentFolder() ?? "mods");

        public bool IsInstalled(
            GameInstance? instance,
            Solar.Core.Mods.ModProjectType type,
            Solar.Core.Mods.ModFile file,
            string? world = null) =>
            File.Exists(Path.Combine(GetContentDirectory(instance, type, world), file.FileName));

        public IReadOnlyList<string> GetWorlds(GameInstance? instance) => [];

        public Task<Solar.Core.Mods.ModInstallResult> InstallAsync(
            GameInstance? instance,
            Solar.Core.Mods.ModProjectType type,
            Solar.Core.Mods.ModInstallPlan plan,
            string? world = null,
            bool overwrite = false,
            IProgress<Solar.Core.Download.DownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            var directory = GetContentDirectory(instance, type, world);
            Directory.CreateDirectory(directory);

            var files = new List<string>();
            foreach (var item in plan.Pending)
            {
                var name = item.Version!.File.FileName;
                File.WriteAllText(Path.Combine(directory, name), "stub");
                files.Add(name);
            }

            return Task.FromResult(new Solar.Core.Mods.ModInstallResult(files.Count, 0, files));
        }
    }

    private sealed class FakeJavaDetector : IJavaDetector
    {
        public Task<IReadOnlyList<JavaRuntime>> DetectAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<JavaRuntime>>([new JavaRuntime("java.exe", "21", 21)]);

        public JavaRuntime? Select(IReadOnlyList<JavaRuntime> runtimes, Solar.Core.Java.JavaRequirement requirement) =>
            runtimes.FirstOrDefault(requirement.IsSatisfiedBy);
    }

    private sealed class FakeSettings : Solar.Core.Settings.ISettingsService
    {
        public Solar.Core.Settings.LauncherSettings Current { get; } = new();
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // 附加 Mod 的下载：返回一小段内容，验证落盘即可
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4]),
            };

            return Task.FromResult(response);
        }
    }
}
