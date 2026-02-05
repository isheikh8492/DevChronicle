using System;
using System.Collections.ObjectModel;

namespace DevChronicle.ViewModels;

public class BranchEvidenceViewModel
{
    public BranchEvidenceViewModel(string branchName, IReadOnlyList<BranchCommitViewModel> commits)
    {
        BranchName = branchName;
        Commits = new ObservableCollection<BranchCommitViewModel>(commits);
    }

    public string BranchName { get; }
    public ObservableCollection<BranchCommitViewModel> Commits { get; }
    public int CommitCount => Commits.Count;
}

public class BranchCommitViewModel
{
    public BranchCommitViewModel(string sha, string subject, DateTime authorDate)
    {
        Sha = sha;
        Subject = subject;
        AuthorDate = authorDate;
    }

    public string Sha { get; }
    public string Subject { get; }
    public DateTime AuthorDate { get; }
}
