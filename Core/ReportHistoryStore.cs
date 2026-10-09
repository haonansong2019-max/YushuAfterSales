using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YushuAfterSales.Reporting;

namespace YushuAfterSales.Core
{
    public static class ReportHistoryStore
    {
        public static void Save(ReportDocument report)
        {
            if (report == null) throw new ArgumentNullException("report");
            List<DiagnosticLogEntry> existingLogs = new List<DiagnosticLogEntry>();
            Guid id;
            if (Guid.TryParseExact(report.ReportId, "N", out id))
                existingLogs = LoadLogs(report.ReportId);
            Save(report, existingLogs);
        }

        public static void Save(ReportDocument report, IEnumerable<DiagnosticLogEntry> logs)
        {
            SaveTo(Root(), report, logs);
        }

        internal static void SaveTo(string root, ReportDocument report, IEnumerable<DiagnosticLogEntry> logs)
        {
            if (report == null) throw new ArgumentNullException("report");
            if (String.IsNullOrWhiteSpace(root)) throw new ArgumentException("A report history directory is required.", "root");
            Directory.CreateDirectory(root);
            ReportWriter.WritePackage(report, root, logs, false);
        }

        public static ReportDocument Load(string reportId)
        {
            return LoadFrom(Root(), reportId);
        }

        internal static ReportDocument LoadFrom(string root, string reportId)
        {
            string packageDirectory = ReportWriter.PackageDirectory(root, reportId);
            string reportPath = Path.Combine(packageDirectory, "report.json");
            if (IsReparsePoint(root) || IsReparsePoint(packageDirectory) || !File.Exists(reportPath) || IsReparsePoint(reportPath)) return null;
            ReportDocument report = ReportJson.DeserializeReport(File.ReadAllText(reportPath));
            if (!String.Equals(report.ReportId, Path.GetFileName(packageDirectory), StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Report ID does not match its history directory.");
            return report;
        }

        /// <summary>Loads the redacted diagnostic log entries stored with a local history report.</summary>
        public static List<DiagnosticLogEntry> LoadLogs(string reportId)
        {
            return LoadLogsFrom(Root(), reportId);
        }

        internal static List<DiagnosticLogEntry> LoadLogsFrom(string root, string reportId)
        {
            string packageDirectory = ReportWriter.PackageDirectory(root, reportId);
            string logDirectory = Path.Combine(packageDirectory, "logs");
            var result = new List<DiagnosticLogEntry>();
            if (IsReparsePoint(root) || !Directory.Exists(logDirectory) || IsReparsePoint(packageDirectory) || IsReparsePoint(logDirectory)) return result;
            if (LoadFrom(root, reportId) == null) return result;

            foreach (string path in Directory.GetFiles(logDirectory, "*.log", SearchOption.TopDirectoryOnly)
                .Where(path => !IsReparsePoint(path)).OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                string text = File.ReadAllText(path);
                string evidenceId = ReadHeader(ref text, "# evidenceId: ");
                string source = ReadHeader(ref text, "# source: ");
                string capturedUtc = ReadHeader(ref text, "# capturedUtc: ");
                result.Add(new DiagnosticLogEntry
                {
                    EvidenceId = evidenceId,
                    Name = OriginalLogName(Path.GetFileName(path)),
                    Source = source,
                    CapturedUtc = capturedUtc,
                    Content = text
                });
            }
            return result;
        }

        public static List<ReportDocument> LoadRecent(int maximum)
        {
            var result = new List<ReportDocument>();
            string root = Root();
            if (maximum < 1 || !Directory.Exists(root) || IsReparsePoint(root)) return result;
            foreach (string directory in Directory.GetDirectories(root)
                .Where(path => !IsReparsePoint(path) && IsReportId(Path.GetFileName(path)))
                .OrderByDescending(path => File.Exists(Path.Combine(path, "report.json")) ? File.GetLastWriteTimeUtc(Path.Combine(path, "report.json")) : DateTime.MinValue)
                .Take(Math.Min(maximum, 100)))
            {
                try
                {
                    ReportDocument report = Load(Path.GetFileName(directory));
                    if (report != null) result.Add(report);
                }
                catch (Exception ex) when (ex is IOException || ex is FormatException || ex is InvalidOperationException || ex is System.Runtime.Serialization.SerializationException || ex is ArgumentException) { }
            }
            return result;
        }

        public static string Root()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSYUSHU", "YushuAfterSales", "Reports");
        }

        private static bool IsReportId(string reportId)
        {
            Guid parsed;
            return Guid.TryParseExact(reportId, "N", out parsed);
        }

        private static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        private static string ReadHeader(ref string text, string prefix)
        {
            if (text == null || !text.StartsWith(prefix, StringComparison.Ordinal)) return null;
            int newline = text.IndexOf('\n');
            if (newline < 0)
            {
                string finalLine = text.Substring(prefix.Length).TrimEnd('\r');
                text = String.Empty;
                return finalLine;
            }
            string line = text.Substring(0, newline).TrimEnd('\r');
            text = text.Substring(newline + 1);
            return line.Substring(prefix.Length);
        }

        private static string OriginalLogName(string storedName)
        {
            if (storedName != null && storedName.Length > 4 && storedName[3] == '_' &&
                Char.IsDigit(storedName[0]) && Char.IsDigit(storedName[1]) && Char.IsDigit(storedName[2]))
                return storedName.Substring(4);
            return storedName;
        }
    }
}
