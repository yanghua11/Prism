# Prism Windows 移植 - 实施计划 (tasks.md)

> 说明：本计划将移植工作拆分为 7 个有序任务。各任务间存在依赖关系，必须按顺序执行。每个任务附带具体的验收标准（AC）与测试要求。

## 依赖图
```
Task 1 (项目骨架)
   └── Task 2 (状态机 & 诊断日志)
          └── Task 3 (Pak 打开 & 目录浏览)
                 └── Task 4 (搜索 & 资产选择)
                        └── Task 5 (纹理预览)
                               └── Task 6 (导出功能)
                                      └── Task 7 (Oodle & 构建优化)
```

---

## [ ] Task 1: 新建 WinUI 3 项目骨架与基本布局
- **Priority**: P0
- **Depends On**: 无（首个任务）
- **Description**:
  - 在仓库根新建 `Prism.Windows/` 目录与 `Prism.Windows.csproj`：
    - TargetFramework：`net8.0-windows10.0.19041.0`
    - 引用 `..\PakTool.Core\PakTool.Core.csproj`
    - 使用 Windows App SDK / WinUI 3；`<UseWinUI>true</UseWinUI>`、`<WindowsAppSDKVersion>1.4.*</WindowsAppSDKVersion>`（或按最新稳定版）
    - 保留与现有项目一致的 `Nullable/ImplicitUsings/editorconfig` 风格
  - 新建 `App.xaml` / `App.xaml.cs`（`Application` 入口）
  - 新建 `MainWindow.xaml` / `MainWindow.xaml.cs`，实现基本布局（对应 Android 版 HTML UI）：
    - **顶部**: 状态/进度栏（显示"Select a .pak file to begin."之类文案 + 进度条）
    - **中部**:
      - 路径选择行：选择 `.pak` 按钮 + 显示已选文件名
      - usmap 选择 + 显示已选文件名
      - AES 密钥输入框
      - "打开" 按钮
    - **文件浏览区**:
      - 面包屑路径 + "向上"按钮
      - 搜索框 + 搜索按钮
      - 条目列表（`ListView` / `DataGrid` 形式，每行显示类型图标、名称、大小）
    - **选中/预览区**:
      - 当前选中条目摘要（`"Game/Content/Textures/Stone.uasset (123,456 bytes / 3 raw files)"`）
      - 图像控件 (`Image`) 用于纹理预览
      - "导出原始" 与 "导出 PNG" 两个按钮
    - **底部**:
      - 诊断日志滚动文本框（或 `ItemsRepeater`）
      - Oodle 状态文案
  - 在 `AndroidPakTool.slnx` 中新增一行项目引用（或新建 `WindowsPakTool.sln`）以便在 Windows 下 `dotnet build` 能直接构建新项目。
- **Acceptance Criteria Addressed**: AC-7（基础构建）
- **Test Requirements**:
  - `programmatic` TR-1.1: 在 Windows 开发机运行 `dotnet build Prism.Windows/Prism.Windows.csproj` 成功（ExitCode == 0）。
  - `programmatic` TR-1.2: `dotnet build -c Release Prism.Windows/Prism.Windows.csproj` 成功。
  - `human-judgment` TR-1.3: 手工 `F5` 启动，能看到上述 UI 元素；按钮/输入框/列表按布局占位显示。
- **Notes**: 本阶段不调用 `PakArchiveSession`；仅验证项目骨架与 UI 布局。

---

## [ ] Task 2: PakArchiveSession 封装、状态机与诊断面板基础
- **Priority**: P0
- **Depends On**: Task 1
- **Description**:
  - 在 `MainWindow` 类中新增字段：
    - `PakTool.Core.PakArchiveSession _session`（延迟初始化；`DisposeAsync`/窗口关闭时释放）
    - `string _pakPath`、`string? _usmapPath`、`string _currentFolder = string.Empty`
    - `IReadOnlyList<ArchiveEntryDto> _entries = []`
    - `ArchiveEntryDto? _selectedEntry`
    - `List<string> _diagnostics = new()`（带锁；容量上限 200 行）
    - `string _oodleStatus = "Oodle native not checked."`
    - `bool _busy`、`string _status = "Select a .pak file to begin."`
  - 实现与 Android 版 `LogPerf` / `LogDecode` 等价的 `AddDiagnostic(channel, message)`，追加到底部诊断面板（带时间戳）。
  - 实现 `SetStatus(text)`、`SetBusy(bool busy, string? status = null)`，更新 UI（必须在 UI 线程，使用 `DispatcherQueue`）。
  - 新增一个静态方法 `FormatBytes(long)` 用于显示文件大小（与 Android 版 `FormatSize` 一致）。
