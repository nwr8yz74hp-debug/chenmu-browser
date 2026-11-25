using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ChenmuBrowser
{
    internal sealed class BookmarkTransferItem
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public string FolderPath { get; set; }
    }

    internal static class BookmarkHtmlTransfer
    {
        internal const char FolderSeparator = '\u001f';
        internal const int MaximumInputBytes = 8 * 1024 * 1024;
        internal const int MaximumInputCharacters = 8 * 1024 * 1024;
        internal const int MaximumImportedBookmarks = 5000;
        private const int MaximumFolderDepth = 16;
        private const int MaximumFolderNameCharacters = 256;
        private const int MaximumTitleCharacters = 1024;

        private static readonly Regex CommentRegex = new Regex("<!--.*?-->",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        private static readonly Regex TokenRegex = new Regex(
            "</?DL\\b[^>]*>|<H3\\b[^>]*>.*?</H3\\s*>|<A\\b[^>]*>.*?</A\\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        private static readonly Regex AttributeRegex = new Regex(
            "(?:^|\\s)HREF\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        private static readonly Regex OpeningTagRegex = new Regex("^<[^>]+>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        private static readonly Regex AnyTagRegex = new Regex("<[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        internal static List<BookmarkTransferItem> Parse(string html)
        {
            var result = new List<BookmarkTransferItem>();
            if (String.IsNullOrWhiteSpace(html))
                return result;
            if (html.Length > MaximumInputCharacters)
                throw new InvalidDataException("书签 HTML 超过安全解析上限（8 Mi 字符）。");

            var source = CommentRegex.Replace(html, String.Empty);
            var folders = new List<string>();
            var folderDlFrames = new Stack<bool>();
            string pendingFolder = null;
            var overflowDepth = 0;

            foreach (Match token in TokenRegex.Matches(source))
            {
                var value = token.Value;
                if (value.StartsWith("</DL", StringComparison.OrdinalIgnoreCase))
                {
                    if (overflowDepth > 0)
                    {
                        overflowDepth--;
                        continue;
                    }
                    if (folderDlFrames.Count > 0 && folderDlFrames.Pop() && folders.Count > 0)
                        folders.RemoveAt(folders.Count - 1);
                    pendingFolder = null;
                    continue;
                }

                if (value.StartsWith("<DL", StringComparison.OrdinalIgnoreCase))
                {
                    if (overflowDepth > 0)
                    {
                        overflowDepth++;
                        continue;
                    }
                    if (folderDlFrames.Count >= MaximumFolderDepth)
                    {
                        overflowDepth = 1;
                        pendingFolder = null;
                        continue;
                    }
                    var ownsFolder = !String.IsNullOrWhiteSpace(pendingFolder);
                    if (ownsFolder)
                        folders.Add(pendingFolder);
                    folderDlFrames.Push(ownsFolder);
                    pendingFolder = null;
                    continue;
                }

                if (value.StartsWith("<H3", StringComparison.OrdinalIgnoreCase))
                {
                    var opening = OpeningTagRegex.Match(value);
                    var contentStart = opening.Success ? opening.Length : 0;
                    var closingStart = value.LastIndexOf("</H3", StringComparison.OrdinalIgnoreCase);
                    var rawTitle = closingStart >= contentStart
                        ? value.Substring(contentStart, closingStart - contentStart)
                        : String.Empty;
                    pendingFolder = LimitLength(CleanText(rawTitle), MaximumFolderNameCharacters);
                    continue;
                }

                var openingAnchor = OpeningTagRegex.Match(value);
                if (!openingAnchor.Success)
                    continue;
                var href = AttributeRegex.Match(openingAnchor.Value);
                if (!href.Success)
                    continue;
                var rawUrl = FirstCapture(href).Trim();
                Uri uri;
                if (!TryNormalizeWebUrl(rawUrl, out uri))
                    continue;

                var titleStart = openingAnchor.Length;
                var closingAnchorStart = value.LastIndexOf("</A", StringComparison.OrdinalIgnoreCase);
                var rawText = closingAnchorStart >= titleStart
                    ? value.Substring(titleStart, closingAnchorStart - titleStart)
                    : String.Empty;
                var title = CleanText(rawText);
                if (String.IsNullOrWhiteSpace(title))
                    title = uri.Host;
                title = LimitLength(title, MaximumTitleCharacters);

                result.Add(new BookmarkTransferItem
                {
                    Title = title,
                    Url = uri.AbsoluteUri,
                    FolderPath = String.Join(FolderSeparator.ToString(), folders)
                });
                if (result.Count >= MaximumImportedBookmarks)
                    break;
            }

            return result;
        }

        internal static string Export(IEnumerable<BookmarkTransferItem> bookmarks)
        {
            var root = new FolderNode(String.Empty);
            foreach (var item in bookmarks ?? Enumerable.Empty<BookmarkTransferItem>())
            {
                if (item == null)
                    continue;
                Uri uri;
                if (!TryNormalizeWebUrl(item.Url, out uri))
                    continue;

                var folder = root;
                foreach (var name in SplitFolderPath(item.FolderPath))
                    folder = folder.GetOrAdd(name);
                folder.Bookmarks.Add(new BookmarkTransferItem
                {
                    Title = String.IsNullOrWhiteSpace(item.Title) ? uri.Host : item.Title,
                    Url = uri.AbsoluteUri
                });
            }

            var output = new StringBuilder();
            output.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
            output.AppendLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
            output.AppendLine("<TITLE>Bookmarks</TITLE>");
            output.AppendLine("<H1>Bookmarks</H1>");
            output.AppendLine("<DL><p>");
            AppendFolderContents(output, root, 1);
            output.AppendLine("</DL><p>");
            return output.ToString();
        }

        private static void AppendFolderContents(StringBuilder output, FolderNode folder, int depth)
        {
            var indent = new String(' ', depth * 2);
            foreach (var bookmark in folder.Bookmarks)
            {
                output.Append(indent).Append("<DT><A HREF=\"")
                    .Append(WebUtility.HtmlEncode(bookmark.Url))
                    .Append("\">")
                    .Append(WebUtility.HtmlEncode(bookmark.Title ?? String.Empty))
                    .AppendLine("</A>");
            }

            foreach (var child in folder.Children)
            {
                output.Append(indent).Append("<DT><H3 ADD_DATE=\"0\">")
                    .Append(WebUtility.HtmlEncode(child.Name))
                    .AppendLine("</H3>");
                output.Append(indent).AppendLine("<DL><p>");
                AppendFolderContents(output, child, depth + 1);
                output.Append(indent).AppendLine("</DL><p>");
            }
        }

        private static IEnumerable<string> SplitFolderPath(string folderPath)
        {
            return (folderPath ?? String.Empty)
                .Split(new[] { FolderSeparator }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.Length > 0);
        }

        private static bool TryNormalizeWebUrl(string value, out Uri uri)
        {
            uri = null;
            if (String.IsNullOrWhiteSpace(value))
                return false;
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri) || String.IsNullOrWhiteSpace(uri.Host) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                uri = null;
                return false;
            }
            return true;
        }

        private static string FirstCapture(Match match)
        {
            for (var index = 1; index < match.Groups.Count; index++)
                if (match.Groups[index].Success)
                    return WebUtility.HtmlDecode(match.Groups[index].Value);
            return String.Empty;
        }

        private static string CleanText(string value)
        {
            return WebUtility.HtmlDecode(AnyTagRegex.Replace(value ?? String.Empty, String.Empty)).Trim();
        }

        private static string LimitLength(string value, int maximumLength)
        {
            value = value ?? String.Empty;
            return value.Length <= maximumLength ? value : value.Substring(0, maximumLength);
        }

        private sealed class FolderNode
        {
            public FolderNode(string name) { Name = name; }
            public string Name { get; private set; }
            public List<FolderNode> Children { get; } = new List<FolderNode>();
            public List<BookmarkTransferItem> Bookmarks { get; } = new List<BookmarkTransferItem>();

            public FolderNode GetOrAdd(string name)
            {
                var existing = Children.FirstOrDefault(child => String.Equals(child.Name, name, StringComparison.Ordinal));
                if (existing != null)
                    return existing;
                var created = new FolderNode(name);
                Children.Add(created);
                return created;
            }
        }
    }
}
