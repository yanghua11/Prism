# Prism Windows 移植 - 产品需求文档 (PRD)

## Overview
- **Summary**: 把现有 Android Unreal Engine `.pak` 浏览器与提取器 Prism 移植到 Windows 桌面平台。Windows 版应用需提供与 Android 版**完全相同的核心功能**（pak 挂载、文件浏览、搜索、AES 密钥、usmap 映射、纹理预览、原始文件与 PNG 导出），但使用原生 Windows 桌面 UI 框架替换 Android WebView UI。
- **Purpose**: 将原本仅可在 Android 设备运行的工具扩展到 Windows 桌面，方便 PC 用户直接操作 `.pak` 包（无需模拟器或移动设备），同时保留与 Android 版一致的用户体验与功能集。
- **Target Users**: 使用 Windows 10/11 的游戏 mod 开发者、内容分析人员、需要查看/提取 UE4/UE5 pak 资源的用户。

## Goals
1. 在 Windows 桌面上提供与 Android 版 Prism 功能**完全等价**的 pak 浏览与提取体验。
2. 复用现有 `PakTool.Core`（纯 .NET 业务层），做到零修改或仅需最小配置调整。
3. 使用 WinUI 3 / Windows App SDK 重写 UI 层，替代 `AndroidManifest.xml` + `MainActivity.cs` + 嵌入式 `WebView` + HTML/JS UI。
4. 支持 Oodle 压缩的 pak（通过 Windows 版 `oo2core_*.dll` 或用户提供的原生库）。
5. 构建脚本 (`dotnet build/publish`) 可在 Windows 上产生可执行的桌面应用。

## Non-Goals (Out of Scope)
- 不重写 `PakTool.Core` 与 CUE4Parse 的解析算法（仅保持引用方式不变）。
- 不引入额外的 WebView2/Chromium UI 壳（除非 Windows 原生 UI 完全采用 WebView2 承载同样的 HTML，不过首选方案仍是原生 XAML UI）。
- 不新增 `.utoc` / `.ucas` 容器支持（当前 Android 版同样不支持）。
- 不引入多语言或高级主题切换（Android 版目前只有浅色主题的 HTML UI）。
- 不修改已存在的 `PakTool.Cli`（它本身已可在 Windows 直接运行，作为可选验证工具保留）。
- 不打包分发 Oodle 原生 DLL（保持 Android 版"用户自备"的策略，遵循 Epic/RAD 许可证）。

## Background & Context
### 现有代码结构
```
/workspace
├── AndroidPakTool.slnx       # 解决方案文件（引用三个 csproj + CUE4Parse 子模块）
├── Prism/                     # Android 前端 (.NET 10-android)
│   ├── MainActivity.cs        # Android Activity + WebView 桥接 + JS UI 状态管理
│   ├── Prism.csproj
│   ├── AndroidManifest.xml
│   ├── Resources/layout/...   # 布局
│   └── Resources/values/...   # 字符串资源
├── PakTool.Core/              # 跨平台核心 (.NET 8, 纯管理代码)
│   ├── Models.cs              # DTO: PakOpenOptions, ArchiveEntryDto, 等
│   └── PakArchiveSession.cs   # 核心会话: OpenAsync/ListAsync/SearchAsync/
│                              #           ReadAssetInfoAsync/TryReadTexturePreviewAsync/
│                              #           ExportAsync/ReadRelatedRawFilesAsync
├── PakTool.Cli/               # 跨平台 CLI (.NET 8)
│   └── Program.cs             # 命令行: list/search/export/asset/loose-texture
└── external/CUE4Parse/        # Git 子模块: Unreal 解析库 + CUE4Parse-Conversion
```

### 依赖链
- `PakTool.Core` → `CUE4Parse`、`CUE4Parse-Conversion`（纹理解码 → `CTexture` → `bitmap.Encode(ETextureFormat.Png)`）
- `Prism` → `PakTool.Core`（.NET 10-android 可兼容 netstandard）
- `PakTool.Cli` → `PakTool.Core`（Windows、Linux、macOS 皆可直接 `dotnet run`）

### 关键平台差异
| 功能 | Android (当前) | Windows (目标) |
|------|----------------|----------------|
| 运行时 | .NET 10-android | .NET 8（Windows App SDK）或 .NET 8 WPF |
| 前端 UI | 嵌入式 WebView + 内联 HTML/JS/CSS | WinUI 3 XAML（或 WPF）|
| 文件选择 | `Intent.ActionOpenDocument` | `FileOpenPicker` / `OpenFileDialog` |
| 导出位置 | `Intent.ActionOpenDocumentTree` + `DocumentsContract` | 用户选择任意本地文件夹 |
| Oodle 原生库 | `liboodle-data-shared.so`（.so via `NativeLibrary.TryLoad`） | `oo2core_win64_*.dll` 等 Windows DLL |
| IO 权限 | 通过 `ContentResolver` 读写 URI | 普通 `System.IO`（文件系统路径）|
| 会话管理 | `MainActivity` 持有单例 `PakArchiveSession` | `MainWindow` 持有单例 `PakArchiveSession` |

