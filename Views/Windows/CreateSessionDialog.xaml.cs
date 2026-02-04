using System.IO;
using System.Windows;
using DevChronicle.Models;
using Wpf.Ui.Controls;
using WinForms = System.Windows.Forms;

namespace DevChronicle.Views.Windows;

public partial class CreateSessionDialog : FluentWindow
{
    public Session? Result { get; private set; }

    public CreateSessionDialog()
    {
        InitializeComponent();

        // Set default session name
        SessionNameTextBox.Text = $"Session {DateTime.Now:yyyy-MM-dd}";
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

    private void CreateButton_Click(object sender, RoutedEventArgs e)
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

        // Create the session object
        Result = new Session
        {
            Name = SessionNameTextBox.Text.Trim(),
            RepoPath = RepoPathTextBox.Text.Trim(),
            MainBranch = MainBranchTextBox.Text.Trim(),
            CreatedAt = DateTime.UtcNow
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
