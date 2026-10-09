using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace YushuAfterSales.Core
{
    [DataContract]
    public sealed class UserSettings
    {
        [DataMember(Name = "theme", Order = 1)] public string Theme { get; set; }
        [DataMember(Name = "sidebarCollapsed", Order = 2)] public bool SidebarCollapsed { get; set; }
        [DataMember(Name = "language", Order = 3)] public string Language { get; set; }

        public static UserSettings Load()
        {
            string path = SettingsPath();
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024)
                return new UserSettings { Theme = "dark", Language = "zh-CN" };
            using (FileStream stream = File.OpenRead(path))
            {
                var result = (UserSettings)new DataContractJsonSerializer(typeof(UserSettings)).ReadObject(stream);
                if (result == null) result = new UserSettings();
                if (result.Theme != "brown" && result.Theme != "light" && result.Theme != "dark" && result.Theme != "system") result.Theme = "dark";
                if (result.Language != "en-US") result.Language = "zh-CN";
                return result;
            }
        }

        public static void Save(string theme, bool collapsed, bool english)
        {
            var settings = new UserSettings { Theme = theme, SidebarCollapsed = collapsed, Language = english ? "en-US" : "zh-CN" };
            string path = SettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    new DataContractJsonSerializer(typeof(UserSettings)).WriteObject(stream, settings);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static string SettingsPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSYUSHU", "YushuAfterSales", "settings.json");
        }
    }
}