## Functional Requirements

### FR-1: Pak 文件与 usmap 映射文件选择
- 应用启动时显示主窗口，可选择一个 `.pak` 文件（路径直接存储为本地文件路径）。
- 可选择一个可选 `.usmap` 映射文件（同样为本地路径）。
- 输入可选的 AES 密钥（16 进制字符串，自动补 `0x` 前缀；行为与 `NormalizeAesKey` 一致）。

### FR-2: 挂载与浏览 Pak
- 点击"打开"按钮后，调用 `PakArchiveSession.OpenAsync` 并等待完成。
- 在挂载完成后，调用 `PakArchiveSession.ListAsync(folder)` 展示当前目录项，并在后台线程构建目录索引（`BuildDirectoryIndexAsync`）。
- UI 提供"向上一级"按钮、路径面包屑、文件/文件夹区分显示、文件大小显示。
- 支持搜索：调用 `PakArchiveSession.SearchAsync(query)` 并显示结果列表。

### FR-3: 资产/纹理预览
- 选中一个 `.uasset` / `.umap` 文件时，调用 `PakArchiveSession.TryReadTexturePreviewAsync`。
- 返回的 `TexturePreviewDto.PngData` 在 UI 中以图像控件展示（宽/高显示在标题栏或状态栏）。
- 若无纹理数据，显示提示信息。若缺少 `usmap` 映射，显示与 Android 版一致的错误文案。

### FR-4: 导出功能
- **原始导出（Export Raw）**：调用 `PakArchiveSession.ReadRelatedRawFilesAsync` 获得 `IReadOnlyDictionary<string, byte[]>`，把每一项写入用户选择的输出目录，保留相对路径结构。
- **PNG 导出（Export PNG）**：调用 `PakArchiveSession.TryReadTexturePreviewAsync`（`maxMipSize = int.MaxValue`），将 `PngData` 写入 `<资产名>.png`。
- 支持批量/单文件的进度指示（类似 Android 版 "Exporting raw files..."）。

### FR-5: 诊断与状态面板
- 显示 Android 版 `Diagnostics` 等价的滚动日志面板（"PERF" 与 "DECODE" 通道），包括打开时的阶段计时 (`PakOpenResult.Timings`)。
- 显示 Oodle 状态（初始化成功/失败/未找到对应 DLL）。
- 提供"当前状态"栏（如 "Mounted 1 archive(s), 1234 file(s) in 1.2s."）。

### FR-6: Oodle 支持
- 在 `third_party/lib/win-x64/`（或配置的路径）存在 Oodle Windows DLL 时自动初始化（`CUE4Parse.Compression.OodleHelper.Initialize`）。
- 若不存在则继续工作，但无法读取 Oodle 压缩的条目；UI 中显示清晰的状态信息。
- 逻辑尽量保持与 Android 版 `EnsureBundledOodleInitialized` 相同的错误处理与日志输出。

### FR-7: 构建/发布
- 新增 WinUI 3 项目 `Prism.Windows/Prism.Windows.csproj`（目标框架 `net8.0-windows10.0.19041.0`）。
- 更新 `AndroidPakTool.slnx`（或新增 Windows 解决方案）引用新项目。
- `dotnet build` 与 `dotnet publish` 能在 Windows 10/11 上产生可执行应用。

## Non-Functional Requirements
- **NFR-1 性能**: 打开 pak、首次列出根目录、纹理预览的耗时不应比 Android 版在同等硬件上差 2 倍以上（以 `PakOpenResult.Timings` 对比）。
- **NFR-2 UI 响应性**: 所有耗时操作必须在后台线程执行，UI 线程不得阻塞；使用 `async/await` 和 `IProgress<T>`。
- **NFR-3 代码一致性**: `PakTool.Core` 项目应保持 **零修改或最小修改**；所有平台差异在 UI 项目内部处理。
- **NFR-4 可用性**: 应用支持浅色主题（与 Android 版当前 HTML 主题一致），提供清晰的按钮、图标与文件列表。
- **NFR-5 可维护性**: 新增 Windows 项目与现有项目使用相同的 `.editorconfig`、nullable 检查、隐式 using 风格。
- **NFR-6 许可合规**: Oodle DLL 不进仓库；采用 `Condition="Exists(...)"` 的 MSBuild 项组，让用户自备（与 Android 版一致）。

