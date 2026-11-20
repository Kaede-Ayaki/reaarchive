// MainForm.cs — REAArchive 主窗口：菜单 / 工具栏 / 归档内路径栏 / 虚拟列表 / 状态栏。
// 全部耗时操作 async + 进度对话框 + CancellationToken；不联网、不检查更新、无广告。
using System.Diagnostics;
using System.Runtime.InteropServices;
using REAArchive.Archive;

namespace REAArchive.Ui;

public sealed class MainForm : Form, IListViewRowSource
{
    private sealed class EntryRow
    {
        public string Path = "";
        public string Name = "";
        public bool IsDirectory;
        public long Size;
        public long CompressedSize = -1;
        public DateTime? Modified;
        public string Method = "";
        public string? Crc;
        public bool Encrypted;
        public string TypeText = "";
    }

    /// <summary>后台线程构建的目录树（不与 UI 共享可变状态）。</summary>
    private sealed class TreeData
    {
        public List<EntryRow> AllRows { get; } = new();
        public Dictionary<string, List<EntryRow>> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int FileCount;
        public int DirCount;
        public long CompressedTotal;
    }

    private readonly string[] _args;
    private AppSettings _settings;

    private readonly MenuStrip _menu;
    private readonly ToolTip _tooltip = new();
    private readonly ThemedListView _list;
    private readonly TextBox _pathBox;
    private readonly TextBox _filterBox;
    private readonly FlatProgressBar _progress;
    private readonly Label _lblStatus;
    private readonly Label _lblEngine;
    private readonly Label _lblElapsed;
    private readonly Button _btnOpen;
    private readonly Button _btnExtractTo;
    private readonly Button _btnExtractHere;
    private readonly Button _btnTest;
    private readonly Button _btnCreate;
    private readonly Button _btnAdd;
    private readonly Button _btnDelete;
    private readonly Button _btnInfo;
    private readonly Button _btnUp;
    private readonly Button _btnRecover;
    private readonly ArchiveInfoStrip _infoStrip;
    private readonly System.Windows.Forms.Timer _filterTimer;
    private readonly TableLayoutPanel _root;
    private readonly RowStyle _toolbarRow;
    private readonly RowStyle _pathRow;
    private readonly RowStyle _infoRow;
    private readonly RowStyle _listRow;
    private readonly RowStyle _statusRow;

    private readonly ToolStripMenuItem _miExtractTo;
    private readonly ToolStripMenuItem _miExtractHere;
    private readonly ToolStripMenuItem _miTest;
    private readonly ToolStripMenuItem _miAddFiles;
    private readonly ToolStripMenuItem _miAddFolder;
    private readonly ToolStripMenuItem _miDelete;
    private readonly ToolStripMenuItem _miInfo;
    private readonly ToolStripMenuItem _miRecover;

    private readonly List<string> _tempRoots = new();
    private List<EntryRow> _view = new();
    private List<EntryRow> _allRows = new();
    private Dictionary<string, List<EntryRow>> _children = new(StringComparer.OrdinalIgnoreCase);

    private IArchiveService? _service;
    private ListResult? _listResult;
    private string _archivePath = "";
    private string _currentDir = "";
    private string _filter = "";
    private bool _filterActive;
    private string? _archivePassword;
    private bool _busy;
    private readonly Queue<(string[] Args, bool CloseAfter)> _pendingCommands = new();
    private bool _commandsRunning;
    private string? _preloadPath;
    private Task<PreloadResult>? _preloadTask;

    private readonly record struct PreloadResult(ListResult? Result, Exception? Error);
    private int _sortColumn;
    private bool _sortAscending = true;
    private bool _suppressFilterEvent;
    private bool _firstListPaintLogged;
    private int _fileCount;
    private int _dirCount;
    private long _compressedTotal;
    private bool _archiveEncrypted;
    private bool _archiveReadOnly;
    private long _archiveFileSize;

    public MainForm(string[] args)
    {
        _args = args ?? Array.Empty<string>();
        _settings = SettingsStore.Load();
        Theme.UiFontSize = _settings.FontSize;
        _filterTimer = new System.Windows.Forms.Timer { Interval = 260 };

        Name = "MainForm";
        Text = "REAArchive — 纯净压缩";
        ClientSize = new Size(1200, 760);
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Ui();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Icon = Theme.AppIcon;
        AllowDrop = true;
        KeyPreview = true;

        _menu = BuildMenu();
        // 菜单被放进 TableLayoutPanel（不是直接挂到 Form.Controls），WinForms 不会自动
        // 把它登记为 MainMenuStrip；不显式赋值的话 Alt+F / Alt+T 这些助记键全部失效。
        MainMenuStrip = _menu;

        _btnOpen = Theme.ToolButton("btnOpen", "打开", Glyph.Open, (_, _) => RunUi(OpenArchivePickerAsync));
        _btnExtractTo = Theme.ToolButton("btnExtractTo", "解压到…", Glyph.ExtractTo, (_, _) => RunUi(() => ExtractArchiveAsync(here: false)));
        _btnExtractHere = Theme.ToolButton("btnExtractHere", "解压到当前", Glyph.ExtractHere, (_, _) => RunUi(() => ExtractArchiveAsync(here: true)));
        _btnTest = Theme.ToolButton("btnTest", "测试", Glyph.Test, (_, _) => RunUi(TestArchiveAsync));
        _btnCreate = Theme.ToolButton("btnCreate", "新建", Glyph.Create, (_, _) => RunUi(() => ShowCreateAsync()));
        _btnAdd = Theme.ToolButton("btnAdd", "添加", Glyph.Add, (_, _) => RunUi(AddFilesInteractiveAsync));
        _btnDelete = Theme.ToolButton("btnDelete", "删除", Glyph.Delete, (_, _) => RunUi(DeleteSelectedAsync));
        _btnInfo = Theme.ToolButton("btnInfo", "信息", Glyph.Info, (_, _) => ShowEntryInfo());
        _btnRecover = Theme.ToolButton("btnRecover", "密码破译", Glyph.Key, (_, _) => RunUi(RecoverPasswordAsync));

        _btnUp = Theme.DialogButton("btnUp", "↑", false, 34);
        _btnUp.Name = "btnUp";
        _btnUp.Height = 30;
        _btnUp.Click += (_, _) => GoUp();

        _infoStrip = new ArchiveInfoStrip();

        _list = new ThemedListView { Dock = DockStyle.Fill };
        _list.Columns.Add("名称", 420, HorizontalAlignment.Left);
        _list.Columns.Add("大小", 110, HorizontalAlignment.Right);
        _list.Columns.Add("压缩后", 110, HorizontalAlignment.Right);
        _list.Columns.Add("类型", 100, HorizontalAlignment.Left);
        _list.Columns.Add("修改时间", 150, HorizontalAlignment.Left);
        _list.Columns.Add("CRC", 110, HorizontalAlignment.Left);
        _list.RowSource = this;
        _list.SelectedIndexChanged += (_, _) => UpdateCommandStates();
        _list.ColumnClick += OnColumnClick;
        _list.MouseDoubleClick += OnListMouseDoubleClick;
        _list.KeyDown += OnListKeyDown;
        _list.Resize += (_, _) => AdjustNameColumn();
        _list.Paint += (_, _) =>
        {
            if (_firstListPaintLogged) return;
            _firstListPaintLogged = true;
            Program.TimingLog("list first paint");
        };

        _pathBox = Theme.MakeTextBox("txtPath", 200);
        _pathBox.PlaceholderText = "尚未打开归档";
        _pathBox.ReadOnly = true;
        _pathBox.KeyDown += OnPathBoxKeyDown;

        _filterBox = Theme.MakeTextBox("txtFilter", 190);
        _filterBox.PlaceholderText = "搜索名称…";
        _filterBox.TextChanged += OnFilterTextChanged;
        _filterBox.KeyDown += OnFilterBoxKeyDown;

        _progress = new FlatProgressBar { Name = "progressBar", Width = 150 };
        _lblStatus = new Label
        {
            Name = "lblStatus",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            Font = Theme.Ui(),
            Text = "就绪",
            Margin = new Padding(12, 0, 12, 0),
        };
        _lblEngine = new Label
        {
            Name = "lblEngine",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            Font = Theme.Ui(),
            Margin = new Padding(0, 0, 18, 0),
            Text = "引擎 —",
        };
        _lblElapsed = new Label
        {
            Name = "lblElapsed",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            Font = Theme.Ui(),
            Margin = new Padding(0, 0, 14, 0),
            Text = "",
        };

        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            BackColor = Theme.Background,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 0 菜单
        _toolbarRow = new RowStyle(SizeType.Absolute, 50f);          // 1 工具栏
        _infoRow = new RowStyle(SizeType.Absolute, 0f);              // 2 归档信息条（无归档时收起）
        _pathRow = new RowStyle(SizeType.Absolute, 44f);             // 3 路径栏
        _listRow = new RowStyle(SizeType.Percent, 100f);             // 4 列表
        _statusRow = new RowStyle(SizeType.Absolute, 30f);           // 5 状态栏
        _root.RowStyles.Add(_toolbarRow);
        _root.RowStyles.Add(_infoRow);
        _root.RowStyles.Add(_pathRow);
        _root.RowStyles.Add(_listRow);
        _root.RowStyles.Add(_statusRow);
        Controls.Add(_root);

        _root.Controls.Add(_menu, 0, 0);
        _root.Controls.Add(BuildToolbar(), 0, 1);
        _root.Controls.Add(BuildInfoHost(), 0, 2);
        _root.Controls.Add(BuildPathBar(), 0, 3);
        _root.Controls.Add(BuildListHost(), 0, 4);
        _root.Controls.Add(BuildStatusBar(), 0, 5);

        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            ApplyFilter();
        };

        _miExtractTo = FindMenuItem("menuExtractTo")!;
        _miExtractHere = FindMenuItem("menuExtractHere")!;
        _miTest = FindMenuItem("menuTest")!;
        _miAddFiles = FindMenuItem("menuAddFiles")!;
        _miAddFolder = FindMenuItem("menuAddFolder")!;
        _miDelete = FindMenuItem("menuDelete")!;
        _miInfo = FindMenuItem("menuInfo")!;
        _miRecover = FindMenuItem("menuRecover")!;

