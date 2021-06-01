using System;
using System.Diagnostics;
using System.IO;
using System.Text;

// TODO
// - Single file patch
// - Checkout commit and branch
// - Simplify code

namespace GitIntermediateSync
{
    class Program
    {
        static readonly string SYNC_SUB_PATH = Path.Combine("!sync", "Git");
        const string GIT_INSTALLER_MASK = "Git-*.exe";

        static readonly Encoding PATCH_ENCODER = new UTF8Encoding(false);
        const string PATCH_NAME_FORMAT = "{0}.{1}.{2}.patch"; // TODO: Use this 0 = repo name, 1 = timestamp, 2 = staged/unstaged

        static int Main(string[] args)
        {
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
                Console.Error.WriteLine("No operation provided!");
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
                    Console.Error.WriteLine("Sync directory not found! " + syncPath);
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
                Console.Error.WriteLine("Running " + gitInstallExe);

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
            string repoName;
            if (!GetGitRepoIdentifierName(repositoryPath, out repoName))
            {
                Console.Error.WriteLine("\tUnable to retrieve origin name!");
                return false;
            }

            string addOutput;
            if (SimpleGitCommand(repositoryPath, "add -AN", out addOutput) != 0)
            {
                Console.Error.WriteLine(addOutput);
                return false;
            }

            string timestamp = DateTime.Now.ToFileTimeUtc().ToString();
            string fileNameUnstaged = repoName + "." + timestamp + ".unstaged.patch";
            string fileNameStaged = repoName + "." + timestamp + ".staged.patch";
            string patchFileUnstaged = Path.Combine(syncPath, fileNameUnstaged);
            string patchFileStaged = Path.Combine(syncPath, fileNameStaged);

            string t1 = Path.Combine(Path.GetTempPath(), fileNameUnstaged + ".tmp");
            string t2 = Path.Combine(Path.GetTempPath(), fileNameStaged + ".tmp");

            File.CreateText(t1).Dispose();
            File.CreateText(t2).Dispose();

            bool success = true;

            // TODO: Use stream as input instead of filename
            if (success)
            {
                Console.Out.WriteLine("Create unstaged patch...");
                success &= AddToPatch(repositoryPath, repositoryPath, t1, false);
            }
            if (success)
            {
                Console.Out.WriteLine("Create staged patch...");
                success &= AddToPatch(repositoryPath, repositoryPath, t2, true);
            }

            if (!success)
            {
                File.Delete(t1);
                File.Delete(t2);
                Console.Error.WriteLine("Failed to create patch!");
                return false;
            }

            File.Move(t1, patchFileUnstaged);
            File.Move(t2, patchFileStaged);

            Console.Out.WriteLine();
            Console.Out.WriteLine("Created patch " + patchFileUnstaged);
            Console.Out.WriteLine("Created patch " + patchFileStaged);

            return true;
        }

