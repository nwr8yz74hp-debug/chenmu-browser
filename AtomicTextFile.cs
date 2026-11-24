using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace ChenmuBrowser
{
    internal static class AtomicTextFile
    {
        private static readonly ConcurrentDictionary<string, object> PathLocks =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public static void WriteAllLines(string path, System.Collections.Generic.IEnumerable<string> lines)
        {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentException("目标文件路径不能为空。", nameof(path));
            if (lines == null)
                throw new ArgumentNullException(nameof(lines));

            string fullPath = Path.GetFullPath(path);
            object gate = PathLocks.GetOrAdd(fullPath, delegate { return new object(); });
            lock (gate)
            {
                string directory = Path.GetDirectoryName(fullPath);
                if (String.IsNullOrWhiteSpace(directory))
                    throw new IOException("目标文件没有有效目录。");
                Directory.CreateDirectory(directory);
                string temporaryPath = Path.Combine(directory,
                    "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

                try
                {
                    using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
                        {
                            foreach (var line in lines)
                                writer.WriteLine(line ?? String.Empty);
                            writer.Flush();
                        }
                        stream.Flush(true);
                    }

                    if (File.Exists(fullPath))
                        File.Replace(temporaryPath, fullPath, null);
                    else
                        File.Move(temporaryPath, fullPath);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temporaryPath))
                            File.Delete(temporaryPath);
                    }
                    catch { }
                }
            }
        }
    }
}
