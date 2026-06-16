using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Prism.Windows;

public sealed partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        EnsureBundledOodleInitialized();

        m_window = new MainWindow();
        m_window.Activate();
    }

    private static void EnsureBundledOodleInitialized()
    {
        // 按照 spec.md 中的描述，扫描第三方目录中的 Oodle DLL。
        // 注意：这是一个 "best effort" 的初始化。没有 DLL 也不会阻止应用启动，
        // 只是使用 Oodle 压缩的 pak 条目无法读取。
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "third_party", "lib", "win-x64"),
            Path.Combine(Environment.CurrentDirectory, "third_party", "lib", "win-x64"),
            Path.Combine("..", "third_party", "lib", "win-x64"),
        };

        string[] dllNames =
        {
            "oo2core_win64_9_2.dll",
            "oo2core_win64_9_1.dll",
            "oo2core_win64_9_0.dll",
            "oo2core_win64_8_0.dll",
            "oo2core_win64_7_0.dll",
            "oo2core_win64_6_0.dll",
            "oo2core_win64.dll",
            "liboodle-data-shared.dll",
        };

        foreach (var dir in candidates)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var dllName in dllNames)
            {
                var dllPath = Path.Combine(dir, dllName);
                if (!File.Exists(dllPath)) continue;

                try
                {
                    if (NativeLibrary.TryLoad(dllPath, out var handle))
                    {
                        // 尝试初始化 Oodle（PakTool.Core / CUE4Parse 内部使用）
                        try
                        {
                            CUE4Parse.Compression.OodleHelper.Initialize(new OodleDotNet.Oodle(handle));
                            Debug.WriteLine($"[Oodle] Initialized from: {dllPath}");
                            m_oodleStatus = $"Loaded from {Path.GetFileName(dllPath)}";
                            return;
                        }
                        catch (Exception inner)
                        {
                            Debug.WriteLine($"[Oodle] Loaded DLL but failed to wire: {inner.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Oodle] Failed to load {dllPath}: {ex.Message}");
                }
            }
        }

        m_oodleStatus = "Oodle native library not found — Oodle-compressed entries cannot be read.";
        Debug.WriteLine($"[Oodle] {m_oodleStatus}");
    }

    private static string m_oodleStatus = "Not checked.";
    private Window? m_window;
}
