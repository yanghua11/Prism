using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.WindowsAPICodePack.Dialogs;
using PakTool.Core;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System;

namespace Prism.Windows;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddDiagnostic("INFO", "Application started.");

        SelectPakButton.Click += OnSelectPakClick;
        SelectUsmapButton.Click += OnSelectUsmapClick;
        OpenPakButton.Click += OnOpenPakClick;
        GoUpButton.Click += OnGoUpClick;
        SearchButton.Click += OnSearchClick;
        FileListView.SelectionChanged += OnFileSelectionChanged;
        ExportRawButton.Click += OnExportRawClick;
        ExportPngButton.Click += OnExportPngClick;

        Closed += OnWindowClosed;
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        try
        {
            _cts?.Cancel();
            _session?.DisposeAsync().AsTask().Wait(500);
        }
        catch
        {
            // 忽略关闭时的异常
        }
    }

    // ---------- UI helpers ----------

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "Unknown";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private void RunOnUI(Action action)
    {
        var dq = DispatcherQueue;
        if (dq == null)
        {
            action();
            return;
        }
        dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            try { action(); }
            catch (Exception ex) { Debug.WriteLine($"[UI] {ex.Message}"); }
        });
    }

    private void AddDiagnostic(string channel, string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var line = $"[{timestamp}] [{channel}] {message}";
        RunOnUI(() =>
        {
            DiagnosticsLog.Text += line + Environment.NewLine;
        });
        Debug.WriteLine(line);
    }

    private void SetStatus(string text)
    {
        RunOnUI(() => StatusText.Text = text);
    }

    private void SetBusy(bool busy, string? status = null)
    {
        RunOnUI(() =>
        {
            ProgressBar.IsIndeterminate = busy;
            ProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (status != null)
            {
                StatusText.Text = status;
            }
        });
    }

    // ---------- File selection ----------

    private void OnSelectPakClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new CommonOpenFileDialog
        {
            Title = "Select Pak File",
            IsFolderPicker = false,
            Multiselect = false
        };
        dialog.Filters.Add(new CommonFileDialogFilter("Pak Files", "*.pak"));
        dialog.Filters.Add(new CommonFileDialogFilter("All Files", "*.*"));

        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            _pakPath = dialog.FileName;
            RunOnUI(() =>
            {
                PakPathTextBox.Text = Path.GetFileName(_pakPath);
                OpenPakButton.IsEnabled = !string.IsNullOrEmpty(_pakPath);
            });
            AddDiagnostic("INFO", $"Selected pak: {_pakPath}");
        }
    }

    private void OnSelectUsmapClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new CommonOpenFileDialog
        {
            Title = "Select Usmap File",
            IsFolderPicker = false,
            Multiselect = false
        };
        dialog.Filters.Add(new CommonFileDialogFilter("Usmap Files", "*.usmap"));
        dialog.Filters.Add(new CommonFileDialogFilter("All Files", "*.*"));

        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            _usmapPath = dialog.FileName;
            RunOnUI(() => UsmapPathTextBox.Text = Path.GetFileName(_usmapPath));
            AddDiagnostic("INFO", $"Selected usmap: {_usmapPath}");
        }
    }

    // ---------- Open pak ----------

    private async void OnOpenPakClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pakPath))
        {
            SetStatus("Please select a .pak file first.");
            return;
        }

        try
        {
            SetBusy(true, "Opening pak...");
            AddDiagnostic("INFO", "Starting pak open process...");

            await DisposeSessionAsync();

            _session = new PakArchiveSession();
            var aesKey = NormalizeAesKey(AesKeyTextBox.Text);
            var options = new PakOpenOptions(
                PakPaths: new[] { _pakPath },
                AesKeyHex: aesKey,
                UsmapPath: _usmapPath,
                DecodeLogger: LogDecode
            );

            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            var result = await Task.Run(
                () => _session.OpenAsync(options, _cts.Token),
                _cts.Token);

            foreach (var timing in result.Timings)
            {
                AddDiagnostic("PERF", $"{timing.Name}={timing.Milliseconds}ms");
            }

            SetStatus($"Mounted {result.MountedArchiveCount} archive(s), {result.FileCount} file(s).");
            AddDiagnostic("INFO", $"Mounted {result.MountedArchiveCount} archive(s).");

            await NavigateToAsync(string.Empty);
            AddDiagnostic("INFO", "Ready for browsing.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Operation cancelled.");
            AddDiagnostic("INFO", "Open cancelled by user.");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
            AddDiagnostic("ERROR", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---------- Navigation ----------

    private async Task NavigateToAsync(string folder)
    {
        if (_session == null) return;

        try
        {
            SetBusy(true, "Loading directory...");
            _currentFolder = folder;

            var entries = await Task.Run(
                () => _session.ListAsync(folder),
                _cts?.Token ?? CancellationToken.None);
            _entries = entries;

            RunOnUI(() =>
            {
                PathBreadcrumb.Text = string.IsNullOrEmpty(folder) ? "/" : folder;
                GoUpButton.IsEnabled = !string.IsNullOrEmpty(folder);
                FileListView.ItemsSource = CreateEntryViewModels(entries);
                SelectedItemSummary.Text = "No item selected.";
                TexturePreviewImage.Source = null;
                ExportRawButton.IsEnabled = false;
                ExportPngButton.IsEnabled = false;
            });

            AddDiagnostic("INFO", $"Listed {entries.Count} entries in '{folder}'.");
        }
        catch (Exception ex)
        {
            AddDiagnostic("ERROR", $"Failed to list directory: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnGoUpClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFolder)) return;
        var parent = GetParentPath(_currentFolder);
        await NavigateToAsync(parent);
    }

    // ---------- Search ----------

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        if (_session == null)
        {
            SetStatus("Please open a pak file first.");
            return;
        }

        var query = SearchTextBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            await NavigateToAsync(_currentFolder);
            return;
        }

        try
        {
            SetBusy(true, "Searching...");
            AddDiagnostic("INFO", $"Searching for: {query}");

            var results = await Task.Run(
                () => _session.SearchAsync(query, 500),
                _cts?.Token ?? CancellationToken.None);

            RunOnUI(() =>
            {
                FileListView.ItemsSource = CreateEntryViewModels(results);
                PathBreadcrumb.Text = $"Search: {query} ({results.Count} results)";
                GoUpButton.IsEnabled = false;
            });

            AddDiagnostic("INFO", $"Found {results.Count} results.");
            SetStatus($"{results.Count} results for '{query}'.");
        }
        catch (Exception ex)
        {
            AddDiagnostic("ERROR", $"Search failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---------- Selection ----------

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileListView.SelectedItem is EntryViewModel selected)
        {
            var entry = selected.Entry;
            RunOnUI(() =>
            {
                SelectedItemSummary.Text = entry.IsDirectory
                    ? $"{entry.Name}/"
                    : $"{entry.FullPath} ({FormatBytes(entry.Size)}{(entry.IsEncrypted ? " [encrypted]" : "")})";

                ExportRawButton.IsEnabled = !entry.IsDirectory;
                ExportPngButton.IsEnabled = !entry.IsDirectory && (entry.IsAssetPackage || IsTextureExtension(entry.Extension));
            });
            _selectedEntry = entry;
        }
    }

    // ---------- Export ----------

    private async void OnExportRawClick(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry == null || _session == null) return;

        using var dialog = new CommonOpenFileDialog
        {
            Title = "Select Output Directory",
            IsFolderPicker = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != CommonFileDialogResult.Ok) return;
        var outputDir = dialog.FileName;

        try
        {
            SetBusy(true, "Exporting raw files...");
            AddDiagnostic("INFO", $"Exporting raw files for: {_selectedEntry.FullPath}");

            var files = await Task.Run(
                () => _session.ReadRelatedRawFilesAsync(_selectedEntry.FullPath, _cts?.Token ?? CancellationToken.None),
                _cts?.Token ?? CancellationToken.None);

            int count = 0;
            foreach (var (path, data) in files)
            {
                var fileName = Path.GetFileName(path);
                var outputPath = Path.Combine(outputDir, fileName);
                await File.WriteAllBytesAsync(outputPath, data);
                count++;
            }

            SetStatus($"Exported {count} file(s).");
            AddDiagnostic("INFO", $"Exported {count} files to: {outputDir}");
        }
        catch (Exception ex)
        {
            SetStatus($"Export failed: {ex.Message}");
            AddDiagnostic("ERROR", $"Export failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnExportPngClick(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry == null || _session == null) return;

        using var dialog = new CommonOpenFileDialog
        {
            Title = "Select Output Directory",
            IsFolderPicker = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != CommonFileDialogResult.Ok) return;
        var outputDir = dialog.FileName;

        try
        {
            SetBusy(true, "Decoding PNG...");
            AddDiagnostic("INFO", $"Exporting PNG for: {_selectedEntry.FullPath}");

            var preview = await Task.Run(
                () => _session.TryReadTexturePreviewAsync(_selectedEntry.FullPath, int.MaxValue, _cts?.Token ?? CancellationToken.None),
                _cts?.Token ?? CancellationToken.None);

            if (preview == null)
            {
                SetStatus("No previewable texture found.");
                AddDiagnostic("WARN", "No previewable texture found.");
                return;
            }

            var fileName = Path.GetFileNameWithoutExtension(_selectedEntry.Name) + ".png";
            var outputPath = Path.Combine(outputDir, fileName);
            await File.WriteAllBytesAsync(outputPath, preview.PngData);

            SetStatus($"Exported {fileName} ({preview.Width}x{preview.Height}).");
            AddDiagnostic("INFO", $"Exported PNG: {outputPath}");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("unversioned"))
        {
            SetStatus("This asset uses unversioned properties. Import the matching .usmap mapping file.");
            AddDiagnostic("ERROR", ex.Message);
        }
        catch (Exception ex)
        {
            SetStatus($"Export failed: {ex.Message}");
            AddDiagnostic("ERROR", $"Export failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---------- Helpers ----------

    private void LogDecode(string message)
    {
        AddDiagnostic("DECODE", message);
    }

    private static string? NormalizeAesKey(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var key = input.Trim();
        if (!key.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            key = "0x" + key;
        }
        return key;
    }

    private static string GetParentPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash < 0 ? string.Empty : trimmed[..(lastSlash + 1)];
    }

    private static bool IsTextureExtension(string extension)
    {
        var ext = (extension ?? string.Empty).ToLowerInvariant();
        return ext is ".uasset" or ".umap" or ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga";
    }

    private static ObservableCollection<EntryViewModel> CreateEntryViewModels(IReadOnlyList<ArchiveEntryDto> entries)
    {
        var models = new ObservableCollection<EntryViewModel>();
        foreach (var entry in entries)
        {
            models.Add(new EntryViewModel(entry));
        }
        return models;
    }

    private async Task DisposeSessionAsync()
    {
        if (_session != null)
        {
            await _session.DisposeAsync();
            _session = null;
            AddDiagnostic("INFO", "Previous session disposed.");
        }
    }

    // ---------- State ----------

    private CancellationTokenSource? _cts;
    private string? _pakPath;
    private string? _usmapPath;
    private string _currentFolder = string.Empty;
    private IReadOnlyList<ArchiveEntryDto> _entries = [];
    private ArchiveEntryDto? _selectedEntry;
    private PakArchiveSession? _session;
}

public sealed class EntryViewModel
{
    public EntryViewModel(ArchiveEntryDto entry)
    {
        Entry = entry;
    }

    public ArchiveEntryDto Entry { get; }

    public string Icon => Entry.IsDirectory ? "Folder" : GetFileIcon(Entry.Extension);
    public string DisplayName => Entry.Name + (Entry.IsDirectory ? "/" : "");
    public string SizeDisplay => Entry.IsDirectory ? "" : FormatBytesStatic(Entry.Size);

    private static string GetFileIcon(string extension)
    {
        var ext = (extension ?? string.Empty).ToLowerInvariant();
        return ext switch
        {
            ".uasset" or ".umap" => "Asset",
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" => "Image",
            ".wav" or ".ogg" or ".mp3" => "Audio",
            ".u" or ".uc" => "Script",
            ".ini" or ".cfg" => "Config",
            _ => "File"
        };
    }

    private static string FormatBytesStatic(long bytes)
    {
        if (bytes < 0) return "Unknown";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
