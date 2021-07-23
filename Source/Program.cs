using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

// TODO
// - Simplify code
// - Cleanup Backups and Patches
// - Replace staged/unstaged with index/working/?

namespace GitIntermediateSync
{
    abstract class Defines
    {
        public const string REMOTE_DEFAULT_NAME = "origin";
        public static readonly Encoding PATCH_ENCODER = new UTF8Encoding(false);
        public const string PATCH_NEW_LINE = "\n";
    }

    abstract class Program
    {
        static readonly string ONE_DRIVE_PATH = Environment.GetEnvironmentVariable("OneDriveConsumer");
        static readonly string SYNC_SUB_PATH = Path.Combine("!sync", "git", "patch");
        const string GIT_INSTALLER_MASK = "Git-*.exe";

        static readonly LibGit2Sharp.Signature SIGNATURE = new LibGit2Sharp.Signature("GitIntermediateSync", "(no email)", DateTimeOffset.UtcNow); // TODO: Replace by getter

        static int Main(string[] args)
        {
            // TODO: Only needs to apply to process start info?
            Console.OutputEncoding = Defines.PATCH_ENCODER; // necessary to output the patch with the correct encoding

            bool success = Run(args);
#if DEBUG
            Console.In.ReadLine();
#endif
            return success ? 0 : -1;
        }

        static bool Run(string[] args)
        {
            if (!CheckPrerequisites())
            {
                return false;
            }

            if (args.Length < 1)
            {
                Console.Error.WriteLine("No operation provided");
                OperationCommands.PrintAllCommands();
                return false;
            }

            if (!OperationCommands.TryGetOperation(args[0], out OperationInfo operationInfo))
            {
                Console.Error.WriteLine("Unknown operation");
                OperationCommands.PrintAllCommands();
                return false;
            }

            string syncPath;
            {
                syncPath = Path.Combine(ONE_DRIVE_PATH, SYNC_SUB_PATH);
                if (!Directory.Exists(syncPath))
                {
                    Console.Error.WriteLine("Sync directory not found! ({0})", syncPath);
                    return false;
                }
            }

            string repositoryPath;
            {
                if (args.Length > 1)
                {
                    repositoryPath = args[1];
                }
                else
                {
                    repositoryPath = Directory.GetCurrentDirectory();
                }

                if (!Directory.Exists(repositoryPath))
                {
                    Console.Error.WriteLine("Repository directory not found!");
                    return false;
                }

                if (!LibGit2Sharp.Repository.IsValid(repositoryPath))
                {
                    Console.Error.WriteLine("Repository not valid!");
                    return false;
                }
            }

            // Run Operation

            OperationReturn operationReturn = RunOperation(operationInfo, repositoryPath, syncPath);
            switch (operationReturn.Result)
            {
                case OperationResult.Success:
                    Console.Out.WriteLine("\nOPERATION COMPLETED");
                    return true;
                case OperationResult.Failure:
                    Console.Out.WriteLine("\nOPERATION FAILED");
                    return false;
                case OperationResult.Abort:
                    Console.Out.WriteLine("\nOPERATION ABORTED");
                    return false;
                default:
                    Console.Out.WriteLine("\nUNKOWN OPERATION RESULT");
                    return false;
            }
        }

        static OperationReturn RunOperation(in OperationInfo operationInfo, in string repositoryPath, in string syncPath)
        {
            if (operationInfo.showDestructiveWarning)
            {
                if (!Helper.ShowWarningMessage(string.Format("<{0}> is a destructive operation. Do you want to continue?", operationInfo.command)))
                {
                    return OperationResult.Abort;
                }
            }

            switch (operationInfo.operation)
            {
                case Operation.Save: return Op_MakePatch(repositoryPath, syncPath);
                case Operation.Apply: return Op_ApplyPatch(repositoryPath, syncPath);
                case Operation.Compare: return Op_ComparePatch(repositoryPath, syncPath);

                default:
                    Console.Out.WriteLine("OPERATION NOT IMPLEMENTED!");
                    break;
            }

            return OperationResult.Unknown;
        }

