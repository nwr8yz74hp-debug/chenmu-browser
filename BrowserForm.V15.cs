using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ChenmuBrowser
{
    internal sealed partial class BrowserForm
    {
        private readonly RecentlyClosedTabs _recentlyClosedTabs = new RecentlyClosedTabs();
        private ContextMenuStrip _settingsMenu;
        private ToolStripMenuItem _recentlyClosedMenu;
        private Button _backButton;
        private Button _forwardButton;
        private Form _findForm;
        private TextBox _findTextBox;
        private CoreWebView2 _findCore;

        private static bool IsKeyDiagnosticsEnabled()
        {
            return String.Equals(Environment.GetEnvironmentVariable("CHENMU_WEBVIEW_SMOKE_KEY_DIAGNOSTICS"), "1", StringComparison.Ordinal);
        }

        private static void LogKeyDiagnostic(string message)
        {
            if (IsKeyDiagnosticsEnabled())
                Console.WriteLine("[KEYDIAG] " + message);
        }

        private void InitializeV15Features()
        {
            if (_settingsMenu == null)
                return;

            _settingsMenu.Items.Add(new ToolStripSeparator());
            var pageTools = new ToolStripMenuItem("页面工具");
            pageTools.DropDownItems.Add("在页面中查找…（Ctrl+F）", null, delegate { ShowFindBar(); });

            var zoomMenu = new ToolStripMenuItem("页面缩放");
            zoomMenu.DropDownItems.Add("放大（Ctrl++）", null, delegate { AdjustActiveTabZoom(1); });
            zoomMenu.DropDownItems.Add("缩小（Ctrl+-）", null, delegate { AdjustActiveTabZoom(-1); });
            zoomMenu.DropDownItems.Add("恢复 100%（Ctrl+0）", null, delegate { SetActiveTabZoom(BrowserZoomPolicy.Default); });
            pageTools.DropDownItems.Add(zoomMenu);

            _recentlyClosedMenu = new ToolStripMenuItem("最近关闭的标签（Ctrl+Shift+T）");
            _recentlyClosedMenu.DropDownOpening += delegate { PopulateRecentlyClosedMenu(); };
            pageTools.DropDownItems.Add(_recentlyClosedMenu);
            pageTools.DropDownItems.Add(new ToolStripSeparator());
            pageTools.DropDownItems.Add("生成当前网址二维码…", null, delegate { ShowCurrentPageQr(); });
            pageTools.DropDownItems.Add("将网页保存为 PDF…", null, async (sender, args) => { await SaveCurrentPageAsPdfAsync(); });
            pageTools.DropDownItems.Add("截图当前可见网页为 PNG…", null, async (sender, args) => { await CaptureCurrentPageAsPngAsync(); });
            _settingsMenu.Items.Add(pageTools);
            _settingsMenu.Items.Add("管理浏览器扩展…", null, delegate { ShowExtensionManager(); });
        }

        private void UpdateNavigationControls(CoreWebView2 eventCore = null)
        {
            if (_backButton == null || _forwardButton == null || IsDisposed || Disposing)
                return;
            var core = _activeTab == null || _activeTab.WebView == null
                ? null
                : _activeTab.WebView.CoreWebView2;
            if (eventCore != null && !Object.ReferenceEquals(eventCore, core))
                return;
            _backButton.Enabled = core != null && core.CanGoBack;
            _forwardButton.Enabled = core != null && core.CanGoForward;
        }

        private static bool IsZoomInShortcut(Keys keyData)
        {
            return keyData == (Keys.Control | Keys.Add) ||
                keyData == (Keys.Control | Keys.Oemplus) ||
                keyData == (Keys.Control | Keys.Shift | Keys.Oemplus);
        }

        private static bool IsZoomOutShortcut(Keys keyData)
        {
            return keyData == (Keys.Control | Keys.Subtract) ||
                keyData == (Keys.Control | Keys.OemMinus);
        }

        private static bool IsZoomResetShortcut(Keys keyData)
        {
            return keyData == (Keys.Control | Keys.D0) ||
                keyData == (Keys.Control | Keys.NumPad0);
        }

        private static bool IsPageToolShortcut(Keys keyData)
        {
            return keyData == (Keys.Control | Keys.F) ||
                keyData == (Keys.Control | Keys.Shift | Keys.T) ||
                keyData == (Keys.Alt | Keys.Left) || keyData == (Keys.Alt | Keys.Right) ||
                IsZoomInShortcut(keyData) || IsZoomOutShortcut(keyData) || IsZoomResetShortcut(keyData);
        }

        private bool HandlePageToolShortcut(Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F)) ShowFindBar();
            else if (keyData == (Keys.Control | Keys.Shift | Keys.T)) RestoreMostRecentlyClosedTab();
            else if (IsZoomInShortcut(keyData)) AdjustActiveTabZoom(1);
            else if (IsZoomOutShortcut(keyData)) AdjustActiveTabZoom(-1);
            else if (IsZoomResetShortcut(keyData)) SetActiveTabZoom(BrowserZoomPolicy.Default);
            else if (keyData == (Keys.Alt | Keys.Left)) GoBack();
            else if (keyData == (Keys.Alt | Keys.Right)) GoForward();
            else return false;
            return true;
        }

        private void WebView_KeyDown(object sender, KeyEventArgs e)
        {
            var view = sender as WebView2;
            if (_activeTab == null || !Object.ReferenceEquals(_activeTab.WebView, view))
            {
                LogKeyDiagnostic("WebView_KeyDown ignored: senderIsActive=" + Object.ReferenceEquals(_activeTab == null ? null : _activeTab.WebView, view));
                return;
            }
            var keyData = e.KeyData;
            if (!IsPageToolShortcut(keyData))
                return;
            LogKeyDiagnostic("WebView_KeyDown received keyData=" + keyData + ", viewContainsFocus=" + view.ContainsFocus +
                ", viewFocused=" + view.Focused + ", viewHandle=" + (view.IsHandleCreated ? view.Handle.ToString() : "<none>"));
            e.Handled = true;
            e.SuppressKeyPress = true;
            var tab = _activeTab;
            BeginInvoke((MethodInvoker)delegate
            {
                LogKeyDiagnostic("BeginInvoke ran keyData=" + keyData + ", tabStillActive=" + Object.ReferenceEquals(tab, _activeTab) +
                    ", disposed=" + IsDisposed + ", disposing=" + Disposing);
                if (!IsDisposed && !Disposing && Object.ReferenceEquals(tab, _activeTab))
                {
                    LogKeyDiagnostic("BeginInvoke dispatching HandlePageToolShortcut keyData=" + keyData);
                    HandlePageToolShortcut(keyData);
                }
            });
        }

        private void ApplyTabZoom(CoreWebView2 core)
        {
            var tab = FindTabForCore(core);
            if (tab == null || tab.WebView == null)
                return;
            tab.ZoomFactor = BrowserZoomPolicy.Normalize(tab.ZoomFactor);
            tab.WebView.ZoomFactor = tab.ZoomFactor;
        }

        private void AdjustActiveTabZoom(int steps)
        {
            if (_activeTab == null || _activeTab.IsReader)
            {
                _statusLabel.Text = "请先切换到普通网页标签再调整缩放。";
                return;
            }
            SetActiveTabZoom(BrowserZoomPolicy.Adjust(_activeTab.ZoomFactor, steps));
        }

        private void SetActiveTabZoom(double zoomFactor)
        {
            if (_activeTab == null || _activeTab.IsReader)
            {
                _statusLabel.Text = "请先切换到普通网页标签再调整缩放。";
                return;
            }
            var tab = _activeTab;
            tab.ZoomFactor = BrowserZoomPolicy.Normalize(zoomFactor);
            if (tab.WebView != null && tab.WebView.CoreWebView2 != null)
                tab.WebView.ZoomFactor = tab.ZoomFactor;
            _statusLabel.Text = "当前标签缩放：" + Math.Round(tab.ZoomFactor * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
        }

        private void PopulateRecentlyClosedMenu()
        {
            if (_recentlyClosedMenu == null)
                return;
            _recentlyClosedMenu.DropDownItems.Clear();
            var snapshots = _recentlyClosedTabs.GetItems();
            if (snapshots.Count == 0)
            {
                _recentlyClosedMenu.DropDownItems.Add(new ToolStripMenuItem("没有最近关闭的普通网页") { Enabled = false });
                return;
            }
            for (var index = 0; index < snapshots.Count; index++)
            {
                var itemIndex = index;
                var snapshot = snapshots[index];
                var title = String.IsNullOrWhiteSpace(snapshot.Title) ? snapshot.Url : snapshot.Title;
                if (title.Length > 48)
                    title = title.Substring(0, 45) + "…";
                var item = new ToolStripMenuItem(title)
                {
                    ToolTipText = snapshot.Url + " · 缩放 " + Math.Round(snapshot.ZoomFactor * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%"
                };
                item.Click += delegate { RestoreClosedTabAt(itemIndex); };
                _recentlyClosedMenu.DropDownItems.Add(item);
            }
        }

        private void RestoreMostRecentlyClosedTab()
        {
            RestoreClosedTabAt(0);
        }

        private async void RestoreClosedTabAt(int index)
        {
            var snapshot = _recentlyClosedTabs.PopAt(index);
            if (snapshot == null)
            {
                _statusLabel.Text = "没有可恢复的最近关闭标签。";
                return;
            }
            CreateNewTab();
            if (_activeTab != null)
                _activeTab.ZoomFactor = BrowserZoomPolicy.Normalize(snapshot.ZoomFactor);
            try
            {
                await NavigateAsync(snapshot.Url);
                if (_activeTab != null && _activeTab.WebView != null && _activeTab.WebView.CoreWebView2 != null)
                    _activeTab.WebView.ZoomFactor = _activeTab.ZoomFactor;
                _statusLabel.Text = "已恢复最近关闭的普通网页。";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "恢复最近关闭的标签失败：" + ex.Message;
            }
        }

        private CoreWebView2 GetCurrentPageCore()
        {
            if (_activeTab == null || _activeTab.IsReader || _activeTab.WebView == null || _activeTab.WebView.CoreWebView2 == null)
                return null;
            var core = _activeTab.WebView.CoreWebView2;
            Uri uri;
            return Uri.TryCreate(core.Source, UriKind.Absolute, out uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? core
                : null;
        }

        private void ShowFindBar()
        {
            var core = GetCurrentPageCore();
            LogKeyDiagnostic("ShowFindBar entered: currentCore=" + (core != null) + ", findFormExists=" + (_findForm != null) +
                ", findTextBoxExists=" + (_findTextBox != null) + ", source=" + (core == null ? "<none>" : core.Source));
            if (core == null)
            {
                LogKeyDiagnostic("ShowFindBar returned because no eligible HTTP(S) page is active.");
                _statusLabel.Text = "请先打开普通网页再使用页内查找。";
                return;
            }
            if (_findForm != null && !_findForm.IsDisposed && Object.ReferenceEquals(_findCore, core))
            {
                LogKeyDiagnostic("ShowFindBar reusing existing form; textBoxExists=" + (_findTextBox != null));
                _findForm.Activate();
                if (_findTextBox != null)
                    _findTextBox.Focus();
                return;
            }
            if (_findForm != null && !_findForm.IsDisposed)
                _findForm.Close();

            var form = new Form
            {
                Text = "页内查找",
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(422, 56)
            };
            var layout = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(8, 10, 6, 6),
                BackColor = Color.FromArgb(249, 251, 255)
            };
            var textBox = new TextBox { Width = 205, Height = 26, Margin = new Padding(0, 2, 6, 0) };
            var previous = new Button { Text = "上一个", Width = 62, Height = 30, Margin = new Padding(0, 0, 4, 0) };
            var next = new Button { Text = "下一个", Width = 62, Height = 30, Margin = new Padding(0, 0, 4, 0) };
            var close = new Button { Text = "关闭", Width = 52, Height = 30, Margin = new Padding(0) };
            layout.Controls.Add(textBox);
            layout.Controls.Add(previous);
            layout.Controls.Add(next);
            layout.Controls.Add(close);
            form.Controls.Add(layout);
            _findForm = form;
            _findTextBox = textBox;
            _findCore = core;
            LogKeyDiagnostic("ShowFindBar assigned fields: formHandleCreated=" + form.IsHandleCreated +
                ", textBoxHandleCreated=" + textBox.IsHandleCreated + ", findFormFieldMatches=" + Object.ReferenceEquals(_findForm, form) +
                ", findTextBoxFieldMatches=" + Object.ReferenceEquals(_findTextBox, textBox));

            textBox.TextChanged += async delegate { await StartFindAsync(); };
            textBox.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode == Keys.Enter)
                {
                    args.SuppressKeyPress = true;
                    if (args.Shift)
                        FindPrevious();
                    else
                        FindNext();
                }
                else if (args.KeyCode == Keys.Escape)
                {
                    form.Close();
                }
            };
            previous.Click += delegate { FindPrevious(); };
            next.Click += delegate { FindNext(); };
            close.Click += delegate { form.Close(); };
            form.FormClosed += delegate
            {
                try { if (_findCore != null) _findCore.Find.Stop(); }
                catch { }
                _findForm = null;
                _findTextBox = null;
                _findCore = null;
                if (!IsDisposed && _activeTab != null && _activeTab.WebView != null)
                    _activeTab.WebView.Focus();
            };
            ApplyDialogStyle(form);
            form.Show(this);
            var screen = Screen.FromControl(this).WorkingArea;
            form.Location = new Point(Math.Max(screen.Left, Right - form.Width - 24), Math.Max(screen.Top, Top + 70));
            textBox.Focus();
            LogKeyDiagnostic("ShowFindBar finished: formVisible=" + form.Visible + ", formHandleCreated=" + form.IsHandleCreated +
                ", textBoxFocused=" + textBox.Focused + ", textBoxHandleCreated=" + textBox.IsHandleCreated +
                ", findTextBoxFieldMatches=" + Object.ReferenceEquals(_findTextBox, textBox));
        }

        private async Task StartFindAsync()
        {
            var core = _findCore;
            if (core == null || _findTextBox == null || String.IsNullOrWhiteSpace(_findTextBox.Text))
            {
                try { if (core != null) core.Find.Stop(); }
                catch { }
                return;
            }
            try
            {
                var options = core.Environment.CreateFindOptions();
                options.FindTerm = _findTextBox.Text;
                options.IsCaseSensitive = false;
                options.ShouldHighlightAllMatches = true;
                options.SuppressDefaultFindDialog = true;
                await core.Find.StartAsync(options);
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                    _statusLabel.Text = "页内查找暂时不可用：" + ex.Message;
            }
        }

        private void FindNext()
        {
            try { if (_findCore != null && _findTextBox != null && !String.IsNullOrWhiteSpace(_findTextBox.Text)) _findCore.Find.FindNext(); }
            catch (Exception ex) { _statusLabel.Text = "页内查找暂时不可用：" + ex.Message; }
        }

        private void FindPrevious()
        {
            try { if (_findCore != null && _findTextBox != null && !String.IsNullOrWhiteSpace(_findTextBox.Text)) _findCore.Find.FindPrevious(); }
            catch (Exception ex) { _statusLabel.Text = "页内查找暂时不可用：" + ex.Message; }
        }

        private void ShowCurrentPageQr()
        {
            var core = GetCurrentPageCore();
            if (core == null)
            {
                _statusLabel.Text = "请先打开普通网页再生成网址二维码。";
                return;
            }
            try
            {
                var bytes = QrCodeService.CreatePng(core.Source);
                using (var stream = new MemoryStream(bytes))
                using (var source = Image.FromStream(stream))
                using (var form = new Form
                {
                    Text = "当前网址二维码",
                    StartPosition = FormStartPosition.CenterParent,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    ClientSize = new Size(340, 390),
                    MinimizeBox = false,
                    MaximizeBox = false
                })
                {
                    var picture = new PictureBox
                    {
                        Image = (Image)source.Clone(),
                        SizeMode = PictureBoxSizeMode.Zoom,
                        Width = 260,
                        Height = 260,
                        Location = new Point(40, 18),
                        BackColor = Color.White
                    };
                    var url = new Label
                    {
                        Text = core.Source,
                        Location = new Point(18, 290),
                        Size = new Size(304, 58),
                        TextAlign = ContentAlignment.MiddleCenter,
                        AutoEllipsis = true,
                        ForeColor = Color.FromArgb(76, 90, 106)
                    };
                    var close = new Button
                    {
                        Text = "关闭",
                        Width = 90,
                        Height = 32,
                        Location = new Point(125, 350),
                        DialogResult = DialogResult.OK
                    };
                    form.Controls.Add(picture);
                    form.Controls.Add(url);
                    form.Controls.Add(close);
                    form.AcceptButton = close;
                    form.FormClosed += delegate { picture.Image.Dispose(); };
                    ApplyDialogStyle(form);
                    form.ShowDialog(this);
                }
                _statusLabel.Text = "已在本机生成当前网址二维码。";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "无法生成当前网址二维码：" + ex.Message;
            }
        }

        private async Task SaveCurrentPageAsPdfAsync()
        {
            var core = GetCurrentPageCore();
            if (core == null)
            {
                _statusLabel.Text = "请先打开普通网页再保存为 PDF。";
                return;
            }
            using (var picker = new SaveFileDialog
            {
                Title = "将网页保存为 PDF",
                Filter = "PDF 文件 (*.pdf)|*.pdf",
                DefaultExt = "pdf",
                AddExtension = true,
                FileName = GetSuggestedPageFileName(core, ".pdf"),
                OverwritePrompt = true
            })
            {
                if (picker.ShowDialog(this) != DialogResult.OK)
                    return;
                try
                {
                    _statusLabel.Text = "正在将网页保存为 PDF…";
                    var saved = await core.PrintToPdfAsync(picker.FileName, null);
                    if (!saved || !File.Exists(picker.FileName))
                        throw new IOException("WebView2 未能完成 PDF 输出。");
                    _statusLabel.Text = "网页已保存为 PDF：" + Path.GetFileName(picker.FileName);
                }
                catch (Exception ex)
                {
                    _statusLabel.Text = "无法保存网页 PDF：" + ex.Message;
                    MessageBox.Show(this, "无法将网页保存为 PDF：\r\n" + ex.Message, "保存 PDF",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private async Task CaptureCurrentPageAsPngAsync()
        {
            var core = GetCurrentPageCore();
            if (core == null)
            {
                _statusLabel.Text = "请先打开普通网页再截图。";
                return;
            }
            using (var picker = new SaveFileDialog
            {
                Title = "截图当前可见网页",
                Filter = "PNG 图片 (*.png)|*.png",
                DefaultExt = "png",
                AddExtension = true,
                FileName = GetSuggestedPageFileName(core, ".png"),
                OverwritePrompt = true
            })
            {
                if (picker.ShowDialog(this) != DialogResult.OK)
                    return;
                try
                {
                    _statusLabel.Text = "正在截图当前可见网页…";
                    byte[] imageBytes;
                    using (var stream = new MemoryStream())
                    {
                        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                        imageBytes = stream.ToArray();
                    }
                    if (imageBytes.Length < 8 || imageBytes[0] != 0x89 || imageBytes[1] != 0x50 || imageBytes[2] != 0x4E || imageBytes[3] != 0x47)
                        throw new InvalidDataException("网页截图没有生成有效的 PNG 数据。");
                    File.WriteAllBytes(picker.FileName, imageBytes);
                    _statusLabel.Text = "当前可见网页已截图为 PNG：" + Path.GetFileName(picker.FileName);
                }
                catch (Exception ex)
                {
                    _statusLabel.Text = "无法截图当前网页：" + ex.Message;
                    MessageBox.Show(this, "无法截图当前可见网页：\r\n" + ex.Message, "保存 PNG 截图",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private static string GetSuggestedPageFileName(CoreWebView2 core, string extension)
        {
            var value = String.IsNullOrWhiteSpace(core.DocumentTitle) ? "网页" : core.DocumentTitle.Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');
            if (String.IsNullOrWhiteSpace(value))
                value = "网页";
            if (value.Length > 80)
                value = value.Substring(0, 80);
            return value + extension;
        }

        private async void ShowExtensionManager()
        {
            CoreWebView2Profile profile;
            try
            {
                profile = await GetExtensionManagerProfileAsync();
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "无法启动扩展管理：" + ex.Message;
                MessageBox.Show(this, "无法启动 WebView2 扩展：\r\n" + ex.Message, "浏览器扩展",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (profile == null || IsDisposed || Disposing)
                return;

            using (var dialog = new Form
            {
                Text = "管理浏览器扩展",
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(660, 400),
                MinimumSize = new Size(560, 330),
                FormBorderStyle = FormBorderStyle.Sizable,
                MinimizeBox = false,
                MaximizeBox = false
            })
            {
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68F));
                var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
                var install = new Button { Text = "安装已解压扩展…", AutoSize = true, Height = 32 };
                var toggle = new Button { Text = "启用/停用", AutoSize = true, Height = 32, Enabled = false };
                var remove = new Button { Text = "卸载", AutoSize = true, Height = 32, Enabled = false };
                var refresh = new Button { Text = "刷新", AutoSize = true, Height = 32 };
                var close = new Button { Text = "关闭", AutoSize = true, Height = 32, DialogResult = DialogResult.OK };
                buttons.Controls.Add(install);
                buttons.Controls.Add(toggle);
                buttons.Controls.Add(remove);
                buttons.Controls.Add(refresh);
                buttons.Controls.Add(close);
                var warning = new Label
                {
                    Dock = DockStyle.Fill,
                    Text = "仅支持含 manifest.json 的未打包扩展目录；不支持商店安装或 .crx。扩展与标签共用浏览器配置，可能读取或更改网页数据；请只安装信任的扩展并仔细检查其权限。",
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Color.FromArgb(95, 78, 47)
                };
                layout.Controls.Add(list, 0, 0);
                layout.Controls.Add(buttons, 0, 1);
                layout.Controls.Add(warning, 0, 2);
                dialog.Controls.Add(layout);
                dialog.AcceptButton = close;

                Action updateButtons = delegate
                {
                    var selected = list.SelectedItem as ExtensionListItem;
                    toggle.Enabled = selected != null;
                    remove.Enabled = selected != null;
                    if (selected != null)
                        toggle.Text = selected.Extension.IsEnabled ? "停用" : "启用";
                };
                Func<Task> refreshList = async delegate
                {
                    try
                    {
                        var extensions = await profile.GetBrowserExtensionsAsync();
                        list.BeginUpdate();
                        list.Items.Clear();
                        foreach (var extension in extensions)
                            list.Items.Add(new ExtensionListItem(extension));
                        list.EndUpdate();
                        updateButtons();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(dialog, "无法读取扩展列表：\r\n" + ex.Message, "浏览器扩展",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };
                list.SelectedIndexChanged += delegate { updateButtons(); };
                refresh.Click += async delegate { await refreshList(); };
                install.Click += async delegate { await InstallExtensionFromDialogAsync(profile, dialog, refreshList); };
                toggle.Click += async delegate
                {
                    var selected = list.SelectedItem as ExtensionListItem;
                    if (selected == null)
                        return;
                    try
                    {
                        await selected.Extension.EnableAsync(!selected.Extension.IsEnabled);
                        await refreshList();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(dialog, "无法更改扩展状态：\r\n" + ex.Message, "浏览器扩展",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };
                remove.Click += async delegate
                {
                    var selected = list.SelectedItem as ExtensionListItem;
                    if (selected == null)
                        return;
                    if (MessageBox.Show(dialog, "确定卸载扩展“" + selected.Extension.Name + "”吗？",
                        "卸载浏览器扩展", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                        return;
                    try
                    {
                        await selected.Extension.RemoveAsync();
                        await refreshList();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(dialog, "无法卸载扩展：\r\n" + ex.Message, "浏览器扩展",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };
                ApplyDialogStyle(dialog);
                await refreshList();
                dialog.ShowDialog(this);
            }
        }

        private async Task<CoreWebView2Profile> GetExtensionManagerProfileAsync()
        {
            var tab = _tabs.FirstOrDefault(item => !item.IsReader);
            if (tab == null)
            {
                CreateNewTab();
                tab = _activeTab;
            }
            if (tab == null)
                return null;
            await EnsureWebViewAsync(tab);
            return tab.WebView == null || tab.WebView.CoreWebView2 == null
                ? null
                : tab.WebView.CoreWebView2.Profile;
        }

        private async Task InstallExtensionFromDialogAsync(CoreWebView2Profile profile, IWin32Window owner, Func<Task> refreshList)
        {
            using (var picker = new FolderBrowserDialog { Description = "选择包含 manifest.json 的已解压扩展目录" })
            {
                if (picker.ShowDialog(owner) != DialogResult.OK)
                    return;
                try
                {
                    var manifest = ExtensionManifestPolicy.Read(picker.SelectedPath);
                    var permissions = manifest.Permissions.Count == 0
                        ? "未列出常见权限字段；这不代表扩展没有网页访问能力。"
                        : String.Join("\r\n", manifest.Permissions.Select(permission => "• " + permission));
                    var warning = "准备安装扩展：" + manifest.Name + "（Manifest V" + manifest.ManifestVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + "）\r\n\r\n" +
                        "请求的权限：\r\n" + permissions + "\r\n\r\n" +
                        "扩展文件由你选择，本地检查不能证明其行为安全。扩展可能读取或修改网页内容、访问站点数据或在后台运行；请只安装你信任的扩展。安装后会作用于共用浏览器配置中的标签页。继续安装？";
                    if (MessageBox.Show(owner, warning, "扩展权限与网页数据风险", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                        return;

                    await InstallExtensionDirectoryAsync(profile, picker.SelectedPath);
                    if (refreshList != null)
                        await refreshList();
                    _statusLabel.Text = "扩展已安装；请检查其权限并仅在信任时启用。";
                }
                catch (Exception ex)
                {
                    MessageBox.Show(owner, "无法安装所选扩展：\r\n" + ex.Message, "浏览器扩展",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private async Task<CoreWebView2BrowserExtension> InstallExtensionDirectoryAsync(CoreWebView2Profile profile, string directory)
        {
            if (profile == null)
                throw new InvalidOperationException("浏览器扩展配置尚未就绪。");
            ExtensionManifestPolicy.Read(directory);
            return await profile.AddBrowserExtensionAsync(Path.GetFullPath(directory));
        }

        private sealed class ExtensionListItem
        {
            public CoreWebView2BrowserExtension Extension { get; private set; }
            public ExtensionListItem(CoreWebView2BrowserExtension extension) { Extension = extension; }
            public override string ToString()
            {
                return Extension.Name + "  ·  " + (Extension.IsEnabled ? "已启用" : "已停用") + "  ·  " + Extension.Id;
            }
        }
    }
}