        static bool AddToPatch(in string repoPath, in string rootRepoPath, in string patchFile, in bool staged)
        {
            using (var repo = new LibGit2Sharp.Repository(repoPath))
            {
                Console.Out.Write("\t" + repoPath);

                LibGit2Sharp.StatusOptions options = new LibGit2Sharp.StatusOptions();
                LibGit2Sharp.RepositoryStatus status = repo.RetrieveStatus(options);

                if (!status.IsDirty) // TODO: Can it be dirty while having changes in submodules?
                {
                    Console.Out.WriteLine(" [No changes detected]");
                    return true;
                }

                Console.Out.WriteLine(" [Added]");

                string relativePatchPath = Helper.GetRelativePath(rootRepoPath, repoPath).Replace('\\', '/');
                string diffCommand = "diff --binary --no-color --src-prefix=a/" + relativePatchPath + " --dst-prefix=b/" + relativePatchPath;
                if (staged)
                {
                    diffCommand += " --staged";
                }

                string diffContent;
                if (SimpleGitCommand(repoPath, diffCommand, out diffContent) != 0)
                {
                    Console.Error.WriteLine("\t\tDiff failed!\n");
                    Console.Error.WriteLine(diffContent);
                    return false;
                }

                using (FileStream patchFileStream = new FileStream(patchFile, FileMode.Append))
                {
                    using (StreamWriter patchWriter = new StreamWriter(patchFileStream, PATCH_ENCODER))
                    {
                        patchWriter.NewLine = "\n";

                        patchWriter.Write(diffContent);
                    }
                }

                foreach (var sub in repo.Submodules)
                {
                    string subPath = Path.Combine(repoPath, sub.Path);
                    if (!LibGit2Sharp.Repository.IsValid(subPath))
                    {
                        Console.Error.WriteLine("\t\tSubmodule not valid " + subPath);
                        continue;
                    }

                    if (!AddToPatch(subPath, rootRepoPath, patchFile, staged))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        static bool Op_ApplyPatch(in string repoPath, in string syncPath)
        {
            string repoName;
            if (!GetGitRepoIdentifierName(repoPath, out repoName))
            {
                Console.Error.WriteLine("Unable to retrieve origin name!");
                return false;
            }

            string[] files = Directory.GetFiles(syncPath, repoName + ".*.staged.patch");

            bool foundLatestPatch = false;
            string latestPatchFile = string.Empty;
            DateTime latestPatchTime = DateTime.MinValue;
            foreach (string file in files)
            {
                string patchName = Path.GetFileName(file);
                string timestamp = patchName.Replace(".staged.patch", "").Replace(repoName + ".", "");

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
                Console.Error.WriteLine("Could not find patch for repository " + repoName);
                return false;
            }

            string stagedPatchFile = latestPatchFile;
            string unstagePatchFile = latestPatchFile.Replace(".staged.patch", ".unstaged.patch");
            if (!File.Exists(stagedPatchFile) || !File.Exists(unstagePatchFile))
            {
                Console.Error.WriteLine("Could not find patch for repository " + repoName);
                return false;
            }

            TimeSpan latestPatchTimeSpan = DateTime.Now - latestPatchTime.ToLocalTime();
            Console.Out.WriteLine("Latest patch is from " + Helper.ToReadableString(latestPatchTimeSpan) + " ago (" + latestPatchTime.ToLocalTime().ToString() + ")");
            Console.Out.WriteLine();

            Console.Out.WriteLine("Stashing...");
            if (!StashRecursive(repoPath))
            {
                Console.Error.WriteLine("Stashing failed!");
                return false;
            }
            Console.Out.WriteLine();

            Console.Out.WriteLine("Applying patches...\n");
            if (!ApplyPatch(repoPath, stagedPatchFile, true) ||
                !ApplyPatch(repoPath, unstagePatchFile, false))
            {
                return false;
            }

            return true;
        }

        static bool StashRecursive(string repoPath)
        {
            using (var repo = new LibGit2Sharp.Repository(repoPath))
            {
                var signature = new LibGit2Sharp.Signature("GitIntermediateSync", "invalid", DateTimeOffset.UtcNow);
                var stash = repo.Stashes.Add(signature, "Backup", LibGit2Sharp.StashModifiers.IncludeUntracked);
                if (stash != null)
                {
                    Console.Out.WriteLine("\t" + repoPath);
                }
                // TODO: Handle stashing exception error (if there are any)

                foreach (var sub in repo.Submodules)
                {
                    string subPath = Path.Combine(repoPath, sub.Path);
                    if (!LibGit2Sharp.Repository.IsValid(subPath))
                    {
                        Console.Error.WriteLine("\tSubmodule not valid " + subPath);
                        continue;
                    }
                    
                    if (!StashRecursive(subPath))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        static bool ApplyPatch(string repoPath, string patchFile, bool stage)
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
                Console.Error.WriteLine("\nFailed to apply patch!");
                Console.Error.WriteLine(outputApply);
                return false;
            }
            Console.Out.WriteLine();

            if (stage)
            {
                StageRecursive(repoPath);
            }

            return true;
        }

        static bool StageRecursive(string repoPath)
        {
            using (var repo = new LibGit2Sharp.Repository(repoPath))
            {
                string outputStage;
                if (SimpleGitCommand(repoPath, "add --all", out outputStage) != 0)
                {
                    Console.Error.WriteLine("\nFailed to stage patch!");
                    Console.Error.WriteLine(outputStage);
                    return false;
                }

                // TODO: Handle stashing exception error (if there are any)

                foreach (var sub in repo.Submodules)
                {
                    string subPath = Path.Combine(repoPath, sub.Path);
                    if (!LibGit2Sharp.Repository.IsValid(subPath))
                    {
                        continue;
                    }

                    StageRecursive(subPath);
                }
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

            var remoteOriginUrl = repo.Config.Get<string>("remote.origin.url");
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
    }
}
