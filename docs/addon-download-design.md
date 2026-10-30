# 光影 / 整合包 / 资源包 / 数据包 下载设计

本文记录在现有 Mod 下载引擎上补齐其余四类内容的设计与取舍。
PCL2（`Meloong-Git/PCL`）的做法作为参照，文末附本次实际请求过的接口。

---

## 一、五种内容类型

统一模型里的 `ModProjectType` 负责翻译两个源的标识和安装落点：

| 类型 | Modrinth `project_type` facet | CurseForge `classId` | 安装落点 |
| --- | --- | --- | --- |
| Mod | `mod` | 6 | `mods/` |
| 整合包 | `modpack` | 4471 | 新建隔离版本 |
| 资源包 | `resourcepack` | 12 | `resourcepacks/` |
| 光影 | `shader` | 6552 | `shaderpacks/` |
| 数据包 | `datapack` | 6945 | `saves/<存档>/datapacks/` |

只有 Mod 参与加载器筛选。光影、资源包、整合包的加载器字段为空或者无关，
带上 `categories:fabric` / `modLoaderType` 会让搜索结果变成空列表。

### 数据包的额外过滤

Modrinth 没有独立的 datapack 项目类型：搜索 facet `project_type:datapack`
返回的是“支持数据包”的项目，其中很多项目的 `project_type` 实际是 `mod`，
同一个项目下既有 Mod jar 版本，也有 `.zip` 数据包版本。

因此版本请求在 `Type == DataPack` 时固定追加：

```
&loaders=["datapack"]
```

它不受“显示全部版本”开关影响——否则关掉兼容过滤后，jar 版本会混进数据包列表。
CurseForge 的数据包是独立的 `classId=6945`，不需要这层过滤。

---

## 二、界面结构

五个分区共用一套页面，导航参数（`mod` / `shaders` / `modpacks` /
`resourcepacks` / `datapacks`）决定：

| 组件 | 职责 |
| --- | --- |
| `AddonDownloadPage` | 搜索、来源/排序筛选、加载更多 |
| `AddonProjectPage` | 详情正文、截图、版本列表 |
| `AddonInstallDialog` | Mod / 光影 / 资源包 / 数据包的单文件或带前置安装 |
| `ModpackInstallDialog` | 整合包：解析、起名、创建版本、整包下载 |

筛选条件的来源：

- **Mod / 光影 / 资源包 / 数据包**：顶部选中的版本提供游戏版本；Mod 额外提供加载器。
  没选版本时仍可浏览，只是不能安装。
- **整合包**：整合包自带游戏版本与加载器，安装会新建版本。列表页改为让用户
  选择要浏览的游戏版本（来自版本清单的正式版），而不是绑定已有版本。

Minecraft 分区仍由原来的 `DownloadPage` 负责，两者不混在同一个视图模型里。

---

## 三、整合包安装

支持两种格式：

### Modrinth `.mrpack`

zip 内包含 `modrinth.index.json` 与 `overrides/`：

```json
{
  "formatVersion": 1,
  "game": "minecraft",
  "versionId": "8.2.0",
  "files": [
    {
      "path": "mods/BetterGrassify-1.7.0+fabric.1.21.4.jar",
      "hashes": { "sha1": "..." },
      "env": { "client": "required", "server": "required" },
      "downloads": ["https://cdn.modrinth.com/..."],
      "fileSize": 89214
    }
  ],
  "dependencies": { "minecraft": "1.21.4", "fabric-loader": "0.19.3" }
}
```

- `env.client = "unsupported"` 的条目跳过；`optional` 仍然下载。
- 依赖键决定加载器：`fabric-loader` / `quilt-loader` / `forge` / `neoforge`。
- `overrides/` 与 `client-overrides/` 依次解压，后者覆盖前者。

### CurseForge

zip 内包含 `manifest.json`：

```json
{
  "minecraft": {
    "version": "1.12.2",
    "modLoaders": [{ "id": "forge-14.23.5.2860", "primary": true }]
  },
  "manifestType": "minecraftModpack",
  "name": "RLCraft",
  "version": "2.9.3",
  "files": [{ "projectID": 284754, "fileID": 2725062, "required": true }],
  "overrides": "overrides"
}
```

- manifest 只有 `projectID/fileID`，真正的文件名与下载地址要逐个查
  `GET /v1/mods/{projectID}/files/{fileID}`。走 MCIM 镜像不需要 API Key。
- 文件被作者关闭分发时 `downloadUrl` 为空，记进失败清单，不阻塞整包安装。
- 文件统一放进 `mods/`：manifest 不携带目标目录，社区约定如此；
  资源包/光影通常放在 overrides 里，不会走 `files` 列表。
