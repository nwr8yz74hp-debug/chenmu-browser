using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace ChenmuBrowser
{
    internal sealed partial class BrowserForm
    {
        private const string OfficialWebsiteUrl = "https://chenmu-browser-site.cosmic-cast.workers.dev/";
        private const string SoumuSearchUrlPrefix = "https://duckduckgo.com/html/?q=";
        private readonly Dictionary<CoreWebView2, string> _pageAnimationScriptIds =
            new Dictionary<CoreWebView2, string>();
        private readonly Timer _homeGreetingTimer = new Timer();
        private int _historyRetentionDays = 90;
        private bool _pageAnimationsEnabled = true;
        private Label _homeBrandLabel;
        private Label _homeGreetingLabel;
        private Label _homeClockLabel;
        private string HistoryPath { get { return Path.Combine(_dataRoot, "history.tsv"); } }
        private string PendingUpdateNoticePath { get { return Path.Combine(_dataRoot, "update-notice.txt"); } }

        private sealed class HistoryEntry
        {
            public string Id { get; set; }
            public DateTime VisitedUtc { get; set; }
            public string Title { get; set; }
            public string Url { get; set; }
        }

        private void InitializeV11Features()
        {
            _homeGreetingTimer.Interval = 30000;
            _homeGreetingTimer.Tick += delegate { RefreshHomeGreeting(); };
            _homeGreetingTimer.Start();
            RefreshHomeGreeting();
            ReadHistoryEntries();
        }

        private void LayoutTabSidebarOverlay()
        {
            if (_browserSurface.IsDisposed || _tabSidebarHost.IsDisposed)
                return;
            var top = _header.Bottom;
            var height = Math.Max(0, _browserSurface.ClientSize.Height - _header.Height - _statusPanel.Height);
            var scale = Math.Max(1F, _browserSurface.DeviceDpi / 96F);
            var width = (int)Math.Round((_tabSidebarExpanded ? 118F : 10F) * scale);
            _tabSidebarHost.SetBounds(0, top, width, height);
            _tabSidebarEdge.Width = Math.Max(8, (int)Math.Round(10F * scale));
            _tabSidebarHost.BringToFront();
        }

        private static string GetSearchEngineDisplayName(string engine)
        {
            if (String.Equals(engine, "bing", StringComparison.OrdinalIgnoreCase))
                return "Bing";
            if (String.Equals(engine, "soumu", StringComparison.OrdinalIgnoreCase))
                return "Soumu";
            return "Google";
        }

        private void RefreshHomeGreeting()
        {
            if (_homeGreetingLabel == null || _homeGreetingLabel.IsDisposed)
                return;
            _homeGreetingLabel.Text = GetHomeGreeting();
            if (_homeClockLabel != null && !_homeClockLabel.IsDisposed)
                _homeClockLabel.Text = DateTime.Now.ToString("yyyy年M月d日 dddd  HH:mm", CultureInfo.GetCultureInfo("zh-CN")) +
                    "  ·  愿你在浏览中有所发现";
        }

        private void RecordHistory(CoreWebView2 core)
        {
            if (core == null || String.IsNullOrWhiteSpace(core.Source))
                return;
            Uri uri;
            if (!Uri.TryCreate(core.Source, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return;
            try
            {
                var entries = ReadHistoryEntries();
                entries.Insert(0, new HistoryEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    VisitedUtc = DateTime.UtcNow,
                    Title = String.IsNullOrWhiteSpace(core.DocumentTitle) ? uri.Host : core.DocumentTitle,
                    Url = core.Source
                });
                WriteHistoryEntries(PruneHistory(entries));
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                    _statusLabel.Text = "历史记录无法保存：" + ex.Message;
            }
        }

        private List<HistoryEntry> ReadHistoryEntries()
        {
            var entries = new List<HistoryEntry>();
            try
            {
                if (!File.Exists(HistoryPath))
                    return entries;
                foreach (var line in File.ReadAllLines(HistoryPath, Encoding.UTF8))
                {
                    try
                    {
                        var parts = line.Split('\t');
                        DateTime visited;
                        if (parts.Length != 4 || String.IsNullOrWhiteSpace(parts[1]) ||
                            !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out visited))
                            continue;
                        var title = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
                        var url = Encoding.UTF8.GetString(Convert.FromBase64String(parts[3]));
                        Uri uri;
                        if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                            continue;
                        entries.Add(new HistoryEntry
                        {
                            Id = parts[1],
                            VisitedUtc = visited.ToUniversalTime(),
                            Title = title,
                            Url = url
                        });
                    }
                    catch { }
                }
            }
            catch { }
            var pruned = PruneHistory(entries);
            if (pruned.Count != entries.Count)
                WriteHistoryEntries(pruned);
            return pruned;
        }

        private List<HistoryEntry> PruneHistory(IEnumerable<HistoryEntry> entries)
        {
            var valid = entries.Where(entry => entry != null)
                .OrderByDescending(entry => entry.VisitedUtc)
                .ToList();
            if (_historyRetentionDays > 0)
            {
                var cutoff = DateTime.UtcNow.AddDays(-_historyRetentionDays);
                valid = valid.Where(entry => entry.VisitedUtc >= cutoff).ToList();
            }
            return valid;
        }

        private void WriteHistoryEntries(List<HistoryEntry> entries)
        {
            try
            {
                Directory.CreateDirectory(_dataRoot);
                var lines = entries.Select(entry => entry.VisitedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + "\t" +
                    entry.Id + "\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Title ?? String.Empty)) + "\t" +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Url ?? String.Empty)));
                File.WriteAllLines(HistoryPath, lines, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                    _statusLabel.Text = "历史记录无法保存：" + ex.Message;
            }
        }

        private bool ClearHistoryEntries()
        {
            try
            {
                if (File.Exists(HistoryPath))
                    File.Delete(HistoryPath);
                return !File.Exists(HistoryPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法清除历史记录：" + ex.Message, "CHENMU Browser",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        private void ShowHistoryDialog()
        {
            var dialog = new Form
            {
                Text = "浏览历史",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.Sizable,
                MinimizeBox = false,
                MaximizeBox = false,
                MinimumSize = new System.Drawing.Size(680, 380),
                Size = new System.Drawing.Size(880, 540),
                Font = Font
            };
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(12),
                BackColor = Color.White
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            var search = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 7), Font = Font };
            SetCueBanner(search, "搜索标题或网址");
            var list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false,
                MultiSelect = false,
                Font = Font
            };
            list.Columns.Add("页面", 255);
            list.Columns.Add("网址", 390);
            list.Columns.Add("访问时间", 160);
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 5, 0, 0)
            };
            var close = new Button { Text = "关闭", Width = 82, Height = 30, DialogResult = DialogResult.Cancel };
            var clear = new Button { Text = "清空历史", Width = 96, Height = 30 };
            var remove = new Button { Text = "删除所选", Width = 90, Height = 30, Enabled = false };
            var open = new Button { Text = "打开页面", Width = 90, Height = 30, Enabled = false };
            buttons.Controls.Add(close);
            buttons.Controls.Add(clear);
            buttons.Controls.Add(remove);
            buttons.Controls.Add(open);
            layout.Controls.Add(search, 0, 0);
            layout.Controls.Add(list, 0, 1);
            layout.Controls.Add(buttons, 0, 2);
            dialog.Controls.Add(layout);
            dialog.CancelButton = close;

            Action refresh = delegate
            {
                var filter = (search.Text ?? String.Empty).Trim();
                list.BeginUpdate();
                list.Items.Clear();
                foreach (var entry in ReadHistoryEntries())
                {
                    if (filter.Length > 0 &&
                        (entry.Title ?? String.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        (entry.Url ?? String.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    var item = new ListViewItem(entry.Title ?? String.Empty) { Tag = entry };
                    item.SubItems.Add(entry.Url ?? String.Empty);
                    item.SubItems.Add(entry.VisitedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture));
                    list.Items.Add(item);
                }
                list.EndUpdate();
            };
            Func<Task> openSelected = async delegate
            {
                if (list.SelectedItems.Count == 0)
                    return;
                var entry = list.SelectedItems[0].Tag as HistoryEntry;
                if (entry == null)
                    return;
                dialog.Close();
                await NavigateAsync(entry.Url);
            };
            search.TextChanged += delegate { refresh(); };
            list.SelectedIndexChanged += delegate
            {
                var selected = list.SelectedItems.Count > 0;
                remove.Enabled = selected;
                open.Enabled = selected;
            };
            list.DoubleClick += async delegate { await openSelected(); };
            open.Click += async delegate { await openSelected(); };
            remove.Click += delegate
            {
                if (list.SelectedItems.Count == 0)
                    return;
                var entry = list.SelectedItems[0].Tag as HistoryEntry;
                if (entry == null)
                    return;
                WriteHistoryEntries(ReadHistoryEntries().Where(item => item.Id != entry.Id).ToList());
                refresh();
            };
            clear.Click += delegate
            {
                if (MessageBox.Show(dialog, "清空此设备上保存的浏览历史？", "清空历史",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
                if (!ClearHistoryEntries())
                    return;
                refresh();
            };
            refresh();
            dialog.ShowDialog(this);
        }

        private void ShowBrowserSettingsDialog()
        {
            var dialog = new Form
            {
                Text = "浏览器设置",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new System.Drawing.Size(440, 350),
                Font = Font
            };
            var engineLabel = new Label { Text = "默认搜索", AutoSize = true, Location = new System.Drawing.Point(22, 24) };
            var engine = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new System.Drawing.Point(148, 19), Width = 260 };
            engine.Items.Add("Google");
            engine.Items.Add("Bing");
            engine.Items.Add("Soumu（DuckDuckGo 第三方结果）");
            engine.SelectedIndex = _searchEngine == "bing" ? 1 : _searchEngine == "soumu" ? 2 : 0;
            var retentionLabel = new Label { Text = "历史记录保留", AutoSize = true, Location = new System.Drawing.Point(22, 68) };
            var retention = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new System.Drawing.Point(148, 63), Width = 260 };
            retention.Items.AddRange(new object[] { "7 天", "30 天", "90 天", "365 天", "不自动清理" });
            var retentionValues = new[] { 7, 30, 90, 365, 0 };
            var retentionIndex = Array.IndexOf(retentionValues, _historyRetentionDays);
            retention.SelectedIndex = retentionIndex < 0 ? 2 : retentionIndex;
            var block = new CheckBox { Text = "拦截广告与跟踪域名（随附本地域名列表）", AutoSize = true, Location = new System.Drawing.Point(24, 112), Checked = _blockTrackers };
            var saver = new CheckBox { Text = "省流模式（不加载图片）", AutoSize = true, Location = new System.Drawing.Point(24, 148), Checked = _dataSaver };
            var animations = new CheckBox { Text = "启用轻量页面淡入动画", AutoSize = true, Location = new System.Drawing.Point(24, 184), Checked = _pageAnimationsEnabled };
            var note = new Label
            {
                Text = "Soumu 使用 DuckDuckGo 第三方搜索结果，不是自建索引；结果页可能含第三方广告。\r\n历史文件留在本机；当前搜索词仍会发送给所选搜索服务。",
                AutoSize = false,
                Location = new System.Drawing.Point(24, 220),
                Size = new System.Drawing.Size(390, 64),
                ForeColor = Color.FromArgb(105, 117, 131)
            };
            var cancel = new Button { Text = "取消", Width = 84, Height = 30, Location = new System.Drawing.Point(226, 300), DialogResult = DialogResult.Cancel };
            var save = new Button { Text = "保存", Width = 84, Height = 30, Location = new System.Drawing.Point(320, 300), DialogResult = DialogResult.OK };
            dialog.Controls.Add(engineLabel);
            dialog.Controls.Add(engine);
            dialog.Controls.Add(retentionLabel);
            dialog.Controls.Add(retention);
            dialog.Controls.Add(block);
            dialog.Controls.Add(saver);
            dialog.Controls.Add(animations);
            dialog.Controls.Add(note);
            dialog.Controls.Add(cancel);
            dialog.Controls.Add(save);
            dialog.CancelButton = cancel;
            dialog.AcceptButton = save;
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            _searchEngine = engine.SelectedIndex == 1 ? "bing" : engine.SelectedIndex == 2 ? "soumu" : "google";
            _historyRetentionDays = retentionValues[Math.Max(0, retention.SelectedIndex)];
            _blockTrackers = block.Checked;
            _dataSaver = saver.Checked;
            var animationsChanged = _pageAnimationsEnabled != animations.Checked;
            _pageAnimationsEnabled = animations.Checked;
            if (_homeSearchEngineButton != null)
                _homeSearchEngineButton.Text = GetSearchEngineDisplayName(_searchEngine) + " ▾";
            SavePreferences();
            ReadHistoryEntries();
            if (animationsChanged)
                ApplyPageAnimations();
            _statusLabel.Text = "浏览器设置已保存。";
        }

        private async void OpenOfficialWebsite()
        {
            CreateNewTab();
            await NavigateAsync(OfficialWebsiteUrl);
        }

        private string BuildPageAnimationScript(bool enabled)
        {
            const string styleId = "chenmu-browser-page-motion";
            if (!enabled)
                return "(function(){var s=document.getElementById('" + styleId + "');if(s)s.remove();})();";
            return "(function(){try{if(document.getElementById('" + styleId + "'))return;" +
                "var s=document.createElement('style');s.id='" + styleId + "';" +
                "s.textContent='@keyframes chenmuPageEnter{from{opacity:0;transform:translateY(3px)}to{opacity:1;transform:translateY(0)}}body{animation:chenmuPageEnter 180ms ease-out both}@media(prefers-reduced-motion:reduce){body{animation:none!important}}';" +
                "(document.head||document.documentElement).appendChild(s);}catch(e){}})();";
        }

        private async void RegisterPageAnimationScript(CoreWebView2 core)
        {
            try
            {
                if (core == null || !_pageAnimationsEnabled)
                    return;
                var scriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(BuildPageAnimationScript(true));
                if (IsDisposed || Disposing)
                    return;
                if (!_pageAnimationsEnabled)
                    core.RemoveScriptToExecuteOnDocumentCreated(scriptId);
                else
                    _pageAnimationScriptIds[core] = scriptId;
            }
            catch { }
        }

        private void ApplyPageAnimations()
        {
            foreach (var tab in _tabs.ToList())
            {
                var core = tab.WebView == null ? null : tab.WebView.CoreWebView2;
                if (core == null)
                    continue;
                string scriptId;
                if (_pageAnimationScriptIds.TryGetValue(core, out scriptId))
                {
                    try { core.RemoveScriptToExecuteOnDocumentCreated(scriptId); }
                    catch { }
                    _pageAnimationScriptIds.Remove(core);
                }
                if (_pageAnimationsEnabled)
                    RegisterPageAnimationScript(core);
                try { var ignored = core.ExecuteScriptAsync(BuildPageAnimationScript(_pageAnimationsEnabled)); }
                catch { }
            }
        }

        private void ShowPendingUpdateNotice()
        {
            try
            {
                if (!File.Exists(PendingUpdateNoticePath))
                    return;
                var content = File.ReadAllText(PendingUpdateNoticePath, Encoding.UTF8);
                File.Delete(PendingUpdateNoticePath);
                var split = content.IndexOfAny(new[] { '\r', '\n' });
                if (split <= 0)
                    return;
                var versionText = content.Substring(0, split).Trim();
                Version noticeVersion;
                if (!Version.TryParse(versionText.TrimStart('v'), out noticeVersion) || noticeVersion != UpdateService.CurrentVersion)
                    return;
                var notes = content.Substring(split).Trim();
                if (String.IsNullOrWhiteSpace(notes))
                    notes = ReadInstalledReleaseNotes();
                if (String.IsNullOrWhiteSpace(notes))
                    notes = "浏览器已更新至 v" + noticeVersion.ToString(3) + "。";
                MessageBox.Show(this, "已更新至 v" + noticeVersion.ToString(3) + "。\r\n\r\n更新内容：\r\n" + notes,
                    "CHENMU Browser 更新完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch { }
        }

        private string ReadInstalledReleaseNotes()
        {
            try
            {
                var path = Path.Combine(Application.StartupPath, "RELEASE_NOTES.md");
                if (!File.Exists(path))
                    return String.Empty;
                var bullets = File.ReadAllLines(path, Encoding.UTF8)
                    .Select(line => line.Trim())
                    .Where(line => line.StartsWith("- ", StringComparison.Ordinal) ||
                                   line.StartsWith("* ", StringComparison.Ordinal))
                    .Select(line => "• " + line.Substring(2).Trim())
                    .ToList();
                return String.Join(Environment.NewLine, bullets);
            }
            catch { return String.Empty; }
        }
    }
}
