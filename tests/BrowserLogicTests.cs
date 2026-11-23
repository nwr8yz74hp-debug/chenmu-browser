using System;
using System.Collections.Generic;
using System.Linq;
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

    private static int Main()
    {
        TestBookmarkHtmlImportExport();
        TestBookmarkHtmlVariations();
        TestDomainRules();
        Console.WriteLine("通过：" + _assertions + " 项逻辑断言。");
        return 0;
    }
}
