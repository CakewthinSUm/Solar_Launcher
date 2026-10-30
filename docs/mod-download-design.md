# Mod 下载功能设计方案

> 本文是 **Mod** 的原始设计。引擎后来扩展到光影 / 整合包 / 资源包 / 数据包，
> 类型映射、整合包安装与数据包过滤见 [addon-download-design.md](addon-download-design.md)。
> 文中提到的 `ModDownloadPage` / `ModProjectPage` 已改名为 `AddonDownloadPage` /
> `AddonProjectPage`，安装对话框为 `AddonInstallDialog` 与 `ModpackInstallDialog`。

本文先梳理 PCL2 的实现，再给出 Solar Launcher 的落地方案。
文末「已验证事实」一节记录的是本次实际请求过的接口，可直接作为实现依据。

---

## 一、PCL2 是怎么做的

源码：`Meloong-Git/PCL`（VB.NET，默认分支 `main`）。相关文件集中在
`Modules/Resource/`（搜索、模型、依赖）与 `Modules/Minecraft/ModDownload.vb`（镜像与请求）。

### 1.1 数据源

只有两个上游：**CurseForge 官方 API** 和 **Modrinth 官方 API**。
国内访问统一走 **MCIM 镜像 `mod.mcimirror.top`**（注意不是 BMCLAPI，BMCLAPI 只服务游戏本体）。

- **CurseForge 需要 API Key**（`x-api-key` 头），PCL 从环境变量
  `PCL_CURSEFORGE_API_KEY` 读取，**开源版默认为空**——也就是说官方构建里
  CurseForge 实际上只能通过镜像访问。Modrinth 不需要鉴权。
- 镜像通过**纯字符串前缀替换**实现，同一个函数同时用于 API、文件、图标：

  | 原始主机 | 镜像 |
  | --- | --- |
  | `api.modrinth.com` | `mod.mcimirror.top/modrinth` |
  | `cdn.modrinth.com` | `mod.mcimirror.top` |
  | `api.curseforge.com` | `mod.mcimirror.top/curseforge` |
  | `edge.forgecdn.net` / `mediafilez.forgecdn.net` / `media.forgecdn.net` | `mod.mcimirror.top` |

- 源顺序由设置决定（默认「尽量官方」）：官方(20s/10s) → 官方 30s → 镜像 30s，
  逐个 try/catch，全失败抛 `AggregateException`。

### 1.2 搜索与浏览

- 两源**并行**请求，CurseForge 结果固定排在 Modrinth 前面（避免网络快慢影响排名）。
- 分页是**累加式**：每翻一页把 `TargetResultCount` 提高到 `(Page+1)*40`，重新拉取，而不是丢缓存。
- Modrinth 用 facets 表达过滤：`[["project_type:mod"],["categories:fabric"],["versions:1.21.4"]]`，
  外层数组是 AND，内层是 OR。
- CurseForge 有个兼容处理：**1.13 及以下不传 `modLoaderType`**，因为老 Mod 大多没标加载器，
  拿回结果后在本地过滤。
- 两源结果做**跨源去重**（`IsLike`）：Id 相同即同；否则要求加载器相同、游戏版本集合相同、
  更新时间差 ≤7 天，且译名/原名/描述/slug 规范化后相同。
- 排序：无搜索词按 `下载量 × 平台权重`；有搜索词按 `相似度 + log10(下载量)/9 + 是否有中文名`。

### 1.3 中文搜索（PCL 体验的核心）

CurseForge 和 Modrinth 都只认英文。PCL 内置一份 MC 百科条目表
（`PCLCS/Resource/WikiEntries.txt`，含 slug ↔ 中文名 ↔ 浏览量），先把中文查询词
模糊匹配成英文 slug，再拿英文去搜；匹配不到直接提示「请尝试搜索其英文名称」。

**这是 PCL 最容易被低估的一块，也是纯 API 方案做不到的部分。**

### 1.4 详情与版本列表

- 详情页很轻：**没有富文本描述、没有内嵌图集**，只有一行 summary + 标签 + 外链按钮。
- 版本列表**一次拉全量再本地分组**：CurseForge `pageSize=10000`（JEI 有 3984 个文件、
  6MB JSON），Modrinth 一次请求。然后按「游戏版本 + 加载器」在本地切成卡片。
- 依赖只用于**展示**（「前置资源」区块），**不会自动下载**。
  CurseForge 取 `relationType=3`，Modrinth 取 `dependency_type=required`，
  两边都排除 Fabric API / Quilt API。

