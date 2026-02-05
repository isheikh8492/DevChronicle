using System.IO;
using System.Text.Json;
using System.Windows;
using System.Collections.Generic;
using DevChronicle.Models;
using Wpf.Ui.Controls;
using DevChronicle.Services;
using Microsoft.Extensions.DependencyInjection;
using WinForms = System.Windows.Forms;

namespace DevChronicle.Views.Windows;

public partial class CreateSessionDialog : FluentWindow
{
    public Session? Result { get; private set; }
    private readonly SettingsService _settingsService;

    public CreateSessionDialog()
    {
        InitializeComponent();
        _settingsService = App.ServiceProvider.GetRequiredService<SettingsService>();

        // Set default session name
        SessionNameTextBox.Text = $"Session {DateTime.Now:yyyy-MM-dd}";

        if (RangeEndDatePicker != null)
            RangeEndDatePicker.SelectedDate = DateTime.Today;
        if (RangeStartDatePicker != null)
            RangeStartDatePicker.SelectedDate = DateTime.Today.AddDays(-14);
    }

    private void MineAllHistoryCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (MineAllHistoryCheckBox == null || RangeStartDatePicker == null || RangeEndDatePicker == null)
            return;

        var mineAll = MineAllHistoryCheckBox.IsChecked == true;
        RangeStartDatePicker.IsEnabled = !mineAll;
        RangeEndDatePicker.IsEnabled = !mineAll;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Select a Git repository folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            RepoPathTextBox.Text = dialog.SelectedPath;
            ValidationInfoBar.IsOpen = false;
        }
    }

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        // Validate inputs
        if (string.IsNullOrWhiteSpace(SessionNameTextBox.Text))
        {
            ShowValidationError("Please enter a session name.");
            return;
        }

        if (string.IsNullOrWhiteSpace(RepoPathTextBox.Text))
        {
            ShowValidationError("Please select a repository path.");
            return;
        }

        if (!Directory.Exists(RepoPathTextBox.Text))
        {
            ShowValidationError("The selected directory does not exist.");
            return;
        }

        // Check if it's a Git repository
        var gitDir = Path.Combine(RepoPathTextBox.Text, ".git");
        if (!Directory.Exists(gitDir))
        {
            ShowValidationError("The selected directory is not a Git repository (no .git folder found).");
            return;
        }

        if (string.IsNullOrWhiteSpace(MainBranchTextBox.Text))
        {
            ShowValidationError("Please enter a main branch name.");
            return;
        }

        if (MineAllHistoryCheckBox == null || RangeStartDatePicker == null || RangeEndDatePicker == null)
        {
            ShowValidationError("Date range controls are not available.");
            return;
        }

        var mineAll = MineAllHistoryCheckBox.IsChecked == true;
        if (!mineAll)
        {
            if (RangeStartDatePicker.SelectedDate == null || RangeEndDatePicker.SelectedDate == null)
            {
                ShowValidationError("Please select a start and end date, or choose 'Mine all history'.");
                return;
            }

            if (RangeStartDatePicker.SelectedDate > RangeEndDatePicker.SelectedDate)
            {
                ShowValidationError("Start date must be before end date.");
                return;
            }
        }

        var authorFilters = new List<AuthorFilter>();
        if (AuthorFiltersTextBox != null && !string.IsNullOrWhiteSpace(AuthorFiltersTextBox.Text))
        {
            var tokens = AuthorFiltersTextBox.Text
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var token in tokens)
            {
                if (token.Contains("@"))
                    authorFilters.Add(new AuthorFilter { Email = token });
                else
                    authorFilters.Add(new AuthorFilter { Name = token });
            }
        }

        var options = await _settingsService.GetDefaultSessionOptionsAsync();
        options.IncludeMerges = IncludeMergesCheckBox != null && IncludeMergesCheckBox.IsChecked == true;

        // Create the session object
        Result = new Session
        {
            Name = SessionNameTextBox.Text.Trim(),
            RepoPath = RepoPathTextBox.Text.Trim(),
            MainBranch = MainBranchTextBox.Text.Trim(),
            CreatedAt = DateTime.UtcNow,
            AuthorFiltersJson = JsonSerializer.Serialize(authorFilters),
            OptionsJson = JsonSerializer.Serialize(options),
            RangeStart = mineAll ? null : RangeStartDatePicker.SelectedDate?.Date,
            RangeEnd = mineAll ? null : RangeEndDatePicker.SelectedDate?.Date
        };

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowValidationError(string message)
    {
        ValidationInfoBar.Message = message;
        ValidationInfoBar.IsOpen = true;
    }
}
