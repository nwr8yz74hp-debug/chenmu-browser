using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ChenmuBrowser
{
    internal sealed class ReaderPositionRecord
    {
        public string FilePath { get; set; }
        public int PageIndex { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    internal static class ReaderPositionStorage
    {
        public const int MaximumEntries = 50;
        private const int MaximumFileBytes = 64 * 1024;
        private static readonly object Sync = new object();

        public static List<ReaderPositionRecord> Read(string path)
        {
            var result = new List<ReaderPositionRecord>();
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > MaximumFileBytes)
                    return result;
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8).Take(MaximumEntries * 2))
                {
                    try
                    {
                        var parts = line.Split('\t');
                        int page;
                        DateTime updated;
                        if (parts.Length != 3 || !Int32.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out page) || page < 0 ||
                            !DateTime.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out updated))
                            continue;
                        string filePath = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
                        if (String.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath))
                            continue;
                        result.Add(new ReaderPositionRecord
                        {
                            FilePath = Path.GetFullPath(filePath),
                            PageIndex = page,
                            UpdatedUtc = updated.ToUniversalTime()
                        });
                    }
                    catch { }
                }
            }
            catch { }
            return result.GroupBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.UpdatedUtc).First())
                .OrderByDescending(item => item.UpdatedUtc).Take(MaximumEntries).ToList();
        }

        public static int GetPageIndex(string path, string filePath, int pageCount)
        {
            if (pageCount <= 0 || String.IsNullOrWhiteSpace(filePath))
                return 0;
            string normalized;
            try { normalized = Path.GetFullPath(filePath); }
            catch { return 0; }
            var record = Read(path).FirstOrDefault(item =>
                String.Equals(item.FilePath, normalized, StringComparison.OrdinalIgnoreCase));
            return record == null ? 0 : Math.Max(0, Math.Min(record.PageIndex, pageCount - 1));
        }

        public static void Save(string path, string filePath, int pageIndex)
        {
            if (String.IsNullOrWhiteSpace(filePath) || pageIndex < 0)
                return;
            string normalized;
            try { normalized = Path.GetFullPath(filePath); }
            catch { return; }
            lock (Sync)
            {
                var entries = Read(path);
                entries.RemoveAll(item => String.Equals(item.FilePath, normalized, StringComparison.OrdinalIgnoreCase));
                entries.Insert(0, new ReaderPositionRecord
                {
                    FilePath = normalized,
                    PageIndex = pageIndex,
                    UpdatedUtc = DateTime.UtcNow
                });
                Write(path, entries);
            }
        }

        public static void Clear(string path)
        {
            lock (Sync)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch { }
            }
        }

        private static void Write(string path, IEnumerable<ReaderPositionRecord> entries)
        {
            var lines = entries.OrderByDescending(item => item.UpdatedUtc).Take(MaximumEntries).Select(item =>
                Convert.ToBase64String(Encoding.UTF8.GetBytes(item.FilePath)) + "\t" +
                Math.Max(0, item.PageIndex).ToString(CultureInfo.InvariantCulture) + "\t" +
                item.UpdatedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            AtomicTextFile.WriteAllLines(path, lines);
        }
    }
}