### 1.5 下载与安装

- 目录：`{PathIndie}/mods/`，`PathIndie` 是版本的游戏目录：开启隔离时就是该版本的
  版本文件夹（`.minecraft/versions/<版本id>` 或共享 `.minecraft/versions/<版本id>`），
  未隔离时是共享 `.minecraft`。
- 目标目录是**用户级选择**：优先上次用过的目录 → 当前版本（若版本+加载器兼容）→
  遍历版本找兼容的 → 兜底 `McFolderSelected`。最后还会弹一次「另存为」。
- **不做自动依赖下载**。整合包安装是另一条代码路径。
- 文件名默认按「中文名 + 原名」重命名（`~` 换成 `-`，否则 Mixin 加载失败）。
- 下载引擎：多线程 Range 分片（<1MB 不分片）、单源失败换源、全源失败降级为
  「单线程无 Range」重下一次、Hash 校验、分片合并。断点续传**不跨进程**。
- 已装 Mod 更新：本地算 MurmurHash2（CF）/ SHA1（Modrinth）反查 projectId，
  再比对 `latestFilesIndexes` 或 `/version_files/update`。

---

## 二、Solar Launcher 的方案

### 2.1 与 PCL 的三处关键取舍

| 点 | PCL | 本方案 | 理由 |
| --- | --- | --- | --- |
| 中文搜索 | 内置 MC 百科词表 | **v1 不做**，搜索框提示用英文/原名 | 词表要跟着 MC 百科更新，维护成本高；先保证英文搜索可用 |
| 版本列表 | 一次拉 10000 条本地分组 | **服务端过滤**后分页拉取 | 实测 CF 的 `gameVersion`+`modLoaderType` 过滤可用，JEI 在 1.21.4/NeoForge 下只有 2 个文件，没必要拉 6MB |
| 依赖 | 只展示不下载 | **安装时勾选前置并一并下载** | 前置缺失是新手最常见的崩溃原因，成本也不高 |
| 文件名 | 改成中文名 | **保留上游文件名** | 更新检测、去重、社区排障都依赖原始文件名 |

### 2.2 数据源

两个上游都接，默认走 MCIM 镜像（国内可用，且 CF 不需要 API Key）：

```
Modrinth  官方 https://api.modrinth.com/v2
          镜像 https://mod.mcimirror.top/modrinth/v2
CurseForge 官方 https://api.curseforge.com/v1   （需要用户自备 API Key）
           镜像 https://mod.mcimirror.top/curseforge/v1 （免 Key）
```

设置项（`LauncherSettings` 新增）：

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `ModSourcePreference` | `Auto` | `Auto` 官方优先、`MirrorFirst` 镜像优先、`MirrorOnly` 仅镜像 |
| `ModMirrorUrl` | `https://mod.mcimirror.top` | 可换自建/其它镜像 |
| `CurseForgeApiKey` | 空 | 留空则 CurseForge 只能走镜像 |

镜像改写集中在一个类里，**与现有的 `MirrorRewritingHandler` 互不影响**
（后者只处理 Mojang 的两个主机）：

```csharp
// Solar.Core/Mods/ModSourceEndpoints.cs
public Uri Rewrite(Uri uri);              // api.modrinth.com -> <mirror>/modrinth
                                          // cdn.modrinth.com  -> <mirror>
                                          // api.curseforge.com -> <mirror>/curseforge
                                          // *.forgecdn.net     -> <mirror>
```

> 实测镜像的 `/data/{projectId}/versions/{versionId}/{file}`、`/files/{a}/{b}/{name}`、
> `/avatars/{a}/{b}/{name}` 三条文件路径都可用，改写规则与 PCL 完全一致。

### 2.3 数据模型（统一两个源）

