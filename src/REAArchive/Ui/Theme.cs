// Theme.cs — 深色专业主题（#1e2228 系）、手绘图标与无边框绘制控件。
// 纯 WinForms + GDI+，不加载任何外部图片/网络资源。
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace REAArchive.Ui;

internal enum Glyph
{
    Open,
    ExtractTo,
    ExtractHere,
    Test,
    Create,
    Add,
    Delete,
    Info,
    Folder,
    File,
    Lock,
    Settings,
    Up,
    Search,
    About,
    Shell,
    Warning,
    Close,
}

internal static class Theme
{
    // —— 配色（单一深色系，避免刺眼）——
    public static readonly Color Background = Color.FromArgb(0x1e, 0x22, 0x28);
    public static readonly Color RowAlt = Color.FromArgb(0x22, 0x27, 0x30);
    public static readonly Color Surface = Color.FromArgb(0x25, 0x2a, 0x32);
    public static readonly Color SurfaceAlt = Color.FromArgb(0x2b, 0x31, 0x3a);
    public static readonly Color SurfaceHover = Color.FromArgb(0x34, 0x3b, 0x46);
    public static readonly Color Border = Color.FromArgb(0x3a, 0x41, 0x4c);
    public static readonly Color Text = Color.FromArgb(0xe6, 0xe8, 0xeb);
    public static readonly Color TextDim = Color.FromArgb(0x9a, 0xa4, 0xb2);
    public static readonly Color TextFaint = Color.FromArgb(0x6d, 0x77, 0x85);
    public static readonly Color Accent = Color.FromArgb(0x4a, 0x9e, 0xff);
    public static readonly Color AccentDark = Color.FromArgb(0x2b, 0x5f, 0xa8);
    public static readonly Color Selection = Color.FromArgb(0x1f, 0x4d, 0x80);
    public static readonly Color Danger = Color.FromArgb(0xe0, 0x6c, 0x75);
    public static readonly Color Ok = Color.FromArgb(0x6c, 0xc9, 0x8f);
    public static readonly Color Warn = Color.FromArgb(0xe0, 0xb3, 0x6c);

    public const string UiFontName = "Microsoft YaHei UI";
    public const string MonoFontName = "Consolas";

    /// <summary>全局 UI 字号（设置页可改，8~14）。</summary>
    public static float UiFontSize { get; set; } = 9f;

    public static Font Ui(float delta = 0f, FontStyle style = FontStyle.Regular)
        => new(UiFontName, Math.Clamp(UiFontSize + delta, 7f, 18f), style, GraphicsUnit.Point);

    public static Font Mono(float delta = 0f)
        => new(MonoFontName, Math.Clamp(UiFontSize + delta - 1f, 7f, 16f), FontStyle.Regular, GraphicsUnit.Point);

    private static Icon? _appIcon;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>让系统标题栏跟随深色主题（Win10 1903+ / Win11；失败则保持系统默认）。</summary>
    public static void ApplyDarkTitleBar(Form form)
    {
        if (!form.IsHandleCreated) return;
        try
        {
            int enabled = 1;
            if (DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref enabled, sizeof(int));
        }
        catch
        {
            // 旧系统或禁用 DWM：保持系统默认标题栏
        }
    }

