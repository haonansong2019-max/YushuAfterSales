using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace YushuAfterSales.Core
{
    public sealed class AuthorizationState
    {
        public string AppId { get; set; }
        public string State { get; set; }
        public DateTime? LastOnlineUtc { get; set; }
        public DateTime? GraceUntilUtc { get; set; }
        public string MachineBinding { get; set; }
        public bool CanRepair { get; set; }
    }

    // Kept as a non-persisting request value for diagnostics and contract tests.
    [DataContract]
    public sealed class AuthorizationRequest
    {
        [DataMember(Name = "appId", Order = 1)] public string AppId { get; set; }
        [DataMember(Name = "machineBinding", Order = 2)] public string MachineBinding { get; set; }
        [DataMember(Name = "requestUtc", Order = 3)] public string RequestUtc { get; set; }
        [DataMember(Name = "nonce", Order = 4)] public string Nonce { get; set; }
    }

    [DataContract]
    internal sealed class AuthorizationConfigurationFile
    {
        [DataMember(Name = "authorization", IsRequired = false)] public AuthorizationConfiguration Authorization { get; set; }
    }

    [DataContract]
    internal sealed class AuthorizationConfiguration
    {
        [DataMember(Name = "baseUrl", IsRequired = false)] public string BaseUrl { get; set; }
    }

    [DataContract]
    internal sealed class ApiEnvelope
    {
        [DataMember(Name = "success", IsRequired = false)] public bool? Success { get; set; }
        [DataMember(Name = "authorized", IsRequired = false)] public bool? Authorized { get; set; }
        [DataMember(Name = "code", IsRequired = false)] public int? Code { get; set; }
        [DataMember(Name = "status", IsRequired = false)] public string Status { get; set; }
        [DataMember(Name = "message", IsRequired = false)] public string Message { get; set; }
        [DataMember(Name = "msg", IsRequired = false)] public string Msg { get; set; }
        [DataMember(Name = "appid", IsRequired = false)] public string AppId { get; set; }
        [DataMember(Name = "appId", IsRequired = false)] public string AppIdCamel { get; set; }
        [DataMember(Name = "machine_code", IsRequired = false)] public string MachineCode { get; set; }
        [DataMember(Name = "machineBinding", IsRequired = false)] public string MachineBinding { get; set; }
        [DataMember(Name = "expire_time", IsRequired = false)] public string ExpireTime { get; set; }
        [DataMember(Name = "expires_at", IsRequired = false)] public string ExpiresAt { get; set; }
        [DataMember(Name = "expire", IsRequired = false)] public string Expire { get; set; }
        [DataMember(Name = "valid_until", IsRequired = false)] public string ValidUntil { get; set; }
        [DataMember(Name = "request_id", IsRequired = false)] public string RequestId { get; set; }
        [DataMember(Name = "signature", IsRequired = false)] public string Signature { get; set; }
        [DataMember(Name = "data", IsRequired = false)] public ApiGrant Data { get; set; }
    }

    [DataContract]
    internal sealed class ApiGrant
    {
        [DataMember(Name = "success", IsRequired = false)] public bool? Success { get; set; }
        [DataMember(Name = "authorized", IsRequired = false)] public bool? Authorized { get; set; }
        [DataMember(Name = "status", IsRequired = false)] public string Status { get; set; }
        [DataMember(Name = "appid", IsRequired = false)] public string AppId { get; set; }
        [DataMember(Name = "appId", IsRequired = false)] public string AppIdCamel { get; set; }
        [DataMember(Name = "machine_code", IsRequired = false)] public string MachineCode { get; set; }
        [DataMember(Name = "machineBinding", IsRequired = false)] public string MachineBinding { get; set; }
        [DataMember(Name = "expire_time", IsRequired = false)] public string ExpireTime { get; set; }
        [DataMember(Name = "expires_at", IsRequired = false)] public string ExpiresAt { get; set; }
        [DataMember(Name = "expire", IsRequired = false)] public string Expire { get; set; }
        [DataMember(Name = "valid_until", IsRequired = false)] public string ValidUntil { get; set; }
        [DataMember(Name = "request_id", IsRequired = false)] public string RequestId { get; set; }
        [DataMember(Name = "signature", IsRequired = false)] public string Signature { get; set; }
    }

    [DataContract]
    internal sealed class ProtectedAuthorizationRecord
    {
        [DataMember(Name = "schema", Order = 1)] public int Schema { get; set; }
        [DataMember(Name = "appId", Order = 2)] public string AppId { get; set; }
        [DataMember(Name = "lastOnlineUtc", Order = 3)] public string LastOnlineUtc { get; set; }
        [DataMember(Name = "licenseExpiresUtc", Order = 4)] public string LicenseExpiresUtc { get; set; }
        [DataMember(Name = "lastSeenUtc", Order = 5)] public string LastSeenUtc { get; set; }
        [DataMember(Name = "machineBinding", Order = 6)] public string MachineBinding { get; set; }
    }

    public static class AuthorizationService
    {
        private const string ProductAppId = "ysrepair";
        private const int MaximumGraceDays = 7;
        private const string RegistryPath = @"Software\CSYUSHU\YushuAfterSales";
        private const string RegistryHashValue = "AuthorizationStateSha256";
        private const string StateFileName = "authorization.dat";
        private const int MaximumReplyBytes = 64 * 1024;
        private static readonly object StateLock = new object();
        // Internal seams are reachable only by the separate SelfTest through reflection; production
        // UI and public callers cannot seed grants or override identity/time/configuration.
        private static string _testStatePath;
        private static string _testConfigurationPath;
        private static string _testRegistryPath;
        private static string _testMachineGuid;
        private static DateTime? _testUtcNow;
        private static string _testLastLoadError;

        public static string CurrentMachineBinding()
        {
            string machineGuid = ReadMachineGuid();
            if (String.IsNullOrWhiteSpace(machineGuid)) throw new InvalidOperationException("Windows machine identity is unavailable.");
            return Sha256Hex(Encoding.UTF8.GetBytes(ProductAppId + "|" + machineGuid.Trim().ToUpperInvariant()));
        }

        public static AuthorizationRequest CreateRequest(DateTime requestUtc, string machineBinding, string nonce)
        {
            if (!IsSha256(machineBinding)) throw new ArgumentException("Machine binding must be a SHA-256 hex digest.", "machineBinding");
            if (String.IsNullOrWhiteSpace(nonce) || nonce.Length > 128) throw new ArgumentException("A bounded request nonce is required.", "nonce");
            return new AuthorizationRequest
            {
                AppId = ProductAppId,
                MachineBinding = machineBinding.ToLowerInvariant(),
                RequestUtc = requestUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                Nonce = nonce
            };
        }

        /// <summary>Reads only the local, DPAPI-protected grant. Missing configuration or invalid state fails closed.</summary>
        public static AuthorizationState Load()
        {
            try
            {
                if (String.IsNullOrWhiteSpace(ResolveBaseUrl())) return Invalid("not-configured");
                lock (StateLock)
                {
                    string path = StatePath();
                    if (!File.Exists(path)) return Invalid("not-authorized");
                    byte[] protectedBytes = File.ReadAllBytes(path);
                    string expectedHash = ReadRegistryHash();
                    if (!FixedTimeEquals(expectedHash ?? String.Empty, Sha256Hex(protectedBytes))) return Invalid("local-state-mirror-mismatch");

                    byte[] clear = ProtectedData.Unprotect(protectedBytes, Encoding.UTF8.GetBytes(ProductAppId), DataProtectionScope.CurrentUser);
                    ProtectedAuthorizationRecord record = Deserialize<ProtectedAuthorizationRecord>(clear);
                    if (record == null || record.Schema != 1 || record.AppId != ProductAppId || !IsSha256(record.MachineBinding)) return Invalid("invalid-local-state");
                    string currentBinding = CurrentMachineBinding();
                    if (!FixedTimeEquals(record.MachineBinding, currentBinding)) return Invalid("machine-binding-mismatch");

                    DateTime lastOnline = ParseUtc(record.LastOnlineUtc);
                    DateTime expires = ParseUtc(record.LicenseExpiresUtc);
                    DateTime lastSeen = ParseUtc(record.LastSeenUtc);
                    DateTime now = UtcNow();
                    DateTime graceUntil = Min(expires, lastOnline.AddDays(MaximumGraceDays));
                    if (expires <= lastOnline || lastSeen < lastOnline || lastSeen > expires.AddDays(1) ||
                        lastOnline > now.AddMinutes(2) || now < lastSeen.AddMinutes(-2))
                        return Invalid("clock-rollback-or-invalid-time");

                    if (now > lastSeen)
                    {
                        record.LastSeenUtc = now.ToString("o", CultureInfo.InvariantCulture);
                        SaveRecord(record);
                    }

                    bool valid = now <= graceUntil && now <= expires;
                    return new AuthorizationState
                    {
                        AppId = ProductAppId,
                        State = !valid ? "expired" : now <= lastOnline.AddMinutes(5) ? "authorized" : "offline-grace",
                        LastOnlineUtc = lastOnline,
                        GraceUntilUtc = graceUntil,
                        MachineBinding = currentBinding,
                        CanRepair = valid
                    };
                }
            }
            catch (Exception ex) when (IsExpectedLocalFailure(ex))
            {
                if (!String.IsNullOrWhiteSpace(_testStatePath)) _testLastLoadError = ex.GetType().Name + ":" + ex.Message;
                return Invalid("invalid-local-state");
            }
        }

        /// <summary>
        /// Explicit user-triggered activation. Sends the hashed machine code only to the documented
        /// addUser/useAuthCode endpoints; the card code exists only in this request and is never persisted.
        /// </summary>
        public static Task<AuthorizationState> ActivateAsync(string authCode)
        {
            return Task.Run(() => Activate(authCode));
        }

        /// <summary>Explicit user-triggered online revalidation; does not extend a grant unless the API confirms a future expiry.</summary>
        public static Task<AuthorizationState> RefreshOnlineAsync()
        {
            return Task.Run(RefreshOnline);
        }

        private static AuthorizationState Activate(string authCode)
        {
            if (String.IsNullOrWhiteSpace(authCode) || authCode.Length > 512) return Invalid("activation-code-required");
            try
            {
                string baseUrl = ResolveBaseUrl();
                string binding = CurrentMachineBinding();
                string requestId = Guid.NewGuid().ToString("N");
                ApiEnvelope addUser = Post(baseUrl, "api/auth/addUser", new[]
                {
                    Pair("appid", ProductAppId), Pair("machine_code", binding)
                });
                if (!ApiSucceeded(addUser)) return Invalid("activation-registration-rejected");

                ApiEnvelope reply = Post(baseUrl, "api/auth/useAuthCode", new[]
                {
                    Pair("appid", ProductAppId), Pair("machine_code", binding), Pair("authcode", authCode.Trim()), Pair("request_id", requestId)
                });
                return AcceptGrant(reply, binding, requestId, true);
            }
            catch (Exception ex) when (IsExpectedServiceFailure(ex))
            {
                // An activation attempt cannot create a grant when configuration or the service fails.
                return Invalid("authorization-service-unavailable");
            }
        }

        private static AuthorizationState RefreshOnline()
        {
            try
            {
                string baseUrl = ResolveBaseUrl();
                string binding = CurrentMachineBinding();
                string requestId = Guid.NewGuid().ToString("N");
                ApiEnvelope reply = Post(baseUrl, "api/auth/getUserExpire", new[]
                {
                    Pair("appid", ProductAppId), Pair("machine_code", binding), Pair("request_id", requestId)
                });
                return AcceptGrant(reply, binding, requestId, false);
            }
            catch (Exception ex) when (IsExpectedServiceFailure(ex))
            {
                // A failed refresh does not create or extend anything. A previously verified grant
                // may remain usable only until its already-recorded seven-day grace expires.
                AuthorizationState cached = Load();
                return cached.CanRepair ? cached : Invalid("authorization-service-unavailable");
            }
        }

        private static AuthorizationState AcceptGrant(ApiEnvelope reply, string binding, string requestId, bool activation)
        {
            if (reply == null || !ApiSucceeded(reply) || IsExplicitlyUnauthorized(reply))
                return Invalid(activation ? "activation-rejected" : "authorization-rejected");

            string appId = First(reply.AppId, reply.AppIdCamel, reply.Data == null ? null : First(reply.Data.AppId, reply.Data.AppIdCamel));
            if (!String.IsNullOrEmpty(appId) && !String.Equals(appId, ProductAppId, StringComparison.Ordinal))
                return Invalid("authorization-product-mismatch");
            string responseBinding = First(reply.MachineCode, reply.MachineBinding, reply.Data == null ? null : First(reply.Data.MachineCode, reply.Data.MachineBinding));
            if (!String.IsNullOrEmpty(responseBinding) && !FixedTimeEquals(responseBinding, binding))
                return Invalid("authorization-machine-mismatch");

            string expiryText = First(reply.ExpireTime, reply.ExpiresAt, reply.Expire, reply.ValidUntil,
                reply.Data == null ? null : First(reply.Data.ExpireTime, reply.Data.ExpiresAt, reply.Data.Expire, reply.Data.ValidUntil));
            DateTime expires;
            if (!TryParseExpiry(expiryText, out expires) || expires <= DateTime.UtcNow)
                return Invalid("authorization-expiry-missing-or-expired");
            string signedRequestId = First(reply.RequestId, reply.Data == null ? null : reply.Data.RequestId);
            string signature = First(reply.Signature, reply.Data == null ? null : reply.Data.Signature);
            string signingAppId = First(appId, ProductAppId);
            // No unsigned API response can create or extend a local grant. The service and key
            // contract must be provisioned explicitly in appsettings.json.
            if (!FixedTimeEquals(requestId, signedRequestId ?? String.Empty) || String.IsNullOrWhiteSpace(signature) ||
                !VerifyReceiptSignature(signingAppId, binding, expires, requestId, signature))
                return Invalid("authorization-receipt-signature-invalid");

            DateTime online = UtcNow();
            var record = new ProtectedAuthorizationRecord
            {
                Schema = 1,
                AppId = ProductAppId,
                LastOnlineUtc = online.ToString("o", CultureInfo.InvariantCulture),
                LicenseExpiresUtc = expires.ToString("o", CultureInfo.InvariantCulture),
                LastSeenUtc = online.ToString("o", CultureInfo.InvariantCulture),
                MachineBinding = binding
            };
            try
            {
                lock (StateLock) SaveRecord(record);
                return Load();
            }
            catch (Exception ex) when (IsExpectedLocalFailure(ex))
            {
                return Invalid("authorization-state-save-failed");
            }
        }

        private static ApiEnvelope Post(string baseUrl, string relativePath, string[][] fields)
        {
            Uri root = new Uri(baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/", UriKind.Absolute);
            Uri endpoint = new Uri(root, relativePath);
            EnsureHttps(endpoint);
            var encoded = new StringBuilder();
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0) encoded.Append('&');
                encoded.Append(Uri.EscapeDataString(fields[i][0])).Append('=').Append(Uri.EscapeDataString(fields[i][1]));
            }
            byte[] body = Encoding.UTF8.GetBytes(encoded.ToString());
            var request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded; charset=utf-8";
            request.Accept = "application/json";
            request.UserAgent = "YushuAfterSales/1.0";
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            request.AllowAutoRedirect = false;
            request.ContentLength = body.Length;
            using (Stream output = request.GetRequestStream()) output.Write(body, 0, body.Length);
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                if (response.StatusCode != HttpStatusCode.OK || response.ContentLength > MaximumReplyBytes)
                    throw new InvalidDataException("Authorization server returned an invalid response.");
                using (Stream input = response.GetResponseStream())
                {
                    byte[] json = ReadBounded(input, MaximumReplyBytes);
                    using (var stream = new MemoryStream(json))
                        return (ApiEnvelope)new DataContractJsonSerializer(typeof(ApiEnvelope)).ReadObject(stream);
                }
            }
        }

        private static bool ApiSucceeded(ApiEnvelope reply)
        {
            if (reply == null) return false;
            if (reply.Success.HasValue) return reply.Success.Value;
            if (reply.Authorized.HasValue) return reply.Authorized.Value;
            if (reply.Code.HasValue) return reply.Code.Value == 0 || reply.Code.Value == 200;
            if (reply.Data != null && reply.Data.Success.HasValue) return reply.Data.Success.Value;
            if (reply.Data != null && reply.Data.Authorized.HasValue) return reply.Data.Authorized.Value;
            string status = First(reply.Status, reply.Data == null ? null : reply.Data.Status);
            return String.Equals(status, "success", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(status, "authorized", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(status, "active", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExplicitlyUnauthorized(ApiEnvelope reply)
        {
            return (reply.Authorized.HasValue && !reply.Authorized.Value) ||
                   (reply.Data != null && reply.Data.Authorized.HasValue && !reply.Data.Authorized.Value);
        }

        private static string ResolveBaseUrl()
        {
            string path = ConfigurationPath();
            if (!File.Exists(path)) throw new InvalidOperationException("Authorization baseUrl is not configured.");
            if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("appsettings.json is too large.");
            using (var reader = new StreamReader(path, Encoding.UTF8, true))
            {
                byte[] json = Encoding.UTF8.GetBytes(reader.ReadToEnd());
                using (var stream = new MemoryStream(json))
                {
                var config = (AuthorizationConfigurationFile)new DataContractJsonSerializer(typeof(AuthorizationConfigurationFile)).ReadObject(stream);
                string value = config == null || config.Authorization == null ? null : config.Authorization.BaseUrl;
                if (String.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("authorization.baseUrl is not configured.");
                Uri uri;
                if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                    !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment))
                    throw new InvalidOperationException("authorization.baseUrl must be an HTTPS root URL without credentials or query parameters.");
                return uri.AbsoluteUri.TrimEnd('/');
                }
            }
        }

        private static bool VerifyReceiptSignature(string appId, string binding, DateTime expires, string requestId, string signature)
        {
            try
            {
                // A production public key must be compiled into a signed build. Never trust a
                // key supplied by the customer-editable appsettings.json file.
                const string keyXml = "";
                if (String.IsNullOrWhiteSpace(keyXml)) return false;
                byte[] signatureBytes = Convert.FromBase64String(signature);
                string signedText = appId + "\ntrue\n" + binding.ToLowerInvariant() + "\n" + expires.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + "\n" + requestId;
                byte[] signedBytes = Encoding.UTF8.GetBytes(signedText);
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.FromXmlString(keyXml);
                    return rsa.VerifyData(signedBytes, CryptoConfig.MapNameToOID("SHA256"), signatureBytes);
                }
            }
            catch (Exception ex) when (ex is FormatException || ex is CryptographicException || ex is InvalidOperationException || ex is SerializationException)
            {
                return false;
            }
        }

        private static void EnsureHttps(Uri endpoint)
        {
            if (endpoint == null || endpoint.Scheme != Uri.UriSchemeHttps || !String.IsNullOrEmpty(endpoint.UserInfo))
                throw new InvalidOperationException("Authorization API must use HTTPS.");
        }

        private static void SaveRecord(ProtectedAuthorizationRecord record)
        {
            byte[] protectedBytes = ProtectedData.Protect(Serialize(record), Encoding.UTF8.GetBytes(ProductAppId), DataProtectionScope.CurrentUser);
            string path = StatePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(tempPath, protectedBytes);
                if (File.Exists(path))
                {
                    string backupPath = path + ".bak-" + Guid.NewGuid().ToString("N");
                    try { File.Replace(tempPath, path, backupPath); }
                    finally { if (File.Exists(backupPath)) File.Delete(backupPath); }
                }
                else File.Move(tempPath, path);
                WriteRegistryHash(Sha256Hex(protectedBytes));
            }
            finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
        }

        private static string ReadMachineGuid()
        {
            if (!String.IsNullOrWhiteSpace(_testMachineGuid)) return _testMachineGuid;
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", false))
                    return key == null ? null : key.GetValue("MachineGuid") as string;
            }
            catch (System.Security.SecurityException) { return null; }
        }

        private static string StatePath()
        {
            return String.IsNullOrWhiteSpace(_testStatePath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSYUSHU", "YushuAfterSales", StateFileName)
                : _testStatePath;
        }

        private static string ConfigurationPath()
        {
            return String.IsNullOrWhiteSpace(_testConfigurationPath)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json")
                : _testConfigurationPath;
        }

        private static string EffectiveRegistryPath() { return String.IsNullOrWhiteSpace(_testRegistryPath) ? RegistryPath : _testRegistryPath; }
        private static DateTime UtcNow() { return _testUtcNow.HasValue ? _testUtcNow.Value : DateTime.UtcNow; }

        private static string ReadRegistryHash()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(EffectiveRegistryPath(), false))
                return key == null ? null : key.GetValue(RegistryHashValue) as string;
        }

        private static void WriteRegistryHash(string hash)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(EffectiveRegistryPath()))
            {
                if (key == null) throw new IOException("Authorization registry mirror could not be created.");
                key.SetValue(RegistryHashValue, hash, RegistryValueKind.String);
            }
        }

        private static byte[] ReadBounded(Stream stream, int maximumBytes)
        {
            using (var output = new MemoryStream())
            {
                byte[] buffer = new byte[4096];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > maximumBytes) throw new InvalidDataException("Authorization response is too large.");
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        private static bool TryParseExpiry(string text, out DateTime value)
        {
            value = default(DateTime);
            if (String.IsNullOrWhiteSpace(text)) return false;
            long unix;
            if (Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out unix))
            {
                try
                {
                    value = unix > 100000000000 ? DateTimeOffset.FromUnixTimeMilliseconds(unix).UtcDateTime : DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
                    return true;
                }
                catch (ArgumentOutOfRangeException) { return false; }
            }
            DateTime parsed;
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed)) return false;
            value = parsed.ToUniversalTime();
            return true;
        }

        private static DateTime ParseUtc(string value)
        {
            return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal).ToUniversalTime();
        }

        private static bool IsExpectedLocalFailure(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is CryptographicException ||
                   ex is System.Security.SecurityException || ex is SerializationException || ex is FormatException ||
                   ex is InvalidOperationException || ex is ArgumentException;
        }

        private static bool IsExpectedServiceFailure(Exception ex)
        {
            return IsExpectedLocalFailure(ex) || ex is WebException || ex is InvalidDataException || ex is UriFormatException;
        }

        private static AuthorizationState Invalid(string state)
        {
            return new AuthorizationState { AppId = ProductAppId, State = state, CanRepair = false };
        }

        private static byte[] Serialize<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return stream.ToArray();
            }
        }

        private static T Deserialize<T>(byte[] value)
        {
            using (var stream = new MemoryStream(value)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }

        private static bool IsSha256(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length != 64) return false;
            for (int i = 0; i < value.Length; i++) if (!Uri.IsHexDigit(value[i])) return false;
            return true;
        }

        private static string Sha256Hex(byte[] value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(value);
                var result = new StringBuilder(hash.Length * 2);
                foreach (byte item in hash) result.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

        private static DateTime Min(DateTime left, DateTime right) { return left <= right ? left : right; }
        private static string First(params string[] values)
        {
            foreach (string value in values) if (!String.IsNullOrWhiteSpace(value)) return value.Trim();
            return null;
        }
        private static string[] Pair(string key, string value) { return new[] { key, value }; }
    }
}
