using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using YushuAfterSales.Reporting;

namespace YushuAfterSales.Core
{
    /// <summary>Validates local EXE/MSI/MSU installers. This class never downloads files.</summary>
    public static class PackageSecurityService
    {
        private static readonly Regex SafeFileName = new Regex(
            @"^[A-Za-z0-9][A-Za-z0-9._() +\-]{0,127}\.(exe|msi|msu)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly HashSet<string> AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".msi", ".msu"
        };
        private static readonly Guid GenericVerifyAction = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        private const uint WtdUiNone = 2;
        private const uint WtdRevokeWholeChain = 1;
        private const uint WtdChoiceFile = 1;
        private const uint WtdStateActionVerify = 1;
        private const uint WtdStateActionClose = 2;
        private const uint WtdUiContextExecute = 0;
        private const uint WtdRevocationCheckChainExcludeRoot = 0x80;

        public sealed class PackageValidationResult
        {
            public string Path { get; internal set; }
            public string FileName { get; internal set; }
            public string Extension { get; internal set; }
            public long Length { get; internal set; }
            public string Sha256 { get; internal set; }
            public bool FileNameAllowed { get; internal set; }
            public bool ExtensionAllowed { get; internal set; }
            public bool SignatureVerified { get; internal set; }
            public int SignatureStatus { get; internal set; }
            public string SignatureStatusHex { get; internal set; }
            public string Publisher { get; internal set; }
            public bool PublisherAllowed { get; internal set; }
            public bool ExpectedHashMatched { get; internal set; }
            public bool ExpectedHashProvided { get; internal set; }
            public bool IsAllowed { get; internal set; }
            public string Failure { get; internal set; }
            public List<string> Warnings { get; private set; }
            public DateTime ValidatedUtc { get; internal set; }
            internal string SourcePath { get; set; }

            public PackageValidationResult() { Warnings = new List<string>(); }
        }

