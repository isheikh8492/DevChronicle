using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevChronicle.Services;
using Microsoft.Win32;

namespace DevChronicle.ViewModels;

/// <summary>
/// ViewModel for Phase C: Export controls.
/// </summary>
public partial class ExportViewModel : ObservableObject
{
    private readonly ExportService _exportService;
    private readonly SessionContextService _sessionContext;
    private readonly DatabaseService _databaseService;
    private bool _isUpdatingSelectAll;

    [ObservableProperty]
    private DateTime startDate = DateTime.Now.AddDays(-30);

    [ObservableProperty]
    private DateTime endDate = DateTime.Now;

    [ObservableProperty]
    private string status = "Ready to export";

    [ObservableProperty]
    private bool isExporting;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private ObservableCollection<ExportSessionItemViewModel> sessions = new();

    [ObservableProperty]
    private bool selectAll;

    public ExportViewModel(
        ExportService exportService,
        SessionContextService sessionContext,
        DatabaseService databaseService)
    {
        _exportService = exportService;
        _sessionContext = sessionContext;
        _databaseService = databaseService;

        _ = LoadSessionsAsync();
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
    }

    [RelayCommand]
    private async Task ExportDeveloperDiaryAsync()
    {
        var session = _sessionContext.CurrentSession;
        if (session == null)
        {
            Status = "No session selected";
            return;
        }

        // Show save file dialog
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"DeveloperDiary_{session.Name}_{DateTime.Now:yyyyMMdd}.md",
            Filter = "Markdown files (*.md)|*.md|All files (*.*)|*.*",
            Title = "Export Developer Diary"
        };

        if (dialog.ShowDialog() != true)
            return;

        IsExporting = true;
        Status = "Exporting developer diary...";

        try
        {
            var outputPath = await _exportService.ExportDeveloperDiaryAsync(
                session.Id,
                StartDate,
                EndDate,
                dialog.FileName);

            Status = $"Exported successfully to {Path.GetFileName(outputPath)}";

            // Show success message
            System.Windows.MessageBox.Show(
                $"Developer diary exported successfully!\n\nFile: {outputPath}",
                "Export Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Status = $"Export failed: {ex.Message}";
            System.Windows.MessageBox.Show(
                $"Failed to export developer diary:\n\n{ex.Message}",
                "Export Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsExporting = false;
        }
    }

    [RelayCommand]
    private async Task ExportResumeBulletsAsync()
    {
        var session = _sessionContext.CurrentSession;
        if (session == null)
        {
            Status = "No session selected";
            return;
        }

        // Show save file dialog
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"ResumeBullets_{session.Name}_{DateTime.Now:yyyyMMdd}.md",
            Filter = "Markdown files (*.md)|*.md|All files (*.*)|*.*",
            Title = "Export Resume Bullets"
        };

        if (dialog.ShowDialog() != true)
            return;

        IsExporting = true;
        Status = "Exporting resume bullets...";

        try
        {
            var outputPath = await _exportService.ExportResumeBulletsAsync(
                session.Id,
                StartDate,
                EndDate,
                dialog.FileName,
                maxBullets: 12);

            Status = $"Exported successfully to {Path.GetFileName(outputPath)}";

            // Show success message
            System.Windows.MessageBox.Show(
                $"Resume bullets exported successfully!\n\nFile: {outputPath}",
                "Export Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Status = $"Export failed: {ex.Message}";
            System.Windows.MessageBox.Show(
                $"Failed to export resume bullets:\n\n{ex.Message}",
                "Export Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsExporting = false;
        }
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