    public static Icon AppIcon
    {
        get
        {
            if (_appIcon != null) return _appIcon;
            try
            {
                var ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) { _appIcon = ico; return _appIcon; }
            }
            catch { /* 用系统图标兜底 */ }
            _appIcon = SystemIcons.Application;
            return _appIcon;
        }
    }

    // —— 图标（GDI+ 手绘，无外部资源）——
    public static Bitmap GlyphImage(Glyph glyph, int size, Color color, float stroke = 1.7f)
    {
        var bmp = new Bitmap(Math.Max(8, size), Math.Max(8, size));
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        DrawGlyph(g, glyph, new RectangleF(2f, 2f, size - 4f, size - 4f), color, stroke);
        return bmp;
    }

    public static void DrawGlyph(Graphics g, Glyph glyph, RectangleF r, Color color, float stroke = 1.7f)
    {
        var saved = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        using var brush = new SolidBrush(color);
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;

        PointF Pt(float dx, float dy) => new(x + dx * w, y + dy * h);

        switch (glyph)
        {
            case Glyph.Open:
                g.DrawLines(pen, new[]
                {
                    Pt(0.06f, 0.32f), Pt(0.06f, 0.82f), Pt(0.94f, 0.82f),
                    Pt(0.94f, 0.40f), Pt(0.44f, 0.40f), Pt(0.38f, 0.28f),
                    Pt(0.06f, 0.28f), Pt(0.06f, 0.32f),
                });
                g.DrawLine(pen, Pt(0.14f, 0.56f), Pt(0.86f, 0.56f));
                break;

            case Glyph.ExtractTo:
                g.DrawLine(pen, Pt(0.08f, 0.76f), Pt(0.92f, 0.76f));
                g.DrawLine(pen, Pt(0.08f, 0.88f), Pt(0.32f, 0.88f));
                g.DrawLine(pen, Pt(0.68f, 0.88f), Pt(0.92f, 0.88f));
                g.DrawLine(pen, Pt(0.50f, 0.10f), Pt(0.50f, 0.58f));
                g.DrawLines(pen, new[] { Pt(0.30f, 0.40f), Pt(0.50f, 0.60f), Pt(0.70f, 0.40f) });
                break;

            case Glyph.ExtractHere:
                g.DrawLines(pen, new[]
                {
                    Pt(0.10f, 0.34f), Pt(0.10f, 0.88f), Pt(0.90f, 0.88f), Pt(0.90f, 0.34f),
                });
                g.DrawLine(pen, Pt(0.50f, 0.08f), Pt(0.50f, 0.62f));
                g.DrawLines(pen, new[] { Pt(0.30f, 0.44f), Pt(0.50f, 0.64f), Pt(0.70f, 0.44f) });
                break;

            case Glyph.Test:
                g.DrawEllipse(pen, x + 0.06f * w, y + 0.06f * h, 0.88f * w, 0.88f * h);
                g.DrawLines(pen, new[] { Pt(0.28f, 0.52f), Pt(0.44f, 0.68f), Pt(0.74f, 0.34f) });
                break;

            case Glyph.Create:
                g.DrawLines(pen, new[]
                {
                    Pt(0.16f, 0.06f), Pt(0.62f, 0.06f), Pt(0.84f, 0.28f),
                    Pt(0.84f, 0.94f), Pt(0.16f, 0.94f), Pt(0.16f, 0.06f),
                });
                g.DrawLine(pen, Pt(0.62f, 0.06f), Pt(0.62f, 0.28f));
                g.DrawLine(pen, Pt(0.62f, 0.28f), Pt(0.84f, 0.28f));
                g.DrawLine(pen, Pt(0.36f, 0.62f), Pt(0.70f, 0.62f));
                g.DrawLine(pen, Pt(0.53f, 0.45f), Pt(0.53f, 0.79f));
                break;

            case Glyph.Add:
                g.DrawLine(pen, Pt(0.50f, 0.14f), Pt(0.50f, 0.86f));
                g.DrawLine(pen, Pt(0.14f, 0.50f), Pt(0.86f, 0.50f));
                break;

            case Glyph.Delete:
                g.DrawLine(pen, Pt(0.12f, 0.26f), Pt(0.88f, 0.26f));
                g.DrawLine(pen, Pt(0.38f, 0.26f), Pt(0.38f, 0.12f));
                g.DrawLine(pen, Pt(0.38f, 0.12f), Pt(0.62f, 0.12f));
                g.DrawLine(pen, Pt(0.62f, 0.12f), Pt(0.62f, 0.26f));
                g.DrawLines(pen, new[]
                {
                    Pt(0.22f, 0.26f), Pt(0.28f, 0.92f), Pt(0.72f, 0.92f), Pt(0.78f, 0.26f),
                });
                g.DrawLine(pen, Pt(0.42f, 0.40f), Pt(0.44f, 0.80f));
                g.DrawLine(pen, Pt(0.58f, 0.40f), Pt(0.56f, 0.80f));
                break;

            case Glyph.Info:
                g.DrawEllipse(pen, x + 0.08f * w, y + 0.08f * h, 0.84f * w, 0.84f * h);
                g.DrawLine(pen, Pt(0.50f, 0.44f), Pt(0.50f, 0.72f));
                g.FillEllipse(brush, x + 0.46f * w, y + 0.26f * h, 0.08f * w, 0.08f * h);
                break;

            case Glyph.Folder:
                g.DrawLines(pen, new[]
                {
                    Pt(0.06f, 0.24f), Pt(0.42f, 0.24f), Pt(0.50f, 0.36f), Pt(0.94f, 0.36f),
                    Pt(0.94f, 0.86f), Pt(0.06f, 0.86f), Pt(0.06f, 0.24f),
                });
                break;

            case Glyph.File:
                g.DrawLines(pen, new[]
                {
                    Pt(0.20f, 0.06f), Pt(0.64f, 0.06f), Pt(0.82f, 0.26f),
                    Pt(0.82f, 0.94f), Pt(0.20f, 0.94f), Pt(0.20f, 0.06f),
                });
                g.DrawLine(pen, Pt(0.64f, 0.06f), Pt(0.64f, 0.26f));
                g.DrawLine(pen, Pt(0.64f, 0.26f), Pt(0.82f, 0.26f));
                break;

            case Glyph.Lock:
                g.DrawRectangle(pen, x + 0.20f * w, y + 0.44f * h, 0.60f * w, 0.48f * h);
                g.DrawArc(pen, x + 0.30f * w, y + 0.10f * h, 0.40f * w, 0.52f * h, 180f, 180f);
                g.DrawLine(pen, Pt(0.50f, 0.60f), Pt(0.50f, 0.74f));
                break;

            case Glyph.Settings:
                g.DrawEllipse(pen, x + 0.30f * w, y + 0.30f * h, 0.40f * w, 0.40f * h);
                for (int i = 0; i < 8; i++)
                {
                    double a = Math.PI * i / 4.0;
                    float cx = x + w / 2f, cy = y + h / 2f;
                    float r1 = 0.26f * w, r2 = 0.42f * w;
                    g.DrawLine(pen,
                        cx + (float)(Math.Cos(a) * r1), cy + (float)(Math.Sin(a) * r1),
                        cx + (float)(Math.Cos(a) * r2), cy + (float)(Math.Sin(a) * r2));
                }
                break;

            case Glyph.Up:
                g.DrawLine(pen, Pt(0.50f, 0.86f), Pt(0.50f, 0.14f));
                g.DrawLines(pen, new[] { Pt(0.22f, 0.42f), Pt(0.50f, 0.14f), Pt(0.78f, 0.42f) });
                break;

            case Glyph.Search:
                g.DrawEllipse(pen, x + 0.10f * w, y + 0.10f * h, 0.56f * w, 0.56f * h);
                g.DrawLine(pen, Pt(0.62f, 0.62f), Pt(0.90f, 0.90f));
                break;

            case Glyph.About:
                g.DrawEllipse(pen, x + 0.06f * w, y + 0.06f * h, 0.88f * w, 0.88f * h);
                g.DrawLine(pen, Pt(0.50f, 0.44f), Pt(0.50f, 0.72f));
                g.FillEllipse(brush, x + 0.46f * w, y + 0.26f * h, 0.08f * w, 0.08f * h);
                break;

            case Glyph.Shell:
                g.DrawRectangle(pen, x + 0.08f * w, y + 0.16f * h, 0.84f * w, 0.62f * h);
                g.DrawLine(pen, Pt(0.36f, 0.90f), Pt(0.64f, 0.90f));
                g.DrawLine(pen, Pt(0.50f, 0.78f), Pt(0.50f, 0.90f));
                break;

            case Glyph.Warning:
                g.DrawLines(pen, new[]
                {
                    Pt(0.50f, 0.08f), Pt(0.94f, 0.88f), Pt(0.06f, 0.88f), Pt(0.50f, 0.08f),
                });
                g.DrawLine(pen, Pt(0.50f, 0.36f), Pt(0.50f, 0.62f));
                g.FillEllipse(brush, x + 0.46f * w, y + 0.70f * h, 0.08f * w, 0.08f * h);
                break;

            case Glyph.Close:
                g.DrawLine(pen, Pt(0.18f, 0.18f), Pt(0.82f, 0.82f));
                g.DrawLine(pen, Pt(0.82f, 0.18f), Pt(0.18f, 0.82f));
                break;
        }

        g.SmoothingMode = saved;
    }

    // —— 控件工厂 ——
    public static Button ToolButton(string name, string text, Glyph glyph, EventHandler? onClick = null)
    {
        var button = new Button
        {
            Name = name,
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 38),
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = Text,
            Font = Ui(),
            Image = GlyphImage(glyph, 19, Accent),
            ImageAlign = ContentAlignment.MiddleLeft,
            TextAlign = ContentAlignment.MiddleCenter,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            Padding = new Padding(9, 0, 11, 0),
            Margin = new Padding(2, 5, 2, 5),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            TabStop = true,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = SurfaceHover;
        button.FlatAppearance.MouseDownBackColor = Selection;
        if (onClick != null) button.Click += onClick;
        return button;
    }

    public static Button DialogButton(string name, string text, bool primary = false, int width = 96)
    {
        var button = new Button
        {
            Name = name,
            Text = text,
            Size = new Size(width, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? AccentDark : SurfaceAlt,
            ForeColor = primary ? Color.White : Text,
            Font = Ui(),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Accent : SurfaceHover;
        button.FlatAppearance.MouseDownBackColor = primary ? AccentDark : Selection;
        return button;
    }

    public static TextBox MakeTextBox(string name, int width = 200)
    {
        return new TextBox
        {
            Name = name,
            Width = width,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = SurfaceAlt,
            ForeColor = Text,
            Font = Ui(),
            PlaceholderText = "",
        };
    }

    public static ComboBox MakeCombo(string name, IEnumerable<string> items, int width = 180)
    {
        var combo = new ThemeComboBox
        {
            Name = name,
            Width = width,
            Font = Ui(),
        };
        foreach (var item in items) combo.Items.Add(item);
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        combo.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var selected = (e.State & DrawItemState.Selected) != 0;
            using var back = new SolidBrush(selected ? Selection : SurfaceAlt);
            e.Graphics.FillRectangle(back, e.Bounds);
            var text = combo.Items[e.Index]?.ToString() ?? "";
            TextRenderer.DrawText(e.Graphics, text, combo.Font, e.Bounds,
                selected ? Color.White : Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        return combo;
    }

    public static CheckBox MakeCheckBox(string name, string text, int width = 220)
    {
        return new ThemeCheckBox
        {
            Name = name,
            Text = text,
            Width = width,
            Height = 24,
            Font = Ui(),
        };
    }

    public static Label MakeLabel(string text, bool dim = false, bool bold = false, float delta = 0f)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = dim ? TextDim : Text,
            Font = Ui(delta, bold ? FontStyle.Bold : FontStyle.Regular),
        };
    }

    public static Panel MakeSeparator(bool horizontal)
    {
        return new Panel
        {
            BackColor = Border,
            Size = horizontal ? new Size(10, 1) : new Size(1, 22),
            Margin = horizontal ? new Padding(0, 4, 0, 4) : new Padding(7, 7, 7, 7),
        };
    }

    public static ContextMenuStrip MakeContextMenu()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = SurfaceAlt,
            ForeColor = Text,
            Font = Ui(),
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            Padding = new Padding(2),
        };
        menu.RenderMode = ToolStripRenderMode.Professional;
        return menu;
    }

    public static MenuStrip MakeMenuStrip()
    {
        var menu = new MenuStrip
        {
            BackColor = Surface,
            ForeColor = Text,
            Font = Ui(),
            Renderer = new DarkMenuRenderer(),
            RenderMode = ToolStripRenderMode.Professional,
            Padding = new Padding(6, 2, 0, 2),
            Dock = DockStyle.Fill,
        };
        return menu;
    }

    public static void StyleMenuStrip(MenuStrip menu)
    {
        menu.BackColor = Surface;
        menu.ForeColor = Text;
        menu.Font = Ui();
        menu.Renderer = new DarkMenuRenderer();
        foreach (ToolStripItem item in menu.Items)
        {
            item.ForeColor = Text;
            if (item is ToolStripMenuItem mi) StyleMenuItem(mi);
        }
    }

    private static void StyleMenuItem(ToolStripMenuItem item)
    {
        item.ForeColor = Text;
        item.BackColor = SurfaceAlt;
        item.DropDown.BackColor = SurfaceAlt;
        item.DropDown.ForeColor = Text;
        item.DropDown.Font = Ui();
        item.DropDown.Renderer = new DarkMenuRenderer();
        foreach (ToolStripItem child in item.DropDownItems)
        {
            child.ForeColor = Text;
            if (child is ToolStripMenuItem sub) StyleMenuItem(sub);
        }
    }

    // —— 菜单渲染 ——
    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuStripGradientBegin => Surface;
        public override Color MenuStripGradientEnd => Surface;
        public override Color MenuItemSelected => SurfaceHover;
        public override Color MenuItemSelectedGradientBegin => SurfaceHover;
        public override Color MenuItemSelectedGradientEnd => SurfaceHover;
        public override Color MenuItemPressedGradientBegin => Selection;
        public override Color MenuItemPressedGradientEnd => Selection;
        public override Color MenuItemBorder => AccentDark;
        public override Color MenuBorder => Border;
        public override Color ToolStripDropDownBackground => SurfaceAlt;
        public override Color ImageMarginGradientBegin => SurfaceAlt;
        public override Color ImageMarginGradientMiddle => SurfaceAlt;
        public override Color ImageMarginGradientEnd => SurfaceAlt;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color CheckBackground => SurfaceHover;
        public override Color CheckSelectedBackground => Selection;
        public override Color ButtonSelectedHighlight => SurfaceHover;
        public override Color ButtonSelectedHighlightBorder => AccentDark;
        public override Color ButtonPressedHighlight => Selection;
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is MenuStrip) return;
            using var pen = new Pen(Border);
            var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            e.Graphics.DrawRectangle(pen, rect);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : TextFaint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = TextDim;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected && !e.Item.Pressed) return;
            using var brush = new SolidBrush(e.Item.Pressed ? Selection : SurfaceHover);
            e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
        }
    }

    // —— 圆角工具 ——
    public static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void DrawRoundRect(Graphics g, RectangleF bounds, float radius, Color fill, Color border)
    {
        using var path = RoundedRect(bounds, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
        using var pen = new Pen(border);
        g.DrawPath(pen, path);
    }
}

