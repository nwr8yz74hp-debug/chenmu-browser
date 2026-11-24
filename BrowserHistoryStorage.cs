using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ChenmuBrowser
{
    internal sealed class BrowserHistoryRecord
    {
        public string Id { get; set; }
        public DateTime VisitedUtc { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
    }

    internal static class BrowserHistoryStorage
    {
        public const int MaximumEntries = 5000;
        public const int MaximumSerializedBytes = 2 * 1024 * 1024;

        public static List<BrowserHistoryRecord> Read(string path)
        {
            var entries = new List<BrowserHistoryRecord>();
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    long startOffset = Math.Max(0, stream.Length - MaximumSerializedBytes);
                    stream.Position = startOffset;
                    int byteCount = (int)Math.Min(MaximumSerializedBytes, stream.Length - startOffset);
                    var bytes = new byte[byteCount];
                    int totalRead = 0;
                    while (totalRead < bytes.Length)
                    {
                        int read = stream.Read(bytes, totalRead, bytes.Length - totalRead);
                        if (read == 0)
                            break;
                        totalRead += read;
                    }
                    if (totalRead != bytes.Length)
                        Array.Resize(ref bytes, totalRead);

                    string content;
                    using (var memory = new MemoryStream(bytes, false))
                    using (var reader = new StreamReader(memory, Encoding.UTF8, true))
                        content = reader.ReadToEnd();

                    int firstLineStart = 0;
                    if (startOffset > 0)
                    {
                        int firstLineEnd = content.IndexOf('\n');
                        if (firstLineEnd < 0)
                            return entries;
                        firstLineStart = firstLineEnd + 1;
                    }

                    int lineEnd = content.Length;
                    if (lineEnd > firstLineStart && content[lineEnd - 1] == '\n')
                        lineEnd--;

                    while (lineEnd >= firstLineStart && entries.Count < MaximumEntries)
                    {
                        int newline = lineEnd == 0 ? -1 : content.LastIndexOf('\n', lineEnd - 1);
                        int lineStart = Math.Max(firstLineStart, newline + 1);
                        if (lineStart > lineEnd)
                            break;
                        string line = content.Substring(lineStart, lineEnd - lineStart);
                        if (line.EndsWith("\r", StringComparison.Ordinal))
                            line = line.Substring(0, line.Length - 1);
                        TryReadLine(line, entries);
                        if (newline < firstLineStart)
                            break;
                        lineEnd = newline;
                    }
                }
            }
            catch { }

            // The bounded scan runs backwards so the newest tail is not lost; restore file order.
            entries.Reverse();
            return entries;
        }

        private static void TryReadLine(string line, ICollection<BrowserHistoryRecord> entries)
        {
            if (String.IsNullOrEmpty(line) || line.Length > MaximumSerializedBytes)
                return;
            try
            {
                var parts = line.Split('\t');
                DateTime visited;
                if (parts.Length != 4 || String.IsNullOrWhiteSpace(parts[1]) ||
                    !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out visited))
                    return;
                var title = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
                var url = Encoding.UTF8.GetString(Convert.FromBase64String(parts[3]));
                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return;
                string id = IsSafeId(parts[1]) ? parts[1] : Guid.NewGuid().ToString("N");
                entries.Add(new BrowserHistoryRecord
                {
                    Id = id,
                    VisitedUtc = visited.ToUniversalTime(),
                    Title = title,
                    Url = url
                });
            }
            catch { }
        }

        public static List<BrowserHistoryRecord> Prune(IEnumerable<BrowserHistoryRecord> entries,
            int retentionDays, DateTime nowUtc)
        {
            var result = new List<BrowserHistoryRecord>();
            if (entries == null)
                return result;
            DateTime now = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime();
            DateTime? cutoff = retentionDays > 0 ? (DateTime?)now.AddDays(-retentionDays) : null;
            long totalBytes = 0;
            foreach (var entry in entries.Where(item => item != null)
                .OrderByDescending(item => item.VisitedUtc))
            {
                if (cutoff.HasValue && entry.VisitedUtc.ToUniversalTime() < cutoff.Value)
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

        public static void Write(string path, IEnumerable<BrowserHistoryRecord> entries)
        {
            var bounded = Prune(entries, 0, DateTime.UtcNow);
            AtomicTextFile.WriteAllLines(path, bounded.Select(Serialize));
        }

        private static string Serialize(BrowserHistoryRecord entry)
        {
            string id = IsSafeId(entry.Id) ? entry.Id : Guid.NewGuid().ToString("N");
            return entry.VisitedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + "\t" + id + "\t" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Title ?? String.Empty)) + "\t" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Url ?? String.Empty));
        }

        private static bool IsSafeId(string value)
        {
            return !String.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
                value.All(character => Char.IsLetterOrDigit(character) || character == '-' || character == '_');
        }
    }
}
