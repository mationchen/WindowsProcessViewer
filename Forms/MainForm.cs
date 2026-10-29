using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using WindowsProcessViewer.Models;
using WindowsProcessViewer.Services;

namespace WindowsProcessViewer.Forms;

/// <summary>
/// 主界面：进程列表（上方）+ 详细信息 / AI 分析（下方）。
/// 界面文案全部中文，列宽与字体可在 appsettings.json 中调整。
/// </summary>
public sealed class MainForm : Form
{
    private static readonly Color HeaderBack = Color.FromArgb(240, 243, 248);
    private static readonly Color RiskRed = Color.FromArgb(192, 0, 0);
    private static readonly Color RiskOrange = Color.FromArgb(196, 90, 0);

    private readonly AppCache? _cache;
    private readonly ProcessCollector _collector;
    private readonly ProcessAnalyzer _analyzer;
    private readonly AiService _ai = new();
    private readonly SortableBindingList<ProcessRecord> _view = new();
    private readonly System.Windows.Forms.Timer _autoRefresh = new();
    private readonly ToolTip _toolTip = new();

    private AppSettings _settings;
    private List<ProcessRecord> _all = [];
    private bool _refreshing;
    private bool _aiBusy;
    private CancellationTokenSource? _aiCts;
    private string _lastAiModel = "";
    private Font? _boldFont;

    private DataGridView _grid = null!;
    private SplitContainer _split = null!;
    private RichTextBox _detailBox = null!;
    private RichTextBox _aiBox = null!;
    private TabControl _tabs = null!;
    private TabPage _tabAi = null!;
    private TextBox _searchBox = null!;
    private CheckBox _chkThirdParty = null!;
    private CheckBox _chkRisky = null!;
    private CheckBox _chkHideSystem = null!;
    private CheckBox _chkCloseSafe = null!;
    private CheckBox _chkCloseCaution = null!;
    private CheckBox _chkCloseKeep = null!;
    private CheckBox _chkCloseForbidden = null!;
    private ComboBox _cboInterval = null!;
    private Button _btnRefresh = null!;
    private Button _btnAiSelected = null!;
    private Button _btnAiList = null!;
    private Button _btnPingAi = null!;
    private Button _btnCache = null!;
    private ToolStripStatusLabel _statusText = null!;
    private ToolStripStatusLabel _statusCount = null!;
    private ToolStripStatusLabel _statusAi = null!;
    private ToolStripStatusLabel _statusPrivilege = null!;
    private ToolStripProgressBar _aiProgress = null!;
    private string? _sortColumn;
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    public MainForm(AppSettings settings, string? startupError, Rectangle? startArea = null, AppCache? cache = null)
    {
        _settings = settings;
        _cache = cache;
        _collector = new ProcessCollector(cache);
        _analyzer = new ProcessAnalyzer(_collector.SelfElevated, cache);

        BuildUi();
        ApplySettingsToUi();
        ApplyStartArea(startArea);

        Shown += (_, _) =>
        {
            AdjustSplitter();
            _ = RefreshAsync();
        };

        _autoRefresh.Tick += (_, _) =>
        {
            if (!_refreshing && !_aiBusy)
                _ = RefreshAsync();
        };

        if (!string.IsNullOrWhiteSpace(startupError))
        {
            BeginInvoke(() => MessageBox.Show(this, startupError, "配置提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }
    }

    // ============================================================ 界面搭建
    private void BuildUi()
    {
        Text = "Windows 进程查看器 — 进程来源 / 作用 / 能否关闭";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1480, 920);
        MinimumSize = new Size(1080, 640);
        Font = new Font(_settings.Ui.FontName, _settings.Ui.FontSize);
        KeyPreview = true;

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 6, 8, 4),
            WrapContents = true,
            BackColor = Color.White,
        };

        _btnRefresh = MakeButton("刷新 (F5)", (_, _) => _ = RefreshAsync());
        _cboInterval = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 96,
            Margin = new Padding(4, 3, 8, 2),
        };
        _cboInterval.Items.AddRange(["手动刷新", "5 秒", "10 秒", "30 秒", "60 秒", "5 分钟"]);
        _cboInterval.SelectedIndexChanged += (_, _) => ApplyRefreshInterval();

        _searchBox = new TextBox
        {
            Width = 230,
            Margin = new Padding(4, 3, 8, 2),
            PlaceholderText = "搜索进程名 / PID / 厂商 / 路径",
        };
        _searchBox.TextChanged += (_, _) => ApplyFilter();

        _chkThirdParty = MakeCheck("只看第三方/非系统", (_, _) => ApplyFilter());
        _chkRisky = MakeCheck("只看可疑/高风险", (_, _) => ApplyFilter());
        _chkHideSystem = MakeCheck("隐藏系统进程", (_, _) => ApplyFilter());