        static bool CheckPrerequisites()
        {
            if (GitHelper.CheckGitAvailability())
            {
                return true;
            }

            Console.Error.WriteLine("Git is not available!");

            string[] gitInstallationFiles = Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, GIT_INSTALLER_MASK, SearchOption.TopDirectoryOnly);
            if (gitInstallationFiles.Length == 0)
            {
                Console.Error.WriteLine("Unable to find a git installation executable!");
                return false;
            }
            else if (gitInstallationFiles.Length == 1)
            {
                string gitInstallExe = gitInstallationFiles[0];
                Console.Error.WriteLine("Running {0}", gitInstallExe);

                using (Process p = Process.Start(gitInstallExe))
                {
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                    {
                        Console.Error.WriteLine("Installation failed!");
                        return false;
                    }
                }

                if (!GitHelper.CheckGitAvailability())
                {
                    Console.Error.WriteLine("Git is still not available! Please try to restart your application or computer.");
                    return false;
                }
            }
            else
            {
                Console.Error.WriteLine("There are multiple git installation executables. Unable to install!");
                return false;
            }

            return true;
        }

        static OperationReturn Op_MakePatch(in string rootRepositoryPath, in string syncPath)
        {
            using (var root = new LibGit2Sharp.Repository(rootRepositoryPath))
            {
                if (!IsRepositoryValidAndReady_R(root))
                {
                    return false;
                }

                Patch patch = Patch.New();

                // Write to patch
                {
                    bool success = true;

                    if (success)
                    {
                        success &= AddHeadsToPatch(root, patch);
                    }

                    if (success)
                    {
                        Task<bool> patchDiffTask = AddDiffToPatch(root, patch);
                        patchDiffTask.Wait();

                        success &= patchDiffTask.Result;
                    }

                    if (!success)
                    {
                        return false;
                    }
                }

                if (patch.Serialize(root, syncPath, out string file))
                {
                    Console.Out.WriteLine("Created patch {0}", file);
                    return true;
                }
                else
                {
                    Console.Error.WriteLine("Failed to create patch!");
                    return false;
                }
            }
        }

        static OperationReturn Op_ApplyPatch(in string rootRepositoryPath, in string syncPath)
        {
            using (var root = new LibGit2Sharp.Repository(rootRepositoryPath))
            {
                if (!IsRepositoryValidAndReady_R(root))
                {
                    return false;
                }

                // Find patch

                Patch patch;
                do
                {
                    patch = Patch.FromPath(root, syncPath, out DateTime timestamp);
                    if (patch == null)
                    {
                        Console.Error.WriteLine("Could not find patch in {0}", syncPath);
                        return false;
                    }

                    TimeSpan patchAge = DateTime.Now - timestamp.ToLocalTime();
                    string message = string.Format("Latest patch is from {0} ago ({1})\nDo you want to apply this patch?", Helper.ToReadableString(patchAge), timestamp.ToLocalTime().ToString());

                    if (Helper.ShowConfirmationRequest(message))
                    {
                        Console.Out.WriteLine();
                        break;
                    }
                    else
                    {
                        return OperationResult.Abort;
                    }
                } while (true);

                // Apply latest patch

                if (!Stash_R(root))
                {
                    return false;
                }
                Console.Out.WriteLine();

                if (!ApplyPatchHead_R(root, patch.Heads))
                {
                    return false;
                }
                Console.Out.WriteLine();

                if (!ApplyDiff_R(root, patch.DiffStaged, true) ||
                    !ApplyDiff_R(root, patch.DiffUnstaged, false))
                {
                    return false;
                }

                return true;
            }
        }