        public static PackageValidationResult InspectPackage(string path)
        {
            var result = new PackageValidationResult
            {
                Path = SensitiveDataRedactor.RedactPath(path),
                ValidatedUtc = DateTime.UtcNow,
                PublisherAllowed = false
            };
            try
            {
                string fullPath = ResolveRegularFile(path);
                result.SourcePath = fullPath;
                result.Path = SensitiveDataRedactor.RedactPath(fullPath);
                result.FileName = System.IO.Path.GetFileName(fullPath);
                result.Extension = System.IO.Path.GetExtension(fullPath);
                result.FileNameAllowed = SafeFileName.IsMatch(result.FileName);
                result.ExtensionAllowed = AllowedExtensions.Contains(result.Extension);
                if (!result.FileNameAllowed || !result.ExtensionAllowed)
                {
                    result.Failure = "文件名或扩展名不在 EXE/MSI/MSU 白名单中。";
                    return result;
                }

                var info = new FileInfo(fullPath);
                result.Length = info.Length;
                using (FileStream stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (SHA256 sha = SHA256.Create())
                    result.Sha256 = ToHex(sha.ComputeHash(stream));

                int signatureStatus;
                result.SignatureVerified = VerifyAuthenticode(fullPath, out signatureStatus);
                result.SignatureStatus = signatureStatus;
                result.SignatureStatusHex = "0x" + result.SignatureStatus.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
                result.Publisher = ReadSignerSubject(fullPath);
                if (result.SignatureVerified && String.IsNullOrWhiteSpace(result.Publisher))
                {
                    result.SignatureVerified = false;
                    result.Warnings.Add("签名验证成功但未能读取发布者证书主体。" );
                }
                if (String.Equals(result.Extension, ".msu", StringComparison.OrdinalIgnoreCase))
                    result.Warnings.Add("已检查 MSU 外层文件签名；内部 catalog 信任仍由 Windows servicing 在安装阶段校验。" );
                if (!result.SignatureVerified) result.Failure = "Authenticode 验证未通过，状态 " + result.SignatureStatusHex + "。";
                result.IsAllowed = result.FileNameAllowed && result.ExtensionAllowed && result.SignatureVerified;
                return result;
            }
            catch (Exception ex)
            {
                result.Failure = SensitiveDataRedactor.Redact(ex.Message);
                return result;
            }
        }

        public static PackageValidationResult ValidatePackage(string path, string expectedSha256, IEnumerable<string> allowedPublishers)
        {
            PackageValidationResult result = InspectPackage(path);
            result.ExpectedHashProvided = IsSha256(expectedSha256);
            result.ExpectedHashMatched = result.ExpectedHashProvided && String.Equals(result.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
            result.PublisherAllowed = IsPublisherAllowed(result.Publisher, allowedPublishers);
            if (!result.ExpectedHashProvided) result.Failure = "执行前必须提供 64 位十六进制 SHA-256。";
            else if (!result.ExpectedHashMatched) result.Failure = "SHA-256 与受信清单不匹配。";
            else if (!result.PublisherAllowed) result.Failure = "签名发布者不在允许列表中。";
            result.IsAllowed = result.IsAllowed && result.ExpectedHashProvided && result.ExpectedHashMatched && result.PublisherAllowed;
            return result;
        }

        /// <summary>Rechecks hash, WinVerifyTrust and publisher immediately before a user-confirmed runas launch.</summary>
        public static PackageValidationResult RevalidateBeforeLaunch(
            PackageValidationResult initial, string expectedSha256, IEnumerable<string> allowedPublishers,
            bool userConfirmed, string verb)
        {
            if (initial == null) throw new ArgumentNullException("initial");
            if (!userConfirmed) throw new InvalidOperationException("调用方必须先取得用户对受控安装的明确确认。" );
            if (!String.Equals(verb, "runas", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("受控安装只允许显式的 runas 动作。" );
            string path = String.IsNullOrWhiteSpace(initial.SourcePath) ? initial.Path : initial.SourcePath;
            PackageValidationResult current = ValidatePackage(path, expectedSha256, allowedPublishers);
            if (!String.Equals(current.Sha256, initial.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                current.IsAllowed = false;
                current.Failure = "执行前二次校验发现文件内容已变化。";
            }
            return current;
        }

        /// <summary>Revalidates, then starts an interactive installer through UAC with no caller-supplied arguments.</summary>
        public static Process StartVerifiedPackage(
            PackageValidationResult initial, string expectedSha256, IEnumerable<string> allowedPublishers, bool userConfirmed)
        {
            PackageValidationResult current = RevalidateBeforeLaunch(initial, expectedSha256, allowedPublishers, userConfirmed, "runas");
            if (!current.IsAllowed) throw new InvalidOperationException(current.Failure ?? "安装包未通过执行前校验。" );

            string fileName;
            string arguments;
            if (String.Equals(current.Extension, ".msi", StringComparison.OrdinalIgnoreCase))
            {
                fileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
                arguments = "/i \"" + current.SourcePath + "\"";
            }
            else if (String.Equals(current.Extension, ".msu", StringComparison.OrdinalIgnoreCase))
            {
                fileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wusa.exe");
                arguments = "\"" + current.SourcePath + "\"";
            }
            else
            {
                fileName = current.SourcePath;
                arguments = String.Empty;
            }

            return Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
            });
        }

        private static string ResolveRegularFile(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("必须提供本地安装包路径。", "path");
            string fullPath = System.IO.Path.GetFullPath(path);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("安装包不存在。", fullPath);
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("不允许从重解析点导入安装包。" );
            return fullPath;
        }

        private static bool IsPublisherAllowed(string publisher, IEnumerable<string> allowedPublishers)
        {
            if (allowedPublishers == null || String.IsNullOrWhiteSpace(publisher)) return false;
            return allowedPublishers.Any(x => !String.IsNullOrWhiteSpace(x) && String.Equals(x.Trim(), publisher.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private static string ReadSignerSubject(string path)
        {
            try { using (var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path))) return certificate.Subject; }
            catch { return String.Empty; }
        }

        private static bool VerifyAuthenticode(string path, out int status)
        {
            status = unchecked((int)0x800B0001);
            IntPtr filePath = IntPtr.Zero;
            IntPtr fileInfo = IntPtr.Zero;
            IntPtr verifiedState = IntPtr.Zero;
            Guid action = GenericVerifyAction;
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeWholeChain,
                dwUnionChoice = WtdChoiceFile,
                dwStateAction = WtdStateActionVerify,
                dwProvFlags = WtdRevocationCheckChainExcludeRoot,
                dwUIContext = WtdUiContextExecute
            };
            try
            {
                filePath = Marshal.StringToCoTaskMemUni(path);
                var file = new WinTrustFileInfo
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)),
                    pcwszFilePath = filePath,
                    hFile = IntPtr.Zero,
                    pgKnownSubject = IntPtr.Zero
                };
                fileInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WinTrustFileInfo)));
                Marshal.StructureToPtr(file, fileInfo, false);
                data.pFile = fileInfo;
                int verify = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                status = verify;
                verifiedState = data.hWVTStateData;
                data.dwStateAction = WtdStateActionClose;
                WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                return verify == 0;
            }
            catch (Exception ex)
            {
                status = ex.HResult;
                if (verifiedState != IntPtr.Zero) data.hWVTStateData = verifiedState;
                try { data.dwStateAction = WtdStateActionClose; WinVerifyTrust(IntPtr.Zero, ref action, ref data); } catch { }
                return false;
            }
            finally
            {
                if (fileInfo != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfo);
                if (filePath != IntPtr.Zero) Marshal.FreeCoTaskMem(filePath);
            }
        }

        private static bool IsSha256(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
            for (int i = 0; i < value.Length; i++) if (!Uri.IsHexDigit(value[i])) return false;
            return true;
        }

        private static string ToHex(byte[] value)
        {
            var builder = new StringBuilder(value.Length * 2);
            foreach (byte item in value) builder.Append(item.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WinTrustData pWVTData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        // Keep the Win7-compatible WINTRUST_DATA prefix through dwUIContext.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
        }
    }
}