```csharp
// Solar.Core/Mods/ModModels.cs
public enum ModSource { Modrinth, CurseForge }

public sealed record ModProject
{
    public required ModSource Source { get; init; }
    public required string Id { get; init; }          // Modrinth: base62 / CF: 数字
    public string? Slug { get; init; }
    public required string Title { get; init; }
    public string Summary { get; init; } = "";        // 列表用的一行简介
    public string? Body { get; init; }                // 详情正文（CF 走镜像拿不到，为 null）
    public string? IconUrl { get; init; }
    public string? WebsiteUrl { get; init; }
    public long Downloads { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public IReadOnlyList<ModLoader> Loaders { get; init; } = [];
    public IReadOnlyList<string> GameVersions { get; init; } = [];
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> Gallery { get; init; } = [];
}

public sealed record ModVersion
{
    public required ModSource Source { get; init; }
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string Name { get; init; }
    public string VersionNumber { get; init; } = "";
    public required ModFile File { get; init; };
    public IReadOnlyList<string> GameVersions { get; init; } = [];
    public IReadOnlyList<ModLoader> Loaders { get; init; } = [];
    public IReadOnlyList<ModDependency> Dependencies { get; init; } = [];
    public ReleaseType ReleaseType { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public long Downloads { get; init; }
    public string? Changelog { get; init; }
}

public sealed record ModFile(string FileName, string Url, string? Sha1, long Size);

public sealed record ModDependency(string ProjectId, string? VersionId, DependencyKind Kind);

public enum DependencyKind { Required, Optional, Incompatible, Embedded }
```

加载器映射集中在 `ModLoaderMapping`：

| `ModLoader` | Modrinth facet 值 | CF `modLoaderType` |
| --- | --- | --- |
| Forge | `forge` | 1 |
| Fabric | `fabric` | 4 |
| Quilt | `quilt` | 5 |
| NeoForge | `neoforge` | 6 |
| Vanilla | 不加 facet | 不传 |

### 2.4 服务层

```csharp
// 每个源一个实现，UI 层永远只看到统一模型
public interface IModCatalog
{
    ModSource Source { get; }
    Task<ModSearchPage> SearchAsync(ModSearchQuery query, CancellationToken ct);
    Task<ModProject> GetProjectAsync(string projectId, CancellationToken ct);
    Task<IReadOnlyList<ModVersion>> GetVersionsAsync(string projectId, ModVersionFilter filter, CancellationToken ct);
    Task<IReadOnlyList<ModCategory>> GetCategoriesAsync(CancellationToken ct);
}

// 聚合层：并行查两源、去重、合并、单源失败降级
public interface IModCatalogService
{
    Task<ModSearchPage> SearchAsync(ModSearchQuery query, CancellationToken ct);
    Task<ModProjectDetail> GetProjectAsync(ModSource source, string projectId, CancellationToken ct);
    Task<IReadOnlyList<ModVersion>> GetVersionsAsync(ModSource source, string projectId, ModVersionFilter filter, CancellationToken ct);
}
```

`ModSearchQuery` 的字段：`Text`、`GameVersion`、`Loader`、`Category`、`Source`（任一/两源）、
`Sort`（相关度/下载量/更新时间/最新发布）、`Offset`、`Limit`。

**搜索的具体请求**（已验证）：

- Modrinth
  `GET /v2/search?query=&limit=40&offset=&index=relevance|downloads|updated|newest`
  `&facets=[["project_type:mod"],["categories:fabric"],["versions:1.21.4"]]`
- CurseForge
  `GET /v1/mods/search?gameId=432&classId=6&gameVersion=1.21.4&modLoaderType=4`
  `&categoryId=423&searchFilter=sodium&sortField=2&sortOrder=desc&index=0&pageSize=50`

**跨源去重**沿用 PCL 的思路但简化：规范化标题（小写、去符号、去空格）相等，
且加载器有交集，且 `UpdatedAt` 相差 ≤7 天 → 视为同一个项目，保留 CurseForge 那条，
把 Modrinth 的下载量/图标补进去，并在卡片上标「CF / MR」双来源。

**降级**：两源并行，任一失败不阻塞；只有全部失败才报错。部分失败时在列表上方显示
`InfoBar`：「CurseForge 暂时不可用，当前只显示 Modrinth 的结果」。

### 2.5 版本列表与依赖

- 请求时**带上版本的版本与加载器**做服务端过滤，UI 默认只看兼容版本，
  另有「显示全部版本」开关。
- 默认选中「最新正式版」（`release` 优先，其次 `beta`/`alpha`）。
- 依赖解析：拿到选中版本后，取 `Required` 依赖 → 用同样的
  「版本版本 + 加载器」去查它的版本 → 取最新正式版。深度上限 5 层并做 visited 去重，
  避免循环依赖。
- 安装对话框中列成清单：必选前置默认勾上、可选前置默认不勾、不可用（找不到兼容版本）
  的前置标红并提示。

### 2.6 安装