        static bool Op_ComparePatch(in string rootRepositoryPath, in string syncPath)
        {
            using (var root = new LibGit2Sharp.Repository(rootRepositoryPath))
            {
                if (!IsRepositoryValidAndReady_R(root))
                {
                    return false;
                }

                Patch patch = Patch.FromPath(root, syncPath, out DateTime timestamp);
                if (patch == null)
                {
                    Console.Error.WriteLine("Could not find patch in {0}", syncPath);
                    return false;
                }

                AddUntrackedUnstagedFiles_R(root);

                var diffUnstagedTask = GetCombinedDiff_R(root, false);
                var diffStagedTask = GetCombinedDiff_R(root, true);

                var tasks = new Task<DiffResult>[]
                {
                    diffUnstagedTask,
                    diffStagedTask
                };
                Task.WaitAll(tasks);

                foreach (var task in tasks)
                {
                    if (!task.Result.Success)
                    {
                        Console.Error.WriteLine(task.Result.Error);
                        return false;
                    }
                }

                bool equal = DiffEquals(diffUnstagedTask.Result.Diff, patch.DiffUnstaged) && DiffEquals(diffStagedTask.Result.Diff, patch.DiffStaged);
                if (equal)
                {
                    TimeSpan patchAge = DateTime.Now - timestamp.ToLocalTime();
                    Console.Out.WriteLine("No difference detected. You are up to date with the latest patch from {0} ago ({1})", Helper.ToReadableString(patchAge), timestamp.ToLocalTime().ToString());
                }
                else
                {
                    TimeSpan patchAge = DateTime.Now - timestamp.ToLocalTime();
                    Console.Out.WriteLine("Differences found to the latest patch from {0} ago ({1})", Helper.ToReadableString(patchAge), timestamp.ToLocalTime().ToString());
                }
            }

            return true;
        }

        static bool DiffEquals(in string a, in string b)
        {
            return (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) || string.Equals(a, b, StringComparison.Ordinal);
        }

        static bool IsRepositoryValidAndReady_R(in LibGit2Sharp.Repository root)
        {
            foreach (var it in new RepositoryIterator(root))
            {
                var remote = GitHelper.GetRepositoryRemote(it.Repository);
                if (!GitHelper.IsRemoteSupported(remote))
                {
                    Console.Error.WriteLine("Only https connections are supported for remotes ({0})", it.Repository.Info.WorkingDirectory);
                    return false;
                }

                if (it.Repository.Info.CurrentOperation != LibGit2Sharp.CurrentOperation.None)
                {
                    Console.Error.WriteLine("Repository is currently in operation ({0})", it.Chain);
                    return false;
                }
            }

            return true;
        }

        static bool AddHeadsToPatch(in LibGit2Sharp.Repository root, in Patch patch)
        {
            foreach (var it in new RepositoryIterator(root))
            {
                // TODO: Prevent patch if there are commits made in detached head state

                if (it.Repository.Head.TrackingDetails.AheadBy != null && it.Repository.Head.TrackingDetails.AheadBy != 0)
                {
                    Console.Error.WriteLine("Cannot create patch if there are uncommited changes in {0}", it.Chain);
                    return false;
                }

                Patch.HeadInfo headInfo = new Patch.HeadInfo();
                headInfo.Sha = it.Repository.Head.Tip.Sha;

                if (it.Repository.Info.IsHeadDetached)
                {

                }
                else if (it.Repository.Head.IsTracking && it.Repository.Head.TrackedBranch.IsRemote)
                {
                    LibGit2Sharp.Branch remoteBranch = it.Repository.Head.TrackedBranch;

                    string remoteBranchName = remoteBranch.CanonicalName;
                    string prefixToRemove = "refs/remotes/" + remoteBranch.RemoteName + "/";
                    if (!remoteBranchName.StartsWith(prefixToRemove))
                    {
                        Console.Error.WriteLine("Unexpected remote branch canonical name ({0}: {1})", it.Chain, remoteBranch.CanonicalName);
                        return false;
                    }

                    headInfo.RemoteBranchName = remoteBranchName.Remove(0, prefixToRemove.Length);
                }
                else
                {
                    if (!it.Repository.Info.IsHeadDetached && !it.Repository.Head.IsTracking)
                    {
                        Console.Error.WriteLine("Cannot create patch for local branch ({0}: {1})", it.Chain, it.Repository.Head.CanonicalName);
                    }
                    else
                    {
                        Console.Error.WriteLine("Unknown HEAD location ({0}: {1})", it.Chain, it.Repository.Head.CanonicalName);
                    }

                    return false;
                }

                patch.Heads.Add(it.Chain, headInfo);
            };

            return true;
        }

