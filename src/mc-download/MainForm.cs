using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace McDownload
{
    public sealed class MainForm : Form
    {
        readonly McDownloader _downloader = new McDownloader();
        readonly ComboBox _versionBox;
        readonly CheckBox _snap;
        readonly TextBox _dirBox;
        readonly Button _browse;
        readonly Button _download;
        readonly ProgressBar _progress;
        readonly Label _status;
        readonly TextBox _log;
        readonly ToolTip _tips = new ToolTip();
        Dictionary<string, object> _manifest;
        CancellationTokenSource _cts;
        CancellationTokenSource _loadCts;
        UiMode _mode;
        bool _filling;

        public MainForm() : this(true, true)
        {
        }

        internal MainForm(bool loadManifest, bool useSavedDir)
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = UiFont();
            Text = "原版 Minecraft 下载器";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 520);
            MinimumSize = new Size(640, 440);

            var table = new TableLayoutPanel();
            table.Dock = DockStyle.Fill;
            table.ColumnCount = 3;
            table.RowCount = 5;
            table.Padding = new Padding(12);
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var versionLabel = new Label();
            versionLabel.Text = "游戏版本";
            versionLabel.AutoSize = true;
            versionLabel.Anchor = AnchorStyles.Left;
            versionLabel.Margin = new Padding(6, 8, 6, 8);

            _versionBox = new ComboBox();
            _versionBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _versionBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _versionBox.Margin = new Padding(6, 8, 6, 8);
            _versionBox.IntegralHeight = false;
            _versionBox.MaxDropDownItems = 16;

            _snap = new CheckBox();
            _snap.Text = "包含快照及旧版";
            _snap.AutoSize = true;
            _snap.Anchor = AnchorStyles.Left;
            _snap.Margin = new Padding(6, 8, 6, 8);
            _snap.CheckedChanged += OnSnapChanged;

            var dirLabel = new Label();
            dirLabel.Text = "下载目录";
            dirLabel.AutoSize = true;
            dirLabel.Anchor = AnchorStyles.Left;
            dirLabel.Margin = new Padding(6, 8, 6, 8);

            _dirBox = new TextBox();
            _dirBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _dirBox.Margin = new Padding(6, 8, 6, 8);

            _browse = new Button();
            _browse.Text = "浏览…";
            _browse.AutoSize = true;
            _browse.MinimumSize = new Size(88, 28);
            _browse.Anchor = AnchorStyles.Left;
            _browse.Margin = new Padding(6, 8, 6, 8);
            _browse.Click += OnBrowse;

            _download = new Button();
            _download.Text = "开始下载";
            _download.AutoSize = true;
            _download.MinimumSize = new Size(112, 32);
            _download.Anchor = AnchorStyles.Left;
            _download.Margin = new Padding(6, 8, 6, 8);
            _download.Click += OnDownloadClick;

            _progress = new ProgressBar();
            _progress.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _progress.Margin = new Padding(6, 12, 6, 12);
            _progress.Height = 22;
            _progress.Minimum = 0;
            _progress.Maximum = 100;

            _status = new Label();
            _status.AutoSize = false;
            _status.AutoEllipsis = true;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Dock = DockStyle.Fill;
            _status.Margin = new Padding(6, 0, 6, 4);
            _status.Text = "就绪";

            _log = new TextBox();
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.WordWrap = true;
            _log.BackColor = SystemColors.Window;
            _log.Dock = DockStyle.Fill;
            _log.Margin = new Padding(6, 4, 6, 6);

            table.Controls.Add(versionLabel, 0, 0);
            table.Controls.Add(_versionBox, 1, 0);
            table.Controls.Add(_snap, 2, 0);
            table.Controls.Add(dirLabel, 0, 1);
            table.Controls.Add(_dirBox, 1, 1);
            table.Controls.Add(_browse, 2, 1);
            table.Controls.Add(_download, 0, 2);
            table.Controls.Add(_progress, 1, 2);
            table.SetColumnSpan(_progress, 2);
            table.Controls.Add(_status, 0, 3);
            table.SetColumnSpan(_status, 3);
            table.Controls.Add(_log, 0, 4);
            table.SetColumnSpan(_log, 3);
            Controls.Add(table);

            AcceptButton = _download;
            _tips.SetToolTip(_snap, "默认只列出正式版。勾选后同时列出快照、旧 beta 和 alpha。");
            _tips.SetToolTip(_download, "下载到所选文件夹。已经通过 SHA1 校验的文件会跳过。");

            var saved = useSavedDir ? SettingsStore.LoadGameDir() : null;
            _dirBox.Text = string.IsNullOrWhiteSpace(saved) ? McDownloader.DefaultGameDir() : saved;

            if (loadManifest)
            {
                AppendLog("原版 Minecraft 下载器 " + McDownloader.Version);
                AppendLog("只把游戏文件写入所选目录。已经通过 SHA1 校验的文件会跳过，可以重复下载补齐。");
                BeginLoadManifest();
            }
            else
            {
                SetMode(UiMode.Ready);
            }
        }

        internal string DownloadButtonText { get { return _download.Text; } }
        internal string GameDirText { get { return _dirBox.Text; } }
        internal bool IncludeSnapshots { get { return _snap.Checked; } set { _snap.Checked = value; } }
        internal bool LogIsReadOnlyMultiline { get { return _log.ReadOnly && _log.Multiline && _log.ScrollBars == ScrollBars.Vertical; } }
        internal string SelectedVersionId
        {
            get
            {
                var choice = _versionBox.SelectedItem as VersionChoice;
                return choice == null ? null : choice.Id;
            }
        }
        internal int VersionCount { get { return _versionBox.Items.Count; } }
        internal VersionChoice VersionAt(int index) { return (VersionChoice)_versionBox.Items[index]; }

        internal void LoadManifestJsonForTest(string json)
        {
            _manifest = JsonUtil.ParseObject(json);
            FillVersions();
            SetMode(UiMode.Ready);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_mode == UiMode.Busy && _cts != null) _cts.Cancel();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_cts != null) { _cts.Dispose(); _cts = null; }
                if (_loadCts != null) { _loadCts.Cancel(); _loadCts.Dispose(); _loadCts = null; }
                if (_tips != null) _tips.Dispose();
            }
            base.Dispose(disposing);
        }

        void BeginLoadManifest()
        {
            SetMode(UiMode.Loading);
            _status.Text = "正在获取版本列表…";
            if (_loadCts != null)
            {
                _loadCts.Cancel();
                _loadCts.Dispose();
            }
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;
            var downloader = _downloader;
            Task.Run(delegate
            {
                try
                {
                    var manifest = downloader.FetchManifest(token);
                    try { BeginInvoke(new Action(delegate { ApplyManifest(manifest); })); }
                    catch (InvalidOperationException) { }
                }
                catch (Exception ex)
                {
                    try { BeginInvoke(new Action(delegate { ApplyManifestError(ex); })); }
                    catch (InvalidOperationException) { }
                }
            });
        }

        void ApplyManifest(Dictionary<string, object> manifest)
        {
            if (IsDisposed) return;
            _manifest = manifest;
            FillVersions();
            SetMode(UiMode.Ready);
            var kind = _snap.Checked ? "版本" : "正式版";
            _status.Text = "已载入 " + _versionBox.Items.Count + " 个" + kind;
            AppendLog(_status.Text + "。");
        }

        void ApplyManifestError(Exception ex)
        {
            if (IsDisposed) return;
            AppendLog("获取版本列表失败：" + ex.Message);
            _status.Text = "获取版本列表失败";
            SetMode(UiMode.Failed);
        }

        void FillVersions()
        {
            if (_filling) return;
            _filling = true;
            try
            {
                var keep = SelectedVersionId;
                _versionBox.Items.Clear();
                if (_manifest == null) return;
                var latest = McDownloader.LatestId(_manifest, "release");
                var versions = McDownloader.ListVersions(_manifest);
                for (int i = 0; i < versions.Count; i++)
                {
                    var entry = versions[i];
                    if (!_snap.Checked && !string.Equals(entry.Type, "release", StringComparison.OrdinalIgnoreCase))
                        continue;
                    _versionBox.Items.Add(new VersionChoice(entry, string.Equals(entry.Id, latest, StringComparison.Ordinal)));
                }
                if (_versionBox.Items.Count == 0) return;
                var index = 0;
                if (!string.IsNullOrEmpty(keep))
                {
                    for (int i = 0; i < _versionBox.Items.Count; i++)
                    {
                        if (((VersionChoice)_versionBox.Items[i]).Id == keep) { index = i; break; }
                    }
                }
                else if (!string.IsNullOrEmpty(latest))
                {
                    for (int i = 0; i < _versionBox.Items.Count; i++)
                    {
                        if (((VersionChoice)_versionBox.Items[i]).Id == latest) { index = i; break; }
                    }
                }
                _versionBox.SelectedIndex = index;
            }
            finally
            {
                _filling = false;
            }
        }

        void OnSnapChanged(object sender, EventArgs e)
        {
            FillVersions();
            if (_mode == UiMode.Ready)
            {
                var kind = _snap.Checked ? "版本" : "正式版";
                _status.Text = "已载入 " + _versionBox.Items.Count + " 个" + kind;
            }
        }

        void OnBrowse(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择 .minecraft 目录";
                dialog.ShowNewFolderButton = true;
                var selected = AppDomain.CurrentDomain.BaseDirectory;
                try
                {
                    var resolved = McDownloader.ResolveGameDir(_dirBox.Text);
                    if (Directory.Exists(resolved)) selected = resolved;
                    else
                    {
                        var parent = Path.GetDirectoryName(resolved);
                        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) selected = parent;
                    }
                }
                catch (Exception) { }
                dialog.SelectedPath = selected;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _dirBox.Text = dialog.SelectedPath;
                try { SettingsStore.SaveGameDir(dialog.SelectedPath); }
                catch (Exception ex) { AppendLog("未能在程序旁边保存目录设置：" + ex.Message); }
            }
        }

        async void OnDownloadClick(object sender, EventArgs e)
        {
            if (_mode == UiMode.Busy)
            {
                if (_cts != null) _cts.Cancel();
                AppendLog("正在取消…");
                return;
            }
            if (_mode == UiMode.Failed || _mode == UiMode.Loading)
            {
                BeginLoadManifest();
                return;
            }

            var choice = _versionBox.SelectedItem as VersionChoice;
            if (choice == null)
            {
                MessageBox.Show(this, "请先选择一个版本。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string gameDir;
            try { gameDir = McDownloader.ResolveGameDir(_dirBox.Text); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _dirBox.Text = gameDir;
            try { SettingsStore.SaveGameDir(gameDir); }
            catch (Exception ex) { AppendLog("未能在程序旁边保存目录设置：" + ex.Message); }

            SetMode(UiMode.Busy);
            if (_cts != null) _cts.Dispose();
            _cts = new CancellationTokenSource();
            var log = new Progress<string>(AppendLog);
            var transfer = new Progress<TransferProgress>(OnTransfer);
            var versionId = choice.Id;
            var token = _cts.Token;
            try
            {
                var summary = await Task.Run(delegate
                {
                    return _downloader.Download(versionId, gameDir, McDownloader.DefaultThreads, token, log, transfer);
                });
                if (!IsDisposed) ShowSummary(summary);
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                AppendLog("下载失败：" + ex.Message);
                _status.Text = "下载失败";
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (!IsDisposed) SetMode(_manifest == null ? UiMode.Failed : UiMode.Ready);
            }
        }

        void ShowSummary(DownloadSummary summary)
        {
            if (summary.Canceled)
            {
                _status.Text = "已取消";
                MessageBox.Show(this, "下载已取消。已经下完的文件会保留，可以再次下载补齐。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (summary.Failed > 0)
            {
                _status.Text = "有文件下载失败";
                MessageBox.Show(this, "有 " + summary.Failed + " 个文件失败。再次点击「开始下载」会跳过已通过校验的文件，只补下缺失的部分。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _status.Text = "下载完成";
            if (_progress.Maximum > 0) _progress.Value = _progress.Maximum;
            MessageBox.Show(this, "全部文件 SHA1 校验通过。\r\n版本：" + summary.VersionId + "\r\n目录：" + summary.GameDir, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void OnTransfer(TransferProgress progress)
        {
            if (IsDisposed || progress.Total <= 0) return;
            _progress.Style = ProgressBarStyle.Continuous;
            if (_progress.Maximum != progress.Total)
            {
                _progress.Value = 0;
                _progress.Maximum = progress.Total;
            }
            var value = progress.Completed;
            if (value < _progress.Minimum) value = _progress.Minimum;
            if (value > _progress.Maximum) value = _progress.Maximum;
            _progress.Value = value;
            _status.Text = progress.Stage + "  " + progress.Completed + "/" + progress.Total;
        }

        void SetMode(UiMode mode)
        {
            _mode = mode;
            var editable = mode == UiMode.Ready || mode == UiMode.Failed;
            _versionBox.Enabled = editable;
            _snap.Enabled = editable;
            _dirBox.Enabled = editable;
            _browse.Enabled = editable;
            if (mode == UiMode.Busy) _download.Text = "取消";
            else if (mode == UiMode.Failed) _download.Text = "重试";
            else if (mode == UiMode.Loading) _download.Text = "正在获取…";
            else _download.Text = "开始下载";
            _download.Enabled = mode != UiMode.Loading;
            if (mode == UiMode.Busy) _progress.Style = ProgressBarStyle.Marquee;
            else
            {
                _progress.Style = ProgressBarStyle.Continuous;
                if (mode == UiMode.Loading || mode == UiMode.Failed) _progress.Value = 0;
            }
        }

        void AppendLog(string line)
        {
            if (line == null) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(AppendLog), line); }
                catch (InvalidOperationException) { }
                return;
            }
            _log.AppendText(line + Environment.NewLine);
            if (_log.TextLength > 200000)
                _log.Text = _log.Text.Substring(_log.TextLength - 150000);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }

        static Font UiFont()
        {
            try { return new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point); }
            catch (Exception) { return SystemFonts.MessageBoxFont; }
        }

        enum UiMode
        {
            Ready,
            Loading,
            Failed,
            Busy
        }

        internal sealed class VersionChoice
        {
            readonly VersionEntry _entry;
            readonly bool _latest;

            public VersionChoice(VersionEntry entry, bool latest)
            {
                _entry = entry;
                _latest = latest;
            }

            public string Id { get { return _entry.Id; } }

            public override string ToString()
            {
                var type = _entry.Type ?? "";
                if (type.Equals("release", StringComparison.OrdinalIgnoreCase))
                    return _latest ? _entry.Id + "  （最新正式版）" : _entry.Id;
                return _entry.Id + "  （" + TypeLabel(type) + "）";
            }

            static string TypeLabel(string type)
            {
                if (type.Equals("snapshot", StringComparison.OrdinalIgnoreCase)) return "快照";
                if (type.Equals("old_beta", StringComparison.OrdinalIgnoreCase)) return "旧 beta";
                if (type.Equals("old_alpha", StringComparison.OrdinalIgnoreCase)) return "旧 alpha";
                return type;
            }
        }
    }
}
