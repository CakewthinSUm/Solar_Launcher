<div align="center">

<img src="docs/images/icon.png" width="96" alt="Solar Launcher 图标" />

# ☀️ Solar Launcher

**A modern Minecraft launcher built with WinUI 3**

Native Windows 11 UI · Portable, no install · Instance isolation · Microsoft account login

**English** · [简体中文](README_zh-cn.md)

[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![WinUI 3](https://img.shields.io/badge/WinUI-3%20%2F%20Windows%20App%20SDK%201.8-0067C0?logo=windows&logoColor=white)](https://learn.microsoft.com/windows/apps/winui/winui3/)
[![Version](https://img.shields.io/badge/version-0.1.1--beta-2ea44f)](../../releases)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%201809%2B-0078D4?logo=windows&logoColor=white)](#-getting-started)

[Download](../../releases) · [Features](#-features) · [Screenshots](#-screenshots) · [Development](#-development) · [Roadmap](#-roadmap)

</div>

![Home](docs/images/home.png)

---

## ✨ Features

### 🎮 Instance management
Each instance can use a different Minecraft version and mod loader, independently. Existing versions in your game directory can be **imported with a single scan** — no re-download required.

Pick a version on the Download page to jump straight into creation and install the loader, OptiFine, Fabric API, and other components in one go.

| Instance list | Create instance |
| :---: | :---: |
| ![Instances](docs/images/instances.png) | ![Create instance](docs/images/instance-create.png) |

### 📦 Version isolation
Enable isolation for an instance and it gets its own `.minecraft` directory — saves, mods, and configs are fully separate, so a Fabric instance and a Forge instance can coexist without conflicts.

When enabling it, you can copy `mods` / `config` / `saves` / `resourcepacks` from the shared directory; existing files are never overwritten. Disabling isolation just switches back to the shared directory; the instance directory is left untouched.

`assets` and `libraries` are always shared and unaffected by isolation.

![Instance settings](docs/images/instance-settings.png)

### 🚀 Launch and progress
When you hit Launch, the button expands into a progress card at the bottom: the step list advances through "Resolve version → Install missing files → Install loader → Launch game", and the download stage shows the current file, completed count, and overall progress. Failures are shown directly in the card with retry or cancel — no modal dialog hiding the details. If you have no instances, the card tells you to create one first.

Download and launch progress are mirrored in the taskbar progress bar, and the taskbar flashes when the game starts. Once the game window actually appears, the launcher follows Settings → After game launch: do nothing (default), minimize, or close. If the game process exits early, the launcher stays visible and shows the error.

### ⚙️ Settings
The Settings page manages the game directory, the default isolation for new instances, the Java path, download sources, and the after-launch behavior. If you leave with unsaved changes, it asks Save / Don't save / Cancel; Reset at the bottom restores all defaults (applied after saving).

### ⬇️ Downloads
The version list comes from Mojang's official manifest. Search by version string and filter by release type (release / snapshot / pre-release / old beta / old alpha) and release year; select one to create an instance.

![Downloads](docs/images/download.png)

The other five tabs under Downloads search Modrinth and CurseForge and share the same detail page and install dialog:

| Tab | Search filter | Install location |
| --- | --- | --- |
| **Mod** | Instance game version + loader | Instance `mods/`, required dependencies installed too |
| **Shaders** | Instance game version | Instance `shaderpacks/` |
| **Modpacks** | User-selected game version | **Creates a new isolated instance** and downloads the whole pack |
| **Resource packs** | Instance game version | Instance `resourcepacks/` |
| **Data packs** | Instance game version | `saves/<world>/datapacks/` of the selected world |

The instance selected at the top determines both version and loader filters, so every result can be installed directly. You can browse without selecting an instance; the install button will just prompt you to create one. Modpacks are the exception — they carry their own Minecraft version and loader, create a separate instance on install, and install the game and loader on first launch.

The detail page shows the description, screenshots, and all historical versions. Data packs also require choosing a world in the install dialog; if there are no worlds, it tells you to create one in-game first.

| Mod list | Mod detail |
| :---: | :---: |
| ![Mods](docs/images/mod-download.png) | ![Mod detail](docs/images/mod-detail.png) |

### 🧩 Mod loaders
Supports **Fabric / Quilt / Forge / NeoForge**, plus add-ons such as **OptiFine**, **LiteLoader**, **Fabric API**, and **OptiFabric**.

| Loader | Install method |
| --- | --- |
| Fabric / Quilt | Metadata API returns a profile JSON with `inheritsFrom` |
| Forge / NeoForge | Download the installer jar, extract `version.json`, run client processors |
| OptiFine | Parse the installer and generate a launchwrapper profile |
| LiteLoader | Parse the installer and inject the tweakClass |

Forge / NeoForge installers are not simple archives: they run processors (renaming the vanilla jar, applying binary patches) to produce a usable client artifact. Those processors run under Java, and their arguments mix three placeholder forms — `{KEY}` data table references, `[group:artifact:version@ext]` inline Maven coordinates, and `'literal'` single-quoted strings — all handled in `LoaderInstaller`.

Installation happens **on the instance's first launch**, not at creation — the installer can download hundreds of MB, and blocking instance creation on it is a bad experience.

### 🗂️ Content management
Under "Manage", the launcher reads and writes the instance's actual game directory:

| Sub-item | Contents |
| --- | --- |
| **Instances** | Create / delete instances, open per-instance settings |
| **Mods** | Jars in the `mods` directory |
| **Resource packs** | Zips or extracted folders in `resourcepacks` |
| **Shaders** | Zips or extracted folders in `shaderpacks` |

No extra index — drop files in manually and refresh. You can import files / folders, reveal in Explorer, and delete from the page.

![Mods](docs/images/mods.png)

### 🧠 Memory allocation
**Automatic mode** computes the heap limit from physical memory, currently available memory, and the instance's mod count, re-evaluating on every launch and giving less when many background apps are running. You can also switch to **manual mode** and use a slider.

### 🔐 Microsoft account login
Microsoft accounts only; offline login is not supported.

Login uses the OAuth **device code flow**: click "Sign in" and the launcher shows a one-time code, copies it to the clipboard, and opens `microsoft.com/link` in your default browser — paste and confirm, no embedded browser needed. Multiple accounts and silent token refresh are supported.

The token chain (OAuth → Xbox Live → XSTS → Minecraft Services) is handled by
[CmlLib.Core.Auth.Microsoft](https://github.com/CmlLib/CmlLib.Core.Auth.Microsoft).
`%APPDATA%\SolarLauncher\accounts.json` stores only display data such as username, UUID, and XUID, **no tokens**;
tokens live in `%APPDATA%\SolarLauncher\msa-accounts.json`.

> **Client ID is built in**: the launcher ships its own Azure application ID, so login needs no configuration.
> See [Account configuration](#-account-configuration) below.

### 🚀 Download engine and mirrors
Dependency downloads use [CmlLib.Core](https://github.com/CmlLib/CmlLib.Core)'s `ParallelGameInstaller`:
it splits validation and downloading into two TPL Dataflow stages with independent parallelism, built-in SHA1 verification, and backpressure.
Version resolution, argument assembly, and process launch are all implemented in `Solar.Core`.

Mirrors rewrite URLs at the HTTP layer via `MirrorRewritingHandler`: only `libraries.minecraft.net` (→ `/maven/`)
and `resources.download.minecraft.net` (→ `/assets/`) are rewritten; paths such as `piston-data` stay direct
because mirrors support them incompletely.

Mod sources default to the **MCIM mirror** (`mod.mcimirror.top`), which proxies both Modrinth and CurseForge —
the latter needs no API key, the only practical way to use CurseForge from mainland China.

---

## 🖼️ Screenshots

| Settings | Accounts |
| :---: | :---: |
| ![Settings](docs/images/settings.png) | ![Accounts](docs/images/accounts.png) |

> The UI follows WinUI 3 conventions: `TitleBar` for the title bar, `NavigationView` for navigation,
> all colors from `ThemeResource` (adapts to light / dark / high contrast), and `MicaBackdrop` for the window material.
> Screenshots use the dark theme.

---

## 🧱 Tech stack

| Layer | Technology |
| --- | --- |
| UI | WinUI 3 / Windows App SDK 1.8, unpackaged portable |
| Architecture | MVVM (CommunityToolkit.Mvvm) + constructor dependency injection |
| Core | .NET 8 class library `Solar.Core`, fully decoupled from the UI |
| Downloads | CmlLib.Core `ParallelGameInstaller` (download part only) |
| Content downloads | Modrinth / CurseForge (via MCIM mirror); mods / shaders / modpacks / resource packs / data packs |
| Modpacks | Parse `.mrpack` / CurseForge manifests, create an isolated instance and download the pack |
| Markdown | A minimal parser for detail pages (headings / bold / lists / links) |
| Login | CmlLib.Core.Auth.Microsoft (OAuth device code + MSAL.NET) |
| Tests | xUnit (`tests/Solar.Core.Tests`), 390 tests |

```
src/
  Solar.Core/            Core layer, no UI dependencies
    Paths/               AppPaths: central directory management; game files in the exe directory,
                         settings/accounts in %APPDATA%\SolarLauncher
    Json/                Shared JSON options, atomic writes, argument array converter
    Models/              Mojang version JSON models, rules evaluation
    Minecraft/           Version manifest download and cache, version JSON parsing and inheritsFrom merging
    Instances/           Game instance model, persistence, version scanning, creation flow
    Content/             Mods / resource packs / shaders in the instance game directory
    Mods/                Online content catalog (Modrinth / CurseForge), dependency resolution, file download and install
    Modpacks/            Modpack parsing (mrpack / CurseForge manifest) and full-pack install
    Loaders/             Fabric / Quilt / Forge / NeoForge installation
    Addons/              OptiFine / LiteLoader / Fabric API / OptiFabric
    Download/            Dependency downloads and mirror rewriting
    Launch/              Launch argument assembly and game process launch
    Memory/              Automatic memory allocation
    Accounts/            Microsoft login and account storage
    Java/                Java detection and version selection
    Settings/            Global settings
  Solar.App/             WinUI 3 UI layer
    Services/            Navigation and dialog services
    ViewModels/          Page view models
    Views/               Pages (PageBase handles DataContext and loading)
    Converters/          XAML value converters
tests/
  Solar.Core.Tests/      Core unit tests
tools/                   End-to-end probes (see below)
```

---

## 🚀 Getting started

### Use a release

Download the latest build from [Releases](../../releases), extract it anywhere, and double-click `SolarLauncher.exe`.

No installer: game files live next to the exe, while **settings, accounts and logs live in `%APPDATA%\SolarLauncher`**,
so a second copy of the launcher still finds the same login. Delete the exe folder to uninstall,
and `%APPDATA%\SolarLauncher` to leave nothing behind.
The Windows App SDK is bundled; the **.NET 8 Desktop Runtime** is still required.

> Ready to use: the Azure client ID needed for Microsoft login is built in, so no setup is required. See [Account configuration](#-account-configuration).

### Build from source

Requirements: **.NET 8 SDK**, Windows 10 1809 or later.

```bash
git clone https://github.com/CakewthinSUm/Solar_Launcher.git
cd Solar_Launcher
dotnet build SolarLauncher.sln
dotnet run --project src/Solar.App
```

---

## 📁 Directory layout

Data lives in two places:

```
%APPDATA%\SolarLauncher\   Settings, accounts, logs, version manifest and mod caches
                           (settings.json / accounts.json / msa-accounts.json / instances.json / logs)
<exe directory>\
SolarShared/               Shared resources: versions / libraries / assets / runtime
SolarInstances/            One directory per instance, containing natives, instance.json
                           and the isolated .minecraft/ when version isolation is enabled
.minecraft/                Shared game directory: saves, mods, configs (default; configurable in Settings)
```

A legacy `SolarData/` next to the exe is moved to `%APPDATA%\SolarLauncher` on first launch,
so the login survives the upgrade; existing data at the destination is never overwritten.

The shared game directory (`.minecraft`) is used by all instances by default, so switching instances never loses saves.
Point it at the official launcher's `.minecraft` to reuse existing mods and saves.
Natives for different versions are always isolated per instance under `SolarInstances/<id>/natives`.

---

## ⚙️ Account configuration

The client ID belongs to the **application**, not the user: Solar Launcher ships its own Azure
application ID in `AuthOptions.DefaultClientId`. Two things are worth noting:

- It is public information, not a secret; shipping it in the client is Microsoft's recommended approach.
- It determines whether Minecraft services accept the app, so it must be the ID registered by
  **this launcher itself**; do not borrow another launcher's or a third party's ID (that violates Microsoft's terms).

Regular users **do not need to configure anything**: open the launcher → Accounts → Sign in.

To use your own registered app (for a fork or secondary development):

1. Register an app in the [Azure portal](https://portal.azure.com)
2. Choose "Mobile and desktop applications" and enable "Allow public client flows"
3. Set the redirect URI to `http://localhost`
4. Submit for review at [aka.ms/mce-reviewappid](https://aka.ms/mce-reviewappid)
   (otherwise Minecraft API calls return 403)
5. Put the client ID into `AuthOptions.DefaultClientId`
   (`src/Solar.Core/Accounts/MicrosoftAuthService.cs`)

The client ID is not exposed as a setting and users never need to enter it.

---

## 🆕 Changelog

### 0.1.1-beta

- The launch button expands into a bottom progress card: staged steps, current file and overall progress, cancel and retry
- With no instances, the launch hint moves into the card with an expand / collapse animation
- Download and launch progress mirrored in the taskbar, flashing when the game starts
- New "After game launch" setting: do nothing / minimize / close the launcher, executed once the game window actually appears
- Leaving Settings with unsaved changes now prompts to save; Reset button added
- Fixed Microsoft login doing nothing (device-code callback updated UI off the UI thread); the code is now copied to the clipboard and the verification page opened automatically
- Download page filters by release type and release year
- Added online downloads for shaders / modpacks / resource packs / data packs and the Mod download UI
- Built-in Azure client ID; no manual login configuration

---

## 🛠️ Development

```bash
# Build and test
dotnet build SolarLauncher.sln
dotnet test tests/Solar.Core.Tests/Solar.Core.Tests.csproj

# Run
dotnet run --project src/Solar.App
```

### End-to-end probes

Each probe under `tools/` verifies one real path without the UI:

```bash
# Download all dependencies for a real version (no login needed)
dotnet run --project tools/Solar.Smoke -- 1.21.4 %TEMP%\solar-smoke --prepare-only

# Download and launch; success if still running after 60 seconds (login required)
dotnet run --project tools/Solar.Smoke -- 1.21.4 %TEMP%\solar-smoke

# Verify the full Microsoft login flow: device code → Xbox → XSTS → Minecraft → silent refresh
dotnet run --project tools/Solar.AuthProbe -- <your-client-id>

# Verify loader installation (lists choices and picks the latest if no version is given)
dotnet run --project tools/Solar.LoaderProbe -- fabric 1.21.4
dotnet run --project tools/Solar.LoaderProbe -- forge 1.21.4 54.1.22

# Verify an installed loader version can actually launch (synthetic account, no login)
dotnet run --project tools/Solar.LaunchProbe -- fabric-loader-0.20.1-1.21.4
dotnet run --project tools/Solar.LaunchProbe -- 1.21.4-forge-54.1.22

# Verify the online content data layer (search / details / dependency plan / real install)
dotnet run --project tools/Solar.ModProbe -- search sodium fabric 1.21.4
dotnet run --project tools/Solar.ModProbe -- search complementary 1.21.4 --type=shader
dotnet run --project tools/Solar.ModProbe -- detail modrinth AANobbMI fabric 1.21.4
dotnet run --project tools/Solar.ModProbe -- detail modrinth OhduvhIc 1.21.4 --type=datapack
dotnet run --project tools/Solar.ModProbe -- plan modrinth PtjYWJkn fabric 1.21.4
dotnet run --project tools/Solar.ModProbe -- install curseforge 394468 fabric 1.21.4

# Download and parse a modpack, print its MC version, loader, and file list (no install)
dotnet run --project tools/Solar.ModProbe -- modpack modrinth 1KVo5zza 1.21.4
dotnet run --project tools/Solar.ModProbe -- modpack curseforge 396246 1.21.4
```

Add `SOLAR_PROBE_VERBOSE=1` when debugging network issues to see why each source switch happened.

---

## 🗺️ Roadmap

- [x] Project skeleton, DI, MVVM, navigation
- [x] Instance create / delete / list, persisted to disk
- [x] Version manifest fetch and local cache (switchable to a China mirror in Settings)
- [x] Version JSON download, `inheritsFrom` merging, platform rules evaluation
- [x] Automatic Java detection and version matching
- [x] Dependency (libraries / assets / natives) download with SHA1 verification
- [x] Launch argument assembly and game process launch
- [x] Launch progress card (staged steps, download details, cancel and retry)
- [x] Taskbar download progress and completion flash
- [x] Download mirror (BMCLAPI) and per-instance Java / memory overrides
- [x] Microsoft account login (device code flow, multiple accounts, silent token refresh)
- [x] Mod loader installation (Fabric / Quilt / Forge / NeoForge)
- [x] OptiFine / LiteLoader / Fabric API / OptiFabric add-ons
- [x] Per-instance version isolation (separate game directory, optional copy of shared content)
- [x] Mods / resource packs / shaders list, import, and delete under "Manage"
- [x] Memory allocation: automatic from physical / available memory and mod count, or manual slider
- [x] Game directory version scan and one-click import
- [x] Pick a version on the Download page → install loader and add-ons in one go
- [x] Online content data layer (Modrinth + CurseForge, cross-source dedup, dependency resolution, SHA1-verified install)
- [x] Content download UI (mods / shaders / modpacks / resource packs / data packs share search, details, and install dialog)
- [x] Online shader and resource pack downloads (filtered by instance game version, installed into the right directory)
- [x] Online data pack downloads (installed into `saves/<world>/datapacks/`)
- [x] Online modpack download and install (parse `.mrpack` / CurseForge manifest, create an isolated instance, download all files and extract overrides)
- [x] Download page version filters (release type + release year)
- [x] After-launch window behavior (keep / minimize / close), Settings unsaved-changes prompt and reset
- [ ] Installed content management and update checks (reverse-lookup by SHA1, enable/disable, update)
- [ ] Launcher self-update (check GitHub Releases, download and apply)
- [ ] Network diagnostics (download source speed test)
- [ ] Export an instance as a modpack

### Content download design

The data layer lives in `Solar.Core/Mods/` and unifies both sources behind one model, so the UI does not branch per content type.
`ModProjectType` maps a type to each source's identifier and install location:

| Type | Modrinth `project_type` | CurseForge `classId` | Install location |
| --- | --- | --- | --- |
| Mod | `mod` | 6 | `mods/` |
| Modpack | `modpack` | 4471 | New isolated instance |
| Resource pack | `resourcepack` | 12 | `resourcepacks/` |
| Shader | `shader` | 6552 | `shaderpacks/` |
| Data pack | `datapack` | 6945 | `saves/<world>/datapacks/` |

| Capability | Description |
| --- | --- |
| Sources | Modrinth and CurseForge, MCIM mirror by default |
| Fallback | Both sources queried in parallel; if one fails, results from the other are still returned and the failed source is marked |
| Dedup | Same type + same name + overlapping loaders + update time within 7 days = same project; keep the CurseForge entry |
| Dependencies | Only mods recursively expand required dependencies (depth limit 5, cycle detection); optional dependencies are shown only |
| Install | Writes to the instance's actual directory, keeps upstream file names, SHA1-verified atomic write |

Two type-specific gotchas:

- **Data packs** have no separate project type on Modrinth; the same project often has both mod jar versions
  and `.zip` data pack versions. Version requests add `loaders=["datapack"]`, otherwise jars leak into the list.
- **Shaders / resource packs / modpacks** do not participate in loader filtering. Passing `categories:fabric` or
  `modLoaderType` returns nothing.

Modpacks live in `Solar.Core/Modpacks/`. Installation downloads the pack, parses the manifest, then creates a new isolated instance:

| Format | Parsing |
| --- | --- |
| Modrinth `.mrpack` | Read `modrinth.index.json`, skip entries with `env.client = "unsupported"` |
| CurseForge | Read `manifest.json`, look up each `projectID/fileID` download URL (the mirror needs no key) |

Files are downloaded by the relative paths in the manifest with SHA1 verification; `overrides/` and `client-overrides/`
are extracted into the instance directory (client overrides win when both exist). Paths are zip-slip checked,
rejecting `../`, absolute paths, and drive letters. The game itself and the loader are **not downloaded during install** —
the instance's first launch runs the `GameLaunchService` catch-up flow, avoiding two downloaders in one operation.

Trade-offs versus PCL: **no** Chinese keyword search (English/original names only); version lists filter server-side by
`gameVersion` + `modLoaderType` instead of fetching everything; required mod dependencies are installed together;
CurseForge modpack files all go into `mods/` (the manifest has no target directory; this is the community convention).
See [docs/mod-download-design.md](docs/mod-download-design.md) and
[docs/addon-download-design.md](docs/addon-download-design.md).

The UI lives under Downloads → Mods / Shaders / Modpacks / Resource packs / Data packs: pick a target instance
at the top (modpacks pick a game version instead), search, click a row for details, choose a version and install.
Data packs choose a world in the dialog; modpacks ask for an instance name and show the file count / size to download.
Content sources (official / mirror / CurseForge key) are configured in Settings → Download sources; changing them clears the old cache.

---

## 📄 Notice

This project is not affiliated with Mojang Studios or Microsoft. Minecraft is a trademark of Mojang Studios.
Use a legitimate account and follow the [Minecraft End User License Agreement](https://www.minecraft.net/eula).
