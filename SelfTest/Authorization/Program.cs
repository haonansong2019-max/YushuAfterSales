using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using YushuAfterSales.Core;

namespace YushuAfterSales.AuthorizationSelfTest
{
    internal static class Program
    {
        private static readonly Type Service = typeof(AuthorizationService);
        private static int _assertions;

        private static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "YushuAfterSales.AuthorizationSelfTest", Guid.NewGuid().ToString("N"));
            string registryPath = @"Software\CSYUSHU\YushuAfterSales\SelfTest\" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(root);
            try
            {
                SetField("_testStatePath", Path.Combine(root, "authorization.dat"));
                SetField("_testConfigurationPath", Path.Combine(root, "appsettings.json"));
                SetField("_testRegistryPath", registryPath);
                SetField("_testMachineGuid", "authorization-selftest-machine-guid");

                Assert(!AuthorizationService.Load().CanRepair, "missing configuration fails closed");
                WriteConfiguration(null, null);
                Assert(!AuthorizationService.Load().CanRepair, "unconfigured endpoint fails closed");
                Assert(!AuthorizationService.Load().CanRepair, "offline without verified grant is denied");

                DateTime now = DateTime.UtcNow;
                SetField("_testUtcNow", now);
                string binding = AuthorizationService.CurrentMachineBinding();
                string requestId = Guid.NewGuid().ToString("N");
                object unsignedReply = NewReply(true, "ysrepair", binding, now.AddDays(14), requestId, null);
                Assert(!InvokeAcceptGrant(unsignedReply, binding, requestId).CanRepair, "missing receipt signature rejected");
                Assert(!InvokeAcceptGrant(NewReply(true, "ysrepair", binding, now.AddDays(14), requestId, Convert.ToBase64String(new byte[32])), binding, requestId).CanRepair,
                    "invalid receipt signature rejected");

                using (var rsa = new RSACryptoServiceProvider(2048))
                {
                    WriteConfiguration("https://auth.invalid/", null);
                    string expiry = now.AddDays(14).ToString("o", CultureInfo.InvariantCulture);
                    byte[] payload = Encoding.UTF8.GetBytes("ysrepair\ntrue\n" + binding.ToLowerInvariant() + "\n" + expiry + "\n" + requestId);
                    string signature = Convert.ToBase64String(rsa.SignData(payload, CryptoConfig.MapNameToOID("SHA256")));
                    object validReply = NewReply(true, "ysrepair", binding, DateTime.Parse(expiry, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), requestId, signature);
                    Assert(!InvokeAcceptGrant(validReply, binding, requestId).CanRepair, "un-pinned signing key cannot authorize");
                }

                WriteConfiguration(null, null);
                Assert(!AuthorizationService.Load().CanRepair, "missing service endpoint disables cached authorization");
                WriteConfiguration("https://auth.invalid/", null);
                SeedRecord(now.AddDays(-8), now.AddDays(60), now.AddDays(-8), binding);
                Assert(!AuthorizationService.Load().CanRepair, "offline grant beyond seven-day grace is denied");
                SeedRecord(now.AddDays(-1), now.AddDays(-2), now.AddDays(-1), binding);
                Assert(!AuthorizationService.Load().CanRepair, "expired license is denied");
                SeedRecord(now.AddDays(-1), now.AddDays(10), now.AddDays(-1), new string('a', 64));
                Assert(!AuthorizationService.Load().CanRepair, "different machine binding is denied");
                SeedRecord(now.AddDays(-1), now.AddDays(10), now.AddDays(1), binding);
                Assert(!AuthorizationService.Load().CanRepair, "clock rollback is denied");

                SeedRecord(now, now.AddDays(5), now, binding);
                AuthorizationState validCached = AuthorizationService.Load();
                Assert(validCached.CanRepair, "valid protected grant loads (state=" + validCached.State + ", error=" + GetField("_testLastLoadError") + ")");
                byte[] protectedBytes = File.ReadAllBytes(StatePath());
                protectedBytes[protectedBytes.Length / 2] ^= 0x40;
                File.WriteAllBytes(StatePath(), protectedBytes);
                Assert(!AuthorizationService.Load().CanRepair, "ciphertext tampering is denied");

                SeedRecord(now, now.AddDays(5), now, binding);
                protectedBytes = File.ReadAllBytes(StatePath());
                protectedBytes[protectedBytes.Length / 2] ^= 0x40;
                File.WriteAllBytes(StatePath(), protectedBytes);
                Service.GetMethod("WriteRegistryHash", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { Sha256Hex(protectedBytes) });
                Assert(!AuthorizationService.Load().CanRepair, "DPAPI rejects corrupted ciphertext when mirror hash is updated");

                SeedRecord(now, now.AddDays(5), now, binding);
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(registryPath, true)) key.SetValue("AuthorizationStateSha256", new string('0', 64), RegistryValueKind.String);
                Assert(!AuthorizationService.Load().CanRepair, "HKCU mirror tampering is denied");