        var closeLabel = new Label
        {
            Text = "关闭建议：",
            AutoSize = true,
            Margin = new Padding(10, 8, 0, 2),
            ForeColor = Color.DimGray,
        };
        _chkCloseSafe = MakeCheck("可以关闭", (_, _) => ApplyFilter());
        _chkCloseCaution = MakeCheck("谨慎关闭", (_, _) => ApplyFilter());
        _chkCloseKeep = MakeCheck("建议保留", (_, _) => ApplyFilter());
        _chkCloseForbidden = MakeCheck("禁止关闭", (_, _) => ApplyFilter());
        _chkCloseSafe.ForeColor = Color.FromArgb(0, 110, 60);
        _chkCloseForbidden.ForeColor = RiskRed;

        _btnAiSelected = MakeButton("AI 分析选中", (_, _) => _ = RunAiForSelectionAsync());
        _btnAiList = MakeButton("AI 分析当前列表", (_, _) => _ = RunAiForListAsync());
        _btnPingAi = MakeButton("测试 AI 连接", (_, _) => _ = PingAiAsync());

        var btnOpenConfig = MakeButton("打开配置文件", (_, _) => OpenConfigFile());
        var btnReloadConfig = MakeButton("重新加载配置", (_, _) => ReloadConfig());
        var btnExport = MakeButton("导出 CSV", (_, _) => ExportCsv());
        var btnHelp = MakeButton("说明", (_, _) => ShowHelp());
        _btnCache = MakeButton("缓存", (_, _) => ManageCache());

        top.Controls.AddRange([
            _btnRefresh, _cboInterval, _searchBox,
            _chkThirdParty, _chkRisky, _chkHideSystem,
            closeLabel, _chkCloseSafe, _chkCloseCaution, _chkCloseKeep, _chkCloseForbidden,
            _btnAiSelected, _btnAiList, _btnPingAi,
            btnOpenConfig, btnReloadConfig, btnExport, _btnCache, btnHelp,
        ]);

