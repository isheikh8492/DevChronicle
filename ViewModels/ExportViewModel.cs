using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevChronicle.Services;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace DevChronicle.ViewModels;

public enum ExportMode
{
    Hub = 0,
    NewTarget = 1,
    UpdateDiary = 2
}

/// <summary>
/// ViewModel for Phase C: Export controls.
/// </summary>
public partial class ExportViewModel : ObservableObject
{
    private readonly ExportService _exportService;
    private readonly DatabaseService _databaseService;
    private readonly SettingsService _settingsService;
    private bool _isUpdatingSelectAll;
    private CancellationTokenSource? _cancellationTokenSource;

    public bool HasDiaryPath => !string.IsNullOrWhiteSpace(DiaryPath);
    public bool HasDiffPreview => CurrentDiff != null;
    public int SelectedSessionCount => Sessions.Count(s => s.IsSelected);
    public bool CanConvertToManaged => IsDiaryUnmanaged && SelectedSessionCount > 0 && !IsBusy;
    public string OutputDirectoryDisplay => string.IsNullOrWhiteSpace(OutputDirectory) ? "(not set)" : OutputDirectory;

    [ObservableProperty]
    private ExportMode mode = ExportMode.Hub;

    [ObservableProperty]
    private string status = "Ready";

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private ObservableCollection<ExportSessionItemViewModel> sessions = new();

    [ObservableProperty]
    private bool selectAll;

    [ObservableProperty]
    private string outputDirectory = string.Empty;

    [ObservableProperty]
    private bool exportDiary = true;

    [ObservableProperty]
    private bool exportArchive = true;

    [ObservableProperty]
    private ExportFormat selectedFormat = ExportFormat.MarkdownAndJson;

    [ObservableProperty]
    private bool hideRepoPathsInMarkdown = true;

    [ObservableProperty]
    private bool includePlaceholders = true;

    [ObservableProperty]
    private string diaryFileName = string.Empty;

    [ObservableProperty]
    private string? diaryPath;

    [ObservableProperty]
    private bool isDiaryManaged;

    [ObservableProperty]
    private bool isDiaryUnmanaged;

    [ObservableProperty]
    private DiaryDiff? currentDiff;

    [ObservableProperty]
    private ObservableCollection<int> boundSessionIds = new();

    public ExportViewModel(
        ExportService exportService,
        DatabaseService databaseService,
        SettingsService settingsService)
    {
        _exportService = exportService;
        _databaseService = databaseService;
        _settingsService = settingsService;

        _ = LoadSessionsAsync();
        _ = LoadExportSettingsAsync();
    }

    partial void OnSelectAllChanged(bool value)
    {
        if (_isUpdatingSelectAll)
            return;

        _isUpdatingSelectAll = true;
        foreach (var session in Sessions)
        {
            session.IsSelected = value;
        }
        _isUpdatingSelectAll = false;

        OnPropertyChanged(nameof(SelectedSessionCount));
        OnPropertyChanged(nameof(CanConvertToManaged));
    }

