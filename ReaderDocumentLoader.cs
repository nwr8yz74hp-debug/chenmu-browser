using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml;

namespace ChenmuBrowser
{
    internal sealed class ReaderPage
    {
        public string Title { get; set; }
        public string Html { get; set; }
        public string FilePath { get; set; }
    }

    internal sealed class ReaderDocument
    {
        public string Title { get; set; }
        public List<ReaderPage> Pages { get; } = new List<ReaderPage>();
        public string TemporaryRoot { get; set; }
        public bool IsPdf { get; set; }
    }

    internal static class DocumentLoader
    {
        private const long MaxInputBytes = 100L * 1024L * 1024L;
        private const long MaxArchiveBytes = 350L * 1024L * 1024L;
        private const long MaxArchiveEntryBytes = 100L * 1024L * 1024L;
        private const int MaxArchiveEntries = 10000;
        private const int MaxArchiveEntryNameLength = 1024;
        private const int MaxSlides = 500;
        private const int MaxTextDocumentCharacters = 4000000;
        private const long MaxTextDocumentBytes = 8L * 1024L * 1024L;
        private const long MaxXmlEntryBytes = 8L * 1024L * 1024L;
        private const long MaxXmlAggregateBytes = 32L * 1024L * 1024L;
        private const int MaxWorkbookSheets = 100;
        private const int MaxWorkbookCells = 200000;
        private const int MaxEpubChapters = 500;
        private const long MaxLocalHtmlBytes = 16L * 1024L * 1024L;
        private const long MaxImageBytes = 40L * 1024L * 1024L;
        private const long MaxEpubHtmlBytes = 8L * 1024L * 1024L;
        private const int MaxCsvColumns = 80;
        private const int MaxMarkdownLines = 100000;
        private const int MaxInlineMarkdownLineCharacters = 100000;

        public static ReaderDocument Load(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("找不到要打开的文件。", path);
            if (info.Length > MaxInputBytes) throw new InvalidDataException("为避免占用过多内存，CHENMU Reader 暂不打开大于 100 MB 的单个文件。");

            string extension = Path.GetExtension(path).ToLowerInvariant();
            var result = new ReaderDocument { Title = Path.GetFileName(path) };

            switch (extension)
            {
                case ".pdf":
                    result.IsPdf = true;
                    result.Pages.Add(new ReaderPage { Title = "PDF 文档", FilePath = path });
                    return result;
                case ".epub":
                    return LoadEpub(path);
                case ".docx":
                    return LoadDocx(path);
                case ".xlsx":
                    return LoadXlsx(path);
                case ".pptx":
                    return LoadPptx(path);
                case ".md":
                case ".markdown":
                    result.Pages.Add(new ReaderPage { Title = result.Title, Html = MarkdownToHtml(ReadText(path)) });
                    return result;
                case ".csv":
                    result.Pages.Add(new ReaderPage { Title = result.Title, Html = CsvToHtml(ReadText(path)) });
                    return result;
                case ".txt":
                case ".log":
                    result.Pages.Add(new ReaderPage { Title = result.Title, Html = "<pre class=\"plain-text\">" + E(ReadText(path)) + "</pre>" });
                    return result;
                case ".html":
                case ".htm":
                    EnsureFileSize(path, MaxLocalHtmlBytes, "HTML 文件");
                    result.Pages.Add(new ReaderPage { Title = result.Title, FilePath = path });
                    return result;
                case ".svg":
                    EnsureFileSize(path, MaxLocalHtmlBytes, "SVG 图片");
                    result.Pages.Add(new ReaderPage { Title = result.Title, FilePath = path });
                    return result;
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".gif":
                case ".bmp":
                case ".webp":
                case ".tif":
                case ".tiff":
                    EnsureFileSize(path, MaxImageBytes, "图片");
                    result.Pages.Add(new ReaderPage { Title = result.Title, FilePath = path });
                    return result;
                default:
                    throw new NotSupportedException("CHENMU Reader 暂不支持此格式。支持 PDF、EPUB、TXT、Markdown、DOCX、XLSX、PPTX、CSV、HTML 和常见图片。");
            }
        }