        static bool ApplyPatchHead_R(in LibGit2Sharp.Repository root, in Dictionary<string, Patch.HeadInfo> heads)
        {
            Console.Out.WriteLine("Apply patch HEADs...");

            foreach (var it in new RepositoryIterator(root))
            {
                // INFO: We expect the repository to have a remote that is called origin

                var remote = GitHelper.GetRepositoryRemote(it.Repository);
                if (remote == null)
                {
                    Console.Error.WriteLine("Could not find remote ({0})", it.Repository.Info.WorkingDirectory);
                    return false;
                }

                List<string> fetchRefSpecs = new List<string>();
                string remoteRefDestinationBase = string.Empty;
                foreach (var refSpec in remote.FetchRefSpecs)
                {
                    fetchRefSpecs.Add(refSpec.Specification);
                    remoteRefDestinationBase = refSpec.Destination;
                }

                if (fetchRefSpecs.Count != 1)
                {
                    Console.Error.WriteLine("Unexpected fetch ref specs ({0})", remote.Name);
                    return false;
                }

                it.Repository.Network.Fetch(remote.Name, fetchRefSpecs);

                if (heads.TryGetValue(it.Chain, out Patch.HeadInfo info))
                {
                    var checkoutOptions = new LibGit2Sharp.CheckoutOptions
                    {
                        CheckoutModifiers = LibGit2Sharp.CheckoutModifiers.Force
                    };

                    if (info.RemoteBranchName == null) // Checkout detached HEAD
                    {
                        var commit = it.Repository.Lookup(info.Sha, LibGit2Sharp.ObjectType.Commit) as LibGit2Sharp.Commit;
                        if (commit != null)
                        {
                            LibGit2Sharp.Commands.Checkout(it.Repository, commit, checkoutOptions);
                            Console.Out.WriteLine("{0,-15} -> {1}", it.Chain, commit.Sha);
                            continue;
                        }
                    }
                    else // Checkout branch
                    {
                        // Get remote branch from name
                        // TODO: Handle branch with different local name

                        string remoteBranchRef = remoteRefDestinationBase.Replace("*", info.RemoteBranchName);
                        var remoteBranch = it.Repository.Branches[remoteBranchRef];
                        if (remoteBranch == null)
                        {
                            Console.Error.WriteLine("Could not find remote branch ({0})", info.RemoteBranchName);
                            return false;
                        }

                        // Get local branch from remote branch

                        LibGit2Sharp.Branch localBranch = null;
                        foreach (var branch in it.Repository.Branches)
                        {
                            if (!branch.IsRemote && branch.IsTracking && branch.TrackedBranch == remoteBranch)
                            {
                                localBranch = branch;
                                break;
                            }
                        }

                        // Create new branch if no local branch is tracking remote branch

                        if (localBranch == null)
                        {
                            LibGit2Sharp.Branch newBranch = it.Repository.Branches.Add(info.RemoteBranchName, remoteBranch.Tip, false);
                            localBranch = it.Repository.Branches.Update(newBranch, b => b.TrackedBranch = remoteBranch.CanonicalName);
                        }

                        if (localBranch == null)
                        {
                            Console.Error.WriteLine("Failed to get local branch for remote branch ({0})", info.RemoteBranchName);
                            return false;
                        }

                        // Checkout local branch

                        LibGit2Sharp.Commands.Checkout(it.Repository, localBranch, checkoutOptions);
                        if (!PullCurrentBranch(it, out string checkoutState))
                        {
                            Console.Out.WriteLine("{0,-15} -> {1} [{2}]", it.Chain, localBranch.CanonicalName, checkoutState);
                            return false;
                        }

                        if (it.Repository.Head.Tip.Sha != info.Sha)
                        {
                            var commit = it.Repository.Lookup(info.Sha, LibGit2Sharp.ObjectType.Commit) as LibGit2Sharp.Commit;
                            if (commit != null)
                            {
                                it.Repository.Reset(LibGit2Sharp.ResetMode.Hard, commit);
                                checkoutState += " (Reset back)";
                            }
                            else
                            {
                                Console.Error.WriteLine("Failed to reset branch ({0}) to commit ({1})", info.RemoteBranchName, info.Sha);
                                return false;
                            }
                        }

                        Console.Out.WriteLine("{0,-15} -> {1} [{2}]", it.Chain, localBranch.CanonicalName, checkoutState);
                        continue;
                    }

                    Console.Error.WriteLine("Invalid values in info file ({0})", it.Chain);
                    return false;
                }
            }

            return true;
        }

