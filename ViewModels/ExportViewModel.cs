using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
    public bool CanCreateNewExport => SelectedSessionCount > 0 && !IsBusy;
    public bool CanConvertToManaged => IsDiaryUnmanaged && SelectedSessionCount > 0 && !IsBusy;

    [ObservableProperty]
    private ExportMode mode = ExportMode.Hub;

    [ObservableProperty]
    private string status = "Ready";

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isExporting;

    [ObservableProperty]
    private OperationState exportState = OperationState.Idle;

    [ObservableProperty]
    private string recoverActionText = string.Empty;

    [ObservableProperty]
    private int progressCurrentStep;

    [ObservableProperty]
    private int progressTotalSteps;

    [ObservableProperty]
    private double progressPercent;

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

    public bool HasRecoverableIssue => !string.IsNullOrWhiteSpace(RecoverActionText);
    public bool IsProgressIndeterminate => IsBusy && ProgressTotalSteps <= 0;
    public bool HasProgressCounter => ProgressTotalSteps > 0;
    public bool ShowStatusPanel => IsBusy || ExportState != OperationState.Idle;
    public string ProgressCounterText => ProgressTotalSteps > 0 ? $"{ProgressCurrentStep}/{ProgressTotalSteps}" : string.Empty;

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
        OnPropertyChanged(nameof(CanCreateNewExport));
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
        OnPropertyChanged(nameof(CanCreateNewExport));
        OnPropertyChanged(nameof(CanConvertToManaged));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(ShowStatusPanel));
    }

    partial void OnIsDiaryUnmanagedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConvertToManaged));
    }

    partial void OnOutputDirectoryChanged(string value)
    { }

    partial void OnProgressCurrentStepChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressCounterText));
        OnPropertyChanged(nameof(HasProgressCounter));
    }

    partial void OnProgressTotalStepsChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressCounterText));
        OnPropertyChanged(nameof(HasProgressCounter));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
    }

    partial void OnRecoverActionTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasRecoverableIssue));
    }

    partial void OnExportStateChanged(OperationState value)
    {
        OnPropertyChanged(nameof(ShowStatusPanel));
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
        OnPropertyChanged(nameof(CanCreateNewExport));
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
        RecoverActionText = string.Empty;
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
        RecoverActionText = string.Empty;
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
            RequireInput("Select at least one session.", "Select at least one session.");
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            RequireInput("Choose an output directory.", "Choose an output folder and retry.");
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
        IsExporting = true;
        BeginOperation("Exporting selected sessions");
        _cancellationTokenSource = new CancellationTokenSource();
        var progressReporter = CreateProgressReporter();

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
                ProgressReporter = progressReporter,
                CancellationToken = _cancellationTokenSource.Token
            });

            if (result.Canceled)
            {
                CancelOperation("Export canceled. No partial files were kept.");
                return;
            }

            if (!result.Succeeded)
            {
                FailOperation($"Export failed: {result.ErrorMessage}", "Review output settings and retry.");
                return;
            }

            var files = result.FilesWritten.Count;
            CompleteOperation($"Export complete. {files} file(s) written.");
        }
        catch (OperationCanceledException)
        {
            CancelOperation("Export canceled. No partial files were kept.");
        }
        catch (Exception ex)
        {
            FailOperation($"Export failed: {ex.Message}", "Review output settings and retry.");
        }
        finally
        {
            IsExporting = false;
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
        BeginOperation("Inspecting diary");
        _cancellationTokenSource = new CancellationTokenSource();
        var progressReporter = CreateProgressReporter();

        try
        {
            var preview = await _exportService.PreviewDiaryUpdateAsync(new UpdateDiaryRequest
            {
                DiaryPath = DiaryPath,
                ProgressReporter = progressReporter,
                CancellationToken = _cancellationTokenSource.Token
            });

            if (preview.Result.Canceled)
            {
                CancelOperation("Update canceled. Original diary unchanged.");
                return;
            }

            if (!preview.Result.Succeeded)
            {
                IsDiaryUnmanaged = true;
                FailOperation("Selected file is not managed.", "Use Convert to managed, then Apply Update.");
                return;
            }

            IsDiaryManaged = true;
            CurrentDiff = preview.Diff;
            foreach (var id in preview.BoundSessionIds)
                BoundSessionIds.Add(id);

            CompleteOperation(preview.Diff.IsStale ? "Out of date." : "Up to date.");
        }
        catch (OperationCanceledException)
        {
            CancelOperation("Update canceled. Original diary unchanged.");
        }
        catch (Exception ex)
        {
            FailOperation($"Update failed: {ex.Message}", "Choose a valid diary file and retry.");
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
            RequireInput("Choose a diary file first.", "Browse for a diary file, then retry.");
            return;
        }

        IsBusy = true;
        BeginOperation("Updating managed diary");
        _cancellationTokenSource = new CancellationTokenSource();
        var progressReporter = CreateProgressReporter();

        try
        {
            var (diff, result) = await _exportService.UpdateDiaryAsync(new UpdateDiaryRequest
            {
                DiaryPath = DiaryPath,
                ProgressReporter = progressReporter,
                CancellationToken = _cancellationTokenSource.Token
            });

            if (result.Canceled)
            {
                CancelOperation("Update canceled. Original diary unchanged.");
                return;
            }

            if (!result.Succeeded)
            {
                FailOperation($"Update failed: {result.ErrorMessage}", "Inspect the diary state and retry.");
                return;
            }

            CurrentDiff = diff;
            CompleteOperation("Diary updated successfully.");
        }
        catch (OperationCanceledException)
        {
            CancelOperation("Update canceled. Original diary unchanged.");
        }
        catch (Exception ex)
        {
            FailOperation($"Update failed: {ex.Message}", "Inspect the diary state and retry.");
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
            RequireInput("Choose a diary file first.", "Browse for a diary file, then convert.");
            return;
        }

        var selectedIds = Sessions.Where(s => s.IsSelected).Select(s => s.Id).ToList();
        if (selectedIds.Count == 0)
        {
            RequireInput("Select sessions to bind, then convert.", "Select sessions to bind, then convert.");
            return;
        }

        IsBusy = true;
        BeginOperation("Converting diary to managed format");
        _cancellationTokenSource = new CancellationTokenSource();
        var progressReporter = CreateProgressReporter();

        try
        {
            var output = await _exportService.ConvertUnmanagedDiaryToManagedAsync(
                inputDiaryPath: DiaryPath,
                sessionIds: selectedIds,
                hideRepoPaths: HideRepoPathsInMarkdown,
                includePlaceholders: IncludePlaceholders,
                cancellationToken: _cancellationTokenSource.Token,
                progressReporter: progressReporter);

            DiaryPath = output;
            CompleteOperation("Managed copy created and loaded.");
            await ComputeDiaryDiffAsync();
        }
        catch (OperationCanceledException)
        {
            CancelOperation("Conversion canceled. Source file unchanged.");
        }
        catch (Exception ex)
        {
            FailOperation($"Conversion failed: {ex.Message}", "Review source file and session selection, then retry.");
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
        var dateStamp = DateTime.Now.ToString("yyyy-MM-dd");
        var diaryFileName = $"Diary.Session_{session.Id}.{dateStamp}.md";
        var archiveFileName = $"Archive.Session_{session.Id}.{dateStamp}.json";
        var docsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        string? diaryPath = null;
        string? archivePath = null;

        if (exportDiary)
        {
            var diaryDialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save session diary as",
                Filter = "Markdown files (*.md)|*.md|All files (*.*)|*.*",
                FileName = diaryFileName,
                InitialDirectory = docsDir,
                CheckPathExists = true,
                ValidateNames = true
            };

            if (diaryDialog.ShowDialog() != true)
            {
                RequireInput("No file selected.", string.Empty);
                return;
            }

            diaryPath = diaryDialog.FileName;
        }

        if (exportArchive)
        {
            var archiveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save session archive as",
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                FileName = archiveFileName,
                InitialDirectory = docsDir,
                CheckPathExists = true,
                ValidateNames = true
            };

            if (archiveDialog.ShowDialog() != true)
            {
                RequireInput("No file selected.", string.Empty);
                return;
            }

            archivePath = archiveDialog.FileName;
        }

        var exportDirectory = diaryPath != null
            ? Path.GetDirectoryName(diaryPath)
            : Path.GetDirectoryName(archivePath!);

        if (string.IsNullOrWhiteSpace(exportDirectory))
        {
            RequireInput("Invalid export destination.", "Choose a valid destination and retry.");
            return;
        }

        IsBusy = true;
        IsExporting = true;
        BeginOperation($"Exporting session {session.Id}");
        _cancellationTokenSource = new CancellationTokenSource();
        var progressReporter = CreateProgressReporter();

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
                OutputDirectory = exportDirectory,
                DiaryFileName = exportDiary ? Path.GetFileName(diaryPath) : null,
                ArchiveFileName = exportArchive ? Path.GetFileName(archivePath) : null,
                HideRepoPathsInMarkdown = HideRepoPathsInMarkdown,
                IncludePlaceholders = IncludePlaceholders,
                ProgressReporter = progressReporter,
                CancellationToken = _cancellationTokenSource.Token
            });

            if (result.Canceled)
            {
                CancelOperation("Session export canceled.");
                return;
            }

            if (!result.Succeeded)
            {
                FailOperation($"Session export failed: {result.ErrorMessage}", "Review destination and retry.");
                return;
            }

            CompleteOperation("Session export complete.");
        }
        catch (OperationCanceledException)
        {
            CancelOperation("Session export canceled.");
        }
        catch (Exception ex)
        {
            FailOperation($"Session export failed: {ex.Message}", "Review destination and retry.");
        }
        finally
        {
            IsExporting = false;
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

    private void ResetProgress()
    {
        ProgressCurrentStep = 0;
        ProgressTotalSteps = 0;
        ProgressPercent = 0;
    }

    private void BeginOperation(string verb)
    {
        ExportState = OperationState.Running;
        RecoverActionText = string.Empty;
        ResetProgress();
        Status = OperationStatusFormatter.FormatProgress(verb, 0, 0);
    }

    private void CompleteOperation(string successMessage)
    {
        ExportState = OperationState.Success;
        RecoverActionText = string.Empty;
        Status = OperationStatusFormatter.FormatTerminal(OperationState.Success, successMessage);
    }

    private void CancelOperation(string cancelMessage)
    {
        ExportState = OperationState.Canceled;
        RecoverActionText = string.Empty;
        Status = OperationStatusFormatter.FormatTerminal(OperationState.Canceled, cancelMessage);
    }

    private void FailOperation(string errorMessage, string recoverAction)
    {
        ExportState = OperationState.Error;
        RecoverActionText = recoverAction;
        Status = OperationStatusFormatter.FormatTerminal(OperationState.Error, errorMessage);
    }

    private void RequireInput(string message, string recoverAction)
    {
        ExportState = OperationState.NeedsInput;
        RecoverActionText = recoverAction;
        Status = OperationStatusFormatter.FormatTerminal(OperationState.NeedsInput, message);
    }

    private IProgress<ExportProgress> CreateProgressReporter()
    {
        return new Progress<ExportProgress>(p =>
        {
            var current = Math.Max(0, p.CurrentStep);
            var total = Math.Max(0, p.TotalSteps);
            if (total > 0 && current > total)
            {
                Debug.WriteLine($"[ExportProgress] Invalid progress pair emitted: {current}/{total}. Clamping.");
                current = total;
            }

            ProgressCurrentStep = current;
            ProgressTotalSteps = total;
            ProgressPercent = ProgressTotalSteps > 0
                ? Math.Clamp((double)ProgressCurrentStep / ProgressTotalSteps * 100, 0, 100)
                : 0;

            if (!string.IsNullOrWhiteSpace(p.Status))
            {
                Status = p.Status;
            }
        });
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