/// <summary>列表行数据源（虚拟模式只取可见行，10 万条目也不卡）。</summary>
internal interface IListViewRowSource
{
    int RowCount { get; }
    string CellText(int row, int column);
    bool IsDirectory(int row);
    bool IsEncrypted(int row);
}

/// <summary>深色、虚拟模式、自绘表头/行的 ListView（Details）。</summary>
internal sealed class ThemedListView : ListView
{
    private readonly Color _headerBack = Theme.SurfaceAlt;
    private int _retrieveCount;

    public int RetrieveCount => _retrieveCount;

    // VirtualMode 下访问 SelectedIndices/SelectedItems 会遍历全部条目（10 万条要 2-3 秒），
    // 这里直接用 LVM_GETSELECTEDCOUNT（O(1)）替代。
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public int SelectedCount => IsHandleCreated
        ? SendMessage(Handle, 0x1032 /* LVM_GETSELECTEDCOUNT */, IntPtr.Zero, IntPtr.Zero).ToInt32()
        : 0;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IListViewRowSource? RowSource { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SortColumnIndex { get; set; } = -1;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool SortAscending { get; set; } = true;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EmptyText { get; set; } = "没有打开的归档";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EmptySubText { get; set; } = "把归档拖到这里，或按 Ctrl+O 打开";

    public ThemedListView()
    {
        Name = "lvEntries";
        View = View.Details;
        FullRowSelect = true;
        MultiSelect = true;
        HideSelection = false;
        OwnerDraw = true;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Ui();
        HeaderStyle = ColumnHeaderStyle.Clickable;
        ShowItemToolTips = false;
        VirtualMode = true;
        AllowColumnReorder = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    /// <summary>必须由 OwnerDraw + VirtualMode 手动同步大小。</summary>
    public void RefreshRows()
    {
        var count = RowSource?.RowCount ?? 0;
        if (VirtualListSize != count)
        {
            VirtualListSize = 0;
            VirtualListSize = count;
        }
        Invalidate();
    }

    protected override void OnRetrieveVirtualItem(RetrieveVirtualItemEventArgs e)
    {
        _retrieveCount++;
        var source = RowSource;
        var item = new ListViewItem(new string[Columns.Count]);
        if (source != null && e.ItemIndex >= 0 && e.ItemIndex < source.RowCount)
        {
            for (int i = 0; i < Columns.Count && i < item.SubItems.Count; i++)
            {
                item.SubItems[i].Text = source.CellText(e.ItemIndex, i);
            }
            if (source.IsDirectory(e.ItemIndex)) item.ImageIndex = -1;
        }
        e.Item = item;
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        using var back = new SolidBrush(_headerBack);
        e.Graphics.FillRectangle(back, e.Bounds);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top + 5, e.Bounds.Right - 1, e.Bounds.Bottom - 6);

        var text = e.Header?.Text ?? "";
        if (e.ColumnIndex == SortColumnIndex && SortColumnIndex >= 0)
            text += SortAscending ? "  ▲" : "  ▼";
        var rect = e.Bounds;
        rect.Inflate(-9, -2);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                    (IsRightAligned(e.ColumnIndex) ? TextFormatFlags.Right : TextFormatFlags.Left);
        TextRenderer.DrawText(e.Graphics, text, Font, rect, Theme.TextDim, flags);
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        var selected = e.Item.Selected;
        var backColor = selected
            ? Theme.Selection
            : (e.ItemIndex % 2 == 0 ? Theme.Background : Theme.RowAlt);
        using var brush = new SolidBrush(backColor);
        e.Graphics.FillRectangle(brush, e.Bounds);
        if (e.Item.Focused && !selected)
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, e.Bounds.Left, e.Bounds.Top, e.Bounds.Width - 1, e.Bounds.Height - 1);
        }
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        var source = RowSource;
        if (source == null || e.ItemIndex < 0 || e.ItemIndex >= source.RowCount) return;
        var selected = e.Item?.Selected == true;
        var rect = e.Bounds;
        rect.Inflate(-8, 0);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                    (IsRightAligned(e.ColumnIndex) ? TextFormatFlags.Right : TextFormatFlags.Left);