        static bool PullCurrentBranch(in RepositoryIterator.Result repoIt, out string resultState)
        {
            LibGit2Sharp.FetchOptions fetchOptions = new LibGit2Sharp.FetchOptions();

            LibGit2Sharp.MergeOptions mergeOptions = new LibGit2Sharp.MergeOptions
            {
                FailOnConflict = true,
                CommitOnSuccess = false
            };

            LibGit2Sharp.PullOptions pullOptions = new LibGit2Sharp.PullOptions
            {
                FetchOptions = fetchOptions,
                MergeOptions = mergeOptions
            };

            LibGit2Sharp.MergeResult result = LibGit2Sharp.Commands.Pull(repoIt.Repository, SIGNATURE, pullOptions);

            if (result.Status == LibGit2Sharp.MergeStatus.UpToDate)
            {
                resultState = "Up to date";
                return true;
            }
            else if (result.Status != LibGit2Sharp.MergeStatus.Conflicts)
            {
                resultState = "Pulled";
                return true;
            }
            else
            {
                resultState = "Failed";
                return true; // TODO: Hack to allow amend commits work
            }
        }

        static async Task<bool> AddDiffToPatch(LibGit2Sharp.Repository root, Patch patch)
        {
            AddUntrackedUnstagedFiles_R(root);

            var diffUnstagedTask = GetDiff_R(root, false);
            var diffStagedTask = GetDiff_R(root, true);

            List<Task<DiffResult[]>> remainingTasks = new List<Task<DiffResult[]>>
            {
                diffUnstagedTask,
                diffStagedTask
            };

            while (remainingTasks.Count > 0)
            {
                var finishedDiffTask = await Task.WhenAny(remainingTasks);
                remainingTasks.Remove(finishedDiffTask);

                bool staged = finishedDiffTask == diffStagedTask;
                Console.Out.WriteLine("Create {0} patch...", staged ? "staged" : "unstaged");

                foreach (var result in finishedDiffTask.Result)
                {
                    if (!result.Success)
                    {
                        Console.Out.WriteLine("{0,-15} [Failed]", result.RepositoryChain);
                        Console.Error.WriteLine(Helper.Indent(result.Error));
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(result.Diff))
                    {
                        Console.Out.WriteLine("{0,-15} [No changes detected]", result.RepositoryChain);
                        continue;
                    }

                    if (staged)
                    {
                        patch.DiffStaged += result.Diff;
                    }
                    else
                    {
                        patch.DiffUnstaged += result.Diff;
                    }

                    Console.Out.WriteLine("{0,-15} [Added]", result.RepositoryChain);
                }

                Console.Out.WriteLine();
            }

            return true;
        }

        static bool ApplyDiff_R(in LibGit2Sharp.Repository root, in string diff, in bool stage)
        {
            Console.Out.Write("Applying {0} patch...\t", stage ? "staged" : "unstaged");

            if (string.IsNullOrEmpty(diff))
            {
                Console.Out.WriteLine(" [No changes]");
                return true;
            }

            string tmpDiffFilePath = Path.GetTempFileName();

            using (var writer = File.CreateText(tmpDiffFilePath))
            {
                writer.NewLine = Defines.PATCH_NEW_LINE;
                writer.Write(diff);
            }

            int patchResult = GitHelper.CommandWrite(root.Info.WorkingDirectory, "apply " + tmpDiffFilePath, out string error, out string output);

            File.Delete(tmpDiffFilePath);

            if (patchResult != 0)
            {
                Console.Out.WriteLine(" [Failed]");
                Console.Error.WriteLine("Failed to apply patch:\n{0}", Helper.Indent(error));
                return false;
            }

            Console.Out.WriteLine(" [Done]");

            if (!string.IsNullOrEmpty(output))
            {
                Console.Out.WriteLine(output);
            }

            if (stage)
            {
                Stage_R(root);
            }

            return true;
        }

