# Prism Windows 移植 - 验证清单 (checklist.md)

> 说明：以下检查项对应 PRD 中 7 个验收标准 (AC) 与 tasks.md 中 7 个任务的测试要求 (TR)。所有检查项必须在 Windows 10/11 开发机上手工或程序化验证通过，才能视为移植完成。

## 1. 项目构建 (AC-7, TR-1.1, TR-1.2, TR-7.1)
- [ ] `dotnet build Prism.Windows/Prism.Windows.csproj` Debug 构建成功（ExitCode == 0）
- [ ] `dotnet build -c Release Prism.Windows/Prism.Windows.csproj` Release 构建成功
- [ ] `dotnet publish -c Release -r win-x64 Prism.Windows/Prism.Windows.csproj` 发布成功
- [ ] 产出的 `*.exe` 在 Windows 上可双击启动（不报错）
- [ ] `PakTool.Core` 项目未发生破坏性改动（现有 Android 构建与 CLI 构建不受影响）

## 2. 打开 Pak / AES / usmap (AC-1, TR-3.1, TR-3.2)
- [ ] 可选择合法 `.pak` 文件，UI 显示文件名
- [ ] 可选择合法 `.usmap` 文件，UI 显示文件名；在已打开会话中动态加载后再次预览 asset 能正确解析
- [ ] 不输入 AES 密钥可打开非加密 pak
- [ ] 输入正确 AES 密钥可打开加密 pak（状态区显示挂载数）
- [ ] 输入非法 AES 密钥不会崩溃；错误信息写入状态区与诊断面板
- [ ] `PakTool.Cli list --pak <same>` 输出条目数量与 Windows UI 中根目录条目数量一致（允许隐藏的 `.uexp`/`.ubulk` 按 Android 策略同样被折叠到 asset 下）

## 3. 浏览与搜索 (AC-2, TR-3.3, TR-4.1)
- [ ] 点击文件夹条目进入该文件夹，路径面包屑正确更新
- [ ] "向上"按钮能正确回到父目录；在根目录按"向上"无操作或禁用
- [ ] 在至少 3 层嵌套目录间切换，UI 无卡顿，数据无误
- [ ] 搜索框输入关键字后结果数量/名称与 `PakTool.Cli search --pak <same> --query <keyword>` 前 250 条一致
- [ ] 搜索结果点击后能被正确选中（启用"导出"按钮）

## 4. 资产/纹理预览 (AC-3, TR-5.1, TR-5.2)
- [ ] 选择含纹理的 `.uasset` 时 UI 显示图像预览与宽高
- [ ] 选择不含纹理的 `.uasset` 时在状态区与预览区明确提示 "No previewable texture found."
- [ ] 缺少 usmap 的 unversioned asset 在状态区显示与 Android 版一致的错误文案
- [ ] 纹理预览图像与 `PakTool.Cli loose-texture --uasset <same>` 的输出视觉相同（或字节数近似）

## 5. 原始导出 (AC-4, TR-6.1)
- [ ] 对一个选中条目点击"导出原始"，选择输出目录；操作完成后状态区显示成功
- [ ] 导出目录中存在与该 asset 相关的 `.uasset` / `.uexp` / `.ubulk`（若存在）
- [ ] 导出的所有文件与 `PakTool.Cli export --pak <same> --entry <same> --out <cliDir>` 的同名文件做 SHA256 对比，完全一致
- [ ] 文件名/路径中的 Windows 非法字符被 `SanitizePathPart` 清理（等价 `_` 替换）

## 6. PNG 导出 (AC-5, TR-6.2)
- [ ] 对含纹理的 asset 点击"导出 PNG"，输出目录产生同名 `.png` 文件
- [ ] `.png` 文件头为合法 PNG 签名（`89 50 4E 47 0D 0A 1A 0A`）；文件大小 > 0
- [ ] 在图片查看器中可正常打开，内容与预览显示一致

## 7. 诊断与 Oodle (AC-6, TR-2.2, TR-7.2, TR-7.3)
- [ ] 应用启动后诊断面板至少有一条启动日志
- [ ] 打开 pak 的各阶段计时（`CreateProvider`、`RegisterVfs`、`LoadUsmap`、`SubmitAes`、`Mount`、`PostMount`、`MountedArchives`、`OpenTotal`）在诊断面板可读到
- [ ] 未放置 Oodle DLL 时，诊断面板与 Oodle 状态栏清楚显示 "Bundled Oodle native library is not available..."
- [ ] 放置 Oodle DLL 后打开使用 Oodle 压缩的条目，可正常解码；诊断面板显示 "Oodle native initialized from bundled native library."

## 8. 稳定性与线程安全 (NFR-2)
- [ ] 打开 pak 时 UI 保持响应（可拖动窗口；可点击 Cancel/关闭）
- [ ] 打开大 pak 过程中关闭窗口不会抛未处理异常（`CancellationTokenSource` 正确取消）
- [ ] 连续多次打开/切换 pak 无内存泄漏或 `ObjectDisposedException`
- [ ] 纹理预览在"正在解码"状态下重新选择另一 asset 不会崩溃（取消前一次 `ct`）

## 9. 许可合规 (NFR-6)
- [ ] 仓库中没有 `third_party/lib/win-x64/*.dll` 的提交（或仅存在于本地工作副本，不在 Git 中）
- [ ] 项目文件使用 `Condition="Exists(...)"` 包含 Oodle DLL；无 Oodle DLL 时项目仍可构建

## 10. 与现有 Android / CLI 输出一致性（回归测试）
- [ ] 对同一个 pak，Android 版 UI 列出的条目名称与 Windows 版 UI 列出的条目名称相同（大小写/顺序可不一致）
- [ ] 对同一个纹理 asset，Android 版导出 PNG 与 Windows 版导出 PNG 的图像内容相同（允许文件大小差异 ≤ 2%，由编码器默认参数差异导致）
- [ ] `PakTool.Cli` 仍可在 Windows/Linux 下正常构建与运行（无回归）
