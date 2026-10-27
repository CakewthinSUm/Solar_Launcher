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
    Content/             实例游戏目录里的 Mods / 资源包 / 光影 读写
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
SolarInstances/     每个实例一个目录，内含 natives、instance.json
                    以及开启版本隔离后的 .minecraft/
.minecraft/         共享游戏目录：存档、模组、配置（默认位置，可在设置中改）
```

共享游戏目录（`.minecraft`）默认被所有实例使用，因此换实例不会丢存档。
把它指向官方启动器的 `.minecraft` 就能直接复用已有的模组与存档。
不同版本的 natives 始终按实例隔离在 `SolarInstances/<id>/natives`。

### 下载与创建实例

「下载 → Minecraft」列出全部版本。点一个版本进入**创建实例页**（参考 PCL 的「自动安装」）：

- 顶部可选游戏版本、改实例名；
- 中间是组件卡片：Forge / NeoForge / Fabric / Quilt 四选一，
  选中 Fabric 或 Quilt 后可加 **Fabric API**，
  另有 **OptiFine**、**OptiFabric**、**LiteLoader**（1.12.2 及更早）；
- 不兼容的组合会直接把卡片置灰并写明原因，切换加载器时冲突项自动清掉；
- LiteLoader 还要求本机装了 Java 8：它依赖的 launchwrapper 在 Java 9+ 上会抛
  `ClassCastException`，所以没检测到 Java 8 时这张卡片直接禁用；
- 点「开始下载」后在同页显示阶段进度，**创建即装完**，回到实例列表就能直接启动。

OptiFine 的装法随组合变化：没有模组加载器时跑官方安装器生成独立版本；
有加载器时直接作为 Mod 放进 `mods`（1.13 起 OptiFine 本身就是 Mod）。

版本隔离的默认值在「设置 → 游戏 → 新建实例的版本隔离」里：每次询问 / 总是开启 / 总是关闭。
默认「每次询问」，创建页上的开关就是那次询问，点开始下载时把选择记住。

### 管理

侧边栏「管理」下是四个子项：

| 子项 | 内容 |
| --- | --- |
| 实例 | 创建 / 删除实例，进入单个实例的设置 |
| Mods | 实例 `mods` 目录里的 jar |
| 资源包 | 实例 `resourcepacks` 目录里的 zip 或解压后的文件夹 |
| 光影 | 实例 `shaderpacks` 目录里的 zip 或解压后的文件夹 |

三类内容都直接读写实例实际使用的游戏目录（开启版本隔离的实例读它自己的
`.minecraft`），不维护额外索引——手动往里放文件，刷新就能看到。
页面里可以导入文件 / 文件夹、在资源管理器中定位、删除。

### 版本隔离

在「管理 → 实例 → 设置」里可以给单个实例开启版本隔离。开启后该实例使用
`SolarInstances/<id>/.minecraft`，存档、模组、配置、资源包都与其它实例分开，
因此可以同时放一个 Fabric 实例和一个 Forge 实例而互不干扰。

- 默认关闭，已有实例的行为不变。
- 关闭 → 开启时可以选择把共享目录的 `mods` / `config` / `saves` / `resourcepacks` /
  `shaderpacks` 等复制一份到实例目录；目标已存在的文件不覆盖，共享目录本身不会被改动。
- 开启 → 关闭时只切回共享目录，实例目录原样保留，下次再开启还在。
- `assets` 与 `libraries` 在 `SolarShared/` 下，始终共享，不受隔离影响。

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
- [x] 启动参数拼装与游戏进程启动
- [x] 下载镜像（BMCLAPI）与实例级 Java / 内存覆盖
- [x] 微软正版登录（设备码流程、多账户、令牌静默刷新）
- [x] 模组加载器安装（Fabric / Quilt / Forge / NeoForge）
- [x] 实例级版本隔离（独立游戏目录，可把共享内容复制过来）
- [x] 「管理」下的 Mods / 资源包 / 光影 列表、导入、删除
- [x] 内存分配：自动按物理内存 / 可用内存 / 模组数量计算，或手动用滑条指定
- [x] 下载页点版本进入创建实例页：选加载器 / Fabric API / OptiFine / LiteLoader，创建时一次装完

## 模组加载器

| 加载器 | 安装方式 |
| --- | --- |
| Fabric / Quilt | 元数据接口直接返回带 `inheritsFrom` 的 profile JSON |
| Forge / NeoForge | 下载 installer jar，取出 `version.json`，再执行客户端 processor |
| OptiFine | 跑官方 `optifine.Installer`（原版 jar 的差分补丁只能由它打），收集产出的库与版本 JSON |
| LiteLoader | 写一份 `inheritsFrom` + launchwrapper 的 profile JSON，库由下载器补齐 |

Forge / NeoForge 的 installer 不是简单的解压：它需要跑若干 processor
（重命名原版 jar、打二进制补丁）才能产出可用的 client artifact。
这些 processor 用 Java 执行，参数里混有三种占位符形态——
`{KEY}` 引用数据表、`[group:artifact:version@ext]` 内联 maven 坐标、
`'literal'` 单引号字面量，全部在 `LoaderInstaller` 里处理。

安装时机是**首次启动该实例时**，不是创建实例时——installer 要下载几百 MB，
建实例时同步等待体验很差。

## 账户

只支持**微软正版账户**，不提供离线登录。

登录走 OAuth 设备码流程：点「登录」后启动器显示一个一次性代码，
在浏览器打开 `microsoft.com/link` 输入即可，无需在启动器内嵌浏览器。

令牌链（OAuth → Xbox Live → XSTS → Minecraft Services）由
[CmlLib.Core.Auth.Microsoft](https://github.com/CmlLib/CmlLib.Core.Auth.Microsoft)
完成，它基于 MSAL.NET 并自行缓存与刷新令牌。
`SolarData/accounts.json` 只存用户名、UUID、XUID 等展示信息，
不含任何令牌；令牌在 `SolarData/msa-accounts.json`。

### 必须自行注册 Azure 应用

启动器**不内置**客户端 ID。使用前需要：

1. 在 [Azure 门户](https://portal.azure.com) 注册应用
2. 应用类型选「移动和桌面应用」，启用「允许公共客户端流」
3. 重定向 URI 填 `http://localhost`
4. 在 [aka.ms/mce-reviewappid](https://aka.ms/mce-reviewappid) 提交审核
   （否则调用 Minecraft API 会返回 403）
5. 把客户端 ID 填进「设置 → 账户」

用别人注册的客户端 ID 违反微软的使用条款。

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
# 真实下载一个版本的全部依赖（不需要登录）
dotnet run --project tools/Solar.Smoke -- 1.21.4 %TEMP%\solar-smoke --prepare-only

# 下载并启动，60 秒后若仍在运行则判定成功（需要先登录）
dotnet run --project tools/Solar.Smoke -- 1.21.4 %TEMP%\solar-smoke

# 验证微软登录全流程：设备码 → Xbox → XSTS → Minecraft → 静默刷新
dotnet run --project tools/Solar.AuthProbe -- <你的客户端ID>

# 验证加载器安装（不给版本号则列出可选项并选最新）
dotnet run --project tools/Solar.LoaderProbe -- fabric 1.21.4
dotnet run --project tools/Solar.LoaderProbe -- forge 1.21.4 54.1.22

# 验证附加组件：OptiFine 会真跑安装器，LiteLoader 会装成可启动的版本
dotnet run --project tools/Solar.LoaderProbe -- optifine 1.20.1
dotnet run --project tools/Solar.LoaderProbe -- liteloader 1.12.2

# 只验证 Fabric API 的列表与下载地址
dotnet run --project tools/Solar.LoaderProbe -- fabricapi 1.21.4

# 验证装好的加载器版本能否真正启动（用合成账户，不需要登录）
dotnet run --project tools/Solar.LaunchProbe -- fabric-loader-0.20.1-1.21.4
dotnet run --project tools/Solar.LaunchProbe -- 1.21.4-forge-54.1.22
```
