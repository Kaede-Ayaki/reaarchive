// InfoStrip.cs — 主窗口顶部的归档信息条：图标 + 归档名 + 格式/加密/只读徽章 + 右侧统计。
// 纯自绘（GDI+），无外部图片；不持有业务状态，只做展示。
// 所有像素尺寸都经 S() 按 DeviceDpi 缩放——144 DPI 下高度/边距跟着放大，否则文字会溢出。
using System.Drawing.Drawing2D;
using REAArchive.Archive;

namespace REAArchive.Ui;

/// <summary>归档身份与统计的横向信息条。没有归档时由主窗口把行高收到 0。</summary>
internal sealed class ArchiveInfoStrip : Panel
{
    private string _fileName = "";
    private string _fullPath = "";
    private string _format = "";
    private bool _encrypted;
    private bool _readOnly;
    private int _files;
    private int _dirs;
    private long _rawSize;
    private long _packedSize;

    // 96 DPI 下的基准值；实际绘制一律走 S()。
    private const int StripHeightBase = 56;
    private const int PadXBase = 14;

    private float DpiScale => DeviceDpi / 96f;
    private int S(int value) => (int)Math.Round(value * DpiScale);

    public ArchiveInfoStrip()
    {
        Name = "infoStrip";
        Dock = DockStyle.Fill;
        BackColor = Theme.Background;
        Margin = Padding.Empty;
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>当前 DPI 下这条信息条应有的高度。</summary>
    public int PreferredHeight => S(StripHeightBase);

    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    [System.ComponentModel.Browsable(false)]
    public bool HasArchive { get; private set; }

    public void SetArchive(string archivePath, ArchiveFormat format, bool encrypted, bool readOnly,
        int files, int dirs, long rawSize, long packedSize)
    {
        HasArchive = true;
        _fullPath = archivePath ?? "";
        _fileName = string.IsNullOrEmpty(_fullPath) ? "" : Path.GetFileName(_fullPath);
        _format = FormatUtil.FormatName(format);
        _encrypted = encrypted;
        _readOnly = readOnly;
        _files = files;
        _dirs = dirs;
        _rawSize = rawSize;
        _packedSize = packedSize;
        Invalidate();
    }

    public void Clear()
    {
        if (!HasArchive && _fileName.Length == 0) return;
        HasArchive = false;
        _fileName = _fullPath = _format = "";
        _encrypted = _readOnly = false;
        _files = _dirs = 0;
        _rawSize = _packedSize = 0;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var bounds = ClientRectangle;
        int padX = S(PadXBase);
        using (var back = new SolidBrush(Theme.Background)) g.FillRectangle(back, bounds);

        if (!HasArchive)
        {
            using var hintFont = Theme.Ui();
            TextRenderer.DrawText(g, "未打开归档", hintFont,
                new Rectangle(padX, 0, Math.Max(0, bounds.Width - padX * 2), bounds.Height),
                Theme.TextFaint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }

        // —— 左侧：图标方块 ——
        int iconBox = S(36);
        var iconRect = new RectangleF(padX, (bounds.Height - iconBox) / 2f, iconBox, iconBox);
        using (var boxPath = Theme.RoundedRect(iconRect, S(8)))
        using (var boxFill = new SolidBrush(Color.FromArgb(26, Theme.Accent)))
        using (var boxPen = new Pen(Theme.Border))
        {
            g.FillPath(boxFill, boxPath);
            g.DrawPath(boxPen, boxPath);
        }
        int glyphInset = S(8);
        DrawArchiveGlyph(g, RectangleF.Inflate(iconRect, -glyphInset, -glyphInset),
            Theme.Accent, 1.6f * DpiScale);

        // —— 右侧统计块（先算宽度，给中间让位）——
        using var statFont = Theme.Ui(-0.5f);
        using var statStrong = Theme.Ui(-0.5f, FontStyle.Bold);
        string statLine1 = $"{_files:N0} 个文件 · {_dirs:N0} 个文件夹";
        string statLine2 = _packedSize > 0
            ? $"{FormatUtil.Size(_rawSize)}  →  {FormatUtil.Size(_packedSize)}"
            : FormatUtil.Size(_rawSize);
        var statSize1 = TextRenderer.MeasureText(statLine1, statFont, Size.Empty, TextFormatFlags.NoPadding);
        var statSize2 = TextRenderer.MeasureText(statLine2, statStrong, Size.Empty, TextFormatFlags.NoPadding);
        int statWidth = Math.Max(statSize1.Width, statSize2.Width) + S(4);

        float textX = iconRect.Right + S(12);
        float rightLimit = bounds.Width - padX - statWidth - S(24);

        // —— 中间：归档名 + 徽章 + 路径 ——
        int nameY = S(11);
        int pathY = S(33);
        int badgeY = S(12);

        using var nameFont = Theme.Ui(1f, FontStyle.Bold);
        string name = Ellipsize(g, _fileName, nameFont, Math.Max(S(60), rightLimit - textX - S(190)));
        var nameSize = TextRenderer.MeasureText(name, nameFont, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, name, nameFont,
            new Point((int)textX, nameY), Theme.Text,
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        float badgeX = textX + nameSize.Width + S(10);
        badgeX = DrawBadge(g, badgeX, badgeY, _format, Theme.TextDim, Theme.Border);
        if (_encrypted) badgeX = DrawBadge(g, badgeX + S(6), badgeY, "已加密", Theme.Warn, Color.FromArgb(60, Theme.Warn));
        if (_readOnly) badgeX = DrawBadge(g, badgeX + S(6), badgeY, "只读", Theme.TextFaint, Theme.Border);

        using var pathFont = Theme.Ui(-1f);
        string path = Ellipsize(g, _fullPath, pathFont, Math.Max(S(80), rightLimit - textX));
        TextRenderer.DrawText(g, path, pathFont,
            new Point((int)textX, pathY), Theme.TextFaint,
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        // —— 右侧统计 ——
        int statX = bounds.Width - padX - statWidth;
        TextRenderer.DrawText(g, statLine1, statFont,
            new Point(statX, nameY), Theme.TextDim,
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, statLine2, statStrong,
            new Point(statX, pathY), Theme.Text,
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        // —— 底部 1px 分隔 ——
        using var sep = new Pen(Theme.Border);
        g.DrawLine(sep, 0, bounds.Height - 1, bounds.Width, bounds.Height - 1);
    }

    private static string Ellipsize(Graphics g, string text, Font font, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding).Width <= maxWidth)
            return text;
        const string dots = "…";
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            string candidate = text[..mid] + dots;
            int w = TextRenderer.MeasureText(candidate, font, Size.Empty, TextFormatFlags.NoPadding).Width;
            if (w <= maxWidth) lo = mid; else hi = mid - 1;
        }
        return lo <= 0 ? dots : text[..lo] + dots;
    }

    /// <summary>画一个"压缩盒"图标：圆角方块 + 中间横带 + 顶部小三角，避免依赖 Theme 新增 Glyph。</summary>
    private static void DrawArchiveGlyph(Graphics g, RectangleF r, Color color, float stroke)
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
        using var body = Theme.RoundedRect(new RectangleF(x, y + h * 0.28f, w, h * 0.72f), w * 0.14f);
        g.DrawPath(pen, body);
        g.DrawLine(pen, x, y + h * 0.52f, x + w, y + h * 0.52f);
        g.DrawLine(pen, x + w * 0.36f, y + h * 0.06f, x + w * 0.64f, y + h * 0.06f);
        g.FillRectangle(brush, x + w * 0.46f, y + h * 0.3f, w * 0.08f, h * 0.1f);
        g.SmoothingMode = saved;
    }

    /// <summary>画一枚小徽章，返回右边界 X。</summary>
    private float DrawBadge(Graphics g, float x, float y, string text, Color fg, Color border)
    {
        if (string.IsNullOrEmpty(text)) return x;
        using var font = Theme.Ui(-1.5f);
        var size = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding);
        int padH = S(6);
        float w = size.Width + padH * 2;
        float h = S(17);
        var rect = new RectangleF(x, y, w, h);
        using (var path = Theme.RoundedRect(rect, S(4)))
        using (var fill = new SolidBrush(Color.FromArgb(22, fg)))
        using (var pen = new Pen(Color.FromArgb(110, border)) { Width = 1f })
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, text, font,
            new Point((int)Math.Round(x + padH), (int)Math.Round(y + (h - size.Height) / 2f + 1f)),
            fg, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        return x + w;
    }
}