        private static ReaderDocument LoadDocx(string path)
        {
            using (var archive = ZipFile.OpenRead(path))
            {
                ValidateArchive(archive);
                var entry = archive.GetEntry("word/document.xml");
                if (entry == null) throw new InvalidDataException("DOCX 文件缺少正文内容。");
                var xmlBudget = new XmlReadBudget();
                var document = xmlBudget.Load(entry);
                XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                XElement body = document.Root == null ? null : document.Root.Element(w + "body");
                if (body == null) throw new InvalidDataException("无法读取 DOCX 正文。");

                var pieces = new List<string>();
                var textBudget = new TextBudget(MaxTextDocumentCharacters);
                foreach (var item in body.Elements())
                {
                    if (item.Name == w + "p") pieces.Add(WordParagraph(item, w, textBudget));
                    else if (item.Name == w + "tbl") pieces.Add(WordTable(item, w, textBudget));
                }

                var result = new ReaderDocument { Title = Path.GetFileName(path) };
                var html = String.Join("\n", pieces);
                if (textBudget.IsExhausted)
                    html += "<p class=\"note\">为保持流畅，文档其余正文未预览。</p>";
                result.Pages.Add(new ReaderPage { Title = result.Title, Html = html });
                return result;
            }
        }

        private static string WordParagraph(XElement paragraph, XNamespace w, TextBudget textBudget)
        {
            var builder = new StringBuilder();
            foreach (var node in paragraph.Descendants())
            {
                if (node.Name == w + "t")
                {
                    XElement run = node.Parent;
                    XElement properties = run == null ? null : run.Element(w + "rPr");
                    bool bold = properties != null && properties.Element(w + "b") != null;
                    bool italic = properties != null && properties.Element(w + "i") != null;
                    string text = E(textBudget.Take(node.Value));
                    if (bold) text = "<strong>" + text + "</strong>";
                    if (italic) text = "<em>" + text + "</em>";
                    builder.Append(text);
                }
                else if (node.Name == w + "tab") builder.Append("&emsp;");
                else if (node.Name == w + "br") builder.Append("<br>");
            }
            string content = builder.ToString();
            XElement pPr = paragraph.Element(w + "pPr");
            XElement style = pPr == null ? null : pPr.Element(w + "pStyle");
            string styleId = style == null ? "" : (string)style.Attribute(w + "val") ?? "";
            if (styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
            {
                int level = 1;
                Int32.TryParse(styleId.Substring("Heading".Length), out level);
                level = Math.Max(1, Math.Min(6, level));
                return "<h" + level + ">" + content + "</h" + level + ">";
            }
            return String.IsNullOrWhiteSpace(content) ? "<p class=\"spacer\">&nbsp;</p>" : "<p>" + content + "</p>";
        }

        private static string WordTable(XElement table, XNamespace w, TextBudget textBudget)
        {
            var builder = new StringBuilder("<table><tbody>");
            foreach (var row in table.Elements(w + "tr"))
            {
                builder.Append("<tr>");
                foreach (var cell in row.Elements(w + "tc"))
                {
                    builder.Append("<td>");
                    foreach (var paragraph in cell.Elements(w + "p")) builder.Append(WordParagraph(paragraph, w, textBudget));
                    builder.Append("</td>");
                }
                builder.Append("</tr>");
            }
            builder.Append("</tbody></table>");
            return builder.ToString();
        }

        private static ReaderDocument LoadXlsx(string path)
        {
            using (var archive = ZipFile.OpenRead(path))
            {
                ValidateArchive(archive);
                var workbookEntry = archive.GetEntry("xl/workbook.xml");
                var relEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
                if (workbookEntry == null || relEntry == null) throw new InvalidDataException("XLSX 文件结构不完整。");
                var xmlBudget = new XmlReadBudget();
                var workbook = xmlBudget.Load(workbookEntry);
                var relationships = xmlBudget.Load(relEntry);

                XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
                XNamespace packageRel = "http://schemas.openxmlformats.org/package/2006/relationships";
                var targets = relationships.Root.Elements(packageRel + "Relationship")
                    .ToDictionary(x => (string)x.Attribute("Id"), x => ResolvePackagePath("xl", (string)x.Attribute("Target")));
                var sharedStrings = ReadSharedStrings(archive, main, xmlBudget);
                var result = new ReaderDocument { Title = Path.GetFileName(path) };
                int sheetNumber = 0;
                int cellCount = 0;
                bool truncatedSheets = false;
                bool truncatedCells = false;
                var textBudget = new TextBudget(MaxTextDocumentCharacters);

                foreach (var sheet in workbook.Descendants(main + "sheet"))
                {
                    if (sheetNumber >= MaxWorkbookSheets)
                    {
                        truncatedSheets = true;
                        break;
                    }
                    string id = (string)sheet.Attribute(rel + "id");
                    string sheetName = (string)sheet.Attribute("name") ?? "工作表";
                    if (id == null || !targets.ContainsKey(id)) continue;
                    var entry = archive.GetEntry(targets[id]);
                    if (entry == null) continue;
                    var sheetDoc = xmlBudget.Load(entry);
                    var html = new StringBuilder("<div class=\"sheet-caption\">工作表：" + E(sheetName) + "</div><table class=\"sheet\"><tbody>");
                    int rowCount = 0;
                    foreach (var row in sheetDoc.Descendants(main + "sheetData").Elements(main + "row"))
                    {
                        if (++rowCount > 1000) break;
                        html.Append("<tr>");
                        int columnCount = 0;
                        foreach (var cell in row.Elements(main + "c"))
                        {
                            if (++columnCount > 60) break;
                            if (cellCount >= MaxWorkbookCells || textBudget.IsExhausted)
                            {
                                truncatedCells = true;
                                break;
                            }
                            cellCount++;
                            string type = (string)cell.Attribute("t") ?? "";
                            string value = "";
                            if (type == "inlineStr") value = String.Concat(cell.Descendants(main + "t").Select(x => x.Value));
                            else
                            {
                                var valueElement = cell.Element(main + "v");
                                value = valueElement == null ? "" : valueElement.Value;
                                if (type == "s" && Int32.TryParse(value, out int sharedIndex) && sharedIndex >= 0 && sharedIndex < sharedStrings.Count)
                                    value = sharedStrings[sharedIndex];
                                else if (type == "b") value = value == "1" ? "TRUE" : "FALSE";
                            }
                            html.Append("<td>" + E(textBudget.Take(value)) + "</td>");
                        }
                        html.Append("</tr>");
                        if (truncatedCells) break;
                    }
                    if (rowCount == 0) html.Append("<tr><td>此工作表没有可预览的单元格。</td></tr>");
                    html.Append("</tbody></table>");
                    if (truncatedCells) html.Append("<p class=\"note\">为保持流畅，已达到本文件的单元格或文本预览上限。</p>");
                    result.Pages.Add(new ReaderPage { Title = sheetName, Html = html.ToString() });
                    sheetNumber++;
                    if (truncatedCells) break;
                }

                if (result.Pages.Count == 0) throw new InvalidDataException("没有找到可预览的工作表。");
                if (truncatedSheets)
                    result.Pages[result.Pages.Count - 1].Html += "<p class=\"note\">为保持流畅，仅预览前 100 个工作表。</p>";
                return result;
            }
        }

        private static List<string> ReadSharedStrings(ZipArchive archive, XNamespace main, XmlReadBudget xmlBudget)
        {
            var values = new List<string>();
            var entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry == null) return values;
            var document = xmlBudget.Load(entry);
            var storageBudget = new TextBudget(MaxTextDocumentCharacters);
            foreach (var item in document.Root.Elements(main + "si").Take(MaxWorkbookCells))
                values.Add(storageBudget.Take(String.Concat(item.Descendants(main + "t").Select(text => text.Value))));
            return values;
        }