- **Acceptance Criteria Addressed**: AC-6（诊断面板基础）、AC-7（状态与诊断链路联通）
- **Test Requirements**:
  - `programmatic` TR-2.1: 新建会话后无 `ObjectDisposedException`；窗口关闭时 `_session` 被释放。
  - `human-judgment` TR-2.2: 启动应用在底部诊断面板看到至少一条启动日志；点击（占位）按钮时状态栏能切换文字。

---

## [ ] Task 3: Pak 文件选择 / usmap / AES 密钥 / 打开与目录浏览
- **Priority**: P0
- **Depends On**: Task 2
- **Description**:
  - 使用 `FileOpenPicker`（或 WinUI 3 的 `PickSingleFileAsync` 包装）实现：
    - 选择 `.pak`：过滤 `"*.pak"`；保存到 `_pakPath`，UI 显示 `Path.GetFileName(_pakPath)`。
    - 选择 `.usmap`：过滤 `"*.usmap"`；保存到 `_usmapPath`，并在已打开会话上调用 `_session.LoadUsmapAsync(_usmapPath)`（如有）。
  - AES 密钥输入：`TextBox`，允许空（与 Android `NormalizeAesKey` 行为一致，自动加 `0x` 前缀）。
  - "打开"按钮处理：
    - `SetBusy(true, "Opening pak...")`
    - `var options = new PakOpenOptions(new[] { _pakPath }, aesKeyHex, _usmapPath, DecodeLogger: LogDecode)`
    - `var result = await Task.Run(() => _session.OpenAsync(options, ct))`
    - 打印阶段计时：遍历 `result.Timings` 以 `PERF: {name}={ms}ms` 形式写入诊断面板
    - `SetStatus($"Mounted {result.MountedArchiveCount} archive(s), {result.FileCount} file(s) in ...")`
    - 进入根目录：`await NavigateToAsync(string.Empty)`
    - 后台构建目录索引：`await Task.Run(() => _session.BuildDirectoryIndexAsync(ct))`（注意 `CancellationTokenSource` 在窗口关闭/重新打开时取消）
  - 目录浏览：
    - `NavigateToAsync(string folder)` → 调用 `_session.ListAsync(folder)` → 更新 `_entries` → 刷新 `ListView`（使用 `ObservableCollection<EntryViewModel>` 或等价方案）
    - 每个条目显示：类型（文件夹/资产/文件）图标、名称、大小（`FormatBytes`），是否加密、是否为 asset。
    - 点击文件夹条目触发 `NavigateToAsync(entry.FullPath)`。
    - "向上"按钮：若 `_currentFolder` 非空则取 `Path.GetDirectoryName`（`_currentFolder.TrimEnd('/')` → `LastIndexOf('/')`）后跳转到父目录。
- **Acceptance Criteria Addressed**: AC-1、AC-2
- **Test Requirements**:
  - `programmatic` TR-3.1: 打开一个已知的 pak 后，调用 `PakTool.Cli list --pak ...` 与 UI 中展示的条目数量/名称一致（可在调试输出中比对）。
  - `programmatic` TR-3.2: AES 密钥空/合法/非法三种输入行为正常（非法应抛给状态区与诊断面板）。
  - `human-judgment` TR-3.3: 手动打开、浏览 3 层嵌套目录，验证"向上"按钮与面包屑正确。

---