```
目标目录 = MinecraftPaths.ResolveGameDirectory(version.Id, version.VersionIsolation) + "\mods"
下载到    {目标目录}\{文件名}.part
校验      SHA1（Modrinth 必带；CF 的 hashes 里也有 sha1）
落盘      File.Move(.part -> 正式名)，同名文件已存在则先问「覆盖 / 跳过」
```

- **必须选一个目标版本**：Mod 只能落在具体游戏目录里。未开启版本隔离的版本会标注
  「共享目录」，并在安装前提示「会影响所有未隔离的版本」；已隔离的版本装进版本文件夹，
  同一版本的其他版本也会看到。
- 版本是原版（`ModLoader.Vanilla`）时给 Warning，但仍允许下载（用户可能稍后再加加载器）。
- 下载进度复用 `IProgress<DownloadProgress>` 的形状，UI 与现有启动进度条一致。
- 下载走**独立 HttpClient**：mod 文件动辄几十 MB，超时放宽到 15 分钟，
  与 API 请求（10 秒超时）分开，避免一个慢下载卡住搜索。

### 2.7 缓存与限流

| 内容 | 位置 | 有效期 |
| --- | --- | --- |
| 项目详情 | `%APPDATA%\SolarLauncher\modcache\project/{source}_{id}.json` | 24 小时 |
| 版本列表 | `%APPDATA%\SolarLauncher\modcache\versions/{source}_{id}_{ver}_{loader}.json` | 1 小时 |
| 分类/标签 | `%APPDATA%\SolarLauncher\modcache\categories_{source}.json` | 7 天 |
| 图标 | `%APPDATA%\SolarLauncher\modcache\icons/{hash}.img` | 7 天 |

- 写缓存复用现成的 `JsonFile.WriteAsync`（原子写入）。
- Modrinth 限流是 **300 次/分钟/IP**，用 `SemaphoreSlim(4)` 控制并发 + 100ms 最小间隔即可，
  不必做完整的令牌桶。
- `User-Agent` 必须能识别本应用，Modrinth 明确说通用 UA（如 `okhttp/4.9.3`）容易被封：
  统一用 `SolarLauncher/0.1 (+https://github.com/CakewthinSUm/Solar_Launcher)`。

### 2.8 图标与 WebP 的坑

Modrinth 的 `icon_url` 是 **WebP**（`..._96.webp`），只有单项目/批量项目接口的
`raw_icon_url` 才是 PNG。WinUI 的 `Image` 走 WIC 解码，**WebP 依赖系统编解码器**
（Win11 一般自带 `Microsoft.WebpImageExtension`，Win10 不一定）。

处理方式：

1. 列表优先用 `raw_icon_url`（有就直连 PNG），没有就退回 `icon_url`。
2. `Image.ImageFailed` 事件触发时换成「首字母 + 项目主色」的占位块
   （Modrinth 的 `color` 字段正好可以当主色）。
3. 如果实测 WebP 命中率太低，再考虑引入 `SixLabors.ImageSharp` 做服务端转码——
   但那是额外依赖，先不引入。

### 2.9 UI

导航层：`download-mod` 单独路由到新页面 `ModDownloadPage`，其余五个分区仍走 `DownloadPage`。
详情页 `ModProjectPage` 用 `Frame.Navigate` 打开，标题栏返回键自动生效。
（这样不必把 `DownloadViewModel` 拆成六份，也不必在同一个页面里塞两套 DataContext。）

**列表页 `ModDownloadPage`**

```
┌ 标题：Mod ─────────────────────────────────────────────┐
│ 目标版本 [1.21.4 Fabric ▾]   来源 [全部 ▾]   排序 [相关度 ▾] │
│ [🔍 搜索 Mod…                                        ]  │
│ 分类：[全部] [性能优化] [科技] [魔法] …                    │
├──────────────────────────────────────────────────────┤
│ ▢ 图标  Sodium                              下载 2.5 亿   │
│        A high-performance rendering engine…  更新 3 天前  │
│        #fabric #neoforge  [MR]                          │
│ ▢ 图标  …                                               │
│                    [ 加载更多 ]                          │
└──────────────────────────────────────────────────────┘
```

- 「目标版本」下拉同时决定默认的游戏版本与加载器筛选；切换版本立刻重查。
- 没选版本也能浏览，只是不能安装（安装按钮提示先选版本）。
- 分页用「加载更多」（offset 累加），不做页码——PCL 的累加式分页同理，但更简单。

