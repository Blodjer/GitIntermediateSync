using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

// TODO: Implement readable and writable wrapper?

namespace GitIntermediateSync
{
    class Patch
    {
        const string PATCH_EXTENSION         = ".patch";
        const char   PATCH_NAME_SEPERATOR    = '#';
        const string PATCH_NAME_FORMAT       = "{0}#{1}" + PATCH_EXTENSION; // 0 = repo identifier, 1 = timestamp

        public class HeadInfo
        {
            public string Sha { get; set; }
            public string RemoteBranchName { get; set; }
        };

        public Dictionary<string, HeadInfo> Heads { get; set; }
        public string DiffStaged { get; set; }
        public string DiffUnstaged { get; set; }

        private Patch()
        {
            Heads = new Dictionary<string, HeadInfo>();
        }

        // Hack to allow json to deserialize with a private constructor
        private class PatchDeserializable : Patch { public PatchDeserializable() { } }

        public static Patch New()
        {
            return new Patch();
        }

        public bool Serialize(in LibGit2Sharp.Repository rootRepository, in string outputDirectory, out string file)
        {
            file = null;

            if (!Directory.Exists(outputDirectory))
            {
                Console.Error.WriteLine("Could not find path {0}", outputDirectory);
                return false;
            }

            if (!GitHelper.GetRepositoryIdentifierName(rootRepository, out string repositoryIdentifier))
            {
                Console.Error.WriteLine("Could not get repository identifier name for {0}", rootRepository.Info.WorkingDirectory);
                return false;
            }

            string timestamp = DateTime.Now.ToFileTimeUtc().ToString();

            string patchName = string.Format(PATCH_NAME_FORMAT, repositoryIdentifier, timestamp);
            string patchFile = Path.Combine(outputDirectory, patchName);

            JsonSerializerOptions options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            string json = JsonSerializer.Serialize(this, options);

            using (var writer = File.CreateText(patchFile))
            {
                writer.Write(json);
                file = patchFile;
            }

            return true;
        }

        public static Patch FromFile(in string patchFile)
        {
            if (!File.Exists(patchFile))
            {
                return null;
            }

            string json = File.ReadAllText(patchFile, Defines.PATCH_ENCODER);

            Patch archive = JsonSerializer.Deserialize<PatchDeserializable>(json);

            return archive;
        }

        public static Patch FromPath(in LibGit2Sharp.Repository repository, in string syncPath, out DateTime timestamp)
        {
            if (!FindLatestPatchFile(repository, syncPath, out string patchFile, out timestamp))
            {
                return null;
            }

            return FromFile(patchFile);
        }

        private static bool FindLatestPatchFile(in LibGit2Sharp.Repository repository, in string syncPath, out string patchFile, out DateTime timestamp)
        {
            patchFile = null;
            timestamp = DateTime.MinValue;

            if (!GitHelper.GetRepositoryIdentifierName(repository, out string repositoryIdentifier))
            {
                return false;
            }

            string searchPattern = string.Format(PATCH_NAME_FORMAT, repositoryIdentifier, "*");
            string[] files = Directory.GetFiles(syncPath, searchPattern);

            string latestFile = string.Empty;
            DateTime latestTime = DateTime.MinValue;

            bool found = false;
            foreach (string file in files)
            {
                string fileName = Path.GetFileNameWithoutExtension(file);
                string[] fileNameParts = fileName.Split(PATCH_NAME_SEPERATOR);

                int shouldHavePartsNumber = PATCH_NAME_FORMAT.Split(PATCH_NAME_SEPERATOR).Length;
                if (fileNameParts.Length < shouldHavePartsNumber)
                {
                    continue;
                }

                if (!long.TryParse(fileNameParts[fileNameParts.Length - 1], out long fileTime))
                {
                    continue;
                }

                DateTime time = DateTime.FromFileTimeUtc(fileTime);
                if (time > latestTime)
                {
                    found = true;
                    latestTime = time;
                    latestFile = file;
                }
            }

            if (!found)
            {
                return false;
            }

            patchFile = latestFile;
            timestamp = latestTime;
            return true;
        }
    }
}
