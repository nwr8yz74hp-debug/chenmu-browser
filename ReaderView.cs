using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ChenmuBrowser
{
    internal sealed class ReaderView : UserControl
    {
        private readonly Func<Task<CoreWebView2Environment>> _getEnvironment;
        private readonly WebView2 _view = new WebView2();
        private readonly Label _fileLabel = new Label();
        private readonly Label _pageLabel = new Label();
        private readonly Button _openButton = new Button();
        private readonly Button _previousButton = new Button();
        private readonly Button _nextButton = new Button();
        private readonly Panel _header = new Panel();
        private readonly Panel _rule = new Panel();
        private ReaderDocument _document;
        private string _activeTemporaryRoot;
        private string _allowedFileRoot;
        private bool _webViewReady;
        private bool _loading;
        private bool _initializing;
        private int _pageIndex;

        public ReaderView(Func<Task<CoreWebView2Environment>> getEnvironment)
        {
            _getEnvironment = getEnvironment ?? throw new ArgumentNullException(nameof(getEnvironment));
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(246, 248, 251);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            BuildInterface();
        }

        public void ShowOpenFileDialog()
        {
            _ = ChooseAndOpenAsync();
        }

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (_initializing)
                return;
            _initializing = true;
            try
            {
                var environment = await _getEnvironment();
                if (IsDisposed || Disposing)
                    return;
                await _view.EnsureCoreWebView2Async(environment);
                if (IsDisposed || Disposing)
                    return;
                ConfigureWebView();
                _webViewReady = true;
                ShowWelcome();
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                {
                    _fileLabel.Text = "无法启动阅读界面";
                    MessageBox.Show(FindForm(),
                        "CHENMU Reader 需要 Microsoft Edge WebView2 Runtime 才能显示文档。\n\n" + ex.Message,
                        "无法启动阅读界面", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void BuildInterface()
        {
            _header.Dock = DockStyle.Top;
            _header.Height = 60;
            _header.BackColor = Color.White;
            _header.Padding = new Padding(18, 0, 16, 0);
            _header.Resize += delegate { LayoutHeader(); };

            var brand = new Label
            {
                Text = "CHENMU READER",
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(30, 99, 125),
                Location = new Point(18, 8)
            };
            _header.Controls.Add(brand);

            _fileLabel.Text = "本地阅读 · 内容仅在此设备处理";
            _fileLabel.AutoSize = false;
            _fileLabel.TextAlign = ContentAlignment.MiddleLeft;
            _fileLabel.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
            _fileLabel.ForeColor = Color.FromArgb(111, 123, 136);
            _fileLabel.Location = new Point(18, 31);
            _fileLabel.Height = 21;
            _fileLabel.AutoEllipsis = true;
            _header.Controls.Add(_fileLabel);

            StyleButton(_previousButton, "上一页", false);
            StyleButton(_nextButton, "下一页", false);
            StyleButton(_openButton, "打开文件", true);
            _previousButton.Click += delegate { NavigatePage(_pageIndex - 1); };
            _nextButton.Click += delegate { NavigatePage(_pageIndex + 1); };
            _openButton.Click += async delegate { await ChooseAndOpenAsync(); };
            _header.Controls.Add(_previousButton);
            _header.Controls.Add(_nextButton);
            _header.Controls.Add(_openButton);

            _pageLabel.AutoSize = false;
            _pageLabel.TextAlign = ContentAlignment.MiddleCenter;
            _pageLabel.ForeColor = Color.FromArgb(100, 110, 120);
            _pageLabel.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
            _header.Controls.Add(_pageLabel);

            _rule.Dock = DockStyle.Top;
            _rule.Height = 1;
            _rule.BackColor = Color.FromArgb(230, 233, 237);

            _view.Dock = DockStyle.Fill;
            _view.Margin = Padding.Empty;
            _view.AllowExternalDrop = false;
            _view.DragEnter += OnDragEnter;
            _view.DragDrop += OnDragDrop;
            Controls.Add(_view);
            Controls.Add(_header);
            Controls.Add(_rule);
            _rule.BringToFront();
            _header.BringToFront();
            LayoutHeader();
            UpdateNavigation();
        }

        private void LayoutHeader()
        {
            int right = _header.ClientSize.Width - 16;
            _openButton.Location = new Point(right - _openButton.Width, 12);
            _nextButton.Location = new Point(_openButton.Left - 8 - _nextButton.Width, 12);
            _pageLabel.Size = new Size(78, 36);
            _pageLabel.Location = new Point(_nextButton.Left - 4 - _pageLabel.Width, 12);
            _previousButton.Location = new Point(_pageLabel.Left - 4 - _previousButton.Width, 12);
            _fileLabel.Width = Math.Max(80, _previousButton.Left - 28);
        }

        private static void StyleButton(Button button, string text, bool primary)
        {
            button.Text = text;
            button.Size = new Size(primary ? 94 : 64, 36);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(223, 227, 232);
            button.BackColor = primary ? Color.FromArgb(30, 105, 132) : Color.White;
            button.ForeColor = primary ? Color.White : Color.FromArgb(54, 66, 76);
            button.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
            button.Cursor = Cursors.Hand;
            button.TabStop = false;
        }

        private void ConfigureWebView()
        {
            var core = _view.CoreWebView2;
            var settings = core.Settings;
            settings.IsScriptEnabled = false;
            settings.IsWebMessageEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;

            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += delegate(object sender, CoreWebView2WebResourceRequestedEventArgs args)
            {
                Uri uri;
                if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out uri))
                    return;
                if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                    uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    args.Response = core.Environment.CreateWebResourceResponse(null, 403,
                        "Blocked by CHENMU Reader", "Content-Type: text/plain; charset=utf-8");
            };
            core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs args)
            {
                Uri uri;
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out uri))
                {
                    args.Cancel = true;
                    return;
                }
                if (uri.Scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsAllowedLocalPath(uri.LocalPath))
                        args.Cancel = true;
                    return;
                }
                if (uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase) ||
                    uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase) ||
                    uri.Scheme.Equals("edge-pdf", StringComparison.OrdinalIgnoreCase))
                    return;
                args.Cancel = true;
            };
        }

        private bool IsAllowedLocalPath(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                if (String.IsNullOrWhiteSpace(_allowedFileRoot))
                    return false;
                string root = Path.GetFullPath(_allowedFileRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return fullPath.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                       fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                       fullPath.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private async Task ChooseAndOpenAsync()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "CHENMU Reader · 打开本地文件";
                dialog.Filter = "支持的文件|*.pdf;*.epub;*.txt;*.md;*.markdown;*.docx;*.xlsx;*.pptx;*.csv;*.html;*.htm;*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.tif;*.tiff;*.svg|所有文件|*.*";
                dialog.CheckFileExists = true;
                dialog.Multiselect = false;
                if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
                    await OpenFileAsync(dialog.FileName);
            }
        }

        private async Task OpenFileAsync(string path)
        {
            if (_loading)
                return;
            _loading = true;
            _openButton.Enabled = false;
            string previousTemporaryRoot = _activeTemporaryRoot;
            try
            {
                var loaded = await Task.Run(() => DocumentLoader.Load(path));
                if (IsDisposed || Disposing)
                {
                    TryDeleteDirectory(loaded.TemporaryRoot);
                    return;
                }
                _document = loaded;
                _pageIndex = 0;
                _activeTemporaryRoot = loaded.TemporaryRoot;
                _allowedFileRoot = !String.IsNullOrWhiteSpace(loaded.TemporaryRoot)
                    ? loaded.TemporaryRoot
                    : Path.GetDirectoryName(path);
                _fileLabel.Text = Path.GetFileName(path);
                UpdateNavigation();
                if (_webViewReady)
                    RenderCurrentPage();
                if (!String.IsNullOrWhiteSpace(previousTemporaryRoot) &&
                    !String.Equals(previousTemporaryRoot, _activeTemporaryRoot, StringComparison.OrdinalIgnoreCase))
                    TryDeleteDirectory(previousTemporaryRoot);
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                    MessageBox.Show(FindForm(), ex.Message, "无法打开文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally
            {
                _loading = false;
                if (!IsDisposed && !Disposing)
                {
                    _openButton.Enabled = true;
                    UpdateNavigation();
                }
            }
        }

        private void ShowWelcome()
        {
            _document = null;
            _pageIndex = 0;
            _fileLabel.Text = "本地阅读 · 内容仅在此设备处理";
            _pageLabel.Text = "本地阅读";
            _previousButton.Enabled = false;
            _nextButton.Enabled = false;
            _view.NavigateToString(WrapHtml(
                "<section class=\"welcome\"><div class=\"eyebrow\">CHENMU READER</div><h1>把文件打开，安心读下去。</h1>" +
                "<p>在浏览器中阅读常见文档与电子书。选择本机文件即可开始；内容只在这台设备上处理。</p>" +
                "<div class=\"format-list\"><span>PDF</span><span>EPUB</span><span>DOCX</span><span>XLSX</span><span>PPTX</span><span>TXT · Markdown</span><span>CSV</span><span>HTML · 图片</span></div>" +
                "<p class=\"hint\">点击上方“打开文件”，或把文件拖到阅读区域。</p></section>", "CHENMU Reader"));
        }

        private void NavigatePage(int newIndex)
        {
            if (_document == null || _document.IsPdf || newIndex < 0 || newIndex >= _document.Pages.Count)
                return;
            _pageIndex = newIndex;
            UpdateNavigation();
            RenderCurrentPage();
        }

        private void RenderCurrentPage()
        {
            if (!_webViewReady || _document == null || _document.Pages.Count == 0)
                return;
            var page = _document.Pages[_pageIndex];
            if (!String.IsNullOrWhiteSpace(page.FilePath))
                _view.Source = new Uri(Path.GetFullPath(page.FilePath));
            else
                _view.NavigateToString(WrapHtml(page.Html ?? String.Empty, page.Title ?? _document.Title));
        }

        private void UpdateNavigation()
        {
            if (_document == null)
            {
                _pageLabel.Text = "本地阅读";
                _previousButton.Enabled = false;
                _nextButton.Enabled = false;
                return;
            }
            if (_document.IsPdf)
            {
                _pageLabel.Text = "PDF 阅读";
                _previousButton.Enabled = false;
                _nextButton.Enabled = false;
                return;
            }
            int total = _document.Pages.Count;
            _pageLabel.Text = total == 0 ? String.Empty : (total == 1 ? "共 1 页" : (_pageIndex + 1) + " / " + total);
            _previousButton.Enabled = !_loading && _pageIndex > 0;
            _nextButton.Enabled = !_loading && _pageIndex < total - 1;
        }

        private static string WrapHtml(string content, string title)
        {
            string safeTitle = System.Net.WebUtility.HtmlEncode(title ?? "CHENMU Reader");
            return "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>" + safeTitle + "</title>" +
                "<style>" +
                "*{box-sizing:border-box}html{background:#f6f7f9;color:#27323c;font-family:'Microsoft YaHei UI','Microsoft YaHei',Arial,sans-serif}body{margin:0;min-height:100vh;padding:36px 24px 60px}.reader-page{max-width:900px;min-height:calc(100vh - 96px);margin:0 auto;background:#fff;border:1px solid #e8ebef;border-radius:14px;padding:48px 64px;box-shadow:0 8px 30px rgba(27,45,63,.045);font-size:17px;line-height:1.9;overflow-wrap:anywhere}h1,h2,h3,h4,h5,h6{color:#202d38;line-height:1.45;margin:1.55em 0 .62em;font-weight:650}h1{font-size:2em;margin-top:.2em}h2{font-size:1.55em}h3{font-size:1.27em}p{margin:.7em 0}ul{padding-left:1.6em}li{margin:.35em 0}pre{white-space:pre-wrap;word-break:break-word;background:#f5f7f9;border-radius:9px;padding:18px 20px;font:14px/1.75 Consolas,'Microsoft YaHei UI',monospace}pre.plain-text{background:transparent;padding:0;font:16px/1.9 'Microsoft YaHei UI','Microsoft YaHei',sans-serif}code{font-family:Consolas,monospace;background:#f0f2f4;border-radius:4px;padding:1px 5px}table{border-collapse:collapse;width:100%;margin:20px 0;font-size:14px;line-height:1.55}td,th{border:1px solid #e2e6ea;padding:8px 10px;min-width:66px;vertical-align:top}tr:nth-child(even){background:#fafbfc}.sheet-caption,.slide-number{color:#687785;font-size:13px;margin-bottom:12px}.sheet{font-size:13px}.slide-card{min-height:360px;border:1px solid #e6e9ed;border-radius:10px;padding:30px;background:#fff}.slide-card p{font-size:21px;line-height:1.65;margin:.6em 0}.note{font-size:13px;color:#7a8792}.spacer{height:.7em}.welcome{max-width:760px;margin:9vh auto 0;padding:25px 12px}.eyebrow{font-size:12px;font-weight:700;letter-spacing:.16em;color:#2b719f}.welcome h1{font-size:34px;margin:17px 0 13px}.welcome p{font-size:16px;line-height:1.8;color:#66737f}.format-list{display:flex;flex-wrap:wrap;gap:8px;margin:23px 0}.format-list span{background:white;border:1px solid #e3e8ed;border-radius:999px;padding:7px 12px;font-size:13px;color:#495864}.welcome .hint{font-size:14px;margin-top:30px;color:#788590}@media(max-width:700px){body{padding:14px}.reader-page{padding:30px 22px;font-size:16px}.welcome{margin:6vh auto 0}.welcome h1{font-size:28px}}" +
                "</style></head><body><main class=\"reader-page\">" + content + "</main></body></html>";
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
        }

        private async void OnDragDrop(object sender, DragEventArgs e)
        {
            var files = e.Data == null ? null : e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0)
                await OpenFileAsync(files[0]);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.O)
            {
                e.Handled = true;
                ShowOpenFileDialog();
            }
            else if (e.Alt && e.KeyCode == Keys.Left)
            {
                e.Handled = true;
                NavigatePage(_pageIndex - 1);
            }
            else if (e.Alt && e.KeyCode == Keys.Right)
            {
                e.Handled = true;
                NavigatePage(_pageIndex + 1);
            }
            base.OnKeyDown(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _view.Dispose(); } catch { }
                TryDeleteDirectory(_activeTemporaryRoot);
                _activeTemporaryRoot = null;
            }
            base.Dispose(disposing);
        }

        private static void TryDeleteDirectory(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
                return;
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch { }
        }
    }
}