        if (e.ColumnIndex == 0)
        {
            var glyph = source.IsDirectory(e.ItemIndex) ? Glyph.Folder : Glyph.File;
            var iconSize = Math.Max(14, DeviceDpi * 15 / 96);
            var iconRect = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top + (e.Bounds.Height - iconSize) / 2, iconSize, iconSize);
            Theme.DrawGlyph(e.Graphics, glyph, iconRect,
                selected ? Color.White : (source.IsDirectory(e.ItemIndex) ? Theme.Accent : Theme.TextDim), 1.5f);
            rect = new Rectangle(iconRect.Right + 8, e.Bounds.Top, Math.Max(0, e.Bounds.Right - iconRect.Right - 14), e.Bounds.Height);
            if (source.IsEncrypted(e.ItemIndex))
            {
                var lockSize = Math.Max(11, iconSize - 3);
                var lockRect = new Rectangle(rect.Right - lockSize, e.Bounds.Top + (e.Bounds.Height - lockSize) / 2, lockSize, lockSize);
                Theme.DrawGlyph(e.Graphics, Glyph.Lock, lockRect, selected ? Color.White : Theme.Warn, 1.3f);
                rect.Width = Math.Max(0, rect.Width - lockSize - 4);
            }
            flags &= ~TextFormatFlags.Right;
            flags |= TextFormatFlags.Left;
        }

        var text = source.CellText(e.ItemIndex, e.ColumnIndex);
        if (string.IsNullOrEmpty(text)) return;
        var color = selected ? Color.White
            : e.ColumnIndex == 3 || e.ColumnIndex == 4 || e.ColumnIndex == 5 ? Theme.TextDim : Theme.Text;
        TextRenderer.DrawText(e.Graphics, text, Font, rect, color, flags);
    }

    private static bool IsRightAligned(int column) => column is 1 or 2;
}