## Constraints
- **技术**: 前端使用 WinUI 3 (Windows App SDK 1.4+)；目标框架 `net8.0-windows10.0.19041.0`；运行在 Windows 10 1809+ / Windows 11。
- **业务**: 不得发布或打包 Oodle/Unreal 原生库；必须保持"用户自备"的策略。
- **依赖**: 继续以 Git 子模块方式使用 `external/CUE4Parse`，不替换为 NuGet 版本以保持解析一致性。

## Assumptions
1. 开发/运行环境为 Windows 10 1809+ 或 Windows 11，安装 Visual Studio 2022 + .NET 8 SDK + Windows App SDK 工作负载。
2. `PakTool.Core` 在 `net8.0-windows` 目标下编译无变化。
3. CUE4Parse 的 `CUE4Parse-Conversion` 在 Windows 上能正常产生 PNG（`CTexture.Encode(ETextureFormat.Png)`）。
4. Oodle 场景下，用户能自行提供 `oo2core_win64_*.dll`（与 Android 版 `.so` 对应）。

## Acceptance Criteria

### AC-1: 打开 Pak / AES 密钥 / usmap
- **Given**: 用户已安装 Windows 版应用，并且本地有合法 `.pak` 文件
- **When**: 用户选择 `.pak`，可选输入 AES 密钥与 `.usmap`，然后点击"打开"
- **Then**: 应用完成挂载；状态区显示已挂载归档数、文件数、耗时；根目录文件列表显示在主面板
- **Verification**: `programmatic`（运行 PakTool.Cli 验证相同结果；UI 手动确认）

### AC-2: 浏览与搜索
- **Given**: 已成功挂载 pak
- **When**: 用户点击文件夹条目或"向上"按钮
- **Then**: 文件列表切换到对应目录；路径栏显示当前路径（`/` 为根）
- **Verification**: `programmatic`（对比 `PakTool.Cli list --folder` 输出）

- **Given**: 已挂载 pak
- **When**: 用户在搜索框输入关键字并触发搜索
- **Then**: 显示匹配的条目列表（不区分大小写，包含路径片段匹配）
- **Verification**: `programmatic`（对比 `PakTool.Cli search` 输出）

### AC-3: 纹理预览
- **Given**: 已挂载含纹理资产的 pak（且有对应 usmap 若需要）
- **When**: 用户选中一个 `.uasset`
- **Then**: UI 显示该纹理的解码图像与尺寸；若无法解码显示可读错误
- **Verification**: `human-judgment` + 程序对比（导出 PNG 与 `PakTool.Cli loose-texture` 产出一致）

### AC-4: 原始文件导出
- **Given**: 已选中文件
- **When**: 用户点击"导出原始"并选择输出目录
- **Then**: 应用在输出目录下写入 `.uasset` / `.uexp` / `.ubulk` 等相关文件（保留相对路径/文件名），文件内容与 `PakArchiveSession.ReadRelatedRawFilesAsync` 返回一致
- **Verification**: `programmatic`（文件哈希对比）

### AC-5: PNG 导出
- **Given**: 已选中包含纹理的资产
- **When**: 用户点击"导出 PNG"并选择输出目录
- **Then**: 在目标目录写入 `<资产名>.png` 文件；文件内容与纹理预览解码一致
- **Verification**: `programmatic`（文件存在 + 大小 > 0 + 合法 PNG 头）

### AC-6: Oodle 状态与诊断面板
- **Given**: 应用启动后
- **When**: 打开一个使用 Oodle 压缩的 pak
- **Then**: 若 Oodle DLL 可用则解码顺利，否则显示状态信息与诊断日志行
- **Verification**: `human-judgment`（检查诊断日志内容）

### AC-7: 构建与发布
- **Given**: Windows 10/11 + .NET 8 SDK + Windows App SDK 工作负载
- **When**: 在仓库根执行 `dotnet build Prism.Windows/Prism.Windows.csproj` 与 `dotnet publish -c Release`
- **Then**: 构建成功并产出可运行的 Windows 可执行
- **Verification**: `programmatic`（ExitCode == 0 + 可双击启动）

## Open Questions
- [ ] UI 框架选择：**WinUI 3**（现代化，推荐）还是 **WPF**（更成熟，无需 Windows App SDK 运行时）？（本 PRD 默认选择 WinUI 3；实现任务中提供 WPF 备选方案）
- [ ] 是否要复用 Android 版的 HTML UI 到 Windows 上（通过 WebView2 承载 `BuildHtml()` 输出），从而做到 UI 零重写？（需评估 WebView2 依赖；本 PRD 的推荐方案是原生 XAML）
- [ ] 项目命名：继续使用 `Prism.Windows` 还是另起一个以避免与 Prism MVVM 库同名的混淆？
- [ ] `.slnx` 当前仅包含 Android 项目；Windows 项目是直接加入 `AndroidPakTool.slnx` 还是新增 `WindowsPakTool.sln`/`.slnx`？
