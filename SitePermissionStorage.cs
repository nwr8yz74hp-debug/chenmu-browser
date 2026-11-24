using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ChenmuBrowser
{
    internal enum SitePermissionKind
    {
        Camera,
        Microphone,
        Geolocation,
        Notifications
    }

    internal sealed class SitePermissionRule
    {
        public string Origin { get; set; }
        public HashSet<SitePermissionKind> Allowed { get; set; } = new HashSet<SitePermissionKind>();
    }

    internal static class SitePermissionStorage
    {
        public const int MaximumSites = 200;
        private static readonly object Sync = new object();

        public static string NormalizeSecureOrigin(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                !String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                String.IsNullOrWhiteSpace(uri.Host) || !String.IsNullOrEmpty(uri.UserInfo))
                return null;
            try
            {
                string host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
                if (String.IsNullOrWhiteSpace(host))
                    return null;
                if (uri.HostNameType == UriHostNameType.IPv6 && !host.StartsWith("[", StringComparison.Ordinal))
                    host = "[" + host + "]";
                int port = uri.IsDefaultPort ? 443 : uri.Port;
                return "https://" + host + (port == 443 ? String.Empty : ":" + port.ToString(CultureInfo.InvariantCulture));
            }
            catch { return null; }
        }

        public static List<SitePermissionRule> Read(string path)
        {
            var rules = new List<SitePermissionRule>();
            try
            {
                if (!File.Exists(path))
                    return rules;
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8).Take(MaximumSites * 2))
                {
                    try
                    {
                        var parts = line.Split('\t');
                        if (parts.Length != 2 || parts[1].Length != 4 || parts[1].Any(ch => ch != '0' && ch != '1'))
                            continue;
                        string savedOrigin = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
                        string origin = NormalizeSecureOrigin(savedOrigin);
                        if (origin == null)
                            continue;
                        var rule = new SitePermissionRule { Origin = origin };
                        for (int index = 0; index < 4; index++)
                            if (parts[1][index] == '1')
                                rule.Allowed.Add((SitePermissionKind)index);
                        if (rule.Allowed.Count > 0)
                            rules.Add(rule);
                    }
                    catch { }
                }
            }
            catch { }
            return rules.GroupBy(rule => rule.Origin, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).Take(MaximumSites).ToList();
        }

        public static bool IsAllowed(IEnumerable<SitePermissionRule> rules, string origin, SitePermissionKind kind)
        {
            string normalized = NormalizeSecureOrigin(origin);
            if (normalized == null || rules == null || !Enum.IsDefined(typeof(SitePermissionKind), kind))
                return false;
            var rule = rules.FirstOrDefault(item => String.Equals(item.Origin, normalized, StringComparison.OrdinalIgnoreCase));
            return rule != null && rule.Allowed.Contains(kind);
        }

        public static void Write(string path, IEnumerable<SitePermissionRule> rules)
        {
            lock (Sync)
            {
                var normalized = (rules ?? Enumerable.Empty<SitePermissionRule>())
                    .Where(rule => rule != null && rule.Allowed != null)
                    .Select(rule => new SitePermissionRule
                    {
                        Origin = NormalizeSecureOrigin(rule.Origin),
                        Allowed = new HashSet<SitePermissionKind>(rule.Allowed.Where(kind => Enum.IsDefined(typeof(SitePermissionKind), kind)))
                    })
                    .Where(rule => rule.Origin != null && rule.Allowed.Count > 0)
                    .GroupBy(rule => rule.Origin, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(rule => rule.Origin, StringComparer.OrdinalIgnoreCase)
                    .Take(MaximumSites);
                var lines = normalized.Select(rule =>
                {
                    var flags = new StringBuilder(4);
                    for (int index = 0; index < 4; index++)
                        flags.Append(rule.Allowed.Contains((SitePermissionKind)index) ? '1' : '0');
                    return Convert.ToBase64String(Encoding.UTF8.GetBytes(rule.Origin)) + "\t" + flags;
                }).ToList();
                AtomicTextFile.WriteAllLines(path, lines);
            }
        }
    }
}
