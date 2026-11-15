using System;
using System.IO;
using System.Linq;

namespace CanvasDesktop;

public record GitWorktreeInfo(
    bool IsGitRepo,
    bool IsWorktree,
    string? RepoRoot,
    string? BranchName,
    string? WorktreeName);

public static class GitWorktreeHelper
{
    public static GitWorktreeInfo GetWorktreeInfo(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return new GitWorktreeInfo(false, false, null, null, null);

        try
        {
            if (!Directory.Exists(folder))
                return new GitWorktreeInfo(false, false, null, null, null);

            var fullFolderPath = Path.GetFullPath(folder);
            var dotGit = Path.Combine(fullFolderPath, ".git");

            if (Directory.Exists(dotGit))
            {
                // Standard Git repository root
                var headPath = Path.Combine(dotGit, "HEAD");
                var branchName = ParseBranchFromHead(headPath);
                return new GitWorktreeInfo(
                    IsGitRepo: true,
                    IsWorktree: false,
                    RepoRoot: fullFolderPath,
                    BranchName: branchName,
                    WorktreeName: null);
            }

            if (File.Exists(dotGit))
            {
                // Linked git worktree
                var gitDirPath = ReadGitDir(dotGit);
                if (string.IsNullOrEmpty(gitDirPath))
                    return new GitWorktreeInfo(false, false, null, null, null);

                if (!Path.IsPathFullyQualified(gitDirPath))
                {
                    gitDirPath = Path.GetFullPath(Path.Combine(fullFolderPath, gitDirPath));
                }
                else
                {
                    gitDirPath = Path.GetFullPath(gitDirPath);
                }

                if (!Directory.Exists(gitDirPath))
                    return new GitWorktreeInfo(false, false, null, null, null);

                var worktreeName = Path.GetFileName(gitDirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                var headPath = Path.Combine(gitDirPath, "HEAD");
                var branchName = ParseBranchFromHead(headPath);

                // Resolve repository root
                string? repoRoot = null;
                var commonDirPath = Path.Combine(gitDirPath, "commondir");
                if (File.Exists(commonDirPath))
                {
                    var commonDirRel = File.ReadAllText(commonDirPath).Trim();
                    var commonDir = Path.GetFullPath(Path.Combine(gitDirPath, commonDirRel));
                    repoRoot = Directory.GetParent(commonDir)?.FullName ?? commonDir;
                }
                else
                {
                    var parentDir = Directory.GetParent(gitDirPath);
                    if (parentDir != null && string.Equals(parentDir.Name, "worktrees", StringComparison.OrdinalIgnoreCase))
                    {
                        var dotGitDir = parentDir.Parent;
                        repoRoot = dotGitDir?.Parent?.FullName;
                    }
                }

                return new GitWorktreeInfo(
                    IsGitRepo: true,
                    IsWorktree: true,
                    RepoRoot: repoRoot,
                    BranchName: branchName,
                    WorktreeName: worktreeName);
            }

            return new GitWorktreeInfo(false, false, null, null, null);
        }
        catch
        {
            return new GitWorktreeInfo(false, false, null, null, null);
        }
    }

    private static string? ReadGitDir(string dotGitFile)
    {
        try
        {
            foreach (var rawLine in File.ReadLines(dotGitFile))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                {
                    var path = line.Substring("gitdir:".Length).Trim();
                    if (!string.IsNullOrEmpty(path))
                        return path;
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static string? ParseBranchFromHead(string headPath)
    {
        try
        {
            if (!File.Exists(headPath))
                return null;

            var line = File.ReadLines(headPath).FirstOrDefault()?.Trim();
            if (string.IsNullOrEmpty(line))
                return null;

            if (line.StartsWith("ref: refs/heads/", StringComparison.OrdinalIgnoreCase))
            {
                return line.Substring("ref: refs/heads/".Length).Trim();
            }
            if (line.StartsWith("ref: ", StringComparison.OrdinalIgnoreCase))
            {
                return line.Substring("ref: ".Length).Trim();
            }
            return line;
        }
        catch
        {
            return null;
        }
    }
}