        private static ReaderDocument LoadPptx(string path)
        {
            using (var archive = ZipFile.OpenRead(path))
            {
                ValidateArchive(archive);
                XNamespace p = "http://schemas.openxmlformats.org/presentationml/2006/main";
                XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
                var slideEntries = archive.Entries
                    .Select(entry =>
                    {
                        var match = Regex.Match(entry.FullName, @"^ppt/slides/slide(\d+)\.xml$", RegexOptions.IgnoreCase);
                        int number;
                        return match.Success && Int32.TryParse(match.Groups[1].Value, NumberStyles.None,
                            CultureInfo.InvariantCulture, out number) && number > 0 && number <= MaxSlides
                            ? new { Entry = entry, Number = number }
                            : null;
                    })
                    .Where(item => item != null)
                    .OrderBy(item => item.Number)
                    .Take(MaxSlides)
                    .ToList();
                var result = new ReaderDocument { Title = Path.GetFileName(path) };
                int slideNumber = 0;
                var xmlBudget = new XmlReadBudget();
                var textBudget = new TextBudget(MaxTextDocumentCharacters);
                bool truncatedText = false;

                foreach (var item in slideEntries)
                {
                    if (textBudget.IsExhausted)
                    {
                        truncatedText = true;
                        break;
                    }
                    var slide = xmlBudget.Load(item.Entry);
                    var html = new StringBuilder("<div class=\"slide-card\"><div class=\"slide-number\">第 " + (++slideNumber) + " 页</div>");
                    foreach (var shape in slide.Descendants(p + "sp"))
                    {
                        var paragraphs = shape.Descendants(a + "p")
                            .Select(paragraph => String.Concat(paragraph.Descendants(a + "t").Select(text => text.Value)))
                            .Where(text => !String.IsNullOrWhiteSpace(text)).ToList();
                        if (paragraphs.Count == 0) continue;
                        foreach (var paragraph in paragraphs)
                        {
                            html.Append("<p>").Append(E(textBudget.Take(paragraph))).Append("</p>");
                            if (textBudget.IsExhausted)
                            {
                                truncatedText = true;
                                break;
                            }
                        }
                        if (textBudget.IsExhausted)
                            break;
                    }
                    if (truncatedText) html.Append("<p class=\"note\">为保持流畅，已达到本文件的文本预览上限。</p>");
                    html.Append("</div>");
                    result.Pages.Add(new ReaderPage { Title = "第 " + slideNumber + " 页", Html = html.ToString() });
                    if (truncatedText) break;
                }

                if (result.Pages.Count == 0) throw new InvalidDataException("没有找到可预览的幻灯片。");
                if (truncatedText)
                    result.Pages[result.Pages.Count - 1].Html += "<p class=\"note\">演示文稿其余内容未预览。</p>";
                return result;
            }
        }

