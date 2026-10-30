# Solar Launcher 与 PCL 启动流程对比

对比对象：

- 我们：`src/Solar.Core/Launch/*`、`Download/*`、`Minecraft/VersionResolver.cs`、`Java/*`、`Accounts/*`
- PCL：官方仓库 [Meloong-Git/PCL](https://github.com/Meloong-Git/PCL)（2.99.0.1，VB.NET）
  - 启动编排 `Modules/Minecraft/ModLaunch.vb`
  - 窗口/崩溃监视 `Modules/Minecraft/ModWatcher.vb`
  - 文件检查 `Modules/Minecraft/ModMinecraft.vb`
  - Java 选择 `Modules/Minecraft/ModJava.vb`
  - 文件校验 `MeloongCore/Shared/Utils/FileUtils.cs`（`FileChecker`）

## 一、阶段编排

| 阶段 | PCL | Solar |
|---|---|---|
| 预检 | 路径非法字符（`!` `;`）、版本状态、登录可用性 | 实例是否存在、账户是否登录 |
| Java | 版本范围 + 模组/OptiFine 约束，找不到自动下载 | 大版本精确匹配 → 更高版本 → 最新（兜底） |
| 登录 | 自带 OAuth 实现，刷新令牌存设置 | CmlLib + MSAL，设备码 |
| 补全文件 | 库与资源并行两条加载器；资源默认**只查存在+大小** | CmlLib 并行安装器；**全部 SHA1 校验** |
| 内存优化 | 可选步骤（清空工作集） | 无 |
| 参数 | 补全文件之后构建，含去重/覆盖 | 启动前构建 |
| Natives | 独立步骤，只解压 dll、按大小跳过、清理多余 | 下载器内的 UpdateTask，全量解压 |
| 预启动处理 | 显卡偏好、launcher_profiles、options.txt、离线皮肤 | 无 |
| 自定义命令 | 有（`McLaunchCustom`） | 无 |
| 启动进程 | java.exe + PATH + 编码 + 优先级 | java.exe + 重定向输出 |
| 等待窗口 | 日志里程碑 + 窗口枚举 + 崩溃判定 + 崩溃分析 | 窗口类名枚举 + Java 报错框 + 5 分钟超时 |
| 结束处理 | 启动器可见性、音乐、全局多实例状态 | 最小化/关闭启动器 |

## 二、逐项差异

### 1. Java 选择（差距最大）

PCL `GetJavaRequirement` 先算出一个**版本范围**：

- 按版本号/发布时间：1.20.5+ 至少 21、1.18+ 至少 17、1.17+ 至少 16、1.12+ 至少 8、1.5.2- 最多 8；
- 版本 JSON 的 `javaVersion.majorVersion >= 22` 时取 Mojang 要求的最低版本；
- OptiFine：1.8–1.11 必须恰好 Java 8，1.12 最多 8；
- Forge：按 Forge 版本细分（1.16.3–5 最多 8u320、1.17.1 最多 16、1.19.4–1.20.1 最多 21 等十余条）；
- NeoForge、Fabric（0.16.x 及更早不兼容 Java 25）、LiteLoader（最多 8）。

找不到合适的 Java 时，PCL 会拉取 Mojang 的 `java-runtime` 列表**自动下载**一个。

我们：`JavaDetector.Select` 只按 `javaVersion.majorVersion` 做「精确 → 最小更高 → 最新」三段式，且找不到时**回退到最新版本**硬启动。26.2 需要 Java 25、机器上只有 21，于是把 JDK 24+ 才有的 `--sun-misc-unsafe-memory-access=allow` 传给 Java 21，JVM 直接报错。

### 2. 文件补全与校验

| | PCL | Solar |
|---|---|---|
| 资源校验 | 启动时 `CheckAssetsHash=False`：只查存在 + 大小 | 每个资源对象都算 SHA1 |
| 库校验 | 大小 + SHA1 | SHA1 |
| 已有文件复用 | 在 MC 文件夹、各版本文件夹里按大小+Hash 找同内容文件，找到就**复制** | 资源从 `.minecraft\assets` **硬链接**；库/版本按固定路径查 |
| 跳过校验 | `ShouldIgnoreFileCheck` 设置：只查存在 | 无 |
| 资源索引更新 | 后台每天更新一次索引 | 无 |

我们刚实测：3919 个文件 / 887 MB 全量 SHA1 约 1.9 秒（NVMe）。机械盘或大整合包下这个差距会放大——PCL 的策略是把资源哈希校验排除在启动路径之外。

### 3. 版本 JSON 合并

| | PCL | Solar |
|---|---|---|
| 合并方向 | 子版本库在前、父版本在后，拼接 | 父在前，子覆盖 |
| 去重键 | 完整坐标（含版本）+ 是否 natives；同坐标保留高版本 | `group:artifact[:classifier]`，子覆盖父 |
| 平台变体 | 天然保留（键含完整坐标） | 已修复：同版本内保留全部变体 |

两者取向不同：PCL 靠 classpath 顺序决定同名类，我们把同 artifact 去重以避免 Fabric Knot 的 duplicate ASM 报错。我们的方案需要额外小心平台变体（1.12.2 的 LWJGL 2 就踩过）。

### 4. 启动参数

PCL 在 `McLaunchArgumentsJVM/Game` 里做了大量我们没做的事：

- **去重/覆盖**：同名单参数丢弃、同名键值对后者覆盖（`--width`、`--uuid` 等重复会让游戏失效）；
- **编码**：按 Java 版本写 `stdout.encoding`/`stderr.encoding`，Java 18+ 用 `-Dfile.encoding=COMPAT`；
- **JLW**：中文用户名/路径下用 `JavaWrapper.jar` 包一层，绕开 JDK-8272352 的命令行编码问题；
- **GC 策略**：按 Java 版本选 G1GC/ZGC，Java 24+ 加 `-XX:+UseCompactObjectHeaders`；
- **log4j 防护**：`-Dlog4j2.formatMsgNoLookups=true`；
- **游戏参数**：`--fullscreen`、自动进服（1.20+ 用 `--quickPlayMultiplayer`）、OptiFine/Forge 的 `tweakClass` 顺序修复；
- **路径短名**：`${library_directory}`、natives 目录都转 8.3 短路径。

我们：版本参数展开、占位符替换、额外参数、log4j 配置、分辨率；没有去重、编码参数、GC、log4j 防护、自动进服。

### 5. Natives

| | PCL | Solar |
|---|---|---|
| 解压范围 | 只解压 `.dll` | 除 `META-INF/` 外全部 |
| 跳过策略 | 目标文件大小一致则跳过 | 每次都覆盖写 |
| 清理 | 删除目标目录里多余的旧文件 | 无 |
| 路径 | 含非 ASCII 时回退短路径 / `%APPDATA%\.minecraft\bin\natives` | 实例目录下 `natives` |

### 6. 进程启动

| | PCL | Solar |
|---|---|---|
| 可执行文件 | `java.exe`（注释说明 javaw 有问题） | `java.exe` |
| 工作目录 | 实例游戏目录 | 实例游戏目录 |
| 环境变量 | PATH 加 Java 目录、`appdata` 指向 MC 目录 | 不修改 |
| 输出编码 | 按检测到的 Java 编码设置 | 默认（UTF-8）→ 中文日志可能乱码 |
| 优先级 | 可配置（高于/低于正常）+ PriorityBoost | 不设置 |
| 取消 | 启动后立刻取消会 Kill 进程 | 取消令牌贯穿 |

### 7. 等待窗口与崩溃判定

PCL `Watcher` 的状态机：`Loading → Running → Ended/Crashed/Canceled`。

- **窗口识别**：枚举 `GLFW30`、`SDL_app`、`LWJGL`、`SunAwtFrame`；跳过隐藏的 SDL 窗口、空标题、`PopupMessageWindow`、`GLFW*`、`*Hidden Utility Window`；要求窗口所属进程的启动时间不早于游戏进程（防误认旧窗口）。
- **加载器窗口**：`FML*`、`Quilt Loader` 标题只算"窗口出现"，不算加载完成。
- **日志里程碑**：`Setting user:` → `LWJGL version` → `OpenAL initialized`/`Starting up SoundSystem` → `Created ... textures ... -atlas`，进度 5/5 即认为加载完成（即使窗口还没出现）。
- **崩溃判定**：进程在 Loading 阶段退出、运行中非 0 退出（2012 年后的版本）、日志出现 `Crash report saved to`、`/ERROR]: Unable to launch` 等。
- **崩溃分析**：`ModCrash.vb` 按知识库给出原因与建议。
- **窗口后处理**：自定义窗口标题、最大化。

我们：枚举 `GLFW*`/`LWJGL` + `#32770` Java 报错框，进程退出或 5 分钟超时即失败，带日志尾部。没有日志里程碑、加载器窗口区分、崩溃分析。

### 8. 结束处理与多实例

PCL：全局 `McWatcherList` 维护所有运行中的游戏，据此决定"是否还有游戏在跑"，并在最后一个游戏结束时按设置关闭/恢复启动器、恢复音乐。

我们：单个启动流程的状态 + `AfterLaunchAction`（最小化/关闭），没有跨实例的全局运行状态。

## 三、已对齐（2026-10-30）

| 项 | 落地内容 |
|---|---|
| 预检 | 启动前检查游戏目录/版本目录不含 `!`、`;`，实例必须设置版本 |
| Java 选择 | 新增 `JavaRequirements`：按版本号 + 模组/OptiFine/LiteLoader 约束算范围；找不到合适的 Java 时明确报错，不再回退到最新版本 |
| 校验 | 资源改为只查存在 + 大小（内容寻址），库与版本仍查 SHA1 |
| 参数构建 | 去重/覆盖（JVM 与游戏参数）、`stdout/stderr.encoding=UTF-8`、按 Java 版本选 G1GC/ZGC、log4j 防护、`-Xmx` 以启动器为准 |
| Natives | 只解压 dll、大小一致跳过、并行安全、结束后清理多余文件；非 ASCII 路径回退到 LocalAppData |
| 等待窗口 | 日志里程碑（Setting user → LWJGL → OpenAL → 材质）+ 崩溃关键字 + 加载器窗口识别 + 更多窗口类名 |

## 四、仍未对齐（PCL 有、我们没有）

1. **Java 自动下载**：现在能在没有合适 Java 时明确报错，但还不会像 PCL 那样从 Mojang 的 java-runtime 列表下载一个。
2. **崩溃分析**：只显示崩溃关键字与日志尾部，没有 PCL 的知识库式原因分析。
3. **自定义启动命令、内存优化步骤**：完全没有。
4. **全局多实例监视**：没有跨实例的「是否还有游戏在跑」状态与音乐暂停/恢复。
5. **JLW（中文路径换行编码包装）**：我们改用固定 UTF-8 输出，未处理命令行本身的编码问题。
6. **自动进服 / 全屏设置**：没有对应设置项。

## 五、我们做得不同/更好的地方

- **登录**：CmlLib + MSAL 托管令牌缓存与刷新，设备码体验和错误翻译比 PCL 手写 OAuth 更省心。
- **资源复用方式**：硬链接而非复制，不占额外磁盘空间。
- **下载引擎**：CmlLib 并行安装器 + 镜像改写，并发数可配。
- **进度呈现**：阶段权重合成总进度、任务栏进度、失败时把日志尾部直接放进卡片。
- **数据目录**：单文件版自动迁移到 exe 旁边，换构建不丢数据。
- **架构**：Core 层与 UI 解耦（可单测、可做 CLI 探针），PCL 的启动逻辑与 WPF 窗体耦合较深。

## 六、附：关键实现位置

| 功能 | PCL | Solar |
|---|---|---|
| 启动编排 | `ModLaunch.vb` `McLaunchStart` | `GameLaunchService.LaunchAsync` |
| Java 需求 | `ModJava.vb` `GetJavaRequirement` | `JavaRequirements.For` + `JavaDetector.Select` |
| 文件检查 | `ModMinecraft.vb` `McAssetsFixList` / `McLibNetFilesFromInstance` | `GameDownloader.DownloadAsync` + CmlLib `ParallelGameInstaller` |
| 参数 | `ModLaunch.vb` `McLaunchArgumentsJVM/Game` | `ArgumentBuilder` |
| Natives | `ModLaunch.vb` `McLaunchNatives` | `GameFileBuilder.NativeExtractTask` |
| 进程启动 | `ModLaunch.vb` `McLaunchRun` | `GameLauncher.Start` |
| 窗口/崩溃 | `ModWatcher.vb` `Watcher` | `GameWindowFinder` + `GameLogMonitor` + `GameLaunchService.WaitForGameWindowAsync` |
