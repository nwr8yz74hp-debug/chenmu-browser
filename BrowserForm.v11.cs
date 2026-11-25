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
        private int _downloadHistoryRetentionDays = 30;
        private bool _restoreReaderPosition;
        private List<SitePermissionRule> _sitePermissionRules = new List<SitePermissionRule>();
        private bool _pageAnimationsEnabled = true;
        private Label _homeBrandLabel;
        private Label _homeGreetingLabel;
        private Label _homeClockLabel;
        private DateTime _nextHomeGreetingChangeAt = DateTime.MinValue;
        private List<BrowserHistoryRecord> _historyEntries = new List<BrowserHistoryRecord>();
        private bool _historyLoaded;
        private DebouncedWorkQueue<List<BrowserHistoryRecord>> _historyWriteQueue;
        private string HistoryPath { get { return Path.Combine(_dataRoot, "history.tsv"); } }
        private string DownloadHistoryPath { get { return Path.Combine(_dataRoot, "downloads.tsv"); } }
        private string ReaderPositionPath { get { return Path.Combine(_dataRoot, "reader-positions.tsv"); } }
        private string SitePermissionsPath { get { return Path.Combine(_dataRoot, "site-permissions.tsv"); } }
        private string PendingUpdateNoticePath { get { return Path.Combine(_dataRoot, "update-notice.txt"); } }

        private void InitializeV11Features()
        {
            _historyWriteQueue = new DebouncedWorkQueue<List<BrowserHistoryRecord>>(
                entries => BrowserHistoryStorage.Write(HistoryPath, entries),
                exception => ReportPersistenceError("历史记录无法保存：", exception), 350);
            _homeGreetingTimer.Interval = 30000;
            _homeGreetingTimer.Tick += delegate { RefreshHomeGreeting(); };
            _homeGreetingTimer.Start();
            RefreshHomeGreeting();
            _sitePermissionRules = SitePermissionStorage.Read(SitePermissionsPath);
            ReadHistoryEntries();
            LoadDownloadHistory();
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
            var now = DateTime.Now;
            if (_nextHomeGreetingChangeAt == DateTime.MinValue)
                _nextHomeGreetingChangeAt = now.AddMinutes(HomeGreetingProvider.RotationIntervalMinutes);
            else if (now >= _nextHomeGreetingChangeAt)
            {
                _homeGreetingLabel.Text = HomeGreetingProvider.GetGreeting(now, _homeGreetingLabel.Text);
                _nextHomeGreetingChangeAt = now.AddMinutes(HomeGreetingProvider.RotationIntervalMinutes);
            }
            if (_homeClockLabel != null && !_homeClockLabel.IsDisposed)
                _homeClockLabel.Text = now.ToString("yyyy年M月d日 dddd  HH:mm", CultureInfo.GetCultureInfo("zh-CN")) +
                    "  ·  浏览发现世界";
        }

        private void RecordHistory(CoreWebView2 core)
        {
            var ownerTab = FindTabForCore(core);
            if (_isIsolatedTest || ownerTab == null || ownerTab.IsPrivate ||
                String.IsNullOrWhiteSpace(core.Source))
                return;
            Uri uri;
            if (!Uri.TryCreate(core.Source, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return;
            try
            {
                var entries = ReadHistoryEntries();
                entries.Insert(0, new BrowserHistoryRecord
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
                ReportPersistenceError("历史记录无法保存：", ex);
            }
        }

        private List<BrowserHistoryRecord> ReadHistoryEntries()
        {
            if (!_historyLoaded)
            {
                var stored = BrowserHistoryStorage.Read(HistoryPath);
                _historyEntries = PruneHistory(stored);
                _historyLoaded = true;
                if (!_isIsolatedTest && _historyEntries.Count != stored.Count && _historyWriteQueue != null)
                    _historyWriteQueue.Queue(new List<BrowserHistoryRecord>(_historyEntries));
            }
            return new List<BrowserHistoryRecord>(_historyEntries);
        }

        private List<BrowserHistoryRecord> PruneHistory(IEnumerable<BrowserHistoryRecord> entries)
        {
            return BrowserHistoryStorage.Prune(entries, _historyRetentionDays, DateTime.UtcNow);
        }

        private void WriteHistoryEntries(List<BrowserHistoryRecord> entries)
        {
            _historyEntries = PruneHistory(entries);
            _historyLoaded = true;
            if (!_isIsolatedTest && _historyWriteQueue != null)
                _historyWriteQueue.Queue(new List<BrowserHistoryRecord>(_historyEntries));
        }

        private void LoadDownloadHistory()
        {
            if (_isIsolatedTest)
                return;
            _downloads.Clear();
            foreach (var record in DownloadHistoryStorage.Read(DownloadHistoryPath, _downloadHistoryRetentionDays))
                _downloads.Add(new DownloadEntry { FilePath = record.FilePath, SavedRecord = record });
            PersistDownloadHistory();
        }

        private void PersistDownloadHistory()
        {
            if (_isIsolatedTest)
                return;
            try
            {
                var records = new List<DownloadHistoryRecord>();
                foreach (var entry in _downloads.ToList())
                {
                    if (entry == null || (entry.Owner != null && entry.Owner.IsPrivate))
                        continue;
                    var record = CreateDownloadHistoryRecord(entry);
                    if (record != null)
                        records.Add(record);
                }
                var retained = DownloadHistoryStorage.Prune(records, _downloadHistoryRetentionDays, DateTime.UtcNow);
                DownloadHistoryStorage.Write(DownloadHistoryPath, retained);
                var retainedIds = new HashSet<string>(retained.Select(record => record.Id), StringComparer.Ordinal);
                _downloads.RemoveAll(entry => entry != null && entry.Operation == null &&
                    (entry.SavedRecord == null || !retainedIds.Contains(entry.SavedRecord.Id)));
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                    _statusLabel.Text = "下载记录无法保存：" + ex.Message;
            }
        }

        private static DownloadHistoryRecord CreateDownloadHistoryRecord(DownloadEntry entry)
        {
            if (entry == null)
                return null;
            if (entry.Operation == null)
                return entry.SavedRecord;

            var previous = entry.SavedRecord;
            var state = entry.Operation.State;
            var status = state == CoreWebView2DownloadState.InProgress
                ? DownloadRecordStatus.InProgress
                : state == CoreWebView2DownloadState.Completed
                    ? DownloadRecordStatus.Completed
                    : entry.Operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled
                        ? DownloadRecordStatus.Canceled
                        : DownloadRecordStatus.Interrupted;
            long received = Math.Max(0L, entry.Operation.BytesReceived);
            long? total = null;
            try
            {
                var value = entry.Operation.TotalBytesToReceive;
                if (value.HasValue)
                {
                    long converted = Convert.ToInt64(value.Value, CultureInfo.InvariantCulture);
                    if (converted >= 0)
                        total = converted;
                }
            }
            catch { }

            var record = new DownloadHistoryRecord
            {
                Id = previous == null || String.IsNullOrWhiteSpace(previous.Id) ? Guid.NewGuid().ToString("N") : previous.Id,
                StartedUtc = previous == null ? DateTime.UtcNow : previous.StartedUtc,
                FinishedUtc = status == DownloadRecordStatus.InProgress
                    ? (DateTime?)null
                    : previous != null && previous.FinishedUtc.HasValue ? previous.FinishedUtc : DateTime.UtcNow,
                FilePath = entry.FilePath,
                FileName = Path.GetFileName(entry.FilePath),
                Status = status,
                BytesReceived = received,
                TotalBytes = total
            };
            entry.SavedRecord = record;
            return record;
        }

        private bool ClearHistoryEntries()
        {
            try
            {
                if (!_isIsolatedTest && _historyWriteQueue != null)
                    _historyWriteQueue.Flush();
                if (File.Exists(HistoryPath))
                    File.Delete(HistoryPath);
                if (File.Exists(HistoryPath))
                    return false;
                _historyEntries = new List<BrowserHistoryRecord>();
                _historyLoaded = true;
                return true;
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
                var entry = list.SelectedItems[0].Tag as BrowserHistoryRecord;
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
                var entry = list.SelectedItems[0].Tag as BrowserHistoryRecord;
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
            ApplyDialogStyle(dialog);
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
                ClientSize = new System.Drawing.Size(500, 610),
                Font = Font
            };
            var engineLabel = new Label { Text = "默认搜索", AutoSize = true, Location = new System.Drawing.Point(22, 24) };
            var engine = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new System.Drawing.Point(160, 19), Width = 300 };
            engine.Items.Add("Google");
            engine.Items.Add("Bing");
            engine.Items.Add("Soumu（DuckDuckGo 第三方结果）");
            engine.SelectedIndex = _searchEngine == "bing" ? 1 : _searchEngine == "soumu" ? 2 : 0;
            var retentionLabel = new Label { Text = "历史记录保留", AutoSize = true, Location = new System.Drawing.Point(22, 62) };
            var retention = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new System.Drawing.Point(160, 57), Width = 300 };
            retention.Items.AddRange(new object[] { "7 天", "30 天", "90 天", "365 天", "不自动清理" });
            var retentionValues = new[] { 7, 30, 90, 365, 0 };
            var retentionIndex = Array.IndexOf(retentionValues, _historyRetentionDays);
            retention.SelectedIndex = retentionIndex < 0 ? 2 : retentionIndex;
            var downloadRetentionLabel = new Label { Text = "下载记录保留", AutoSize = true, Location = new System.Drawing.Point(22, 100) };
            var downloadRetention = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new System.Drawing.Point(160, 95), Width = 300 };
            downloadRetention.Items.AddRange(new object[] { "7 天", "30 天", "90 天", "365 天", "不按日期清理" });
            var downloadRetentionIndex = Array.IndexOf(retentionValues, _downloadHistoryRetentionDays);
            downloadRetention.SelectedIndex = downloadRetentionIndex < 0 ? 1 : downloadRetentionIndex;
            var block = new CheckBox { Text = "拦截广告与跟踪域名（随附本地域名列表）", AutoSize = true, Location = new System.Drawing.Point(24, 137), Checked = _blockTrackers };
            var saver = new CheckBox { Text = "省流模式（不加载图片）", AutoSize = true, Location = new System.Drawing.Point(24, 167), Checked = _dataSaver };
            var animations = new CheckBox { Text = "启用轻量页面淡入动画", AutoSize = true, Location = new System.Drawing.Point(24, 197), Checked = _pageAnimationsEnabled };
            var restoreSession = new CheckBox
            {
                Text = "退出后恢复上次普通浏览会话（仅本机保存网址）",
                AutoSize = true,
                Location = new System.Drawing.Point(24, 227),
                Checked = _restorePreviousSession
            };
            var restoreReaderPosition = new CheckBox
            {
                Text = "记住 Reader 的文档页码/章节位置",
                AutoSize = true,
                Location = new System.Drawing.Point(24, 257),
                Checked = _restoreReaderPosition
            };
            var manageAllowlist = new Button
            {
                Text = "管理站点允许列表…",
                Width = 195,
                Height = 32,
                Location = new System.Drawing.Point(24, 290)
            };
            manageAllowlist.Click += delegate { ShowSiteAllowlistDialog(); };
            var managePermissions = new Button
            {
                Text = "管理站点权限…",
                Width = 195,
                Height = 32,
                Location = new System.Drawing.Point(234, 290)
            };
            managePermissions.Click += delegate { ShowSitePermissionsDialog(); };
            var note = new Label
            {
                Text = "历史记录最多 5,000 条 / 2 MiB；下载记录默认保留 30 天、最多 200 项。清理下载记录只删除元数据，不会删除下载文件。\r\n\r\nReader 位置恢复会在本机保存所选文档的完整路径和页码/章节位置；关闭此项会清除已保存的位置。\r\n摄像头、麦克风、位置与通知权限默认拒绝；仅 HTTPS 站点可在“管理站点权限”中逐项允许。私密标签不会写入历史或下载记录。",
                AutoSize = false,
                Location = new System.Drawing.Point(24, 337),
                Size = new System.Drawing.Size(452, 204),
                ForeColor = Color.FromArgb(105, 117, 131)
            };
            var cancel = new Button { Text = "取消", Width = 84, Height = 32, Location = new System.Drawing.Point(304, 562), DialogResult = DialogResult.Cancel };
            var save = new Button { Text = "保存", Width = 84, Height = 32, Location = new System.Drawing.Point(398, 562), DialogResult = DialogResult.OK };
            dialog.Controls.Add(engineLabel);
            dialog.Controls.Add(engine);
            dialog.Controls.Add(retentionLabel);
            dialog.Controls.Add(retention);
            dialog.Controls.Add(downloadRetentionLabel);
            dialog.Controls.Add(downloadRetention);
            dialog.Controls.Add(block);
            dialog.Controls.Add(saver);
            dialog.Controls.Add(animations);
            dialog.Controls.Add(restoreSession);
            dialog.Controls.Add(restoreReaderPosition);
            dialog.Controls.Add(manageAllowlist);
            dialog.Controls.Add(managePermissions);
            dialog.Controls.Add(note);
            dialog.Controls.Add(cancel);
            dialog.Controls.Add(save);
            dialog.CancelButton = cancel;
            dialog.AcceptButton = save;
            ApplyDialogStyle(dialog);
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            _searchEngine = engine.SelectedIndex == 1 ? "bing" : engine.SelectedIndex == 2 ? "soumu" : "google";
            var historyRetentionChanged = _historyRetentionDays != retentionValues[Math.Max(0, retention.SelectedIndex)];
            _historyRetentionDays = retentionValues[Math.Max(0, retention.SelectedIndex)];
            _downloadHistoryRetentionDays = retentionValues[Math.Max(0, downloadRetention.SelectedIndex)];
            _blockTrackers = block.Checked;
            _dataSaver = saver.Checked;
            var restoreSessionChanged = _restorePreviousSession != restoreSession.Checked;
            _restorePreviousSession = restoreSession.Checked;
            var restoreReaderPositionChanged = _restoreReaderPosition != restoreReaderPosition.Checked;
            _restoreReaderPosition = restoreReaderPosition.Checked;
            var animationsChanged = _pageAnimationsEnabled != animations.Checked;
            _pageAnimationsEnabled = animations.Checked;
            if (_homeSearchEngineButton != null)
                _homeSearchEngineButton.Text = GetSearchEngineDisplayName(_searchEngine) + " ▾";
            SavePreferences();
            if (restoreReaderPositionChanged && !_restoreReaderPosition)
                ReaderPositionStorage.Clear(ReaderPositionPath);
            if (restoreSessionChanged)
            {
                if (_restorePreviousSession)
                    SaveSessionSnapshot();
                else
                {
                    try
                    {
                        if (_sessionSnapshotQueue != null)
                            _sessionSnapshotQueue.Flush();
                        if (File.Exists(SessionPath))
                            File.Delete(SessionPath);
                    }
                    catch { _statusLabel.Text = "会话恢复已关闭，但旧快照无法删除。"; }
                }
            }
            if (historyRetentionChanged)
                WriteHistoryEntries(ReadHistoryEntries());
            else
                ReadHistoryEntries();
            PersistDownloadHistory();
            if (animationsChanged)
                ApplyPageAnimations();
            _statusLabel.Text = "浏览器设置已保存。";
        }

        private void ShowSitePermissionsDialog()
        {
            var dialog = new Form
            {
                Text = "站点权限",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new System.Drawing.Size(600, 470),
                Font = Font
            };
            var rules = SitePermissionStorage.Read(SitePermissionsPath);
            var originBox = new TextBox { Location = new System.Drawing.Point(18, 18), Width = 350, Height = 25 };
            SetCueBanner(originBox, "输入 HTTPS 站点，例如 https://example.com");
            var currentButton = new Button { Text = "当前 HTTPS 站点", Location = new System.Drawing.Point(378, 16), Width = 122, Height = 30 };
            var addButton = new Button { Text = "添加", Location = new System.Drawing.Point(508, 16), Width = 72, Height = 30 };
            var sites = new ListBox
            {
                Location = new System.Drawing.Point(18, 56),
                Size = new System.Drawing.Size(230, 292),
                IntegralHeight = false,
                Font = dialog.Font
            };
            var permissions = new GroupBox
            {
                Text = "所选 HTTPS 站点可使用的权限",
                Location = new System.Drawing.Point(262, 56),
                Size = new System.Drawing.Size(318, 292),
                Font = dialog.Font
            };
            var camera = new CheckBox { Text = "允许摄像头", AutoSize = true, Location = new System.Drawing.Point(18, 38), Enabled = false };
            var microphone = new CheckBox { Text = "允许麦克风", AutoSize = true, Location = new System.Drawing.Point(18, 86), Enabled = false };
            var geolocation = new CheckBox { Text = "允许地理位置", AutoSize = true, Location = new System.Drawing.Point(18, 134), Enabled = false };
            var notifications = new CheckBox { Text = "允许通知", AutoSize = true, Location = new System.Drawing.Point(18, 182), Enabled = false };
            permissions.Controls.Add(camera);
            permissions.Controls.Add(microphone);
            permissions.Controls.Add(geolocation);
            permissions.Controls.Add(notifications);
            var saveButton = new Button { Text = "保存所选权限", Location = new System.Drawing.Point(262, 360), Width = 112, Height = 32, Enabled = false };
            var removeButton = new Button { Text = "删除站点规则", Location = new System.Drawing.Point(382, 360), Width = 108, Height = 32, Enabled = false };
            var closeButton = new Button { Text = "关闭", Location = new System.Drawing.Point(498, 360), Width = 82, Height = 32, DialogResult = DialogResult.Cancel };
            var note = new Label
            {
                Text = "未在此处允许的权限一律拒绝；只接受 HTTPS 来源，应用不会弹出打断式权限提示。删除规则或取消勾选即恢复默认拒绝。",
                Location = new System.Drawing.Point(18, 405),
                Size = new System.Drawing.Size(562, 48),
                ForeColor = Color.FromArgb(105, 117, 131)
            };
            dialog.Controls.Add(originBox);
            dialog.Controls.Add(currentButton);
            dialog.Controls.Add(addButton);
            dialog.Controls.Add(sites);
            dialog.Controls.Add(permissions);
            dialog.Controls.Add(saveButton);
            dialog.Controls.Add(removeButton);
            dialog.Controls.Add(closeButton);
            dialog.Controls.Add(note);
            dialog.CancelButton = closeButton;

            Action refresh = delegate
            {
                string selected = sites.SelectedItem as string;
                sites.BeginUpdate();
                sites.Items.Clear();
                foreach (var rule in rules.OrderBy(item => item.Origin, StringComparer.OrdinalIgnoreCase))
                    sites.Items.Add(rule.Origin);
                sites.EndUpdate();
                if (!String.IsNullOrWhiteSpace(selected))
                    sites.SelectedItem = selected;
                if (sites.SelectedIndex < 0 && sites.Items.Count > 0)
                    sites.SelectedIndex = 0;
            };
            Action loadSelection = delegate
            {
                var selected = sites.SelectedItem as string;
                originBox.Text = selected ?? originBox.Text;
                var rule = selected == null ? null : rules.FirstOrDefault(item =>
                    String.Equals(item.Origin, selected, StringComparison.OrdinalIgnoreCase));
                camera.Checked = rule != null && rule.Allowed.Contains(SitePermissionKind.Camera);
                microphone.Checked = rule != null && rule.Allowed.Contains(SitePermissionKind.Microphone);
                geolocation.Checked = rule != null && rule.Allowed.Contains(SitePermissionKind.Geolocation);
                notifications.Checked = rule != null && rule.Allowed.Contains(SitePermissionKind.Notifications);
                bool hasSelection = selected != null;
                camera.Enabled = microphone.Enabled = geolocation.Enabled = notifications.Enabled = hasSelection;
                saveButton.Enabled = removeButton.Enabled = hasSelection;
            };
            sites.SelectedIndexChanged += delegate { loadSelection(); };
            currentButton.Click += delegate
            {
                var core = _activeTab == null || _activeTab.WebView == null ? null : _activeTab.WebView.CoreWebView2;
                string origin = core == null ? null : SitePermissionStorage.NormalizeSecureOrigin(core.Source);
                if (origin == null)
                {
                    MessageBox.Show(dialog, "当前页面不是 HTTPS 站点。请打开 HTTPS 页面，或输入一个 HTTPS 来源。", "站点权限",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                originBox.Text = origin;
            };
            addButton.Click += delegate
            {
                string origin = SitePermissionStorage.NormalizeSecureOrigin(originBox.Text.Trim());
                if (origin == null)
                {
                    MessageBox.Show(dialog, "请输入有效的 HTTPS 站点地址。", "站点权限",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (!rules.Any(item => String.Equals(item.Origin, origin, StringComparison.OrdinalIgnoreCase)))
                    rules.Add(new SitePermissionRule { Origin = origin });
                refresh();
                sites.SelectedItem = origin;
                loadSelection();
            };
            saveButton.Click += delegate
            {
                var origin = sites.SelectedItem as string;
                var rule = origin == null ? null : rules.FirstOrDefault(item =>
                    String.Equals(item.Origin, origin, StringComparison.OrdinalIgnoreCase));
                if (rule == null)
                    return;
                rule.Allowed.Clear();
                if (camera.Checked) rule.Allowed.Add(SitePermissionKind.Camera);
                if (microphone.Checked) rule.Allowed.Add(SitePermissionKind.Microphone);
                if (geolocation.Checked) rule.Allowed.Add(SitePermissionKind.Geolocation);
                if (notifications.Checked) rule.Allowed.Add(SitePermissionKind.Notifications);
                if (rule.Allowed.Count == 0)
                    rules.Remove(rule);
                try
                {
                    SitePermissionStorage.Write(SitePermissionsPath, rules);
                    _sitePermissionRules = SitePermissionStorage.Read(SitePermissionsPath);
                    _statusLabel.Text = "站点权限已保存；未明确允许的权限仍会被拒绝。";
                    refresh();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(dialog, "无法保存站点权限：" + ex.Message, "站点权限",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            removeButton.Click += delegate
            {
                var origin = sites.SelectedItem as string;
                if (origin == null)
                    return;
                rules.RemoveAll(item => String.Equals(item.Origin, origin, StringComparison.OrdinalIgnoreCase));
                try
                {
                    SitePermissionStorage.Write(SitePermissionsPath, rules);
                    _sitePermissionRules = SitePermissionStorage.Read(SitePermissionsPath);
                    refresh();
                    loadSelection();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(dialog, "无法删除站点权限：" + ex.Message, "站点权限",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            refresh();
            loadSelection();
            ApplyDialogStyle(dialog);
            dialog.ShowDialog(this);
        }

        private void ShowSiteAllowlistDialog()
        {
            var dialog = new Form
            {
                Text = "站点允许列表",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new System.Drawing.Size(480, 390),
                Font = Font
            };
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(18, 14, 18, 14),
                BackColor = Color.FromArgb(249, 251, 255)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            var hint = new Label
            {
                Text = "允许列表中的站点及其子域名绕过随附的广告/跟踪域名表。",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(92, 108, 126)
            };
            var inputRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
            inputRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            var hostBox = new TextBox { Dock = DockStyle.Fill, MaxLength = 253, Margin = new Padding(0, 2, 8, 2) };
            SetCueBanner(hostBox, "输入域名，例如 example.com");
            var add = new Button { Text = "添加域名", Dock = DockStyle.Fill };
            inputRow.Controls.Add(hostBox, 0, 0);
            inputRow.Controls.Add(add, 1, 0);
            var current = new Button
            {
                Text = "允许当前网页及其子域名",
                Width = 230,
                Height = 30,
                Anchor = AnchorStyles.Left
            };
            var currentRow = new Panel { Dock = DockStyle.Fill };
            currentRow.Controls.Add(current);
            var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 5, 0, 0)
            };
            var close = new Button { Text = "完成", Width = 84, Height = 30, DialogResult = DialogResult.Cancel };
            var remove = new Button { Text = "移除所选", Width = 96, Height = 30 };
            footer.Controls.Add(close);
            footer.Controls.Add(remove);
            layout.Controls.Add(hint, 0, 0);
            layout.Controls.Add(inputRow, 0, 1);
            layout.Controls.Add(currentRow, 0, 2);
            layout.Controls.Add(list, 0, 3);
            layout.Controls.Add(footer, 0, 4);
            dialog.Controls.Add(layout);
            dialog.CancelButton = close;
            dialog.AcceptButton = add;

            Action refresh = delegate
            {
                list.BeginUpdate();
                list.Items.Clear();
                foreach (var host in _allowedHosts.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
                    list.Items.Add(host);
                list.EndUpdate();
            };
            Action<string> addHost = delegate(string value)
            {
                var host = NormalizeHost(value);
                if (host.Length == 0)
                {
                    MessageBox.Show(dialog, "请输入有效的域名或 IP 地址（不含协议、路径或端口）。", "站点允许列表",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (_allowedHosts.Add(host))
                    SavePreferences();
                refresh();
                hostBox.Clear();
            };
            add.Click += delegate { addHost(hostBox.Text); };
            hostBox.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode == Keys.Enter)
                {
                    args.SuppressKeyPress = true;
                    addHost(hostBox.Text);
                }
            };
            current.Click += delegate
            {
                var source = _activeTab == null || _activeTab.WebView == null || _activeTab.WebView.CoreWebView2 == null
                    ? null
                    : _activeTab.WebView.CoreWebView2.Source;
                Uri uri;
                if (String.IsNullOrWhiteSpace(source) || !Uri.TryCreate(source, UriKind.Absolute, out uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    MessageBox.Show(dialog, "当前标签页没有可加入允许列表的 HTTP 或 HTTPS 网站。", "站点允许列表",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                addHost(uri.Host);
            };
            remove.Click += delegate
            {
                var host = list.SelectedItem as string;
                if (host == null)
                    return;
                _allowedHosts.Remove(host);
                SavePreferences();
                refresh();
            };
            refresh();
            ApplyDialogStyle(dialog);
            dialog.Shown += delegate { hostBox.Focus(); };
            dialog.ShowDialog(this);
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
                var notes = ReadInstalledReleaseNotes(noticeVersion);
                MessageBox.Show(this, UpdateNoticeContent.BuildMessage(noticeVersion, notes),
                    "CHENMU Browser 更新完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch { }
        }

        private string ReadInstalledReleaseNotes(Version noticeVersion)
        {
            try
            {
                var path = Path.Combine(Application.StartupPath, "RELEASE_NOTES.md");
                if (!File.Exists(path))
                    return String.Empty;
                return UpdateNoticeContent.ReadCurrentReleaseNotes(
                    File.ReadAllText(path, Encoding.UTF8), noticeVersion);
            }
            catch { return String.Empty; }
        }
    }
}
