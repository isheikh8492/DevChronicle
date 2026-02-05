using System.Collections.ObjectModel;
using DevChronicle.Models;

namespace DevChronicle.ViewModels;

public class CommitEvidenceViewModel
{
    public CommitEvidenceViewModel(string sha, string subject, IReadOnlyList<FileEvidenceViewModel> files)
    {
        Sha = sha;
        Subject = subject;
        Files = new ObservableCollection<FileEvidenceViewModel>(files);
    }

    public string Sha { get; }
    public string Subject { get; }
    public ObservableCollection<FileEvidenceViewModel> Files { get; }
}

public class FileEvidenceViewModel
{
    public FileEvidenceViewModel(string path, int additions, int deletions)
    {
        Path = path;
        Additions = additions;
        Deletions = deletions;
    }

    public string Path { get; }
    public int Additions { get; }
    public int Deletions { get; }
}