**详情页 `ModProjectPage`**

- 头部卡片：图标、标题、作者、下载量、外链（官网/源码/问题反馈）、`安装` 主按钮。
- 正文：Modrinth 有 `body`（Markdown）→ 用一个极简 Markdown → `RichTextBlock` 转换器
  （标题/粗体/链接/列表/图片）；CurseForge 镜像拿不到正文（`/description` 返回 404），
  只显示 summary + 截图 + 「在 CurseForge 上查看」外链。
- 版本列表：默认只显示与版本版本+加载器兼容的，按发布时间倒序；
  每行显示版本名、类型徽章（正式/Beta/Alpha）、文件名、大小、发布时间、前置数量。
- 「前置资源」区块：列出选中版本的必选/可选依赖。

**安装对话框**

```
安装 Sodium 0.6.13 到「生存服」？
目标：F:\...\.minecraft\versions\<版本id>\mods
□ 前置（必选，2 项）
   ☑ Fabric API 0.115.0        [已安装，将跳过]
   ☑ Cloth Config 15.0.140
□ 可选前置（1 项）
   ☐ Mod Menu 13.0.3
[ 取消 ]                                    [ 安装 3 个文件 ]
```

---

## 三、实施计划

### 阶段 1：数据层（无 UI，可单测）

- [ ] `Solar.Core/Mods/ModModels.cs`、`ModSourceEndpoints.cs`、`ModLoaderMapping.cs`
- [ ] `ModrinthCatalog` / `CurseForgeCatalog`（JSON 映射 + 镜像改写）
- [ ] `ModCatalogService`（并行、去重、降级）
- [ ] `ModCache`（磁盘缓存 + TTL）
- [ ] `ModDependencyResolver`
- [ ] `ModInstaller`（下载、SHA1 校验、原子落盘、同名处理）
- [ ] DI 注册（`AddSolarCore` 里加 `AddHttpClient`，API 与下载分开配超时）
- [ ] `tests/Solar.Core.Tests/ModCatalogTests.cs`：facets 拼接、两源 JSON 映射、
      去重规则、文件名清洗、依赖展开去环（用假 `HttpMessageHandler`，不打真网）

### 阶段 2：搜索与列表 UI

- [ ] `ModDownloadViewModel` + `ModDownloadPage`
- [ ] `MainWindow` 里把 `download-mod` 路由到新页面
- [ ] 设置页新增「Mod 来源」分组（来源偏好、镜像地址、CurseForge Key）

### 阶段 3：详情、版本与安装

- [ ] `ModProjectViewModel` + `ModProjectPage`
- [ ] 版本筛选、安装对话框、依赖勾选、进度
- [ ] Markdown 轻量渲染转换器

### 阶段 4（后续）：已装 Mod 管理

- [ ] 扫描 `mods/` 目录，算 SHA1，用 `/v2/version_files`（Modrinth）与
      `/v1/fingerprints/432`（CurseForge，需要 MurmurHash2 实现）反查项目
- [ ] 更新检查、启用/禁用（改扩展名）、删除
- [ ] 这一步依赖「保留上游文件名」与「不重命名」的决策，所以必须在阶段 3 之后

---

## 四、风险与待确认

1. **MCIM 镜像的稳定性**：它是社区服务，可能限流或下线。方案里所有调用都走
   `ModSourceEndpoints`，换成别的镜像或直连只改一处。
2. **CurseForge 官方 API 的 Key**：不自带 Key 是硬约束（分发 Key 违反 ToS）。
   默认走镜像，用户想用官方就自己填。
3. **WebP 图标**：见 2.8，需要真机验证 Win10 下的解码率。
4. **中文搜索**：v1 不做词表，搜索框明确提示「支持英文名或原名」。
   后续若要做，最小实现是打包一份 slug→中文名的静态表，而不是复刻 PCL 的模糊匹配打分。
5. **镜像的 CurseForge 详情正文缺失**：`/v1/mods/{id}/description` 返回 404，
   所以 CF 项目的详情页只能给摘要 + 截图 + 外链。Modrinth 不受影响。

---

## 五、已验证事实（本次实测）

> 以下请求均在 2026-10-27 从本机发出，UA 为 `SolarLauncher/0.1 (test)`。

