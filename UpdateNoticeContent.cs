using System;
using System.Collections.Generic;
using System.IO;

namespace ChenmuBrowser
{
    internal static class UpdateNoticeContent
    {
        public static string ReadCurrentReleaseNotes(string cumulativeReleaseNotes, Version currentVersion)
        {
            if (String.IsNullOrWhiteSpace(cumulativeReleaseNotes) || currentVersion == null)
                return String.Empty;

            var expectedHeading = "# CHENMU Browser v" + currentVersion.ToString(3) + " 更新说明";
            var currentSectionFound = false;
            var bullets = new List<string>();
            using (var reader = new StringReader(cumulativeReleaseNotes))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                    {
                        if (currentSectionFound)
                            break;
                        currentSectionFound = String.Equals(trimmed, expectedHeading, StringComparison.Ordinal);
                        continue;
                    }

                    if (!currentSectionFound || trimmed.Length < 3 ||
                        !(trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                          trimmed.StartsWith("* ", StringComparison.Ordinal)))
                        continue;

                    var bullet = trimmed.Substring(2).Trim();
                    if (bullet.Length > 0)
                        bullets.Add("• " + bullet);
                }
            }

            return String.Join(Environment.NewLine, bullets);
        }

        public static string BuildMessage(Version currentVersion, string currentReleaseNotes)
        {
            if (currentVersion == null)
                throw new ArgumentNullException("currentVersion");

            var versionText = currentVersion.ToString(3);
            var notes = String.IsNullOrWhiteSpace(currentReleaseNotes)
                ? "本次没有找到该版本的更新说明。"
                : currentReleaseNotes.Trim();
            return "已更新至 v" + versionText + "。" + Environment.NewLine + Environment.NewLine +
                "更新内容：" + Environment.NewLine + notes;
        }

        public static bool IsFinalV1Lts(Version version)
        {
            return version != null && version.Major == 1 && version.Minor == 5 && version.Build == 5;
        }

        public static string GetEditionName(Version version)
        {
            if (version == null)
                throw new ArgumentNullException("version");
            return IsFinalV1Lts(version) ? "v1.5 LTS" : "v" + version.ToString(3);
        }
    }
}