## [ ] Task 4: 搜索与资产选择
- **Priority**: P1
- **Depends On**: Task 3
- **Description**:
  - 搜索框 + 搜索按钮：
    - 用户输入 query 后调用 `var results = await _session.SearchAsync(query.Trim(), limit: 500)`
    - 用结果替换当前 `_entries`；状态栏更新 `"N results."`
    - 搜索结果点击：若是 asset 触发"选中"逻辑（见下）；若是文件夹：继续进入该文件夹的条目（注意搜索返回的是文件列表，不会出现真正的"文件夹"条目，但 `FullPath` 可能需要解析父路径 —— 与 Android 版保持一致的行为）。
  - 选择逻辑：
    - 点击文件条目 → `_selectedEntry = entry`；清空预览图像与标题；状态栏更新选中摘要。
    - 点击 `.uasset` / `.umap` → 触发纹理预览（下一个 Task 实现，此 Task 中先实现"选择与禁用导出按钮启用"）。
    - "导出原始"按钮在选中文件时启用，"导出 PNG" 按钮在选中 asset 时启用（与 Android `canExportRaw`/`canExportPng` 对应）。
- **Acceptance Criteria Addressed**: AC-2（搜索）
- **Test Requirements**:
  - `programmatic` TR-4.1: `PakTool.Cli search --pak <x> --query <keyword>` 的前 250 条结果与 UI 展示的结果名称集合一致（顺序可以不同）。
  - `human-judgment` TR-4.2: 选择 asset 后 `Export Raw` / `Export PNG` 按钮正确启用/禁用。

---

## [ ] Task 5: 纹理预览
- **Priority**: P1
- **Depends On**: Task 4
- **Description**:
  - 选择 `.uasset`/`.umap` 后：
    - `SetBusy(true, "Loading texture preview...")`
    - `var preview = await Task.Run(() => _session.TryReadTexturePreviewAsync(_selectedEntry.FullPath, cancellationToken: ct))`
    - 若 `preview == null`：显示 `"No previewable texture found."`（状态栏 + 诊断面板）
    - 若抛 `InvalidOperationException`（缺失映射）：显示 `"This asset uses unversioned properties. Import the matching .usmap mapping file, then open or preview it again."`
    - 若成功：把 `preview.PngData` 写入 `MemoryStream`，再用 WinUI `BitmapImage`（或 `WriteableBitmap`）设置给 `Image` 控件；标题更新为 `$"{preview.TextureName} ({preview.Width}x{preview.Height})"`
    - `SetBusy(false)`
  - 诊断日志：记录每一步"开始/完成/失败"，与 Android 版一致。
- **Acceptance Criteria Addressed**: AC-3
- **Test Requirements**:
  - `programmatic` TR-5.1: 对一个已知纹理的 `.uasset`，在 Windows 与 Android 上分别导出 PNG（下一 Task 也会验证）；两者文件大小应在合理误差（相同图像内容）。
  - `human-judgment` TR-5.2: 在 UI 中看到清晰的图像预览；缺失映射时显示可读错误。

---

## [ ] Task 6: 原始导出与 PNG 导出
- **Priority**: P1
- **Depends On**: Task 5
- **Description**:
  - **原始导出** (`Export Raw`):
    - 若 `_selectedEntry` 为 null → 状态错误提示；
    - 让用户选择输出目录（`FolderPicker` / `PickFolderAsync`）；
    - `SetBusy(true, "Exporting raw files...")`；
    - `var files = await Task.Run(() => _session.ReadRelatedRawFilesAsync(_selectedEntry.FullPath, ct))`；
    - 对每个 `(path, data)`：
      - 计算输出路径：`Path.Combine(outputDir, Path.GetFileName(path))`（更完整方案：保留 `path` 相对路径 —— 与 Android `BuildOutputPath` / `SanitizePathPart` 对齐）
      - `Directory.CreateDirectory(...)`
      - `await File.WriteAllBytesAsync(outputPath, data, ct)`
    - 状态更新 `"Exported N file(s)."`
  - **PNG 导出** (`Export PNG`):
    - 若 `_selectedEntry` 为 null 或不是 asset → 提示；
    - 让用户选择输出目录；
    - `SetBusy(true, "Decoding PNG...")`；
    - `var preview = await Task.Run(() => _session.TryReadTexturePreviewAsync(_selectedEntry.FullPath, maxMipSize: int.MaxValue, ct))`；
    - `SetBusy(true, "Encoding PNG...")`；
    - `var fileName = Path.GetFileNameWithoutExtension(_selectedEntry.Name) + ".png"`；
    - `await File.WriteAllBytesAsync(Path.Combine(outputDir, fileName), preview!.PngData, ct)`；
    - 状态更新 `"Exported fileName (WxH)."`
  - 所有 IO 异常写入诊断面板，并在最后更新状态 `"Export failed: ..."`。
