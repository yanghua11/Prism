using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.WindowsAPICodePack.Dialogs;
using PakTool.Core;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

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
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "Unknown";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private void AddDiagnostic(string channel, string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var line = $"[{timestamp}] [{channel}] {message}";
        DispatcherQueue.TryEnqueue(() =>
        {
            DiagnosticsLog.Text = DiagnosticsLog.Text + line + Environment.NewLine;
        });
        Debug.WriteLine(line);
    }

    private void SetStatus(string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = text;
        });
    }

    private void SetBusy(bool busy, string? status = null)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (busy)
            {
                ProgressBar.IsIndeterminate = true;
                ProgressBar.Visibility = Visibility.Visible;
            }
            else
            {
                ProgressBar.Visibility = Visibility.Collapsed;
            }

            if (status != null)
            {
                StatusText.Text = status;
            }
        });
    }

    private async void OnSelectPakClick(object sender, RoutedEventArgs e)
    {
        var dialog = new CommonOpenFileDialog
        {
            Title = "Select Pak File",
            IsFolderPicker = false,
            Multiselect = false,
            Filters = { new CommonFileDialogFilter("Pak Files", "*.pak") }
        };

        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            _pakPath = dialog.FileName;
            PakPathTextBox.Text = System.IO.Path.GetFileName(_pakPath);
            OpenPakButton.IsEnabled = !string.IsNullOrEmpty(_pakPath);
            AddDiagnostic("INFO", $"Selected pak: {_pakPath}");
        }
    }

    private async void OnSelectUsmapClick(object sender, RoutedEventArgs e)
    {
        var dialog = new CommonOpenFileDialog
        {
            Title = "Select Usmap File",
            IsFolderPicker = false,
            Multiselect = false,
            Filters = { new CommonFileDialogFilter("Usmap Files", "*.usmap") }
        };

        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            _usmapPath = dialog.FileName;
            UsmapPathTextBox.Text = System.IO.Path.GetFileName(_usmapPath);
            AddDiagnostic("INFO", $"Selected usmap: {_usmapPath}");
        }
    }

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
                new[] { _pakPath },
                aesKey,
                _usmapPath,
                DecodeLogger: LogDecode
            );

            var cts = new CancellationTokenSource();
            _cts?.Dispose();
            _cts = cts;

            PakOpenResult? result = null;
            result = await Task.Run(() => _session.OpenAsync(options, cts.Token), cts.Token);

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

    private async Task NavigateToAsync(string folder)
    {
        if (_session == null) return;

        try
        {
            SetBusy(true, "Loading directory...");
            _currentFolder = folder;

            var entries = await Task.Run(() => _session.ListAsync(folder), _cts?.Token ?? CancellationToken.None);
            _entries = entries;

            await DispatcherQueue.EnqueueAsync(() =>
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

            var results = await Task.Run(() => _session.SearchAsync(query, 500), _cts?.Token ?? CancellationToken.None);

            await DispatcherQueue.EnqueueAsync(() =>
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

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileListView.SelectedItem is EntryViewModel selected)
        {
            var entry = selected.Entry;
            SelectedItemSummary.Text = entry.IsDirectory
                ? $"{entry.Name}/"
                : $"{entry.FullPath} ({FormatBytes(entry.Size)}{(entry.IsEncrypted ? " [encrypted]" : "")})";

            ExportRawButton.IsEnabled = !entry.IsDirectory;
            ExportPngButton.IsEnabled = !entry.IsDirectory && (entry.IsAssetPackage || IsTextureExtension(entry.Extension));

            _selectedEntry = entry;
            TexturePreviewImage.Source = null;
        }
    }

    private async void OnExportRawClick(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry == null || _session == null) return;

        var dialog = new CommonOpenFileDialog
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
                var fileName = System.IO.Path.GetFileName(path);
                var outputPath = System.IO.Path.Combine(outputDir, fileName);
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

        var dialog = new CommonOpenFileDialog
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

            var fileName = System.IO.Path.GetFileNameWithoutExtension(_selectedEntry.Name) + ".png";
            var outputPath = System.IO.Path.Combine(outputDir, fileName);
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
        var ext = extension.ToLowerInvariant();
        return ext is ".uasset" or ".umap" or ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga";
    }

    private ObservableCollection<EntryViewModel> CreateEntryViewModels(IReadOnlyList<ArchiveEntryDto> entries)
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

    public string Icon => Entry.IsDirectory ? "📁" : GetFileIcon(Entry.Extension);
    public string DisplayName => Entry.Name + (Entry.IsDirectory ? "/" : "");
    public string SizeDisplay => Entry.IsDirectory ? "" : FormatBytesStatic(Entry.Size);

    private static string GetFileIcon(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".uasset" or ".umap" => "🎨",
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" => "🖼️",
            ".wav" or ".ogg" or ".mp3" => "🎵",
            ".u" or ".uc" => "📜",
            ".ini" or ".cfg" => "⚙️",
            _ => "📄"
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