    partial void OnDiaryPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasDiaryPath));
    }

    partial void OnCurrentDiffChanged(DiaryDiff? value)
    {
        OnPropertyChanged(nameof(HasDiffPreview));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConvertToManaged));
    }

    partial void OnIsDiaryUnmanagedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConvertToManaged));
    }

    partial void OnOutputDirectoryChanged(string value)
    {
        OnPropertyChanged(nameof(OutputDirectoryDisplay));
    }

    [RelayCommand]
    private async Task LoadSessionsAsync()
    {
        IsLoading = true;
        try
        {
            var sessionsList = await _databaseService.GetAllSessionsAsync();

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Sessions.Clear();
                foreach (var session in sessionsList)
                {
                    var item = new ExportSessionItemViewModel(session);
                    item.SelectionChanged += OnSessionSelectionChanged;
                    Sessions.Add(item);
                }
            });
        }
        catch (Exception ex)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                System.Windows.MessageBox.Show(
                    $"Failed to load sessions: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            });
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void OnSessionSelectionChanged(object? sender, EventArgs e)
    {
        if (_isUpdatingSelectAll)
            return;

        var allSelected = Sessions.Count > 0 && Sessions.All(s => s.IsSelected);
        if (SelectAll != allSelected)
        {
            _isUpdatingSelectAll = true;
            SelectAll = allSelected;
            _isUpdatingSelectAll = false;
        }

        OnPropertyChanged(nameof(SelectedSessionCount));
        OnPropertyChanged(nameof(CanConvertToManaged));
    }

    [RelayCommand]
    private void GoToHub()
    {
        Mode = ExportMode.Hub;
        CurrentDiff = null;
        BoundSessionIds.Clear();
        DiaryPath = null;
        IsDiaryManaged = false;
        IsDiaryUnmanaged = false;
    }

    [RelayCommand]
    private void GoToNewTarget()
    {
        Mode = ExportMode.NewTarget;
    }

    [RelayCommand]
    private void GoToUpdateDiary()
    {
        Mode = ExportMode.UpdateDiary;
        CurrentDiff = null;
        BoundSessionIds.Clear();
        DiaryPath = null;
        IsDiaryManaged = false;
        IsDiaryUnmanaged = false;
    }

    [RelayCommand]
    private void Cancel()
    {
        _cancellationTokenSource?.Cancel();
    }

    [RelayCommand]
    private async Task ChooseOutputDirectoryAsync()
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Select export output folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        OutputDirectory = dialog.SelectedPath;
        await _databaseService.UpsertAppSettingAsync("export.last_dir", OutputDirectory);
    }

    [RelayCommand]
    private async Task ExportNewAsync()
    {
        if (IsBusy)
            return;

        var selectedIds = Sessions.Where(s => s.IsSelected).Select(s => s.Id).ToList();
        if (selectedIds.Count == 0)
        {
            Status = "Select at least one session.";
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            Status = "Choose an output directory.";
            return;
        }

        var effectiveFormat = (ExportDiary, ExportArchive) switch
        {
            (true, true) => ExportFormat.MarkdownAndJson,
            (true, false) => ExportFormat.MarkdownOnly,
            (false, true) => ExportFormat.JsonOnly,
            _ => ExportFormat.MarkdownAndJson
        };

        SelectedFormat = effectiveFormat;

        IsBusy = true;
        Status = "Exporting...";
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            var effectiveDiaryFileName = ExportDiary
                ? BuildSafeDiaryFileName(DiaryFileName)
                : null;

            var result = await _exportService.ExportAsync(new ExportRequest
            {
                SessionIds = selectedIds,
                ExportDiary = ExportDiary,
                ExportArchive = ExportArchive,
                Format = SelectedFormat,
                OutputDirectory = OutputDirectory,
                DiaryFileName = effectiveDiaryFileName,
                HideRepoPathsInMarkdown = HideRepoPathsInMarkdown,
                IncludePlaceholders = IncludePlaceholders,
                CancellationToken = _cancellationTokenSource.Token
            });

            if (result.Canceled)
            {
                Status = "Canceled.";
                return;
            }

            if (!result.Succeeded)
            {
                Status = $"Export failed: {result.ErrorMessage}";
                return;
            }

            var files = result.FilesWritten.Select(Path.GetFileName).ToList();
            Status = files.Count > 0
                ? $"Exported: {string.Join(", ", files)}"
                : "Export completed.";
        }
        catch (OperationCanceledException)
        {
            Status = "Canceled.";
        }
        catch (Exception ex)
        {
            Status = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    [RelayCommand]
    private async Task BrowseDiaryAsync()
    {
        if (IsBusy)
            return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Markdown files (*.md)|*.md|All files (*.*)|*.*",
            Title = "Select an existing Developer Diary"
        };

        if (dialog.ShowDialog() != true)
            return;

        DiaryPath = dialog.FileName;
        await ComputeDiaryDiffAsync();
    }

    [RelayCommand]
    private async Task ComputeDiaryDiffAsync()
    {
        CurrentDiff = null;
        BoundSessionIds.Clear();
        IsDiaryManaged = false;
        IsDiaryUnmanaged = false;

        if (string.IsNullOrWhiteSpace(DiaryPath))
            return;

        IsBusy = true;
        Status = "Inspecting diary...";
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            var preview = await _exportService.PreviewDiaryUpdateAsync(new UpdateDiaryRequest
            {
                DiaryPath = DiaryPath,
                CancellationToken = _cancellationTokenSource.Token
            });

            if (preview.Result.Canceled)
            {
                Status = "Canceled.";
                return;
            }

            if (!preview.Result.Succeeded)
            {
                IsDiaryUnmanaged = true;
                Status = preview.Result.ErrorMessage ?? "Diary is unmanaged.";
                return;
            }

            IsDiaryManaged = true;
            CurrentDiff = preview.Diff;
            foreach (var id in preview.BoundSessionIds)
                BoundSessionIds.Add(id);

            Status = preview.Diff.IsStale ? "Out of date." : "Up to date.";
        }
        catch (OperationCanceledException)
        {
            Status = "Canceled.";
        }
        catch (Exception ex)
        {
            Status = $"Failed to inspect diary: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    [RelayCommand]
    private async Task ApplyDiaryUpdateAsync()
    {
        if (IsBusy)
            return;

        if (string.IsNullOrWhiteSpace(DiaryPath))
        {
            Status = "Choose a diary file first.";
            return;
        }

        IsBusy = true;
        Status = "Updating diary...";
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            var (diff, result) = await _exportService.UpdateDiaryAsync(new UpdateDiaryRequest
            {
                DiaryPath = DiaryPath,
                CancellationToken = _cancellationTokenSource.Token
            });

            if (result.Canceled)
            {
                Status = "Canceled.";
                return;
            }

            if (!result.Succeeded)
            {
                Status = $"Update failed: {result.ErrorMessage}";
                return;
            }

            CurrentDiff = diff;
            Status = "Diary updated.";
        }
        catch (OperationCanceledException)
        {
            Status = "Canceled.";
        }
        catch (Exception ex)
        {
            Status = $"Update failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    [RelayCommand]
    private async Task ConvertToManagedAsync()
    {
        if (IsBusy)
            return;

        if (string.IsNullOrWhiteSpace(DiaryPath))
        {
            Status = "Choose a diary file first.";
            return;
        }

        var selectedIds = Sessions.Where(s => s.IsSelected).Select(s => s.Id).ToList();
        if (selectedIds.Count == 0)
        {
            Status = "Select sessions to bind to the managed diary, then convert.";
            return;
        }

        IsBusy = true;
        Status = "Converting to managed diary...";
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            var output = await _exportService.ConvertUnmanagedDiaryToManagedAsync(
                inputDiaryPath: DiaryPath,
                sessionIds: selectedIds,
                hideRepoPaths: HideRepoPathsInMarkdown,
                includePlaceholders: IncludePlaceholders,
                cancellationToken: _cancellationTokenSource.Token);

            DiaryPath = output;
            Status = "Converted. Inspecting...";
            await ComputeDiaryDiffAsync();
        }
        catch (OperationCanceledException)
        {
            Status = "Canceled.";
        }
        catch (Exception ex)
        {
            Status = $"Convert failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    [RelayCommand]
    private async Task ExportSingleDiaryAsync(ExportSessionItemViewModel? session)
    {
        if (session == null || IsBusy)
            return;

        await ExportSingleSessionAsync(session, exportDiary: true, exportArchive: false);
    }

    [RelayCommand]
    private async Task ExportSingleArchiveAsync(ExportSessionItemViewModel? session)
    {
        if (session == null || IsBusy)
            return;

        await ExportSingleSessionAsync(session, exportDiary: false, exportArchive: true);
    }

    [RelayCommand]
    private async Task ExportSingleBothAsync(ExportSessionItemViewModel? session)
    {
        if (session == null || IsBusy)
            return;

        await ExportSingleSessionAsync(session, exportDiary: true, exportArchive: true);
    }

    private async Task ExportSingleSessionAsync(ExportSessionItemViewModel session, bool exportDiary, bool exportArchive)
    {
        if (string.IsNullOrWhiteSpace(OutputDirectory) || !Directory.Exists(OutputDirectory))
        {
            await ChooseOutputDirectoryAsync();
            if (string.IsNullOrWhiteSpace(OutputDirectory) || !Directory.Exists(OutputDirectory))
            {
                Status = "Choose an output directory first.";
                return;
            }
        }

        var perSessionDir = Path.Combine(OutputDirectory, "PerSession");
        Directory.CreateDirectory(perSessionDir);

        var dateStamp = DateTime.Now.ToString("yyyy-MM-dd");
        var diaryFileName = $"Diary.Session_{session.Id}.{dateStamp}.md";
        var archiveFileName = $"Archive.Session_{session.Id}.{dateStamp}.json";

        IsBusy = true;
        Status = $"Exporting session {session.Id}...";
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            var effectiveFormat = (exportDiary, exportArchive) switch
            {
                (true, true) => ExportFormat.MarkdownAndJson,
                (true, false) => ExportFormat.MarkdownOnly,
                (false, true) => ExportFormat.JsonOnly,
                _ => ExportFormat.MarkdownAndJson
            };

            var result = await _exportService.ExportAsync(new ExportRequest
            {
                SessionIds = new[] { session.Id },
                ExportDiary = exportDiary,
                ExportArchive = exportArchive,
                Format = effectiveFormat,
                OutputDirectory = perSessionDir,
                DiaryFileName = exportDiary ? diaryFileName : null,
                ArchiveFileName = exportArchive ? archiveFileName : null,
                HideRepoPathsInMarkdown = HideRepoPathsInMarkdown,
                IncludePlaceholders = IncludePlaceholders,
                CancellationToken = _cancellationTokenSource.Token
            });

            if (result.Canceled)
            {
                Status = "Canceled.";
                return;
            }

            if (!result.Succeeded)
            {
                Status = $"Export failed: {result.ErrorMessage}";
                return;
            }

            var files = result.FilesWritten.Select(Path.GetFileName).ToList();
            Status = files.Count > 0
                ? $"Exported: {string.Join(", ", files)}"
                : "Export completed.";
        }
        catch (OperationCanceledException)
        {
            Status = "Canceled.";
        }
        catch (Exception ex)
        {
            Status = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private async Task LoadExportSettingsAsync()
    {
        try
        {
            var lastDir = await _databaseService.GetAppSettingAsync("export.last_dir");
            if (!string.IsNullOrWhiteSpace(lastDir))
            {
                OutputDirectory = lastDir;
                DiaryFileName = BuildSafeDiaryFileName(string.Empty);
                return;
            }

            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var fallback = Path.Combine(docs, "DevChronicleExports");
            var defaultDir = await _settingsService.GetAsync(SettingsService.ExportDefaultDirectoryKey, fallback);
            OutputDirectory = string.IsNullOrWhiteSpace(defaultDir) ? fallback : defaultDir;
            DiaryFileName = BuildSafeDiaryFileName(string.Empty);
        }
        catch
        {
            // ignore settings load failures
        }
    }

    private static string BuildSafeDiaryFileName(string input)
    {
        var candidate = string.IsNullOrWhiteSpace(input)
            ? $"DevDiary_{DateTime.Now:yyyyMMdd_HHmmss}.md"
            : input.Trim();

        if (!candidate.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            candidate += ".md";

        foreach (var invalid in Path.GetInvalidFileNameChars())
            candidate = candidate.Replace(invalid, '_');

        return candidate;
    }
}

public partial class ExportSessionItemViewModel : ObservableObject
{
    private readonly Models.Session _session;

    public ExportSessionItemViewModel(Models.Session session)
    {
        _session = session;
    }

    public int Id => _session.Id;
    public string Name => _session.Name;
    public string RepoPath => _session.RepoPath;

    public string RepoName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_session.RepoPath))
                return "(unknown repo)";

            var trimmed = _session.RepoPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFileName(trimmed);
        }
    }

    public string RangeDisplay
    {
        get
        {
            if (_session.RangeStart.HasValue && _session.RangeEnd.HasValue)
                return $"{_session.RangeStart:yyyy-MM-dd} -> {_session.RangeEnd:yyyy-MM-dd}";

            return "All history";
        }
    }

    public string RepoAndRange => $"{RepoName} - {RangeDisplay}";

    public string CreatedAtDisplay => $"Created: {_session.CreatedAt:yyyy-MM-dd HH:mm}";

    [ObservableProperty]
    private bool isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? SelectionChanged;
}
