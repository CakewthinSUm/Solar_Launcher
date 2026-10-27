# Solar Launcher

基于 WinUI 3（Windows App SDK）的新一代 Minecraft 启动器。

当前是**基础骨架**：UI 框架、依赖注入、导航、实例管理、版本清单拉取、Java 探测已打通；
Minecraft 的依赖下载与游戏启动尚未实现。

## 技术栈

| 层 | 技术 |
| --- | --- |
| UI | WinUI 3 / Windows App SDK 1.8，非打包便携版 |
| 架构 | MVVM（CommunityToolkit.Mvvm）+ 构造函数依赖注入 |
| 核心 | .NET 8 类库 `Solar.Core`，与 UI 完全解耦 |
| 测试 | xUnit（`tests/Solar.Core.Tests`） |

## 项目结构

```
src/
  Solar.Core/            核心层，无 UI 依赖
    Paths/               AppPaths：所有目录集中管理，便携版根目录 = exe 所在目录
    Json/                统一 JSON 选项、原子写入、参数数组转换器
    Models/              Mojang 版本 JSON 模型、rules 求值
    Minecraft/           版本清单下载与缓存、版本 JSON 解析与 inheritsFrom 合并
    Instances/           游戏实例模型与持久化
    Java/                Java 探测与版本选择
    Settings/            全局设置
  Solar.App/             WinUI 3 界面层
    Services/            导航服务、对话框服务
    ViewModels/          各页面视图模型
    Views/               页面（PageBase 统一处理 DataContext 与加载）
    Converters/          XAML 值转换器
tests/
  Solar.Core.Tests/      核心层单元测试
```

## 设计规范

界面遵循 WinUI 3 官方规范：

- **标题栏**：`TitleBar` 控件 + `ExtendsContentIntoTitleBar`，导航面板开关放在标题栏
- **导航**：`NavigationView`（`PaneDisplayMode="Left"`，设置项使用内置入口），内容在 `Frame` 中
- **主题**：颜色全部取自 `ThemeResource`（卡片底色、描边、控件圆角），自动适配浅色/深色/高对比度
- **排版**：使用系统排版样式（`TitleTextBlockStyle`、`BodyStrongTextBlockStyle`、`CaptionTextBlockStyle`），
  页面内边距 24px、区块间距 24px、卡片内边距 16px，遵循 4px 网格
- **提示**：行内提示统一用 `InfoBar`，确认操作使用 `ContentDialog`
- **窗口材质**：`MicaBackdrop`，系统不支持时自动回退

## 目录布局（便携版）

运行后在 exe 所在目录生成：

```
SolarData/          设置、账户、日志、版本清单缓存
SolarShared/        共享资源：versions / libraries / assets / runtime
SolarInstances/     每个实例一个目录，内含 .minecraft 与 instance.json
```

## 开发

```bash
dotnet build SolarLauncher.sln
dotnet test tests/Solar.Core.Tests/Solar.Core.Tests.csproj
dotnet run --project src/Solar.App
```

要求：.NET 8 SDK、Windows 10 1809 及以上。
Windows App SDK 以自包含方式随输出一起分发，终端用户无需单独安装运行时。

## 当前进度

- [x] 项目骨架、DI、MVVM、导航
- [x] 实例创建 / 删除 / 列表，持久化到磁盘
- [x] 版本清单拉取与本地缓存（可在设置中换成国内镜像）
- [x] 版本 JSON 下载、`inheritsFrom` 合并、rules 平台求值
- [x] Java 自动探测与版本匹配
- [x] 依赖（libraries / assets / natives）下载与 SHA1 校验
- [x] 启动参数拼装与游戏进程启动（离线账户）
- [x] 下载镜像（BMCLAPI）与实例级 Java / 内存覆盖
- [ ] 账户系统（微软登录）
- [ ] 模组加载器安装（Forge / NeoForge / Fabric / Quilt）

## 下载引擎

依赖下载使用 [CmlLib.Core](https://github.com/CmlLib/CmlLib.Core) 的 `ParallelGameInstaller`：
它用 TPL Dataflow 把"校验"和"下载"分成两级流水线，各自独立并行度，
并内置 SHA1 校验与背压。我们只使用它的下载部分，版本解析、参数拼装、
进程启动全部是 `Solar.Core` 自己的实现。

镜像通过 `MirrorRewritingHandler` 在 HTTP 层重写 URL：
只重写 `libraries.minecraft.net`（→ `/maven/`）与
`resources.download.minecraft.net`（→ `/assets/`），
`piston-data` 等路径保持直连，因为镜像对它们的支持不完整。

## 端到端验证

```bash
# 真实下载一个版本的全部依赖
dotnet run --project tools/Solar.Smoke -- 1.21.4 %TEMP%\solar-smoke --prepare-only

# 下载并启动，60 秒后若仍在运行则判定成功
dotnet run --project tools/Solar.Smoke -- 1.21.4 %TEMP%\solar-smoke
```