        private class DiffResult
        {
            public readonly string RepositoryChain = null;

            public bool Success = true;
            public string Error = null;
            public string Diff = null;

            public DiffResult(string repositoryChain)
            {
                this.RepositoryChain = repositoryChain;
            }
        }

        static bool AddUntrackedUnstagedFiles_R(in LibGit2Sharp.Repository root)
        {
            foreach (var it in new RepositoryIterator(root))
            {
                if (GitHelper.CommandWrite(it.Repository.Info.WorkingDirectory, "add -AN", out string error) != 0)
                {
                    Console.Error.WriteLine(error);
                    return false;
                }
            }

            return true;
        }

        static async Task<DiffResult[]> GetDiff_R(LibGit2Sharp.Repository root, bool staged)
        {
            return await Task.WhenAll(GetDiffTasks_R(root, staged));
        }

        static async Task<DiffResult> GetCombinedDiff_R(LibGit2Sharp.Repository root, bool staged)
        {
            List<Task<DiffResult>> tasks = GetDiffTasks_R(root, staged);

            DiffResult fullResult = new DiffResult(null);
            foreach (var task in tasks)
            {
                DiffResult result = await task;
                if (!result.Success)
                {
                    fullResult.Success = false;
                    fullResult.Error = result.Error;
                    return fullResult;
                }

                fullResult.Diff += result.Diff;
                fullResult.Success &= result.Success;
            }

            return fullResult;
        }

        static List<Task<DiffResult>> GetDiffTasks_R(in LibGit2Sharp.Repository root, bool staged)
        {
            List<Task<DiffResult>> tasks = new List<Task<DiffResult>>();
            foreach (var it in new RepositoryIterator(root))
            {
                string workingDir = it.Repository.Info.WorkingDirectory;
                string relativPath = it.RelativePath;

                tasks.Add(
                    Task.Run(() =>
                    {
                        DiffResult result = new DiffResult(it.Chain);

                        string relativeDiffPath = relativPath.Replace('\\', '/');
                        string diffCommand = string.Format("diff --binary --no-color --src-prefix=a/{0} --dst-prefix=b/{0}", relativeDiffPath);
                        if (staged)
                        {
                            diffCommand += " --staged";
                        }

                        result.Success = GitHelper.CommandRead(workingDir, diffCommand, out result.Error, out result.Diff) == 0;
                        return result;
                    })
                );
            }

            return tasks;
        }

        static bool Stash_R(in LibGit2Sharp.Repository root)
        {
            Console.Out.WriteLine("Stashing...");

            foreach (var it in new RepositoryIterator(root))
            {
                var stash = it.Repository.Stashes.Add(SIGNATURE, "Backup", LibGit2Sharp.StashModifiers.IncludeUntracked);
                // TODO: Handle stashing exception error (if there are any)

                Console.Out.WriteLine("{0,-15} [{1}]", it.Chain, stash != null ? "Stashed" : "Skipped");
            }

            return true;
        }

        static bool Stage_R(in LibGit2Sharp.Repository root)
        {
            foreach (var it in new RepositoryIterator(root))
            {
                if (GitHelper.CommandWrite(it.Repository.Info.WorkingDirectory, "add --all", out string error) != 0)
                {
                    Console.Error.WriteLine("Failed to stage patch ({0})", it.Chain);
                    Console.Error.WriteLine(Helper.Indent(error));
                    return false;
                }
            }

            return true;
        }
    }
}