| 结论 | 证据 |
| --- | --- |
| Modrinth 搜索 + facets 可用 | `GET api.modrinth.com/v2/search?query=sodium&facets=[[project_type:mod],[categories:fabric],[versions:1.21.4]]` → `total_hits=35` |
| 无关键字纯浏览可用 | 空 `query` + facets → `total_hits=13761` |
| 多版本 OR 过滤可用 | `["versions:1.21.4","versions:1.21.5"]` → `total_hits=15231` |
| 加载器 facet 三个都有效 | forge 4373 / neoforge 9494 / quilt 4298（1.21.4） |
| Modrinth 版本接口支持服务端过滤 | `.../project/AANobbMI/version?loaders=["fabric"]&game_versions=["1.21.4"]` → 8 条，`files[0].primary=true`，带 `sha1` |
| Modrinth 依赖字段 | `dependencies[{project_id, version_id, file_name, dependency_type}]`，`dependency_type ∈ required/optional/incompatible/embedded` |
| Modrinth 限流与 UA | 官方文档：300 次/分钟/IP，必须带可识别 UA |
| CurseForge 官方 API 无 Key 不可用 | `GET api.curseforge.com/v1/mods/search?...` → **403** |
| MCIM 镜像的 CurseForge 搜索可用 | `/curseforge` + `/v1/mods` + `/search`，带 `gameId=432&classId=6&gameVersion=1.21.4&modLoaderType=4` → 正常返回 `pagination` / `data` |
| CF `modLoaderType` 编号 | 1=Forge、4=Fabric、5=Quilt、6=NeoForge（1.21.4 下分别 2847/7300/1981/8009 条） |
| CF 文件列表支持服务端过滤 | `.../mods/238222/files?gameVersion=1.21.4&modLoaderType=6` → 2 条；JEI 在该版本确实只有 NeoForge 版 |
| CF 文件列表分页 | `pageSize=50&index=50` 正常翻页；`pageSize=10000` 一次拉全量（JEI 3984 条 / 6.1 MB） |
| CF 文件字段 | `fileName`、`downloadUrl`、`hashes[]`、`releaseType`、`dependencies[{modId, relationType}]`（3=必选、2=可选） |
| CF 批量项目可用 | `POST /curseforge/v1/mods` body `{"modIds":[306612,308702]}` → Fabric API / Mod Menu |
| CF 分类可用 | `GET /curseforge/v1/categories?gameId=432&classId=6` → 52 条，含 406 世界生成、412 科技、6814 性能优化等 |
| 镜像文件代理可用 | `/data/AANobbMI/versions/c3YkZvne/sodium-...jar` → 200，1 306 799 字节（与原站一致） |
| 镜像 CF 文件代理可用 | `/files/9092/537/wick-1.0.1+mc1.21.4.jar` → 200 |
| 镜像图标代理可用 | `/avatars/862/389/638270817972208061.png` → 200 image/png |
| 原站 CDN 直连可用 | `cdn.modrinth.com`、`edge.forgecdn.net`、`media.forgecdn.net` 均 200 |
| 镜像缺字段 | `/modrinth/v2/projects` 批量接口**没有** `raw_icon_url`；`/modrinth/v2/team/{id}/members` 404；`/curseforge/v1/mods/{id}/description` 404 |
| Modrinth 图标是 WebP | `icon_url` = `..._96.webp`；`raw_icon_url` 只在官方单项目/批量接口里有，是 PNG |
| 镜像的 CurseForge 中文搜索无效 | `searchFilter=钠` → `totalCount=10000`（等于没过滤，返回随机结果） |
| Modrinth 中文搜索也很弱 | `query=钠` → `total_hits=2`，只命中名字里带中文的条目 |

---

## 六、与现有代码的接缝

| 现有组件 | 用途 |
| --- | --- |
| `MinecraftPaths.ResolveGameDirectory(id, isolated)` | 解析 Mod 的目标游戏目录（隔离时即版本文件夹） |
| `MinecraftPaths.GetVersionDirectory(id)` | 版本文件夹本身 |
| `GameVersion.MinecraftVersion` / `Loader` | 搜索与版本列表的默认筛选条件 |
| `JsonFile` / `SolarJson` | 缓存读写 |
| `AppPaths.Data` | `modcache/` 的父目录 |
| `ViewModelBase` / `PageBase` | 新页面直接继承 |
| `IDialogService` | 同名文件覆盖确认 |
| `DownloadProgress` | 下载进度上报形状 |
| `MirrorRewritingHandler` | **不复用**：它只改写 Mojang 的两个主机，Mod 源用独立的 `ModSourceEndpoints` |
