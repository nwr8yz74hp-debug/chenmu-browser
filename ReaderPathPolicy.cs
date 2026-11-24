using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ChenmuBrowser
{
    internal static class ReaderPathPolicy
    {
        private const uint FileReadAttributes = 0x00000080;
        private const uint ShareRead = 0x00000001;
        private const uint ShareWrite = 0x00000002;
        private const uint ShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint VolumeNameDos = 0;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);

        public static bool IsAllowedFileUri(string uriText, string allowedRoot)
        {
            Uri uri;
            return Uri.TryCreate(uriText, UriKind.Absolute, out uri) &&
                uri.IsFile && IsAllowedLocalPath(uri.LocalPath, allowedRoot);
        }

        public static bool IsAllowedLocalPath(string candidatePath, string allowedRoot)
        {
            try
            {
                string candidate = NormalizePath(candidatePath);
                string root = NormalizePath(allowedRoot);
                if (candidate == null || root == null || !IsWithinRoot(candidate, root))
                    return false;

                if (!IsWindows())
                    return true;

                string finalRoot = GetFinalPath(root);
                string finalCandidate = GetFinalPath(candidate);
                return finalRoot != null && finalCandidate != null && IsWithinRoot(finalCandidate, finalRoot);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsWithinRoot(string candidatePath, string allowedRoot)
        {
            if (String.IsNullOrWhiteSpace(candidatePath) || String.IsNullOrWhiteSpace(allowedRoot))
                return false;
            try
            {
                string candidate = Path.GetFullPath(candidatePath);
                string root = Path.GetFullPath(allowedRoot);
                string volumeRoot = Path.GetPathRoot(root);
                if (!String.Equals(root, volumeRoot, StringComparison.OrdinalIgnoreCase))
                    root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (String.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
                    return true;
                string prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                    root.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                    ? root
                    : root + Path.DirectorySeparatorChar;
                return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar &&
                     candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
                return null;
            string value = path.Trim();
            if (value.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
                value.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
                value.StartsWith("\\??\\", StringComparison.Ordinal) ||
                value.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase))
                return null;

            string full = Path.GetFullPath(value);
            if (IsWindows())
            {
                string volume = Path.GetPathRoot(full) ?? String.Empty;
                if (full.IndexOf(':', volume.Length) >= 0)
                    return null; // Reject alternate data streams.
            }
            return full;
        }

        private static bool IsWindows()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT;
        }

        private static string GetFinalPath(string path)
        {
            using (var handle = CreateFile(path, FileReadAttributes, ShareRead | ShareWrite | ShareDelete,
                IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero))
            {
                if (handle == null || handle.IsInvalid)
                    return null;
                var buffer = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, VolumeNameDos);
                if (length == 0 || length >= buffer.Capacity)
                    return null;
                string finalPath = buffer.ToString();
                if (finalPath.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
                    finalPath = "\\\\" + finalPath.Substring(8);
                else if (finalPath.StartsWith("\\\\?\\", StringComparison.Ordinal))
                    finalPath = finalPath.Substring(4);
                return Path.GetFullPath(finalPath);
            }
        }
    }
}