                AssertPublicAsyncMethods();
                Console.WriteLine("AUTHORIZATION_SELFTEST=PASS");
                Console.WriteLine("ASSERTIONS=" + _assertions);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("AUTHORIZATION_SELFTEST=FAIL");
                Console.Error.WriteLine(ex);
                return 1;
            }
            finally
            {
                SetField("_testUtcNow", null);
                SetField("_testMachineGuid", null);
                SetField("_testStatePath", null);
                SetField("_testConfigurationPath", null);
                SetField("_testRegistryPath", null);
                SetField("_testLastLoadError", null);
                try { Registry.CurrentUser.DeleteSubKeyTree(registryPath, false); } catch { }
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void AssertPublicAsyncMethods()
        {
            MethodInfo activate = Service.GetMethod("ActivateAsync", BindingFlags.Public | BindingFlags.Static);
            MethodInfo refresh = Service.GetMethod("RefreshOnlineAsync", BindingFlags.Public | BindingFlags.Static);
            Assert(activate != null && activate.ReturnType == typeof(System.Threading.Tasks.Task<AuthorizationState>), "public async activation signature");
            Assert(refresh != null && refresh.ReturnType == typeof(System.Threading.Tasks.Task<AuthorizationState>), "public async refresh signature");
            Assert(Service.GetMethod("StoreOnlineSuccess", BindingFlags.Public | BindingFlags.Static) == null, "no public local success setter");
            Assert(Service.GetMethod("StoreGrant", BindingFlags.Public | BindingFlags.Static) == null, "no public grant writer");
        }

        private static AuthorizationState InvokeAcceptGrant(object reply, string binding, string requestId)
        {
            MethodInfo method = Service.GetMethod("AcceptGrant", BindingFlags.NonPublic | BindingFlags.Static);
            return method.Invoke(null, new[] { reply, (object)binding, requestId, false }) as AuthorizationState;
        }

        private static object NewReply(bool success, string appId, string binding, DateTime expires, string requestId, string signature)
        {
            Type type = Service.Assembly.GetType("YushuAfterSales.Core.ApiEnvelope", true);
            object reply = Activator.CreateInstance(type, true);
            SetProperty(type, reply, "Success", (bool?)success);
            SetProperty(type, reply, "AppId", appId);
            SetProperty(type, reply, "MachineBinding", binding);
            SetProperty(type, reply, "ExpireTime", expires.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            SetProperty(type, reply, "RequestId", requestId);
            SetProperty(type, reply, "Signature", signature);
            return reply;
        }

        private static void SeedRecord(DateTime lastOnline, DateTime expires, DateTime lastSeen, string binding)
        {
            Type type = Service.Assembly.GetType("YushuAfterSales.Core.ProtectedAuthorizationRecord", true);
            object record = Activator.CreateInstance(type, true);
            SetProperty(type, record, "Schema", 1);
            SetProperty(type, record, "AppId", "ysrepair");
            SetProperty(type, record, "LastOnlineUtc", lastOnline.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            SetProperty(type, record, "LicenseExpiresUtc", expires.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            SetProperty(type, record, "LastSeenUtc", lastSeen.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            SetProperty(type, record, "MachineBinding", binding);
            Service.GetMethod("SaveRecord", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new[] { record });
        }

        private static void WriteConfiguration(string baseUrl, string publicKey)
        {
            string keyPath = Path.Combine(Path.GetDirectoryName(StatePath()), "public-key.xml");
            if (publicKey != null) File.WriteAllText(keyPath, publicKey, Encoding.UTF8);
            var builder = new StringBuilder("{\"authorization\":{");
            if (baseUrl != null) builder.Append("\"baseUrl\":\"").Append(baseUrl).Append("\"");
            if (baseUrl != null && publicKey != null) builder.Append(',');
            if (publicKey != null) builder.Append("\"receiptPublicKeyXml\":\"").Append(EscapeJson(publicKey)).Append("\"");
            builder.Append("}}");
            File.WriteAllText(ConfigurationPath(), builder.ToString(), Encoding.UTF8);
        }

        private static string EscapeJson(string value) { return value.Replace("\\", "\\\\").Replace("\"", "\\\""); }
        private static string Sha256Hex(byte[] value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(value);
                var result = new StringBuilder(digest.Length * 2);
                foreach (byte item in digest) result.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }
        private static string StatePath() { return (string)GetField("_testStatePath"); }
        private static string ConfigurationPath() { return (string)GetField("_testConfigurationPath"); }
        private static void SetField(string name, object value) { Service.GetField(name, BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, value); }
        private static object GetField(string name) { return Service.GetField(name, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null); }
        private static void SetProperty(Type type, object instance, string name, object value) { type.GetProperty(name).SetValue(instance, value, null); }
        private static void Assert(bool condition, string label)
        {
            _assertions++;
            if (!condition) throw new InvalidOperationException("ASSERT_FAILED: " + label);
        }
    }
}
