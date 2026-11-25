using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetDlgItem(IntPtr dialog, int identifier);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildWindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    [STAThread]
    private static int Main()
    {
        bool resumeOnly = String.Equals(Environment.GetEnvironmentVariable("CHENMU_WEBVIEW_SMOKE_RESUME_ONLY"), "1", StringComparison.Ordinal);
        string root = Path.Combine(Path.GetTempPath(), "chenmu-webview2-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            string browserData = Path.Combine(root, "browser-data");
            Directory.CreateDirectory(browserData);
            if (!resumeOnly)
                File.WriteAllText(Path.Combine(browserData, "update-notice.txt"), "v1.5.0\r\n", new UTF8Encoding(false));
            using (var server = new LocalTestServer())
            using (var browser = new BrowserForm(browserData, true, 1))
            {
                var updateNoticeValidation = resumeOnly ? null : StartUpdateNoticeDialogValidation(browser);
                using (updateNoticeValidation)
                {
                    browser.Shown += async delegate
                    {
                        try
                        {
                            if (resumeOnly)
                            {
                                Console.WriteLine("START: isolated background resume regression.");
                                await EnsureBrowserWebViewAsync(browser);
                                var resumeCore = GetCoreForTab(GetActiveTab(browser));
                                await NavigateCoreAndWaitAsync(browser, resumeCore, server.Url + "resume-only");
                                await TestBackgroundResumeAsync(browser);
                                await TestHomeViewResumeAsync(browser);
                                Console.WriteLine("PASS: isolated background resume and homepage repaint regressions.");
                            }
                            else
                            {
                                Console.WriteLine("START: wallpaper menu lifecycle.");
                                TestWallpaperMenuLifecycle(browser);
                                Console.WriteLine("START: navigation history.");
                                await TestV15NavigationHistoryAsync(browser);
                                Console.WriteLine("START: find, zoom and recently closed tabs.");
                                await TestV15FindZoomAndRecentlyClosedAsync(browser);
                                Console.WriteLine("START: local extensions.");
                                await TestV15ExtensionsAsync(browser, root);
                                Console.WriteLine("START: PDF/PNG save dialogs.");
                                await TestV15PageExportsAndCancelAsync(browser, root);
                                Console.WriteLine("START: default-deny site permissions.");
                                await TestDefaultDenyPermissionsAsync(browser, server);
                                Console.WriteLine("START: Reader file boundary.");
                                await TestReaderSubresourceBoundaryAsync(root);
                                Console.WriteLine("START: background resume regression.");
                                await TestBackgroundResumeAsync(browser);
                                await TestHomeViewResumeAsync(browser);
                                await CaptureRunningBrowserScreenshotAsync(browser);
                                Console.WriteLine("PASS: v1.5 navigation, find, zoom, extensions, recent tabs, exports, permission, Reader boundary, page resume and home repaint regressions.");
                            }
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
            if (!resumeOnly && !_updateNoticeValidated)
                throw new InvalidOperationException("The v1.5.0 update-complete dialog was not visually validated.");
            if (!resumeOnly && !_wallpaperMenuDisposedOnBrowserClose)
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
                    if (!message.Contains("已更新至 v1.5.0") ||
                        !message.Contains("修复前进/返回历史导航") ||
                        !message.Contains("将当前可见 WebView 网页截图为 PNG") ||
                        message.Contains("更新完成弹窗误显示过往版本更新内容") ||
                        message.Contains("切回前台时多余的窗口标题栏重算") ||
                        message.Contains("ContextMenuStrip 已释放异常"))
                        throw new InvalidOperationException("The visible update dialog did not contain only the current v1.5.0 changelog: " + message);

                    _updateNoticeValidated = true;
                    Console.WriteLine("PASS: visible v1.5.0 update dialog contains only the current Chinese changelog.");
                }
                catch (Exception ex)
                {
                    _failure = ex;
                    Console.Error.WriteLine(ex);
                }
                finally
                {
                    var okButton = GetDlgItem(dialog, 1);
                    if (okButton == IntPtr.Zero)
                        Console.Error.WriteLine("Update notice OK button was not found; dismissing the validated dialog directly.");
                    else
                    {
                        SendMessage(okButton, 0x00F5, IntPtr.Zero, IntPtr.Zero);
                        if (!IsWindowVisible(dialog))
                            Console.WriteLine("PASS: synchronously clicked the update notice OK button.");
                    }
                    if (IsWindowVisible(dialog))
                        SendMessage(dialog, 0x0111, new IntPtr(1), IntPtr.Zero);
                    if (IsWindowVisible(dialog))
                        SendMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    if (IsWindowVisible(dialog))
                        Console.Error.WriteLine("Update notice dialog remained open after BM_CLICK, IDOK, and WM_CLOSE.");
                }
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                timer.Stop();
                _failure = new TimeoutException("The v1.5.0 update-complete dialog did not appear within 15 seconds.");
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

    private static async Task TestV15NavigationHistoryAsync(BrowserForm browser)
    {
        await EnsureBrowserWebViewAsync(browser);
        using (var server = new LocalHttpTestServer())
        {
            var firstTab = GetActiveTab(browser);
            var firstCore = GetCoreForTab(firstTab);
            string a = server.Url + "a";
            string b = server.Url + "b";
            string c = server.Url + "c";
            await NavigateCoreAndWaitAsync(browser, firstCore, a);
            await NavigateCoreAndWaitAsync(browser, firstCore, b);
            await NavigateCoreAndWaitAsync(browser, firstCore, c);
            if (!String.Equals(firstCore.Source, new Uri(c).AbsoluteUri, StringComparison.OrdinalIgnoreCase) || !firstCore.CanGoBack)
                throw new InvalidOperationException("The A→B→C HTTP navigation sequence did not create a back history entry.");

            await NavigateHistoryAndWaitAsync(browser, firstCore, "GoBack", b);
            if (!firstCore.CanGoForward)
                throw new InvalidOperationException("Going back from C did not preserve the forward history entry.");
            await NavigateHistoryAndWaitAsync(browser, firstCore, "GoBack", a);
            await NavigateHistoryAndWaitAsync(browser, firstCore, "GoForward", b);
            await NavigateHistoryAndWaitAsync(browser, firstCore, "GoForward", c);
            await FocusPageInputAndSendKeysAsync(browser, firstCore, "%{LEFT}");
            await WaitUntilAsync(() => String.Equals(firstCore.Source, new Uri(b).AbsoluteUri, StringComparison.OrdinalIgnoreCase), 12000);
            await FocusPageInputAndSendKeysAsync(browser, firstCore, "%{RIGHT}");
            await WaitUntilAsync(() => String.Equals(firstCore.Source, new Uri(c).AbsoluteUri, StringComparison.OrdinalIgnoreCase), 12000);
            Console.WriteLine("PASS: HTTP A→B→C history buttons and Alt+Left/Right keyboard navigation preserve history.");

            var createTab = typeof(BrowserForm).GetMethod("CreateNewTab", BindingFlags.Instance | BindingFlags.NonPublic);
            if (createTab == null)
                throw new InvalidOperationException("BrowserForm.CreateNewTab was not found.");
            createTab.Invoke(browser, null);
            await EnsureBrowserWebViewAsync(browser);
            var secondTab = GetActiveTab(browser);
            var secondCore = GetCoreForTab(secondTab);
            string secondA = server.Url + "second-a";
            string secondB = server.Url + "second-b";
            await NavigateCoreAndWaitAsync(browser, secondCore, secondA);
            await NavigateCoreAndWaitAsync(browser, secondCore, secondB);

            SwitchToTab(browser, firstTab);
            await NavigateHistoryAndWaitAsync(browser, firstCore, "GoBack", b);
            if (!String.Equals(secondCore.Source, new Uri(secondB).AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Using back on the first tab changed the second tab's URL.");
            SwitchToTab(browser, secondTab);
            await NavigateHistoryAndWaitAsync(browser, secondCore, "GoBack", secondA);
            if (!String.Equals(firstCore.Source, new Uri(b).AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Using back on the second tab changed the first tab's URL.");
            Console.WriteLine("PASS: back navigation is isolated to the active tab.");
        }
    }

    private static async Task TestV15FindZoomAndRecentlyClosedAsync(BrowserForm browser)
    {
        using (var server = new LocalHttpTestServer())
        {
            await EnsureBrowserWebViewAsync(browser);
            var core = GetCoreForTab(GetActiveTab(browser));
            string findUrl = server.Url + "find-page";
            await NavigateCoreAndWaitAsync(browser, core, findUrl);

            var findTextField = typeof(BrowserForm).GetField("_findTextBox", BindingFlags.Instance | BindingFlags.NonPublic);
            if (findTextField == null)
                throw new InvalidOperationException("The v1.5 find bar was not found.");
            await FocusPageInputAndSendKeysAsync(browser, core, "^f");
            await WaitUntilAsync(() => findTextField.GetValue(browser) is TextBox, 5000);
            var findText = findTextField.GetValue(browser) as TextBox;
            if (findText == null || findText.FindForm() == null || !findText.FindForm().Visible)
                throw new InvalidOperationException("Ctrl+F did not create its find bar input.");
            findText.Text = "needle";
            await WaitUntilAsync(() => core.Find.MatchCount >= 2, 5000);
            if (core.Find.MatchCount < 2)
                throw new InvalidOperationException("The v1.5 find bar did not match repeated text in the page.");
            var closeFind = findText.FindForm();
            if (closeFind != null)
                closeFind.Close();
            Console.WriteLine("PASS: Ctrl+F custom find bar finds and highlights page text.");

            var setZoom = typeof(BrowserForm).GetMethod("SetActiveTabZoom", BindingFlags.Instance | BindingFlags.NonPublic);
            var createTab = typeof(BrowserForm).GetMethod("CreateNewTab", BindingFlags.Instance | BindingFlags.NonPublic);
            if (setZoom == null || createTab == null)
                throw new InvalidOperationException("The v1.5 zoom implementation was not found.");
            var firstTab = GetActiveTab(browser);
            setZoom.Invoke(browser, new object[] { 1.35 });
            if (Math.Abs(((WebView2)GetWebViewForTab(firstTab)).ZoomFactor - 1.35) > 0.001)
                throw new InvalidOperationException("The active tab did not apply its 135% zoom.");
            await FocusPageInputAndSendKeysAsync(browser, GetCoreForTab(firstTab), "^{ADD}");
            await WaitUntilAsync(() => Math.Abs(((WebView2)GetWebViewForTab(firstTab)).ZoomFactor - 1.45) < 0.001, 5000);
            if (Math.Abs(((WebView2)GetWebViewForTab(firstTab)).ZoomFactor - 1.45) > 0.001)
                throw new InvalidOperationException("Ctrl+Plus did not adjust zoom in the WebView-focused page.");
            await FocusPageInputAndSendKeysAsync(browser, GetCoreForTab(firstTab), "^{SUBTRACT}");
            await WaitUntilAsync(() => Math.Abs(((WebView2)GetWebViewForTab(firstTab)).ZoomFactor - 1.35) < 0.001, 5000);
            await FocusPageInputAndSendKeysAsync(browser, GetCoreForTab(firstTab), "^0");
            await WaitUntilAsync(() => Math.Abs(((WebView2)GetWebViewForTab(firstTab)).ZoomFactor - 1.0) < 0.001, 5000);
            setZoom.Invoke(browser, new object[] { 1.35 });
            createTab.Invoke(browser, null);
            await EnsureBrowserWebViewAsync(browser);
            var secondTab = GetActiveTab(browser);
            if (Math.Abs(((WebView2)GetWebViewForTab(secondTab)).ZoomFactor - 1.0) > 0.001)
                throw new InvalidOperationException("A new tab did not start at 100% zoom.");
            setZoom.Invoke(browser, new object[] { 0.8 });
            SwitchToTab(browser, firstTab);
            if (Math.Abs(((WebView2)GetWebViewForTab(firstTab)).ZoomFactor - 1.35) > 0.001 ||
                Math.Abs(((WebView2)GetWebViewForTab(secondTab)).ZoomFactor - 0.8) > 0.001)
                throw new InvalidOperationException("Zoom state was not isolated per tab.");
            Console.WriteLine("PASS: independent per-tab zoom persists across tab switching.");

            var recentField = typeof(BrowserForm).GetField("_recentlyClosedTabs", BindingFlags.Instance | BindingFlags.NonPublic);
            var closeTab = typeof(BrowserForm).GetMethod("CloseTab", BindingFlags.Instance | BindingFlags.NonPublic);
            if (recentField == null || closeTab == null)
                throw new InvalidOperationException("The recently closed tab stack was not found.");
            var recent = recentField.GetValue(browser);
            var countProperty = recent.GetType().GetProperty("Count");
            var before = (int)countProperty.GetValue(recent, null);

            createTab.Invoke(browser, null);
            await EnsureBrowserWebViewAsync(browser);
            var privateTab = GetActiveTab(browser);
            typeof(BrowserForm).GetNestedType("BrowserTab", BindingFlags.NonPublic)
                .GetProperty("IsPrivate").SetValue(privateTab, true, null);
            await NavigateCoreAndWaitAsync(browser, GetCoreForTab(privateTab), server.Url + "private-tab");
            closeTab.Invoke(browser, new[] { privateTab });
            if ((int)countProperty.GetValue(recent, null) != before)
                throw new InvalidOperationException("A private tab was added to recently closed history.");

            createTab.Invoke(browser, null);
            await EnsureBrowserWebViewAsync(browser);
            var readerMarkedTab = GetActiveTab(browser);
            await NavigateCoreAndWaitAsync(browser, GetCoreForTab(readerMarkedTab), server.Url + "reader-marked");
            typeof(BrowserForm).GetNestedType("BrowserTab", BindingFlags.NonPublic)
                .GetProperty("IsReader").SetValue(readerMarkedTab, true, null);
            closeTab.Invoke(browser, new[] { readerMarkedTab });
            if ((int)countProperty.GetValue(recent, null) != before)
                throw new InvalidOperationException("A Reader tab was added to recently closed history.");

            string closedUrl = server.Url + "ordinary-closed";
            createTab.Invoke(browser, null);
            await EnsureBrowserWebViewAsync(browser);
            var ordinaryTab = GetActiveTab(browser);
            await NavigateCoreAndWaitAsync(browser, GetCoreForTab(ordinaryTab), closedUrl);
            setZoom.Invoke(browser, new object[] { 1.3 });
            closeTab.Invoke(browser, new[] { ordinaryTab });
            var items = recent.GetType().GetMethod("GetItems").Invoke(recent, null) as System.Collections.IList;
            if (items == null || items.Count != before + 1)
                throw new InvalidOperationException("Closing an ordinary HTTP(S) tab did not add one recent item.");
            var snapshot = items[0];
            if (!String.Equals((string)snapshot.GetType().GetProperty("Url").GetValue(snapshot, null), new Uri(closedUrl).AbsoluteUri, StringComparison.OrdinalIgnoreCase) ||
                Math.Abs((double)snapshot.GetType().GetProperty("ZoomFactor").GetValue(snapshot, null) - 1.3) > 0.001)
                throw new InvalidOperationException("The recent-tab snapshot did not preserve URL and zoom.");
            await FocusPageInputAndSendKeysAsync(browser, GetCoreForTab(GetActiveTab(browser)), "^+t");
            await WaitUntilAsync(() =>
            {
                var active = GetActiveTab(browser);
                var webViewProperty = active.GetType().GetProperty("WebView", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var view = webViewProperty == null ? null : webViewProperty.GetValue(active, null) as WebView2;
                var restoredCore = view == null ? null : view.CoreWebView2;
                return restoredCore != null && String.Equals(restoredCore.Source, new Uri(closedUrl).AbsoluteUri, StringComparison.OrdinalIgnoreCase);
            }, 15000);
            var restoredTab = GetActiveTab(browser);
            if (Math.Abs(((WebView2)GetWebViewForTab(restoredTab)).ZoomFactor - 1.3) > 0.001)
                throw new InvalidOperationException("Restoring a recently closed tab did not restore its zoom.");
            Console.WriteLine("PASS: recent-tab restore retains only ordinary pages and preserves URL/zoom.");
        }
    }

    private static void FocusWebViewAndSendKeys(BrowserForm browser, string sequence)
    {
        var view = GetWebViewForTab(GetActiveTab(browser)) as WebView2;
        if (view == null || !browser.Visible)
            throw new InvalidOperationException("Cannot send a keyboard shortcut because the active WebView is not visible.");
        browser.Activate();
        browser.BringToFront();
        SetForegroundWindow(browser.Handle);
        Application.DoEvents();
        view.BringToFront();
        view.Select();
        view.Focus();
        Application.DoEvents();
        if (!view.ContainsFocus)
            throw new InvalidOperationException("The active WebView did not retain keyboard focus before sending " + sequence +
                "; visible=" + view.Visible + ", enabled=" + view.Enabled + ", canFocus=" + view.CanFocus +
                ", browserActive=" + browser.ContainsFocus + ".");
        SendKeys.SendWait(sequence);
        Application.DoEvents();
    }

    private static async Task FocusPageInputAndSendKeysAsync(BrowserForm browser, CoreWebView2 core, string sequence)
    {
        string focusedElement = await core.ExecuteScriptAsync("document.getElementById('editor').focus(); document.activeElement.id");
        if (!String.Equals(focusedElement, "\"editor\"", StringComparison.Ordinal))
            throw new InvalidOperationException("The test page input did not receive DOM focus before sending " + sequence + ": " + focusedElement);
        FocusWebViewAndSendKeys(browser, sequence);
    }

    private static async Task TestV15ExtensionsAsync(BrowserForm browser, string root)
    {
        await EnsureBrowserWebViewAsync(browser);
        var core = GetCoreForTab(GetActiveTab(browser));
        string extensionDirectory = Path.Combine(root, "smoke-extension");
        Directory.CreateDirectory(extensionDirectory);
        File.WriteAllText(Path.Combine(extensionDirectory, "manifest.json"),
            "{\"manifest_version\":3,\"name\":\"CHENMU smoke extension\",\"version\":\"1.0.0\",\"permissions\":[\"storage\"],\"host_permissions\":[\"http://127.0.0.1/*\"]}",
            new UTF8Encoding(false));
        var install = typeof(BrowserForm).GetMethod("InstallExtensionDirectoryAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        if (install == null)
            throw new InvalidOperationException("The v1.5 local extension installation method was not found.");
        var task = (Task<CoreWebView2BrowserExtension>)install.Invoke(browser, new object[] { core.Profile, extensionDirectory });
        var extension = await task;
        if (extension == null || !extension.IsEnabled || extension.Name != "CHENMU smoke extension")
            throw new InvalidOperationException("The selected unpacked extension was not installed and enabled.");
        await extension.EnableAsync(false);
        if (extension.IsEnabled)
            throw new InvalidOperationException("The extension manager could not disable an extension.");
        await extension.EnableAsync(true);
        if (!extension.IsEnabled)
            throw new InvalidOperationException("The extension manager could not re-enable an extension.");
        await extension.RemoveAsync();
        var remaining = await core.Profile.GetBrowserExtensionsAsync();
        if (remaining.Any(item => item.Id == extension.Id))
            throw new InvalidOperationException("The extension manager did not uninstall the selected extension.");
        Console.WriteLine("PASS: unpacked local extension installs, enables/disables, and uninstalls through WebView2 profile APIs.");
    }

    private static async Task TestV15PageExportsAndCancelAsync(BrowserForm browser, string root)
    {
        await EnsureBrowserWebViewAsync(browser);
        var core = GetCoreForTab(GetActiveTab(browser));
        string pdfPath = Path.Combine(root, "v15-export-smoke.pdf");
        try
        {
            bool savedPdf = await core.PrintToPdfAsync(pdfPath, null);
            if (!savedPdf || !File.Exists(pdfPath))
                throw new InvalidOperationException("WebView2 did not produce a PDF output file.");
            byte[] pdfHeader = File.ReadAllBytes(pdfPath).Take(5).ToArray();
            if (Encoding.ASCII.GetString(pdfHeader) != "%PDF-")
                throw new InvalidDataException("The saved page output did not have a PDF signature.");
        }
        finally { try { if (File.Exists(pdfPath)) File.Delete(pdfPath); } catch { } }

        byte[] pngBytes;
        using (var stream = new MemoryStream())
        {
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            pngBytes = stream.ToArray();
        }
        if (pngBytes.Length < 8 || pngBytes[0] != 0x89 || pngBytes[1] != 0x50 || pngBytes[2] != 0x4E || pngBytes[3] != 0x47)
            throw new InvalidDataException("WebView2 did not capture the visible page as a valid PNG.");

        string uiPdfPath = Path.Combine(root, "v15-ui-export-smoke.pdf");
        string uiPngPath = Path.Combine(root, "v15-ui-capture-smoke.png");
        await SaveDialogAndVerifyOutputAsync(browser, "将网页保存为 PDF", "SaveCurrentPageAsPdfAsync", uiPdfPath);
        if (!File.Exists(uiPdfPath) || Encoding.ASCII.GetString(File.ReadAllBytes(uiPdfPath).Take(5).ToArray()) != "%PDF-")
            throw new InvalidDataException("Confirming the standard PDF save dialog did not produce a valid PDF file.");
        await SaveDialogAndVerifyOutputAsync(browser, "截图当前可见网页", "CaptureCurrentPageAsPngAsync", uiPngPath);
        byte[] uiPngBytes = File.ReadAllBytes(uiPngPath);
        if (uiPngBytes.Length < 8 || uiPngBytes[0] != 0x89 || uiPngBytes[1] != 0x50 || uiPngBytes[2] != 0x4E || uiPngBytes[3] != 0x47)
            throw new InvalidDataException("Confirming the standard PNG save dialog did not produce a valid PNG file.");
        try { File.Delete(uiPdfPath); } catch { }
        try { File.Delete(uiPngPath); } catch { }

        await CancelSaveDialogAndVerifyNoSideEffectAsync(browser, "截图当前可见网页", "CaptureCurrentPageAsPngAsync");
        await CancelSaveDialogAndVerifyNoSideEffectAsync(browser, "将网页保存为 PDF", "SaveCurrentPageAsPdfAsync");
        Console.WriteLine("PASS: standard save dialogs produce visible-page PDF/PNG files; canceling either dialog leaves state unchanged.");
    }

    private static async Task SaveDialogAndVerifyOutputAsync(BrowserForm browser, string title, string methodName, string outputPath)
    {
        var method = typeof(BrowserForm).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        var statusField = typeof(BrowserForm).GetField("_statusLabel", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null || statusField == null)
            throw new InvalidOperationException("The save operation " + methodName + " was not found.");

        bool dialogSeen = false;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        using (var timer = new System.Windows.Forms.Timer { Interval = 100 })
        {
            timer.Tick += delegate
            {
                var handle = FindWindow("#32770", title);
                if (handle != IntPtr.Zero)
                {
                    dialogSeen = true;
                    timer.Stop();
                    SetForegroundWindow(handle);
                    SendKeys.SendWait("%n");
                    SendKeys.SendWait("^a");
                    SendKeys.SendWait(EscapeSendKeysLiteral(outputPath));
                    SendKeys.SendWait("{ENTER}");
                }
                else if (DateTime.UtcNow >= deadline)
                {
                    timer.Stop();
                    SendKeys.SendWait("{ESC}");
                }
            };
            timer.Start();
            var task = method.Invoke(browser, null) as Task;
            if (task == null)
                throw new InvalidOperationException(methodName + " did not return a Task.");
            await task;
            timer.Stop();
        }

        if (!dialogSeen)
            throw new TimeoutException("Could not locate the standard save dialog titled '" + title + "'.");
        var status = (Label)statusField.GetValue(browser);
        if (!File.Exists(outputPath) || !status.Text.Contains(Path.GetFileName(outputPath)))
            throw new InvalidOperationException("Confirming '" + title + "' did not save the requested file and update browser status.");
    }

    private static string EscapeSendKeysLiteral(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            switch (character)
            {
                case '+': result.Append("{+}"); break;
                case '^': result.Append("{^}"); break;
                case '%': result.Append("{%}"); break;
                case '~': result.Append("{~}"); break;
                case '(': result.Append("{(}"); break;
                case ')': result.Append("{)}"); break;
                case '{': result.Append("{{}"); break;
                case '}': result.Append("{}}"); break;
                default: result.Append(character); break;
            }
        }
        return result.ToString();
    }

    private static async Task CancelSaveDialogAndVerifyNoSideEffectAsync(BrowserForm browser, string title, string methodName)
    {
        var method = typeof(BrowserForm).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        var statusField = typeof(BrowserForm).GetField("_statusLabel", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null || statusField == null)
            throw new InvalidOperationException("The save operation " + methodName + " was not found.");
        var status = (Label)statusField.GetValue(browser);
        string before = status.Text;
        bool dialogSeen = false;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        using (var timer = new System.Windows.Forms.Timer { Interval = 100 })
        {
            timer.Tick += delegate
            {
                var handle = FindWindow("#32770", title);
                if (handle != IntPtr.Zero)
                {
                    dialogSeen = true;
                    timer.Stop();
                    SendMessage(handle, 0x0111, new IntPtr(2), IntPtr.Zero);
                }
                else if (DateTime.UtcNow >= deadline)
                {
                    timer.Stop();
                    SendKeys.SendWait("{ESC}");
                }
            };
            timer.Start();
            var task = method.Invoke(browser, null) as Task;
            if (task == null)
                throw new InvalidOperationException(methodName + " did not return a Task.");
            await task;
            timer.Stop();
        }
        if (!dialogSeen)
            throw new TimeoutException("Could not locate the standard save dialog titled '" + title + "'.");
        if (!String.Equals(status.Text, before, StringComparison.Ordinal))
            throw new InvalidOperationException("Canceling '" + title + "' changed browser status or state.");
    }

    private static async Task NavigateCoreAndWaitAsync(BrowserForm browser, CoreWebView2 core, string targetUrl)
    {
        Uri expected;
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out expected))
            throw new InvalidOperationException("The test navigation URL is invalid: " + targetUrl);
        var completion = new TaskCompletionSource<bool>();
        ulong navigationId = 0;
        EventHandler<CoreWebView2NavigationStartingEventArgs> starting = delegate(object sender, CoreWebView2NavigationStartingEventArgs args)
        {
            Uri current;
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out current) &&
                String.Equals(current.AbsoluteUri, expected.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                navigationId = args.NavigationId;
        };
        EventHandler<CoreWebView2NavigationCompletedEventArgs> completed = delegate(object sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (navigationId != 0 && args.NavigationId == navigationId)
            {
                if (args.IsSuccess)
                    completion.TrySetResult(true);
                else
                    completion.TrySetException(new InvalidOperationException("Navigation failed for " + targetUrl + ": " + args.WebErrorStatus));
            }
        };
        core.NavigationStarting += starting;
        core.NavigationCompleted += completed;
        try
        {
            if (!Object.ReferenceEquals(GetCoreForTab(GetActiveTab(browser)), core))
                throw new InvalidOperationException("The test navigation target is not the active browser tab.");
            var navigate = typeof(BrowserForm).GetMethod("NavigateAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            if (navigate == null)
                throw new InvalidOperationException("BrowserForm.NavigateAsync was not found.");
            await (Task)navigate.Invoke(browser, new object[] { expected.AbsoluteUri, false });
            var finished = await Task.WhenAny(completion.Task, Task.Delay(15000));
            if (finished != completion.Task)
                throw new TimeoutException("Timed out navigating to " + targetUrl + "; source=" + core.Source);
            await completion.Task;
            if (!String.Equals(core.Source, expected.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The navigation source did not retain the expected URL: " + core.Source);
        }
        finally
        {
            core.NavigationStarting -= starting;
            core.NavigationCompleted -= completed;
        }
    }

    private static async Task NavigateHistoryAndWaitAsync(BrowserForm browser, CoreWebView2 core, string methodName, string targetUrl)
    {
        string expected = new Uri(targetUrl).AbsoluteUri;
        var completion = new TaskCompletionSource<bool>();
        EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = delegate(object sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess && String.Equals(core.Source, expected, StringComparison.OrdinalIgnoreCase))
                completion.TrySetResult(true);
        };
        core.NavigationCompleted += handler;
        try
        {
            var method = typeof(BrowserForm).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
                throw new InvalidOperationException("BrowserForm." + methodName + " was not found.");
            method.Invoke(browser, null);
            var finished = await Task.WhenAny(completion.Task, Task.Delay(12000));
            if (finished != completion.Task)
                throw new TimeoutException(methodName + " did not reach " + expected + "; current source=" + core.Source);
            await completion.Task;
            if (!String.Equals(core.Source, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(methodName + " reached an unexpected URL: " + core.Source);
        }
        finally { core.NavigationCompleted -= handler; }
    }

    private static object GetActiveTab(BrowserForm browser)
    {
        var field = typeof(BrowserForm).GetField("_activeTab", BindingFlags.Instance | BindingFlags.NonPublic);
        var tab = field == null ? null : field.GetValue(browser);
        if (tab == null)
            throw new InvalidOperationException("BrowserForm has no active tab.");
        return tab;
    }

    private static object GetWebViewForTab(object tab)
    {
        var field = tab.GetType().GetProperty("WebView", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var view = field == null ? null : field.GetValue(tab, null);
        if (view == null)
            throw new InvalidOperationException("The requested browser tab has no WebView2 control.");
        return view;
    }

    private static CoreWebView2 GetCoreForTab(object tab)
    {
        var view = GetWebViewForTab(tab) as WebView2;
        if (view == null || view.CoreWebView2 == null)
            throw new InvalidOperationException("The requested browser tab's WebView2 core is not initialized.");
        return view.CoreWebView2;
    }

    private static void SwitchToTab(BrowserForm browser, object tab)
    {
        var method = typeof(BrowserForm).GetMethod("SwitchToTab", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null)
            throw new InvalidOperationException("BrowserForm.SwitchToTab was not found.");
        method.Invoke(browser, new[] { tab });
    }

    private static async Task CaptureRunningBrowserScreenshotAsync(BrowserForm browser)
    {
        var showHome = typeof(BrowserForm).GetMethod("ShowHomeView", BindingFlags.Instance | BindingFlags.NonPublic);
        if (showHome == null)
            throw new InvalidOperationException("The browser home view was not found for the running-app screenshot.");
        showHome.Invoke(browser, null);
        if (!browser.Visible)
            browser.Show();
        browser.Activate();
        await Task.Delay(500);
        Application.DoEvents();

        var screenBounds = browser.RectangleToScreen(browser.ClientRectangle);
        if (screenBounds.Width <= 0 || screenBounds.Height <= 0)
            throw new InvalidOperationException("The browser window has no visible client area for screenshot capture.");
        using (var bitmap = new Bitmap(screenBounds.Width, screenBounds.Height))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(screenBounds.Location, Point.Empty, screenBounds.Size);
            string workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
            if (String.IsNullOrWhiteSpace(workspace))
                workspace = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
            string outputDirectory = Path.Combine(workspace, "test-artifacts");
            Directory.CreateDirectory(outputDirectory);
            string outputPath = Path.Combine(outputDirectory, "CHENMU-Browser-v1.5.0-running-app.png");
            bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("RUNNING_APP_SCREENSHOT=" + outputPath);
        }
    }

    private static Color CaptureScreenPixel(Control control, Point clientPoint)
    {
        var screenPoint = control.PointToScreen(clientPoint);
        using (var bitmap = new Bitmap(1, 1))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(screenPoint, Point.Empty, new Size(1, 1));
            return bitmap.GetPixel(0, 0);
        }
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
                // Re-showing a hidden window may legitimately recalculate its client area.
                // The activation regression is an additional calculation after Show completes.
                int nonClientAfterShow = nonClientCalculationsBefore;
                int nonClientAfterActivate = nonClientCalculationsBefore;
                int nonClientAfterBringToFront = nonClientCalculationsBefore;
                int nonClientAfterActivated = nonClientCalculationsBefore;
                var activated = new TaskCompletionSource<bool>();
                // Activated can be dispatched inside Show before the managed Visible flag changes.
                bool resumeShowStarted = false;
                EventHandler activatedHandler = delegate
                {
                    Console.WriteLine("PAGE RESUME activated: showAttemptStarted=" + resumeShowStarted +
                        ", browserVisible=" + browser.Visible);
                    if (resumeShowStarted)
                        activated.TrySetResult(true);
                };
                browser.Activated += activatedHandler;
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    resumeShowStarted = true;
                    browser.Show();
                    if (!browser.Visible)
                        throw new InvalidOperationException("The browser window remained hidden after Show during resume testing.");
                    nonClientAfterShow = probe.NonClientCalculationCount;
                    browser.Activate();
                    nonClientAfterActivate = probe.NonClientCalculationCount;
                    browser.BringToFront();
                    nonClientAfterBringToFront = probe.NonClientCalculationCount;
                    var activationResult = await Task.WhenAny(activated.Task, Task.Delay(5000));
                    if (activationResult != activated.Task)
                        throw new TimeoutException("The browser window did not reactivate within 5 seconds.");
                    await activated.Task;
                    nonClientAfterActivated = probe.NonClientCalculationCount;
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
                await Task.Delay(100);
                int nonClientAfterSettle = probe.NonClientCalculationCount;
                Console.WriteLine("WM_NCCALCSIZE resume counts: before=" + nonClientCalculationsBefore.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ", Show=" + nonClientAfterShow.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ", Activate=" + nonClientAfterActivate.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ", BringToFront=" + nonClientAfterBringToFront.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ", Activated=" + nonClientAfterActivated.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ", settled=" + nonClientAfterSettle.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (nonClientAfterSettle != nonClientAfterShow)
                    throw new InvalidOperationException("The browser recalculated its non-client frame again after Show during activation; see WM_NCCALCSIZE phase counts above.");
                if (stopwatch.ElapsedMilliseconds > 5000)
                    throw new TimeoutException("The browser took " + stopwatch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms to reactivate; limit is 5000 ms.");

                Console.WriteLine("PASS: hide/resume " + stopwatch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " ms; page responsive; URL/DOM preserved; navigation delta=0; WM_NCCALCSIZE delta after Show=0.");
            }
        }
        finally
        {
            core.NavigationStarting -= navigationHandler;
            if (!browser.Visible)
                browser.Show();
        }
    }

    private static async Task TestHomeViewResumeAsync(BrowserForm browser)
    {
        var showHome = typeof(BrowserForm).GetMethod("ShowHomeView", BindingFlags.Instance | BindingFlags.NonPublic);
        var homeField = typeof(BrowserForm).GetField("_homeView", BindingFlags.Instance | BindingFlags.NonPublic);
        var greetingField = typeof(BrowserForm).GetField("_homeGreetingLabel", BindingFlags.Instance | BindingFlags.NonPublic);
        if (showHome == null || homeField == null || greetingField == null)
            throw new InvalidOperationException("The browser home view could not be located for resume testing.");

        showHome.Invoke(browser, null);
        var home = homeField.GetValue(browser) as Control;
        var greeting = greetingField.GetValue(browser) as Label;
        if (home == null || greeting == null || !home.Visible || String.IsNullOrWhiteSpace(greeting.Text))
            throw new InvalidOperationException("The browser home view was not ready for resume testing.");

        var originalWallpaper = home.BackgroundImage;
        var expected = Color.FromArgb(39, 112, 177);
        var activeTabField = typeof(BrowserForm).GetField("_activeTab", BindingFlags.Instance | BindingFlags.NonPublic);
        using (var wallpaper = new Bitmap(1, 1))
        {
            wallpaper.SetPixel(0, 0, expected);
            var activated = new TaskCompletionSource<bool>();
            int paintCount = 0;
            // Activated can be dispatched inside Show before the managed Visible flag changes.
            bool resumeShowStarted = false;
            bool activatedHandlerAttached = false;
            EventHandler activatedHandler = delegate
            {
                var activeTab = activeTabField == null ? null : activeTabField.GetValue(browser);
                var isHomeProperty = activeTab == null ? null : activeTab.GetType().GetProperty("IsHome");
                var isReaderProperty = activeTab == null ? null : activeTab.GetType().GetProperty("IsReader");
                bool browserVisible = browser.Visible;
                bool homeVisible = home.Visible;
                bool activeIsHome = isHomeProperty != null && (bool)isHomeProperty.GetValue(activeTab, null);
                bool activeIsReader = isReaderProperty != null && (bool)isReaderProperty.GetValue(activeTab, null);
                Console.WriteLine("HOME RESUME activated: showAttemptStarted=" + resumeShowStarted +
                    ", browserVisible=" + browserVisible +
                    ", homeVisible=" + homeVisible + ", activeIsHome=" + activeIsHome +
                    ", activeIsReader=" + activeIsReader +
                    ", homeHandle=" + home.IsHandleCreated + ", paintCount=" + paintCount);
                if (!resumeShowStarted || !activeIsHome || activeIsReader)
                {
                    Console.WriteLine("HOME RESUME ignored activation outside the homepage show attempt.");
                    return;
                }
                activated.TrySetResult(true);
            };
            PaintEventHandler paintHandler = delegate { paintCount++; };
            home.Paint += paintHandler;
            try
            {
                home.BackgroundImage = wallpaper;
                home.Refresh();
                var samplePoints = new[]
                {
                    new Point(5, 5),
                    new Point(Math.Max(0, home.Width - 10), 5),
                    new Point(5, Math.Max(0, home.Height - 10)),
                    new Point(Math.Max(0, home.Width - 10), Math.Max(0, home.Height - 10))
                };
                var screenPixelsBeforeResume = new Color[samplePoints.Length];
                for (var index = 0; index < samplePoints.Length; index++)
                {
                    screenPixelsBeforeResume[index] = CaptureScreenPixel(home, samplePoints[index]);
                    Console.WriteLine("HOME RESUME baseline screen pixel[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) +"] at " +
                        samplePoints[index] + "=" + screenPixelsBeforeResume[index]);
                }
                int paintsBeforeResume = paintCount;
                Console.WriteLine("HOME RESUME baseline: browserVisible=" + browser.Visible +
                    ", homeVisible=" + home.Visible + ", paintCount=" + paintsBeforeResume);
                browser.Hide();
                await WaitUntilAsync(() => !browser.Visible, 2000);
                await Task.Delay(100);
                browser.Activated += activatedHandler;
                activatedHandlerAttached = true;
                resumeShowStarted = true;
                browser.Show();
                int paintsAfterShow = paintCount;
                Console.WriteLine("HOME RESUME after Show: browserVisible=" + browser.Visible +
                    ", homeVisible=" + home.Visible + ", paintCount=" + paintCount);
                browser.Activate();
                Console.WriteLine("HOME RESUME after Activate: paintCount=" + paintCount);
                browser.BringToFront();
                Console.WriteLine("HOME RESUME after BringToFront: paintCount=" + paintCount);
                if (await Task.WhenAny(activated.Task, Task.Delay(5000)) != activated.Task)
                    throw new TimeoutException("The home view did not reactivate within 5 seconds.");
                await activated.Task;
                await Task.Delay(100);

                if (!browser.Visible || !home.Visible || String.IsNullOrWhiteSpace(greeting.Text))
                    throw new InvalidOperationException("The home view or greeting disappeared after foreground resume.");
                if (paintCount <= paintsAfterShow)
                    throw new InvalidOperationException("The homepage did not repaint again after the window's Show/activation sequence.");
                using (var frame = new Bitmap(home.Width, home.Height))
                {
                    home.DrawToBitmap(frame, new Rectangle(Point.Empty, home.Size));
                    for (var index = 0; index < samplePoints.Length; index++)
                    {
                        var screenAfterResume = CaptureScreenPixel(home, samplePoints[index]);
                        Console.WriteLine("HOME RESUME pixel[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) +"] at " +
                            samplePoints[index] + ": before=" + screenPixelsBeforeResume[index] +
                            ", screenAfter=" + screenAfterResume + ", DrawToBitmap=" + frame.GetPixel(samplePoints[index].X, samplePoints[index].Y));
                    }
                    var sample = samplePoints[3];
                    var actual = frame.GetPixel(sample.X, sample.Y);
                    try
                    {
                        var screenPoint = home.PointToScreen(sample);
                        using (var screenFrame = new Bitmap(1, 1))
                        using (var graphics = Graphics.FromImage(screenFrame))
                        {
                            graphics.CopyFromScreen(screenPoint, Point.Empty, new Size(1, 1));
                            Console.WriteLine("HOME RESUME pixel comparison: DrawToBitmap=" + actual +
                                ", screen=" + screenFrame.GetPixel(0, 0) + ", point=" + screenPoint);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("HOME RESUME screen pixel unavailable: " + ex.GetType().Name + ": " + ex.Message);
                    }
                    const int tolerance = 40;
                    if (Math.Abs(actual.R - expected.R) > tolerance ||
                        Math.Abs(actual.G - expected.G) > tolerance ||
                        Math.Abs(actual.B - expected.B) > tolerance)
                        throw new InvalidOperationException("The homepage wallpaper was not repainted after resume; expected " +
                            expected + ", got " + actual + ".");
                }

                Console.WriteLine("PASS: home view and wallpaper repainted after hide/resume.");
            }
            finally
            {
                if (activatedHandlerAttached)
                    browser.Activated -= activatedHandler;
                home.Paint -= paintHandler;
                home.BackgroundImage = originalWallpaper;
                home.Refresh();
                if (!browser.Visible)
                    browser.Show();
            }
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
            await (Task)navigate.Invoke(browser, new object[] { expected.AbsoluteUri, false });
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

    private sealed class LocalHttpTestServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serveTask;
        private volatile bool _stopping;

        public string Url { get; private set; }

        public LocalHttpTestServer()
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
                        var request = new byte[4096];
                        await stream.ReadAsync(request, 0, request.Length);
                        byte[] body = Encoding.UTF8.GetBytes(
                            "<!doctype html><html><head><meta charset=\"utf-8\"><title>history-smoke</title></head>" +
                            "<body><h1>History smoke page</h1><input id=\"editor\"><p>needle appears here</p><p>needle appears again</p></body></html>");
                        byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
                            "Content-Length: " + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                            "\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
                        await stream.WriteAsync(header, 0, header.Length);
                        await stream.WriteAsync(body, 0, body.Length);
                    }
                }
                catch (ObjectDisposedException) { if (_stopping) break; }
                catch (SocketException) { if (_stopping) break; }
                catch (IOException) { }
                catch (Exception ex) { if (!_stopping) Console.Error.WriteLine("[LocalHttpTestServer] " + ex); }
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
