using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using ChenmuBrowser;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class WindowsWebView2SmokeTests
{
    private static Exception _failure;

    [STAThread]
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "chenmu-webview2-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            using (var server = new LocalTestServer())
            using (var browser = new BrowserForm(Path.Combine(root, "browser-data"), true, 1))
            {
                browser.Shown += async delegate
                {
                    try
                    {
                        await TestDefaultDenyPermissionsAsync(browser, server);
                        await TestReaderSubresourceBoundaryAsync(root);
                        Console.WriteLine("PASS: WebView2 permission default-deny and Reader HTML/SVG subresource boundary.");
                    }
                    catch (Exception ex)
                    {
                        _failure = ex;
                        Console.Error.WriteLine(ex);
                    }
                    finally
                    {
                        try { browser.Close(); }
                        catch { }
                    }
                };
                Application.Run(browser);
            }
            if (_failure != null)
                throw _failure;
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task TestDefaultDenyPermissionsAsync(BrowserForm browser, LocalTestServer server)
    {
        var navigate = typeof(BrowserForm).GetMethod("NavigateAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        if (navigate == null)
            throw new InvalidOperationException("BrowserForm.NavigateAsync was not found.");
        await (Task)navigate.Invoke(browser, new object[] { server.Url });

        var webViewField = typeof(BrowserForm).GetField("_webView", BindingFlags.Instance | BindingFlags.NonPublic);
        var webView = webViewField == null ? null : webViewField.GetValue(browser) as WebView2;
        if (webView == null || webView.CoreWebView2 == null)
            throw new InvalidOperationException("The browser WebView2 control did not initialize.");
        var core = webView.CoreWebView2;
        var seen = new Dictionary<CoreWebView2PermissionKind, ObservedPermission>();
        core.PermissionRequested += delegate(object sender, CoreWebView2PermissionRequestedEventArgs args)
        {
            if (IsTargetPermission(args.PermissionKind))
            {
                lock (seen)
                    seen[args.PermissionKind] = new ObservedPermission
                    {
                        State = args.State,
                        Handled = args.Handled,
                        SavesInProfile = args.SavesInProfile
                    };
            }
        };

        const string requestScript = "(function(){" +
            "try{if(window.Notification)Notification.requestPermission().catch(function(){});}catch(e){}" +
            "try{if(navigator.geolocation)navigator.geolocation.getCurrentPosition(function(){},function(){});}catch(e){}" +
            "try{if(navigator.mediaDevices&&navigator.mediaDevices.getUserMedia)navigator.mediaDevices.getUserMedia({video:true}).catch(function(){});}catch(e){}" +
            "try{if(navigator.mediaDevices&&navigator.mediaDevices.getUserMedia)navigator.mediaDevices.getUserMedia({audio:true}).catch(function(){});}catch(e){}" +
            "return 'requested';})()";
        await core.ExecuteScriptAsync(requestScript);
        DateTime deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            lock (seen)
                if (seen.Count >= 4)
                    break;
            await Task.Delay(200);
        }

        lock (seen)
        {
            foreach (var kind in new[]
            {
                CoreWebView2PermissionKind.Camera,
                CoreWebView2PermissionKind.Microphone,
                CoreWebView2PermissionKind.Geolocation,
                CoreWebView2PermissionKind.Notifications
            })
            {
                ObservedPermission observed;
                if (!seen.TryGetValue(kind, out observed))
                    throw new InvalidOperationException("WebView2 did not raise PermissionRequested for " + kind + ".");
                if (observed.State != CoreWebView2PermissionState.Deny || !observed.Handled || observed.SavesInProfile)
                    throw new InvalidOperationException("Permission " + kind + " was not synchronously denied without profile persistence.");
            }
        }
        Console.WriteLine("PASS: camera, microphone, geolocation and notifications are denied without a modal/profile prompt.");
    }

    private static async Task TestReaderSubresourceBoundaryAsync(string root)
    {
        string allowedRoot = Path.Combine(root, "reader-allowed");
        string outsideRoot = Path.Combine(root, "reader-outside");
        Directory.CreateDirectory(allowedRoot);
        Directory.CreateDirectory(outsideRoot);
        string outsidePng = Path.Combine(outsideRoot, "secret.png");
        string outsideSvg = Path.Combine(outsideRoot, "secret.svg");
        using (var bitmap = new Bitmap(2, 2))
            bitmap.Save(outsidePng, System.Drawing.Imaging.ImageFormat.Png);
        File.WriteAllText(outsideSvg,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"4\" height=\"4\"><rect width=\"4\" height=\"4\" fill=\"red\"/></svg>");

        string htmlPath = Path.Combine(allowedRoot, "test.html");
        File.WriteAllText(htmlPath,
            "<!doctype html><html><body><img src=\"" + ToFileUri(outsidePng) + "\"><img src=\"" +
            ToFileUri(outsideSvg) + "\"></body></html>");
        string svgPath = Path.Combine(allowedRoot, "test.svg");
        File.WriteAllText(svgPath,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"12\" height=\"12\"><image xlink:href=\"" +
            ToFileUri(outsidePng) + "\" width=\"12\" height=\"12\"/></svg>");

        string profile = Path.Combine(root, "reader-profile");
        var environment = await CoreWebView2Environment.CreateAsync(null, profile, null);
        var readerType = typeof(BrowserForm).Assembly.GetType("ChenmuBrowser.ReaderView", true);
        var getEnvironment = new Func<Task<CoreWebView2Environment>>(() => Task.FromResult(environment));
        var reader = (ReaderView)Activator.CreateInstance(readerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { getEnvironment, null, null }, null);
        using (var host = new Form
        {
            Text = "Reader security smoke test",
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-2000, -2000),
            Size = new Size(760, 520)
        })
        {
            host.Controls.Add(reader);
            host.Show();
            var viewField = readerType.GetField("_view", BindingFlags.Instance | BindingFlags.NonPublic);
            var view = viewField == null ? null : viewField.GetValue(reader) as WebView2;
            if (view == null)
                throw new InvalidOperationException("Reader WebView2 field was not found.");
            var readyField = readerType.GetField("_webViewReady", BindingFlags.Instance | BindingFlags.NonPublic);
            await WaitUntilAsync(() => view.CoreWebView2 != null && readyField != null && (bool)readyField.GetValue(reader), 45000);
            var core = view.CoreWebView2;
            var outsideStatuses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            core.WebResourceRequested += delegate(object sender, CoreWebView2WebResourceRequestedEventArgs args)
            {
                Uri requestUri;
                if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out requestUri) || !requestUri.IsFile)
                    return;
                string path;
                try { path = Path.GetFullPath(requestUri.LocalPath); }
                catch { return; }
                if (String.Equals(path, outsidePng, StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(path, outsideSvg, StringComparison.OrdinalIgnoreCase))
                    outsideStatuses[path] = args.Response == null ? 0 : (int)args.Response.StatusCode;
            };

            await OpenReaderDocumentAsync(reader, core, htmlPath);
            AssertBlocked(outsideStatuses, outsidePng, "HTML 外部图片");
            AssertBlocked(outsideStatuses, outsideSvg, "HTML 外部 SVG");
            outsideStatuses.Clear();
            await OpenReaderDocumentAsync(reader, core, svgPath);
            AssertBlocked(outsideStatuses, outsidePng, "SVG 外部图片");
            Console.WriteLine("PASS: HTML and SVG file:// subresources outside the selected file directory receive HTTP 403.");
        }
    }

    private static async Task OpenReaderDocumentAsync(ReaderView reader, CoreWebView2 core, string path)
    {
        string target = new Uri(Path.GetFullPath(path)).AbsoluteUri;
        var completion = new TaskCompletionSource<bool>();
        EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = delegate(object sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            Uri current;
            if (Uri.TryCreate(core.Source, UriKind.Absolute, out current) &&
                String.Equals(current.AbsoluteUri, target, StringComparison.OrdinalIgnoreCase))
            {
                if (args.IsSuccess)
                    completion.TrySetResult(true);
                else
                    completion.TrySetException(new InvalidOperationException("Reader could not load test document " + path + ": " + args.WebErrorStatus));
            }
        };
        core.NavigationCompleted += handler;
        try
        {
            var method = typeof(ReaderView).GetMethod("OpenFileAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
                throw new InvalidOperationException("ReaderView.OpenFileAsync was not found.");
            await (Task)method.Invoke(reader, new object[] { path });
            var finished = await Task.WhenAny(completion.Task, Task.Delay(20000));
            if (finished != completion.Task)
                throw new TimeoutException("Timed out waiting for Reader to navigate to " + path);
            await completion.Task;
            await Task.Delay(900);
        }
        finally { core.NavigationCompleted -= handler; }
    }

    private static void AssertBlocked(Dictionary<string, int> statuses, string path, string description)
    {
        int status;
        if (!statuses.TryGetValue(path, out status))
            throw new InvalidOperationException("No WebResourceRequested event was observed for " + description + " outside the Reader root.");
        if (status != 403)
            throw new InvalidOperationException(description + " should be blocked with HTTP 403, received " + status + ".");
    }

    private static bool IsTargetPermission(CoreWebView2PermissionKind kind)
    {
        return kind == CoreWebView2PermissionKind.Camera || kind == CoreWebView2PermissionKind.Microphone ||
            kind == CoreWebView2PermissionKind.Geolocation || kind == CoreWebView2PermissionKind.Notifications;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMilliseconds)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
                return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Timed out waiting for WebView2 initialization.");
    }

    private static string ToFileUri(string path)
    {
        return new Uri(Path.GetFullPath(path)).AbsoluteUri;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { }
    }

    private sealed class ObservedPermission
    {
        public CoreWebView2PermissionState State { get; set; }
        public bool Handled { get; set; }
        public bool SavesInProfile { get; set; }
    }

    private sealed class LocalTestServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serveTask;
        private volatile bool _stopping;
        private readonly byte[] _responseBody = Encoding.UTF8.GetBytes("<!doctype html><html><head><title>permission-test</title></head><body>permission test</body></html>");

        public string Url { get; private set; }

        public LocalTestServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            Url = "http://127.0.0.1:" + endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";
            _serveTask = Task.Run((Func<Task>)ServeAsync);
        }

        private async Task ServeAsync()
        {
            while (!_stopping)
            {
                TcpClient client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        var request = new byte[2048];
                        try { await stream.ReadAsync(request, 0, request.Length); }
                        catch { }
                        string header = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
                            "Content-Length: " + _responseBody.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                            "\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
                        var headerBytes = Encoding.ASCII.GetBytes(header);
                        await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
                        await stream.WriteAsync(_responseBody, 0, _responseBody.Length);
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { if (_stopping) break; }
                catch (IOException) { }
                finally { if (client != null) client.Close(); }
            }
        }

        public void Dispose()
        {
            _stopping = true;
            try { _listener.Stop(); }
            catch { }
            try { _serveTask.Wait(1000); }
            catch { }
        }
    }
}
