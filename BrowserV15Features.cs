using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using QRCoder;

namespace ChenmuBrowser
{
    internal sealed class ClosedTabSnapshot
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public double ZoomFactor { get; set; }
    }

    internal sealed class RecentlyClosedTabs
    {
        public const int MaximumEntries = 10;
        private readonly List<ClosedTabSnapshot> _items = new List<ClosedTabSnapshot>();

        public int Count { get { return _items.Count; } }

        public IList<ClosedTabSnapshot> GetItems()
        {
            return _items.Select(item => new ClosedTabSnapshot
            {
                Url = item.Url,
                Title = item.Title,
                ZoomFactor = item.ZoomFactor
            }).ToList();
        }

        public void Record(string url, string title, double zoomFactor, bool isPrivate, bool isReader)
        {
            if (isPrivate || isReader || String.IsNullOrWhiteSpace(url))
                return;

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return;

            _items.Insert(0, new ClosedTabSnapshot
            {
                Url = uri.AbsoluteUri,
                Title = String.IsNullOrWhiteSpace(title) ? uri.Host : title,
                ZoomFactor = BrowserZoomPolicy.Normalize(zoomFactor)
            });
            if (_items.Count > MaximumEntries)
                _items.RemoveRange(MaximumEntries, _items.Count - MaximumEntries);
        }

        public ClosedTabSnapshot Pop()
        {
            return PopAt(0);
        }

        public ClosedTabSnapshot PopAt(int index)
        {
            if (index < 0 || index >= _items.Count)
                return null;
            var result = _items[index];
            _items.RemoveAt(index);
            return result;
        }
    }

    internal static class BrowserZoomPolicy
    {
        public const double Minimum = 0.25;
        public const double Maximum = 5.0;
        public const double Step = 0.1;
        public const double Default = 1.0;

        public static double Normalize(double zoomFactor)
        {
            if (Double.IsNaN(zoomFactor) || Double.IsInfinity(zoomFactor))
                return Default;
            return Math.Max(Minimum, Math.Min(Maximum, Math.Round(zoomFactor, 2, MidpointRounding.AwayFromZero)));
        }

        public static double Adjust(double zoomFactor, int steps)
        {
            return Normalize(zoomFactor + (Step * steps));
        }
    }

    internal sealed class ExtensionManifestInfo
    {
        public string Name { get; set; }
        public int ManifestVersion { get; set; }
        public IList<string> Permissions { get; set; }
    }

    internal static class ExtensionManifestPolicy
    {
        public const int MaximumManifestBytes = 1024 * 1024;

        public static ExtensionManifestInfo Read(string directoryPath)
        {
            if (String.IsNullOrWhiteSpace(directoryPath))
                throw new InvalidDataException("请选择包含 manifest.json 的扩展文件夹。");

            string fullDirectory;
            try { fullDirectory = Path.GetFullPath(directoryPath); }
            catch (Exception ex) { throw new InvalidDataException("扩展文件夹路径无效。", ex); }

            if (!Directory.Exists(fullDirectory))
                throw new InvalidDataException("扩展文件夹不存在。");
            if (Path.GetFileName(fullDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .StartsWith("_", StringComparison.Ordinal))
                throw new InvalidDataException("扩展文件夹名称不能以下划线开头。");

            string manifestPath = Path.Combine(fullDirectory, "manifest.json");
            var manifestFile = new FileInfo(manifestPath);
            if (!manifestFile.Exists)
                throw new InvalidDataException("所选文件夹中没有 manifest.json；请选择已解压的浏览器扩展目录。");
            if (manifestFile.Length <= 0 || manifestFile.Length > MaximumManifestBytes)
                throw new InvalidDataException("manifest.json 为空或超过 1 MiB 安全限制。");

            Dictionary<string, object> manifest;
            try
            {
                var jsonText = File.ReadAllText(manifestPath, Encoding.UTF8).TrimStart('\uFEFF');
                using (var stream = new MemoryStream(new UTF8Encoding(false).GetBytes(jsonText)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(Dictionary<string, object>),
                        new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
                    manifest = serializer.ReadObject(stream) as Dictionary<string, object>;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("无法读取扩展 manifest.json。", ex);
            }

            object versionValue;
            int manifestVersion;
            if (manifest == null || !manifest.TryGetValue("manifest_version", out versionValue) ||
                !Int32.TryParse(Convert.ToString(versionValue, System.Globalization.CultureInfo.InvariantCulture),
                    System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                    out manifestVersion) || (manifestVersion != 2 && manifestVersion != 3))
                throw new InvalidDataException("扩展 manifest_version 必须为 2 或 3。");

            object nameValue;
            string name = manifest.TryGetValue("name", out nameValue) ? nameValue as string : null;
            if (String.IsNullOrWhiteSpace(name))
                name = "未命名扩展";

            var permissions = new List<string>();
            AddPermissionValues(manifest, "permissions", permissions);
            AddPermissionValues(manifest, "host_permissions", permissions);
            AddPermissionValues(manifest, "optional_permissions", permissions);
            AddPermissionValues(manifest, "optional_host_permissions", permissions);

            return new ExtensionManifestInfo
            {
                Name = name,
                ManifestVersion = manifestVersion,
                Permissions = permissions.Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList()
            };
        }

        private static void AddPermissionValues(Dictionary<string, object> manifest, string key, IList<string> output)
        {
            object value;
            var array = manifest.TryGetValue(key, out value) ? value as object[] : null;
            if (array == null)
                return;
            foreach (var entry in array)
            {
                var permission = entry as string;
                if (!String.IsNullOrWhiteSpace(permission))
                    output.Add(permission.Trim());
            }
        }
    }

    internal static class QrCodeService
    {
        public static byte[] CreatePng(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("二维码只支持有效的 HTTP 或 HTTPS 网址。", "url");

            using (var generator = new QRCodeGenerator())
            using (var data = generator.CreateQrCode(uri.AbsoluteUri, QRCodeGenerator.ECCLevel.M))
            {
                var png = new PngByteQRCode(data);
                return png.GetGraphic(8);
            }
        }
    }
}