- `overrides` 字段指定目录名（默认 `overrides`）。

### 安装流程

```
下载整包到 %TEMP%\SolarLauncher\modpacks\<guid>\
  → 解析 manifest，得到 MC 版本 / 加载器 / 文件清单
  → 创建隔离版本（写入 versions/*/.solar/version.json）
  → 并行下载文件（4 路），SHA1 校验，.part 原子落盘
  → 解压 overrides / client-overrides（zip-slip 校验）
  → 保存版本
```

游戏本体与加载器不在这里下载。版本的 `LoaderInstalled = false`，首次启动时由
`GameLaunchService.EnsureLoaderAsync` 安装加载器，并补齐游戏文件。
这样建整合包时不必同步等待几百 MB 的游戏下载，也不会同时跑两套下载器。

取消或失败时版本记录与已下载文件会保留，对话框会明确提示；
临时目录在对话框关闭时由 `IModpackService.Cleanup` 删除。

---

## 四、安全

整合包来自网络，所有路径都按不可信输入处理：

- 拒绝 `..`、空段、绝对路径、盘符（`C:`）与越界路径。
- 目标路径必须落在版本游戏目录之下（`CombineUnderRoot` 双重检查）。
- 文件先写 `.part`，SHA1 通过后才改名；失败不会留下半截可执行文件。
- 临时目录只删除自己创建的 `%TEMP%\SolarLauncher\modpacks\*`。

---

## 五、与 PCL 的取舍

| 点 | PCL | 本实现 | 理由 |
| --- | --- | --- | --- |
| 中文搜索 | 内置 MC 百科词表 | 不做，提示英文名 | 词表维护成本高，先保证英文搜索可用 |
| 版本列表 | 一次拉全量本地分组 | 服务端按版本/加载器过滤 | 请求更小，翻页语义一致 |
| Mod 依赖 | 只展示不下载 | 必选前置一并安装 | 缺前置是新手崩溃的最常见原因 |
| 整合包文件 | 按 classId 分目录 | 统一 `mods/` | CF manifest 不提供目录，绝大多数条目是 Mod |
| 数据包落点 | 按存档 | 按存档 | 原版只读 `saves/<world>/datapacks` |

---

## 六、代码位置

```
src/Solar.Core/Mods/
  ModModels.cs             ModProjectType 与两个源的映射
  ModrinthCatalog.cs       搜索 facet、版本 datapack 过滤
  CurseForgeCatalog.cs     classId、随类型变化的搜索/版本请求
  ModCatalogService.cs     跨源去重（同类型才可能合并）
  ModFileDownloader.cs     换源、SHA1、.part 原子落盘
  ModInstaller.cs          按类型写入 mods / resourcepacks / shaderpacks / saves/<world>/datapacks
src/Solar.Core/Modpacks/
  ModpackModels.cs         ModpackPlan / 进度 / 服务接口
  ModpackManifestReader.cs 两种 manifest 的纯解析 + 路径安全
  ModpackService.cs        整包下载、创建版本、文件下载、overrides 解压
src/Solar.App/
  ViewModels/AddonDownloadViewModel.cs
  ViewModels/AddonProjectViewModel.cs
  ViewModels/AddonInstallDialogViewModel.cs
  ViewModels/ModpackInstallDialogViewModel.cs
  Views/AddonDownloadPage.xaml
  Views/AddonProjectPage.xaml
  Views/AddonInstallDialog.xaml
  Views/ModpackInstallDialog.xaml
```

---

## 七、已验证事实（2026-10-28 实测）

| 结论 | 证据 |
| --- | --- |
| CurseForge classId | 6 Mod / 12 资源包 / 4471 整合包 / 6552 光影 / 6945 数据包（镜像搜索均有结果） |
| Modrinth 五种 facet 可用 | `project_type:mod/modpack/resourcepack/shader/datapack` 均返回结果 |
| 数据包 facet 会包含混合项目 | `VeinMiner` 的 `project_type` 是 `mod`，但支持 `datapack` loader |
| 数据包版本可服务端过滤 | `/version?loaders=["datapack"]` 把 152 个版本收窄到 16 个 zip 版本 |
| `.mrpack` 结构 | `formatVersion/versionId/dependencies` 为 camelCase，文件带 `env.client` |
| CF manifest 结构 | `projectID/fileID/overrides` 字段名带大写；文件需逐个查询 |
| CF 单文件接口镜像可用 | `/curseforge/v1/mods/238222/files/3043174` 返回 fileName / downloadUrl / sha1 |
| 整合包真实解析 | Fabulously Optimized 的 Modrinth 8.2.0 → Fabric 0.19.3 / 48 个文件 / 40.3 MB；CurseForge 396246 → 47 个文件，1 个作者关闭分发 |
