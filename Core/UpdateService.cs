using System;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace YushuAfterSales.Core
{
    [DataContract]
    public sealed class UpdateManifest
    {
        [DataMember(Name = "productId", Order = 1)] public string ProductId { get; set; }
        [DataMember(Name = "version", Order = 2)] public string Version { get; set; }
        [DataMember(Name = "downloadUrl", Order = 3)] public string DownloadUrl { get; set; }
        [DataMember(Name = "sha256", Order = 4)] public string Sha256 { get; set; }
        [DataMember(Name = "notesUrl", Order = 5, EmitDefaultValue = false)] public string NotesUrl { get; set; }
        [DataMember(Name = "publishedUtc", Order = 6, EmitDefaultValue = false)] public string PublishedUtc { get; set; }
    }

    public sealed class UpdateDownloadResult
    {
        public string FilePath { get; internal set; }
        public string Sha256 { get; internal set; }
        public long Size { get; internal set; }
    }

    public static class UpdateService
    {
        private const string ProductId = "ysrepair";
        private const int MaximumManifestBytes = 64 * 1024;
        private const long MaximumPackageBytes = 512L * 1024L * 1024L;

        /// <summary>Set from deployment configuration. Empty by default to fail closed.</summary>
        public static string ManifestUrl { get; set; }
        public static bool IsConfigured
        {
            get
            {
                Uri uri;
                return Uri.TryCreate(ManifestUrl, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps;
            }
        }

        public static string FetchManifestText()
        {
            return FetchBoundedText(ResolveManifestUrl());
        }

        public static UpdateManifest FetchManifest()
        {
            UpdateManifest manifest = ParseManifest(FetchManifestText());
            ValidateManifest(manifest);
            return manifest;
        }

        public static UpdateManifest ParseManifest(string json)
        {
            if (String.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumManifestBytes)
                throw new InvalidDataException("Update manifest is empty or too large.");
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                UpdateManifest manifest = (UpdateManifest)new DataContractJsonSerializer(typeof(UpdateManifest)).ReadObject(stream);
                ValidateManifest(manifest);
                return manifest;
            }
        }

        public static bool IsNewerVersion(string candidate, Version current)
        {
            Version parsed;
            if (current == null || !Version.TryParse(candidate, out parsed))
                throw new InvalidDataException("Update version must be a numeric version such as 1.2.3.0.");
            return parsed > current;
        }

        public static void ValidateManifest(UpdateManifest manifest)
        {
            if (manifest == null || !String.Equals(manifest.ProductId, ProductId, StringComparison.Ordinal))
                throw new InvalidDataException("Update manifest productId does not match this application.");
            Version version;
            if (!Version.TryParse(manifest.Version, out version))
                throw new InvalidDataException("Update manifest version is invalid.");
            if (!IsSha256(manifest.Sha256)) throw new InvalidDataException("Update manifest SHA-256 is invalid.");
            EnsureHttpsUrl(manifest.DownloadUrl, "downloadUrl");
            if (!String.IsNullOrWhiteSpace(manifest.NotesUrl)) EnsureHttpsUrl(manifest.NotesUrl, "notesUrl");
            if (!String.IsNullOrWhiteSpace(manifest.PublishedUtc))
            {
                DateTime published;
                if (!DateTime.TryParse(manifest.PublishedUtc, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out published))
                    throw new InvalidDataException("Update manifest publishedUtc is invalid.");
            }
        }

        /// <summary>Downloads a manifest package to a temp file and publishes it only after hash verification.</summary>
        public static UpdateDownloadResult DownloadVerified(UpdateManifest manifest, string destinationDirectory, CancellationToken cancellationToken)
        {
            ValidateManifest(manifest);
            if (String.IsNullOrWhiteSpace(destinationDirectory)) throw new ArgumentException("Destination directory is required.", "destinationDirectory");
            Directory.CreateDirectory(destinationDirectory);
            string finalPath = Path.Combine(destinationDirectory, "ysrepair-" + manifest.Version + ".zip");
            string tempPath = finalPath + ".download-" + Guid.NewGuid().ToString("N");
            try
            {
                var request = CreateHttpsRequest(manifest.DownloadUrl);
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK) throw new WebException("Update server returned HTTP " + (int)response.StatusCode + ".");
                    if (response.ContentLength > MaximumPackageBytes) throw new InvalidDataException("Update package exceeds the 512 MiB limit.");
                    using (Stream input = response.GetResponseStream())
                    using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (SHA256 sha = SHA256.Create())
                    {
                        byte[] buffer = new byte[64 * 1024];
                        long total = 0;
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            total += read;
                            if (total > MaximumPackageBytes) throw new InvalidDataException("Update package exceeds the 512 MiB limit.");
                            output.Write(buffer, 0, read);
                            sha.TransformBlock(buffer, 0, read, buffer, 0);
                        }
                        sha.TransformFinalBlock(new byte[0], 0, 0);
                        string actualHash = ToHex(sha.Hash);
                        if (!String.Equals(actualHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Downloaded update package SHA-256 does not match the manifest.");
                        output.Flush(true);
                        output.Close();
                        if (File.Exists(finalPath)) File.Replace(tempPath, finalPath, null);
                        else File.Move(tempPath, finalPath);
                        return new UpdateDownloadResult { FilePath = finalPath, Sha256 = actualHash, Size = total };
                    }
                }
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        public static string ResolveManifestUrl()
        {
            string value = ManifestUrl;
            if (String.IsNullOrWhiteSpace(value)) value = Environment.GetEnvironmentVariable("YSREPAIR_UPDATE_MANIFEST_URL");
            if (String.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Update manifest URL is not configured.");
            EnsureHttpsUrl(value.Trim(), "manifestUrl");
            return value.Trim();
        }

        private static string FetchBoundedText(string url)
        {
            var request = CreateHttpsRequest(url);
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                if (response.StatusCode != HttpStatusCode.OK) throw new WebException("Update server returned HTTP " + (int)response.StatusCode + ".");
                if (response.ContentLength > MaximumManifestBytes) throw new InvalidDataException("Update manifest exceeds 64 KiB.");
                using (Stream input = response.GetResponseStream())
                using (var output = new MemoryStream())
                {
                    byte[] buffer = new byte[4096];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + read > MaximumManifestBytes) throw new InvalidDataException("Update manifest exceeds 64 KiB.");
                        output.Write(buffer, 0, read);
                    }
                    return Encoding.UTF8.GetString(output.ToArray());
                }
            }
        }

        private static HttpWebRequest CreateHttpsRequest(string url)
        {
            EnsureHttpsUrl(url, "url");
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            request.UserAgent = "YushuAfterSales/1.0";
            request.AllowAutoRedirect = false;
            return request;
        }

        private static void EnsureHttpsUrl(string value, string name)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Fragment))
                throw new InvalidDataException(name + " must be an HTTPS URL without embedded credentials.");
        }

        private static bool IsSha256(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length != 64) return false;
            for (int i = 0; i < value.Length; i++) if (!Uri.IsHexDigit(value[i])) return false;
            return true;
        }

        private static string ToHex(byte[] value)
        {
            var result = new StringBuilder(value.Length * 2);
            foreach (byte item in value) result.Append(item.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return result.ToString();
        }
    }
}
