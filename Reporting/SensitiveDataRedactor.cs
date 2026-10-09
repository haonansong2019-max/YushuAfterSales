using System;
using System.IO;
using System.Text.RegularExpressions;

namespace YushuAfterSales.Reporting
{
    /// <summary>Conservative redaction for customer reports. It does not mutate the source log.</summary>
    public static class SensitiveDataRedactor
    {
        private static readonly Regex SecretLine = new Regex(
            @"(?im)(\b(?:machineguid|machine\s*id|productid|serial(?:number)?|activationid|license(?:key)?|token|api[-_]?key|password|passwd|secret)\b\s*[:=]\s*)[^\r\n,;]+",
            RegexOptions.Compiled);
        private static readonly Regex Bearer = new Regex(@"(?i)(\bBearer\s+)[A-Za-z0-9._~+/=-]+", RegexOptions.Compiled);
        private static readonly Regex UserPath = new Regex(@"(?i)[A-Z]:\\Users\\[^\\\r\n]+", RegexOptions.Compiled);
        private static readonly Regex UncHost = new Regex(@"(?i)\\\\[^\\\s]+", RegexOptions.Compiled);

        public static string Redact(string value)
        {
            if (String.IsNullOrEmpty(value))
                return value ?? String.Empty;

            string text = value;
            text = ReplaceKnownDirectory(text, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
            text = ReplaceKnownDirectory(text, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%");
            text = ReplaceKnownDirectory(text, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%");
            text = ReplaceKnownDirectory(text, Path.GetTempPath(), "%TEMP%\\");
            text = SecretLine.Replace(text, "$1<REDACTED>");
            text = Bearer.Replace(text, "$1<REDACTED>");
            // Preserve the relative path after the profile root while removing the account name.
            text = UserPath.Replace(text, "%USERPROFILE%");
            // UNC host names are machine identifiers. Keep a marker so the evidence remains searchable.
            text = UncHost.Replace(text, @"\\<HOST>");
            return text;
        }

        public static string RedactPath(string path)
        {
            return Redact(path);
        }

        private static string ReplaceKnownDirectory(string text, string directory, string token)
        {
            if (String.IsNullOrEmpty(directory))
                return text;
            int start = 0;
            int index;
            var builder = new System.Text.StringBuilder(text.Length);
            while ((index = text.IndexOf(directory, start, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                builder.Append(text, start, index - start);
                builder.Append(token);
                start = index + directory.Length;
            }
            builder.Append(text, start, text.Length - start);
            return builder.ToString();
        }
    }
}
