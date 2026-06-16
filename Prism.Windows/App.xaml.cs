using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CUE4Parse.Compression;

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
        var libDir = Path.Combine("..", "third_party", "lib", "win-x64");
        if (!Directory.Exists(libDir))
        {
            Debug.WriteLine("[Oodle] Bundled Oodle library directory does not exist.");
            return;
        }

        string[] dllNames =
        [
            "oo2core_win64_9_2.dll",
            "oo2core_win64_9_1.dll",
            "oo2core_win64_9_0.dll",
            "oo2core_win64_8_0.dll",
            "oo2core_win64_7_0.dll",
            "oo2core_win64_6_0.dll",
            "liboodle-data-shared.dll",
        ];

        foreach (var dllName in dllNames)
        {
            var dllPath = Path.Combine(libDir, dllName);
            if (File.Exists(dllPath))
            {
                try
                {
                    var handle = NativeLibrary.TryLoad(dllPath, out var libHandle);
                    if (handle)
                    {
                        Debug.WriteLine($"[Oodle] Native library loaded from: {dllPath}");
                        OodleHelper.Initialize(new OodleDotNet.Oodle(libHandle));
                        Debug.WriteLine("[Oodle] Oodle native initialized from bundled native library.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Oodle] Failed to load {dllName}: {ex.Message}");
                }
            }
        }

        Debug.WriteLine("[Oodle] Bundled Oodle native library is not available. Oodle-compressed entries will not be readable.");
    }

    private Window? m_window;
}
