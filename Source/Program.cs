using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

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
        static readonly string SYNC_SUB_PATH = Path.Combine("!sync", "Git");
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
                string oneDrive = Environment.GetEnvironmentVariable("OneDriveConsumer");
                syncPath = Path.Combine(oneDrive, SYNC_SUB_PATH);
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

            if (operationInfo.critical)
            {
                if (!Helper.ShowConfirmationMessage(string.Format("<{0}> is a critical operation. Do you want to continue?", operationInfo.command)))
                {
                    Console.Error.WriteLine("OPERATION ABORTED");
                    return false;
                }
            }

            bool operationSuccess = false;
            switch (operationInfo.operation)
            {
                case Operation.Save:
                    operationSuccess = Op_MakePatch(repositoryPath, syncPath);
                    break;
                case Operation.Apply:
                    operationSuccess = Op_ApplyPatch(repositoryPath, syncPath);
                    break;
            }

            if (operationSuccess)
            {
                Console.Out.WriteLine("OPERATION COMPLETED");
                return false;
            }
            else
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine("OPERATION FAILED");
                return false;
            }
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

        static bool CheckRepositoryReady(in LibGit2Sharp.Repository rootRepository)
        {
            foreach (var it in new RepositoryIterator(rootRepository))
            {
                if (it.Repository.Info.CurrentOperation != LibGit2Sharp.CurrentOperation.None)
                {
                    Console.Error.WriteLine("Repository is currently in operation ({0})", it.Chain);
                    return false;
                }
            }

            return true;
        }

        static bool Op_MakePatch(in string rootRepositoryPath, in string syncPath)
        {
            using (var repository = new LibGit2Sharp.Repository(rootRepositoryPath))
            {
                if (!CheckRepositoryReady(repository))
                {
                    return false;
                }

                foreach (var it in new RepositoryIterator(repository))
                {
                    if (GitHelper.Command(it.Repository.Info.WorkingDirectory, "add -AN", out string error) != 0)
                    {
                        Console.Error.WriteLine(Helper.Indent(error));
                        return false;
                    }
                }

                Patch patch = Patch.New();

                // Writing to patch
                {
                    bool success = true;

                    if (success)
                    {
                        success &= WritePatchHeads(repository, patch);
                    }
                    if (success)
                    {
                        Console.Out.WriteLine("Create unstaged patch...");
                        success &= WritePatchDiff(repository, patch, false);
                    }
                    if (success)
                    {
                        Console.Out.WriteLine("Create staged patch...");
                        success &= WritePatchDiff(repository, patch, true);
                    }

                    if (!success)
                    {
                        return false;
                    }
                }

                if (patch.Serialize(repository, syncPath, out string file))
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

        static bool WritePatchDiff(in LibGit2Sharp.Repository rootRepository, in Patch patch, in bool staged)
        {
            foreach (var it in new RepositoryIterator(rootRepository))
            {
                LibGit2Sharp.StatusOptions options = new LibGit2Sharp.StatusOptions();
                LibGit2Sharp.RepositoryStatus status = it.Repository.RetrieveStatus(options);

                if (!status.IsDirty)
                {
                    Console.Out.WriteLine("{0,-15} [No changes detected]", it.Chain);
                    continue;
                }

                string relativePatchPath = it.RelativePath.Replace('\\', '/');
                string diffCommand = string.Format("diff --binary --no-color --src-prefix=a/{0} --dst-prefix=b/{0}", relativePatchPath);
                if (staged)
                {
                    diffCommand += " --staged";
                }

                if (GitHelper.Command(it.Repository.Info.WorkingDirectory, diffCommand, out string error, out string output) != 0)
                {
                    Console.Error.WriteLine("{0,-15} [Failed]", it.Chain);
                    Console.Error.WriteLine(Helper.Indent(error));
                    return false;
                }

                if (string.IsNullOrEmpty(output))
                {
                    Console.Out.WriteLine("{0,-15} [No changes detected]", it.Chain);
                    continue;
                }

                if (staged)
                {
                    patch.DiffStaged += output;
                }
                else
                {
                    patch.DiffUnstaged += output;
                }

                Console.Out.WriteLine("{0,-15} [Added]", it.Chain);
            }

            Console.Out.WriteLine();

            return true;
        }

        static bool Op_ApplyPatch(in string rootRepositoryPath, in string syncPath)
        {
            using (var repository = new LibGit2Sharp.Repository(rootRepositoryPath))
            {
                if (!CheckRepositoryReady(repository))
                {
                    return false;
                }

                Patch patch = Patch.FromPath(repository, syncPath, out DateTime timestamp);
                if (patch == null)
                {
                    Console.Error.WriteLine("Could not find patch in {0}", syncPath);
                    return false;
                }

                TimeSpan patchAge = DateTime.Now - timestamp.ToLocalTime();
                Console.Out.WriteLine("Latest patch is from {0} ago ({1})", Helper.ToReadableString(patchAge), timestamp.ToLocalTime().ToString());
                Console.Out.WriteLine();

                // Apply latest patch

                if (!Stash(repository, true))
                {
                    return false;
                }
                Console.Out.WriteLine();

                if (!ApplyPatchHeads(repository, patch.Heads))
                {
                    return false;
                }
                Console.Out.WriteLine();

                if (!ApplyPatch(repository, patch.DiffStaged, true) ||
                    !ApplyPatch(repository, patch.DiffUnstaged, false))
                {
                    return false;
                }
                Console.Out.WriteLine();

                return true;
            }
        }

        static bool WritePatchHeads(in LibGit2Sharp.Repository rootRepository, in Patch patch)
        {
            foreach (var it in new RepositoryIterator(rootRepository))
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
                    //remoteUrl = repo.Network.Remotes[remoteBranch.RemoteName].Url;
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

        static bool ApplyPatchHeads(in LibGit2Sharp.Repository rootRepository, in Dictionary<string, Patch.HeadInfo> heads)
        {
            Console.Out.WriteLine("Apply patch HEADs...");

            foreach (var it in new RepositoryIterator(rootRepository))
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

        static bool ApplyPatch(in LibGit2Sharp.Repository rootRepository, in string patchContent, in bool stage)
        {
            Console.Out.Write("Applying {0} patch...\t", stage ? "staged" : "unstaged");

            if (string.IsNullOrEmpty(patchContent))
            {
                Console.Out.WriteLine(" [No changes]");
                return true;
            }

            string tmpPatchFilePath = Path.GetTempFileName();

            using (var writer = File.CreateText(tmpPatchFilePath))
            {
                writer.NewLine = Defines.PATCH_NEW_LINE;
                writer.Write(patchContent);
            }

            int patchResult = GitHelper.Command(rootRepository.Info.WorkingDirectory, "apply " + tmpPatchFilePath, out string error, out string output);

            File.Delete(tmpPatchFilePath);

            if (patchResult != 0)
            {
                Console.Out.WriteLine(" [Failed]");
                Console.Error.WriteLine("Failed to apply patch:");
                Console.Error.WriteLine(Helper.Indent(error));
                return false;
            }

            Console.Out.WriteLine(" [Done]");

            if (!string.IsNullOrEmpty(output))
            {
                Console.Out.WriteLine(output);
            }

            if (stage)
            {
                Stage(rootRepository, true);
            }

            return true;
        }

        static bool Stash(in LibGit2Sharp.Repository repository, in bool recursive)
        {
            Console.Out.WriteLine("Stashing...");

            foreach (var it in new RepositoryIterator(repository))
            {
                var stash = it.Repository.Stashes.Add(SIGNATURE, "Backup", LibGit2Sharp.StashModifiers.IncludeUntracked);
                // TODO: Handle stashing exception error (if there are any)

                Console.Out.WriteLine("{0,-15} [{1}]", it.Chain, stash != null ? "Stashed" : "Skipped");

                if (!recursive)
                {
                    return true;
                }
            }

            return true;
        }

        static bool Stage(in LibGit2Sharp.Repository repository, in bool recursive)
        {
            foreach (var it in new RepositoryIterator(repository))
            {
                if (GitHelper.Command(it.Repository.Info.WorkingDirectory, "add --all", out string error) != 0)
                {
                    Console.Error.WriteLine("Failed to stage patch ({0})", it.Chain);
                    Console.Error.WriteLine(Helper.Indent(error));
                    return false;
                }

                if (!recursive)
                {
                    return true;
                }
            }

            return true;
        }
    }
}
