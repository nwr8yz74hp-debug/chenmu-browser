using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using ChenmuBrowser;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class WindowsWebView2SmokeTests
{
    private static Exception _failure;
    private static bool _wallpaperMenuDisposedOnBrowserClose;
    private static bool _updateNoticeValidated;

    private delegate bool EnumChildWindowCallback(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildWindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    [STAThread]
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "chenmu-webview2-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            string browserData = Path.Combine(root, "browser-data");
            Directory.CreateDirectory(browserData);
            File.WriteAllText(Path.Combine(browserData, "update-notice.txt"), "v1.4.4\r\n", new UTF8Encoding(false));
            using (var server = new LocalTestServer())
            using (var browser = new BrowserForm(browserData, true, 1))
            {
                using (StartUpdateNoticeDialogValidation(browser))
                {
                    browser.Shown += async delegate
                    {
                        try
                        {
                            TestWallpaperMenuLifecycle(browser);
                            await TestDefaultDenyPermissionsAsync(browser, server);
                            await TestReaderSubresourceBoundaryAsync(root);
                            await TestBackgroundResumeAsync(browser);
                            Console.WriteLine("PASS: permission, Reader boundary and background-resume regressions.");
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
            }
            if (_failure != null)
                throw _failure;
            if (!_updateNoticeValidated)
                throw new InvalidOperationException("The v1.4.4 update-complete dialog was not visually validated.");
            if (!_wallpaperMenuDisposedOnBrowserClose)
                throw new InvalidOperationException("The wallpaper ContextMenuStrip was not disposed with BrowserForm.");
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

    private static System.Windows.Forms.Timer StartUpdateNoticeDialogValidation(BrowserForm browser)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += delegate
        {
            var dialog = FindWindow("#32770", "CHENMU Browser 更新完成");
            if (dialog != IntPtr.Zero)
            {
                timer.Stop();
                try
                {
                    var text = new List<string>();
                    EnumChildWindowCallback callback = delegate(IntPtr child, IntPtr ignored)
                    {
                        var buffer = new StringBuilder(4096);
                        if (GetWindowText(child, buffer, buffer.Capacity) > 0)
                            text.Add(buffer.ToString());
                        return true;
                    };
                    EnumChildWindows(dialog, callback, IntPtr.Zero);
                    var message = String.Join(Environment.NewLine, text);
                    if (!message.Contains("已更新至 v1.4.4") ||
                        !message.Contains("切回前台时多余的窗口标题栏重算") ||
                        message.Contains("更新完成弹窗误显示过往版本更新内容") ||
                        message.Contains("修复新标签页壁纸被白色遮罩洗淡") ||
                        message.Contains("ContextMenuStrip 已释放异常"))
                        throw new InvalidOperationException("The visible update dialog did not contain only the current v1.4.4 changelog: " + message);

                    _updateNoticeValidated = true;
                    Console.WriteLine("PASS: visible v1.4.4 update dialog contains only the current Chinese changelog.");
                }
                catch (Exception ex)
                {
                    _failure = ex;
                    Console.Error.WriteLine(ex);
                }
                finally
                {
                    SendMessage(dialog, 0x0111, new IntPtr(1), IntPtr.Zero);
                }
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                timer.Stop();
                _failure = new TimeoutException("The v1.4.4 update-complete dialog did not appear within 15 seconds.");
                try { browser.Close(); }
                catch { }
            }
        };
        timer.Start();
        return timer;
    }

    private static void TestWallpaperMenuLifecycle(BrowserForm browser)
    {
        var showMenu = typeof(BrowserForm).GetMethod("ShowHomeWallpaperMenu", BindingFlags.Instance | BindingFlags.NonPublic);
        var menuField = typeof(BrowserForm).GetField("_homeWallpaperMenu", BindingFlags.Instance | BindingFlags.NonPublic);
        if (showMenu == null || menuField == null)
            throw new InvalidOperationException("The wallpaper menu implementation was not found.");

        showMenu.Invoke(browser, null);
        var menu = menuField.GetValue(browser) as ContextMenuStrip;
        if (menu == null || menu.IsDisposed || menu.Items.Count != 2)
            throw new InvalidOperationException("The wallpaper menu was not created correctly.");

        browser.FormClosed += delegate { _wallpaperMenuDisposedOnBrowserClose = menu.IsDisposed; };
        menu.Close(ToolStripDropDownCloseReason.CloseCalled);
        Application.DoEvents();
        if (menu.IsDisposed)
            throw new InvalidOperationException("The wallpaper menu was disposed while handling its Closed event.");

        showMenu.Invoke(browser, null);
        if (!Object.ReferenceEquals(menu, menuField.GetValue(browser)))
            throw new InvalidOperationException("The wallpaper menu was not reused after closing.");
        menu.Items[1].PerformClick();
        Application.DoEvents();
        if (menu.IsDisposed)
            throw new InvalidOperationException("Selecting a wallpaper menu item disposed the menu before the event completed.");

        showMenu.Invoke(browser, null);
        menu.Close(ToolStripDropDownCloseReason.CloseCalled);
        Application.DoEvents();
        if (menu.IsDisposed)
            throw new InvalidOperationException("The wallpaper menu could not be reopened after a menu-item click.");

        Console.WriteLine("PASS: wallpaper ContextMenuStrip remains reusable after closing and is disposed with BrowserForm.");
    }

    private static async Task TestBackgroundResumeAsync(BrowserForm browser)
    {
        await EnsureBrowserWebViewAsync(browser);
        var webViewField = typeof(BrowserForm).GetField("_webView", BindingFlags.Instance | BindingFlags.NonPublic);
        var webView = webViewField == null ? null : webViewField.GetValue(browser) as WebView2;
        if (webView == null || webView.CoreWebView2 == null)
            throw new InvalidOperationException("The browser WebView2 control did not initialize for resume testing.");
        var core = webView.CoreWebView2;
        if (String.IsNullOrWhiteSpace(core.Source))
            throw new InvalidOperationException("The browser has no loaded page to preserve during resume testing.");

        string sourceBefore = core.Source;
        string sentinel = "chenmu-resume-" + Guid.NewGuid().ToString("N");
        await core.ExecuteScriptAsync("window.__chenmuResumeSentinel=" + JsonString(sentinel) + "; 'ready'");
        int navigationStarts = 0;
        EventHandler<CoreWebView2NavigationStartingEventArgs> navigationHandler = delegate { navigationStarts++; };
        core.NavigationStarting += navigationHandler;
        try
        {
            using (var probe = new NonClientCalcProbe(browser.Handle))
            {
                browser.Hide();
                await WaitUntilAsync(() => !browser.Visible, 2000);
                await Task.Delay(100);
                int nonClientCalculationsBefore = probe.NonClientCalculationCount;
                var activated = new TaskCompletionSource<bool>();
                EventHandler activatedHandler = delegate { activated.TrySetResult(true); };
                browser.Activated += activatedHandler;
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    browser.Show();
                    browser.Activate();
                    browser.BringToFront();
                    var activationResult = await Task.WhenAny(activated.Task, Task.Delay(5000));
                    if (activationResult != activated.Task)
                        throw new TimeoutException("The browser window did not reactivate within 5 seconds.");
                    await activated.Task;
                }
                finally
                {
                    browser.Activated -= activatedHandler;
                }
                stopwatch.Stop();

                var scriptTask = core.ExecuteScriptAsync("window.__chenmuResumeSentinel===" + JsonString(sentinel));
                var scriptResult = await Task.WhenAny(scriptTask, Task.Delay(5000));
                if (scriptResult != scriptTask)
                    throw new TimeoutException("The page did not respond to JavaScript within 5 seconds after resume.");
                if (await scriptTask != "true")
                    throw new InvalidOperationException("The page's in-memory DOM state was lost while returning to the foreground.");
                if (!String.Equals(core.Source, sourceBefore, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The page URL changed while returning to the foreground.");
                if (navigationStarts != 0)
                    throw new InvalidOperationException("Returning to the foreground unexpectedly started " + navigationStarts + " navigation(s).");
                if (probe.NonClientCalculationCount != nonClientCalculationsBefore)
                    throw new InvalidOperationException("The browser recalculated its non-client frame when merely reactivated.");
                if (stopwatch.ElapsedMilliseconds > 5000)
                    throw new TimeoutException("The browser took " + stopwatch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms to reactivate; limit is 5000 ms.");

                Console.WriteLine("PASS: hide/resume " + stopwatch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " ms; page responsive; URL/DOM preserved; navigation delta=0; WM_NCCALCSIZE delta=0.");
            }
        }
        finally
        {
            core.NavigationStarting -= navigationHandler;
            if (!browser.Visible)
                browser.Show();
        }
    }


    private static async Task TestDefaultDenyPermissionsAsync(BrowserForm browser, LocalTestServer server)
    {
        await EnsureBrowserWebViewAsync(browser);

        var webViewField = typeof(BrowserForm).GetField("_webView", BindingFlags.Instance | BindingFlags.NonPublic);
        var webView = webViewField == null ? null : webViewField.GetValue(browser) as WebView2;
        if (webView == null || webView.CoreWebView2 == null)
            throw new InvalidOperationException("The browser WebView2 control did not initialize.");
        var core = webView.CoreWebView2;
        await NavigateAndWaitAsync(browser, core, server.Url);
        string pageCapabilities = await core.ExecuteScriptAsync("JSON.stringify({origin:location.origin,secure:window.isSecureContext,media:!!(navigator.mediaDevices&&navigator.mediaDevices.getUserMedia),geolocation:!!navigator.geolocation,notifications:!!window.Notification})");
        Console.WriteLine("Permission test page: " + pageCapabilities);
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
        string devToolsParameters = "{\"expression\":" + JsonString(requestScript) + ",\"userGesture\":true,\"returnByValue\":true}";
        await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", devToolsParameters);
        DateTime deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            lock (seen)
                if (seen.Count >= 4)
                    break;
            await Task.Delay(200);
        }

        if (seen.Count < 4)
            Console.Error.WriteLine("Permission event diagnostics: " + pageCapabilities);

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
        ulong targetNavigationId = 0;
        string lastNavigation = "none";
        string lastCompletion = "none";
        EventHandler<CoreWebView2NavigationStartingEventArgs> startingHandler = delegate(object sender, CoreWebView2NavigationStartingEventArgs args)
        {
            lastNavigation = args.Uri + " (id " + args.NavigationId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
            if (String.Equals(args.Uri, target, StringComparison.OrdinalIgnoreCase))
                targetNavigationId = args.NavigationId;
        };
        EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = delegate(object sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            lastCompletion = "id " + args.NavigationId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ", success=" + args.IsSuccess.ToString() + ", error=" + args.WebErrorStatus.ToString();
            if (targetNavigationId != 0 && args.NavigationId == targetNavigationId)
            {
                if (args.IsSuccess)
                    completion.TrySetResult(true);
                else
                    completion.TrySetException(new InvalidOperationException("Reader could not load test document " + path + ": " + args.WebErrorStatus));
            }
        };
        core.NavigationStarting += startingHandler;
        core.NavigationCompleted += handler;
        try
        {
            var method = typeof(ReaderView).GetMethod("OpenFileAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
                throw new InvalidOperationException("ReaderView.OpenFileAsync was not found.");
            await (Task)method.Invoke(reader, new object[] { path });
            var finished = await Task.WhenAny(completion.Task, Task.Delay(20000));
            if (finished != completion.Task)
                throw new TimeoutException("Timed out waiting for Reader to navigate to " + path +
                    ". Current source: " + core.Source + "; last navigation: " + lastNavigation +
                    "; last completion: " + lastCompletion);
            await completion.Task;
            await Task.Delay(900);
        }
        finally
        {
            core.NavigationStarting -= startingHandler;
            core.NavigationCompleted -= handler;
        }
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

    private static async Task EnsureBrowserWebViewAsync(BrowserForm browser)
    {
        var activeTabField = typeof(BrowserForm).GetField("_activeTab", BindingFlags.Instance | BindingFlags.NonPublic);
        var ensure = typeof(BrowserForm).GetMethod("EnsureWebViewAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        if (activeTabField == null || ensure == null)
            throw new InvalidOperationException("BrowserForm WebView initialization methods were not found.");
        var activeTab = activeTabField.GetValue(browser);
        if (activeTab == null)
            throw new InvalidOperationException("BrowserForm has no active test tab.");
        await (Task)ensure.Invoke(browser, new[] { activeTab });
    }

    private static async Task NavigateAndWaitAsync(BrowserForm browser, CoreWebView2 core, string targetUrl)
    {
        Uri expected;
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out expected))
            throw new InvalidOperationException("The permission test server URL is invalid.");
        var completion = new TaskCompletionSource<bool>();
        ulong targetNavigationId = 0;
        string lastNavigation = "none";
        string lastCompletion = "none";
        Console.WriteLine("[PermissionNav] WebView2 runtime: " + core.Environment.BrowserVersionString);
        EventHandler<CoreWebView2NavigationStartingEventArgs> startingHandler = delegate(object sender, CoreWebView2NavigationStartingEventArgs args)
        {
            lastNavigation = args.Uri + " (id " + args.NavigationId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
            Console.WriteLine("[PermissionNav] NavigationStarting: " + lastNavigation + "; redirected=" + args.IsRedirected + "; canceled=" + args.Cancel);
            Uri requested;
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out requested) &&
                String.Equals(requested.AbsoluteUri, expected.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                targetNavigationId = args.NavigationId;
        };
        EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = delegate(object sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            lastCompletion = "id " + args.NavigationId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ", success=" + args.IsSuccess.ToString() + ", error=" + args.WebErrorStatus.ToString();
            Console.WriteLine("[PermissionNav] NavigationCompleted: " + lastCompletion + "; source=" + core.Source);
            if (targetNavigationId != 0 && args.NavigationId == targetNavigationId)
            {
                if (args.IsSuccess)
                    completion.TrySetResult(true);
                else
                    completion.TrySetException(new InvalidOperationException("Permission test page navigation failed: " + args.WebErrorStatus));
            }
        };
        EventHandler<CoreWebView2ServerCertificateErrorDetectedEventArgs> certificateErrorHandler = delegate(object sender, CoreWebView2ServerCertificateErrorDetectedEventArgs args)
        {
            Console.Error.WriteLine("[PermissionNav] Server certificate error: request=" + args.RequestUri + "; status=" + args.ErrorStatus + "; action=" + args.Action);
        };
        EventHandler<CoreWebView2SourceChangedEventArgs> sourceChangedHandler = delegate
        {
            Console.WriteLine("[PermissionNav] SourceChanged: " + core.Source);
        };
        core.NavigationStarting += startingHandler;
        core.NavigationCompleted += handler;
        core.ServerCertificateErrorDetected += certificateErrorHandler;
        core.SourceChanged += sourceChangedHandler;
        try
        {
            var navigate = typeof(BrowserForm).GetMethod("NavigateAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            if (navigate == null)
                throw new InvalidOperationException("BrowserForm.NavigateAsync was not found.");
            await (Task)navigate.Invoke(browser, new object[] { expected.AbsoluteUri });
            var finished = await Task.WhenAny(completion.Task, Task.Delay(20000));
            if (finished != completion.Task)
                throw new TimeoutException("Timed out waiting for the permission test page. Current source: " + core.Source +
                    "; last navigation: " + lastNavigation + "; last completion: " + lastCompletion);
            await completion.Task;
        }
        finally
        {
            core.NavigationStarting -= startingHandler;
            core.NavigationCompleted -= handler;
            core.ServerCertificateErrorDetected -= certificateErrorHandler;
            core.SourceChanged -= sourceChangedHandler;
        }
    }

    private static string ToFileUri(string path)
    {
        return new Uri(Path.GetFullPath(path)).AbsoluteUri;
    }

    private static string JsonString(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { }
    }

    private sealed class NonClientCalcProbe : NativeWindow, IDisposable
    {
        private const int WmNcCalcSize = 0x0083;

        public int NonClientCalculationCount { get; private set; }

        public NonClientCalcProbe(IntPtr handle)
        {
            AssignHandle(handle);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmNcCalcSize)
                NonClientCalculationCount++;
            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
                ReleaseHandle();
        }
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
        private readonly X509Certificate2 _certificate;
        private volatile bool _stopping;
        private readonly byte[] _responseBody = Encoding.UTF8.GetBytes("<!doctype html><html><head><title>permission-test</title></head><body>permission test</body></html>");

        public string Url { get; private set; }

        public LocalTestServer()
        {
            using (var privateKey = RSA.Create())
            {
                privateKey.KeySize = 2048;
                var certificateRequest = new CertificateRequest("CN=127.0.0.1", privateKey,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var subjectAlternativeName = new SubjectAlternativeNameBuilder();
                subjectAlternativeName.AddIpAddress(IPAddress.Loopback);
                certificateRequest.CertificateExtensions.Add(subjectAlternativeName.Build());
                certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
                certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
                var serverAuthentication = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
                certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(serverAuthentication, false));
                using (var generatedCertificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1)))
                {
                    byte[] pfx = generatedCertificate.Export(X509ContentType.Pfx);
                    try
                    {
                        _certificate = new X509Certificate2(pfx, (string)null,
                            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
                    }
                    finally { Array.Clear(pfx, 0, pfx.Length); }
                }
            }
            if (!_certificate.HasPrivateKey)
                throw new InvalidOperationException("The local HTTPS test certificate has no usable private key.");

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            Url = "https://127.0.0.1:" + endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";
            Console.WriteLine("[LocalTestServer] Listening at " + Url + "; certificate=" + _certificate.Subject +
                "; thumbprint=" + _certificate.Thumbprint + "; privateKey=" + _certificate.HasPrivateKey +
                "; valid=" + _certificate.NotBefore.ToUniversalTime().ToString("O") +
                ".." + _certificate.NotAfter.ToUniversalTime().ToString("O"));
            _serveTask = Task.Run((Func<Task>)ServeAsync);
        }

        private async Task ServeAsync()
        {
            Console.WriteLine("[LocalTestServer] Accept loop started.");
            while (!_stopping)
            {
                TcpClient client = null;
                string stage = "accept";
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                    Console.WriteLine("[LocalTestServer] Accepted " + client.Client.RemoteEndPoint + "; starting TLS handshake.");
                    using (client)
                    using (var stream = new SslStream(client.GetStream(), false))
                    {
                        stage = "TLS handshake";
                        await stream.AuthenticateAsServerAsync(_certificate, false, SslProtocols.Tls12, false);
                        Console.WriteLine("[LocalTestServer] TLS handshake completed: " + stream.SslProtocol + "; reading HTTP request.");
                        stage = "HTTP request read";
                        var request = new byte[2048];
                        int requestLength = await stream.ReadAsync(request, 0, request.Length);
                        string requestLine = requestLength == 0 ? "<connection closed without request>" :
                            Encoding.ASCII.GetString(request, 0, requestLength).Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                        Console.WriteLine("[LocalTestServer] Received " + requestLength.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                            " request bytes: " + requestLine);
                        string header = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
                            "Content-Length: " + _responseBody.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                            "\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
                        var headerBytes = Encoding.ASCII.GetBytes(header);
                        stage = "HTTP response write";
                        await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
                        await stream.WriteAsync(_responseBody, 0, _responseBody.Length);
                        Console.WriteLine("[LocalTestServer] Sent HTTP 200 response.");
                    }
                }
                catch (ObjectDisposedException ex)
                {
                    if (!_stopping) Console.Error.WriteLine("[LocalTestServer] " + stage + " failed: " + ex);
                    break;
                }
                catch (SocketException ex)
                {
                    if (!_stopping) Console.Error.WriteLine("[LocalTestServer] " + stage + " failed: " + ex);
                    if (_stopping) break;
                }
                catch (AuthenticationException ex) { Console.Error.WriteLine("[LocalTestServer] " + stage + " failed: " + ex); }
                catch (IOException ex) { Console.Error.WriteLine("[LocalTestServer] " + stage + " failed: " + ex); }
                catch (Exception ex) { Console.Error.WriteLine("[LocalTestServer] " + stage + " failed: " + ex); }
                finally { if (client != null) client.Close(); }
            }
            Console.WriteLine("[LocalTestServer] Accept loop stopped.");
        }

        public void Dispose()
        {
            _stopping = true;
            try { _listener.Stop(); }
            catch { }
            try { _serveTask.Wait(1000); }
            catch { }
            _certificate.Dispose();
        }
    }
}