        EnableDrop(this);
        EnableDrop(_list);
        EnableDrop(_pathBox);
        EnableDrop(_filterBox);

        _tooltip.SetToolTip(_btnOpen, "打开归档 (Ctrl+O)");
        _tooltip.SetToolTip(_btnExtractTo, "解压到指定文件夹 (Ctrl+E)");
        _tooltip.SetToolTip(_btnExtractHere, "解压到归档所在文件夹");
        _tooltip.SetToolTip(_btnTest, "测试归档完整性 (Ctrl+T)");
        _tooltip.SetToolTip(_btnCreate, "新建归档 (Ctrl+N)");
        _tooltip.SetToolTip(_btnAdd, "向当前归档添加文件");
        _tooltip.SetToolTip(_btnDelete, "删除选中条目 (Delete)");
        _tooltip.SetToolTip(_btnInfo, "查看条目信息");
        _tooltip.SetToolTip(_btnRecover, "忘记密码时尝试找回归档密码（字典 / 掩码 / 增量）");
        _tooltip.SetToolTip(_pathBox, "归档内的当前路径，回车可跳转");
        _tooltip.SetToolTip(_filterBox, "按名称筛选（全归档），Esc 清除");

        _service = null;
        _lblEngine.Text = "引擎 检测中…";
        _lblStatus.Text = "正在检测归档引擎…";
        UpdateEmptyState();
        UpdateCommandStates();
        TryStartPreload();
        Program.TimingLog("MainForm ctor done");
    }

    // ————————————————————————————————————————————————
    // 界面构建
    // ————————————————————————————————————————————————
    private MenuStrip BuildMenu()
    {
        var menu = Theme.MakeMenuStrip();
        menu.Name = "menuStrip";

        var file = new ToolStripMenuItem("文件(&F)");
        file.DropDownItems.Add(MenuItem("打开归档…", (_, _) => RunUi(OpenArchivePickerAsync), Keys.Control | Keys.O));
        file.DropDownItems.Add(MenuItem("新建归档…", (_, _) => RunUi(() => ShowCreateAsync()), Keys.Control | Keys.N));
        file.DropDownItems.Add(new ToolStripSeparator());
        var addFiles = MenuItem("添加文件…", (_, _) => RunUi(AddFilesInteractiveAsync));
        addFiles.Name = "menuAddFiles";
        file.DropDownItems.Add(addFiles);
        var addFolder = MenuItem("添加文件夹…", (_, _) => RunUi(AddFolderInteractiveAsync));
        addFolder.Name = "menuAddFolder";
        file.DropDownItems.Add(addFolder);
        var delete = MenuItem("删除选中条目 (Delete)", (_, _) => RunUi(DeleteSelectedAsync));
        delete.Name = "menuDelete";
        file.DropDownItems.Add(delete);
        file.DropDownItems.Add(new ToolStripSeparator());
        var extractTo = MenuItem("解压到…", (_, _) => RunUi(() => ExtractArchiveAsync(here: false)), Keys.Control | Keys.E);
        extractTo.Name = "menuExtractTo";
        file.DropDownItems.Add(extractTo);
        var extractHere = MenuItem("解压到当前文件夹", (_, _) => RunUi(() => ExtractArchiveAsync(here: true)));
        extractHere.Name = "menuExtractHere";
        file.DropDownItems.Add(extractHere);
        var test = MenuItem("测试完整性", (_, _) => RunUi(TestArchiveAsync), Keys.Control | Keys.T);
        test.Name = "menuTest";
        file.DropDownItems.Add(test);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(MenuItem("退出", (_, _) => Close()));

        var edit = new ToolStripMenuItem("编辑(&E)");
        edit.DropDownItems.Add(MenuItem("全选", (_, _) => SelectAllEntries(), Keys.Control | Keys.A));
        edit.DropDownItems.Add(MenuItem("复制选中路径", (_, _) => CopySelectedPaths(), Keys.Control | Keys.C));
        edit.DropDownItems.Add(new ToolStripSeparator());
        var info = MenuItem("条目信息…", (_, _) => ShowEntryInfo(), Keys.Alt | Keys.Enter);
        info.Name = "menuInfo";
        edit.DropDownItems.Add(info);

        var view = new ToolStripMenuItem("查看(&V)");
        view.DropDownItems.Add(MenuItem("刷新", (_, _) => RunUi(RefreshArchiveAsync), Keys.F5));
        view.DropDownItems.Add(MenuItem("上级目录 (Backspace)", (_, _) => GoUp()));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(MenuItem("聚焦搜索框", (_, _) => { _filterBox.Focus(); _filterBox.SelectAll(); }, Keys.Control | Keys.F));
        view.DropDownItems.Add(MenuItem("清除搜索", (_, _) => ClearFilter()));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(MenuItem("设置…", (_, _) => ShowSettings(), Keys.Control | Keys.Oemcomma));

        var tools = new ToolStripMenuItem("工具(&T)");
        var recover = MenuItem("密码破译…", (_, _) => RunUi(RecoverPasswordAsync));
        recover.Name = "menuRecover";
        tools.DropDownItems.Add(recover);
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(MenuItem("安装右键菜单…", (_, _) => InstallShellMenu()));
        tools.DropDownItems.Add(MenuItem("卸载右键菜单…", (_, _) => UninstallShellMenu()));
        tools.DropDownItems.Add(MenuItem("打开 Windows 默认应用设置…", (_, _) => OpenDefaultAppsSettings()));
        var defaultHint = MenuItem("（Windows 不允许程序自行改默认打开方式，请在设置中为 .7z/.zip 选择 REAArchive）",
            null);
        defaultHint.Enabled = false;
        tools.DropDownItems.Add(defaultHint);
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(MenuItem("打开临时目录", (_, _) => OpenTempFolder()));
        tools.DropDownItems.Add(MenuItem("清理临时文件…", (_, _) => CleanTempFiles()));
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(MenuItem("引擎与版本信息", (_, _) => ShowEngineInfo()));

        var help = new ToolStripMenuItem("帮助(&H)");
        help.DropDownItems.Add(MenuItem("关于 REAArchive", (_, _) => ShowAbout()));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, view, tools, help });
        Theme.StyleMenuStrip(menu);
        return menu;
    }

    private static ToolStripMenuItem MenuItem(string text, EventHandler? handler, Keys shortcut = Keys.None)
    {
        var item = new ToolStripMenuItem(text) { ForeColor = Theme.Text, BackColor = Theme.SurfaceAlt };
        if (shortcut != Keys.None) item.ShortcutKeys = shortcut;
        if (handler != null) item.Click += handler;
        return item;
    }

    private ToolStripMenuItem? FindMenuItem(string name)
    {
        foreach (ToolStripItem top in _menu.Items)
        {
            if (top is not ToolStripMenuItem menuItem) continue;
            var found = FindMenuItem(menuItem, name);
            if (found != null) return found;
        }
        return null;
    }

    private static ToolStripMenuItem? FindMenuItem(ToolStripMenuItem parent, string name)
    {
        foreach (ToolStripItem item in parent.DropDownItems)
        {
            if (item is not ToolStripMenuItem menuItem) continue;
            if (menuItem.Name == name) return menuItem;
            var found = FindMenuItem(menuItem, name);
            if (found != null) return found;
        }
        return null;
    }

    private Control BuildInfoHost() => _infoStrip;

    private Control BuildToolbar()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
        host.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, host.Height - 1, host.Width, host.Height - 1);
        };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            Padding = new Padding(10, 6, 10, 4),
            WrapContents = false,
            AutoScroll = false,
            Margin = Padding.Empty,
        };
        void AddSeparator() => flow.Controls.Add(new Panel
        {
            BackColor = Theme.Border,
            Size = new Size(1, 24),
            Margin = new Padding(9, 8, 9, 7),
        });

        // 组 1：打开 / 解压
        flow.Controls.Add(_btnOpen);
        flow.Controls.Add(_btnExtractTo);
        flow.Controls.Add(_btnExtractHere);
        AddSeparator();
        // 组 2：校验
        flow.Controls.Add(_btnTest);
        AddSeparator();
        // 组 3：编辑归档
        flow.Controls.Add(_btnCreate);
        flow.Controls.Add(_btnAdd);
        flow.Controls.Add(_btnDelete);
        AddSeparator();
        // 组 4：信息 / 工具
        flow.Controls.Add(_btnInfo);
        flow.Controls.Add(_btnRecover);
        host.Controls.Add(flow);
        return host;
    }

    private Control BuildPathBar()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
        host.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, host.Height - 1, host.Width, host.Height - 1);
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            BackColor = Theme.Surface,
            Margin = Padding.Empty,
            Padding = new Padding(10, 0, 10, 0),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34f));   // 上级
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8f));    // 间隙
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));   // 归档内路径
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 14f));   // 间隙
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230f));  // 筛选

        _btnUp.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _btnUp.Margin = new Padding(0, 8, 0, 8);
        layout.Controls.Add(_btnUp, 0, 0);

        _pathBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _pathBox.Margin = new Padding(0, 8, 0, 8);
        layout.Controls.Add(_pathBox, 2, 0);

        _filterBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _filterBox.Margin = new Padding(0, 8, 0, 8);
        layout.Controls.Add(_filterBox, 4, 0);

        host.Controls.Add(layout);
        return host;
    }

    private Control BuildListHost()
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Background,
            Padding = new Padding(10, 4, 10, 4),
            Margin = Padding.Empty,
        };
        // 空态由 ThemedListView 自己在 WM_PAINT 里画（图标 + 主标题 + 副标题），
        // 这里不再叠覆盖层——叠了会把它整个盖住。
        host.Controls.Add(_list);
        return host;
    }

    private Control BuildStatusBar()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
        host.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, host.Width, 0);
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Theme.Surface,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        layout.Controls.Add(_lblStatus, 0, 0);

        _progress.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _progress.Margin = new Padding(0, 9, 16, 9);
        layout.Controls.Add(_progress, 1, 0);

        _lblEngine.Anchor = AnchorStyles.Left;
        layout.Controls.Add(_lblEngine, 2, 0);
        _lblElapsed.Anchor = AnchorStyles.Left;
        layout.Controls.Add(_lblElapsed, 3, 0);

        host.Controls.Add(layout);
        return host;
    }

    private void EnableDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += OnDragEnter;
        control.DragDrop += OnDragDrop;
    }

    // ————————————————————————————————————————————————
    // 列表数据源 / 视图
    // ————————————————————————————————————————————————
    int IListViewRowSource.RowCount => _view.Count;

    string IListViewRowSource.CellText(int row, int column) => CellTextCore(row, column);

    bool IListViewRowSource.IsDirectory(int row) => row >= 0 && row < _view.Count && _view[row].IsDirectory;

    bool IListViewRowSource.IsEncrypted(int row) => row >= 0 && row < _view.Count && _view[row].Encrypted;

    private string CellTextCore(int row, int column)
    {
        if (row < 0 || row >= _view.Count) return "";
        var item = _view[row];
        return column switch
        {
            0 => _filterActive ? item.Path : item.Name,
            1 => item.IsDirectory ? "—" : FormatUtil.Size(item.Size),
            2 => item.IsDirectory ? "—" : FormatUtil.Size(item.CompressedSize),
            3 => item.TypeText,
            4 => UiFormat.When(item.Modified),
            5 => item.Crc ?? "—",
            _ => "",
        };
    }

    /// <summary>在线程池构建目录树；10 万条目也不阻塞 UI。</summary>
    private static TreeData BuildTreeData(IReadOnlyList<ArchiveEntryInfo> entries)
    {
        var data = new TreeData();
        var map = new Dictionary<string, EntryRow>(Math.Max(16, entries.Count), StringComparer.OrdinalIgnoreCase);
        var explicitDirs = new List<(string Path, ArchiveEntryInfo Entry)>();

        foreach (var entry in entries)
        {
            var path = NormalizePath(entry.Path);
            if (path.Length == 0) continue;
            if (entry.IsDirectory) explicitDirs.Add((path, entry));
            else AddFileRow(path, entry, map, data);
        }
        foreach (var (path, entry) in explicitDirs)
        {
            EnsureDirRow(path, entry, map, data);
            EnsureParents(path, map, data);
        }

        foreach (var row in map.Values)
        {
            var parent = ParentOf(row.Path);
            if (!data.Children.TryGetValue(parent, out var list))
                data.Children[parent] = list = new List<EntryRow>();
            list.Add(row);
        }

        int files = 0, dirs = 0;
        foreach (var row in data.AllRows)
        {
            if (row.IsDirectory) dirs++;
            else files++;
        }
        long compressed = 0;
        foreach (var entry in entries)
        {
            if (!entry.IsDirectory && entry.CompressedSize >= 0) compressed += entry.CompressedSize;
        }
        data.FileCount = files;
        data.DirCount = dirs;
        data.CompressedTotal = compressed;
        return data;
    }

    private void ApplyTree(TreeData data)
    {
        _allRows = data.AllRows;
        _children = data.Children;
        _fileCount = data.FileCount;
        _dirCount = data.DirCount;
        _compressedTotal = data.CompressedTotal;
    }

    private static void AddFileRow(string path, ArchiveEntryInfo entry,
        Dictionary<string, EntryRow> map, TreeData data)
    {
        EnsureParents(path, map, data);
        if (map.ContainsKey(path)) return;
        var row = new EntryRow
        {
            Path = path,
            Name = NameOf(path),
            IsDirectory = false,
            Size = entry.Size,
            CompressedSize = entry.CompressedSize,
            Modified = entry.Modified,
            Method = entry.Method,
            Crc = entry.Crc,
            Encrypted = entry.Encrypted,
            TypeText = TypeTextFor(entry),
        };
        map[path] = row;
        data.AllRows.Add(row);
    }

    private static EntryRow EnsureDirRow(string path, ArchiveEntryInfo? entry,
        Dictionary<string, EntryRow> map, TreeData data)
    {
        if (map.TryGetValue(path, out var existing))
        {
            if (entry != null)
            {
                existing.CompressedSize = entry.CompressedSize;
                existing.Modified = entry.Modified;
                existing.Method = entry.Method;
                existing.Crc = entry.Crc;
                existing.Encrypted = entry.Encrypted;
            }
            return existing;
        }
        var row = new EntryRow
        {
            Path = path,
            Name = NameOf(path),
            IsDirectory = true,
            Size = 0,
            CompressedSize = entry?.CompressedSize ?? -1,
            Modified = entry?.Modified,
            Method = entry?.Method ?? "",
            Crc = entry?.Crc,
            Encrypted = entry?.Encrypted ?? false,
            TypeText = "文件夹",
        };
        map[path] = row;
        data.AllRows.Add(row);
        return row;
    }

    private static void EnsureParents(string path, Dictionary<string, EntryRow> map, TreeData data)
    {
        var index = path.LastIndexOf('/');
        while (index > 0)
        {
            var dir = path[..index];
            if (map.ContainsKey(dir)) break;
            EnsureDirRow(dir, null, map, data);
            index = dir.LastIndexOf('/');
        }
    }

    private static string TypeTextFor(ArchiveEntryInfo entry)
    {
        if (entry.IsDirectory) return "文件夹";
        var ext = Path.GetExtension(entry.Path);
        return string.IsNullOrEmpty(ext) ? "文件" : ext.TrimStart('.').ToUpperInvariant();
    }

    private static string NormalizePath(string path)
        => (path ?? "").Replace('\\', '/').Trim('/');

    private static string NameOf(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? path : path[(index + 1)..];
    }

    private static string ParentOf(string path)
    {
        var index = path.LastIndexOf('/');
        return index <= 0 ? "" : path[..index];
    }

    private void RebuildView()
    {
        IEnumerable<EntryRow> source;
        if (_filterActive)
        {
            source = _allRows.Where(r =>
                r.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
                r.Path.Contains(_filter, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            source = _children.TryGetValue(_currentDir, out var list) ? list : Enumerable.Empty<EntryRow>();
        }

        var view = source.ToList();
        SortRows(view, _sortColumn, _sortAscending, _filterActive);
        _view = view;
        _list.SortColumnIndex = _sortColumn;
        _list.SortAscending = _sortAscending;
        _list.RefreshRows();
        _list.Invalidate();
        UpdateEmptyState();
        UpdateStatus();
        UpdateCommandStates();
    }

    /// <summary>初始视图在后台排好序，UI 只做一次 SetItemCount。</summary>
    private static List<EntryRow> BuildInitialView(TreeData data, int sortColumn, bool ascending)
    {
        var view = data.Children.TryGetValue("", out var children)
            ? new List<EntryRow>(children)
            : new List<EntryRow>();
        SortRows(view, sortColumn, ascending, usePaths: false);
        return view;
    }

    private static void SortRows(List<EntryRow> rows, int sortColumn, bool ascending, bool usePaths)
    {
        rows.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            int result = sortColumn switch
            {
                1 => a.Size.CompareTo(b.Size),
                2 => a.CompressedSize.CompareTo(b.CompressedSize),
                3 => string.Compare(a.TypeText, b.TypeText, StringComparison.CurrentCultureIgnoreCase),
                4 => Nullable.Compare(a.Modified, b.Modified),
                5 => string.Compare(a.Crc ?? "", b.Crc ?? "", StringComparison.OrdinalIgnoreCase),
                _ => string.Compare(
                    usePaths ? a.Path : a.Name,
                    usePaths ? b.Path : b.Name,
                    StringComparison.CurrentCultureIgnoreCase),
            };
            if (result == 0)
                result = string.Compare(a.Path, b.Path, StringComparison.CurrentCultureIgnoreCase);
            return ascending ? result : -result;
        });
    }

    private void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        if (e.Column == _sortColumn) _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = e.Column;
            _sortAscending = e.Column is not (1 or 2);
        }
        RebuildView();
    }

    private void AdjustNameColumn()
    {
        if (_list.Columns.Count < 6) return;
        int others = 0;
        for (int i = 1; i < _list.Columns.Count; i++) others += _list.Columns[i].Width;
        _list.Columns[0].Width = Math.Max(220, _list.ClientSize.Width - others - 6);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            var selected = SelectedRows();
            if (selected.Count == 1)
            {
                if (selected[0].IsDirectory) NavigateTo(selected[0].Path);
                else RunUi(() => PreviewFileAsync(selected[0]));
                e.Handled = true;
            }
        }
    }

    private void OnListMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        // 必须按鼠标落点取行：虚拟列表里旧选中项会让“双击表头/空白”误触发导航。
        int headerBottom;
        try
        {
            headerBottom = _list.GetItemRect(0).Top;
        }
        catch
        {
            return;
        }
        if (e.Y < headerBottom) return;

        ListViewHitTestInfo hit;
        try { hit = _list.HitTest(e.X, e.Y); }
        catch { return; }
        var index = hit.Item?.Index ?? -1;
        if (index < 0 || index >= _view.Count) return;

        var row = _view[index];
        if (row.IsDirectory) NavigateTo(row.Path);
        else RunUi(() => PreviewFileAsync(row));
    }

    private List<EntryRow> SelectedRows()
    {
        var result = new List<EntryRow>();
        foreach (int index in _list.SelectedIndices)
        {
            if (index >= 0 && index < _view.Count) result.Add(_view[index]);
        }
        return result;
    }

    private void SelectAllEntries()
    {
        if (_view.Count == 0) return;
        var item = new LvItemState
        {
            mask = LvifState,
            stateMask = LvisSelected | LvisFocused,
            state = LvisSelected | LvisFocused,
        };
        _ = SendMessage(_list.Handle, LvmSetItemState, new IntPtr(-1), ref item);
        _list.Invalidate();
        UpdateCommandStates();
    }

    private void CopySelectedPaths()
    {
        var rows = SelectedRows();
        if (rows.Count == 0) return;
        try
        {
            Clipboard.SetText(string.Join("\r\n", rows.Select(r => r.Path)));
            SetStatus($"已复制 {rows.Count} 个路径");
        }
        catch (Exception ex)
        {
            MsgBox.Warn(this, "复制到剪贴板失败：" + ex.Message);
        }
    }

    // ————————————————————————————————————————————————
    // 导航 / 筛选
    // ————————————————————————————————————————————————
    private void NavigateTo(string dir)
    {
        dir = NormalizePath(dir);
        if (dir.Length > 0)
        {
            var exists = _children.ContainsKey(dir) ||
                         _allRows.Any(r => r.IsDirectory && string.Equals(r.Path, dir, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                SetStatus("目录不存在：" + dir);
                UpdatePathUi();
                return;
            }
        }

        _currentDir = dir;
        _filter = "";
        _suppressFilterEvent = true;
        _filterBox.Text = "";
        _suppressFilterEvent = false;
        _filterActive = false;
        UpdatePathUi();
        RebuildView();
        _list.Focus();
    }

    private void GoUp()
    {
        if (_currentDir.Length == 0) return;
        NavigateTo(ParentOf(_currentDir));
    }

    private void UpdatePathUi()
    {
        if (_listResult == null)
        {
            _pathBox.ReadOnly = true;
            _pathBox.Text = "";
            _pathBox.PlaceholderText = "尚未打开归档";
            _btnUp.Enabled = false;
            return;
        }
        _pathBox.ReadOnly = false;
        _pathBox.PlaceholderText = "归档内路径（回车跳转）";
        _pathBox.Text = _currentDir.Length == 0 ? "/" : "/" + _currentDir;
        _pathBox.SelectionStart = _pathBox.TextLength;
        _btnUp.Enabled = _currentDir.Length > 0 && !_busy;
    }

    private void OnPathBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter || _listResult == null) return;
        e.SuppressKeyPress = true;
        var raw = _pathBox.Text.Trim().Replace('\\', '/');
        var fileName = Path.GetFileName(_archivePath);
        if (raw.StartsWith(fileName + "/", StringComparison.OrdinalIgnoreCase))
            raw = raw[(fileName.Length + 1)..];
        raw = raw.Trim('/');
        if (raw.Length == 0)
        {
            NavigateTo("");
            return;
        }
        NavigateTo(raw);
    }

    private void OnFilterTextChanged(object? sender, EventArgs e)
    {
        if (_suppressFilterEvent) return;
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private void OnFilterBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            ClearFilter();
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            _filterTimer.Stop();
            ApplyFilter();
            e.SuppressKeyPress = true;
        }
    }

    private void ApplyFilter()
    {
        var text = _filterBox.Text.Trim();
        if (string.Equals(text, _filter, StringComparison.Ordinal)) return;
        _filter = text;
        _filterActive = text.Length > 0;
        RebuildView();
        if (_filterActive) SetStatus($"筛选“{text}”：{_view.Count} 项（全归档）");
    }

    private void ClearFilter()
    {
        _suppressFilterEvent = true;
        _filterBox.Text = "";
        _suppressFilterEvent = false;
        _filter = "";
        _filterActive = false;
        RebuildView();
    }

    // ————————————————————————————————————————————————
    // 状态 / 命令可用性
    // ————————————————————————————————————————————————
    private void UpdateStatus()
    {
        if (_listResult == null)
        {
            _lblStatus.Text = "就绪 — 打开或拖入一个归档文件";
            _lblElapsed.Text = "";
            SyncInfoStrip();
            UpdateEngineLabel();
            return;
        }

        var filtered = _filterActive ? $"  ·  筛选 {_view.Count} 项" : $"  ·  当前 {_view.Count} 项";
        var flags = (_archiveEncrypted ? "  ·  已加密" : "") + (_archiveReadOnly ? "  ·  只读" : "");
        var compressedSize = _archiveFileSize > 0 ? _archiveFileSize : _compressedTotal;
        _lblStatus.Text = $"{_fileCount} 个文件, {_dirCount} 个文件夹  /  总计 {FormatUtil.Size(_listResult.TotalSize)}  /  压缩后 {FormatUtil.Size(compressedSize)}{filtered}{flags}";
        _lblElapsed.Text = "耗时 " + (_listResult.Elapsed.TotalSeconds < 1
            ? $"{_listResult.Elapsed.TotalMilliseconds:0} ms"
            : $"{_listResult.Elapsed.TotalSeconds:0.00} s");
        SyncInfoStrip();
        UpdateEngineLabel();
        Program.TimingLog("UpdateStatus done");
    }

    /// <summary>把归档身份信息同步到顶部信息条，并按有无归档把该行展开/收起。</summary>
    private void SyncInfoStrip()
    {
        var hasArchive = _listResult != null;
        if (hasArchive)
        {
            var compressed = _archiveFileSize > 0 ? _archiveFileSize : _compressedTotal;
            _infoStrip.SetArchive(_archivePath, _listResult!.Format, _archiveEncrypted, _archiveReadOnly,
                _fileCount, _dirCount, _listResult.TotalSize, compressed);
        }
        else
        {
            _infoStrip.Clear();
        }

        float target = hasArchive ? _infoStrip.PreferredHeight : 0f;
        if (Math.Abs(_infoRow.Height - target) > 0.5f)
        {
            _infoRow.Height = target;
            _root.PerformLayout();
        }
    }

    private void SetStatus(string text)
    {
        _lblStatus.Text = text;
    }

    private void UpdateEngineLabel()
    {
        var service = _service;
        if (service is { EngineAvailable: true })
        {
            var version = service.EngineVersion;
            _lblEngine.Text = string.IsNullOrWhiteSpace(version) ? "引擎 7-Zip" : "引擎 " + version;
        }
        else
        {
            _lblEngine.Text = "引擎 未就绪";
        }
    }

    private void UpdateEmptyState()
    {
        string title;
        string sub;
        if (_listResult == null)
        {
            title = "没有打开的归档";
            if (_service == null)
                sub = "正在检测归档引擎…";
            else if (_service.EngineAvailable)
                sub = "把归档拖到这里，或按 Ctrl+O 打开\n支持 7Z / ZIP / TAR / GZ / BZ2 / XZ / RAR（只读）";
            else
                sub = "归档引擎未就绪\n请使用完整构建版本，或检查 Resources\\7z\\7z.exe 是否被隔离";
        }
        else if (_filterActive)
        {
            title = "没有匹配的条目";
            sub = "换个关键词，或按 Esc 清除筛选";
        }
        else
        {
            title = "此文件夹为空";
            sub = "按 Backspace 返回上级目录";
        }

        _list.EmptyText = title;
        _list.EmptySubText = sub;
    }

    private bool CanEditArchive()
    {
        if (_listResult == null) return false;
        if (_listResult.Format is ArchiveFormat.Rar or ArchiveFormat.Iso or ArchiveFormat.Wim or ArchiveFormat.Unknown)
            return false;
        if (_listResult.Format is ArchiveFormat.GZip or ArchiveFormat.BZip2 or ArchiveFormat.Xz)
            return false;
        return !IsMultipartArchive();
    }

    private bool IsMultipartArchive()
    {
        var name = Path.GetFileName(_archivePath);
        if (name.EndsWith(".001", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".002", StringComparison.OrdinalIgnoreCase)) return true;
        var dir = Path.GetDirectoryName(_archivePath);
        if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, name + ".001"))) return true;
        return false;
    }

    private void UpdateCommandStates()
    {
        var hasArchive = _listResult != null;
        var hasSelection = _list.SelectedCount > 0;
        var canEdit = CanEditArchive();

        _btnOpen.Enabled = !_busy;
        _btnCreate.Enabled = !_busy;
        _btnExtractTo.Enabled = hasArchive && !_busy;
        _btnExtractHere.Enabled = hasArchive && !_busy;
        _btnTest.Enabled = hasArchive && !_busy;
        _btnAdd.Enabled = canEdit && !_busy;
        _btnDelete.Enabled = canEdit && hasSelection && !_busy;
        _btnInfo.Enabled = hasArchive && !_busy;
        _btnRecover.Enabled = hasArchive && !_busy;
        _btnUp.Enabled = hasArchive && _currentDir.Length > 0 && !_busy;

        _miExtractTo.Enabled = hasArchive;
        _miExtractHere.Enabled = hasArchive;
        _miTest.Enabled = hasArchive;
        _miRecover.Enabled = hasArchive;
        _miAddFiles.Enabled = canEdit;
        _miAddFolder.Enabled = canEdit;
        _miDelete.Enabled = canEdit && hasSelection;
        _miInfo.Enabled = hasArchive;
    }

    // ————————————————————————————————————————————————
    // 打开 / 关闭归档
    // ————————————————————————————————————————————————
    private async Task OpenArchivePickerAsync()
    {
        if (_busy) return;
        using var dialog = new OpenFileDialog
        {
            Title = "打开归档",
            Filter = "支持的归档|*.7z;*.zip;*.tar;*.gz;*.tgz;*.bz2;*.tbz2;*.xz;*.txz;*.rar;*.iso;*.wim;*.cab|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = string.IsNullOrEmpty(_archivePath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : Path.GetDirectoryName(_archivePath) ?? "",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await OpenArchiveAsync(dialog.FileName);
    }

    private async Task OpenArchiveAsync(string path)
    {
        if (_busy) return;
        await RunBusyAsync("正在读取归档…", async () => { await LoadArchiveCoreAsync(path); });
    }

    private async Task<bool> LoadArchiveCoreAsync(string path, string? knownPassword = null)
    {
        if (!EnsureEngine()) return false;
        var service = _service!;

        Program.TimingLog("LoadArchive start");
        string full;
        try { full = Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception ex)
        {
            MsgBox.Error(this, "路径无效：" + ex.Message, "无法打开归档");
            return false;
        }
        if (!File.Exists(full))
        {
            MsgBox.Error(this, "文件不存在：\n" + full, "无法打开归档");
            return false;
        }

        ListResult? result = null;
        Exception? lastError = null;
        string? password = knownPassword;

        if (knownPassword == null && _preloadTask != null && _preloadPath != null &&
            string.Equals(_preloadPath, full, StringComparison.OrdinalIgnoreCase))
        {
            Program.TimingLog("await preload");
            try
            {
                var preload = await _preloadTask.ConfigureAwait(true);
                if (preload.Error == null && preload.Result != null) result = preload.Result;
                else Program.TimingLog("preload failed: " + preload.Error?.Message);
            }
            catch (Exception ex)
            {
                Program.TimingLog("preload exception: " + ex.Message);
            }
            finally
            {
                _preloadTask = null;
                _preloadPath = null;
            }
        }

        Program.TimingLog("ListAsync begin");

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                result = await service.ListAsync(full, password).ConfigureAwait(true);
                lastError = null;
                break;
            }            catch (ArchiveException ex) when (IsPasswordError(ex))
            {
                lastError = ex;
                if (attempt == 2) break;
                var entered = PasswordDialog.Ask(this, full,
                    attempt == 0 ? "该归档已加密，请输入密码：" : "密码不正确，请重新输入：");
                if (entered == null) return false;
                password = entered;
            }
        }

        if (result == null)
        {
            MsgBox.Error(this, lastError?.Message ?? "无法读取该归档。", "无法打开归档");
            return false;
        }

        Program.TimingLog("ListAsync returned");

        _archivePassword = password;
        _listResult = result;
        _archivePath = full;
        _currentDir = "";
        _filter = "";
        _filterActive = false;
        _suppressFilterEvent = true;
        _filterBox.Text = "";
        _suppressFilterEvent = false;
        _sortColumn = 0;
        _sortAscending = true;

        // 计数直接来自引擎列表结果：状态栏先出现，目录树/排序在后台线程完成。
        Text = $"{Path.GetFileName(full)} — REAArchive";
        _lblStatus.Text = $"{result.FileCount} 个文件, {result.DirCount} 个文件夹 / 正在载入列表…";
        _view = new List<EntryRow>();
        _list.RefreshRows();
        _list.EmptyText = "正在载入列表…";
        _list.EmptySubText = "";
        UpdateCommandStates();
        Program.TimingLog("provisional status");

        var tree = await Task.Run(() => BuildTreeData(result.Entries)).ConfigureAwait(true);
        ApplyTree(tree);
        _archiveEncrypted = result.IsEncrypted;
        _archiveReadOnly = !CanEditArchive();
        try { _archiveFileSize = new FileInfo(full).Length; } catch { _archiveFileSize = 0; }
        Program.TimingLog($"tree built {tree.FileCount}");
        UpdatePathUi();

        var initialView = await Task.Run(() => BuildInitialView(tree, _sortColumn, _sortAscending))
            .ConfigureAwait(true);
        _view = initialView;
        _list.SortColumnIndex = _sortColumn;
        _list.SortAscending = _sortAscending;
        _list.RefreshRows();
        _list.Invalidate();
        UpdateEmptyState();
        UpdateStatus();
        UpdateCommandStates();
        _list.Focus();
        Program.TimingLog($"LoadArchive UI done retrieve={_list.RetrieveCount}");
        return true;
    }

    /// <summary>窗口还没显示时就开始列表：把 7z 的启动/解析时间与 WinForms 初始化重叠。</summary>
    private void TryStartPreload()
    {
        var args = _args.Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
        string? path = null;
        if (args.Length >= 2 && args[0] is "--extract-here" or "--extract-to" or "--extract-ask"
                or "--test" or "--extract" or "-x")
            path = args[1];
        else if (args.Length > 0 && !args[0].StartsWith('-')) path = args[0];
        if (path == null) return;

        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            if (!File.Exists(full)) return;
            _preloadPath = full;
            _preloadTask = Task.Run(async () =>
            {
                try
                {
                    var service = EngineBridge.Service;
                    if (service is not { EngineAvailable: true }) return new PreloadResult(null, null);
                    var listing = await service.ListAsync(full, null).ConfigureAwait(false);
                    return new PreloadResult(listing, null);
                }
                catch (Exception ex)
                {
                    return new PreloadResult(null, ex);
                }
            });
        }
        catch
        {
            _preloadTask = null;
            _preloadPath = null;
        }
    }

    private static bool IsPasswordError(ArchiveException ex)
    {
        var message = ex.Message ?? "";
        return message.Contains("密码", StringComparison.Ordinal) ||
               message.Contains("加密", StringComparison.Ordinal) ||
               message.Contains("password", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshArchiveAsync()
    {
        if (_listResult == null || _busy) return;
        var dir = _currentDir;
        await RunBusyAsync("正在刷新归档…", async () =>
        {
            if (!await LoadArchiveCoreAsync(_archivePath, _archivePassword)) return;
            if (dir.Length > 0 && _children.ContainsKey(dir)) NavigateTo(dir);
        });
    }

    // ————————————————————————————————————————————————
    // 解压
    // ————————————————————————————————————————————————
    private async Task ExtractArchiveAsync(bool here)
    {
        if (_listResult == null)
        {
            MsgBox.Info(this, "请先打开一个归档。", "没有归档");
            return;
        }
        if (_busy) return;

        var selected = SelectedRows();
        if (here)
        {
            var destination = Path.GetDirectoryName(_archivePath) ?? Environment.CurrentDirectory;
            var password = ResolvePasswordForOperation();
            if (_listResult.IsEncrypted && password == null) return;
            await RunBusyAsync("正在解压…", () => ExtractCoreAsync(destination, selected, _settings.GetOverwritePolicy(),
                password, preservePaths: true, openAfter: false));
            return;
        }

        var options = ExtractDialog.Show(this, _archivePath, _settings, selected.Count,
            _listResult.IsEncrypted, _archivePassword);
        if (options == null) return;
        await RunBusyAsync("正在解压…", () => ExtractCoreAsync(options.Destination, selected, options.Policy,
            options.Password, options.PreservePaths, options.OpenAfter));
    }

    private async Task<bool> ExtractCoreAsync(string destination, IReadOnlyList<EntryRow> selected, OverwritePolicy policy,
        string? password, bool preservePaths, bool openAfter, bool showSummary = true)
    {
        var service = _service!;
        var selectedPaths = ExpandSelection(selected);
        ExtractResult? result = null;
        await Task.Yield();

        var totals = SelectionTotals(selected);
        if (!ConfirmDiskSpace(destination, totals.Bytes)) return false;

        ProgressDialog.Run(this, "正在解压归档", async (progress, ct) =>
        {
            var options = new ExtractOptions
            {
                Destination = destination,
                SelectedPaths = selectedPaths.Count > 0 ? selectedPaths : null,
                Password = password,
                Overwrite = policy,
                PreservePaths = preservePaths,
                CreateDestination = true,
            };
            result = await service.ExtractAsync(_archivePath, options, progress, ct).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(password)) _archivePassword = password;
        });

        if (result == null) return false;
        var message = $"已解压 {result.Files} 个文件，共 {FormatUtil.Size(result.Bytes)}，耗时 {UiFormat.Duration(result.Elapsed)}。\n\n目标文件夹：\n{destination}";
        if (result.Warnings.Count > 0)
        {
            message += $"\n\n有 {result.Warnings.Count} 条警告：\n" +
                       string.Join("\n", result.Warnings.Take(6).Select(w => "• " + w));
            if (result.Warnings.Count > 6) message += "\n……";
        }
        if (openAfter) OpenFolder(destination);
        SetStatus($"解压完成：{result.Files} 个文件 / {FormatUtil.Size(result.Bytes)}");
        if (showSummary) MsgBox.Info(this, message, "解压完成");
        return true;
    }

    private List<string> ExpandSelection(IReadOnlyList<EntryRow> selected)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in selected)
        {
            if (!row.IsDirectory)
            {
                if (seen.Add(row.Path)) paths.Add(row.Path);
                continue;
            }
            foreach (var child in _allRows)
            {
                if (child.IsDirectory) continue;
                if (child.Path.StartsWith(row.Path + "/", StringComparison.OrdinalIgnoreCase) && seen.Add(child.Path))
                    paths.Add(child.Path);
            }
            if (seen.Add(row.Path)) paths.Add(row.Path);
        }
        return paths;
    }

    /// <summary>选中条目（含目录展开）的条目数与解压后总字节。</summary>
    private (int Items, long Bytes) SelectionTotals(IReadOnlyList<EntryRow> selected)
    {
        if (_listResult == null) return (0, 0);
        if (selected.Count == 0) return (_listResult.Entries.Count, _listResult.TotalSize);

        var selectedPaths = new HashSet<string>(selected.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
        var prefixes = selected.Where(r => r.IsDirectory).Select(r => r.Path + "/").ToList();
        int items = 0;
        long bytes = 0;
        foreach (var row in _allRows)
        {
            bool match = selectedPaths.Contains(row.Path);
            if (!match)
            {
                foreach (var prefix in prefixes)
                {
                    if (row.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                }
            }
            if (!match) continue;
            items++;
            if (!row.IsDirectory) bytes += row.Size;
        }
        return (items, bytes);
    }

    /// <summary>解压前检查目标盘空间；不足时让用户确认（B08）。</summary>
    private bool ConfirmDiskSpace(string destination, long requiredBytes)
    {
        if (requiredBytes <= 0) return true;
        long free;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destination));
            if (string.IsNullOrEmpty(root)) return true;
            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return true;
        }

        const long margin = 16L * 1024 * 1024;
        if (free <= 0 || requiredBytes + margin <= free) return true;

        return MsgBox.Confirm(this,
            $"预计解压后占用 {FormatUtil.Size(requiredBytes)}，目标磁盘剩余 {FormatUtil.Size(free)}。\n\n" +
            "空间可能不足，仍要继续吗？", "磁盘空间可能不足", danger: true);
    }

    private async Task PreviewFileAsync(EntryRow row)
    {
        if (_busy) return;
        if (!EnsureEngine()) return;
        var password = ResolvePasswordForOperation();
        if (_listResult!.IsEncrypted && password == null) return;

        string root;
        try { root = CreateTempRoot("preview"); }
        catch (Exception ex)
        {
            MsgBox.Error(this, "无法创建临时目录：" + ex.Message, "预览失败");
            return;
        }

        await RunBusyAsync("正在准备预览…", async () =>
        {
            await Task.Yield();
            try
            {
                ProgressDialog.Run(this, "正在解压预览文件", async (progress, ct) =>
                {
                    await _service!.ExtractAsync(_archivePath, new ExtractOptions
                    {
                        Destination = root,
                        SelectedPaths = new[] { row.Path },
                        Password = password,
                        Overwrite = OverwritePolicy.Overwrite,
                    }, progress, ct).ConfigureAwait(true);
                });
            }
            catch (OperationCanceledException)
            {
                SetStatus("已取消预览");
                return;
            }
            catch (ArchiveException ex)
            {
                MsgBox.Error(this, ex.Message, "预览失败");
                return;
            }

            var target = Path.Combine(root, row.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(target))
            {
                MsgBox.Warn(this, "解压后的文件不存在：\n" + target, "无法预览");
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
                SetStatus("已用系统默认程序打开预览");
            }
            catch (Exception ex)
            {
                MsgBox.Warn(this, "无法打开文件：" + ex.Message + "\n\n已打开临时文件夹。", "无法打开");
                OpenFolder(Path.GetDirectoryName(target) ?? root);
            }
        });
    }

    // ————————————————————————————————————————————————
    // 测试
    // ————————————————————————————————————————————————
    private Task TestArchiveAsync() => TestArchiveCoreAsync(closeAfter: false);

    /// <summary>密码破译：本地组合尝试找回密码；找到后可立即用于解压。</summary>
    private async Task RecoverPasswordAsync()
    {
        if (_busy || _listResult == null) return;

        using var dialog = new RecoveryDialog(_archivePath, _listResult.Format, _service);
        var result = dialog.ShowDialog(this);

        var found = dialog.FoundPassword;
        if (string.IsNullOrEmpty(found))
        {
            if (result == DialogResult.OK) UpdateCommandStates();
            await Task.CompletedTask;
            return;
        }

        _archivePassword = found;
        SetStatus("已找到密码并记入本次会话 —— 直接「解压到…」即可，不必再输入。");
        UpdateCommandStates();

        if (MsgBox.Confirm(this, "密码已找到。现在用它解压这个归档吗？"))
            await ExtractArchiveAsync(here: false);
    }    private async Task TestArchiveCoreAsync(bool closeAfter)
    {
        if (_listResult == null)
        {
            MsgBox.Info(this, "请先打开一个归档。", "没有归档");
            return;
        }
        if (_busy || !EnsureEngine()) return;

        var password = ResolvePasswordForOperation();
        if (_listResult.IsEncrypted && password == null) return;

        await RunBusyAsync("正在测试归档…", async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            bool ok = false;
            ProgressDialog.Run(this, "正在测试归档完整性", async (progress, ct) =>
            {
                ok = await _service!.TestAsync(_archivePath, password, progress, ct).ConfigureAwait(true);
            });
            stopwatch.Stop();
            SetStatus(ok ? "测试通过" : "测试未通过");
            MsgBox.Info(this, ok
                ? $"测试通过：未发现错误。\n\n耗时 {UiFormat.Duration(stopwatch.Elapsed)}"
                : "测试未通过：归档可能存在损坏。", ok ? "测试完成" : "测试失败");
            await Task.CompletedTask;
        });

        if (closeAfter) Close();
    }

    // ————————————————————————————————————————————————
    // 新建 / 添加 / 删除
    // ————————————————————————————————————————————————
    private async Task ShowCreateAsync(IEnumerable<string>? sources = null)
    {
        if (_busy) return;
        if (!EnsureEngine()) return;

        var options = CreateDialog.Show(this, _settings, sources);
        if (options == null) return;

        await RunBusyAsync("正在创建归档…", async () =>
        {
            ExtractResult? created = null;
            ProgressDialog.Run(this, "正在创建归档", async (progress, ct) =>
            {
                created = await _service!.CreateAsync(options, progress, ct).ConfigureAwait(true);
            });

            if (File.Exists(options.ArchivePath))
                await LoadArchiveCoreAsync(options.ArchivePath, options.Password);

            var summary = created == null
                ? "归档已创建。"
                : $"已写入 {created.Files} 个文件，共 {FormatUtil.Size(created.Bytes)}，耗时 {UiFormat.Duration(created.Elapsed)}。";
            SetStatus("归档已创建：" + options.ArchivePath);
            MsgBox.Info(this, summary + "\n\n" + options.ArchivePath, "创建完成");
        });
    }

    private async Task AddFilesInteractiveAsync()
    {
        if (_listResult == null)
        {
            await ShowCreateAsync();
            return;
        }
        if (_busy) return;
        if (!EnsureEditable()) return;

        using var dialog = new OpenFileDialog
        {
            Title = "选择要添加到归档的文件",
            Multiselect = true,
            Filter = "所有文件 (*.*)|*.*",
            InitialDirectory = Path.GetDirectoryName(_archivePath) ?? "",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await AddSourcesAsync(dialog.FileNames);
    }

    private async Task AddFolderInteractiveAsync()
    {
        if (_listResult == null)
        {
            await ShowCreateAsync();
            return;
        }
        if (_busy) return;
        if (!EnsureEditable()) return;

        using var dialog = new FolderBrowserDialog
        {
            Description = "选择要添加到归档的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await AddSourcesAsync(new[] { dialog.SelectedPath });
    }

    private async Task AddSourcesAsync(IReadOnlyList<string> sources)
    {
        if (_listResult == null)
        {
            await ShowCreateAsync(sources);
            return;
        }
        if (_busy || sources.Count == 0) return;
        if (!EnsureEditable()) return;

        var password = ResolvePasswordForOperation();
        if (_listResult.IsEncrypted && password == null) return;

        var preview = string.Join("\n", sources.Take(5).Select(s => "• " + s));
        if (sources.Count > 5) preview += $"\n…… 另外 {sources.Count - 5} 项";
        if (!MsgBox.Confirm(this, $"将以下内容添加到：\n{_archivePath}\n\n{preview}\n\n归档会就地更新。", "添加到归档"))
            return;

        var archive = _archivePath;
        await RunBusyAsync("正在添加到归档…", async () =>
        {
            ExtractResult? added = null;
            ProgressDialog.Run(this, "正在添加文件到归档", async (progress, ct) =>
            {
                added = await _service!.AddAsync(new AddOptions
                {
                    ArchivePath = archive,
                    Sources = sources.ToList(),
                    Password = password,
                    Level = 5,
                    BaseDirectory = null,
                }, progress, ct).ConfigureAwait(true);
            });

            await LoadArchiveCoreAsync(archive, password);
            var summary = added == null ? "" : $"已添加 {added.Files} 个文件，共 {FormatUtil.Size(added.Bytes)}。";
            SetStatus("已添加到归档");
            MsgBox.Info(this, summary + "\n\n" + archive, "添加完成");
        });
    }

    private async Task DeleteSelectedAsync()
    {
        if (_listResult == null) return;
        if (_busy) return;
        if (!EnsureEditable()) return;

        var selected = SelectedRows();
        if (selected.Count == 0)
        {
            MsgBox.Info(this, "请先选择要删除的条目。", "没有选中条目");
            return;
        }

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in selected)
        {
            if (seen.Add(row.Path)) paths.Add(row.Path);
            if (!row.IsDirectory) continue;
            foreach (var child in _allRows)
            {
                if (child.IsDirectory) continue;
                if (child.Path.StartsWith(row.Path + "/", StringComparison.OrdinalIgnoreCase) && seen.Add(child.Path))
                    paths.Add(child.Path);
            }
        }

        var names = string.Join("\n", selected.Take(6).Select(r => "• " + r.Path));
        if (selected.Count > 6) names += $"\n…… 另外 {selected.Count - 6} 项";
        if (!MsgBox.Confirm(this,
                $"将从归档中永久删除以下条目：\n{_archivePath}\n\n{names}\n\n删除后无法撤销。是否继续？",
                "删除条目", danger: true)) return;

        var password = ResolvePasswordForOperation();
        if (_listResult.IsEncrypted && password == null) return;

        var archive = _archivePath;
        await RunBusyAsync("正在删除条目…", async () =>
        {
            DeleteResult? deleted = null;
            ProgressDialog.Run(this, "正在从归档中删除条目", async (progress, ct) =>
            {
                deleted = await _service!.DeleteAsync(archive, paths, password, progress, ct).ConfigureAwait(true);
            });

            await LoadArchiveCoreAsync(archive, password);
            SetStatus($"已删除 {deleted?.Removed ?? 0} 个条目");
        });
    }

    private bool EnsureEditable()
    {
        if (_listResult == null) return false;
        if (IsMultipartArchive())
        {
            MsgBox.Warn(this, "分卷归档暂不支持直接添加/删除。\n请先解压后用“新建归档”重新打包。", "分卷归档");
            return false;
        }
        if (!CanEditArchive())
        {
            var reason = _listResult.Format switch
            {
                ArchiveFormat.Rar => "RAR 在本程序中为只读格式。",
                ArchiveFormat.Iso => "ISO 在本程序中为只读格式。",
                ArchiveFormat.Wim => "WIM 在本程序中为只读格式。",
                ArchiveFormat.GZip or ArchiveFormat.BZip2 or ArchiveFormat.Xz =>
                    "GZ / BZ2 / XZ 是单文件压缩流，不能直接添加或删除条目。",
                _ => "该归档格式不支持编辑。",
            };
            MsgBox.Warn(this, reason + "\n\n请使用“新建归档”重新打包。", "不能编辑");
            return false;
        }
        return true;
    }

    // ————————————————————————————————————————————————
    // 信息
    // ————————————————————————————————————————————————
    private void ShowEntryInfo()
    {
        if (_listResult == null)
        {
            MsgBox.Info(this, "请先打开一个归档。", "没有归档");
            return;
        }

        var selected = SelectedRows();
        var entries = (selected.Count > 0
                ? selected.Select(ToEntryInfo)
                : _listResult.Entries)
            .ToList();
        EntryInfoDialog.Show(this, _archivePath, _listResult.Format, EngineBridge.EngineText, entries);
    }

    private static ArchiveEntryInfo ToEntryInfo(EntryRow row) => new()
    {
        Path = row.Path,
        IsDirectory = row.IsDirectory,
        Size = row.Size,
        CompressedSize = row.CompressedSize,
        Modified = row.Modified,
        Encrypted = row.Encrypted,
        Method = row.Method,
        Crc = row.Crc,
    };

    // ————————————————————————————————————————————————
    // 设置 / 工具 / 关于
    // ————————————————————————————————————————————————
    private void ShowSettings()
    {
        var updated = SettingsDialog.Show(this, _settings);
        if (updated == null) return;
        if (!SettingsStore.Save(updated, out var error))
        {
            MsgBox.Error(this, "设置保存失败：" + error, "保存失败");
            return;
        }
        _settings = updated;
        Theme.UiFontSize = updated.FontSize;
        ApplyUiFont();
        UpdateStatus();
        SetStatus("设置已保存");
    }

    private void ApplyUiFont()
    {
        Font = Theme.Ui();
        _menu.Font = Theme.Ui();
        ApplyMenuFont(_menu);
        _list.Font = Theme.Ui();
        _pathBox.Font = Theme.Ui();
        _filterBox.Font = Theme.Ui();
        _lblStatus.Font = Theme.Ui();
        _lblEngine.Font = Theme.Ui();
        _lblElapsed.Font = Theme.Ui();
        _btnUp.Font = Theme.Ui();
        foreach (var button in new[] { _btnOpen, _btnExtractTo, _btnExtractHere, _btnTest, _btnCreate, _btnAdd, _btnDelete, _btnInfo })
            button.Font = Theme.Ui();

        _toolbarRow.Height = Math.Max(50f, 34f + _settings.FontSize * 3f);
        _pathRow.Height = Math.Max(46f, 26f + _settings.FontSize * 3.2f);
        _statusRow.Height = Math.Max(30f, 16f + _settings.FontSize * 2.4f);
        _list.Invalidate();
        AdjustNameColumn();
    }

    private static void ApplyMenuFont(ToolStripMenuItem parent)
    {
        foreach (ToolStripItem item in parent.DropDownItems)
        {
            item.Font = Theme.Ui();
            if (item is ToolStripMenuItem child) ApplyMenuFont(child);
        }
    }

    private void ApplyMenuFont(MenuStrip menu)
    {
        foreach (ToolStripItem item in menu.Items)
        {
            item.Font = Theme.Ui();
            if (item is ToolStripMenuItem child) ApplyMenuFont(child);
        }
    }

    private void OpenDefaultAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
            SetStatus("已打开 Windows 默认应用设置；请为 .7z / .zip 选择 REAArchive");
        }
        catch (Exception ex)
        {
            MsgBox.Warn(this,
                "无法打开 Windows 默认应用设置：" + ex.Message +
                "\n\n可以手动打开：设置 → 应用 → 默认应用。", "无法打开设置");
        }
    }

    private void InstallShellMenu()
    {
        if (!MsgBox.Confirm(this,
                "将在当前用户注册表中写入 REAArchive 右键菜单、ProgID 与“打开方式”候选：\n\n" +
                ShellIntegration.RegistryPreview +
                "\n\n仅写 HKCU 当前用户，不改文件关联、不写系统目录，可一键卸载。\n" +
                "Windows 不允许程序自行修改默认打开方式；安装后可在默认应用设置里手动选择 REAArchive。\n\n是否安装？",
                "安装右键菜单"))
            return;
        if (ShellIntegration.Install(Application.ExecutablePath, out var error))
            MsgBox.Info(this,
                "右键菜单已安装。\n资源管理器可能需要几秒刷新。\n\n" +
                "在右键菜单中可以看到以 REAArchive: 开头的菜单项；\n" +
                "如需双击默认用 REAArchive 打开，请到“设置 → 应用 → 默认应用”中为 .7z/.zip 选择 REAArchive。",
                "安装成功");
        else
            MsgBox.Error(this, error, "安装失败");
    }

    private void UninstallShellMenu()
    {
        if (!ShellIntegration.IsInstalled())
        {
            MsgBox.Info(this, "没有检测到完整的 REAArchive 右键菜单/ProgID。", "卸载右键菜单");
            return;
        }
        if (!MsgBox.Confirm(this, "将删除本程序写入 HKCU 的右键菜单、ProgID 与 OpenWithProgids 值，不会影响你的文件。是否卸载？", "卸载右键菜单"))
            return;
        if (ShellIntegration.Uninstall(out var error))
            MsgBox.Info(this, "右键菜单已卸载（0 残留）。资源管理器可能需要几秒刷新。", "卸载成功");
        else
            MsgBox.Error(this, error, "卸载失败");
    }

    private void OpenTempFolder()
    {
        var root = _settings.ResolvedTempDir();
        try { Directory.CreateDirectory(root); } catch { }
        if (!Directory.Exists(root))
        {
            MsgBox.Warn(this, "临时目录不存在：" + root);
            return;
        }
        OpenFolder(root);
    }

    private void CleanTempFiles()
    {
        var root = _settings.ResolvedTempDir();
        if (!Directory.Exists(root))
        {
            MsgBox.Info(this, "临时目录已经是空的。", "清理临时文件");
            return;
        }
        if (!MsgBox.Confirm(this, "将删除本程序的临时文件夹内容：\n" + root + "\n\n不会影响任何归档文件。是否继续？", "清理临时文件"))
            return;
        try
        {
            foreach (var dir in Directory.GetDirectories(root)) Directory.Delete(dir, recursive: true);
            foreach (var file in Directory.GetFiles(root)) File.Delete(file);
            lock (_tempRoots) _tempRoots.Clear();
            MsgBox.Info(this, "临时文件已清理。", "清理完成");
        }
        catch (Exception ex)
        {
            MsgBox.Warn(this, "有部分临时文件正在使用，未能全部删除：" + ex.Message, "清理未完成");
        }
    }

    private void ShowEngineInfo()
    {
        if (_service is { EngineAvailable: true })
        {
            var version = _service.EngineVersion;
            MsgBox.Info(this,
                "归档引擎：" + (string.IsNullOrWhiteSpace(version) ? "7-Zip" : version) +
                "\n引擎文件：Resources\\7z\\7z.exe" +
                "\n\n本程序不联网、不检查更新、无广告、无遥测。", "引擎信息");
        }
        else
        {
            MsgBox.Warn(this, EngineBridge.Error, "引擎未就绪");
        }
    }

    private void ShowAbout()
    {
        using var dialog = new AboutDialog(EngineBridge.EngineText.Replace("引擎 ", ""));
        dialog.ShowDialog(this);
    }

    // ————————————————————————————————————————————————
    // 拖放 / 键盘 / 窗口
    // ————————————————————————————————————————————————
    private static void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (_busy) return;
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        if (_listResult == null) RunUi(() => ShowCreateAsync(paths));
        else RunUi(() => AddSourcesAsync(paths));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var textFocused = _filterBox.Focused || _pathBox.Focused;
        switch (keyData)
        {
            case Keys.Control | Keys.A when !textFocused:
                SelectAllEntries();
                return true;
            case Keys.Delete when !textFocused:
                RunUi(DeleteSelectedAsync);
                return true;
            case Keys.Back when !textFocused:
                GoUp();
                return true;
            case Keys.F5:
                RunUi(RefreshArchiveAsync);
                return true;
            case Keys.Escape when _filterBox.Focused:
                ClearFilter();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ————————————————————————————————————————————————
    // 单实例转发（WM_COPYDATA）
    // ————————————————————————————————————————————————
    private const int WmCopyData = 0x004A;
    private const int CopyDataMagic = 0x52454131;

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmCopyData && m.LParam != IntPtr.Zero)
        {
            var data = Marshal.PtrToStructure<CopyDataStruct>(m.LParam);
            if (data.dwData == new IntPtr(CopyDataMagic) && data.lpData != IntPtr.Zero && data.cbData > 0)
            {
                var text = Marshal.PtrToStringUni(data.lpData, data.cbData / 2)?.TrimEnd('\0') ?? "";
                var args = text.Length == 0 ? Array.Empty<string>() : text.Split('\u0001');
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
                BeginInvoke(new Action(() => RunUi(() => QueueCommandsAsync(args, closeAfter: false))));
                m.Result = new IntPtr(1);
                return;
            }
        }
        base.WndProc(ref m);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            Program.TimingLog("OnShown start");
            // 版本探测会启动 7z 子进程，放到线程池，避免主窗口显示前卡住。
            var probe = await Task.Run(() =>
            {
                var service = EngineBridge.Service;
                string? version = null;
                if (service is { EngineAvailable: true })
                {
                    try { version = service.EngineVersion; } catch { version = null; }
                }
                return (Service: service, Version: version);
            });
            Program.TimingLog("engine probe done");

            _service = probe.Service;
            UpdateEngineLabel();
            UpdateEmptyState();
            _lblStatus.Text = _service is { EngineAvailable: true }
                ? "就绪 — 打开或拖入一个归档文件"
                : "引擎未就绪";

            await QueueCommandsAsync(_args, closeAfter: true);
            Program.TimingLog("startup handled");
        }
        catch (Exception ex)
        {
            ShowUnexpected(ex);
        }
        finally
        {
            if (_listResult == null) _list.Focus();
        }
    }

    private async Task QueueCommandsAsync(string[] args, bool closeAfter)
    {
        lock (_pendingCommands) _pendingCommands.Enqueue((args ?? Array.Empty<string>(), closeAfter));
        if (_commandsRunning) return;
        _commandsRunning = true;
        try
        {
            while (true)
            {
                (string[] Args, bool CloseAfter) next;
                lock (_pendingCommands)
                {
                    if (_pendingCommands.Count == 0) break;
                    next = _pendingCommands.Dequeue();
                }
                await HandleCommandArgsAsync(next.Args, next.CloseAfter);
            }
        }
        finally
        {
            _commandsRunning = false;
        }
    }

    private async Task HandleCommandArgsAsync(string[] rawArgs, bool closeAfter)
    {
        var args = rawArgs.Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
        if (args.Length == 0)
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            return;
        }

        _service ??= EngineBridge.Service;
        UpdateEngineLabel();
        UpdateEmptyState();

        switch (args[0])
        {
            case "--settings":
                ShowSettings();
                return;
            case "--about":
                ShowAbout();
                return;

            case "--extract":
            case "--extract-ask":
            case "-x":
                if (args.Length < 2) { MsgBox.Warn(this, "缺少归档路径。", "参数错误"); return; }
                if (!await EnsureArchiveLoadedAsync(args[1])) return;
                await ExtractArchiveAsync(here: false);
                return;

            case "--extract-here":
                if (args.Length < 2) { MsgBox.Warn(this, "缺少归档路径。", "参数错误"); return; }
                if (!await EnsureArchiveLoadedAsync(args[1])) return;
                await ExtractShellAsync(here: true, closeAfter);
                return;

            case "--extract-to":
                if (args.Length < 2) { MsgBox.Warn(this, "缺少归档路径。", "参数错误"); return; }
                if (!await EnsureArchiveLoadedAsync(args[1])) return;
                await ExtractShellAsync(here: false, closeAfter);
                return;

            case "--test":
                if (args.Length < 2) { MsgBox.Warn(this, "缺少归档路径。", "参数错误"); return; }
                if (!await EnsureArchiveLoadedAsync(args[1])) return;
                await TestArchiveCoreAsync(closeAfter);
                return;

            case "--compress-7z":
                if (args.Length < 2) { MsgBox.Warn(this, "缺少要压缩的路径。", "参数错误"); return; }
                await QuickCompressAsync(args[1], ArchiveFormat.SevenZip, closeAfter);
                return;
            case "--compress-zip":
                if (args.Length < 2) { MsgBox.Warn(this, "缺少要压缩的路径。", "参数错误"); return; }
                await QuickCompressAsync(args[1], ArchiveFormat.Zip, closeAfter);
                return;

            case "--compress":
            case "-c":
                if (args.Length < 2) { MsgBox.Warn(this, "缺少要压缩的路径。", "参数错误"); return; }
                await ShowCreateAsync(new[] { args[1] });
                return;
        }

        if (args[0].StartsWith('-'))
        {
            MsgBox.Warn(this, "未知参数：" + args[0], "参数错误");
            return;
        }
        await OpenArchiveAsync(args[0]);
    }

    private async Task<bool> EnsureArchiveLoadedAsync(string path)
    {
        string full;
        try { full = Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception ex)
        {
            MsgBox.Error(this, "路径无效：" + ex.Message, "无法打开归档");
            return false;
        }

        if (_listResult != null && string.Equals(_archivePath, full, StringComparison.OrdinalIgnoreCase))
            return true;
        await OpenArchiveAsync(full);
        return _listResult != null;
    }

    /// <summary>右键“解压到当前文件夹/同名文件夹”：无选择框，只显示进度。</summary>
    private async Task ExtractShellAsync(bool here, bool closeAfter)
    {
        if (_listResult == null) return;
        var parent = Path.GetDirectoryName(_archivePath) ?? Environment.CurrentDirectory;
        var destination = here
            ? parent
            : Path.Combine(parent, ExtractDialog.StripArchiveExtensions(Path.GetFileName(_archivePath)));

        var password = ResolvePasswordForOperation();
        if (_listResult.IsEncrypted && password == null) return;

        bool success = false;
        await RunBusyAsync("正在解压…", async () =>
        {
            success = await ExtractCoreAsync(destination, Array.Empty<EntryRow>(),
                _settings.GetOverwritePolicy(), password, preservePaths: true, openAfter: false, showSummary: false);
        });

        if (!success) return;
        SetStatus("解压完成：" + destination);
        if (closeAfter)
        {
            await Task.Delay(700);
            Close();
        }
    }

    private async Task QuickCompressAsync(string sourcePath, ArchiveFormat format, bool closeAfter)
    {
        if (_busy || !EnsureEngine()) return;

        string full;
        try { full = Path.GetFullPath(sourcePath.Trim().Trim('"')); }
        catch (Exception ex)
        {
            MsgBox.Error(this, "路径无效：" + ex.Message, "无法压缩");
            return;
        }
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            MsgBox.Error(this, "源文件不存在：\n" + full, "无法压缩");
            return;
        }

        var trimmed = Path.TrimEndingDirectorySeparator(full);
        var isDirectory = Directory.Exists(full);
        var stem = isDirectory ? new DirectoryInfo(trimmed).Name : Path.GetFileNameWithoutExtension(full);
        var parent = Path.GetDirectoryName(trimmed) ?? Environment.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(stem)) stem = "归档";
        var output = Path.Combine(parent, stem + FormatUtil.ExtensionOf(format));

        if (File.Exists(output))
        {
            if (closeAfter)
            {
                // 右键菜单调用：不弹模态框（否则资源管理器里会卡住一个窗口），自动改名 "名字 (n).ext"。
                string ext = FormatUtil.ExtensionOf(format);
                for (int n = 1; n <= 999 && File.Exists(output); n++)
                {
                    output = Path.Combine(parent, $"{stem} ({n}){ext}");
                }
                if (File.Exists(output))
                {
                    SetStatus("同名归档过多，已取消：" + stem + ext);
                    if (closeAfter) Close();
                    return;
                }
            }
            else if (!MsgBox.Confirm(this, "目标归档已存在：\n" + output + "\n\n继续将覆盖重建该归档。", "覆盖已有归档"))
            {
                return;
            }
        }

        var options = new CreateOptions
        {
            ArchivePath = output,
            Format = format,
            Level = _settings.CompressionLevel,
            Sources = new[] { full },
            BaseDirectory = parent,
        };

        bool success = false;
        await RunBusyAsync("正在压缩…", async () =>
        {
            await Task.Yield();
            ExtractResult? created = null;
            ProgressDialog.Run(this, "正在创建归档", async (progress, ct) =>
            {
                created = await _service!.CreateAsync(options, progress, ct).ConfigureAwait(true);
            });
            if (created == null) return;
            success = true;
            SetStatus("已创建：" + output);
            if (!closeAfter)
            {
                MsgBox.Info(this,
                    $"已写入 {created.Files} 个文件，共 {FormatUtil.Size(created.Bytes)}，耗时 {UiFormat.Duration(created.Elapsed)}。\n\n{output}",
                    "压缩完成");
            }
        });

        if (success && closeAfter)
        {
            // 右键菜单调用：给用户 1 秒看到窗口再自动关闭，不留一个需要手动关的窗口。
            await Task.Delay(1000);
            Close();
        }
    }

    private async Task RunBusyAsync(string status, Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        SetBusyUi(true, status);
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消");
        }
        catch (ArchiveException ex)
        {
            MsgBox.Error(this, ex.Message, "操作失败");
        }
        catch (Exception ex)
        {
            ShowUnexpected(ex);
        }
        finally
        {
            _busy = false;
            SetBusyUi(false, null);
            UpdateStatus();
            UpdateCommandStates();
        }
    }

    private void SetBusyUi(bool busy, string? status)
    {
        _progress.Indeterminate = busy;
        if (!busy) _progress.Value = 0;
        if (busy && !string.IsNullOrEmpty(status)) SetStatus(status);
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        UpdateCommandStates();
    }

    private async void RunUi(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (ArchiveException ex) { MsgBox.Error(this, ex.Message, "操作失败"); }
        catch (Exception ex) { ShowUnexpected(ex); }
    }

    private bool EnsureEngine()
    {
        _service ??= EngineBridge.Service;
        if (_service == null)
        {
            MsgBox.Error(this, EngineBridge.Error, "归档引擎未就绪");
            return false;
        }
        if (!_service.EngineAvailable)
        {
            MsgBox.Error(this, EngineBridge.Error, "归档引擎缺失");
            return false;
        }
        return true;
    }

    private string? ResolvePasswordForOperation()
    {
        if (_listResult is not { IsEncrypted: true }) return _archivePassword;
        if (!string.IsNullOrEmpty(_archivePassword)) return _archivePassword;
        var entered = PasswordDialog.Ask(this, _archivePath);
        if (entered == null) return null;
        _archivePassword = entered;
        return entered;
    }

    private string CreateTempRoot(string kind)
    {
        var root = Path.Combine(_settings.ResolvedTempDir(), kind, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        lock (_tempRoots) _tempRoots.Add(root);
        return root;
    }

    private void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                MsgBox.Warn(this, "文件夹不存在：" + path);
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MsgBox.Warn(this, "无法打开文件夹：" + ex.Message);
        }
    }

    private void ShowUnexpected(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            File.AppendAllText(Path.Combine(SettingsStore.Folder, "errors.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}\r\n\r\n");
        }
        catch { /* 日志失败不影响主流程 */ }

        MsgBox.Error(this,
            "操作没有完成，程序保持运行。\n\n" + ex.Message +
            "\n\n详细信息已记录到本地日志（不含密码）：\n" + Path.Combine(SettingsStore.Folder, "errors.log"),
            "出现错误");
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindowChrome(this);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try
        {
            if (_settings.CleanTempOnExit)
            {
                lock (_tempRoots)
                {
                    foreach (var root in _tempRoots)
                    {
                        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
                        catch { /* 文件可能仍被外部程序占用 */ }
                    }
                    _tempRoots.Clear();
                }
            }
        }
        catch { }
        _filterTimer.Dispose();
        _tooltip.Dispose();
        base.OnFormClosed(e);
    }

    // ————————————————————————————————————————————————
    // Win32：虚拟列表全选
    // ————————————————————————————————————————————————
    private const int LvmSetItemState = 0x102B;
    private const int LvifState = 0x0008;
    private const int LvisSelected = 0x0002;
    private const int LvisFocused = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct LvItemState
    {
        public int mask;
        public int iItem;
        public int iSubItem;
        public int state;
        public int stateMask;
        public IntPtr pszText;
        public int cchTextMax;
        public int iImage;
        public IntPtr lParam;
        public int iIndent;
        public int iGroupId;
        public int cColumns;
        public IntPtr puColumns;
        public IntPtr piColFmt;
        public int iGroup;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref LvItemState lParam);
}
