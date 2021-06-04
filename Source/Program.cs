using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

// TODO
// - Single file patch
// - Checkout commit and branch
//      - Warn if commit is not pushed yet
// - Simplify code
// - Cleanup

namespace GitIntermediateSync
{
    class Program
    {
        static readonly string SYNC_SUB_PATH = Path.Combine("!sync", "Git");
        const string GIT_INSTALLER_MASK = "Git-*.exe";
        const string REMOTE_DEFAULT_NAME = "origin";

        static readonly LibGit2Sharp.Signature SIGNATURE = new LibGit2Sharp.Signature("GitIntermediateSync", "(no email)", DateTimeOffset.UtcNow); // TODO: Replace by getter

        static readonly Encoding PATCH_ENCODER = new UTF8Encoding(false);
        static readonly string PATCH_NEW_LINE = "\n";
        const string PATCH_NAME_FORMAT = "{0}.{1}.{2}.patch"; // TODO: Use this 0 = repo name, 1 = timestamp, 2 = staged/unstaged

        static int Main(string[] args)
        {
            // TODO: Only needs to apply to proccess start info?
            Console.OutputEncoding = PATCH_ENCODER; // necessary to output the patch with the correct encoding

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

            Operation operation;
            OperationInfo operationInfo;
            if (!OperationCommands.TryGetOperation(args[0], out operation, out operationInfo))
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

            string repoPath;
            {
                if (args.Length > 1)
                {
                    repoPath = args[1];
                }
                else
                {
                    repoPath = Directory.GetCurrentDirectory();
                }

                if (!Directory.Exists(repoPath))
                {
                    Console.Error.WriteLine("Repository directory not found!");
                    return false;
                }

                if (!LibGit2Sharp.Repository.IsValid(repoPath))
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
            switch (operation)
            {
                case Operation.Save:
                    operationSuccess = Op_MakePatch(repoPath, syncPath);
                    break;
                case Operation.Apply:
                    operationSuccess = Op_ApplyPatch(repoPath, syncPath);
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
            if (Helper.CheckGit())
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

                if (!Helper.CheckGit())
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

        static bool Op_MakePatch(in string repositoryPath, in string syncPath)
        {
            // TODO: Update status

            string repoName;
            if (!GetGitRepoIdentifierName(repositoryPath, out repoName))
            {
                Console.Error.WriteLine("\tUnable to retrieve origin name!");
                return false;
            }

            // TODO: Check for idle repo

            string addOutput;
            if (SimpleGitCommand(repositoryPath, "add -AN", out addOutput) != 0)
            {
                Console.Error.WriteLine(addOutput);
                return false;
            }

            string timestamp = DateTime.Now.ToFileTimeUtc().ToString();

            string fileNameInfo = repoName + "." + timestamp + ".info.patch";
            string fileNameUnstaged = repoName + "." + timestamp + ".unstaged.patch";
            string fileNameStaged = repoName + "." + timestamp + ".staged.patch";

            string patchFileInfo = Path.Combine(syncPath, fileNameInfo);
            string patchFileUnstaged = Path.Combine(syncPath, fileNameUnstaged);
            string patchFileStaged = Path.Combine(syncPath, fileNameStaged);

            string tmpPath = Path.GetTempPath();

            string fInfo = Path.Combine(tmpPath, fileNameInfo + ".tmp");
            string fPatchUnstaged = Path.Combine(tmpPath, fileNameUnstaged + ".tmp");
            string fPatchStaged = Path.Combine(tmpPath, fileNameStaged + ".tmp");

            File.CreateText(fInfo).Dispose();
            File.CreateText(fPatchUnstaged).Dispose();
            File.CreateText(fPatchStaged).Dispose();

            // Writing patch
            {
                bool success = true;

                if (success)
                {
                    success &= WritePatchInfo(repositoryPath, fInfo);
                }
                if (success)
                {
                    Console.Out.WriteLine("Create unstaged patch...");
                    success &= WritePatchDiff(repositoryPath, fPatchUnstaged, false);
                }
                if (success)
                {
                    Console.Out.WriteLine("Create staged patch...");
                    success &= WritePatchDiff(repositoryPath, fPatchStaged, true);
                }

                if (!success)
                {
                    File.Delete(fInfo);
                    File.Delete(fPatchUnstaged);
                    File.Delete(fPatchStaged);
                    return false;
                }
            }

            File.Move(fInfo, patchFileInfo);
            File.Move(fPatchUnstaged, patchFileUnstaged);
            File.Move(fPatchStaged, patchFileStaged);

            Console.Out.WriteLine("Created info  {0}", patchFileInfo);
            Console.Out.WriteLine("Created patch {0}", patchFileUnstaged);
            Console.Out.WriteLine("Created patch {0}", patchFileStaged);
            Console.Out.WriteLine();

            return true;
        }

        static bool WritePatchDiff(in string repoRootPath, in string patchFile, in bool staged)
        {
            using (FileStream patchFileStream = new FileStream(patchFile, FileMode.Append))
            using (StreamWriter patchWriter = new StreamWriter(patchFileStream, PATCH_ENCODER))
            {
                patchWriter.NewLine = PATCH_NEW_LINE;

                foreach (var it in new RepositoryIterator(repoRootPath))
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

                    string diffContent;
                    if (SimpleGitCommand(it.Repository.Info.WorkingDirectory, diffCommand, out diffContent) != 0)
                    {
                        Console.Error.WriteLine("{0,-15} [Failed]", it.Chain);
                        Console.Error.WriteLine(diffContent);
                        return false;
                    }

                    if (string.IsNullOrEmpty(diffContent))
                    {
                        Console.Out.WriteLine("{0,-15} [No changes detected]", it.Chain);
                        continue;
                    }

                    Console.Out.WriteLine("{0,-15} [Added]", it.Chain);
                    patchWriter.Write(diffContent);
                }
            }

            Console.Out.WriteLine();

            return true;
        }

        static bool Op_ApplyPatch(in string repoPath, in string syncPath)
        {
            // TODO: Update status

            string repoName;
            if (!GetGitRepoIdentifierName(repoPath, out repoName))
            {
                Console.Error.WriteLine("Unable to retrieve origin name!", repoPath);
                return false;
            }

            // TODO: Check for idle repo

            string[] files = Directory.GetFiles(syncPath, repoName + ".*.info.patch");

            string latestPatchFile = string.Empty;
            DateTime latestPatchTime = DateTime.MinValue;

            {
                bool foundLatestPatch = false;
                foreach (string file in files)
                {
                    string patchName = Path.GetFileName(file);
                    string timestamp = patchName.Replace(".info.patch", "").Replace(repoName + ".", "");

                    long fileTime;
                    if (!long.TryParse(timestamp, out fileTime))
                    {
                        continue;
                    }

                    DateTime time = DateTime.FromFileTimeUtc(fileTime);
                    if (time > latestPatchTime)
                    {
                        foundLatestPatch = true;
                        latestPatchTime = time;
                        latestPatchFile = file;
                    }
                }

                if (!foundLatestPatch)
                {
                    Console.Error.WriteLine("Could not find any patch for repository ({0})", repoName);
                    return false;
                }
            }

            string infoPatchFile = latestPatchFile;
            string stagedPatchFile = latestPatchFile.Replace(".info.patch", ".staged.patch");
            string unstagePatchFile = latestPatchFile.Replace(".info.patch", ".unstaged.patch");
            if (!File.Exists(infoPatchFile) || !File.Exists(stagedPatchFile) || !File.Exists(unstagePatchFile))
            {
                Console.Error.WriteLine("Could not find all required patches for repository ({0})", repoName);
                return false;
            }

            TimeSpan latestPatchTimeSpan = DateTime.Now - latestPatchTime.ToLocalTime();
            Console.Out.WriteLine("Latest patch is from {0} ago ({1})", Helper.ToReadableString(latestPatchTimeSpan), latestPatchTime.ToLocalTime().ToString());
            Console.Out.WriteLine();

            // Apply latest patch

            if (!StashRecursive(repoPath))
            {
                return false;
            }
            Console.Out.WriteLine();

            if (!ApplyPatchHeads(repoPath, infoPatchFile))
            {
                return false;
            }
            Console.Out.WriteLine();

            Console.Out.WriteLine("Applying patches...");
            if (!ApplyPatch(repoPath, stagedPatchFile, true) ||
                !ApplyPatch(repoPath, unstagePatchFile, false))
            {
                return false;
            }
            Console.Out.WriteLine();

            return true;
        }

        private static bool WritePatchInfo(in string repositoryPath, in string infoFile)
        {
            using (FileStream infoFileStream = new FileStream(infoFile, FileMode.Append))
            using (StreamWriter infoWriter = new StreamWriter(infoFileStream, PATCH_ENCODER))
            {
                infoWriter.NewLine = PATCH_NEW_LINE;

                foreach (var it in new RepositoryIterator(repositoryPath))
                {
                    var repo = it.Repository;

                    // TODO: Prevent patch if there are commits made in detached head state

                    if (repo.Head.TrackingDetails.AheadBy != null && repo.Head.TrackingDetails.AheadBy != 0)
                    {
                        Console.Error.WriteLine("Cannot create patch if there are uncommited changes ({0})", it.Chain);
                        return false;
                    }

                    string headCommit = repo.Head.Tip.Sha;
                    string branchName = string.Empty;

                    if (repo.Info.IsHeadDetached)
                    {
                        
                    }
                    else if (repo.Head.IsTracking && repo.Head.TrackedBranch.IsRemote)
                    {
                        LibGit2Sharp.Branch remoteBranch = repo.Head.TrackedBranch;

                        string remoteBranchName = remoteBranch.CanonicalName;
                        string prefixToRemove = "refs/remotes/" + remoteBranch.RemoteName + "/";
                        if (!remoteBranchName.StartsWith(prefixToRemove))
                        {
                            Console.Error.WriteLine("Unexpected remote branch canonical name ({0}: {1})", it.Chain, remoteBranch.CanonicalName);
                            return false;
                        }

                        branchName = remoteBranchName.Remove(0, prefixToRemove.Length);
                        //remoteUrl = repo.Network.Remotes[remoteBranch.RemoteName].Url;
                    }
                    else
                    {
                        if (!repo.Info.IsHeadDetached && !repo.Head.IsTracking)
                        {
                            Console.Error.WriteLine("Cannot create patch for local branch ({0}: {1})", it.Chain, repo.Head.CanonicalName);
                        }
                        else
                        {
                            Console.Error.WriteLine("Unknown HEAD location ({0}: {1})", it.Chain, repo.Head.CanonicalName);
                        }

                        return false;
                    }

                    string line = string.Format("{0} {1} {2}", it.Chain, headCommit, branchName).Trim();
                    infoWriter.WriteLine(line);
                };
            }

            return true;
        }

        static bool ApplyPatchHeads(in string repoPath, in string infoFile)
        {
            Console.Out.WriteLine("Apply patch HEADs...");

            Dictionary<string, string[]> d = new Dictionary<string, string[]>();

            using (FileStream infoFileStream = new FileStream(infoFile, FileMode.Open))
            using (StreamReader infoFileReader = new StreamReader(infoFileStream, PATCH_ENCODER))
            {
                while (!infoFileReader.EndOfStream)
                {
                    string line = infoFileReader.ReadLine().Trim();
                    string[] pair = line.Split(new[] { ' ' }, 2);
                    if (pair.Length < 2)
                    {
                        Console.Error.WriteLine("Unexpected line in info file ({0})", infoFile);
                        return false;
                    }

                    string[] values = pair[1].Split(' ');
                    if (values.Length < 1)
                    {
                        Console.Error.WriteLine("Unexpected values in info file ({0})", infoFile);
                        return false;
                    }

                    d.Add(pair[0], values);
                }
            }

            foreach (var it in new RepositoryIterator(repoPath))
            {
                // INFO: We expect the repository to have a remote that is called origin

                var originRemote = it.Repository.Network.Remotes[REMOTE_DEFAULT_NAME];
                if (originRemote == null)
                {
                    Console.Error.WriteLine("Could not find default remote origin ({0})", it.Repository.Info.WorkingDirectory);
                    return false;
                }

                List<string> fetchRefSpecs = new List<string>();
                string remoteRefDestinationBase = string.Empty;
                foreach (var refSpec in originRemote.FetchRefSpecs)
                {
                    fetchRefSpecs.Add(refSpec.Specification);
                    remoteRefDestinationBase = refSpec.Destination;
                }

                if (fetchRefSpecs.Count != 1)
                {
                    Console.Error.WriteLine("Unexpected fetch ref specs ({0})", originRemote.Name);
                    return false;
                }
                
                it.Repository.Network.Fetch(originRemote.Name, fetchRefSpecs);

                if (d.TryGetValue(it.Chain, out string[] infos))
                {
                    var options = new LibGit2Sharp.CheckoutOptions();
                    options.CheckoutModifiers = LibGit2Sharp.CheckoutModifiers.Force;

                    const string formatCheckout = "{0,-15} -> {1}";

                    if (infos.Length == 1) // Checkout detached HEAD
                    {
                        var commit = it.Repository.Lookup(infos[0], LibGit2Sharp.ObjectType.Commit) as LibGit2Sharp.Commit;
                        if (commit != null)
                        {
                            LibGit2Sharp.Commands.Checkout(it.Repository, commit, options);
                            Console.Out.WriteLine(formatCheckout, it.Chain, commit.Sha);
                            continue;
                        }
                    }
                    else if (infos.Length == 2) // Checkout branch
                    {
                        // TODO: Handle branch with different local name

                        string remoteBranchName = infos[1];
                        string remoteBranchRef = remoteRefDestinationBase.Replace("*", remoteBranchName);
                        var remoteBranch = it.Repository.Branches[remoteBranchRef];
                        if (remoteBranch == null)
                        {
                            Console.Error.WriteLine("Could not find remote branch ({0})", remoteBranchName);
                            return false;
                        }

                        LibGit2Sharp.Branch localBranch = null;
                        foreach (var branch in it.Repository.Branches)
                        {
                            if (!branch.IsRemote && branch.IsTracking && branch.TrackedBranch == remoteBranch)
                            {
                                localBranch = branch;
                                break;
                            }
                        }

                        if (localBranch == null)
                        {
                            LibGit2Sharp.Branch newBranch = it.Repository.Branches.Add(remoteBranchName, remoteBranch.Tip, false);
                            localBranch = it.Repository.Branches.Update(newBranch, b => b.TrackedBranch = remoteBranch.CanonicalName);
                        }

                        if (localBranch == null)
                        {
                            Console.Error.WriteLine("Failed to get local branch for remote branch ({0})", remoteBranchName);
                            return false;
                        }

                        string checkoutState = string.Empty;

                        localBranch = LibGit2Sharp.Commands.Checkout(it.Repository, localBranch, options);
                        if (!PullCurrentBranch(it, out checkoutState))
                        {
                            return false;
                        }

                        if (localBranch.Tip.Sha != infos[0])
                        {
                            var commit = it.Repository.Lookup(infos[0], LibGit2Sharp.ObjectType.Commit) as LibGit2Sharp.Commit;
                            if (commit != null)
                            {
                                LibGit2Sharp.Commands.Checkout(it.Repository, commit, options);
                                checkoutState = "Detached";
                            }
                        }

                        Console.Out.WriteLine(formatCheckout + " [{2}]", it.Chain, localBranch.CanonicalName, checkoutState);
                        continue;
                    }

                    Console.Error.WriteLine("Invalid values in info file ({0})", infoFile);
                    return false;
                }
            }

            return true;
        }

        private static bool PullCurrentBranch(in RepositoryIterator.Result repoIt, out string resultState)
        {
            LibGit2Sharp.FetchOptions fetchOptions = new LibGit2Sharp.FetchOptions();

            LibGit2Sharp.MergeOptions mergeOptions = new LibGit2Sharp.MergeOptions();
            mergeOptions.FailOnConflict = true;
            mergeOptions.CommitOnSuccess = false;

            LibGit2Sharp.PullOptions pullOptions = new LibGit2Sharp.PullOptions();
            pullOptions.FetchOptions = fetchOptions;
            pullOptions.MergeOptions = mergeOptions;

            LibGit2Sharp.MergeResult result = LibGit2Sharp.Commands.Pull(repoIt.Repository, SIGNATURE, pullOptions);

            if (result.Status == LibGit2Sharp.MergeStatus.UpToDate)
            {
                resultState = "Up to date";
                return true;
            }
            else if (result.Status == LibGit2Sharp.MergeStatus.FastForward)
            {
                resultState = "Pulled";
                return true;
            }
            else
            {
                resultState = "Failed";
                return false;
            }
        }

        static bool ApplyPatch(in string repoPath, in string patchFile, in bool stage)
        {
            Console.Out.WriteLine("Applying patch " + patchFile);

            FileInfo patchFileInfo = new FileInfo(patchFile);
            if (patchFileInfo.Length <= 1)
            {
                return true;
            }

            string outputApply;
            if (SimpleGitCommand(repoPath, "apply " + patchFile, out outputApply) != 0)
            {
                Console.Error.WriteLine("Failed to apply patch:");
                Console.Error.WriteLine(outputApply);
                return false;
            }

            if (stage)
            {
                StageRecursive(repoPath);
            }

            return true;
        }

        static bool StashRecursive(string repoRootPath)
        {
            Console.Out.WriteLine("Stashing...");

            foreach (var it in new RepositoryIterator(repoRootPath))
            {
                var stash = it.Repository.Stashes.Add(SIGNATURE, "Backup", LibGit2Sharp.StashModifiers.IncludeUntracked);

                Console.Out.WriteLine("{0,-15} [{1}]", it.Chain, stash != null ? "Stashed" : "Skipped");
                // TODO: Handle stashing exception error (if there are any)
            }

            return true;
        }

        static bool StageRecursive(string repoRootPath)
        {
            foreach (var it in new RepositoryIterator(repoRootPath))
            {
                string outputStage;
                if (SimpleGitCommand(it.Repository.Info.WorkingDirectory, "add --all", out outputStage) != 0)
                {
                    Console.Error.WriteLine("Failed to stage patch ({0})", it.Chain);
                    Console.Error.WriteLine(outputStage);
                    return false;
                }
                // TODO: Handle stashing exception error (if there are any)
            }

            return true;
        }

        static int SimpleGitCommand(in string workingDir, in string command, out string output)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = "git";
            startInfo.Arguments = command;
            startInfo.WorkingDirectory = workingDir;
            startInfo.RedirectStandardOutput = true;
            startInfo.UseShellExecute = false;

            startInfo.CreateNoWindow = false;
            startInfo.WindowStyle = ProcessWindowStyle.Minimized;

            using (Process p = Process.Start(startInfo))
            {
                // TODO: Handle/Indent error output

                output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        static bool GetGitRepoIdentifierName(in string repoPath, out string repoName)
        {
            using (var repo = new LibGit2Sharp.Repository(repoPath))
            {
                return GetGitRepoIdentifierName(repo, out repoName);
            }
        }

        static bool GetGitRepoIdentifierName(in LibGit2Sharp.Repository repo, out string repoName)
        {
            repoName = string.Empty;

            // TODO: Handle non-default origin names
            string remoteOriginUrlKey = string.Format("remote.{0}.url", REMOTE_DEFAULT_NAME);
            var remoteOriginUrl = repo.Config.Get<string>(remoteOriginUrlKey);
            if (remoteOriginUrl == null)
            {
                return false;
            }

            string origin = remoteOriginUrl.Value;
            
            string gitUrlEnding = ".git";
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

            repoName = origin;
            return true;
        }

        public class RepositoryIterator : IEnumerable<RepositoryIterator.Result>
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

            private string RootRepoPath;
            private string CurrentRepoPath;
            private string CurrentRepoChain;

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
                    if (!GetGitRepoIdentifierName(repo, out string repoName))
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
}