/// <summary>自绘进度条（深色轨道 + 蓝色填充，支持不定进度）。</summary>
internal sealed class FlatProgressBar : Control
{
    private double _value;
    private bool _indeterminate;
    private readonly System.Windows.Forms.Timer _timer;
    private int _offset;
    private int _animatedValue;

    public FlatProgressBar()
    {
        Name = "progressBar";
        Height = 12;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.SurfaceAlt;
        _timer = new System.Windows.Forms.Timer { Interval = 33 };
        _timer.Tick += (_, _) =>
        {
            _offset = (_offset + 6) % 120;
            _animatedValue = (_animatedValue + 2) % 110;
            Invalidate();
        };
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (Math.Abs(clamped - _value) < 0.01) return;
            _value = clamped;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Indeterminate
    {
        get => _indeterminate;
        set
        {
            if (_indeterminate == value) return;
            _indeterminate = value;
            if (value) { _offset = 0; _timer.Start(); }
            else _timer.Stop();
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(0, Math.Max(0, (Height - 8) / 2f), Width, 8);
        using (var path = Theme.RoundedRect(track, 4))
        using (var back = new SolidBrush(Theme.SurfaceAlt))
        {
            g.FillPath(back, path);
        }

        if (_indeterminate)
        {
            float chunk = Math.Max(30f, Width * 0.28f);
            float x = (Width + chunk) * (_offset / 120f) - chunk;
            var rect = RectangleF.Intersect(track, new RectangleF(x, track.Y, chunk, track.Height));
            if (rect.Width > 1)
            {
                using var path = Theme.RoundedRect(rect, 4);
                using var brush = new SolidBrush(Theme.Accent);
                g.FillPath(brush, path);
            }
            return;
        }

        var percent = (float)Math.Clamp(_value / 100.0, 0.0, 1.0);
        var fillWidth = track.Width * percent;
        if (fillWidth < 0.5f) return;
        var fill = new RectangleF(track.X, track.Y, fillWidth, track.Height);
        using (var path = Theme.RoundedRect(fill, 4))
        using (var brush = new LinearGradientBrush(
            new RectangleF(track.X, track.Y, Math.Max(1f, fillWidth), track.Height),
            Theme.AccentDark, Theme.Accent, LinearGradientMode.Horizontal))
        {
            g.FillPath(brush, path);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>深色复选框：自绘方框/勾，避免系统浅色控件破坏主题。</summary>
internal sealed class ThemeCheckBox : CheckBox
{
    public ThemeCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false;
        Height = 24;
        ForeColor = Theme.Text;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var background = Parent?.BackColor ?? Theme.Background;
        e.Graphics.Clear(background);

        const int boxSize = 14;
        var box = new Rectangle(1, (Height - boxSize) / 2, boxSize, boxSize);
        using (var fill = new SolidBrush(Theme.SurfaceAlt))
            e.Graphics.FillRectangle(fill, box);
        using (var border = new Pen(Enabled ? Theme.Border : Theme.TextFaint))
            e.Graphics.DrawRectangle(border, box);

        if (Checked)
        {
            using (var accent = new SolidBrush(Enabled ? Theme.AccentDark : Theme.SurfaceHover))
                e.Graphics.FillRectangle(accent, box.X + 1, box.Y + 1, box.Width - 1, box.Height - 1);
            using var check = new Pen(Enabled ? Color.White : Theme.TextDim, 1.8f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(check, new[]
            {
                new PointF(box.X + 3.5f, box.Y + 7f),
                new PointF(box.X + 6f, box.Y + 10f),
                new PointF(box.X + 11f, box.Y + 4f),
            });
        }

        var textRect = new Rectangle(box.Right + 6, 0, Math.Max(0, Width - box.Right - 6), Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, textRect,
            Enabled ? ForeColor : Theme.TextFaint,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if (Focused && ShowFocusCues)
        {
            var focusRect = new Rectangle(box.Right + 5, 2, Math.Max(0, Width - box.Right - 7), Height - 5);
            ControlPaint.DrawFocusRectangle(e.Graphics, focusRect, ForeColor, background);
        }
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }
}

/// <summary>深色只读下拉框：闭合并自绘文字与箭头，展开项由 DrawItem 深色绘制。</summary>
internal sealed class ThemeComboBox : ComboBox
{
    private const int WmPaint = 0x000F;
    private const int WmEraseBkgnd = 0x0014;

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr Hdc;
        public int Erase;
        public PaintRect Paint;
        public int Restore;
        public int IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Reserved;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct paintStruct);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct paintStruct);

    public ThemeComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        DrawMode = DrawMode.OwnerDrawFixed;
        BackColor = Theme.SurfaceAlt;
        ForeColor = Theme.Text;
        ItemHeight = 22;
        IntegralHeight = false;
        MaxDropDownItems = 12;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmEraseBkgnd)
        {
            m.Result = new IntPtr(1);
            return;
        }
        if (m.Msg == WmPaint)
        {
            var ps = new PaintStruct();
            IntPtr hdc = BeginPaint(Handle, out ps);
            try
            {
                using var g = Graphics.FromHdc(hdc);
                PaintFace(g);
            }
            finally
            {
                EndPaint(Handle, ref ps);
            }
            return;
        }
        base.WndProc(ref m);
    }

    private void PaintFace(Graphics g)
    {
        var rect = ClientRectangle;
        using (var back = new SolidBrush(Theme.SurfaceAlt))
            g.FillRectangle(back, rect);
        using (var border = new Pen(Enabled ? Theme.Border : Theme.SurfaceHover))
            g.DrawRectangle(border, 0, 0, rect.Width - 1, rect.Height - 1);

        var text = SelectedIndex >= 0
            ? Items[SelectedIndex]?.ToString() ?? ""
            : Text;
        var textRect = new Rectangle(8, 0, Math.Max(0, rect.Width - 30), rect.Height);
        TextRenderer.DrawText(g, text, Font, textRect,
            Enabled ? Theme.Text : Theme.TextFaint,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        using (var chevron = new Pen(Enabled ? Theme.TextDim : Theme.TextFaint, 1.6f))
        {
            float cx = rect.Width - 15f;
            float cy = rect.Height / 2f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawLines(chevron, new[]
            {
                new PointF(cx - 4f, cy - 2f),
                new PointF(cx, cy + 2f),
                new PointF(cx + 4f, cy - 2f),
            });
        }

        if (Focused && Enabled)
        {
            using var focusPen = new Pen(Theme.AccentDark);
            g.DrawRectangle(focusPen, 1, 1, rect.Width - 3, rect.Height - 3);
        }
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }
}

/// <summary>所有对话框的深色基类。</summary>
internal class ThemedForm : Form
{
    public ThemedForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Ui();
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        Icon = Theme.AppIcon;
        DoubleBuffered = true;
        KeyPreview = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkTitleBar(this);
    }
}
