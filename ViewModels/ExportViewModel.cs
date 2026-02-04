using System.IO;
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

    [ObservableProperty]
    private DateTime startDate = DateTime.Now.AddDays(-30);

    [ObservableProperty]
    private DateTime endDate = DateTime.Now;

    [ObservableProperty]
    private string status = "Ready to export";

    [ObservableProperty]
    private bool isExporting;

    public ExportViewModel(
        ExportService exportService,
        SessionContextService sessionContext)
    {
        _exportService = exportService;
        _sessionContext = sessionContext;
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