- **Acceptance Criteria Addressed**: AC-4、AC-5
- **Test Requirements**:
  - `programmatic` TR-6.1: 导出原始文件 → `PakTool.Cli export --pak <same> --entry <same> --out <cliDir>` 与 Windows UI 导出的输出做 SHA256 对比，所有文件一致。
  - `programmatic` TR-6.2: PNG 导出的文件头 `89 50 4E 47 0D 0A 1A 0A` 合法；文件大小 > 0。
  - `human-judgment` TR-6.3: 手工测试"无 usmap 时的错误提示"与"无条目选中时的错误提示"都能被用户清晰理解。

---

## [ ] Task 7: Oodle 原生库初始化 & 构建脚本与打包优化
- **Priority**: P2
- **Depends On**: Task 6
- **Description**:
  - 在 `App.xaml.cs` 的 `OnLaunched` / 或 `MainWindow` 构造中加入 Windows 版 `EnsureBundledOodleInitialized(reason)`：
    - 扫描 `..\third_party\lib\win-x64\` 下的 `oo2core_*.dll` / `liboodle-data-shared.dll`（与 Android 版的查找顺序近似，但文件名为 Windows DLL）。
    - 使用 `NativeLibrary.TryLoad(path, out handle)` 或直接调用 `CUE4Parse.Compression.OodleHelper.Initialize(new OodleDotNet.Oodle(path))`。
    - 将结果写入 `_oodleStatus` 与诊断面板；失败记录 `warn`。
    - `csproj` 中以 `<Content Include="..\third_party\lib\win-x64\*.dll" CopyToOutputDirectory="PreserveNewest" Condition="Exists('..\third_party\lib\win-x64\')" />` 的方式仅在存在时复制（与 Android `AndroidNativeLibrary Condition=Exists` 一致）。
  - README.md（可选）增加 Windows 构建小节：`dotnet build Prism.Windows/Prism.Windows.csproj` 与如何放置 Oodle DLL 的说明。
  - `dotnet publish` 产出可分发的 Windows 应用（`-c Release -r win-x64 --self-contained false` 或 `true` 按需求）。
- **Acceptance Criteria Addressed**: AC-6、AC-7
- **Test Requirements**:
  - `programmatic` TR-7.1: `dotnet publish -c Release Prism.Windows/Prism.Windows.csproj` 成功（ExitCode == 0）。
  - `human-judgment` TR-7.2: 放置 Oodle DLL 后打开一个 Oodle 压缩 pak，能正常解析；不放置时 UI 中清楚提示"Oodle native not available..."。
  - `human-judgment` TR-7.3: 在全新 Windows 虚拟机中安装 + 运行一次，验证不缺少运行时。

---

## 备选方案：WPF 替代 WinUI 3
若团队不希望依赖 Windows App SDK，可将 **Task 1 改为**：
- 目标框架：`net8.0-windows`
- 项目 SDK：`Microsoft.NET.Sdk`（或 WPF 专用 SDK）
- UI 用 `Window`、`Grid`、`ListView`、`Image`、`TextBox`、`Button` 等 WPF 控件
- 其他 Task 2-7 的业务逻辑完全相同（`PakArchiveSession` 调用部分无差异）

## 里程碑
| 里程碑 | 包含任务 | 预期交付物 |
|--------|----------|-----------|
| M1 骨架可构建 | Task 1 | `dotnet build` 成功；空壳 UI 可启动 |
| M2 可打开 pak | Task 2-3 | 能打开 pak 并浏览根目录 |
| M3 可预览纹理 | Task 4-5 | 能搜索、选择 asset 并预览 PNG |
| M4 可导出 | Task 6 | 能导出原始文件与 PNG |
| M5 可发布 | Task 7 | Oodle + publish 成功；可分发给用户 |
