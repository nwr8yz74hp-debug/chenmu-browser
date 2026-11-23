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
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, string lParam);

        private readonly Panel _header = new Panel();
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
        private FlowLayoutPanel _homeBookmarks;
        private TextBox _homeSearchBox;
        private Button _homeSearchEngineButton;
        private Label _privacyLabel;
        private WebView2 _webView;
        private Task _initializationTask;
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
            MinimumSize = new Size(900, 600);
            Size = new Size(1180, 780);
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            LoadPreferences();
            LoadBlocklist();
            BuildInterface();
            Shown += async delegate { await CheckForUpdatesAsync(false); };
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
            _header.BackColor = Color.FromArgb(248, 250, 253);
            _header.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(Color.FromArgb(226, 232, 239)))
                    e.Graphics.DrawLine(pen, 0, _header.Height - 1, _header.Width, _header.Height - 1);
            };

            var chrome = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 8,
                RowCount = 1,
                Padding = new Padding(8, 7, 8, 7),
                BackColor = _header.BackColor
            };
            for (var i = 0; i < 4; i++)
                chrome.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42F));
            chrome.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            for (var i = 0; i < 3; i++)
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
                Padding = new Padding(10, 3, 10, 3),
                BackColor = Color.White,
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
            _addressBox.Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
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
                Font = new Font("Segoe UI", 13F, FontStyle.Regular, GraphicsUnit.Point),
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
                UpdatePrivacyIndicator();
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
                UpdatePrivacyIndicator();
            };
            settingsMenu.Items.Add(saverItem);
            settingsMenu.Items.Add(new ToolStripSeparator());
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
            chrome.Controls.Add(settingsButton, 7, 0);
            _header.Controls.Add(chrome);

            _viewport.Dock = DockStyle.Fill;
            _viewport.BackColor = Color.FromArgb(245, 248, 252);
            _homeView = BuildHomeView();
            _viewport.Controls.Add(_homeView);

            _statusPanel.Dock = DockStyle.Bottom;
            _statusPanel.Height = 26;
            _statusPanel.BackColor = Color.FromArgb(248, 250, 253);
            _statusPanel.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(Color.FromArgb(226, 232, 239)))
                    e.Graphics.DrawLine(pen, 0, 0, _statusPanel.Width, 0);
            };
            _statusLabel.Dock = DockStyle.Fill;
            _statusLabel.Padding = new Padding(14, 2, 0, 0);
            _statusLabel.Text = "就绪";
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _statusLabel.ForeColor = Color.FromArgb(112, 124, 138);
            _statusLabel.Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
            _statusPanel.Controls.Add(_statusLabel);

            Controls.Add(_viewport);
            Controls.Add(_statusPanel);
            Controls.Add(_header);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
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
            var panel = new GradientPanel { Dock = DockStyle.Fill };
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(44, 6, 44, 10),
                BackColor = Color.Transparent
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 16F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 22F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 7F));

            var hero = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.Transparent
            };
            hero.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            hero.RowStyles.Add(new RowStyle(SizeType.Percent, 60F));
            hero.RowStyles.Add(new RowStyle(SizeType.Percent, 40F));
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
                Size = new Size(58, 58),
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 12, 0)
            };
            if (brandLogo.Image == null)
                brandLogo.Visible = false;
            var title = new Label
            {
                Text = "CHENMU Browser",
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 31F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(35, 55, 75),
                Margin = new Padding(0, 3, 0, 0)
            };
            brandRow.Controls.Add(brandLogo);
            brandRow.Controls.Add(title);
            var hint = new Label
            {
                Text = "简约、轻快、安心地浏览",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopCenter,
                Font = new Font("Segoe UI", 11F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(112, 126, 143)
            };
            hero.Controls.Add(brandRow, 0, 0);
            hero.Controls.Add(hint, 0, 1);
            layout.Controls.Add(hero, 0, 0);

            var searchFrame = new RoundedPanel
            {
                Width = 720,
                Height = 58,
                Anchor = AnchorStyles.None,
                Margin = new Padding(0),
                Padding = new Padding(4),
                BackColor = Color.White,
                BorderColor = Color.FromArgb(222, 229, 237),
                CornerRadius = 20
            };
            var searchLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 1,
                BackColor = Color.White,
                Padding = new Padding(4, 2, 4, 2)
            };
            searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120F));
            searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32F));
            searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82F));
            searchLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            _homeSearchEngineButton = new Button
            {
                Text = (_searchEngine == "google" ? "Google" : "Bing") + " ▾",
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(52, 103, 170),
                Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
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

            _homeSearchBox = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(48, 58, 70),
                Margin = new Padding(5, 8, 4, 4)
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
                Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point),
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
            _homeSearchBox.Leave += delegate { searchFrame.BorderColor = Color.FromArgb(222, 229, 237); };
            _toolTip.SetToolTip(homeSearchClearButton, "清空搜索内容");
            var searchButton = new RoundedButton
            {
                Text = "搜索",
                Dock = DockStyle.Fill,
                Margin = new Padding(2),
                CornerRadius = 16,
                BackColor = Color.FromArgb(48, 112, 190),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                TabStop = false,
                AccessibleName = "搜索"
            };
            searchButton.FlatAppearance.BorderSize = 0;
            searchButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(37, 96, 168);
            searchButton.Click += async delegate { await NavigateAsync(_homeSearchBox.Text); };
            searchLayout.Controls.Add(_homeSearchEngineButton, 0, 0);
            searchLayout.Controls.Add(_homeSearchBox, 1, 0);
            searchLayout.Controls.Add(homeSearchClearButton, 2, 0);
            searchLayout.Controls.Add(searchButton, 3, 0);
            searchFrame.Controls.Add(searchLayout);
            layout.Controls.Add(searchFrame, 0, 1);

            var shortcuts = new FlowLayoutPanel
            {
                Anchor = AnchorStyles.None,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            shortcuts.Controls.Add(CreateShortcutCard("G", "Google", Color.FromArgb(66, 133, 244), delegate { NavigateFromPage("https://www.google.com/"); }));
            shortcuts.Controls.Add(CreateShortcutCard("b", "Bing", Color.FromArgb(0, 120, 212), delegate { NavigateFromPage("https://www.bing.com/"); }));
            shortcuts.Controls.Add(CreateShortcutCard("百", "百度", Color.FromArgb(37, 91, 202), delegate { NavigateFromPage("https://www.baidu.com/"); }));
            shortcuts.Controls.Add(CreateShortcutCard("☆", "书签", Color.FromArgb(117, 97, 193), ShowBookmarksDialog));
            layout.Controls.Add(shortcuts, 0, 2);

            var bookmarkArea = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                BorderColor = Color.FromArgb(230, 235, 241),
                CornerRadius = 18,
                Padding = new Padding(20, 12, 20, 12),
                Margin = new Padding(60, 4, 60, 4)
            };
            var bookmarkHeading = new Label
            {
                Text = "常用收藏",
                Dock = DockStyle.Top,
                Height = 30,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(70, 83, 99),
                BackColor = Color.White
            };
            _homeBookmarks = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 8, 0, 0),
                BackColor = Color.White
            };
            bookmarkArea.Controls.Add(_homeBookmarks);
            bookmarkArea.Controls.Add(bookmarkHeading);
            layout.Controls.Add(bookmarkArea, 0, 3);

            _privacyLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(104, 119, 136),
                BackColor = Color.Transparent
            };
            layout.Controls.Add(_privacyLabel, 0, 4);
            panel.Controls.Add(layout);
            RefreshHomeBookmarks();
            UpdatePrivacyIndicator();
            return panel;
        }

        private Control CreateShortcutCard(string glyph, string title, Color accent, Action action)
        {
            var card = new RoundedPanel
            {
                Width = 112,
                Height = 94,
                Margin = new Padding(9, 4, 9, 4),
                BackColor = Color.White,
                BorderColor = Color.FromArgb(226, 232, 239),
                CornerRadius = 16,
                Cursor = Cursors.Hand
            };
            var badge = new RoundedPanel
            {
                Width = 38,
                Height = 38,
                CornerRadius = 19,
                BackColor = accent,
                BorderColor = Color.Transparent,
                Location = new Point((card.Width - 38) / 2, 12),
                Cursor = Cursors.Hand
            };
            var glyphLabel = new Label
            {
                Text = glyph,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 12F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            badge.Controls.Add(glyphLabel);
            var titleLabel = new Label
            {
                Text = title,
                Dock = DockStyle.Bottom,
                Height = 25,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(65, 78, 94),
                BackColor = Color.White,
                Cursor = Cursors.Hand
            };
            card.Controls.Add(titleLabel);
            card.Controls.Add(badge);
            card.MouseEnter += delegate
            {
                card.BackColor = Color.FromArgb(248, 251, 255);
                card.BorderColor = Color.FromArgb(173, 201, 231);
                titleLabel.BackColor = card.BackColor;
            };
            card.MouseLeave += delegate
            {
                var point = card.PointToClient(Cursor.Position);
                if (card.ClientRectangle.Contains(point))
                    return;
                card.BackColor = Color.White;
                card.BorderColor = Color.FromArgb(226, 232, 239);
                titleLabel.BackColor = Color.White;
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

        private void UpdatePrivacyIndicator()
        {
            if (_privacyLabel == null)
                return;
            var trackerText = _blockTrackers ? "跟踪拦截开启" : "跟踪拦截关闭";
            var saverText = _dataSaver ? "省流模式开启" : "省流模式关闭";
            _privacyLabel.Text = "Ctrl+L 地址栏  ·  Ctrl+D 收藏  ·  HTTPS 优先    " + trackerText + "    " + saverText + "    密码保存关闭";
        }

        private async void SearchFromHome()
        {
            await NavigateAsync(_homeSearchBox.Text);
        }

        private void RefreshHomeBookmarks()
        {
            if (_homeBookmarks == null)
                return;

            _homeBookmarks.Controls.Clear();
            var bookmarks = ReadBookmarks().Take(12).ToList();
            if (bookmarks.Count == 0)
            {
                var emptyState = new RoundedPanel
                {
                    Width = 430,
                    Height = 62,
                    Margin = new Padding(0, 4, 0, 0),
                    Padding = new Padding(10, 6, 10, 6),
                    BackColor = Color.FromArgb(248, 250, 253),
                    BorderColor = Color.FromArgb(231, 236, 243),
                    CornerRadius = 14
                };
                var emptyIcon = new Label
                {
                    Text = "☆",
                    Dock = DockStyle.Left,
                    Width = 44,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI Symbol", 18F, FontStyle.Regular, GraphicsUnit.Point),
                    ForeColor = Color.FromArgb(74, 126, 187),
                    BackColor = Color.Transparent
                };
                var emptyCopy = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 1,
                    RowCount = 2,
                    BackColor = Color.Transparent,
                    Padding = new Padding(2, 2, 0, 0)
                };
                emptyCopy.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
                emptyCopy.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
                emptyCopy.Controls.Add(new Label
                {
                    Text = "收藏常用网页",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.BottomLeft,
                    Font = new Font("Segoe UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point),
                    ForeColor = Color.FromArgb(70, 83, 99),
                    BackColor = Color.Transparent
                }, 0, 0);
                emptyCopy.Controls.Add(new Label
                {
                    Text = "收藏后会显示在这里；按 Ctrl+D 可快速保存当前页面。",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.TopLeft,
                    Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
                    ForeColor = Color.FromArgb(126, 139, 153),
                    BackColor = Color.Transparent
                }, 0, 1);
                emptyState.Controls.Add(emptyCopy);
                emptyState.Controls.Add(emptyIcon);
                _homeBookmarks.Controls.Add(emptyState);
                return;
            }

            foreach (var bookmark in bookmarks)
                _homeBookmarks.Controls.Add(CreateBookmarkTile(bookmark));
        }

        private Control CreateBookmarkTile(BookmarkItem bookmark)
        {
            var tile = new RoundedPanel
            {
                Width = 232,
                Height = 58,
                Margin = new Padding(0, 4, 10, 4),
                Padding = new Padding(6, 4, 6, 4),
                BackColor = Color.FromArgb(249, 251, 254),
                BorderColor = Color.FromArgb(230, 236, 243),
                CornerRadius = 13,
                Cursor = Cursors.Hand
            };
            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                BackColor = Color.Transparent,
                Padding = new Padding(0)
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36F));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 55F));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 45F));
            var mark = new Label
            {
                Text = "↗",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Symbol", 13F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(64, 119, 181),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            content.Controls.Add(mark, 0, 0);
            content.SetRowSpan(mark, 2);
            var title = new Label
            {
                Text = bookmark.Title,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.BottomLeft,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(54, 69, 85),
                BackColor = Color.Transparent,
                AutoEllipsis = true,
                Cursor = Cursors.Hand
            };
            var host = new Label
            {
                Text = GetBookmarkHost(bookmark.Url),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft,
                Font = new Font("Segoe UI", 8F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(129, 143, 158),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            content.Controls.Add(title, 1, 0);
            content.Controls.Add(host, 1, 1);
            tile.Controls.Add(content);
            EventHandler openBookmark = async delegate { await NavigateAsync(bookmark.Url); };
            tile.Click += openBookmark;
            content.Click += openBookmark;
            mark.Click += openBookmark;
            title.Click += openBookmark;
            host.Click += openBookmark;
            tile.MouseEnter += delegate
            {
                tile.BackColor = Color.FromArgb(243, 248, 254);
                tile.BorderColor = Color.FromArgb(174, 203, 232);
            };
            tile.MouseLeave += delegate
            {
                var point = tile.PointToClient(Cursor.Position);
                if (tile.ClientRectangle.Contains(point))
                    return;
                tile.BackColor = Color.FromArgb(249, 251, 254);
                tile.BorderColor = Color.FromArgb(230, 236, 243);
            };
            _toolTip.SetToolTip(tile, bookmark.Url);
            return tile;
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

            _addressBox.Text = target;
            try
            {
                await EnsureWebViewAsync();
            }
            catch (Exception ex)
            {
                ShowRuntimeProblem(ex, target);
                return;
            }

            if (IsDisposed || _webView == null || _webView.CoreWebView2 == null)
                return;

            HideErrorView();
            _homeView.Visible = false;
            _webView.Visible = true;
            _webView.BringToFront();
            _statusLabel.Text = "正在打开页面…";
            try
            {
                _webView.CoreWebView2.Navigate(target);
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "无法打开页面：" + ex.Message;
            }
        }

        private Task EnsureWebViewAsync()
        {
            if (_webView != null && _webView.CoreWebView2 != null)
                return Task.CompletedTask;
            if (_initializationTask == null)
                _initializationTask = InitializeWebViewAsync();
            return _initializationTask;
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                Directory.CreateDirectory(_dataRoot);
                var userDataFolder = Path.Combine(_dataRoot, "WebView2");
                _webView = new WebView2 { Dock = DockStyle.Fill, Visible = false };
                _viewport.Controls.Add(_webView);
                _webView.BringToFront();

                var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
                await _webView.EnsureCoreWebView2Async(environment);
                ConfigureCoreWebView(_webView.CoreWebView2);
            }
            catch
            {
                if (_webView != null)
                {
                    _viewport.Controls.Remove(_webView);
                    _webView.Dispose();
                    _webView = null;
                }
                throw;
            }
            finally
            {
                _initializationTask = null;
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
            core.NavigationStarting += Core_NavigationStarting;
            core.SourceChanged += delegate
            {
                if (core.Source != null)
                    _addressBox.Text = core.Source;
            };
            core.NavigationCompleted += delegate(object sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                _statusLabel.Text = e.IsSuccess ? "完成" : "页面加载失败；可检查网址或网络连接。";
                UpdateWindowTitle(core);
            };
            core.DocumentTitleChanged += delegate { UpdateWindowTitle(core); };
            core.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e)
            {
                e.Handled = true;
                NavigateFromPage(e.Uri);
            };
            core.ProcessFailed += delegate
            {
                _statusLabel.Text = "网页内核遇到问题，可尝试刷新页面。";
            };
        }

        private void Core_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            Uri uri;
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out uri))
                return;

            if (String.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                var secureUrl = "https:" + e.Uri.Substring("http:".Length);
                _addressBox.Text = secureUrl;
                _webView.CoreWebView2.Navigate(secureUrl);
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
                var core = _webView == null ? null : _webView.CoreWebView2;
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
            HideErrorView();
            if (_webView != null)
                _webView.Visible = false;
            _homeView.Visible = true;
            _homeView.BringToFront();
            _addressBox.Text = String.Empty;
            _statusLabel.Text = "就绪";
            Text = "CHENMU Browser";
            RefreshHomeBookmarks();
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
            WriteBookmarks(bookmarks);
            RefreshHomeBookmarks();
            _statusLabel.Text = "已收藏：" + title;
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
                WriteBookmarks(items);
                var current = list.SelectedIndex;
                list.Items.Remove(selected);
                if (list.Items.Count > 0)
                    list.SelectedIndex = Math.Min(current, list.Items.Count - 1);
                RefreshHomeBookmarks();
            };
            dialog.ShowDialog(this);
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
                if (_webView != null && _webView.CoreWebView2 != null)
                {
                    await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
                    _webView.CoreWebView2.CookieManager.DeleteAllCookies();
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
            var title = core.DocumentTitle;
            Text = String.IsNullOrWhiteSpace(title) ? "CHENMU Browser" : title + " - CHENMU Browser";
        }

        private void ShowRuntimeProblem(Exception ex, string attemptedUrl)
        {
            if (_webView != null)
            {
                _viewport.Controls.Remove(_webView);
                _webView.Dispose();
                _webView = null;
            }
            HideErrorView();
            _homeView.Visible = false;

            var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(34) };
            var heading = new Label
            {
                Text = "暂时无法启动网页内核",
                Dock = DockStyle.Top,
                Height = 42,
                Font = new Font("Segoe UI", 18F, FontStyle.Bold, GraphicsUnit.Point),
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

        private void WriteBookmarks(List<BookmarkItem> bookmarks)
        {
            try
            {
                Directory.CreateDirectory(_dataRoot);
                File.WriteAllLines(BookmarksPath, bookmarks.Select(b => Uri.EscapeDataString(b.Title ?? String.Empty) + "\t" + b.Url));
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "书签无法保存：" + ex.Message;
            }
        }

        private sealed class BookmarkItem
        {
            public string Title { get; set; }
            public string Url { get; set; }
            public override string ToString() { return Title; }
        }
    }
}