        _grid = new BufferedGrid
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AllowUserToOrderColumns = true,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            ColumnHeadersHeight = 34,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            EnableHeadersVisualStyles = false,
            DataSource = _view,
        };
        _grid.RowTemplate.Height = 24;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = HeaderBack;
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = HeaderBack;
        _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 120, 215);
        _grid.DefaultCellStyle.SelectionForeColor = Color.White;
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 250, 251);

        AddColumn("Name", "进程名", 140);
        AddColumn("Pid", "PID", 55, alignment: DataGridViewContentAlignment.MiddleRight);
        AddColumn("Software", "所属软件 / 厂商", 150);
        AddColumn("Purpose", "作用说明", 300, fill: true);
        AddColumn("LaunchedBy", "谁启动的", 200);
        AddColumn("CanClose", "能否关闭", 85);
        AddColumn("CloseMethod", "如何关闭", 190);
        AddColumn("Risk", "风险", 52, alignment: DataGridViewContentAlignment.MiddleCenter);
        AddColumn("WorkingSetMb", "内存(MB)", 86, "N1", DataGridViewContentAlignment.MiddleRight);
        AddColumn("CpuPercent", "CPU%", 62, "N1", DataGridViewContentAlignment.MiddleRight);
        AddColumn("Owner", "运行账户", 120);
        AddColumn("Category", "类别", 90);
        AddColumn("AiSummary", "AI 结论", 140);
        AddColumn("CommandLineSummary", "命令行（判断启动程序用）", 200);

        // 进程名列固定在最左，横向滚动时不会丢；作用说明作为自适应列，给最小宽度避免被挤成几像素
        if (_grid.Columns["Name"] is { } nameColumn)
            nameColumn.Frozen = true;
        if (_grid.Columns["Purpose"] is { } purposeColumn)
            purposeColumn.MinimumWidth = 180;

        _grid.CellFormatting += OnCellFormatting;
        _grid.SelectionChanged += (_, _) => UpdateDetail();
        _grid.ColumnHeaderMouseClick += OnColumnHeaderMouseClick;
        _grid.ContextMenuStrip = BuildContextMenu();

        _detailBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 9.5f),
            WordWrap = true,
            DetectUrls = false,
        };

        _aiBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 9.5f),
            WordWrap = true,
            DetectUrls = false,
        };

        var aiPanel = new Panel { Dock = DockStyle.Fill };
        var aiBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(4, 3, 4, 3),
        };
        aiBar.Controls.Add(MakeButton("分析选中进程", (_, _) => _ = RunAiForSelectionAsync()));
        aiBar.Controls.Add(MakeButton("分析当前列表", (_, _) => _ = RunAiForListAsync()));
        aiPanel.Controls.Add(_aiBox);
        aiPanel.Controls.Add(aiBar);

        _tabs = new TabControl { Dock = DockStyle.Fill };
        var tabDetail = new TabPage("详细信息");
        tabDetail.Controls.Add(_detailBox);
        _tabAi = new TabPage("AI 分析");
        _tabAi.Controls.Add(aiPanel);
        _tabs.TabPages.Add(tabDetail);
        _tabs.TabPages.Add(_tabAi);

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            FixedPanel = FixedPanel.None,
        };
        _split.Panel1.Controls.Add(_grid);
        _split.Panel2.Controls.Add(_tabs);

        var status = new StatusStrip { SizingGrip = true };
        _statusText = new ToolStripStatusLabel
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "就绪",
        };
        _statusCount = new ToolStripStatusLabel { Text = "进程 0" };
        _statusAi = new ToolStripStatusLabel { Text = "AI：未配置" };
        _statusPrivilege = new ToolStripStatusLabel
        {
            Text = _collector.SelfElevated ? "已提权（管理员）" : "未提权（部分进程信息不可见）",
            ForeColor = _collector.SelfElevated ? Color.DarkGreen : RiskOrange,
        };
        _aiProgress = new ToolStripProgressBar
        {
            Visible = false,
            Width = 180,
            Minimum = 0,
            Maximum = 100,
            Style = ProgressBarStyle.Continuous,
            Alignment = ToolStripItemAlignment.Left,
        };
        status.Items.AddRange([_statusText, _aiProgress, _statusCount, _statusAi, _statusPrivilege]);

        Controls.Add(_split);
        Controls.Add(status);
        Controls.Add(top);

        UpdateAiIndicator();
        UpdateCacheIndicator();
    }

    private void AddColumn(string property, string header, int width, string? format = null,
        DataGridViewContentAlignment alignment = DataGridViewContentAlignment.MiddleLeft, bool fill = false)
    {
        var column = new DataGridViewTextBoxColumn
        {
            DataPropertyName = property,
            HeaderText = header,
            Name = property,
            Width = width,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            ReadOnly = true,
            Tag = header,
            DefaultCellStyle = { Alignment = alignment },
        };
        if (!string.IsNullOrEmpty(format))
            column.DefaultCellStyle.Format = format;
        if (fill)
            column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _grid.Columns.Add(column);
    }

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 28),
            Padding = new Padding(8, 2, 8, 2),
            Margin = new Padding(4, 2, 4, 2),
            UseVisualStyleBackColor = true,
        };
        button.Click += onClick;
        return button;
    }

    private static CheckBox MakeCheck(string text, EventHandler onClick)
    {
        var check = new CheckBox
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(6, 7, 4, 2),
        };
        check.CheckedChanged += onClick;
        return check;
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("复制详细信息", null, (_, _) => CopyDetail());
        menu.Items.Add("打开文件所在位置", null, (_, _) => OpenFileLocation());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("结束进程…", null, (_, _) => KillSelected());
        menu.Items.Add("打开“服务”管理器", null, (_, _) => OpenServicesConsole());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("刷新列表 (F5)", null, (_, _) => _ = RefreshAsync());

        menu.Opening += (_, _) =>
        {
            var record = SelectedRecord();
            var canKill = record is not null &&
                          !ProcessTerminator.IsBlocked(record, Environment.ProcessId, out _);
            menu.Items[3].Enabled = canKill && _settings.Ui.ShowKillMenu;
        };
        return menu;
    }

    private void ApplySettingsToUi()
    {
        _chkThirdParty.Checked = _settings.Ui.OnlyThirdParty;
        _chkRisky.Checked = _settings.Ui.OnlyRisky;
        _chkHideSystem.Checked = _settings.Ui.HideMicrosoftProcesses;

        var index = _settings.Ui.AutoRefreshSeconds switch
        {
            <= 0 => 0,
            <= 5 => 1,
            <= 10 => 2,
            <= 30 => 3,
            <= 60 => 4,
            _ => 5,
        };
        _cboInterval.SelectedIndex = index;
        ApplyRefreshInterval();
        UpdateAiIndicator();
    }

    private void ApplyRefreshInterval()
    {
        var seconds = _cboInterval.SelectedIndex switch
        {
            1 => 5,
            2 => 10,
            3 => 30,
            4 => 60,
            5 => 300,
            _ => 0,
        };

        _autoRefresh.Stop();
        if (seconds <= 0)
            return;

        _autoRefresh.Interval = seconds * 1000;
        _autoRefresh.Start();
    }

    /// <summary>
    /// 指定启动显示器（--screen N，按 Screen.AllScreens 顺序，从 1 开始）。
    /// 用于多显示器环境：把窗口放在指定屏幕中间，不覆盖主屏。
    /// </summary>
    private void ApplyStartArea(Rectangle? area)
    {
        if (area is not { } bounds || bounds.Width < 400 || bounds.Height < 300)
            return;

        StartPosition = FormStartPosition.Manual;
        var width = Math.Min(Width, bounds.Width - 40);
        var height = Math.Min(Height, bounds.Height - 40);
        Size = new Size(width, height);
        Location = new Point(bounds.X + (bounds.Width - width) / 2,
                             bounds.Y + (bounds.Height - height) / 2);
    }

    private void AdjustSplitter()
    {
        if (_split.Height > 320)
            _split.SplitterDistance = (int)(_split.Height * 0.6);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F5)
        {
            _ = RefreshAsync();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ============================================================ 采集与过滤
    private async Task RefreshAsync()
    {
        if (_refreshing)
            return;

        _refreshing = true;
        SetBusy(true, "正在采集进程信息…");
        try
        {
            var list = await Task.Run(() =>
            {
                var items = _collector.Collect();
                _analyzer.AnalyzeAll(items);
                return items;
            }).ConfigureAwait(true);

            if (IsDisposed || Disposing)
                return;

            _cache?.Flush();
            UpdateCacheIndicator();
            _all = list;
            ApplyFilter();
            _statusText.Text = $"刷新完成 {DateTime.Now:HH:mm:ss}（{list.Count} 个进程）";
        }
        catch (Exception ex)
        {
            Log.Error("刷新进程列表失败", ex);
            if (IsDisposed || Disposing)
                return;

            _statusText.Text = "刷新失败：" + ex.Message;
            MessageBox.Show(this, ex.Message, "刷新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _refreshing = false;
            SetBusy(false, null);
        }
    }

    private void ApplyFilter()
    {
        var keyword = _searchBox.Text.Trim();
        IEnumerable<ProcessRecord> query = _all;

        if (_chkHideSystem.Checked)
            query = query.Where(r => !IsSystemCategory(r.Category));

        if (_chkThirdParty.Checked)
            query = query.Where(r => !r.SignatureStatus.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                                     !IsSystemCategory(r.Category));

        if (_chkRisky.Checked)
            query = query.Where(r => r.Category == "可疑程序" ||
                                     r.Risk is "高" or "极高" ||
                                     r.AiRisk is "高" or "极高");

        var closeStates = SelectedCloseStates();
        if (closeStates.Count > 0)
            query = query.Where(r => closeStates.Contains(r.CanClose));

        if (keyword.Length > 0)
        {
            query = query.Where(r =>
                Contains(r.Name, keyword) ||
                r.Pid.ToString().Contains(keyword, StringComparison.Ordinal) ||
                Contains(r.Software, keyword) ||
                Contains(r.Purpose, keyword) ||
                Contains(r.Path, keyword) ||
                Contains(r.CommandLine, keyword) ||
                Contains(r.Owner, keyword) ||
                Contains(r.Category, keyword) ||
                Contains(r.AiSummary, keyword));
        }

        var filtered = query.ToList();
        var selectedPid = SelectedRecord()?.Pid;

        _view.RaiseListChangedEvents = false;
        _view.Clear();
        foreach (var record in filtered)
            _view.Add(record);
        _view.RaiseListChangedEvents = true;
        _view.ResetBindings();

        ApplyCurrentSort();

        _statusCount.Text = $"显示 {filtered.Count} / 共 {_all.Count}";
        if (selectedPid is int pid)
            SelectPid(pid);

        UpdateDetail();
    }

    private List<string> SelectedCloseStates()
    {
        var states = new List<string>(4);
        if (_chkCloseSafe.Checked) states.Add("可以关闭");
        if (_chkCloseCaution.Checked) states.Add("谨慎关闭");
        if (_chkCloseKeep.Checked) states.Add("建议保留");
        if (_chkCloseForbidden.Checked) states.Add("禁止关闭");
        return states;
    }

    /// <summary>点击列头：切换升/降序，并显示三角标识（不依赖 DataGridView 自带排序）。</summary>
    private void OnColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.ColumnIndex < 0 || e.ColumnIndex >= _grid.Columns.Count)
            return;

        var column = _grid.Columns[e.ColumnIndex];
        var propertyName = column.DataPropertyName;
        if (string.IsNullOrEmpty(propertyName))
            return;

        var ascending = !string.Equals(_sortColumn, propertyName, StringComparison.Ordinal) ||
                        _sortDirection == ListSortDirection.Descending;

        _sortColumn = propertyName;
        _sortDirection = ascending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        ApplyCurrentSort();
        _statusText.Text = $"已按“{column.HeaderText}”{(ascending ? "升序" : "降序")}排列";
        UpdateDetail();
    }

    private void ApplyCurrentSort()
    {
        if (string.IsNullOrEmpty(_sortColumn))
            return;

        var property = TypeDescriptor.GetProperties(typeof(ProcessRecord))[_sortColumn];
        if (property is null)
            return;

        _view.SortBy(property, _sortDirection);

        // 表头文字加 ▲/▼：某些视觉样式下 DataGridView 自带的排序三角不渲染，文字标识更可靠
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            column.HeaderCell.SortGlyphDirection = SortOrder.None;
            if (column.Tag is string baseText)
                column.HeaderText = baseText.TrimEnd('▲', '▼', ' ');
        }

        var target = _grid.Columns
            .Cast<DataGridViewColumn>()
            .FirstOrDefault(c => string.Equals(c.DataPropertyName, _sortColumn, StringComparison.Ordinal));
        if (target is not null)
        {
            var ascending = _sortDirection == ListSortDirection.Ascending;
            target.HeaderCell.SortGlyphDirection = ascending ? SortOrder.Ascending : SortOrder.Descending;
            var baseText = target.Tag as string ?? target.HeaderText.TrimEnd('▲', '▼', ' ');
            target.HeaderText = baseText + (ascending ? " ▲" : " ▼");
        }
    }

    private void SelectPid(int pid)
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.DataBoundItem is ProcessRecord record && record.Pid == pid)
            {
                row.Selected = true;
                return;
            }
        }
    }

    private static bool Contains(string? text, string keyword)
        => !string.IsNullOrEmpty(text) && text.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    private static bool IsSystemCategory(string category)
        => category is "系统核心" or "Windows 子系统" or "Windows 服务" or "Windows 更新" or "Windows 组件";

    private void SetBusy(bool busy, string? status)
    {
        _btnRefresh.Enabled = !busy;
        UseWaitCursor = busy;
        if (!string.IsNullOrEmpty(status))
            _statusText.Text = status;
    }

    private ProcessRecord? SelectedRecord()
        => _grid.CurrentRow?.DataBoundItem as ProcessRecord;

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count)
            return;

        var record = _grid.Rows[e.RowIndex].DataBoundItem as ProcessRecord;
        if (record is null)
            return;

        var columnName = _grid.Columns[e.ColumnIndex].Name;
        if (columnName == "Risk")
        {
            e.CellStyle.ForeColor = record.Risk switch
            {
                "极高" => RiskRed,
                "高" => RiskOrange,
                _ => Color.Black,
            };
            e.CellStyle.Font = _boldFont ??= new Font(_grid.Font, FontStyle.Bold);
        }
        else if (columnName == "CanClose")
        {
            e.CellStyle.ForeColor = record.CanClose switch
            {
                "禁止关闭" => RiskRed,
                "建议保留" => RiskOrange,
                "可以关闭" => Color.FromArgb(0, 110, 60),
                _ => Color.Black,
            };
        }
        else if (columnName == "Name" && record.IsCurrentProcess)
        {
            e.CellStyle.Font = _boldFont ??= new Font(_grid.Font, FontStyle.Bold);
        }
    }

    // ============================================================ 详情展示
    private void UpdateDetail()
    {
        var record = SelectedRecord();
        if (record is null)
        {
            _detailBox.Text = "请选择一个进程查看详细信息。";
            _aiBox.Text = "请选择一个进程后再做 AI 分析。";
            return;
        }

        _detailBox.Text = BuildDetailText(record);
        _aiBox.Text = BuildAiText(record);
    }

    private static string BuildDetailText(ProcessRecord r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"进程名：{r.Name}    PID：{r.Pid}");
        sb.AppendLine($"所属软件：{r.Software}");
        sb.AppendLine($"作用说明：{r.Purpose}");
        sb.AppendLine($"谁启动的：{r.LaunchedBy}");
        if (!string.IsNullOrWhiteSpace(r.LaunchChain))
            sb.AppendLine($"进程链：{r.LaunchChain}");
        if (!string.IsNullOrWhiteSpace(r.LaunchEvidence))
            sb.AppendLine($"判断依据：{r.LaunchEvidence}");
        sb.AppendLine($"能否关闭：{r.CanClose}");
        sb.AppendLine($"如何关闭：{r.CloseMethod}");
        sb.AppendLine($"风险等级：{r.Risk}    类别：{r.Category}    判断来源：{r.AnalyzeSource}");
        sb.AppendLine();
        sb.AppendLine("──── 运行信息 ────");
        sb.AppendLine($"文件路径：{(string.IsNullOrWhiteSpace(r.Path) ? "（无法读取，可能权限不足）" : r.Path)}");
        sb.AppendLine($"命令行：{(string.IsNullOrWhiteSpace(r.CommandLine) ? "（无法读取）" : r.CommandLine)}");
        sb.AppendLine($"运行账户：{(string.IsNullOrWhiteSpace(r.Owner) ? "（无法读取）" : r.Owner)}");
        sb.AppendLine($"管理员权限：{(r.IsElevated ? "是" : "否")}    会话：{r.SessionId}");
        sb.AppendLine($"父进程：{(string.IsNullOrWhiteSpace(r.ParentName) ? "（未知）" : r.ParentName)} (PID {r.ParentPid})");
        sb.AppendLine($"内存：工作集 {r.WorkingSetMb:F1} MB / 专用 {r.PrivateMb:F1} MB    线程：{r.ThreadCount}    CPU：{r.CpuPercent:F1}%");
        sb.AppendLine($"启动时间：{(r.StartTime is { } time ? time.ToString("yyyy-MM-dd HH:mm:ss") : "（无法读取）")}");
        sb.AppendLine($"所属服务：{(r.IsService ? $"{r.ServiceDisplayName}（{r.ServiceName}）" : "无")}");
        sb.AppendLine($"开机自启：{(string.IsNullOrWhiteSpace(r.Autostart) ? "未检测到" : r.Autostart)}");
        sb.AppendLine($"数字签名：{r.SignatureStatus}");
        sb.AppendLine($"厂商 / 产品：{r.Company} / {r.ProductName}    文件版本：{r.FileVersion}");
        sb.AppendLine();
        sb.AppendLine("──── 备注 ────");
        sb.AppendLine(r.Notes);

        if (r.HasAi)
        {
            sb.AppendLine();
            sb.AppendLine("──── AI 判断 ────");
            sb.AppendLine(r.AiDetail);
        }

        return sb.ToString();
    }

    private string BuildAiText(ProcessRecord r)
    {
        if (!r.HasAi)
        {
            return "尚未对该进程做 AI 分析。\n\n" +
                   "点击下方“分析选中进程”即可让 MiniMax 给出判断；\n" +
                   "也可以点击“分析当前列表”批量分析（按 appsettings.json 中的 MaxProcessesPerRequest 分批）。\n" +
                   "分析结果会自动写入本地缓存 cache.db，下次打开同类程序直接复用、不重复花钱。\n\n" +
                   $"当前模型：{_settings.MiniMax.Model}\n" +
                   $"接口地址：{_settings.MiniMax.Endpoint}";
        }

        var source = r.AiFromCache ? "（来自本地缓存；如需最新判断，点“分析选中进程”重新分析）\n\n" : "";
        return $"模型：{(_lastAiModel.Length > 0 ? _lastAiModel : _settings.MiniMax.Model)}\n\n{source}{r.AiDetail}";
    }

    private static string BuildAiDetail(AiVerdict verdict)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"所属软件：{Fallback(verdict.Software)}");
        sb.AppendLine($"作用说明：{Fallback(verdict.Purpose)}");
        sb.AppendLine($"谁启动的：{Fallback(verdict.LaunchedBy)}");
        sb.AppendLine($"能否关闭：{Fallback(verdict.CanClose)}");
        sb.AppendLine($"如何关闭：{Fallback(verdict.CloseMethod)}");
        sb.AppendLine($"风险等级：{Fallback(verdict.Risk)}");
        if (!string.IsNullOrWhiteSpace(verdict.Note))
            sb.AppendLine($"补充说明：{verdict.Note}");
        return sb.ToString();
    }

    private static string Fallback(string text) => string.IsNullOrWhiteSpace(text) ? "不确定" : text;

    // ============================================================ AI
    private async Task RunAiForSelectionAsync()
    {
        var record = SelectedRecord();
        if (record is null)
        {
            MessageBox.Show(this, "请先在列表中选择一个进程。", "AI 分析",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        await RunAiAsync([record]);
    }

    private async Task RunAiForListAsync()
    {
        var targets = _view.Where(r => !r.HasAi).Take(200).ToList();
        if (targets.Count == 0)
        {
            targets = _view.Take(200).ToList();
            if (targets.Count == 0)
            {
                MessageBox.Show(this, "当前列表为空，请先刷新。", "AI 分析",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }

        await RunAiAsync(targets);
    }

    private async Task RunAiAsync(IReadOnlyList<ProcessRecord> targets)
    {
        if (_aiBusy)
            return;

        var apiKey = ConfigService.ResolveApiKey(_settings);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ShowMissingApiKey();
            return;
        }

        var chunkSize = Math.Clamp(_settings.MiniMax.MaxProcessesPerRequest, 1, 40);
        _aiBusy = true;
        SetAiBusy(true);
        _aiCts = new CancellationTokenSource();
        var token = _aiCts.Token;
        var applied = 0;

        try
        {
            var processed = 0;
            ShowAiProgress(0, targets.Count);

            foreach (var chunk in targets.Chunk(chunkSize))
            {
                token.ThrowIfCancellationRequested();
                SetAiStatus($"AI 分析中 {processed}/{targets.Count}…");

                var result = await _ai.AnalyzeAsync(chunk, _settings, apiKey, token);
                _lastAiModel = result.Model;
                applied += ApplyVerdicts(chunk, result);
                processed += chunk.Length;
                ShowAiProgress(processed, targets.Count);
            }

            if (IsDisposed || Disposing)
                return;

            HideAiProgress();
            _statusAi.Text = $"AI 完成：{applied}/{targets.Count}，模型 {_lastAiModel}";
            _cache?.Flush();
            UpdateCacheIndicator();
            _view.ResetBindings();
            UpdateDetail();
        }
        catch (OperationCanceledException)
        {
            HideAiProgress();
            SetAiStatus("AI 分析已取消");
        }
        catch (Exception ex)
        {
            HideAiProgress();
            Log.Error("AI 分析失败", ex);
            if (IsDisposed || Disposing)
                return;

            _statusAi.Text = "AI 分析失败";
            MessageBox.Show(this, ex.Message, "AI 分析失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            HideAiProgress();
            _aiCts?.Dispose();
            _aiCts = null;
            _aiBusy = false;
            if (!IsDisposed && !Disposing)
                SetAiBusy(false);
        }
    }

    /// <summary>AI 批量分析的进度：状态栏进度条 + "已处理/总数（百分比）"。</summary>
    private void ShowAiProgress(int done, int total)
    {
        if (IsDisposed || Disposing || _aiProgress is null)
            return;

        _aiProgress.Visible = true;
        _aiProgress.Maximum = Math.Max(1, total);
        _aiProgress.Value = Math.Max(0, Math.Min(done, _aiProgress.Maximum));

        if (done >= total)
            _statusAi.Text = $"AI 完成 {done}/{total}";
        else
            _statusAi.Text = $"AI 分析中 {done}/{total}（{(total <= 0 ? 0 : (int)Math.Round(done * 100d / total))}%）";
    }

    private void HideAiProgress()
    {
        if (_aiProgress is not null && !IsDisposed && !Disposing)
            _aiProgress.Visible = false;
    }

    private void SetAiStatus(string text)
    {
        if (IsDisposed || Disposing)
            return;

        _statusAi.Text = text;
    }

    private int ApplyVerdicts(IReadOnlyList<ProcessRecord> chunk, AiAnalysisResult result)
    {
        var byPid = new Dictionary<int, AiVerdict>();
        foreach (var verdict in result.Verdicts)
            byPid[verdict.Pid] = verdict;

        var applied = 0;
        foreach (var record in chunk)
        {
            if (!byPid.TryGetValue(record.Pid, out var verdict))
            {
                if (chunk.Count == 1)
                {
                    record.AiSummary = "AI 未按格式返回";
                    record.AiDetail = "模型返回内容无法解析为结构化结果，原始回复如下：\n\n" + result.RawText;
                    applied++;
                }

                continue;
            }

            record.AiSoftware = verdict.Software;
            record.AiPurpose = verdict.Purpose;
            record.AiLaunchedBy = verdict.LaunchedBy;
            record.AiCanClose = verdict.CanClose;
            record.AiCloseMethod = verdict.CloseMethod;
            record.AiRisk = verdict.Risk;
            record.AiDetail = BuildAiDetail(verdict);
            record.AiSummary = string.IsNullOrWhiteSpace(verdict.Note)
                ? $"{verdict.CanClose}｜{verdict.Risk}风险"
                : verdict.Note;
            record.AiFromCache = false;
            _cache?.UpdateFromRecord(record, result.Model);
            applied++;
        }

        return applied;
    }

    private void SetAiBusy(bool busy)
    {
        _btnAiSelected.Enabled = !busy;
        _btnAiList.Enabled = !busy;
        _btnPingAi.Enabled = !busy;
        // 不再用全局“转圈”光标：进度由状态栏进度条 + 计数显示，用户能看清进度。
    }

    private async Task PingAiAsync()
    {
        var apiKey = ConfigService.ResolveApiKey(_settings);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ShowMissingApiKey();
            return;
        }

        _statusAi.Text = "正在测试 AI 连接…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.MiniMax.TimeoutSeconds));
            var reply = await _ai.PingAsync(_settings, apiKey, cts.Token);
            _statusAi.Text = "AI 连接正常";
            MessageBox.Show(this, $"连接成功。\n\n模型：{reply}\n接口：{_settings.MiniMax.Endpoint}",
                "测试 AI 连接", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Error("测试 AI 连接失败", ex);
            SetAiStatus("AI 连接失败");
            if (IsDisposed || Disposing)
                return;

            MessageBox.Show(this, ex.Message, "测试 AI 连接失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateAiIndicator()
    {
        var configured = _ai.IsConfigured(_settings);
        _statusAi.Text = configured ? $"AI：{_settings.MiniMax.Model}" : "AI：未配置 Key";
        _statusAi.ForeColor = configured ? Color.DarkGreen : Color.DimGray;
        _btnAiSelected.Enabled = configured;
        _btnAiList.Enabled = configured;
        _btnPingAi.Enabled = configured;
    }

    private void UpdateCacheIndicator()
    {
        if (_btnCache is null)
            return;

        if (_cache is null || !_cache.Enabled)
        {
            _btnCache.Text = "缓存：不可用";
            _toolTip.SetToolTip(_btnCache, _cache?.LastError ?? "缓存未启用");
            return;
        }

        _btnCache.Text = $"缓存 {_cache.Count}";
        _toolTip.SetToolTip(_btnCache,
            $"已缓存 {_cache.Count} 个应用的信息与 AI 结论\n{AppCache.DatabasePath}");
    }

    private void ManageCache()
    {
        if (_cache is null || !_cache.Enabled)
        {
            MessageBox.Show(this,
                "本地缓存不可用。\n\n" +
                (_cache?.LastError ?? "缓存未启用") + "\n\n" +
                "程序仍可正常使用，只是每次启动都会重新读取文件信息。",
                "缓存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var answer = MessageBox.Show(this,
            $"本地缓存（cache.db）已记录 {_cache.Count} 个应用的文件信息、判断结论与 AI 结论。\n" +
            $"位置：{AppCache.DatabasePath}\n\n" +
            "清空后，下次刷新会重新读取文件信息并重新推断，AI 结论也会丢失（需重新调用接口）。\n\n" +
            "是否清空缓存？",
            "缓存管理", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
            return;

        var cleared = _cache.Clear();
        UpdateCacheIndicator();
        _statusText.Text = $"已清空 {cleared} 条缓存，正在重新分析…";
        _ = RefreshAsync();
    }

    private void ShowMissingApiKey()
    {
        var result = MessageBox.Show(this,
            "尚未配置 MiniMax API Key，AI 功能不可用。\n\n" +
            $"请在配置文件中填写 MiniMax.ApiKey：\n{ConfigService.ConfigPath}\n\n" +
            "也可以用环境变量 MINIMAX_API_KEY 提供。\n\n是否现在打开配置文件？",
            "未配置 AI", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

        if (result == DialogResult.Yes)
            OpenConfigFile();
    }

    // ============================================================ 操作
    private void CopyDetail()
    {
        if (SelectedRecord() is null)
            return;

        try
        {
            Clipboard.SetText(_detailBox.Text);
            _statusText.Text = "详细信息已复制到剪贴板";
        }
        catch (Exception ex)
        {
            Log.Error("复制失败", ex);
            MessageBox.Show(this, "复制失败：" + ex.Message, "复制", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenFileLocation()
    {
        var record = SelectedRecord();
        if (record is null)
            return;

        if (string.IsNullOrWhiteSpace(record.Path))
        {
            MessageBox.Show(this, "该进程的文件路径不可读（通常是权限不足或进程已退出）。",
                "打开文件位置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{record.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("打开文件位置失败", ex);
            MessageBox.Show(this, ex.Message, "打开文件位置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void KillSelected()
    {
        var record = SelectedRecord();
        if (record is null)
            return;

        if (ProcessTerminator.IsBlocked(record, Environment.ProcessId, out var reason))
        {
            MessageBox.Show(this, reason, "已阻止", MessageBoxButtons.OK, MessageBoxIcon.Stop);
            return;
        }

        var extra = record.AiCanClose == "禁止关闭" ? "\n\n注意：AI 判断该进程为“禁止关闭”。" : "";
        var answer = MessageBox.Show(this,
            $"确定结束该进程吗？未保存的数据会丢失，操作不可撤销。{extra}\n\n" +
            $"进程：{record.Name}\nPID：{record.Pid}\n所属软件：{record.Software}\n路径：{record.Path}",
            "结束进程", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
            return;

        try
        {
            ProcessTerminator.Kill(record);
            Log.Info($"已结束进程 {record.Name} ({record.Pid})");
            _statusText.Text = $"已结束 {record.Name} (PID {record.Pid})";
        }
        catch (Exception ex)
        {
            Log.Error($"结束进程 {record.Name} ({record.Pid}) 失败", ex);
            MessageBox.Show(this, NativeProcess.DescribeError(ex), "结束进程失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        _ = RefreshAsync();
    }

    private void OpenServicesConsole()
    {
        try
        {
            Process.Start(new ProcessStartInfo("services.msc") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("打开服务管理器失败", ex);
            MessageBox.Show(this, ex.Message, "打开服务", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenConfigFile()
    {
        try
        {
            if (!File.Exists(ConfigService.ConfigPath))
                ConfigService.Load(out _);

            Process.Start(new ProcessStartInfo(ConfigService.ConfigPath) { UseShellExecute = true });
            _statusText.Text = "已打开配置文件，保存后点“重新加载配置”";
        }
        catch (Exception ex)
        {
            Log.Error("打开配置文件失败", ex);
            MessageBox.Show(this,
                $"无法打开配置文件：{ex.Message}\n\n路径：{ConfigService.ConfigPath}",
                "打开配置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ReloadConfig()
    {
        var settings = ConfigService.Load(out var error);
        _settings = settings;
        ApplySettingsToUi();
        UpdateAiIndicator();

        if (!string.IsNullOrWhiteSpace(error))
        {
            MessageBox.Show(this, error, "配置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _statusText.Text = _ai.IsConfigured(_settings)
            ? "配置已重新加载，AI 可用"
            : "配置已重新加载，但尚未填写 MiniMax.ApiKey";
    }

    private void ExportCsv()
    {
        var rows = _view.ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "当前没有可导出的行。", "导出 CSV",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "导出进程列表",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            FileName = $"进程列表_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            AddExtension = true,
            DefaultExt = "csv",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            CsvExporter.Export(dialog.FileName, rows);
            _statusText.Text = $"已导出 {rows.Count} 行到 {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            Log.Error("导出 CSV 失败", ex);
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowHelp()
    {
        MessageBox.Show(this,
            "使用说明\n\n" +
            "1. 列：进程名 / PID / 所属软件 / 作用说明 / 谁启动的 / 能否关闭 / 如何关闭 / 风险 / 命令行。\n" +
            "2. “谁启动的”是启动它的具体程序：优先看命令行的宿主线索（例如 WebView2 的 --webview-exe-name），\n" +
            "   其次看父进程；选中一行后“详细信息”里还有完整进程链和判断依据。\n" +
            "3. 点列头可排序；顶部可搜索、筛选“只看第三方”“只看可疑”“隐藏系统进程”。\n" +
            "4. 每个应用的文件信息、判断结论和 AI 结论都会写进本地 cache.db，下次启动直接复用，\n" +
            "   不再重复读文件/重复调用 AI；点顶部“缓存 N”可以查看数量并清空。\n" +
            "5. AI 需要填 Key：配置文件在 " + ConfigService.ConfigPath + "（也可用环境变量 MINIMAX_API_KEY），\n" +
            "   保存后点“重新加载配置”，或先用“测试 AI 连接”验证。\n" +
            "6. 右键菜单可复制信息、定位文件、结束进程；系统核心进程已禁止结束。\n" +
            "7. 想看受保护进程的详细信息，请右键 exe 选择“以管理员身份运行”。",
            "说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _autoRefresh.Stop();
        _autoRefresh.Dispose();
        _aiCts?.Cancel();
        _aiCts?.Dispose();
        _boldFont?.Dispose();
        _toolTip.Dispose();
        _cache?.Flush();
        base.OnFormClosed(e);
    }

    /// <summary>开启双缓冲，减少上千行时的闪烁。</summary>
    private sealed class BufferedGrid : DataGridView
    {
        public BufferedGrid()
        {
            DoubleBuffered = true;
        }
    }
}
