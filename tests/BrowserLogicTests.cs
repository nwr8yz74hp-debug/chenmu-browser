using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ChenmuBrowser;

internal static class BrowserLogicTests
{
    private static int _assertions;

    private static void Assert(bool condition, string description)
    {
        _assertions++;
        if (!condition)
            throw new InvalidOperationException("失败：" + description);
    }

    private static void TestBookmarkHtmlImportExport()
    {
        var html = "<!DOCTYPE NETSCAPE-Bookmark-file-1>\n" +
            "<!-- <A HREF=\"https://comment.example\">not a bookmark</A> -->\n" +
            "<DL><p>\n" +
            "<DT><A HREF=\"https://example.com/path?a=1&amp;b=2\">Home &amp; Search</A>\n" +
            "<DT><H3 ADD_DATE=\"0\">Bookmarks bar</H3>\n" +
            "<DL><p>\n" +
            "<DT><A ADD_DATE=\"123\" HREF='https://news.example.org/story'>News</A>\n" +
            "<DT><H3>Research &amp; notes</H3>\n" +
            "<DL><p><DT><A HREF=\"https://docs.example.net/中文\">资料 &amp; 文档</A></DL><p>\n" +
            "</DL><p>\n" +
            "<DT><A HREF=\"javascript:alert(1)\">unsafe</A>\n" +
            "<DT><A HREF=\"file:///C:/secret.txt\">local</A>\n" +
            "</DL><p>";

        var parsed = BookmarkHtmlTransfer.Parse(html);
        Assert(parsed.Count == 3, "只接受可用的 HTTP(S) 书签并忽略注释内容");
        Assert(parsed[0].Title == "Home & Search", "HTML 实体解码书签标题");
        Assert(parsed[0].Url.Contains("a=1&b=2"), "HTML 实体解码书签地址");
        Assert(parsed[0].FolderPath == String.Empty, "保留根目录书签");
        Assert(parsed[1].FolderPath == "Bookmarks bar", "保留一级文件夹");
        Assert(parsed[2].FolderPath == "Bookmarks bar" + BookmarkHtmlTransfer.FolderSeparator + "Research & notes",
            "保留嵌套文件夹和实体文本");

        var exported = BookmarkHtmlTransfer.Export(parsed);
        Assert(exported.StartsWith("<!DOCTYPE NETSCAPE-Bookmark-file-1>", StringComparison.Ordinal),
            "导出使用 Netscape 书签 HTML 标准头");
        Assert(exported.Contains("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">"),
            "导出明确标注 UTF-8");
        Assert(exported.Contains("<H3 ADD_DATE=\"0\">Bookmarks bar</H3>"), "导出文件夹层级");

        var roundTrip = BookmarkHtmlTransfer.Parse(exported);
        Assert(roundTrip.Count == parsed.Count, "导出后可重新导入全部书签");
        for (var index = 0; index < parsed.Count; index++)
        {
            Assert(roundTrip[index].Title == parsed[index].Title, "往返保留标题 " + index);
            Assert(roundTrip[index].Url == parsed[index].Url, "往返保留地址 " + index);
            Assert(roundTrip[index].FolderPath == parsed[index].FolderPath, "往返保留文件夹 " + index);
        }
    }

    private static void TestBookmarkHtmlVariations()
    {
        var html = "<html><body><DL><p>" +
            "<dt><a data-x='1' href=https://plain.example/path>Plain title</a>" +
            "<DT><A HREF='https://quotes.example/?x=1&amp;y=2'>A <B>bold</B> title</A>" +
            "</DL><p></body></html>";
        var parsed = BookmarkHtmlTransfer.Parse(html);
        Assert(parsed.Count == 2, "兼容标签大小写、属性顺序和无引号网址");
        Assert(parsed[0].Title == "Plain title", "读取无引号 HREF");
        Assert(parsed[1].Title == "A bold title", "标题内标签被安全移除");
        Assert(parsed[1].Url.Contains("x=1&y=2"), "读取单引号属性和实体网址");
        Assert(BookmarkHtmlTransfer.Parse("<html>no bookmarks</html>").Count == 0, "空 HTML 可安全处理");
    }

    private static void TestDomainRules()
    {
        var blocked = new[] { "doubleclick.net", ".tracker.example." };
        var allowed = new[] { "shop.example.com" };
        Assert(DomainRuleMatcher.NormalizeHost(" .Example.COM. ") == "example.com", "域名大小写和点号规范化");
        Assert(DomainRuleMatcher.ShouldBlock("doubleclick.net", blocked, allowed), "精确黑名单命中");
        Assert(DomainRuleMatcher.ShouldBlock("ads.doubleclick.net", blocked, allowed), "黑名单规则匹配子域名");
        Assert(DomainRuleMatcher.ShouldBlock("cdn.tracker.example", blocked, allowed), "前后点输入也规范化");
        Assert(!DomainRuleMatcher.ShouldBlock("shop.example.com", blocked, allowed), "允许列表放行精确站点");
        Assert(!DomainRuleMatcher.ShouldBlock("img.shop.example.com", blocked, allowed), "允许列表放行站点子域名");
        Assert(DomainRuleMatcher.ShouldBlock("shop.example.com.evil.test", new[] { "evil.test" }, allowed),
            "允许站点名伪造的子串不应绕过另一条黑名单规则");
        Assert(!DomainRuleMatcher.ShouldBlock("notexample.com", new[] { "example.com" }, null),
            "域名后缀匹配需要分隔点");
        Assert(!DomainRuleMatcher.ShouldBlock("doubleclick.net", null, null), "空规则表不拦截");
        Assert(DomainRuleMatcher.NormalizeHost("https://example.com/path").Length == 0, "拒绝带协议和路径的主机规则");
    }

    private static void TestAtomicTextFile()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "settings.ini");
            AtomicTextFile.WriteAllLines(path, new[] { "first=1", "second=two" });
            AtomicTextFile.WriteAllLines(path, new[] { "updated=true" });
            Assert(File.ReadAllLines(path, Encoding.UTF8).SequenceEqual(new[] { "updated=true" }), "原子替换完整更新现有文件");
            Assert(Directory.GetFiles(root, "*.tmp").Length == 0, "原子替换后清理临时文件");
        }
        finally { TryDelete(root); }
    }

    private static void TestReaderPathBoundary()
    {
        var root = CreateTempDirectory();
        var allowed = Path.Combine(root, "reader");
        var outside = Path.Combine(root, "reader-private");
        Directory.CreateDirectory(allowed);
        Directory.CreateDirectory(outside);
        var inFile = Path.Combine(allowed, "page.html");
        var outFile = Path.Combine(outside, "secret.svg");
        File.WriteAllText(inFile, "<html></html>");
        File.WriteAllText(outFile, "<svg></svg>");
        try
        {
            Assert(ReaderPathPolicy.IsWithinRoot(inFile, allowed), "允许 Reader 根目录内路径");
            Assert(!ReaderPathPolicy.IsWithinRoot(outFile, allowed), "拒绝 Reader 根目录外路径");
            Assert(!ReaderPathPolicy.IsWithinRoot(Path.Combine(root, "reader-sibling", "secret.svg"), allowed),
                "不因目录名前缀相同而放行");
            Assert(ReaderPathPolicy.IsAllowedLocalPath(inFile, allowed), "本地文件 URI 的根目录判定通过");
            Assert(!ReaderPathPolicy.IsAllowedLocalPath(outFile, allowed), "本地文件 URI 的越界路径被拒绝");
            Assert(ReaderPathPolicy.IsAllowedFileUri(new Uri(inFile).AbsoluteUri, allowed), "允许根目录内 file URI");
            Assert(!ReaderPathPolicy.IsAllowedFileUri(new Uri(outFile).AbsoluteUri, allowed), "拒绝根目录外 file URI");
        }
        finally { TryDelete(root); }
    }

    private static void TestBrowserHistoryStorage()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "history.tsv");
            var legacy = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture) + "\t" +
                "legacy-id\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes("旧标题")) + "\t" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes("https://example.com/old"));
            AtomicTextFile.WriteAllLines(path, new[] { legacy });
            var readLegacy = BrowserHistoryStorage.Read(path);
            Assert(readLegacy.Count == 1 && readLegacy[0].Title == "旧标题", "兼容读取 v1.3.0 历史 TSV 格式");

            var now = DateTime.UtcNow;
            var recent = new BrowserHistoryRecord { Id = "recent", VisitedUtc = now, Title = "recent", Url = "https://example.com" };
            var old = new BrowserHistoryRecord { Id = "old", VisitedUtc = now.AddDays(-20), Title = "old", Url = "https://old.example.com" };
            var retained = BrowserHistoryStorage.Prune(new[] { old, recent }, 7, now);
            Assert(retained.Count == 1 && retained[0].Id == "recent", "历史保留期裁掉过期条目");

            var many = Enumerable.Range(0, BrowserHistoryStorage.MaximumEntries + 12)
                .Select(index => new BrowserHistoryRecord
                {
                    Id = "id" + index,
                    VisitedUtc = now.AddSeconds(-index),
                    Title = "页面" + index,
                    Url = "https://example.com/" + index
                }).ToList();
            var bounded = BrowserHistoryStorage.Prune(many, 0, now);
            Assert(bounded.Count == BrowserHistoryStorage.MaximumEntries, "历史记录数量限制生效");
            Assert(bounded.Sum(entry => Encoding.UTF8.GetByteCount(entry.Id + entry.Title + entry.Url)) < BrowserHistoryStorage.MaximumSerializedBytes,
                "测试记录低于历史字节容量限制");
            BrowserHistoryStorage.Write(path, bounded);
            Assert(new FileInfo(path).Length <= BrowserHistoryStorage.MaximumSerializedBytes, "历史文件保持在 2 MiB 上限内");
        }
        finally { TryDelete(root); }
    }

    private static void TestBrowserHistoryOversizedRead()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "oversized-history.tsv");
            var now = DateTime.UtcNow;
            string title = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('x', 200)));
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                for (int index = 0; index < 21000; index++)
                {
                    string url = Convert.ToBase64String(Encoding.UTF8.GetBytes("https://example.com/" + index));
                    writer.WriteLine(now.AddSeconds(-index).ToString("o", System.Globalization.CultureInfo.InvariantCulture) +
                        "\told" + index + "\t" + title + "\t" + url);
                }
                writer.WriteLine(now.AddMinutes(1).ToString("o", System.Globalization.CultureInfo.InvariantCulture) +
                    "\tlatest-tail\t" + title + "\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes("https://example.com/latest")));
            }

            Assert(new FileInfo(path).Length > BrowserHistoryStorage.MaximumSerializedBytes,
                "超量历史测试文件超过 2 MiB 读取窗口");
            var loaded = BrowserHistoryStorage.Read(path);
            Assert(loaded.Count <= BrowserHistoryStorage.MaximumEntries, "超量文件读取仍限制为最多 5,000 条");
            Assert(loaded.Any(entry => entry.Id == "latest-tail"), "超量文件尾部最新记录不会被前部旧记录遮蔽");
        }
        finally { TryDelete(root); }
    }

    private static void TestDownloadHistoryStorage()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "downloads.tsv");
            var now = DateTime.UtcNow;
            var records = new[]
            {
                new DownloadHistoryRecord
                {
                    Id = "completed-1", StartedUtc = now, FinishedUtc = now, FilePath = Path.Combine(root, "file.bin"),
                    FileName = "file.bin", Status = DownloadRecordStatus.Completed, BytesReceived = 42, TotalBytes = 42
                },
                new DownloadHistoryRecord
                {
                    Id = "active-1", StartedUtc = now.AddMinutes(-1), FilePath = Path.Combine(root, "active.bin"),
                    FileName = "active.bin", Status = DownloadRecordStatus.InProgress, BytesReceived = 5
                }
            };
            DownloadHistoryStorage.Write(path, records);
            var loaded = DownloadHistoryStorage.Read(path, 30, now);
            Assert(loaded.Count == 2, "下载元数据跨重启读取");
            Assert(loaded.Any(item => item.Id == "completed-1" && item.Status == DownloadRecordStatus.Completed), "完成下载状态与大小被保留");
            Assert(loaded.Any(item => item.Id == "active-1" && item.Status == DownloadRecordStatus.Interrupted), "重启时将活动下载标记为中断");

            var old = new DownloadHistoryRecord
            {
                Id = "expired-1", StartedUtc = now.AddDays(-40), FilePath = Path.Combine(root, "old.bin"),
                FileName = "old.bin", Status = DownloadRecordStatus.Completed
            };
            Assert(DownloadHistoryStorage.Prune(new[] { old, records[0] }, 7, now).Count == 1, "下载记录保留期过滤生效");
            var many = Enumerable.Range(0, DownloadHistoryStorage.MaximumEntries + 5).Select(index =>
                new DownloadHistoryRecord
                {
                    Id = "d" + index, StartedUtc = now.AddSeconds(-index), FilePath = Path.Combine(root, "d" + index),
                    FileName = "d" + index, Status = DownloadRecordStatus.Completed
                });
            Assert(DownloadHistoryStorage.Prune(many, 0, now).Count == DownloadHistoryStorage.MaximumEntries, "下载元数据容量上限生效");
        }
        finally { TryDelete(root); }
    }

    private static void TestSitePermissionStorage()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "site-permissions.tsv");
            Assert(SitePermissionStorage.NormalizeSecureOrigin("https://EXAMPLE.com:443/a/b?q=1") == "https://example.com",
                "HTTPS 来源按 origin 规范化");
            Assert(SitePermissionStorage.NormalizeSecureOrigin("http://example.com") == null, "拒绝非 HTTPS 来源");
            var rules = new List<SitePermissionRule>
            {
                new SitePermissionRule { Origin = "https://example.com/path", Allowed = new HashSet<SitePermissionKind> { SitePermissionKind.Camera, SitePermissionKind.Geolocation } }
            };
            SitePermissionStorage.Write(path, rules);
            var read = SitePermissionStorage.Read(path);
            Assert(SitePermissionStorage.IsAllowed(read, "https://example.com/other", SitePermissionKind.Camera), "同一 HTTPS origin 的授权生效");
            Assert(!SitePermissionStorage.IsAllowed(read, "https://sub.example.com", SitePermissionKind.Camera), "授权不继承到子域名");
            Assert(!SitePermissionStorage.IsAllowed(read, "https://example.com", SitePermissionKind.Microphone), "未勾选的权限默认拒绝");
            Assert(!SitePermissionStorage.IsAllowed(read, "http://example.com", SitePermissionKind.Camera), "HTTP 站点保持默认拒绝");
        }
        finally { TryDelete(root); }
    }

    private static void TestReaderPositionStorage()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "reader-positions.tsv");
            var document = Path.Combine(root, "sample.epub");
            File.WriteAllText(document, "test");
            ReaderPositionStorage.Save(path, document, 4);
            Assert(ReaderPositionStorage.GetPageIndex(path, document, 9) == 4, "Reader 位置可按文档路径恢复");
            Assert(ReaderPositionStorage.GetPageIndex(path, document, 3) == 2, "文档页数变化时位置安全夹取");
            ReaderPositionStorage.Clear(path);
            Assert(!File.Exists(path) && ReaderPositionStorage.GetPageIndex(path, document, 9) == 0, "关闭恢复后清除本机位置数据");
        }
        finally { TryDelete(root); }
    }

    private static void TestUpdateNoticeVersionIsolation()
    {
        var cumulativeNotes = "# CHENMU Browser v1.4.2 更新说明\n" +
            "- 修复新标签页壁纸被白色遮罩洗淡的问题；缓存适配当前窗口尺寸的壁纸绘制帧，减少重开、换图和重绘时的白屏闪烁与卡顿。\n" +
            "# CHENMU Browser v1.4.1 更新说明\n" +
            "- 修复主页更换壁纸菜单过早释放的问题。\n" +
            "# CHENMU Browser v1.4.0 更新说明\n" +
            "- 收紧 Reader 的本地文件边界。\n";

        var currentNotes = UpdateNoticeContent.ReadCurrentReleaseNotes(cumulativeNotes, new Version(1, 4, 2));
        Assert(currentNotes == "• 修复新标签页壁纸被白色遮罩洗淡的问题；缓存适配当前窗口尺寸的壁纸绘制帧，减少重开、换图和重绘时的白屏闪烁与卡顿。",
            "升级说明只提取当前版本标题下的条目");
        var message = UpdateNoticeContent.BuildMessage(new Version(1, 4, 2), currentNotes);
        Assert(message.Contains("已更新至 v1.4.2") && message.Contains("更新内容："), "更新弹窗正文标明当前版本和更新内容");
        Assert(!message.Contains("更换壁纸菜单过早释放") && !message.Contains("收紧 Reader"),
            "更新弹窗不包含旧版本累计说明");

        var v143Notes = "# CHENMU Browser v1.4.3 更新说明\n- 仅展示当前版本的更新说明。\n" + cumulativeNotes;
        Assert(UpdateNoticeContent.ReadCurrentReleaseNotes(v143Notes, new Version(1, 4, 3)) ==
            "• 仅展示当前版本的更新说明。", "后续版本只读取自己的说明区块");
        Assert(UpdateNoticeContent.ReadCurrentReleaseNotes(cumulativeNotes, new Version(1, 4, 3)) == String.Empty,
            "缺少目标版本区块时不误用其他版本说明");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "chenmu-logic-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { }
    }

    private static int Main()
    {
        TestBookmarkHtmlImportExport();
        TestBookmarkHtmlVariations();
        TestDomainRules();
        TestAtomicTextFile();
        TestReaderPathBoundary();
        TestBrowserHistoryStorage();
        TestBrowserHistoryOversizedRead();
        TestDownloadHistoryStorage();
        TestSitePermissionStorage();
        TestReaderPositionStorage();
        TestUpdateNoticeVersionIsolation();
        Console.WriteLine("通过：" + _assertions + " 项逻辑断言。");
        return 0;
    }
}