        private static ReaderDocument LoadEpub(string path)
        {
            string root = Path.Combine(Path.GetTempPath(), "CHENMU Reader", "EPUB",
                Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                long extractedBytes = 0;
                using (var archive = ZipFile.OpenRead(path))
                {
                    ValidateArchive(archive);
                    foreach (var entry in archive.Entries)
                    {
                        string relative = entry.FullName.Replace('\\', '/');
                        if (String.IsNullOrWhiteSpace(relative) || relative.EndsWith("/", StringComparison.Ordinal)) continue;
                        var extension = Path.GetExtension(relative).ToLowerInvariant();
                        if ((extension == ".html" || extension == ".htm" || extension == ".xhtml" ||
                             extension == ".xml" || extension == ".opf" || extension == ".ncx") &&
                            entry.Length > MaxEpubHtmlBytes)
                            throw new InvalidDataException("EPUB 中的文本/XML 文件超过 8 MB，已停止打开以保护设备资源。");
                        string target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                        string rootPrefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
                        if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("EPUB 包含无效路径，已停止打开。");
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var input = entry.Open())
                        using (var output = File.Create(target))
                        {
                            var copied = CopyStreamBounded(input, output,
                                Math.Min(MaxArchiveEntryBytes, MaxArchiveBytes - extractedBytes));
                            if (copied != entry.Length)
                                throw new InvalidDataException("EPUB 条目解压长度与目录记录不一致。");
                            extractedBytes += copied;
                        }
                    }
                }

                string containerPath = Path.Combine(root, "META-INF", "container.xml");
                if (!File.Exists(containerPath)) throw new InvalidDataException("EPUB 文件缺少容器目录。");
                var packageXmlBudget = new XmlReadBudget();
                XDocument container = packageXmlBudget.LoadFile(containerPath);
                string packageRelative = (string)container.Descendants().FirstOrDefault(x => x.Name.LocalName == "rootfile")?.Attribute("full-path");
                if (String.IsNullOrWhiteSpace(packageRelative)) throw new InvalidDataException("EPUB 文件缺少书籍目录。");
                string packagePath = SafeExtractedPath(root, packageRelative);
                XDocument package = packageXmlBudget.LoadFile(packagePath);
                XNamespace opf = "http://www.idpf.org/2007/opf";
                string baseDirectory = (Path.GetDirectoryName(packageRelative) ?? "").Replace('\\', '/');
                var manifest = package.Descendants(opf + "item")
                    .Where(item => item.Attribute("id") != null && item.Attribute("href") != null)
                    .ToDictionary(item => (string)item.Attribute("id"), item => (string)item.Attribute("href"));
                var result = new ReaderDocument { Title = Path.GetFileName(path), TemporaryRoot = root };
                string metadataTitle = package.Descendants().FirstOrDefault(x => x.Name.LocalName == "title")?.Value;
                if (!String.IsNullOrWhiteSpace(metadataTitle)) result.Title = metadataTitle.Trim();
                int chapter = 0;
                bool truncatedChapters = false;

                foreach (var itemref in package.Descendants(opf + "itemref"))
                {
                    if (chapter >= MaxEpubChapters)
                    {
                        truncatedChapters = true;
                        break;
                    }
                    string idref = (string)itemref.Attribute("idref");
                    if (idref == null || !manifest.ContainsKey(idref)) continue;
                    string href = manifest[idref].Split('#')[0];
                    string contentPath = SafeExtractedPath(root, CombinePackagePath(baseDirectory, Uri.UnescapeDataString(href)));
                    if (!File.Exists(contentPath)) continue;
                    string chapterTitle = Path.GetFileNameWithoutExtension(contentPath);
                    try
                    {
                        var contentInfo = new FileInfo(contentPath);
                        if (contentInfo.Length > MaxEpubHtmlBytes)
                            throw new InvalidDataException("EPUB 章节超过 8 MB，已停止打开以保护设备资源。");
                        XDocument content = new XmlReadBudget().LoadFile(contentPath);
                        string found = content.Descendants().FirstOrDefault(x => x.Name.LocalName == "title")?.Value;
                        if (String.IsNullOrWhiteSpace(found)) found = content.Descendants().FirstOrDefault(x => x.Name.LocalName == "h1")?.Value;
                        if (!String.IsNullOrWhiteSpace(found)) chapterTitle = found.Trim();
                    }
                    catch (InvalidDataException) { throw; }
                    catch { }
                    result.Pages.Add(new ReaderPage { Title = chapterTitle, FilePath = contentPath });
                    chapter++;
                }

                if (result.Pages.Count == 0) throw new InvalidDataException("没有找到 EPUB 正文内容。");
                if (truncatedChapters)
                    result.Pages.Add(new ReaderPage { Title = "预览说明", Html = "<p>为保持流畅，CHENMU Reader 最多预览前 500 个 EPUB 章节。</p>" });
                return result;
            }
            catch
            {
                ReaderTemporaryDirectoryCleanup.ScheduleDelete(root);
                throw;
            }
        }

