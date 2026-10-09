using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace YushuAfterSales.Core
{
    [DataContract]
    internal sealed class AppConfigurationFile
    {
        [DataMember(Name = "updates", IsRequired = false)] public UpdateConfiguration Updates { get; set; }
    }

    [DataContract]
    internal sealed class UpdateConfiguration
    {
        [DataMember(Name = "manifestUrl", IsRequired = false)] public string ManifestUrl { get; set; }
    }

    public static class AppConfiguration
    {
        public static void Load()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return;
            using (FileStream stream = File.OpenRead(path))
            {
                var config = (AppConfigurationFile)new DataContractJsonSerializer(typeof(AppConfigurationFile)).ReadObject(stream);
                UpdateService.ManifestUrl = config == null || config.Updates == null ? null : config.Updates.ManifestUrl;
            }
        }
    }
}
