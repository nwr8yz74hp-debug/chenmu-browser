using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ChenmuBrowser
{
    internal enum DownloadRecordStatus
    {
        InProgress,
        Completed,
        Canceled,
        Interrupted
    }

    internal sealed class DownloadHistoryRecord
    {
        public string Id { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public DownloadRecordStatus Status { get; set; }
        public long BytesReceived { get; set; }
        public long? TotalBytes { get; set; }
    }

    internal static class DownloadHistoryStorage
    {
        public const int MaximumEntries = 200;
        public const int MaximumSerializedBytes = 512 * 1024;
        private const int MaximumReadLines = 1000;

        public static List<DownloadHistoryRecord> Read(string path, int retentionDays, DateTime? nowUtc = null)
        {
            var entries = new List<DownloadHistoryRecord>();
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > MaximumSerializedBytes * 4L)
                    return entries;
                using (var reader = new StreamReader(path, Encoding.UTF8, true))
                {
                    string line;
                    int lines = 0;
                    while (lines++ < MaximumReadLines && (line = reader.ReadLine()) != null)
                    {
                        if (line.Length > MaximumSerializedBytes)
                            continue;
                        try
                        {
                            var parts = line.Split('\t');
                            DateTime started;
                            DateTime finished = DateTime.MinValue;
                            long received;
                            long total = 0;
                            DownloadRecordStatus status;
                            if (parts.Length != 8 || !IsSafeId(parts[0]) ||
                                !DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out started) ||
                                (parts[2].Length > 0 && !DateTime.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out finished)) ||
                                !Enum.TryParse(parts[3], false, out status) || !Enum.IsDefined(typeof(DownloadRecordStatus), status) ||
                                !Int64.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out received) || received < 0 ||
                                (parts[5].Length > 0 && (!Int64.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out total) || total < 0)))
                                continue;
                            var name = Encoding.UTF8.GetString(Convert.FromBase64String(parts[6]));
                            var filePath = Encoding.UTF8.GetString(Convert.FromBase64String(parts[7]));
                            if (String.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath))
                                continue;
                            entries.Add(new DownloadHistoryRecord
                            {
                                Id = parts[0],
                                StartedUtc = started.ToUniversalTime(),
                                FinishedUtc = parts[2].Length == 0 ? (DateTime?)null : finished.ToUniversalTime(),
                                FileName = String.IsNullOrWhiteSpace(name) ? Path.GetFileName(filePath) : name,
                                FilePath = filePath,
                                Status = status,
                                BytesReceived = received,
                                TotalBytes = parts[5].Length == 0 ? (long?)null : total
                            });
                        }
                        catch { }
                    }
                }
            }
            catch { }

            DateTime now = nowUtc ?? DateTime.UtcNow;
            if (now.Kind != DateTimeKind.Utc)
                now = now.ToUniversalTime();
            foreach (var item in entries.Where(item => item.Status == DownloadRecordStatus.InProgress))
            {
                item.Status = DownloadRecordStatus.Interrupted;
                item.FinishedUtc = now;
            }
            return Prune(entries, retentionDays, now);
        }

        public static List<DownloadHistoryRecord> Prune(IEnumerable<DownloadHistoryRecord> entries,
            int retentionDays, DateTime nowUtc)
        {
            var result = new List<DownloadHistoryRecord>();
            if (entries == null)
                return result;
            DateTime now = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime();
            DateTime? cutoff = retentionDays > 0 ? (DateTime?)now.AddDays(-retentionDays) : null;
            long totalBytes = 0;
            foreach (var entry in entries.Where(item => item != null)
                .OrderByDescending(item => item.StartedUtc))
            {
                if (cutoff.HasValue && entry.StartedUtc.ToUniversalTime() < cutoff.Value)
                    continue;
                string line = Serialize(entry);
                long bytes = Encoding.UTF8.GetByteCount(line) + 1L;
                if (result.Count >= MaximumEntries || totalBytes + bytes > MaximumSerializedBytes)
                    continue;
                result.Add(entry);
                totalBytes += bytes;
            }
            return result;
        }

        public static void Write(string path, IEnumerable<DownloadHistoryRecord> entries)
        {
            var bounded = Prune(entries, 0, DateTime.UtcNow);
            AtomicTextFile.WriteAllLines(path, bounded.Select(Serialize));
        }

        private static string Serialize(DownloadHistoryRecord entry)
        {
            string id = IsSafeId(entry.Id) ? entry.Id : Guid.NewGuid().ToString("N");
            string finished = entry.FinishedUtc.HasValue
                ? entry.FinishedUtc.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                : String.Empty;
            return id + "\t" + entry.StartedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + "\t" +
                finished + "\t" + entry.Status.ToString() + "\t" + Math.Max(0, entry.BytesReceived).ToString(CultureInfo.InvariantCulture) + "\t" +
                (entry.TotalBytes.HasValue ? Math.Max(0, entry.TotalBytes.Value).ToString(CultureInfo.InvariantCulture) : String.Empty) + "\t" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.FileName ?? String.Empty)) + "\t" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.FilePath ?? String.Empty));
        }

        private static bool IsSafeId(string value)
        {
            return !String.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
                value.All(character => Char.IsLetterOrDigit(character) || character == '-' || character == '_');
        }
    }
}