        private static string SafeExtractedPath(string root, string relative)
        {
            relative = Uri.UnescapeDataString((relative ?? "").Replace('/', Path.DirectorySeparatorChar));
            string full = Path.GetFullPath(Path.Combine(root, relative));
            string prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("EPUB 引用了包外路径。");
            return full;
        }

        private static void ValidateArchive(ZipArchive archive)
        {
            if (archive.Entries.Count > MaxArchiveEntries)
                throw new InvalidDataException("压缩文档包含过多条目，已停止打开以保护设备资源。");
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Length > MaxArchiveEntryNameLength)
                    throw new InvalidDataException("压缩文档包含过长的文件路径，已停止打开。");
                if (entry.Length > MaxArchiveEntryBytes)
                    throw new InvalidDataException("压缩文档中的单个内容超过 100 MB，已停止打开。");
                total += entry.Length;
                if (total > MaxArchiveBytes)
                    throw new InvalidDataException("压缩文档解压后超过 350 MB，已停止打开以保护设备空间。");
            }
        }

        private static string CombinePackagePath(string baseDirectory, string target)
        {
            var parts = new List<string>();
            foreach (string part in ((baseDirectory ?? "") + "/" + (target ?? "")).Replace('\\', '/').Split('/'))
            {
                if (part == "" || part == ".") continue;
                if (part == "..")
                {
                    if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                    else throw new InvalidDataException("EPUB 引用了无效路径。");
                }
                else parts.Add(part);
            }
            return String.Join("/", parts);
        }

        private static string ResolvePackagePath(string baseDirectory, string target)
        {
            if (String.IsNullOrWhiteSpace(target)) return "";
            if (target.StartsWith("/", StringComparison.Ordinal)) return target.TrimStart('/');
            return CombinePackagePath(baseDirectory, target);
        }

        private static void EnsureFileSize(string path, long maximumBytes, string description)
        {
            var info = new FileInfo(path);
            if (info.Length > maximumBytes)
                throw new InvalidDataException(description + "超过安全读取上限，已停止打开以保护设备资源。");
        }

        private static long CopyStreamBounded(Stream input, Stream output, long maximumBytes)
        {
            var buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (copied > maximumBytes - read)
                    throw new InvalidDataException("解压内容超过安全读取上限，已停止打开以保护设备资源。");
                output.Write(buffer, 0, read);
                copied += read;
            }
            return copied;
        }

        private sealed class XmlReadBudget
        {
            private long _bytesRead;

            public XDocument Load(ZipArchiveEntry entry)
            {
                if (entry == null)
                    throw new ArgumentNullException(nameof(entry));
                if (entry.Length > MaxXmlEntryBytes)
                    throw new InvalidDataException("压缩文档中的 XML 超过 8 MB，已停止打开以保护设备资源。");
                using (var input = entry.Open())
                    return LoadStream(input, entry.Length);
            }

            public XDocument LoadFile(string path)
            {
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    return LoadStream(input, input.Length);
            }

            private XDocument LoadStream(Stream input, long declaredLength)
            {
                if (declaredLength < 0 || declaredLength > MaxXmlEntryBytes ||
                    _bytesRead > MaxXmlAggregateBytes - declaredLength)
                    throw new InvalidDataException("文档 XML 总量超过安全读取预算，已停止打开以保护设备资源。");

                using (var buffer = new MemoryStream((int)declaredLength))
                {
                    var copied = CopyStreamBounded(input, buffer,
                        Math.Min(MaxXmlEntryBytes, MaxXmlAggregateBytes - _bytesRead));
                    if (copied != declaredLength)
                        throw new InvalidDataException("XML 文件长度与目录记录不一致。");
                    _bytesRead += copied;
                    buffer.Position = 0;
                    var settings = new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersInDocument = MaxXmlEntryBytes,
                        MaxCharactersFromEntities = 1024
                    };
                    using (var reader = XmlReader.Create(buffer, settings))
                        return XDocument.Load(reader, LoadOptions.None);
                }
            }
        }

        private sealed class TextBudget
        {
            private int _remaining;

            public TextBudget(int maximumCharacters)
            {
                _remaining = Math.Max(0, maximumCharacters);
            }

            public bool IsExhausted { get { return _remaining <= 0; } }

            public string Take(string value)
            {
                value = value ?? String.Empty;
                if (_remaining <= 0)
                    return String.Empty;
                if (value.Length <= _remaining)
                {
                    _remaining -= value.Length;
                    return value;
                }
                var result = value.Substring(0, _remaining);
                _remaining = 0;
                return result;
            }
        }

        private static string CsvToHtml(string text)
        {
            var rows = ParseCsv(text).Take(3000).ToList();
            var html = new StringBuilder("<table class=\"sheet\"><tbody>");
            foreach (var row in rows)
            {
                html.Append("<tr>");
                foreach (var value in row.Take(80)) html.Append("<td>" + E(value) + "</td>");
                html.Append("</tr>");
            }
            html.Append("</tbody></table>");
            if (rows.Count == 0) html.Append("<p>文件中没有内容。</p>");
            else if (rows.Count == 3000) html.Append("<p class=\"note\">为保持流畅，仅预览前 3,000 行。</p>");
            return html.ToString();
        }

        private static IEnumerable<List<string>> ParseCsv(string text)
        {
            var row = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        if (row.Count < MaxCsvColumns) cell.Append('"');
                        i++;
                    }
                    else if (c == '"') quoted = false;
                    else if (row.Count < MaxCsvColumns) cell.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',')
                {
                    if (row.Count < MaxCsvColumns)
                        row.Add(cell.ToString());
                    cell.Clear();
                }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    if (row.Count < MaxCsvColumns)
                        row.Add(cell.ToString());
                    cell.Clear();
                    if (row.Count > 1 || row[0].Length > 0) yield return row;
                    row = new List<string>();
                }
                else if (row.Count < MaxCsvColumns) cell.Append(c);
            }
            if (row.Count < MaxCsvColumns)
                row.Add(cell.ToString());
            if (row.Count > 1 || row[0].Length > 0) yield return row;
        }

        private static string MarkdownToHtml(string text)
        {
            var html = new StringBuilder();
            bool inCode = false;
            bool inList = false;
            bool inParagraph = false;
            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            int lineCount = 1;
            foreach (char value in normalized)
                if (value == '\n' && ++lineCount > MaxMarkdownLines)
                    throw new InvalidDataException("Markdown 文件包含过多行，已停止打开以保护设备资源。");
            foreach (string line in normalized.Split('\n'))
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    CloseParagraph(html, ref inParagraph);
                    CloseList(html, ref inList);
                    if (!inCode) html.Append("<pre><code>"); else html.Append("</code></pre>");
                    inCode = !inCode;
                    continue;
                }
                if (inCode) { html.Append(E(line)).Append('\n'); continue; }
                string trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    CloseParagraph(html, ref inParagraph);
                    CloseList(html, ref inList);
                    continue;
                }
                int heading = 0;
                while (heading < trimmed.Length && heading < 6 && trimmed[heading] == '#') heading++;
                if (heading > 0 && heading < trimmed.Length && Char.IsWhiteSpace(trimmed[heading]))
                {
                    CloseParagraph(html, ref inParagraph);
                    CloseList(html, ref inList);
                    html.Append("<h").Append(heading).Append('>').Append(InlineMarkdown(trimmed.Substring(heading).Trim())).Append("</h").Append(heading).Append('>');
                    continue;
                }
                if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
                {
                    CloseParagraph(html, ref inParagraph);
                    if (!inList) { html.Append("<ul>"); inList = true; }
                    html.Append("<li>").Append(InlineMarkdown(trimmed.Substring(2))).Append("</li>");
                    continue;
                }
                CloseList(html, ref inList);
                if (!inParagraph) { html.Append("<p>"); inParagraph = true; } else html.Append("<br>");
                html.Append(InlineMarkdown(trimmed));
            }
            CloseParagraph(html, ref inParagraph);
            CloseList(html, ref inList);
            if (inCode) html.Append("</code></pre>");
            return html.ToString();
        }

        private static string InlineMarkdown(string text)
        {
            if (text.Length > MaxInlineMarkdownLineCharacters)
                return E(text);
            string safe = E(text);
            safe = Regex.Replace(safe, @"`([^`]+)`", "<code>$1</code>");
            safe = Regex.Replace(safe, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
            safe = Regex.Replace(safe, @"\*(.+?)\*", "<em>$1</em>");
            safe = Regex.Replace(safe, @"~~(.+?)~~", "<del>$1</del>");
            return safe;
        }

        private static void CloseParagraph(StringBuilder html, ref bool open)
        {
            if (open) { html.Append("</p>"); open = false; }
        }

        private static void CloseList(StringBuilder html, ref bool open)
        {
            if (open) { html.Append("</ul>"); open = false; }
        }

        private static string ReadText(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > MaxTextDocumentBytes)
                    throw new InvalidDataException("文本文件超过 8 MB，已停止打开以保护设备资源。");
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    var builder = new StringBuilder();
                    var buffer = new char[8192];
                    int read;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (builder.Length > MaxTextDocumentCharacters - read)
                            throw new InvalidDataException("文本文件超过 4,000,000 个字符，已停止打开以保护设备资源。");
                        builder.Append(buffer, 0, read);
                    }
                    return builder.ToString();
                }
            }
        }

        private static string E(string value) { return WebUtility.HtmlEncode(value ?? ""); }
    }

    internal static class ReaderTemporaryDirectoryCleanup
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<string> ActiveRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> PendingRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static System.Threading.Timer _retryTimer;

        private static string ManagedRoot
        {
            get { return Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CHENMU Reader", "EPUB")); }
        }

        public static void CleanupStaleProcessRoots()
        {
            var root = ManagedRoot;
            if (!Directory.Exists(root))
                return;
            string[] children;
            try { children = Directory.GetDirectories(root); }
            catch { return; }
            foreach (var child in children)
            {
                int processId;
                if (!Int32.TryParse(Path.GetFileName(child), NumberStyles.None, CultureInfo.InvariantCulture, out processId) ||
                    IsProcessRunning(processId))
                    continue;
                ScheduleDelete(child);
            }
            RetryPending();
        }

        public static void RegisterActive(string path)
        {
            string normalized;
            if (!TryNormalizeManagedPath(path, out normalized))
                return;
            lock (Gate)
            {
                PendingRoots.Remove(normalized);
                ActiveRoots.Add(normalized);
            }
        }

        public static void ReleaseActive(string path)
        {
            string normalized;
            if (!TryNormalizeManagedPath(path, out normalized))
                return;
            lock (Gate)
            {
                ActiveRoots.Remove(normalized);
                TryDeleteOrQueueLocked(normalized);
            }
        }

        public static void ScheduleDelete(string path)
        {
            string normalized;
            if (!TryNormalizeManagedPath(path, out normalized))
                return;
            lock (Gate)
            {
                ActiveRoots.Remove(normalized);
                TryDeleteOrQueueLocked(normalized);
            }
        }

        private static bool TryNormalizeManagedPath(string path, out string normalized)
        {
            normalized = null;
            if (String.IsNullOrWhiteSpace(path))
                return false;
            try
            {
                var full = Path.GetFullPath(path);
                var prefix = ManagedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;
                normalized = full;
                return true;
            }
            catch { return false; }
        }

        private static void TryDeleteOrQueueLocked(string path)
        {
            if (TryDelete(path))
            {
                PendingRoots.Remove(path);
                StopRetryTimerIfIdleLocked();
                return;
            }
            PendingRoots.Add(path);
            if (_retryTimer == null)
                _retryTimer = new System.Threading.Timer(delegate { RetryPending(); }, null,
                    TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        private static void RetryPending()
        {
            lock (Gate)
            {
                foreach (var path in PendingRoots.ToArray())
                {
                    if (ActiveRoots.Contains(path))
                        continue;
                    if (TryDelete(path))
                        PendingRoots.Remove(path);
                }
                StopRetryTimerIfIdleLocked();
            }
        }

        private static void StopRetryTimerIfIdleLocked()
        {
            if (PendingRoots.Count == 0 && _retryTimer != null)
            {
                _retryTimer.Dispose();
                _retryTimer = null;
            }
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
                return !Directory.Exists(path);
            }
            catch { return false; }
        }

        private static bool IsProcessRunning(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                    return !process.HasExited;
            }
            catch { return false; }
        }
    }
}
