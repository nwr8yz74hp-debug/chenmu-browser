using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ChenmuBrowser
{
    internal sealed partial class BrowserForm : Form, IMessageFilter
    {
        private const int EmSetCueBanner = 0x1501;
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaBorderColor = 34;
        private const int DwmwaCaptionColor = 35;
        private const int DwmwaTextColor = 36;
        private const int MaximumSettingsFileBytes = 64 * 1024;
        private const int MaximumSessionFileBytes = 256 * 1024;
        private const int MaximumBookmarkStoreBytes = 4 * 1024 * 1024;
        private const int MaximumStoredBookmarks = 20000;
        private const int MaximumAllowedHosts = 200;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpFrameChanged = 0x0020;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RtlOsVersionInfo
        {
            public uint Size;
            public uint MajorVersion;
            public uint MinorVersion;
            public uint BuildNumber;
            public uint PlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string ServicePack;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, string lParam);
        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint flags);
        [DllImport("ntdll.dll", EntryPoint = "RtlGetVersion", ExactSpelling = true)]
        private static extern int RtlGetVersion(ref RtlOsVersionInfo versionInfo);

        private readonly Panel _header = new Panel();
        private readonly Panel _tabSidebarHost = new Panel();
        private readonly Panel _tabSidebarEdge = new Panel();
        private readonly Panel _tabSidebarContent = new Panel();
        private readonly Panel _tabViewport = new Panel();
        private readonly FlowLayoutPanel _tabHeaders = new FlowLayoutPanel();
        private readonly VScrollBar _tabScrollBar = new VScrollBar();
        private readonly TableLayoutPanel _rootLayout = new TableLayoutPanel();
        private readonly Panel _browserSurface = new Panel();
        private readonly TextBox _addressBox = new TextBox();
        private readonly Panel _viewport = new Panel();
        private readonly Panel _statusPanel = new Panel();
        private readonly Label _statusLabel = new Label();
        private readonly ToolTip _toolTip = new ToolTip();
        private readonly string _dataRoot;
        private readonly HashSet<string> _blockedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private Panel _homeView;
        private GradientPanel _homeGradientPanel;
        private Panel _errorView;
        private TextBox _homeSearchBox;
        private Button _homeSearchEngineButton;
        private WebView2 _webView;
        private readonly List<BrowserTab> _tabs = new List<BrowserTab>();
        private BrowserTab _activeTab;
        private bool _updatingTabSidebar;
        private bool _tabSidebarExpanded;
        private readonly bool _isIsolatedTest;
        private Task<CoreWebView2Environment> _webViewEnvironmentTask;
        private DebouncedWorkQueue<string[]> _sessionSnapshotQueue;
        private IntPtr _captionColorsAppliedHandle;
        private readonly List<DownloadEntry> _downloads = new List<DownloadEntry>();
        private bool _blockTrackers = true;
        private bool _restorePreviousSession;
        private bool _isRestoringSession;
        private readonly List<string> _startupSessionUrls = new List<string>();
        private int _startupSessionActiveIndex;
        private bool _dataSaver;
        private string _searchEngine = "google";
        private DateTime _lastUpdateCheckUtc = DateTime.MinValue;
        private bool _updateCheckInProgress;
        private bool _homeViewRefreshPending;

        private string SettingsPath { get { return Path.Combine(_dataRoot, "settings.ini"); } }
        private string BookmarksPath { get { return Path.Combine(_dataRoot, "bookmarks.tsv"); } }
        private string SessionPath { get { return Path.Combine(_dataRoot, "session.tsv"); } }

        public BrowserForm(string dataRoot = null, bool isIsolatedTest = false, int initialTestTabs = 1)
        {
            _dataRoot = String.IsNullOrWhiteSpace(dataRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CHENMU Browser")
                : dataRoot;
            _sessionSnapshotQueue = new DebouncedWorkQueue<string[]>(WriteSessionSnapshotFile,
                exception => ReportPersistenceError("浏览会话无法保存：", exception), 350);
            _isIsolatedTest = isIsolatedTest;
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = _isIsolatedTest ? "CHENMU Browser（隔离测试）" : "CHENMU Browser";
            try
            {
                var executableIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (executableIcon != null)
                    Icon = executableIcon;
            }
            catch { }
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(640, 500);
            Size = new Size(1180, 780);
            WindowState = FormWindowState.Maximized;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            LoadPreferences();
            LoadBlocklist();
            LoadSessionSnapshot();
            HandleCreated += delegate { ApplyWindowCaptionColors(); };
            HandleDestroyed += delegate { _captionColorsAppliedHandle = IntPtr.Zero; };
            Activated += delegate
            {
                ApplyWindowCaptionColors();
                RefreshVisibleHomeView();
            };
            BuildInterface();
            InitializeV11Features();
            InitializeV15Features();
            Application.AddMessageFilter(this);
#if DEBUG
            if (_isIsolatedTest)
                InitializeVisualTestTabs(initialTestTabs);
#endif
            Shown += async delegate
            {
                ApplyWindowCaptionColors();
                UpdateTabSidebar();
                EnsureTabVisible(_activeTab);
                if (!_isIsolatedTest)
                    await RestoreSessionAsync();
                ShowPendingUpdateNotice();
#if DEBUG
                UpdateVisualTestStatus();
#endif
                if (!_isIsolatedTest)
                    await CheckForUpdatesAsync(false);
            };
            FormClosed += delegate
            {
                Application.RemoveMessageFilter(this);
                _homeGreetingTimer.Stop();
                _homeGreetingTimer.Dispose();
                foreach (var core in _pageAnimationScriptIds.Keys.ToList())
                    RemovePageAnimationScript(core);
                foreach (var core in _pageAnimationRegistrationVersions.Keys.ToList())
                    RemovePageAnimationScript(core);
                if (_sessionSnapshotQueue != null)
                    _sessionSnapshotQueue.Dispose();
                if (_historyWriteQueue != null)
                    _historyWriteQueue.Dispose();
            };
            FormClosing += delegate
            {
                SaveSessionSnapshot();
                if (_sessionSnapshotQueue != null)
                    _sessionSnapshotQueue.Flush();
                if (_historyWriteQueue != null)
                    _historyWriteQueue.Flush();
            };
        }

#if DEBUG
        private void InitializeVisualTestTabs(int requestedCount)
        {
            var count = Math.Max(1, Math.Min(60, requestedCount));
            while (_tabs.Count < count)
                CreateNewTab();
            for (var index = 0; index < _tabs.Count; index++)
                _tabs[index].Title = "布局验证标签 " + (index + 1).ToString("00", CultureInfo.InvariantCulture) +
                    " — 较长页面标题用于省略显示测试";
            UpdateTabHeaders();
            _statusLabel.Text = "隔离测试 profile · " + _tabs.Count.ToString(CultureInfo.InvariantCulture) + " 个标签";
            Text = "CHENMU Browser（隔离测试 · " + _tabs.Count.ToString(CultureInfo.InvariantCulture) + " 标签）";
        }

        private void UpdateVisualTestStatus()
        {
            if (!_isIsolatedTest || _statusLabel.IsDisposed)
                return;
            var screen = Screen.FromControl(this);
            _statusLabel.Text = "隔离测试 · " + DeviceDpi.ToString(CultureInfo.InvariantCulture) + " DPI · " +
                screen.Bounds.Width.ToString(CultureInfo.InvariantCulture) + "×" +
                screen.Bounds.Height.ToString(CultureInfo.InvariantCulture) + " · " +
                _tabs.Count.ToString(CultureInfo.InvariantCulture) + " 个标签 · 侧栏" +
                (_tabSidebarExpanded ? "展开" : "收起");
        }
#endif

        private void ApplyWindowCaptionColors()
        {
            if (!IsHandleCreated)
                return;

            var handle = Handle;
            if (_captionColorsAppliedHandle == handle)
                return;
            try
            {
                var lightMode = 0;
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref lightMode, sizeof(int));
                if (IsWindows11OrGreater())
                {
                    var caption = ToColorRef(Color.FromArgb(248, 250, 253));
                    var text = ToColorRef(Color.FromArgb(35, 55, 75));
                    DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
                    DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(int));
                    DwmSetWindowAttribute(handle, DwmwaBorderColor, ref caption, sizeof(int));
                }
                SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                    SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
                _captionColorsAppliedHandle = handle;
            }
            catch { _captionColorsAppliedHandle = IntPtr.Zero; }
        }

        private void RefreshVisibleHomeView()
        {
            if (IsDisposed || Disposing || _homeView == null || _homeView.IsDisposed ||
                _activeTab == null || !_activeTab.IsHome || _activeTab.IsReader)
                return;

            if (!_homeView.Visible)
            {
                // Activated can run inside Show before WinForms updates visible state.
                if (!IsHandleCreated || _homeViewRefreshPending)
                    return;
                _homeViewRefreshPending = true;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _homeViewRefreshPending = false;
                        if (IsDisposed || Disposing || !Visible || _homeView == null || _homeView.IsDisposed ||
                            !_homeView.Visible || _activeTab == null || !_activeTab.IsHome || _activeTab.IsReader)
                            return;
                        _homeView.Refresh();
                    });
                }
                catch (InvalidOperationException)
                {
                    _homeViewRefreshPending = false;
                }
                return;
            }

            _homeView.Refresh();
        }

        private static bool IsWindows11OrGreater()
        {
            try
            {
                var version = new RtlOsVersionInfo
                {
                    Size = (uint)Marshal.SizeOf(typeof(RtlOsVersionInfo)),
                    ServicePack = String.Empty
                };
                if (RtlGetVersion(ref version) == 0)
                    return version.BuildNumber >= 22000;
            }
            catch { }

            return Environment.OSVersion.Version.Build >= 22000;
        }

        private static int ToColorRef(Color color)
        {
            return color.R | (color.G << 8) | (color.B << 16);
        }

        private void ReportPersistenceError(string prefix, Exception exception)
        {
            if (IsDisposed || Disposing || !IsHandleCreated)
                return;
            Action update = delegate
            {
                if (!_statusLabel.IsDisposed && !_statusLabel.Disposing)
                    _statusLabel.Text = prefix + exception.Message;
            };
            try
            {
                if (InvokeRequired)
                    BeginInvoke((MethodInvoker)delegate { update(); });
                else
                    update();
            }
            catch { }
        }

        private static Image LoadBrandMark()
        {
            var imagePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "chenmu-logo.png");
            if (!File.Exists(imagePath))
                return null;

            try
            {
                using (var stream = new MemoryStream(File.ReadAllBytes(imagePath)))
                using (var source = Image.FromStream(stream))
                    return (Image)source.Clone();
            }
            catch
            {
                return null;
            }
        }

        private static void SetCueBanner(TextBox textBox, string text)
        {
            Action setCue = delegate
            {
                if (!textBox.IsDisposed && textBox.IsHandleCreated)
                    SendMessage(textBox.Handle, EmSetCueBanner, new IntPtr(1), text);
            };
            if (textBox.IsHandleCreated)
                setCue();
            else
                textBox.HandleCreated += delegate { setCue(); };
        }

        private static Panel CreateCenteredTextBoxHost(TextBox textBox, Padding inset)
        {
            textBox.AutoSize = false;
            textBox.Dock = DockStyle.None;
            textBox.Margin = Padding.Empty;
            var host = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = inset,
                BackColor = Color.White
            };
            Action alignTextBox = delegate
            {
                if (host.IsDisposed || textBox.IsDisposed)
                    return;
                var innerHeight = Math.Max(0, host.ClientSize.Height - host.Padding.Vertical);
                var textHeight = Math.Max(1, Math.Min(Math.Max(1, innerHeight), Math.Max(1, textBox.PreferredHeight)));
                textBox.SetBounds(host.Padding.Left,
                    host.Padding.Top + Math.Max(0, (innerHeight - textHeight) / 2),
                    Math.Max(0, host.ClientSize.Width - host.Padding.Horizontal), textHeight);
            };
            host.Controls.Add(textBox);
            host.SizeChanged += delegate { alignTextBox(); };
            textBox.FontChanged += delegate { alignTextBox(); };
            host.HandleCreated += delegate
            {
                if (host.IsHandleCreated)
                    host.BeginInvoke((MethodInvoker)delegate { alignTextBox(); });
            };
            return host;
        }

        private void BuildInterface()
        {
            _header.Dock = DockStyle.Top;
            _header.Height = 60;
            _header.BackColor = Color.FromArgb(249, 251, 255);
            _header.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(Color.FromArgb(226, 233, 241)))
                    e.Graphics.DrawLine(pen, 0, _header.Height - 1, _header.Width, _header.Height - 1);
            };

            var chrome = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 9,
                RowCount = 1,
                Padding = new Padding(8, 7, 8, 7),
                BackColor = _header.BackColor
            };
            for (var i = 0; i < 4; i++)
                chrome.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42F));
            chrome.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            for (var i = 0; i < 4; i++)
                chrome.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42F));
            chrome.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            _backButton = CreateChromeButton("←", "后退（Alt+←）", delegate
            {
                GoBack();
            });
            _backButton.Enabled = false;
            chrome.Controls.Add(_backButton, 0, 0);
            _forwardButton = CreateChromeButton("→", "前进（Alt+→）", delegate
            {
                GoForward();
            });
            _forwardButton.Enabled = false;
            chrome.Controls.Add(_forwardButton, 1, 0);
            chrome.Controls.Add(CreateChromeButton("↻", "刷新页面（Ctrl+R）", delegate
            {
                ReloadCurrentPage();
            }), 2, 0);
            chrome.Controls.Add(CreateChromeButton("⌂", "回到起始页", ShowHomeView), 3, 0);

            var addressFrame = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(5, 1, 5, 1),
                Padding = new Padding(10, 8, 10, 8),
                SurfaceColor = Color.White,
                BorderColor = Color.FromArgb(220, 227, 235),
                CornerRadius = 18
            };
            var addressLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Color.White,
                Padding = new Padding(2, 0, 2, 0)
            };
            addressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40F));
            addressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            addressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34F));
            addressLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            var addressGlyph = new Label
            {
                Text = "\uE721",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe MDL2 Assets", 12F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(105, 120, 137),
                Margin = Padding.Empty,
                AccessibleName = "搜索图标"
            };
            _addressBox.BorderStyle = BorderStyle.None;
            _addressBox.BackColor = Color.White;
            _addressBox.MaxLength = 4096;
            _addressBox.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
            _addressBox.KeyDown += AddressBox_KeyDown;
            SetCueBanner(_addressBox, "输入网址或搜索关键词");
            _toolTip.SetToolTip(_addressBox, "输入网址或搜索关键词；按 Ctrl+L 可快速定位到这里");
            var addressBoxHost = CreateCenteredTextBoxHost(_addressBox, new Padding(8, 0, 8, 0));
            var addressClearButton = new Button
            {
                Text = "×",
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(132, 145, 159),
                Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Regular, GraphicsUnit.Point),
                Margin = new Padding(0),
                TabStop = false,
                Cursor = Cursors.Hand,
                AccessibleName = "清空地址栏",
                Visible = false
            };
            addressClearButton.FlatAppearance.BorderSize = 0;
            addressClearButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(239, 244, 249);
            addressClearButton.Click += delegate { _addressBox.Clear(); _addressBox.Focus(); };
            _addressBox.TextChanged += delegate { addressClearButton.Visible = _addressBox.TextLength > 0; };
            _addressBox.Enter += delegate { addressFrame.BorderColor = Color.FromArgb(76, 139, 214); };
            _addressBox.Leave += delegate { addressFrame.BorderColor = Color.FromArgb(220, 227, 235); };
            _toolTip.SetToolTip(addressClearButton, "清空地址栏");
            addressLayout.Controls.Add(addressGlyph, 0, 0);
            addressLayout.Controls.Add(addressBoxHost, 1, 0);
            addressLayout.Controls.Add(addressClearButton, 2, 0);
            addressFrame.Controls.Add(addressLayout);
            chrome.Controls.Add(addressFrame, 4, 0);

            chrome.Controls.Add(CreateChromeButton("☆", "收藏当前页面（Ctrl+D）", AddCurrentBookmark), 5, 0);
            chrome.Controls.Add(CreateChromeButton("▤", "查看书签", ShowBookmarksDialog), 6, 0);

            var settingsMenu = new ContextMenuStrip();
            _settingsMenu = settingsMenu;
            var searchMenu = new ToolStripMenuItem("默认搜索引擎");
            searchMenu.DropDownItems.Add("Google", null, delegate { SetSearchEngine("google"); });
            searchMenu.DropDownItems.Add("Bing", null, delegate { SetSearchEngine("bing"); });
            searchMenu.DropDownItems.Add("Soumu（DuckDuckGo）", null, delegate { SetSearchEngine("soumu"); });
            settingsMenu.Items.Add(searchMenu);
            settingsMenu.Items.Add(new ToolStripSeparator());
            var blockItem = new ToolStripMenuItem("拦截广告与跟踪器")
            {
                CheckOnClick = true,
                Checked = _blockTrackers,
                ToolTipText = "使用随程序附带的本地域名列表"
            };
            blockItem.CheckedChanged += delegate
            {
                _blockTrackers = blockItem.Checked;
                SavePreferences();
                _statusLabel.Text = _blockTrackers ? "广告/跟踪域名拦截已开启" : "广告/跟踪域名拦截已关闭";
            };
            settingsMenu.Items.Add(blockItem);

            var saverItem = new ToolStripMenuItem("省流模式（不加载图片）")
            {
                CheckOnClick = true,
                Checked = _dataSaver
            };
            saverItem.CheckedChanged += delegate
            {
                _dataSaver = saverItem.Checked;
                SavePreferences();
                _statusLabel.Text = _dataSaver ? "省流模式已开启；刷新页面后生效" : "省流模式已关闭";
            };
            settingsMenu.Items.Add(saverItem);
            settingsMenu.Opening += delegate
            {
                blockItem.Checked = _blockTrackers;
                saverItem.Checked = _dataSaver;
            };
            settingsMenu.Items.Add(new ToolStripSeparator());
            settingsMenu.Items.Add("浏览历史…", null, delegate { ShowHistoryDialog(); });
            settingsMenu.Items.Add("浏览器设置…", null, delegate { ShowBrowserSettingsDialog(); });
            settingsMenu.Items.Add("进入 CHENMU Browser 官网", null, delegate { OpenOfficialWebsite(); });
            settingsMenu.Items.Add(new ToolStripSeparator());
            settingsMenu.Items.Add("下载管理…", null, delegate { ShowDownloadsDialog(); });
            settingsMenu.Items.Add("复制当前网址", null, delegate { CopyCurrentUrl(); });
            settingsMenu.Items.Add("清除浏览数据…", null, async (sender, args) => { await ClearBrowsingDataAsync(); });
            settingsMenu.Items.Add(new ToolStripSeparator());
            settingsMenu.Items.Add("检查更新…", null, async (sender, args) => { await CheckForUpdatesAsync(true); });
            settingsMenu.Items.Add("关于 CHENMU Browser", null, (sender, args) => ShowAbout());
            Button settingsButton = null;
            settingsButton = CreateChromeButton("⋯", "设置与隐私", delegate
            {
                settingsMenu.Show(settingsButton, new Point(0, settingsButton.Height));
            });
            chrome.Controls.Add(CreateReaderChromeButton(), 7, 0);
            chrome.Controls.Add(settingsButton, 8, 0);
            _header.Controls.Add(chrome);

            BuildTabSidebar();

            _viewport.Dock = DockStyle.Fill;
            _viewport.BackColor = Color.FromArgb(244, 248, 253);
            _homeView = BuildHomeView();
            _viewport.Controls.Add(_homeView);

            _statusPanel.Dock = DockStyle.Bottom;
            _statusPanel.Height = 26;
            _statusPanel.BackColor = Color.FromArgb(249, 251, 255);
            _statusPanel.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(Color.FromArgb(226, 233, 241)))
                    e.Graphics.DrawLine(pen, 0, 0, _statusPanel.Width, 0);
            };
            _statusLabel.Dock = DockStyle.Fill;
            _statusLabel.Padding = new Padding(14, 2, 0, 0);
            _statusLabel.Text = "就绪";
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _statusLabel.ForeColor = Color.FromArgb(112, 124, 138);
            _statusLabel.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
            _statusPanel.Controls.Add(_statusLabel);

            _browserSurface.Dock = DockStyle.Fill;
            _browserSurface.BackColor = Color.White;
            _browserSurface.Controls.Add(_viewport);
            _browserSurface.Controls.Add(_statusPanel);
            _browserSurface.Controls.Add(_header);
            _browserSurface.Controls.Add(_tabSidebarHost);
            _browserSurface.SizeChanged += delegate { LayoutTabSidebarOverlay(); };
            _header.SizeChanged += delegate { LayoutTabSidebarOverlay(); };
            _statusPanel.SizeChanged += delegate { LayoutTabSidebarOverlay(); };
            _rootLayout.Controls.Add(_browserSurface, 0, 0);
            Controls.Add(_rootLayout);
            LayoutTabSidebarOverlay();
            CreateNewTab();
        }

        private void BuildTabSidebar()
        {
            var scale = Math.Max(1F, DeviceDpi / 96F);
            var collapsedWidth = Math.Max(8, (int)Math.Round(10F * scale));
            var scrollWidth = Math.Max(14, (int)Math.Round(16F * scale));

            _rootLayout.Dock = DockStyle.Fill;
            _rootLayout.Margin = Padding.Empty;
            _rootLayout.Padding = Padding.Empty;
            _rootLayout.ColumnCount = 1;
            _rootLayout.RowCount = 1;
            _rootLayout.BackColor = Color.White;
            _rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            _rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            _tabSidebarHost.Dock = DockStyle.None;
            _tabSidebarHost.Location = Point.Empty;
            _tabSidebarHost.Size = new Size(collapsedWidth, 100);
            _tabSidebarHost.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            _tabSidebarHost.BackColor = Color.FromArgb(249, 251, 255);
            _tabSidebarContent.Dock = DockStyle.Fill;
            _tabSidebarContent.Visible = false;
            _tabSidebarContent.BackColor = Color.FromArgb(249, 251, 255);
            _tabSidebarContent.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(Color.FromArgb(226, 233, 241)))
                    e.Graphics.DrawLine(pen, _tabSidebarContent.Width - 1, 0,
                        _tabSidebarContent.Width - 1, _tabSidebarContent.Height);
            };

            _tabSidebarEdge.Dock = DockStyle.Left;
            _tabSidebarEdge.Width = collapsedWidth;
            _tabSidebarEdge.BackColor = Color.FromArgb(239, 245, 252);
            _tabSidebarEdge.Cursor = Cursors.Hand;
            _tabSidebarEdge.AccessibleName = "点击展开或收起标签栏";
            _tabSidebarEdge.Paint += delegate(object sender, PaintEventArgs e)
            {
                var markerHeight = Math.Max(24, (int)Math.Round(30F * scale));
                var markerWidth = Math.Max(2, (int)Math.Round(2F * scale));
                var marker = new Rectangle(
                    Math.Max(0, (_tabSidebarEdge.ClientSize.Width - markerWidth) / 2),
                    Math.Max(0, (_tabSidebarEdge.ClientSize.Height - markerHeight) / 2),
                    markerWidth, Math.Min(markerHeight, _tabSidebarEdge.ClientSize.Height));
                using (var brush = new SolidBrush(Color.FromArgb(122, 151, 183)))
                    e.Graphics.FillRectangle(brush, marker);
            };
            _tabSidebarHost.Controls.Add(_tabSidebarContent);
            _tabSidebarHost.Controls.Add(_tabSidebarEdge);
            _tabSidebarEdge.Click += delegate { SetTabSidebarExpanded(!_tabSidebarExpanded); };

            var sidebarLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = new Padding((int)Math.Round(4F * scale), (int)Math.Round(7F * scale),
                    (int)Math.Round(3F * scale), (int)Math.Round(5F * scale)),
                BackColor = _tabSidebarContent.BackColor
            };
            sidebarLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            sidebarLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, scrollWidth));
            sidebarLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            sidebarLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, Math.Max(40, (int)Math.Round(44F * scale))));

            _tabViewport.Dock = DockStyle.Fill;
            _tabViewport.Margin = Padding.Empty;
            _tabViewport.BackColor = _tabSidebarContent.BackColor;
            _tabHeaders.Dock = DockStyle.None;
            _tabHeaders.AutoScroll = false;
            _tabHeaders.WrapContents = false;
            _tabHeaders.FlowDirection = FlowDirection.TopDown;
            _tabHeaders.Margin = Padding.Empty;
            _tabHeaders.Padding = Padding.Empty;
            _tabHeaders.Location = Point.Empty;
            _tabHeaders.ControlAdded += delegate { UpdateTabSidebar(); };
            _tabHeaders.ControlRemoved += delegate { UpdateTabSidebar(); };
            _tabViewport.Controls.Add(_tabHeaders);
            _tabViewport.SizeChanged += delegate { UpdateTabSidebar(); };

            _tabScrollBar.Dock = DockStyle.Fill;
            _tabScrollBar.Margin = Padding.Empty;
            _tabScrollBar.Visible = false;
            _tabScrollBar.Minimum = 0;
            _tabScrollBar.SmallChange = Math.Max(1, (int)Math.Round(40F * scale));
            _tabScrollBar.LargeChange = 1;
            _tabScrollBar.ValueChanged += delegate { PositionTabHeaders(); };

            var newTabButton = new Button
            {
                Text = "＋ 新标签页",
                Dock = DockStyle.Fill,
                Margin = new Padding(2, (int)Math.Round(3F * scale), 2, 0),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(249, 251, 255),
                ForeColor = Color.FromArgb(66, 82, 100),
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding((int)Math.Round(8F * scale), 0, 0, 0),
                Cursor = Cursors.Hand,
                TabStop = false,
                AccessibleName = "新建标签页"
            };
            newTabButton.FlatAppearance.BorderSize = 0;
            newTabButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(239, 244, 250);
            newTabButton.Click += delegate { CreateNewTab(); FocusAddress(); };
            _toolTip.SetToolTip(newTabButton, "新建标签页（Ctrl+T）");

            sidebarLayout.Controls.Add(_tabViewport, 0, 0);
            sidebarLayout.Controls.Add(_tabScrollBar, 1, 0);
            sidebarLayout.Controls.Add(newTabButton, 0, 1);
            sidebarLayout.SetColumnSpan(newTabButton, 2);
            _tabSidebarContent.Controls.Add(sidebarLayout);
        }

        private void SetTabSidebarExpanded(bool expanded)
        {
            if (_tabSidebarExpanded == expanded)
                return;
            _tabSidebarExpanded = expanded;
            var scale = Math.Max(1F, DeviceDpi / 96F);
            var width = (int)Math.Round((expanded ? 118F : 10F) * scale);
            _tabSidebarContent.Visible = expanded;
            _tabSidebarHost.Width = width;
            _tabSidebarEdge.Width = Math.Max(8, (int)Math.Round(10F * scale));
            _tabSidebarHost.BringToFront();
            if (expanded)
            {
                UpdateTabSidebar();
                EnsureTabVisible(_activeTab);
            }
#if DEBUG
            UpdateVisualTestStatus();
#endif
        }

        private BrowserTab CreateNewTab()
        {
            var tab = new BrowserTab { Title = "新标签页", IsHome = true };
            var header = new Panel
            {
                Width = 214,
                Height = 38,
                Margin = new Padding(3, 2, 3, 2),
                BackColor = Color.FromArgb(243, 246, 250),
                Tag = tab
            };
            header.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(_activeTab == tab ? Color.FromArgb(215, 223, 233) : Color.FromArgb(225, 231, 239)))
                    e.Graphics.DrawRectangle(pen, 0, 0, header.Width - 1, header.Height - 1);
            };
            var headerLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = new Padding(1),
                BackColor = header.BackColor
            };
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28F));
            headerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            var titleButton = new Button
            {
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                Text = tab.Title,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Padding = new Padding(7, 0, 3, 0),
                BackColor = header.BackColor,
                ForeColor = Color.FromArgb(52, 66, 82),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            titleButton.FlatAppearance.BorderSize = 0;
            titleButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(241, 245, 249);
            titleButton.Click += delegate { SwitchToTab(tab); };
            _toolTip.SetToolTip(titleButton, tab.Title);
            var closeButton = new Button
            {
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                Text = "×",
                Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point),
                BackColor = header.BackColor,
                ForeColor = Color.FromArgb(105, 117, 131),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            closeButton.FlatAppearance.BorderSize = 0;
            closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(226, 79, 79);
            closeButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(204, 58, 58);
            closeButton.Click += delegate { CloseTab(tab); };
            _toolTip.SetToolTip(closeButton, "关闭标签页");
            headerLayout.Controls.Add(titleButton, 0, 0);
            headerLayout.Controls.Add(closeButton, 1, 0);
            header.Controls.Add(headerLayout);

            tab.Header = header;
            _tabs.Add(tab);
            _tabHeaders.Controls.Add(header);
            UpdateTabSidebar();
            SwitchToTab(tab);
            FocusAddress();
            return tab;
        }

        private void OpenReader()
        {
            if (_activeTab != null && _activeTab.IsReader && _activeTab.ReaderView != null)
            {
                SwitchToTab(_activeTab);
                return;
            }

            CreateNewTab();
            var tab = _activeTab;
            if (tab == null)
                return;

            tab.IsHome = false;
            tab.IsReader = true;
            tab.Title = "CHENMU Reader";
            var readerView = new ReaderView(GetWebViewEnvironmentAsync,
                delegate(string path, int pageCount)
                {
                    return _restoreReaderPosition
                        ? ReaderPositionStorage.GetPageIndex(ReaderPositionPath, path, pageCount)
                        : 0;
                },
                delegate(string path, int pageIndex)
                {
                    if (_restoreReaderPosition && !_isIsolatedTest)
                        ReaderPositionStorage.Save(ReaderPositionPath, path, pageIndex);
                })
            {
                Dock = DockStyle.Fill,
                Visible = false
            };
            tab.ReaderView = readerView;
            _viewport.Controls.Add(readerView);
            SwitchToTab(tab);
        }

        private void CloseTab(BrowserTab tab)
        {
            var index = _tabs.IndexOf(tab);
            if (index < 0)
                return;
            var wasActive = tab == _activeTab;
            var closedUrl = tab.WebView != null && tab.WebView.CoreWebView2 != null
                ? tab.WebView.CoreWebView2.Source
                : tab.SessionUrl;
            _recentlyClosedTabs.Record(closedUrl, tab.Title, tab.ZoomFactor, tab.IsPrivate, tab.IsReader);
            if (tab.WebView != null && tab.WebView.CoreWebView2 != null)
                RemovePageAnimationScript(tab.WebView.CoreWebView2);
            _tabs.RemoveAt(index);
            _tabHeaders.Controls.Remove(tab.Header);
            UpdateTabSidebar();
            SaveSessionSnapshot();
            if (tab.Header != null)
                tab.Header.Dispose();
            if (tab.WebView != null)
            {
                try { if (tab.WebView.CoreWebView2 != null) tab.WebView.CoreWebView2.Stop(); }
                catch { }
                _viewport.Controls.Remove(tab.WebView);
                tab.WebView.Dispose();
                tab.WebView = null;
            }
            if (tab.ReaderView != null)
            {
                _viewport.Controls.Remove(tab.ReaderView);
                tab.ReaderView.Dispose();
                tab.ReaderView = null;
            }
            if (_tabs.Count == 0)
            {
                _activeTab = null;
                _webView = null;
                CreateNewTab();
                return;
            }
            if (wasActive)
                SwitchToTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
            else
            {
                UpdateTabHeaders();
                EnsureTabVisible(_activeTab);
            }
        }

        private void SwitchToTab(BrowserTab tab)
        {
            if (tab == null || !_tabs.Contains(tab))
                return;
            _activeTab = tab;
            _webView = tab.WebView;
            HideErrorView();
            foreach (var item in _tabs)
            {
                if (item.WebView != null)
                    item.WebView.Visible = false;
                if (item.ReaderView != null)
                    item.ReaderView.Visible = false;
            }

            if (tab.IsReader && tab.ReaderView != null)
            {
                tab.IsHome = false;
                _webView = null;
                _homeView.Visible = false;
                tab.ReaderView.Visible = true;
                tab.ReaderView.BringToFront();
                _addressBox.Text = String.Empty;
                _statusLabel.Text = "CHENMU Reader · 本地阅读";
                Text = "CHENMU Reader - CHENMU Browser" + (_isIsolatedTest ? "（隔离测试）" : String.Empty);
            }
            else if (tab.IsHome || tab.WebView == null || tab.WebView.CoreWebView2 == null)
            {
                tab.IsHome = true;
                _webView = tab.WebView;
                _homeView.Visible = true;
                _homeView.BringToFront();
                _addressBox.Text = String.Empty;
                _statusLabel.Text = "就绪";
                Text = "新标签页 - CHENMU Browser" + (_isIsolatedTest ? "（隔离测试）" : String.Empty);
            }
            else
            {
                _homeView.Visible = false;
                tab.WebView.Visible = true;
                tab.WebView.BringToFront();
                _addressBox.Text = tab.WebView.CoreWebView2.Source ?? String.Empty;
                UpdateWindowTitle(tab.WebView.CoreWebView2);
            }
            UpdateTabHeaders();
            EnsureTabVisible(tab);
            UpdateNavigationControls();
            SaveSessionSnapshot();
        }

        private void UpdateTabSidebar()
        {
            if (_tabSidebarHost.IsDisposed || _tabHeaders.IsDisposed || _tabViewport.IsDisposed || _updatingTabSidebar)
                return;
            _updatingTabSidebar = true;
            try
            {
                var viewportWidth = Math.Max(0, _tabViewport.ClientSize.Width);
                var viewportHeight = Math.Max(0, _tabViewport.ClientSize.Height);
                if (!_tabSidebarContent.Visible || viewportWidth == 0 || viewportHeight == 0)
                {
                    _tabScrollBar.Visible = false;
                    PositionTabHeaders();
                    return;
                }

                var scale = Math.Max(1F, _tabSidebarHost.DeviceDpi / 96F);
                foreach (Control header in _tabHeaders.Controls)
                    header.Width = Math.Max(1, viewportWidth - header.Margin.Horizontal);
                _tabHeaders.PerformLayout();

                long requiredHeight = _tabHeaders.Padding.Vertical;
                foreach (Control header in _tabHeaders.Controls)
                    requiredHeight += header.Height + header.Margin.Vertical;
                var maxScroll = Math.Max(0L, requiredHeight - viewportHeight);
                _tabScrollBar.Visible = maxScroll > 0;
                _tabHeaders.Width = viewportWidth;
                _tabHeaders.Height = (int)Math.Min(Int32.MaxValue / 2L, Math.Max(requiredHeight, viewportHeight));

                var largeChange = Math.Max(1, viewportHeight);
                var scrollRange = (int)Math.Min(Int32.MaxValue - (long)largeChange, maxScroll);
                _tabScrollBar.Minimum = 0;
                _tabScrollBar.SmallChange = Math.Max(1, (int)Math.Round(40F * scale));
                _tabScrollBar.LargeChange = largeChange;
                _tabScrollBar.Maximum = scrollRange + largeChange - 1;
                if (_tabScrollBar.Value > scrollRange)
                    _tabScrollBar.Value = scrollRange;
                PositionTabHeaders();
            }
            finally
            {
                _updatingTabSidebar = false;
            }
        }

        private void PositionTabHeaders()
        {
            if (_tabHeaders.IsDisposed)
                return;
            _tabHeaders.Location = new Point(0, -_tabScrollBar.Value);
        }

        private void EnsureTabVisible(BrowserTab tab)
        {
            if (tab == null || tab.Header == null || tab.Header.IsDisposed || !_tabScrollBar.Visible)
                return;
            var viewportHeight = _tabViewport.ClientSize.Height;
            if (viewportHeight <= 0)
                return;

            _tabHeaders.PerformLayout();
            var visibleTop = tab.Header.Top - tab.Header.Margin.Top;
            var visibleBottom = tab.Header.Bottom + tab.Header.Margin.Bottom;
            var target = _tabScrollBar.Value;
            if (visibleTop < target)
                target = visibleTop;
            else if (visibleBottom > target + viewportHeight)
                target = visibleBottom - viewportHeight;

            var maxScroll = Math.Max(0, _tabScrollBar.Maximum - _tabScrollBar.LargeChange + 1);
            target = Math.Max(0, Math.Min(target, maxScroll));
            if (_tabScrollBar.Value != target)
                _tabScrollBar.Value = target;
        }

        private void UpdateTabHeaders()
        {
            foreach (var tab in _tabs)
            {
                if (tab.Header == null || tab.Header.IsDisposed)
                    continue;
                var active = tab == _activeTab;
                tab.Header.BackColor = active ? Color.White : Color.FromArgb(243, 246, 250);
                var layout = tab.Header.Controls.Count == 0 ? null : tab.Header.Controls[0] as TableLayoutPanel;
                if (layout == null)
                    continue;
                layout.BackColor = tab.Header.BackColor;
                var titleButton = layout.GetControlFromPosition(0, 0) as Button;
                var closeButton = layout.GetControlFromPosition(1, 0) as Button;
                if (titleButton != null)
                {
                    titleButton.Text = tab.Title;
                    titleButton.BackColor = tab.Header.BackColor;
                    titleButton.ForeColor = active ? Color.FromArgb(35, 48, 63) : Color.FromArgb(75, 88, 103);
                    _toolTip.SetToolTip(titleButton, tab.Title);
                }
                if (closeButton != null)
                    closeButton.BackColor = tab.Header.BackColor;
                tab.Header.Invalidate();
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.T))
            {
                CreateNewTab();
                FocusAddress();
                return true;
            }
            if (keyData == (Keys.Control | Keys.W))
            {
                CloseTab(_activeTab);
                return true;
            }
            if (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.Shift | Keys.Tab))
            {
                if (_tabs.Count > 1 && _activeTab != null)
                {
                    var direction = (keyData & Keys.Shift) == Keys.Shift ? -1 : 1;
                    var index = _tabs.IndexOf(_activeTab);
                    SwitchToTab(_tabs[(index + direction + _tabs.Count) % _tabs.Count]);
                }
                return true;
            }
            if (keyData == (Keys.Control | Keys.L))
            {
                FocusAddress();
                return true;
            }
            if (IsPageToolShortcut(keyData))
            {
                // WebView2 forwards accelerator keys to its KeyDown event. Avoid handling
                // the same shortcut twice when focus is inside its native child window.
                if (_activeTab != null && _activeTab.WebView != null && _activeTab.WebView.ContainsFocus)
                    return base.ProcessCmdKey(ref msg, keyData);
                return HandlePageToolShortcut(keyData);
            }
            if (keyData == (Keys.Control | Keys.O) && _activeTab != null &&
                _activeTab.IsReader && _activeTab.ReaderView != null)
            {
                _activeTab.ReaderView.ShowOpenFileDialog();
                return true;
            }
            if (keyData == (Keys.Control | Keys.D))
            {
                AddCurrentBookmark();
                return true;
            }
            if (keyData == (Keys.Control | Keys.R) || keyData == Keys.F5)
            {
                ReloadCurrentPage();
                return true;
            }
            if (keyData == (Keys.Control | Keys.Shift | Keys.Delete))
            {
                ClearBrowsingDataFromShortcut();
                return true;
            }
            if (keyData == Keys.Escape)
            {
                if (_addressBox.Focused)
                {
                    _addressBox.Text = _webView != null && _webView.CoreWebView2 != null
                        ? _webView.CoreWebView2.Source ?? String.Empty
                        : String.Empty;
                    return true;
                }
                if (_homeSearchBox != null && _homeSearchBox.Focused)
                {
                    _homeSearchBox.Clear();
                    return true;
                }
                if (_webView != null && _webView.CoreWebView2 != null)
                {
                    _webView.CoreWebView2.Stop();
                    _statusLabel.Text = "已停止加载。";
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private async void ClearBrowsingDataFromShortcut()
        {
            await ClearBrowsingDataAsync();
        }

        private async Task CheckForUpdatesAsync(bool showNoUpdate)
        {
            if (_updateCheckInProgress)
                return;
            if (!showNoUpdate && _lastUpdateCheckUtc != DateTime.MinValue &&
                DateTime.UtcNow >= _lastUpdateCheckUtc &&
                DateTime.UtcNow - _lastUpdateCheckUtc < TimeSpan.FromHours(24))
                return;

            _updateCheckInProgress = true;
            var previousStatus = _statusLabel.Text;
            try
            {
                _lastUpdateCheckUtc = DateTime.UtcNow;
                SavePreferences();
                _statusLabel.Text = "正在检查更新…";

                var latest = await UpdateService.GetLatestReleaseAsync();
                if (IsDisposed || Disposing)
                    return;

                if (latest == null)
                {
                    _statusLabel.Text = "暂时没有可用的更新版本。";
                    if (showNoUpdate)
                        MessageBox.Show(this, "目前还没有可用的公开更新版本。", "检查更新",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var current = UpdateService.CurrentVersion;
                if (latest.Version.CompareTo(current) <= 0)
                {
                    _statusLabel.Text = "当前已是最新版本。";
                    if (showNoUpdate)
                        MessageBox.Show(this, "当前已是最新版本（v" + current.ToString(3) + "）。", "检查更新",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (!UpdateService.IsInstalledDirectory(Application.StartupPath))
                {
                    _statusLabel.Text = "发现新版本；安装版可使用自动更新。";
                    if (showNoUpdate)
                        MessageBox.Show(this, "发现新版本 v" + latest.Version.ToString(3) + "。当前运行的是便携版，请先使用安装器安装，之后即可在应用内更新。",
                            "发现更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var decision = MessageBox.Show(this,
                    "发现 CHENMU Browser 新版本 v" + latest.Version.ToString(3) + "。\r\n" +
                    "当前版本：v" + current.ToString(3) + "。\r\n\r\n" +
                    "现在下载并安装吗？浏览器会在安装时关闭，完成后自动重启。\r\n" +
                    "安装包来自公开 GitHub Releases，并会在运行前校验 SHA-256。",
                    "发现更新", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (decision != DialogResult.Yes)
                {
                    _statusLabel.Text = "发现新版本 v" + latest.Version.ToString(3) + "，可稍后更新。";
                    return;
                }

                _statusLabel.Text = "正在下载并校验更新安装包…";
                var installerPath = await UpdateService.DownloadAndVerifyAsync(latest);
                if (IsDisposed || Disposing)
                {
                    try { File.Delete(installerPath); }
                    catch { }
                    return;
                }

                _statusLabel.Text = "更新包校验通过，正在安装并重启…";
                UpdateService.LaunchInstallerAfterExit(installerPath, Process.GetCurrentProcess().Id,
                    Application.StartupPath, latest.Version);
                Close();
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                {
                    _statusLabel.Text = "更新检查未完成。";
                    if (showNoUpdate)
                        MessageBox.Show(this, "无法检查或安装更新：\r\n" + ex.Message, "CHENMU Browser",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally
            {
                _updateCheckInProgress = false;
                if (!IsDisposed && !Disposing && _statusLabel.Text == "正在检查更新…")
                    _statusLabel.Text = previousStatus;
            }
        }

        private void FocusAddress()
        {
            _addressBox.Focus();
            _addressBox.SelectAll();
        }

        private void GoBack()
        {
            var core = _activeTab == null || _activeTab.WebView == null ? null : _activeTab.WebView.CoreWebView2;
            if (core != null && core.CanGoBack)
            {
                core.GoBack();
            }
            UpdateNavigationControls();
        }

        private void GoForward()
        {
            var core = _activeTab == null || _activeTab.WebView == null ? null : _activeTab.WebView.CoreWebView2;
            if (core != null && core.CanGoForward)
            {
                core.GoForward();
            }
            UpdateNavigationControls();
        }

        private void ReloadCurrentPage()
        {
            if (_webView != null && _webView.CoreWebView2 != null)
            {
                _webView.CoreWebView2.Reload();
            }
        }

        private void CopyCurrentUrl()
        {
            var url = _webView != null && _webView.CoreWebView2 != null
                ? _webView.CoreWebView2.Source
                : _addressBox.Text;
            if (String.IsNullOrWhiteSpace(url))
            {
                _statusLabel.Text = "当前没有可复制的网址。";
                return;
            }
            try
            {
                Clipboard.SetText(url);
                _statusLabel.Text = "网址已复制。";
            }
            catch
            {
                _statusLabel.Text = "无法访问剪贴板。";
            }
        }

        private Button CreateReaderChromeButton()
        {
            var button = new RoundedButton
            {
                Text = "\uE8A5",
                Dock = DockStyle.Fill,
                Margin = new Padding(2),
                CornerRadius = 15,
                BackColor = Color.FromArgb(248, 250, 253),
                ForeColor = Color.FromArgb(198, 67, 77),
                Font = new Font("Segoe MDL2 Assets", 13F, FontStyle.Regular, GraphicsUnit.Point),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                TabStop = false,
                AccessibleName = "CHENMU Reader · 本地阅读"
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(250, 237, 239);
            button.Click += delegate { OpenReader(); };
            _toolTip.SetToolTip(button, "CHENMU Reader · 在浏览器内阅读本地文件");
            return button;
        }

        private Button CreateChromeButton(string text, string tooltip, Action action)
        {
            var button = new RoundedButton
            {
                Text = text,
                Dock = DockStyle.Fill,
                Margin = new Padding(2),
                CornerRadius = 15,
                BackColor = Color.FromArgb(248, 250, 253),
                ForeColor = Color.FromArgb(69, 84, 101),
                Font = new Font("Segoe UI Symbol", 13F, FontStyle.Regular, GraphicsUnit.Point),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                TabStop = false,
                AccessibleName = tooltip
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(231, 238, 246);
            button.Click += delegate { action(); };
            _toolTip.SetToolTip(button, tooltip);
            return button;
        }

        private Panel BuildHomeView()
        {
            var panel = new GradientPanel
            {
                Dock = DockStyle.Fill,
                StartColor = Color.FromArgb(249, 251, 255),
                EndColor = Color.FromArgb(239, 245, 252)
            };
            _homeGradientPanel = panel;
            panel.BackgroundImage = LoadHomeWallpaperImage();
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(28, 12, 28, 10),
                BackColor = Color.Transparent
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 86F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

            var hero = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.None,
                ColumnCount = 1,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            hero.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            hero.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var brandRow = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.None,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            brandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76F));
            brandRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            brandRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var brandInfo = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = Color.Transparent,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            brandInfo.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            brandInfo.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            brandInfo.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            brandInfo.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _homeBrandLabel = new Label
            {
                Text = "CHENMU Browser",
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(91, 111, 133),
                Margin = new Padding(1, 0, 0, 1)
            };
            var brandLogo = new PictureBox
            {
                Image = LoadBrandMark(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(64, 64),
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 12, 0)
            };
            if (brandLogo.Image == null)
                brandLogo.Visible = false;
            var title = new Label
            {
                Text = GetHomeGreeting(),
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft YaHei UI", 25F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(38, 58, 80),
                Anchor = AnchorStyles.Left,
                Margin = Padding.Empty
            };
            _homeGreetingLabel = title;
            _homeClockLabel = new Label
            {
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(101, 119, 139),
                Margin = new Padding(1, 2, 0, 0)
            };
            brandInfo.Controls.Add(_homeBrandLabel, 0, 0);
            brandInfo.Controls.Add(title, 0, 1);
            brandInfo.Controls.Add(_homeClockLabel, 0, 2);
            brandRow.Controls.Add(brandLogo, 0, 0);
            brandRow.Controls.Add(brandInfo, 1, 0);
            hero.Controls.Add(brandRow, 0, 0);
            layout.Controls.Add(hero, 0, 1);

            var searchFrame = new RoundedPanel
            {
                Width = 760,
                Height = 62,
                Anchor = AnchorStyles.None,
                Margin = new Padding(0),
                Padding = new Padding(8),
                SurfaceColor = Color.White,
                BorderColor = Color.FromArgb(218, 228, 239),
                CornerRadius = 19
            };
            var searchLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 1,
                BackColor = Color.White,
                Padding = new Padding(2, 1, 2, 1)
            };
            searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52F));
            searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30F));
            searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
            searchLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            var searchButton = new RoundedButton
            {
                Text = "\uE721",
                Dock = DockStyle.Fill,
                Margin = new Padding(1),
                CornerRadius = 15,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(61, 111, 170),
                Font = new Font("Segoe MDL2 Assets", 16F, FontStyle.Regular, GraphicsUnit.Point),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                TabStop = false,
                AccessibleName = "搜索"
            };
            searchButton.FlatAppearance.BorderSize = 0;
            searchButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(239, 245, 252);
            searchButton.Click += delegate { SearchFromHome(); };
            _toolTip.SetToolTip(searchButton, "搜索（Enter）");

            _homeSearchBox = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(48, 58, 70),
                BackColor = Color.White,
                Margin = Padding.Empty
            };
            var homeSearchBoxHost = CreateCenteredTextBoxHost(_homeSearchBox, new Padding(5, 0, 4, 0));
            SetCueBanner(_homeSearchBox, "搜索网页，或直接输入网址");
            _homeSearchBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    SearchFromHome();
                }
            };
            var homeSearchClearButton = new Button
            {
                Text = "×",
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(132, 145, 159),
                Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Regular, GraphicsUnit.Point),
                Margin = new Padding(0),
                TabStop = false,
                Cursor = Cursors.Hand,
                AccessibleName = "清空搜索内容",
                Visible = false
            };
            homeSearchClearButton.FlatAppearance.BorderSize = 0;
            homeSearchClearButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(239, 244, 249);
            homeSearchClearButton.Click += delegate { _homeSearchBox.Clear(); _homeSearchBox.Focus(); };
            _homeSearchBox.TextChanged += delegate { homeSearchClearButton.Visible = _homeSearchBox.TextLength > 0; };
            _homeSearchBox.Enter += delegate { searchFrame.BorderColor = Color.FromArgb(76, 139, 214); };
            _homeSearchBox.Leave += delegate { searchFrame.BorderColor = Color.FromArgb(218, 228, 239); };
            _toolTip.SetToolTip(homeSearchClearButton, "清空搜索内容");

            _homeSearchEngineButton = new Button
            {
                Text = GetSearchEngineDisplayName(_searchEngine) + " ▾",
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(52, 103, 170),
                Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point),
                Margin = new Padding(2),
                Cursor = Cursors.Hand
            };
            _homeSearchEngineButton.FlatAppearance.BorderSize = 0;
            _homeSearchEngineButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(245, 248, 252);
            var searchEngineMenu = new ContextMenuStrip();
            searchEngineMenu.Items.Add("Google", null, delegate { SetSearchEngine("google"); });
            searchEngineMenu.Items.Add("Bing", null, delegate { SetSearchEngine("bing"); });
            searchEngineMenu.Items.Add("Soumu（DuckDuckGo）", null, delegate { SetSearchEngine("soumu"); });
            _homeSearchEngineButton.Click += delegate
            {
                searchEngineMenu.Show(_homeSearchEngineButton, new Point(0, _homeSearchEngineButton.Height));
            };
            _toolTip.SetToolTip(_homeSearchEngineButton, "选择搜索引擎；Soumu 使用 DuckDuckGo 第三方结果");

            searchLayout.Controls.Add(searchButton, 0, 0);
            searchLayout.Controls.Add(homeSearchBoxHost, 1, 0);
            searchLayout.Controls.Add(homeSearchClearButton, 2, 0);
            searchLayout.Controls.Add(_homeSearchEngineButton, 3, 0);
            searchFrame.Controls.Add(searchLayout);
            layout.Controls.Add(searchFrame, 0, 2);

            var shortcuts = new FlowLayoutPanel
            {
                Anchor = AnchorStyles.None,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            shortcuts.Controls.Add(CreateShortcutCard("＋", "添加快捷方式", Color.FromArgb(77, 133, 193), ShowAddShortcutDialog));
            shortcuts.Controls.Add(CreateShortcutCard("☆", "收藏夹", Color.FromArgb(117, 105, 181), ShowBookmarksDialog));
            shortcuts.Controls.Add(CreateShortcutCard("↓", "下载", Color.FromArgb(74, 145, 137), ShowDownloadsDialog));
            shortcuts.Controls.Add(CreateShortcutCard("▧", "更换壁纸", Color.FromArgb(132, 117, 172), ShowHomeWallpaperMenu));
            layout.Controls.Add(shortcuts, 0, 3);

            panel.Controls.Add(layout);
            Action updateHomeLayout = delegate
            {
                if (panel.ClientSize.Width <= 0 || layout.IsDisposed)
                    return;

                var compact = panel.ClientSize.Width < 980 || panel.ClientSize.Height < 520;
                var scale = panel.DeviceDpi / 96F;
                layout.Padding = compact
                    ? new Padding(20, 8, 20, 8)
                    : new Padding(32, 18, 32, 14);
                var availableWidth = Math.Max(1, layout.ClientSize.Width - layout.Padding.Horizontal);
                var logoSize = (int)Math.Round(64F * scale);
                brandLogo.Size = new Size(logoSize, logoSize);
                brandLogo.Margin = new Padding(0, 0, (int)Math.Round(12F * scale), 0);
                brandRow.ColumnStyles[0].Width = (float)Math.Round(76F * scale);
                searchFrame.Width = Math.Min((int)Math.Round(760 * scale), availableWidth);
                searchFrame.Height = (int)Math.Round((compact ? 56 : 62) * scale);
                layout.RowStyles[1].SizeType = SizeType.Absolute;
                layout.RowStyles[1].Height = (int)Math.Round((compact ? 82 : 92) * scale);
                layout.RowStyles[2].SizeType = SizeType.Absolute;
                layout.RowStyles[2].Height = searchFrame.Height + (int)Math.Round(10 * scale);
                layout.RowStyles[3].SizeType = SizeType.Absolute;
                layout.RowStyles[3].Height = (int)Math.Round((compact ? 84 : 92) * scale);
                title.Font = new Font("Microsoft YaHei UI", compact ? 21F : 25F, FontStyle.Bold, GraphicsUnit.Point);
                brandInfo.PerformLayout();
                brandRow.PerformLayout();
                homeSearchBoxHost.PerformLayout();
                layout.PerformLayout();
            };
            layout.SizeChanged += delegate { updateHomeLayout(); };
            panel.SizeChanged += delegate { updateHomeLayout(); };
            panel.HandleCreated += delegate
            {
                panel.BeginInvoke((MethodInvoker)delegate { updateHomeLayout(); });
            };
            updateHomeLayout();
            return panel;
        }

        private Control CreateShortcutCard(string glyph, string title, Color accent, Action action)
        {
            var card = new RoundedPanel
            {
                Width = 104,
                Height = 78,
                Margin = new Padding(6, 3, 6, 3),
                Padding = new Padding(4),
                SurfaceColor = Color.White,
                BorderColor = Color.FromArgb(226, 233, 241),
                CornerRadius = 15,
                Cursor = Cursors.Hand
            };
            var badge = new RoundedPanel
            {
                Width = 34,
                Height = 34,
                CornerRadius = 17,
                SurfaceColor = accent,
                BorderColor = Color.Transparent,
                Location = new Point((card.Width - 34) / 2, 8),
                Cursor = Cursors.Hand
            };
            var glyphLabel = new Label
            {
                Text = glyph,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            badge.Controls.Add(glyphLabel);
            var titleLabel = new Label
            {
                Text = title,
                Dock = DockStyle.Bottom,
                Height = 24,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(62, 77, 94),
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Cursor = Cursors.Hand
            };
            card.Controls.Add(titleLabel);
            card.Controls.Add(badge);
            card.MouseEnter += delegate
            {
                card.SurfaceColor = Color.FromArgb(244, 248, 253);
                card.BorderColor = Color.FromArgb(184, 207, 232);
            };
            card.MouseLeave += delegate
            {
                var point = card.PointToClient(Cursor.Position);
                if (card.ClientRectangle.Contains(point))
                    return;
                card.SurfaceColor = Color.White;
                card.BorderColor = Color.FromArgb(226, 233, 241);
            };
            card.Click += delegate { action(); };
            badge.Click += delegate { action(); };
            glyphLabel.Click += delegate { action(); };
            titleLabel.Click += delegate { action(); };
            _toolTip.SetToolTip(card, title);
            return card;
        }

        private void SetSearchEngine(string engine)
        {
            _searchEngine = String.Equals(engine, "bing", StringComparison.OrdinalIgnoreCase) ? "bing" :
                String.Equals(engine, "soumu", StringComparison.OrdinalIgnoreCase) ? "soumu" : "google";
            if (_homeSearchEngineButton != null)
                _homeSearchEngineButton.Text = GetSearchEngineDisplayName(_searchEngine) + " ▾";
            SavePreferences();
            _statusLabel.Text = "默认搜索引擎：" + GetSearchEngineDisplayName(_searchEngine);
        }

        private async void SearchFromHome()
        {
            await NavigateAsync(_homeSearchBox.Text, true);
        }

        private static string GetHomeGreeting()
        {
            return HomeGreetingProvider.GetGreeting(DateTime.Now);
        }

        private static string GetBookmarkHost(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) ? uri.Host : url;
        }

        private void AddressBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                NavigateFromAddress();
            }
        }

        private async void NavigateFromAddress()
        {
            await NavigateAsync(_addressBox.Text, true);
        }

        private string NormalizeAddress(string text, bool upgradeHttp = false)
        {
            var value = (text ?? String.Empty).Trim();
            if (value.Length == 0)
                return null;

            if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return value;
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return upgradeHttp ? "https://" + value.Substring("http://".Length) : value;

            if (!value.Any(Char.IsWhiteSpace) && value.Contains("."))
                return "https://" + value;

            var searchUrl = _searchEngine == "bing"
                ? "https://www.bing.com/search?q="
                : _searchEngine == "soumu"
                    ? SoumuSearchUrlPrefix
                    : "https://www.google.com/search?q=";
            return searchUrl + Uri.EscapeDataString(value);
        }

        private async Task NavigateAsync(string input, bool upgradeHttp = false, BrowserTab navigationTab = null)
        {
            var target = NormalizeAddress(input, upgradeHttp);
            if (String.IsNullOrWhiteSpace(target))
                return;

            if (navigationTab == null)
            {
                if (_activeTab == null)
                    CreateNewTab();
                navigationTab = _activeTab;
            }
            var tab = navigationTab;
            if (tab == null || !_tabs.Contains(tab))
                return;
            if (tab.IsReader)
            {
                if (tab.ReaderView != null)
                {
                    _viewport.Controls.Remove(tab.ReaderView);
                    tab.ReaderView.Dispose();
                    tab.ReaderView = null;
                }
                tab.IsReader = false;
                tab.IsHome = true;
                tab.Title = "新标签页";
                UpdateTabHeaders();
            }
            try
            {
                await EnsureWebViewAsync(tab);
            }
            catch (Exception ex)
            {
                ShowRuntimeProblem(tab, ex, target);
                return;
            }

            if (IsDisposed || !_tabs.Contains(tab) || tab.WebView == null || tab.WebView.CoreWebView2 == null)
                return;

            tab.IsHome = false;
            if (tab == _activeTab)
            {
                _webView = tab.WebView;
                HideErrorView();
                _homeView.Visible = false;
                _webView.Visible = true;
                _webView.BringToFront();
                _addressBox.Text = target;
                _statusLabel.Text = "正在打开页面…";
            }
            try
            {
                tab.WebView.CoreWebView2.Navigate(target);
                UpdateTabHeaders();
            }
            catch (Exception ex)
            {
                if (tab == _activeTab)
                    _statusLabel.Text = "无法打开页面：" + ex.Message;
            }
        }

        private Task EnsureWebViewAsync(BrowserTab tab)
        {
            if (tab == null)
                return Task.CompletedTask;
            if (tab.WebView != null && tab.WebView.CoreWebView2 != null)
                return Task.CompletedTask;
            if (tab.InitializationTask == null || tab.InitializationTask.IsFaulted || tab.InitializationTask.IsCanceled)
                tab.InitializationTask = InitializeWebViewAsync(tab);
            return tab.InitializationTask;
        }

        private async Task InitializeWebViewAsync(BrowserTab tab)
        {
            WebView2 webView = null;
            try
            {
                webView = new WebView2 { Dock = DockStyle.Fill, Visible = false };
                webView.KeyDown += WebView_KeyDown;
                tab.WebView = webView;
                _viewport.Controls.Add(webView);
                var environment = await GetWebViewEnvironmentAsync();
                if (IsDisposed || Disposing || !_tabs.Contains(tab))
                    throw new OperationCanceledException("标签页已关闭。");
                await webView.EnsureCoreWebView2Async(environment);
                ConfigureCoreWebView(webView.CoreWebView2);
                await RegisterPageAnimationScriptAsync(webView.CoreWebView2);
                if (tab == _activeTab)
                    _webView = webView;
            }
            catch
            {
                if (webView != null)
                {
                    _viewport.Controls.Remove(webView);
                    webView.Dispose();
                }
                if (tab.WebView == webView)
                    tab.WebView = null;
                if (tab == _activeTab)
                    _webView = null;
                throw;
            }
            finally
            {
                tab.InitializationTask = null;
            }
        }

        private async Task<CoreWebView2Environment> GetWebViewEnvironmentAsync()
        {
            if (_webViewEnvironmentTask == null)
            {
                Directory.CreateDirectory(_dataRoot);
                var userDataFolder = Path.Combine(_dataRoot, "WebView2");
                var options = new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true };
                if (_isIsolatedTest)
                    options.AdditionalBrowserArguments = "--ignore-certificate-errors --use-fake-device-for-media-stream";
                _webViewEnvironmentTask = CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
            }
            try
            {
                return await _webViewEnvironmentTask;
            }
            catch
            {
                _webViewEnvironmentTask = null;
                throw;
            }
        }

        private void ConfigureCoreWebView(CoreWebView2 core)
        {
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsZoomControlEnabled = false;
            core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Balanced;
            ApplyTabZoom(core);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += Core_WebResourceRequested;
            core.DownloadStarting += Core_DownloadStarting;
            core.PermissionRequested += Core_PermissionRequested;
            core.NavigationStarting += Core_NavigationStarting;
            core.SourceChanged += delegate
            {
                if (IsActiveCore(core) && core.Source != null)
                    _addressBox.Text = core.Source;
                UpdateNavigationControls(core);
            };
            core.HistoryChanged += delegate { UpdateNavigationControls(core); };
            core.NavigationCompleted += delegate(object sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                if (IsActiveCore(core))
                    _statusLabel.Text = e.IsSuccess ? "完成" : "页面加载失败；可检查网址或网络连接。";
                if (e.IsSuccess)
                {
                    RecordHistory(core);
                    var ownerTab = FindTabForCore(core);
                    if (ownerTab != null)
                    {
                        ownerTab.SessionUrl = GetSessionUrl(core.Source);
                        SaveSessionSnapshot();
                    }
                }
                UpdateWindowTitle(core);
                UpdateNavigationControls(core);
            };
            core.DocumentTitleChanged += delegate { UpdateWindowTitle(core); };
            core.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e)
            {
                e.Handled = true;
                OpenNewTabFromPage(e.Uri);
            };
            core.ProcessFailed += delegate
            {
                if (IsActiveCore(core))
                    _statusLabel.Text = "网页内核遇到问题，可尝试刷新页面。";
            };
        }

        private void Core_PermissionRequested(object sender, CoreWebView2PermissionRequestedEventArgs e)
        {
            SitePermissionKind permission;
            switch (e.PermissionKind)
            {
                case CoreWebView2PermissionKind.Camera:
                    permission = SitePermissionKind.Camera;
                    break;
                case CoreWebView2PermissionKind.Microphone:
                    permission = SitePermissionKind.Microphone;
                    break;
                case CoreWebView2PermissionKind.Geolocation:
                    permission = SitePermissionKind.Geolocation;
                    break;
                case CoreWebView2PermissionKind.Notifications:
                    permission = SitePermissionKind.Notifications;
                    break;
                default:
                    return;
            }

            bool allowed = SitePermissionStorage.IsAllowed(_sitePermissionRules, e.Uri, permission);
            e.State = allowed ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            e.SavesInProfile = false;
            e.Handled = true;
        }

        private bool IsActiveCore(CoreWebView2 core)
        {
            return core != null && _activeTab != null && _activeTab.WebView != null &&
                _activeTab.WebView.CoreWebView2 == core;
        }

        private BrowserTab FindTabForCore(CoreWebView2 core)
        {
            return core == null ? null : _tabs.FirstOrDefault(tab =>
                tab.WebView != null && tab.WebView.CoreWebView2 == core);
        }

        private async void OpenNewTabFromPage(string url)
        {
            var newTab = CreateNewTab();
            if (!String.IsNullOrWhiteSpace(url))
                await NavigateAsync(url, false, newTab);
        }

        private void Core_DownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            try
            {
                var core = sender as CoreWebView2;
                var suggestedPath = e.ResultFilePath;
                var downloadsDirectory = String.IsNullOrWhiteSpace(suggestedPath)
                    ? String.Empty
                    : Path.GetDirectoryName(suggestedPath);
                if (String.IsNullOrWhiteSpace(downloadsDirectory))
                    downloadsDirectory = core == null ? String.Empty : core.Profile.DefaultDownloadFolderPath;
                if (String.IsNullOrWhiteSpace(downloadsDirectory))
                    downloadsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

                var fileName = Path.GetFileName(suggestedPath);
                if (String.IsNullOrWhiteSpace(fileName))
                    fileName = "download";
                var resultPath = GetUniqueDownloadPath(downloadsDirectory, fileName);
                e.ResultFilePath = resultPath;

                var ownerTab = _tabs.FirstOrDefault(tab => tab.WebView != null && tab.WebView.CoreWebView2 == core);
                var entry = new DownloadEntry
                {
                    Operation = e.DownloadOperation,
                    FilePath = resultPath,
                    Owner = ownerTab,
                    SavedRecord = new DownloadHistoryRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        StartedUtc = DateTime.UtcNow,
                        FilePath = resultPath,
                        FileName = Path.GetFileName(resultPath),
                        Status = DownloadRecordStatus.InProgress
                    }
                };
                _downloads.Insert(0, entry);
                PersistDownloadHistory();
                // Keep WebView2's built-in download UI visible; the browser list below tracks the same operation.
                e.Handled = false;
                if (ownerTab == _activeTab)
                    _statusLabel.Text = "正在下载：" + Path.GetFileName(resultPath);
                entry.Operation.StateChanged += (downloadSender, args) =>
                {
                    if (IsDisposed || Disposing || !IsHandleCreated)
                        return;
                    try
                    {
                        BeginInvoke((Action)delegate
                        {
                            if (IsDisposed || Disposing)
                                return;
                            if (entry.Operation.State != CoreWebView2DownloadState.InProgress)
                                PersistDownloadHistory();
                            if (entry.Owner != _activeTab)
                                return;
                            if (entry.Operation.State == CoreWebView2DownloadState.Completed)
                                _statusLabel.Text = "下载完成：" + Path.GetFileName(entry.FilePath);
                            else if (entry.Operation.State == CoreWebView2DownloadState.Interrupted)
                                _statusLabel.Text = entry.Operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled
                                    ? "下载已取消：" + Path.GetFileName(entry.FilePath)
                                    : "下载已中断：" + Path.GetFileName(entry.FilePath);
                        });
                    }
                    catch { }
                };
            }
            catch (Exception ex)
            {
                // If custom path selection fails, preserve WebView2's original path and allow its default flow.
                if (!IsDisposed && !Disposing)
                    _statusLabel.Text = "下载路径设置失败，继续使用默认下载流程：" + ex.Message;
            }
        }

        private string GetUniqueDownloadPath(string directory, string fileName)
        {
            Directory.CreateDirectory(directory);
            foreach (var invalidChar in Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(invalidChar, '_');
            if (String.IsNullOrWhiteSpace(fileName) || fileName == "." || fileName == "..")
                fileName = "download";

            var name = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            var candidate = Path.Combine(directory, fileName);
            var suffix = 1;
            while (File.Exists(candidate) || _downloads.Any(item =>
                String.Equals(item.FilePath, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                candidate = Path.Combine(directory, name + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")" + extension);
                suffix++;
            }
            return candidate;
        }

        private void Core_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            var core = sender as CoreWebView2;
            Uri uri;
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out uri))
                return;

            // Do not cancel and re-Navigate HTTP history entries here. That creates a new
            // history item and can destroy the forward stack during back/forward traversal.
            // Address-bar HTTP input is upgraded in NormalizeAddress; existing page history
            // is allowed to complete with its original URL.
            if (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == "about")
                return;

            e.Cancel = true;
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                if (IsActiveCore(core))
                    _statusLabel.Text = "无法打开外部链接：" + ex.Message;
            }
        }

        private void Core_WebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var shouldBlock = _dataSaver && e.ResourceContext == CoreWebView2WebResourceContext.Image;
            if (!shouldBlock && _blockTrackers)
            {
                Uri uri;
                if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out uri))
                    shouldBlock = DomainRuleMatcher.ShouldBlock(uri.Host, _blockedHosts, _allowedHosts);
            }

            if (shouldBlock)
            {
                var core = sender as CoreWebView2;
                if (core != null)
                    e.Response = core.Environment.CreateWebResourceResponse(null, 204, "Blocked", "Content-Length: 0\r\n");
            }
        }

        private static string NormalizeHost(string host)
        {
            return DomainRuleMatcher.NormalizeHost(host);
        }

        private static string GetSessionUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || String.IsNullOrWhiteSpace(uri.Host) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return null;
            return uri.AbsoluteUri;
        }

        private void NavigateFromPage(string url)
        {
            NavigateFromPageAsync(url);
        }

        private async void NavigateFromPageAsync(string url)
        {
            await NavigateAsync(url);
        }

        private void ShowHomeView()
        {
            if (_activeTab == null)
                CreateNewTab();
            if (_activeTab.IsReader)
            {
                if (_activeTab.ReaderView != null)
                {
                    _viewport.Controls.Remove(_activeTab.ReaderView);
                    _activeTab.ReaderView.Dispose();
                    _activeTab.ReaderView = null;
                }
                _activeTab.IsReader = false;
            }
            _activeTab.IsHome = true;
            _activeTab.Title = "新标签页";
            _activeTab.SessionUrl = null;
            SwitchToTab(_activeTab);
        }

        private void AddCurrentBookmark()
        {
            if (_webView == null || _webView.CoreWebView2 == null || String.IsNullOrWhiteSpace(_webView.CoreWebView2.Source))
            {
                _statusLabel.Text = "打开网页后即可收藏。";
                return;
            }

            var url = _webView.CoreWebView2.Source;
            var title = _webView.CoreWebView2.DocumentTitle;
            if (String.IsNullOrWhiteSpace(title))
                title = url;

            var bookmarks = ReadBookmarks();
            if (bookmarks.Any(b => String.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase)))
            {
                _statusLabel.Text = "这个页面已在书签中。";
                return;
            }

            bookmarks.Insert(0, new BookmarkItem { Title = title, Url = url });
            if (!WriteBookmarks(bookmarks))
                return;
            _statusLabel.Text = "已收藏：" + title;
        }

        private void ShowAddShortcutDialog()
        {
            var dialog = new Form
            {
                Text = "添加快捷方式",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                AutoScaleMode = AutoScaleMode.Dpi,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                ClientSize = new Size(460, 246)
            };
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(18, 14, 18, 12),
                BackColor = Color.White
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var titleLabel = new Label
            {
                Text = "名称",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(65, 78, 94)
            };
            var titleBox = new TextBox { Dock = DockStyle.Fill, MaxLength = 160 };
            SetCueBanner(titleBox, "例如：常用网站");
            var urlLabel = new Label
            {
                Text = "网址",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(65, 78, 94)
            };
            var urlBox = new TextBox { Dock = DockStyle.Fill, MaxLength = 4096 };
            SetCueBanner(urlBox, "输入网址，例如 example.com");
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 8, 0, 0),
                BackColor = Color.White
            };
            var cancelButton = new Button { Text = "取消", Width = 88, Height = 30, DialogResult = DialogResult.Cancel };
            var saveButton = new Button { Text = "保存", Width = 88, Height = 30, Margin = new Padding(0, 0, 8, 0) };
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(saveButton);
            layout.Controls.Add(titleLabel, 0, 0);
            layout.Controls.Add(titleBox, 0, 1);
            layout.Controls.Add(urlLabel, 0, 2);
            layout.Controls.Add(urlBox, 0, 3);
            layout.Controls.Add(buttons, 0, 4);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = saveButton;
            dialog.CancelButton = cancelButton;

            saveButton.Click += delegate
            {
                var value = (urlBox.Text ?? String.Empty).Trim();
                if (value.Length == 0)
                {
                    MessageBox.Show(dialog, "请输入快捷方式的网址。", "添加快捷方式",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    urlBox.Focus();
                    return;
                }

                Uri uri;
                if (!Uri.TryCreate(value, UriKind.Absolute, out uri))
                {
                    if (!Uri.TryCreate("https://" + value, UriKind.Absolute, out uri))
                        uri = null;
                }
                if (uri == null || String.IsNullOrWhiteSpace(uri.Host) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    MessageBox.Show(dialog, "请输入有效的 HTTP 或 HTTPS 网址。", "添加快捷方式",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    urlBox.Focus();
                    return;
                }

                var shortcutTitle = (titleBox.Text ?? String.Empty).Trim();
                if (shortcutTitle.Length == 0)
                    shortcutTitle = uri.Host;
                var bookmarks = ReadBookmarks();
                var existing = bookmarks.FirstOrDefault(item =>
                    String.Equals(item.Url, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                    existing.Title = shortcutTitle;
                else
                    bookmarks.Insert(0, new BookmarkItem { Title = shortcutTitle, Url = uri.AbsoluteUri });
                if (!WriteBookmarks(bookmarks))
                    return;
                _statusLabel.Text = "已保存快捷方式：" + shortcutTitle;
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            };
            dialog.Shown += delegate { titleBox.Focus(); };
            ApplyDialogStyle(dialog);
            dialog.ShowDialog(this);
        }

        private void ShowBookmarksDialog()
        {
            var dialog = new Form
            {
                Text = "书签",
                StartPosition = FormStartPosition.CenterParent,
                AutoScaleMode = AutoScaleMode.Dpi,
                Size = new Size(760, 520),
                MinimumSize = new Size(600, 390),
                FormBorderStyle = FormBorderStyle.Sizable,
                MinimizeBox = false,
                MaximizeBox = false
            };

            var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
            var items = ReadBookmarks();
            foreach (var item in items)
                list.Items.Add(item);
            list.DisplayMember = "DisplayTitle";

            var buttons = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 58,
                ColumnCount = 5,
                RowCount = 1,
                Padding = new Padding(6),
                BackColor = Color.White
            };
            for (var column = 0; column < 5; column++)
                buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
            buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            var importButton = new Button { Text = "导入 HTML", Dock = DockStyle.Fill, Margin = new Padding(3) };
            var exportButton = new Button { Text = "导出 HTML", Dock = DockStyle.Fill, Margin = new Padding(3) };
            var openButton = new Button { Text = "打开", Dock = DockStyle.Fill, Margin = new Padding(3) };
            var deleteButton = new Button { Text = "删除", Dock = DockStyle.Fill, Margin = new Padding(3) };
            var closeButton = new Button { Text = "关闭", Dock = DockStyle.Fill, Margin = new Padding(3), DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(importButton, 0, 0);
            buttons.Controls.Add(exportButton, 1, 0);
            buttons.Controls.Add(openButton, 2, 0);
            buttons.Controls.Add(deleteButton, 3, 0);
            buttons.Controls.Add(closeButton, 4, 0);
            dialog.Controls.Add(list);
            dialog.Controls.Add(buttons);
            dialog.CancelButton = closeButton;
            dialog.AcceptButton = openButton;

            openButton.Click += async delegate { await NavigateSelectedBookmarkAsync(list, dialog); };
            list.DoubleClick += async delegate { await NavigateSelectedBookmarkAsync(list, dialog); };
            importButton.Click += delegate
            {
                ImportBookmarksFromHtml(list, items, dialog);
                UpdateBookmarkListHorizontalExtent(list, items);
            };
            exportButton.Click += delegate { ExportBookmarksToHtml(items, dialog); };
            deleteButton.Click += delegate
            {
                var selected = list.SelectedItem as BookmarkItem;
                if (selected == null)
                    return;
                items.RemoveAll(b => String.Equals(b.Url, selected.Url, StringComparison.OrdinalIgnoreCase));
                if (!WriteBookmarks(items))
                    return;
                var current = list.SelectedIndex;
                list.Items.Remove(selected);
                if (list.Items.Count > 0)
                    list.SelectedIndex = Math.Min(current, list.Items.Count - 1);
            };
            ApplyDialogStyle(dialog);
            dialog.Shown += delegate { UpdateBookmarkListHorizontalExtent(list, items); };
            dialog.ShowDialog(this);
        }

        private static void UpdateBookmarkListHorizontalExtent(ListBox list, IEnumerable<BookmarkItem> items)
        {
            if (list == null || list.IsDisposed || items == null)
                return;
            var width = items.Select(item => TextRenderer.MeasureText(item.DisplayTitle ?? String.Empty, list.Font).Width)
                .DefaultIfEmpty(0)
                .Max();
            list.HorizontalExtent = Math.Min(100000, width + 24);
        }

        private void ImportBookmarksFromHtml(ListBox list, List<BookmarkItem> items, Form owner)
        {
            using (var picker = new OpenFileDialog
            {
                Title = "导入浏览器书签",
                Filter = "书签 HTML 文件 (*.html;*.htm)|*.html;*.htm|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            })
            {
                if (picker.ShowDialog(owner) != DialogResult.OK)
                    return;
                try
                {
                    if (new FileInfo(picker.FileName).Length > BookmarkHtmlTransfer.MaximumInputBytes)
                        throw new InvalidDataException("书签 HTML 超过 8 MiB 安全上限；请拆分后再导入。");
                    var parsed = BookmarkHtmlTransfer.Parse(File.ReadAllText(picker.FileName, Encoding.UTF8));
                    if (parsed.Count == 0)
                    {
                        MessageBox.Show(owner, "文件中没有找到有效的 HTTP 或 HTTPS 书签。", "导入书签",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    var merged = items.ToList();
                    var knownUrls = new HashSet<string>(merged.Select(item => item.Url), StringComparer.OrdinalIgnoreCase);
                    var added = 0;
                    var duplicates = 0;
                    foreach (var item in parsed)
                    {
                        if (!knownUrls.Add(item.Url))
                        {
                            duplicates++;
                            continue;
                        }
                        merged.Add(new BookmarkItem
                        {
                            Title = item.Title,
                            Url = item.Url,
                            FolderPath = item.FolderPath
                        });
                        added++;
                    }
                    if (!WriteBookmarks(merged))
                        return;

                    items.Clear();
                    items.AddRange(merged);
                    list.BeginUpdate();
                    list.Items.Clear();
                    foreach (var item in items)
                        list.Items.Add(item);
                    list.EndUpdate();
                    _statusLabel.Text = "已导入 " + added.ToString(CultureInfo.InvariantCulture) + " 个书签。";
                    MessageBox.Show(owner,
                        "已添加 " + added.ToString(CultureInfo.InvariantCulture) + " 个书签；重复网址跳过 " +
                        duplicates.ToString(CultureInfo.InvariantCulture) + " 个。导入采用合并方式，原书签不会被覆盖。",
                        "导入书签", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(owner, "无法导入书签：\r\n" + ex.Message, "导入书签",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void ExportBookmarksToHtml(List<BookmarkItem> items, Form owner)
        {
            using (var picker = new SaveFileDialog
            {
                Title = "导出浏览器书签",
                Filter = "书签 HTML 文件 (*.html)|*.html|所有文件 (*.*)|*.*",
                DefaultExt = "html",
                AddExtension = true,
                FileName = "bookmarks.html",
                OverwritePrompt = true
            })
            {
                if (picker.ShowDialog(owner) != DialogResult.OK)
                    return;
                try
                {
                    var transferItems = items.Select(item => new BookmarkTransferItem
                    {
                        Title = item.Title,
                        Url = item.Url,
                        FolderPath = item.FolderPath
                    });
                    var html = BookmarkHtmlTransfer.Export(transferItems);
                    File.WriteAllText(picker.FileName, html, new System.Text.UTF8Encoding(false));
                    _statusLabel.Text = "书签已导出到 HTML 文件。";
                }
                catch (Exception ex)
                {
                    MessageBox.Show(owner, "无法导出书签：\r\n" + ex.Message, "导出书签",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void ShowDownloadsDialog()
        {
            var dialog = new Form
            {
                Text = "下载管理",
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(760, 430),
                MinimumSize = new Size(580, 340),
                FormBorderStyle = FormBorderStyle.Sizable,
                MinimizeBox = false,
                MaximizeBox = false,
                AutoScaleMode = AutoScaleMode.Dpi,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point)
            };

            var list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false,
                MultiSelect = false,
                Font = dialog.Font
            };
            list.Columns.Add("文件名", 300, HorizontalAlignment.Left);
            list.Columns.Add("状态", 125, HorizontalAlignment.Left);
            list.Columns.Add("进度", 210, HorizontalAlignment.Left);
            var listHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            var emptyState = new Label
            {
                Text = "暂无下载记录。\r\n请先在网页中点击文件的下载链接；下载开始后会显示在这里。",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(110, 125, 145),
                BackColor = Color.White,
                Font = dialog.Font
            };
            listHost.Controls.Add(list);
            listHost.Controls.Add(emptyState);
            emptyState.BringToFront();

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8),
                WrapContents = false
            };
            var closeButton = new Button { Text = "关闭", Width = 78, Height = 30, DialogResult = DialogResult.Cancel };
            var clearButton = new Button { Text = "清理记录", Width = 90, Height = 30 };
            _toolTip.SetToolTip(clearButton, "只删除下载记录，不会删除下载文件。");
            var folderButton = new Button { Text = "打开文件夹", Width = 104, Height = 30, Enabled = false };
            var cancelButton = new Button { Text = "取消下载", Width = 90, Height = 30, Enabled = false };
            var openButton = new Button { Text = "打开文件", Width = 84, Height = 30, Enabled = false };
            buttons.Controls.Add(closeButton);
            buttons.Controls.Add(clearButton);
            buttons.Controls.Add(folderButton);
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(openButton);
            dialog.Controls.Add(listHost);
            dialog.Controls.Add(buttons);
            dialog.CancelButton = closeButton;

            Action refresh = delegate
            {
                RefreshDownloadList(list);
                UpdateDownloadManagerButtons(list, openButton, folderButton, cancelButton, clearButton);
                emptyState.Visible = _downloads.Count == 0;
            };
            list.SelectedIndexChanged += delegate { UpdateDownloadManagerButtons(list, openButton, folderButton, cancelButton, clearButton); };
            list.DoubleClick += delegate { OpenSelectedDownload(list); };
            openButton.Click += delegate { OpenSelectedDownload(list); };
            folderButton.Click += delegate { OpenSelectedDownloadFolder(list); };
            cancelButton.Click += delegate
            {
                var entry = GetSelectedDownload(list);
                if (entry == null || entry.Operation == null || entry.Operation.State != CoreWebView2DownloadState.InProgress)
                    return;
                try { entry.Operation.Cancel(); }
                catch (Exception ex) { MessageBox.Show(dialog, "无法取消下载：" + ex.Message, "下载管理", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                refresh();
            };
            clearButton.Click += delegate
            {
                if (MessageBox.Show(dialog, "清理此列表中的下载记录？此操作不会删除任何已下载文件。", "清理下载记录",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
                    return;
                _downloads.RemoveAll(entry => entry.Operation == null ||
                    entry.Operation.State != CoreWebView2DownloadState.InProgress);
                PersistDownloadHistory();
                refresh();
            };

            var timer = new System.Windows.Forms.Timer { Interval = 700 };
            timer.Tick += delegate { refresh(); };
            dialog.FormClosed += delegate
            {
                timer.Stop();
                timer.Dispose();
            };
            refresh();
            timer.Start();
            ApplyDialogStyle(dialog);
            dialog.ShowDialog(this);
        }

        private void RefreshDownloadList(ListView list)
        {
            var selectedEntry = GetSelectedDownload(list);
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var entry in _downloads)
                {
                    var item = new ListViewItem(Path.GetFileName(entry.FilePath));
                    item.SubItems.Add(GetDownloadStatusText(entry));
                    item.SubItems.Add(GetDownloadProgressText(entry));
                    item.ToolTipText = entry.FilePath;
                    item.Tag = entry;
                    list.Items.Add(item);
                    if (selectedEntry != null && String.Equals(selectedEntry.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase))
                        item.Selected = true;
                }
            }
            finally
            {
                list.EndUpdate();
            }
        }

        private void UpdateDownloadManagerButtons(ListView list, Button openButton, Button folderButton,
            Button cancelButton, Button clearButton)
        {
            var entry = GetSelectedDownload(list);
            var inProgress = entry != null && entry.Operation != null &&
                entry.Operation.State == CoreWebView2DownloadState.InProgress;
            var completed = entry != null && (entry.Operation != null
                ? entry.Operation.State == CoreWebView2DownloadState.Completed
                : entry.SavedRecord != null && entry.SavedRecord.Status == DownloadRecordStatus.Completed);
            openButton.Enabled = completed && File.Exists(entry.FilePath);
            folderButton.Enabled = entry != null && Directory.Exists(Path.GetDirectoryName(entry.FilePath));
            cancelButton.Enabled = inProgress;
            clearButton.Enabled = _downloads.Any(item => item.Operation == null ||
                item.Operation.State != CoreWebView2DownloadState.InProgress);
        }

        private static DownloadEntry GetSelectedDownload(ListView list)
        {
            return list.SelectedItems.Count == 0 ? null : list.SelectedItems[0].Tag as DownloadEntry;
        }

        private static string GetDownloadStatusText(DownloadEntry entry)
        {
            if (entry == null)
                return "未知";
            if (entry.Operation == null)
            {
                var status = entry.SavedRecord == null ? DownloadRecordStatus.Interrupted : entry.SavedRecord.Status;
                switch (status)
                {
                    case DownloadRecordStatus.InProgress: return "下载中断";
                    case DownloadRecordStatus.Completed: return "已完成";
                    case DownloadRecordStatus.Canceled: return "已取消";
                    default: return "已中断";
                }
            }
            if (entry.Operation.State == CoreWebView2DownloadState.InProgress)
                return "下载中";
            if (entry.Operation.State == CoreWebView2DownloadState.Completed)
                return "已完成";
            return entry.Operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled
                ? "已取消"
                : "已中断";
        }

        private static string GetDownloadProgressText(DownloadEntry entry)
        {
            if (entry == null)
                return String.Empty;
            if (entry.Operation == null)
            {
                var record = entry.SavedRecord;
                if (record == null)
                    return String.Empty;
                var savedReceived = (ulong)Math.Max(0L, record.BytesReceived);
                if (record.TotalBytes.HasValue && record.TotalBytes.Value > 0)
                {
                    var savedTotal = (ulong)record.TotalBytes.Value;
                    var savedPercentage = (int)Math.Min(100.0, savedReceived * 100.0 / savedTotal);
                    return FormatDownloadSize(savedReceived) + " / " + FormatDownloadSize(savedTotal) + "  " +
                        savedPercentage.ToString(CultureInfo.InvariantCulture) + "%";
                }
                return FormatDownloadSize(savedReceived);
            }
            var received = (ulong)Math.Max(0L, entry.Operation.BytesReceived);
            var total = entry.Operation.TotalBytesToReceive.GetValueOrDefault();
            if (total > 0)
            {
                var percentage = (int)Math.Min(100.0, received * 100.0 / total);
                return FormatDownloadSize(received) + " / " + FormatDownloadSize(total) + "  " + percentage.ToString(CultureInfo.InvariantCulture) + "%";
            }
            return FormatDownloadSize(received);
        }

        private static string FormatDownloadSize(ulong bytes)
        {
            var size = (double)bytes;
            var unit = 0;
            var units = new[] { "B", "KB", "MB", "GB" };
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return size.ToString(unit == 0 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + units[unit];
        }

        private void OpenSelectedDownload(ListView list)
        {
            var entry = GetSelectedDownload(list);
            var completed = entry != null && (entry.Operation != null
                ? entry.Operation.State == CoreWebView2DownloadState.Completed
                : entry.SavedRecord != null && entry.SavedRecord.Status == DownloadRecordStatus.Completed);
            if (!completed || !File.Exists(entry.FilePath))
                return;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = entry.FilePath, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开文件：" + ex.Message, "下载管理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenSelectedDownloadFolder(ListView list)
        {
            var entry = GetSelectedDownload(list);
            if (entry == null)
                return;
            try
            {
                var folder = Path.GetDirectoryName(entry.FilePath);
                if (File.Exists(entry.FilePath))
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + entry.FilePath + "\"") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开下载文件夹：" + ex.Message, "下载管理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async Task NavigateSelectedBookmarkAsync(ListBox list, Form dialog)
        {
            var selected = list.SelectedItem as BookmarkItem;
            if (selected == null)
                return;
            var url = selected.Url;
            dialog.Close();
            await NavigateAsync(url);
        }

        private async Task ClearBrowsingDataAsync()
        {
            var result = MessageBox.Show(this,
                "清除本浏览器配置中的 Cookie、缓存和站点数据？此操作无法撤销。",
                "清除浏览数据", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result != DialogResult.Yes)
                return;

            try
            {
                await ClearBrowsingDataForCurrentProfileAsync();
                if (!ClearHistoryEntries())
                {
                    _statusLabel.Text = "浏览数据已清除，但历史记录未能清除。";
                    return;
                }
                _statusLabel.Text = "浏览数据与历史记录已清除。";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "清除失败：" + ex.Message, "CHENMU Browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task ClearBrowsingDataForCurrentProfileAsync()
        {
            var core = _tabs.Select(tab => tab.WebView == null ? null : tab.WebView.CoreWebView2)
                .FirstOrDefault(item => item != null);
            if (core != null)
            {
                await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
                core.CookieManager.DeleteAllCookies();
                return;
            }

            var temporaryView = new WebView2 { Visible = false, Size = new Size(1, 1) };
            _viewport.Controls.Add(temporaryView);
            try
            {
                var environment = await GetWebViewEnvironmentAsync();
                await temporaryView.EnsureCoreWebView2Async(environment);
                core = temporaryView.CoreWebView2;
                if (core == null)
                    throw new InvalidOperationException("网页配置尚未就绪，无法清理浏览数据。");
                await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
                core.CookieManager.DeleteAllCookies();
            }
            finally
            {
                _viewport.Controls.Remove(temporaryView);
                temporaryView.Dispose();
            }
        }

        private void ShowAbout()
        {
            var dialog = new Form
            {
                Text = "关于 CHENMU Browser",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(380, 190),
                Font = Font
            };
            var name = new Label
            {
                Text = "CHENMU Browser",
                Dock = DockStyle.Top,
                Height = 66,
                TextAlign = ContentAlignment.BottomCenter,
                Font = new Font("Microsoft YaHei UI", 19F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(39, 59, 82)
            };
            var version = new Label
            {
                Text = "版本 " + UpdateNoticeContent.GetEditionName(UpdateService.CurrentVersion),
                Dock = DockStyle.Top,
                Height = 42,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(104, 119, 137)
            };
            var close = new Button
            {
                Text = "确定",
                Width = 96,
                Height = 34,
                DialogResult = DialogResult.OK,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            close.Location = new Point(dialog.ClientSize.Width - close.Width - 22,
                dialog.ClientSize.Height - close.Height - 18);
            dialog.Controls.Add(close);
            dialog.Controls.Add(version);
            dialog.Controls.Add(name);
            dialog.AcceptButton = close;
            ApplyDialogStyle(dialog);
            dialog.ShowDialog(this);
        }

        private void ApplyDialogStyle(Form dialog)
        {
            if (dialog == null)
                return;
            dialog.BackColor = Color.FromArgb(249, 251, 255);
            dialog.ForeColor = Color.FromArgb(52, 66, 82);
            dialog.Font = Font;
            dialog.ShowIcon = false;
            dialog.Shown += delegate
            {
                StyleDialogControls(dialog, dialog.AcceptButton as Control);
            };
        }

        private void StyleDialogControls(Control parent, Control primaryButton)
        {
            foreach (Control control in parent.Controls)
            {
                var container = control as Panel;
                if (container != null && control.BackColor == SystemColors.Control)
                    control.BackColor = Color.FromArgb(249, 251, 255);

                var button = control as Button;
                if (button != null)
                {
                    var primary = button == primaryButton || button.DialogResult == DialogResult.OK;
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderSize = primary ? 0 : 1;
                    button.FlatAppearance.BorderColor = Color.FromArgb(218, 226, 236);
                    button.FlatAppearance.MouseOverBackColor = primary
                        ? Color.FromArgb(54, 129, 219)
                        : Color.FromArgb(239, 244, 250);
                    button.BackColor = primary ? Color.FromArgb(42, 118, 212) : Color.White;
                    button.ForeColor = primary ? Color.White : Color.FromArgb(57, 72, 90);
                    button.Cursor = Cursors.Hand;
                }

                var textBox = control as TextBox;
                if (textBox != null)
                {
                    textBox.BorderStyle = BorderStyle.FixedSingle;
                    textBox.BackColor = Color.White;
                    textBox.ForeColor = Color.FromArgb(47, 62, 80);
                }

                var list = control as ListBox;
                if (list != null)
                {
                    list.BorderStyle = BorderStyle.FixedSingle;
                    list.BackColor = Color.White;
                    list.ForeColor = Color.FromArgb(47, 62, 80);
                }

                var listView = control as ListView;
                if (listView != null)
                {
                    listView.BorderStyle = BorderStyle.FixedSingle;
                    listView.BackColor = Color.White;
                    listView.ForeColor = Color.FromArgb(47, 62, 80);
                }

                var combo = control as ComboBox;
                if (combo != null)
                    combo.FlatStyle = FlatStyle.Popup;

                StyleDialogControls(control, primaryButton);
            }
        }

        private void UpdateWindowTitle(CoreWebView2 core)
        {
            var tab = _tabs.FirstOrDefault(item => item.WebView != null && item.WebView.CoreWebView2 == core);
            if (tab == null)
                return;
            var title = core.DocumentTitle;
            if (String.IsNullOrWhiteSpace(title))
                title = String.IsNullOrWhiteSpace(core.Source) ? "网页" : GetBookmarkHost(core.Source);
            tab.Title = title;
            UpdateTabHeaders();
            if (tab == _activeTab)
                Text = title + " - CHENMU Browser" + (_isIsolatedTest ? "（隔离测试）" : String.Empty);
        }

        private void ShowRuntimeProblem(BrowserTab tab, Exception ex, string attemptedUrl)
        {
            if (tab != null && tab.WebView != null)
            {
                _viewport.Controls.Remove(tab.WebView);
                tab.WebView.Dispose();
                tab.WebView = null;
            }
            if (tab != null)
            {
                tab.IsHome = true;
                tab.Title = "新标签页";
            }
            if (tab != _activeTab || IsDisposed || Disposing)
                return;
            _webView = null;
            HideErrorView();
            _homeView.Visible = false;
            UpdateTabHeaders();

            var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(34) };
            var heading = new Label
            {
                Text = "暂时无法启动网页内核",
                Dock = DockStyle.Top,
                Height = 42,
                Font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(45, 55, 65)
            };
            var message = new Label
            {
                Text = "请确认已安装 Microsoft Edge WebView2 Evergreen Runtime。安装或修复运行时后，重新打开浏览器即可。\r\n\r\n尝试访问：" + attemptedUrl,
                Dock = DockStyle.Top,
                Height = 72,
                ForeColor = Color.FromArgb(85, 95, 105)
            };
            var details = new TextBox
            {
                Text = ex.ToString(),
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point)
            };
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 8, 0, 0)
            };
            var download = new Button { Text = "打开微软运行时下载页", AutoSize = true, Height = 32 };
            download.Click += delegate { OpenWebView2DownloadPage(); };
            var copy = new Button { Text = "复制诊断信息", AutoSize = true, Height = 32 };
            copy.Click += delegate
            {
                try { Clipboard.SetText(ex.ToString()); _statusLabel.Text = "诊断信息已复制。"; }
                catch { _statusLabel.Text = "无法访问剪贴板。"; }
            };
            var home = new Button { Text = "返回起始页", AutoSize = true, Height = 32 };
            home.Click += delegate { ShowHomeView(); };
            buttons.Controls.Add(download);
            buttons.Controls.Add(copy);
            buttons.Controls.Add(home);
            panel.Controls.Add(details);
            panel.Controls.Add(buttons);
            panel.Controls.Add(message);
            panel.Controls.Add(heading);
            _errorView = panel;
            _viewport.Controls.Add(_errorView);
            _errorView.BringToFront();
            _statusLabel.Text = "WebView2 初始化失败；可查看诊断信息。";
        }

        private void HideErrorView()
        {
            if (_errorView == null)
                return;
            _viewport.Controls.Remove(_errorView);
            _errorView.Dispose();
            _errorView = null;
        }

        private static void OpenWebView2DownloadPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/")
                {
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void LoadBlocklist()
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "domain-blocklist.txt");
                if (!File.Exists(path))
                    path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "domain-blocklist.txt");
                if (!File.Exists(path))
                    return;

                foreach (var line in File.ReadAllLines(path))
                {
                    var value = line.Trim();
                    if (value.Length == 0 || value.StartsWith("#", StringComparison.Ordinal))
                        continue;
                    var host = NormalizeHost(value);
                    if (host.Length > 0)
                        _blockedHosts.Add(host);
                }
            }
            catch
            {
                _blockedHosts.Clear();
            }
        }

        private void LoadPreferences()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return;
                if (new FileInfo(SettingsPath).Length > MaximumSettingsFileBytes)
                    return;
                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length != 2)
                        continue;
                    if (parts[0] == "searchEngine")
                    {
                        if (parts[1] == "google" || parts[1] == "bing" || parts[1] == "soumu")
                            _searchEngine = parts[1];
                        continue;
                    }
                    if (parts[0] == "historyRetentionDays")
                    {
                        int days;
                        if (Int32.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out days) &&
                            (days == 0 || days == 7 || days == 30 || days == 90 || days == 365))
                            _historyRetentionDays = days;
                        continue;
                    }
                    if (parts[0] == "downloadHistoryRetentionDays")
                    {
                        int days;
                        if (Int32.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out days) &&
                            (days == 0 || days == 7 || days == 30 || days == 90 || days == 365))
                            _downloadHistoryRetentionDays = days;
                        continue;
                    }
                    if (parts[0] == "lastUpdateCheckUtc")
                    {
                        DateTime timestamp;
                        if (DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp))
                            _lastUpdateCheckUtc = timestamp.ToUniversalTime();
                        continue;
                    }
                    if (parts[0] == "allowedHosts")
                    {
                        foreach (var item in parts[1].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (_allowedHosts.Count >= MaximumAllowedHosts)
                                break;
                            if (item.Length > 768)
                                continue;
                            try
                            {
                                var host = NormalizeHost(Uri.UnescapeDataString(item));
                                if (host.Length > 0)
                                    _allowedHosts.Add(host);
                            }
                            catch { }
                        }
                        continue;
                    }
                    bool value;
                    if (!Boolean.TryParse(parts[1], out value))
                        continue;
                    if (parts[0] == "blockTrackers")
                        _blockTrackers = value;
                    else if (parts[0] == "restorePreviousSession")
                        _restorePreviousSession = value;
                    else if (parts[0] == "restoreReaderPosition")
                        _restoreReaderPosition = value;
                    else if (parts[0] == "dataSaver")
                        _dataSaver = value;
                    else if (parts[0] == "pageAnimations")
                        _pageAnimationsEnabled = value;
                }
            }
            catch { }
        }

        private void SavePreferences()
        {
            try
            {
                Directory.CreateDirectory(_dataRoot);
                var lastUpdateCheck = _lastUpdateCheckUtc == DateTime.MinValue
                    ? String.Empty
                    : _lastUpdateCheckUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
                AtomicTextFile.WriteAllLines(SettingsPath, new[]
                {
                    "blockTrackers=" + _blockTrackers,
                    "restorePreviousSession=" + _restorePreviousSession,
                    "restoreReaderPosition=" + _restoreReaderPosition,
                    "dataSaver=" + _dataSaver,
                    "allowedHosts=" + String.Join(";", _allowedHosts
                        .Take(MaximumAllowedHosts)
                        .OrderBy(host => host, StringComparer.OrdinalIgnoreCase)
                        .Select(Uri.EscapeDataString)),
                    "pageAnimations=" + _pageAnimationsEnabled,
                    "searchEngine=" + _searchEngine,
                    "historyRetentionDays=" + _historyRetentionDays.ToString(CultureInfo.InvariantCulture),
                    "downloadHistoryRetentionDays=" + _downloadHistoryRetentionDays.ToString(CultureInfo.InvariantCulture),
                    "lastUpdateCheckUtc=" + lastUpdateCheck
                });
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "设置无法保存：" + ex.Message;
            }
        }

        private void LoadSessionSnapshot()
        {
            _startupSessionUrls.Clear();
            if (!_restorePreviousSession || _isIsolatedTest)
                return;
            try
            {
                if (!File.Exists(SessionPath))
                    return;
                if (new FileInfo(SessionPath).Length > MaximumSessionFileBytes)
                    return;
                var urls = new List<string>();
                var activeIndex = 0;
                foreach (var line in File.ReadAllLines(SessionPath))
                {
                    if (line.Length > 8192)
                        continue;
                    if (line.StartsWith("activeIndex=", StringComparison.Ordinal))
                    {
                        Int32.TryParse(line.Substring("activeIndex=".Length), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out activeIndex);
                        continue;
                    }
                    if (!line.StartsWith("url=", StringComparison.Ordinal))
                        continue;
                    try
                    {
                        var url = GetSessionUrl(Uri.UnescapeDataString(line.Substring("url=".Length)));
                        if (url != null)
                            urls.Add(url);
                    }
                    catch { }
                }
                const int maximumTabs = 40;
                if (urls.Count > maximumTabs)
                {
                    var discardCount = urls.Count - maximumTabs;
                    urls.RemoveRange(0, discardCount);
                    activeIndex = Math.Max(0, activeIndex - discardCount);
                }
                _startupSessionUrls.AddRange(urls);
                _startupSessionActiveIndex = urls.Count == 0
                    ? 0
                    : Math.Max(0, Math.Min(activeIndex, urls.Count - 1));
            }
            catch
            {
                _startupSessionUrls.Clear();
                _startupSessionActiveIndex = 0;
            }
        }

        private async Task RestoreSessionAsync()
        {
            if (!_restorePreviousSession || _isIsolatedTest || _startupSessionUrls.Count == 0)
                return;

            var urls = _startupSessionUrls.ToList();
            _isRestoringSession = true;
            try
            {
                if (_tabs.Count == 0)
                    CreateNewTab();
                var firstTab = _tabs[0];
                for (var index = 0; index < urls.Count; index++)
                {
                    BrowserTab targetTab;
                    if (index == 0)
                    {
                        SwitchToTab(firstTab);
                        targetTab = firstTab;
                    }
                    else
                        targetTab = CreateNewTab();
                        await NavigateAsync(urls[index], false, targetTab);
                }
                if (_tabs.Count > 0)
                    SwitchToTab(_tabs[Math.Min(_startupSessionActiveIndex, urls.Count - 1)]);
                _statusLabel.Text = "已恢复上次普通浏览会话。";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "恢复上次浏览会话时遇到问题：" + ex.Message;
            }
            finally
            {
                _isRestoringSession = false;
                _startupSessionUrls.Clear();
                SaveSessionSnapshot();
            }
        }

        private void SaveSessionSnapshot()
        {
            if (!_restorePreviousSession || _isIsolatedTest || _isRestoringSession ||
                _startupSessionUrls.Count > 0 || Disposing || IsDisposed || _sessionSnapshotQueue == null)
                return;
            try
            {
                var savedTabs = new List<KeyValuePair<BrowserTab, string>>();
                foreach (var tab in _tabs)
                {
                    // Private tabs are never written to the ordinary browsing-session snapshot.
                    if (tab == null || tab.IsPrivate)
                        continue;
                    string url = null;
                    try
                    {
                        var core = tab.WebView == null ? null : tab.WebView.CoreWebView2;
                        url = core == null ? null : GetSessionUrl(core.Source);
                    }
                    catch { }
                    if (url == null)
                        url = GetSessionUrl(tab.SessionUrl);
                    if (url != null)
                        savedTabs.Add(new KeyValuePair<BrowserTab, string>(tab, url));
                }

                const int maximumTabs = 40;
                if (savedTabs.Count > maximumTabs)
                    savedTabs = savedTabs.Skip(savedTabs.Count - maximumTabs).ToList();
                if (savedTabs.Count == 0)
                {
                    _sessionSnapshotQueue.Queue(null);
                    return;
                }

                var activeIndex = savedTabs.FindIndex(item => item.Key == _activeTab);
                if (activeIndex < 0)
                    activeIndex = savedTabs.Count - 1;
                var lines = new List<string> { "activeIndex=" + activeIndex.ToString(CultureInfo.InvariantCulture) };
                lines.AddRange(savedTabs.Select(item => "url=" + Uri.EscapeDataString(item.Value)));
                _sessionSnapshotQueue.Queue(lines.ToArray());
            }
            catch (Exception ex)
            {
                ReportPersistenceError("浏览会话无法保存：", ex);
            }
        }

        private void WriteSessionSnapshotFile(string[] lines)
        {
            if (lines == null || lines.Length == 0)
            {
                if (File.Exists(SessionPath))
                    File.Delete(SessionPath);
                return;
            }
            AtomicTextFile.WriteAllLines(SessionPath, lines);
        }

        private List<BookmarkItem> ReadBookmarks()
        {
            var result = new List<BookmarkItem>();
            try
            {
                if (!File.Exists(BookmarksPath))
                    return result;
                if (new FileInfo(BookmarksPath).Length > MaximumBookmarkStoreBytes)
                {
                    _statusLabel.Text = "书签文件超出安全读取上限；未加载现有书签。";
                    return result;
                }
                foreach (var line in File.ReadLines(BookmarksPath))
                {
                    if (result.Count >= MaximumStoredBookmarks)
                        break;
                    if (line.Length > 16384)
                        continue;
                    try
                    {
                        var parts = line.Split(new[] { '\t' }, 3);
                        Uri url;
                        if (parts.Length < 2 || !Uri.TryCreate(parts[1], UriKind.Absolute, out url) ||
                            (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                            continue;
                        result.Add(new BookmarkItem
                        {
                            Title = Uri.UnescapeDataString(parts[0]),
                            Url = parts[1],
                            FolderPath = parts.Length > 2 ? Uri.UnescapeDataString(parts[2]) : String.Empty
                        });
                    }
                    catch
                    {
                        continue;
                    }
                }
            }
            catch { }
            return result;
        }

        private bool WriteBookmarks(List<BookmarkItem> bookmarks)
        {
            try
            {
                if (bookmarks == null || bookmarks.Count > MaximumStoredBookmarks)
                {
                    _statusLabel.Text = "书签数量超过本地安全上限（" + MaximumStoredBookmarks.ToString(CultureInfo.InvariantCulture) + " 条），未保存。";
                    return false;
                }
                var lines = new List<string>(bookmarks.Count);
                long serializedBytes = 0;
                foreach (var bookmark in bookmarks)
                {
                    if (bookmark == null)
                        continue;
                    var title = bookmark.Title ?? String.Empty;
                    var folder = bookmark.FolderPath ?? String.Empty;
                    var url = bookmark.Url ?? String.Empty;
                    if (title.Length > 2048 || folder.Length > 4096 || url.Length > 8192)
                    {
                        _statusLabel.Text = "书签字段过长，未保存；标题、网址或文件夹路径超出限制。";
                        return false;
                    }
                    var line = Uri.EscapeDataString(title) + "\t" + url + "\t" + Uri.EscapeDataString(folder);
                    serializedBytes += Encoding.UTF8.GetByteCount(line) + 2;
                    if (serializedBytes > MaximumBookmarkStoreBytes)
                    {
                        _statusLabel.Text = "书签文件超过 4 MiB 安全上限，未保存。";
                        return false;
                    }
                    lines.Add(line);
                }
                AtomicTextFile.WriteAllLines(BookmarksPath, lines);
                return true;
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "书签无法保存：" + ex.Message;
                return false;
            }
        }

        private sealed class BrowserTab
        {
            public string Title { get; set; }
            public bool IsHome { get; set; }
            public bool IsPrivate { get; set; }
            public string SessionUrl { get; set; }
            public Panel Header { get; set; }
            public WebView2 WebView { get; set; }
            public ReaderView ReaderView { get; set; }
            public bool IsReader { get; set; }
            public Task InitializationTask { get; set; }
            public double ZoomFactor { get; set; } = BrowserZoomPolicy.Default;
        }

        private sealed class DownloadEntry
        {
            public CoreWebView2DownloadOperation Operation { get; set; }
            public string FilePath { get; set; }
            public BrowserTab Owner { get; set; }
            public DownloadHistoryRecord SavedRecord { get; set; }
        }

        private sealed class BookmarkItem
        {
            public string Title { get; set; }
            public string Url { get; set; }
            public string FolderPath { get; set; }
            public string DisplayTitle
            {
                get
                {
                    return String.IsNullOrWhiteSpace(FolderPath)
                        ? Title
                        : FolderPath.Replace(BookmarkHtmlTransfer.FolderSeparator, '›') + "  /  " + Title;
                }
            }
            public override string ToString() { return Title; }
        }
    }
}
