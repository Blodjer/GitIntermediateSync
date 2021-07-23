using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace GitIntermediateSync
{
    abstract class GitHelper
    {
        public static bool CheckGitAvailability()
        {
            try
            {
                ProcessStartInfo ps = new ProcessStartInfo("git", "--version")
                {
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using (Process p = Process.Start(ps))
                {
                    p.WaitForExit();
                    return p.ExitCode == 0;
                }
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        public static LibGit2Sharp.Remote GetRepositoryRemote(in LibGit2Sharp.Repository repository)
        {
            // TODO: Handle non-default origin names
            return repository.Network.Remotes[Defines.REMOTE_DEFAULT_NAME];
        }

        public static bool GetRepositoryIdentifierName(in string repositoryPath, out string repositoryIdentifier)
        {
            using (var repository = new LibGit2Sharp.Repository(repositoryPath))
            {
                return GetRepositoryIdentifierName(repository, out repositoryIdentifier);
            }
        }

        public static bool GetRepositoryIdentifierName(in LibGit2Sharp.Repository repository, out string repositoryIdentifier)
        {
            repositoryIdentifier = string.Empty;

            var remote = GetRepositoryRemote(repository);
            if (remote == null)
            {
                return false;
            }

            string origin = remote.Url;

            const string gitUrlEnding = ".git";
            if (origin.EndsWith(gitUrlEnding))
            {
                origin = origin.Remove(origin.Length - gitUrlEnding.Length, gitUrlEnding.Length);
            }

            int baseLength = origin.LastIndexOf('/');
            if (baseLength == -1)
            {
                return false;
            }

            origin = origin.Remove(0, baseLength + 1);

            repositoryIdentifier = origin;
            return true;
        }
        
        public static int CommandRead(in string workingDir, in string command, out string error)
        {
            return CommandRead(workingDir, command, out error, out _);
        }

        public static int CommandWrite(in string workingDir, in string command, out string error)
        {
            return CommandWrite(workingDir, command, out error, out _);
        }

        public static int CommandRead(in string workingDir, in string command, out string error, out string output)
        {
            return Command_Internal(false, workingDir, command, out error, out output);
        }

        public static int CommandWrite(in string workingDir, in string command, out string error, out string output)
        {
            return Command_Internal(true, workingDir, command, out error, out output);
        }

        static readonly ReaderWriterLockSlim commandLock = new ReaderWriterLockSlim();
        private static int Command_Internal(in bool write, in string workingDir, in string command, out string error, out string output)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = command,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,

                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Minimized
            };

            if (write)
            {
                commandLock.EnterWriteLock();
            }
            else
            {
                commandLock.EnterReadLock();
            }

            using (Process p = Process.Start(startInfo))
            {
                output = p.StandardOutput.ReadToEnd();
                error = p.StandardError.ReadToEnd();
                p.WaitForExit();

                if (write)
                {
                    commandLock.ExitWriteLock();
                }
                else
                {
                    commandLock.ExitReadLock();
                }

                return p.ExitCode;
            }
        }

        public static bool IsRemoteSupported(in LibGit2Sharp.Remote remote)
        {
            if (string.IsNullOrEmpty(remote.Url))
            {
                return false;
            }

            if (!remote.Url.StartsWith("https://"))
            {
                return false;
            }

            return true;
        }
    }
    
    // TODO: Iterator is not implemented correctly
    class RepositoryIterator : IEnumerable<RepositoryIterator.Result>
    {
        public class Result
        {
            public Result(LibGit2Sharp.Repository repository, string relativePath, string chain)
            {
                this.Repository = repository;
                this.RelativePath = relativePath;
                this.Chain = chain;
            }

            public readonly LibGit2Sharp.Repository Repository;
            public readonly string RelativePath;
            public readonly string Chain;
        }

        private readonly string RootRepoPath;
        private readonly string CurrentRepoPath;
        private string CurrentRepoChain;

        public RepositoryIterator(LibGit2Sharp.Repository repository)
        {
            this.RootRepoPath = repository.Info.WorkingDirectory;
            this.CurrentRepoPath = this.RootRepoPath;
            this.CurrentRepoChain = string.Empty;
        }

        public RepositoryIterator(string repoPath)
        {
            this.RootRepoPath = repoPath;
            this.CurrentRepoPath = repoPath;
            this.CurrentRepoChain = string.Empty;
        }

        private RepositoryIterator(string repoPath, string currentRepoPath, string currentRepoChain)
        {
            this.RootRepoPath = repoPath;
            this.CurrentRepoPath = currentRepoPath;
            this.CurrentRepoChain = currentRepoChain;
        }

        public IEnumerator<Result> GetEnumerator()
        {
            using (var repo = new LibGit2Sharp.Repository(this.CurrentRepoPath))
            {
                if (!GitHelper.GetRepositoryIdentifierName(repo, out string repoName))
                {
                    yield break;
                }

                CurrentRepoChain = Path.Combine(CurrentRepoChain, repoName);

                Result info = new Result(
                    repo,
                    Helper.GetRelativePath(RootRepoPath, CurrentRepoPath),
                    CurrentRepoChain
                );

                yield return info;

                foreach (var submodule in repo.Submodules)
                {
                    string subPath = Path.Combine(CurrentRepoPath, submodule.Path);
                    if (!LibGit2Sharp.Repository.IsValid(subPath))
                    {
                        Console.Error.WriteLine("Submodule not valid " + subPath);
                        continue;
                    }

                    foreach (Result subInfo in new RepositoryIterator(RootRepoPath, subPath, CurrentRepoChain))
                    {
                        yield return subInfo;
                    }
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
