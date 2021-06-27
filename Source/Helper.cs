using System;
using System.IO;

namespace GitIntermediateSync
{
    abstract class Helper
    {
        public static string GetRelativePath(string fromPath, string toPath)
        {
            if (string.IsNullOrEmpty(fromPath))
            {
                throw new ArgumentNullException("fromPath");
            }

            if (string.IsNullOrEmpty(toPath))
            {
                throw new ArgumentNullException("toPath");
            }

            Uri fromUri = new Uri(AppendDirectorySeparatorChar(fromPath));
            Uri toUri = new Uri(AppendDirectorySeparatorChar(toPath));

            if (fromUri.Scheme != toUri.Scheme)
            {
                return toPath;
            }

            Uri relativeUri = fromUri.MakeRelativeUri(toUri);
            string relativePath = Uri.UnescapeDataString(relativeUri.ToString());

            if (string.Equals(toUri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            {
                relativePath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            }

            return relativePath;
        }

        private static string AppendDirectorySeparatorChar(string path)
        {
            // Append a slash only if the path is a directory and does not have a slash.
            if (!Path.HasExtension(path) && !path.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                return path + Path.DirectorySeparatorChar;
            }

            return path;
        }

        public static string ToReadableString(in TimeSpan span)
        {
            string formatted = string.Empty;

            if (span.Duration().Days > 0)
            {
                formatted += string.Format("{0:0} day{1}, ", span.Days, span.Days == 1 ? string.Empty : "s");
            }
            if (span.Duration().Hours > 0 && span.Duration().TotalDays < 2)
            {
                formatted += string.Format("{0:0} hour{1}, ", span.Hours, span.Hours == 1 ? string.Empty : "s");
            }
            if (span.Duration().Minutes > 0 && span.Duration().TotalHours < 24)
            {
                formatted += string.Format("{0:0} minute{1}, ", span.Minutes, span.Minutes == 1 ? string.Empty : "s");
            }
            if (span.Duration().Seconds > 0 && span.Duration().TotalMinutes < 2)
            {
                formatted += string.Format("{0:0} second{1}, ", span.Seconds, span.Seconds == 1 ? string.Empty : "s");
            }

            if (formatted.EndsWith(", ")) formatted = formatted.Substring(0, formatted.Length - 2);

            if (string.IsNullOrEmpty(formatted)) formatted = "0 seconds";

            return formatted;
        }

        public static bool ShowWarningMessage(string message)
        {
            System.Windows.Forms.DialogResult result = System.Windows.Forms.MessageBox.Show(message, "Warning", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Warning);
            return result == System.Windows.Forms.DialogResult.Yes;
        }

        public static bool ShowConfirmationRequest(string message)
        {
            if (Console.IsInputRedirected)
            {
                Console.Out.WriteLine(message);
                System.Windows.Forms.DialogResult result = System.Windows.Forms.MessageBox.Show(message, "Confirmation required", System.Windows.Forms.MessageBoxButtons.OKCancel, System.Windows.Forms.MessageBoxIcon.Question);
                return result == System.Windows.Forms.DialogResult.OK;
            }
            else
            {
                Console.Out.Write(message);
                Console.Out.WriteLine(" (Press \"Enter\" to continue; \"c\" to cancel)");

                do
                {
                    var consoleKey = Console.ReadKey(true).Key;
                    if (consoleKey == ConsoleKey.Enter)
                    {
                        return true;
                    }
                    else if (consoleKey == ConsoleKey.C)
                    {
                        return false;
                    }
                } while (true);
            }
        }

        public static string Indent(in string text)
        {
            const string indent = "    ";
            return string.Concat(indent, text.Replace("\n", "\n" + indent));
        }
    }
}
