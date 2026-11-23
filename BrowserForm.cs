using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ChenmuBrowser
{
    internal sealed class BrowserForm : Form
    {
        private const int EmSetCueBanner = 0x1501;
        private const int DwmwaUseImmersiveDarkModeLegacy = 19;
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaBorderColor = 34;
        private const int DwmwaCaptionColor = 35;
        private const int DwmwaTextColor = 36;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, string lParam);
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private readonly Panel _header = new Panel();
        private readonly Panel _tabStrip = new Panel();
        private readonly FlowLayoutPanel _tabHeaders = new FlowLayoutPanel();
        private readonly TextBox _addressBox = new TextBox();
        private readonly Panel _viewport = new Panel();
        private readonly Panel _statusPanel = new Panel();
        private readonly Label _statusLabel = new Label();
        private readonly ToolTip _toolTip = new ToolTip();
        private readonly string _dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CHENMU Browser");
        private readonly HashSet<string> _blockedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private Panel _homeView;
        private Panel _errorView;
        private TextBox _homeSearchBox;
        private Button _homeSearchEngineButton;
        private WebView2 _webView;
        private readonly List<BrowserTab> _tabs = new List<BrowserTab>();
        private BrowserTab _activeTab;
        private Task<CoreWebView2Environment> _webViewEnvironmentTask;
        private readonly List<DownloadEntry> _downloads = new List<DownloadEntry>();
        private bool _blockTrackers = true;
        private bool _dataSaver;
        private string _searchEngine = "google";
        private DateTime _lastUpdateCheckUtc = DateTime.MinValue;
        private bool _updateCheckInProgress;

        private string SettingsPath { get { return Path.Combine(_dataRoot, "settings.ini"); } }
        private string BookmarksPath { get { return Path.Combine(_dataRoot, "bookmarks.tsv"); } }

        public BrowserForm()
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "CHENMU Browser";
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
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            LoadPreferences();
            LoadBlocklist();
            BuildInterface();
            HandleCreated += delegate { ApplyWindowCaptionColors(); };
            Shown += async delegate { await CheckForUpdatesAsync(false); };
        }

        private void ApplyWindowCaptionColors()
        {
            if (!IsHandleCreated)
                return;

            try
            {
                var lightMode = 0;
                var caption = ToColorRef(Color.FromArgb(248, 250, 253));
                var text = ToColorRef(Color.FromArgb(35, 55, 75));
                DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkModeLegacy, ref lightMode, sizeof(int));
                DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref lightMode, sizeof(int));
                DwmSetWindowAttribute(Handle, DwmwaCaptionColor, ref caption, sizeof(int));
                DwmSetWindowAttribute(Handle, DwmwaTextColor, ref text, sizeof(int));
                DwmSetWindowAttribute(Handle, DwmwaBorderColor, ref caption, sizeof(int));
            }
            catch { }
        }

        private static int ToColorRef(Color color)
        {
            return color.R | (color.G << 8) | (color.B << 16);
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

            chrome.Controls.Add(CreateChromeButton("←", "后退（Alt+←）", delegate
            {
                if (_webView != null && _webView.CoreWebView2 != null && _webView.CoreWebView2.CanGoBack)
                    _webView.CoreWebView2.GoBack();
            }), 0, 0);
            chrome.Controls.Add(CreateChromeButton("→", "前进（Alt+→）", delegate
            {
                if (_webView != null && _webView.CoreWebView2 != null && _webView.CoreWebView2.CanGoForward)
                    _webView.CoreWebView2.GoForward();
            }), 1, 0);
            chrome.Controls.Add(CreateChromeButton("↻", "刷新页面（Ctrl+R）", delegate
            {
                if (_webView != null && _webView.CoreWebView2 != null)
                    _webView.CoreWebView2.Reload();
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
            addressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28F));
            addressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            addressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30F));
            addressLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            var addressGlyph = new Label
            {
                Text = "⌕",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Symbol", 17F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(105, 120, 137)
            };
            _addressBox.BorderStyle = BorderStyle.None;
            _addressBox.Dock = DockStyle.Fill;
            _addressBox.MaxLength = 4096;
            _addressBox.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
            _addressBox.KeyDown += AddressBox_KeyDown;
            SetCueBanner(_addressBox, "输入网址或搜索关键词");
            _toolTip.SetToolTip(_addressBox, "输入网址或搜索关键词；按 Ctrl+L 可快速定位到这里");
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
            addressLayout.Controls.Add(_addressBox, 1, 0);
            addressLayout.Controls.Add(addressClearButton, 2, 0);
            addressFrame.Controls.Add(addressLayout);
            chrome.Controls.Add(addressFrame, 4, 0);

            chrome.Controls.Add(CreateChromeButton("☆", "收藏当前页面（Ctrl+D）", AddCurrentBookmark), 5, 0);
            chrome.Controls.Add(CreateChromeButton("▤", "查看书签", ShowBookmarksDialog), 6, 0);

            var settingsMenu = new ContextMenuStrip();
            var searchMenu = new ToolStripMenuItem("默认搜索引擎");
            searchMenu.DropDownItems.Add("Google", null, delegate { SetSearchEngine("google"); });
            searchMenu.DropDownItems.Add("Bing", null, delegate { SetSearchEngine("bing"); });
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
            chrome.Controls.Add(CreateChromeButton("↓", "查看下载", ShowDownloadsDialog), 7, 0);
            chrome.Controls.Add(settingsButton, 8, 0);
            _header.Controls.Add(chrome);

            _tabStrip.Dock = DockStyle.Top;
            _tabStrip.Height = 40;
            _tabStrip.Padding = new Padding(7, 3, 7, 3);
            _tabStrip.BackColor = _header.BackColor;
            _tabHeaders.Dock = DockStyle.Fill;
            _tabHeaders.AutoScroll = true;
            _tabHeaders.WrapContents = false;
            _tabHeaders.FlowDirection = FlowDirection.LeftToRight;
            _tabHeaders.Margin = Padding.Empty;
            var newTabButton = new Button
            {
                Text = "+",
                Dock = DockStyle.Right,
                Width = 34,
                FlatStyle = FlatStyle.Flat,
                BackColor = _tabStrip.BackColor,
                ForeColor = Color.FromArgb(66, 82, 100),
                Font = new Font("Segoe UI", 16F, FontStyle.Regular, GraphicsUnit.Point),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            newTabButton.FlatAppearance.BorderSize = 0;
            newTabButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(239, 244, 250);
            newTabButton.Click += delegate { CreateNewTab(); FocusAddress(); };
            _toolTip.SetToolTip(newTabButton, "新建标签页（Ctrl+T）");
            _tabStrip.Controls.Add(_tabHeaders);
            _tabStrip.Controls.Add(newTabButton);

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

            Controls.Add(_viewport);
            Controls.Add(_statusPanel);
            Controls.Add(_header);
            Controls.Add(_tabStrip);
            CreateNewTab();
        }

        private void CreateNewTab()
        {
            var tab = new BrowserTab { Title = "新标签页", IsHome = true };
            var header = new Panel
            {
                Width = 196,
                Height = 32,
                Margin = new Padding(2, 1, 4, 1),
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
            SwitchToTab(tab);
            FocusAddress();
        }

        private void CloseTab(BrowserTab tab)
        {
            var index = _tabs.IndexOf(tab);
            if (index < 0)
                return;
            var wasActive = tab == _activeTab;
            _tabs.RemoveAt(index);
            _tabHeaders.Controls.Remove(tab.Header);
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
                UpdateTabHeaders();
        }

        private void SwitchToTab(BrowserTab tab)
        {
            if (tab == null || !_tabs.Contains(tab))
                return;
            _activeTab = tab;
            _webView = tab.WebView;
            HideErrorView();
            foreach (var item in _tabs)
                if (item.WebView != null)
                    item.WebView.Visible = false;

            if (tab.IsHome || tab.WebView == null || tab.WebView.CoreWebView2 == null)
            {
                tab.IsHome = true;
                _webView = tab.WebView;
                _homeView.Visible = true;
                _homeView.BringToFront();
                _addressBox.Text = String.Empty;
                _statusLabel.Text = "就绪";
                Text = "新标签页 - CHENMU Browser";
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
            if (keyData == (Keys.Alt | Keys.Left))
            {
                GoBack();
                return true;
            }
            if (keyData == (Keys.Alt | Keys.Right))
            {
                GoForward();
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
                UpdateService.LaunchInstallerAfterExit(installerPath, Process.GetCurrentProcess().Id, Application.StartupPath);
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
            if (_webView != null && _webView.CoreWebView2 != null && _webView.CoreWebView2.CanGoBack)
                _webView.CoreWebView2.GoBack();
        }

        private void GoForward()
        {
            if (_webView != null && _webView.CoreWebView2 != null && _webView.CoreWebView2.CanGoForward)
                _webView.CoreWebView2.GoForward();
        }

        private void ReloadCurrentPage()
        {
            if (_webView != null && _webView.CoreWebView2 != null)
                _webView.CoreWebView2.Reload();
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
                RowCount = 2,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            hero.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            hero.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            hero.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var brandRow = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.None,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            var brandLogo = new PictureBox
            {
                Image = LoadBrandMark(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(44, 44),
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 10, 0)
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
                Margin = new Padding(0, 1, 0, 0)
            };
            brandRow.Controls.Add(brandLogo);
            brandRow.Controls.Add(title);
            var hint = new Label
            {
                Text = "简约、轻快、安心地浏览",
                AutoSize = true,
                Anchor = AnchorStyles.None,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(112, 126, 143),
                Margin = new Padding(0, 4, 0, 0)
            };
            hero.Controls.Add(brandRow, 0, 0);
            hero.Controls.Add(hint, 0, 1);
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
                Text = "⌕",
                Dock = DockStyle.Fill,
                Margin = new Padding(1),
                CornerRadius = 15,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(61, 111, 170),
                Font = new Font("Segoe UI Symbol", 19F, FontStyle.Regular, GraphicsUnit.Point),
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
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Height = 30,
                Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(48, 58, 70),
                Margin = new Padding(5, 0, 4, 0)
            };
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
                Text = (_searchEngine == "google" ? "Google" : "Bing") + " ▾",
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
            _homeSearchEngineButton.Click += delegate
            {
                searchEngineMenu.Show(_homeSearchEngineButton, new Point(0, _homeSearchEngineButton.Height));
            };
            _toolTip.SetToolTip(_homeSearchEngineButton, "选择搜索引擎");

            searchLayout.Controls.Add(searchButton, 0, 0);
            searchLayout.Controls.Add(_homeSearchBox, 1, 0);
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
                searchFrame.Width = Math.Min((int)Math.Round(760 * scale), availableWidth);
                searchFrame.Height = (int)Math.Round((compact ? 56 : 62) * scale);
                _homeSearchBox.Height = (int)Math.Round((compact ? 30 : 32) * scale);
                layout.RowStyles[1].SizeType = SizeType.Absolute;
                layout.RowStyles[1].Height = (int)Math.Round((compact ? 74 : 86) * scale);
                layout.RowStyles[2].SizeType = SizeType.Absolute;
                layout.RowStyles[2].Height = searchFrame.Height + (int)Math.Round(10 * scale);
                layout.RowStyles[3].SizeType = SizeType.Absolute;
                layout.RowStyles[3].Height = (int)Math.Round((compact ? 84 : 92) * scale);
                title.Font = new Font("Microsoft YaHei UI", compact ? 22F : 25F, FontStyle.Bold, GraphicsUnit.Point);
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
            _searchEngine = String.Equals(engine, "bing", StringComparison.OrdinalIgnoreCase) ? "bing" : "google";
            if (_homeSearchEngineButton != null)
                _homeSearchEngineButton.Text = (_searchEngine == "google" ? "Google" : "Bing") + " ▾";
            SavePreferences();
            _statusLabel.Text = "默认搜索引擎：" + (_searchEngine == "google" ? "Google" : "Bing");
        }

        private async void SearchFromHome()
        {
            await NavigateAsync(_homeSearchBox.Text);
        }

        private static string GetHomeGreeting()
        {
            var hour = DateTime.Now.Hour;
            if (hour >= 5 && hour < 12)
                return "早上好，今天想去哪儿？";
            if (hour >= 12 && hour < 18)
                return "下午好，今天想去哪儿？";
            if (hour >= 18 && hour < 23)
                return "晚上好，今天想去哪儿？";
            return "夜深了，今天想去哪儿？";
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
            await NavigateAsync(_addressBox.Text);
        }

        private string NormalizeAddress(string text)
        {
            var value = (text ?? String.Empty).Trim();
            if (value.Length == 0)
                return null;

            if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return value;

            if (!value.Any(Char.IsWhiteSpace) && value.Contains("."))
                return "https://" + value;

            var searchUrl = _searchEngine == "bing"
                ? "https://www.bing.com/search?q="
                : "https://www.google.com/search?q=";
            return searchUrl + Uri.EscapeDataString(value);
        }

        private async Task NavigateAsync(string input)
        {
            var target = NormalizeAddress(input);
            if (String.IsNullOrWhiteSpace(target))
                return;

            if (_activeTab == null)
                CreateNewTab();
            var tab = _activeTab;
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
                tab.WebView = webView;
                _viewport.Controls.Add(webView);
                var environment = await GetWebViewEnvironmentAsync();
                if (IsDisposed || Disposing || !_tabs.Contains(tab))
                    throw new OperationCanceledException("标签页已关闭。");
                await webView.EnsureCoreWebView2Async(environment);
                ConfigureCoreWebView(webView.CoreWebView2);
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
                _webViewEnvironmentTask = CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
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
            core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Balanced;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += Core_WebResourceRequested;
            core.DownloadStarting += Core_DownloadStarting;
            core.NavigationStarting += Core_NavigationStarting;
            core.SourceChanged += delegate
            {
                if (IsActiveCore(core) && core.Source != null)
                    _addressBox.Text = core.Source;
            };
            core.NavigationCompleted += delegate(object sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                if (IsActiveCore(core))
                    _statusLabel.Text = e.IsSuccess ? "完成" : "页面加载失败；可检查网址或网络连接。";
                UpdateWindowTitle(core);
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

        private bool IsActiveCore(CoreWebView2 core)
        {
            return core != null && _activeTab != null && _activeTab.WebView != null &&
                _activeTab.WebView.CoreWebView2 == core;
        }

        private async void OpenNewTabFromPage(string url)
        {
            CreateNewTab();
            if (!String.IsNullOrWhiteSpace(url))
                await NavigateAsync(url);
        }

        private void Core_DownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            try
            {
                var core = sender as CoreWebView2;
                var downloadsDirectory = core == null ? String.Empty : core.Profile.DefaultDownloadFolderPath;
                if (String.IsNullOrWhiteSpace(downloadsDirectory))
                    downloadsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

                var fileName = Path.GetFileName(e.ResultFilePath);
                if (String.IsNullOrWhiteSpace(fileName))
                    fileName = "download";
                var resultPath = GetUniqueDownloadPath(downloadsDirectory, fileName);
                e.ResultFilePath = resultPath;

                var ownerTab = _tabs.FirstOrDefault(tab => tab.WebView != null && tab.WebView.CoreWebView2 == core);
                var entry = new DownloadEntry { Operation = e.DownloadOperation, FilePath = resultPath, Owner = ownerTab };
                _downloads.Insert(0, entry);
                e.Handled = true;
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
                e.Cancel = true;
                if (!IsDisposed && !Disposing && IsActiveCore(sender as CoreWebView2))
                    _statusLabel.Text = "无法开始下载：" + ex.Message;
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

            if (String.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                var secureUrl = "https:" + e.Uri.Substring("http:".Length);
                if (IsActiveCore(core))
                    _addressBox.Text = secureUrl;
                if (core != null)
                    core.Navigate(secureUrl);
                return;
            }

            if (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "about")
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
                    shouldBlock = IsBlockedHost(uri.Host);
            }

            if (shouldBlock)
            {
                var core = sender as CoreWebView2;
                if (core != null)
                    e.Response = core.Environment.CreateWebResourceResponse(null, 204, "Blocked", "Content-Length: 0\r\n");
            }
        }

        private bool IsBlockedHost(string host)
        {
            var normalized = (host ?? String.Empty).TrimEnd('.');
            foreach (var blocked in _blockedHosts)
            {
                if (String.Equals(normalized, blocked, StringComparison.OrdinalIgnoreCase) ||
                    normalized.EndsWith("." + blocked, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
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
            _activeTab.IsHome = true;
            _activeTab.Title = "新标签页";
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
            dialog.ShowDialog(this);
        }

        private void ShowBookmarksDialog()
        {
            var dialog = new Form
            {
                Text = "书签",
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(640, 430),
                MinimumSize = new Size(520, 320),
                FormBorderStyle = FormBorderStyle.Sizable,
                MinimizeBox = false,
                MaximizeBox = false
            };

            var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            var items = ReadBookmarks();
            foreach (var item in items)
                list.Items.Add(item);
            list.DisplayMember = "Title";

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(6)
            };
            var closeButton = new Button { Text = "关闭", Width = 85, DialogResult = DialogResult.Cancel };
            var deleteButton = new Button { Text = "删除", Width = 85 };
            var openButton = new Button { Text = "打开", Width = 85 };
            buttons.Controls.Add(closeButton);
            buttons.Controls.Add(deleteButton);
            buttons.Controls.Add(openButton);
            dialog.Controls.Add(list);
            dialog.Controls.Add(buttons);
            dialog.CancelButton = closeButton;

            openButton.Click += async delegate { await NavigateSelectedBookmarkAsync(list, dialog); };
            list.DoubleClick += async delegate { await NavigateSelectedBookmarkAsync(list, dialog); };
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
            dialog.ShowDialog(this);
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
            var folderButton = new Button { Text = "打开文件夹", Width = 104, Height = 30, Enabled = false };
            var cancelButton = new Button { Text = "取消下载", Width = 90, Height = 30, Enabled = false };
            var openButton = new Button { Text = "打开文件", Width = 84, Height = 30, Enabled = false };
            buttons.Controls.Add(closeButton);
            buttons.Controls.Add(clearButton);
            buttons.Controls.Add(folderButton);
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(openButton);
            dialog.Controls.Add(list);
            dialog.Controls.Add(buttons);
            dialog.CancelButton = closeButton;

            Action refresh = delegate
            {
                RefreshDownloadList(list);
                UpdateDownloadManagerButtons(list, openButton, folderButton, cancelButton, clearButton);
            };
            list.SelectedIndexChanged += delegate { UpdateDownloadManagerButtons(list, openButton, folderButton, cancelButton, clearButton); };
            list.DoubleClick += delegate { OpenSelectedDownload(list); };
            openButton.Click += delegate { OpenSelectedDownload(list); };
            folderButton.Click += delegate { OpenSelectedDownloadFolder(list); };
            cancelButton.Click += delegate
            {
                var entry = GetSelectedDownload(list);
                if (entry == null || entry.Operation.State != CoreWebView2DownloadState.InProgress)
                    return;
                try { entry.Operation.Cancel(); }
                catch (Exception ex) { MessageBox.Show(dialog, "无法取消下载：" + ex.Message, "下载管理", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                refresh();
            };
            clearButton.Click += delegate
            {
                _downloads.RemoveAll(entry => entry.Operation.State != CoreWebView2DownloadState.InProgress);
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
            var inProgress = entry != null && entry.Operation.State == CoreWebView2DownloadState.InProgress;
            openButton.Enabled = entry != null && entry.Operation.State == CoreWebView2DownloadState.Completed && File.Exists(entry.FilePath);
            folderButton.Enabled = entry != null && Directory.Exists(Path.GetDirectoryName(entry.FilePath));
            cancelButton.Enabled = inProgress;
            clearButton.Enabled = _downloads.Any(item => item.Operation.State != CoreWebView2DownloadState.InProgress);
        }

        private static DownloadEntry GetSelectedDownload(ListView list)
        {
            return list.SelectedItems.Count == 0 ? null : list.SelectedItems[0].Tag as DownloadEntry;
        }

        private static string GetDownloadStatusText(DownloadEntry entry)
        {
            if (entry == null || entry.Operation == null)
                return "未知";
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
            if (entry == null || entry.Operation.State != CoreWebView2DownloadState.Completed || !File.Exists(entry.FilePath))
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
                var core = _tabs.Select(tab => tab.WebView == null ? null : tab.WebView.CoreWebView2)
                    .FirstOrDefault(item => item != null);
                if (core != null)
                {
                    await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
                    core.CookieManager.DeleteAllCookies();
                }
                _statusLabel.Text = "浏览数据已清除。";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "清除失败：" + ex.Message, "CHENMU Browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowAbout()
        {
            MessageBox.Show(this,
                "CHENMU Browser for Windows\r\n轻量桌面浏览器 · 版本 v" + UpdateService.CurrentVersion.ToString(3) +
                "\r\n\r\n基于 Microsoft Edge WebView2 Evergreen Runtime。\r\n" +
                "Ctrl+L 地址栏  ·  Ctrl+D 收藏  ·  Ctrl+R 刷新  ·  Esc 停止\r\n" +
                "内置每日更新检查；更新时连接 GitHub Releases，不上传浏览记录或设备标识。\r\n" +
                "密码自动保存与表单自动填充默认关闭。",
                "关于 CHENMU Browser", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                Text = title + " - CHENMU Browser";
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
                    var host = line.Trim();
                    if (host.Length == 0 || host.StartsWith("#", StringComparison.Ordinal))
                        continue;
                    _blockedHosts.Add(host.TrimStart('.').TrimEnd('.'));
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
                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length != 2)
                        continue;
                    if (parts[0] == "searchEngine")
                    {
                        if (parts[1] == "google" || parts[1] == "bing")
                            _searchEngine = parts[1];
                        continue;
                    }
                    if (parts[0] == "lastUpdateCheckUtc")
                    {
                        DateTime timestamp;
                        if (DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp))
                            _lastUpdateCheckUtc = timestamp.ToUniversalTime();
                        continue;
                    }
                    bool value;
                    if (!Boolean.TryParse(parts[1], out value))
                        continue;
                    if (parts[0] == "blockTrackers")
                        _blockTrackers = value;
                    else if (parts[0] == "dataSaver")
                        _dataSaver = value;
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
                File.WriteAllLines(SettingsPath, new[]
                {
                    "blockTrackers=" + _blockTrackers,
                    "dataSaver=" + _dataSaver,
                    "searchEngine=" + _searchEngine,
                    "lastUpdateCheckUtc=" + lastUpdateCheck
                });
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "设置无法保存：" + ex.Message;
            }
        }

        private List<BookmarkItem> ReadBookmarks()
        {
            var result = new List<BookmarkItem>();
            try
            {
                if (!File.Exists(BookmarksPath))
                    return result;
                foreach (var line in File.ReadAllLines(BookmarksPath))
                {
                    try
                    {
                        var parts = line.Split(new[] { '\t' }, 2);
                        Uri url;
                        if (parts.Length != 2 || !Uri.TryCreate(parts[1], UriKind.Absolute, out url) ||
                            (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                            continue;
                        result.Add(new BookmarkItem
                        {
                            Title = Uri.UnescapeDataString(parts[0]),
                            Url = parts[1]
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
                Directory.CreateDirectory(_dataRoot);
                File.WriteAllLines(BookmarksPath, bookmarks.Select(b => Uri.EscapeDataString(b.Title ?? String.Empty) + "\t" + b.Url));
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
            public Panel Header { get; set; }
            public WebView2 WebView { get; set; }
            public Task InitializationTask { get; set; }
        }

        private sealed class DownloadEntry
        {
            public CoreWebView2DownloadOperation Operation { get; set; }
            public string FilePath { get; set; }
            public BrowserTab Owner { get; set; }
        }

        private sealed class BookmarkItem
        {
            public string Title { get; set; }
            public string Url { get; set; }
            public override string ToString() { return Title; }
        }
    }
}
