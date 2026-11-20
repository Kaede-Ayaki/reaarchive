// Theme.cs — 深色专业设计系统（近黑中性面 + 单一强调色）、24×24 手绘图标与全自绘控件。
// 纯 WinForms + GDI+，不加载任何外部图片/网络资源；所有几何按 24 单位设计网格绘制。
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
    // —— 6.9 设计系统新增 ——
    Key,
    Shield,
    Refresh,
    ChevronRight,
    Archive,
    Sparkle,
    Clock,
    Eject,
}

/// <summary>
/// 全局设计令牌与控件工厂。颜色/字体/圆角/描边全部集中在这里，
/// 调用方只拿控件，不自己写颜色字面量。
/// </summary>
internal static class Theme
{
    // ————————————————————————————————————————————————
    // 配色：近黑中性层次 + 单一强调色（#4d8df6）
    // ————————————————————————————————————————————————
    /// <summary>窗口底（最深）。</summary>
    public static readonly Color Background = Color.FromArgb(0x15, 0x17, 0x1b);
    /// <summary>列表/内容区凹陷面。</summary>
    public static readonly Color SurfaceSunken = Color.FromArgb(0x1a, 0x1d, 0x22);
    /// <summary>工具栏、面板。</summary>
    public static readonly Color Surface = Color.FromArgb(0x1f, 0x22, 0x27);
    /// <summary>输入框、卡片、菜单。</summary>
    public static readonly Color SurfaceAlt = Color.FromArgb(0x26, 0x2a, 0x30);
    /// <summary>悬停。</summary>
    public static readonly Color SurfaceHover = Color.FromArgb(0x2f, 0x34, 0x3b);
    /// <summary>按下。</summary>
    public static readonly Color SurfaceActive = Color.FromArgb(0x37, 0x3d, 0x45);
    /// <summary>常规描边。</summary>
    public static readonly Color Border = Color.FromArgb(0x2f, 0x34, 0x3b);
    /// <summary>强调描边/分隔。</summary>
    public static readonly Color BorderStrong = Color.FromArgb(0x3d, 0x44, 0x4e);
    /// <summary>主文字。</summary>
    public static readonly Color Text = Color.FromArgb(0xe9, 0xeb, 0xee);
    /// <summary>次要文字。</summary>
    public static readonly Color TextDim = Color.FromArgb(0x98, 0xa1, 0xac);
    /// <summary>禁用/提示。</summary>
    public static readonly Color TextFaint = Color.FromArgb(0x62, 0x6b, 0x77);
    /// <summary>强调（按钮/选中/图标高亮）。</summary>
    public static readonly Color Accent = Color.FromArgb(0x4d, 0x8d, 0xf6);
    /// <summary>强调悬停。</summary>
    public static readonly Color AccentHover = Color.FromArgb(0x6b, 0xa1, 0xff);
    /// <summary>强调色 12% 底（选中行、徽章底）。</summary>
    public static readonly Color AccentSoft = Color.FromArgb(0x1e, 0x2c, 0x44);
    /// <summary>强调按下/深调。</summary>
    public static readonly Color AccentDark = Color.FromArgb(0x2b, 0x5c, 0xae);
    /// <summary>列表选中底。</summary>
    public static readonly Color Selection = Color.FromArgb(0x26, 0x37, 0x4d);
    public static readonly Color Danger = Color.FromArgb(0xef, 0x6b, 0x6b);
    public static readonly Color Ok = Color.FromArgb(0x4f, 0xc9, 0x8a);
    public static readonly Color Warn = Color.FromArgb(0xe5, 0xb5, 0x67);
    /// <summary>列表交替行。</summary>
    public static readonly Color RowAlt = Color.FromArgb(0x1c, 0x1f, 0x24);

    // ————————————————————————————————————————————————
    // 字体
    // ————————————————————————————————————————————————
    public const string UiFontName = "Microsoft YaHei UI";
    public const string MonoFontName = "Consolas";
    /// <summary>emoji 回退字体（Windows 10/11 自带；缺失时退回 Segoe UI）。</summary>
    public const string EmojiFontName = "Segoe UI Emoji";

    private static float _uiFontSize = 9f;

    /// <summary>全局 UI 字号（设置页可改，8~14）。</summary>
    public static float UiFontSize
    {
        get => _uiFontSize;
        set
        {
            if (Math.Abs(_uiFontSize - value) < 0.01f) return;
            _uiFontSize = value;
            InvalidateFontCache();
        }
    }

    public static Font Ui(float delta = 0f, FontStyle style = FontStyle.Regular)
        => new(UiFontName, Math.Clamp(UiFontSize + delta, 7f, 18f), style, GraphicsUnit.Point);

    public static Font Mono(float delta = 0f)
        => new(MonoFontName, Math.Clamp(UiFontSize + delta - 1f, 7f, 16f), FontStyle.Regular, GraphicsUnit.Point);

    // emoji 回退字体缓存（按字号）
    private static Font? _emojiFont;
    private static float _emojiFontSize = -1f;

    private static void InvalidateFontCache()
    {
        _emojiFont?.Dispose();
        _emojiFont = null;
        _emojiFontSize = -1f;
    }

    /// <summary>文本里是否有代理对（emoji/星平面字符）。</summary>
    public static bool HasSurrogatePair(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (int i = 0; i < text.Length - 1; i++)
        {
            if (char.IsSurrogatePair(text, i)) return true;
        }
        return false;
    }

    /// <summary>给含 emoji 的文本挑一个能把字画出来的字体（修 B06：方块豆腐）。</summary>
    public static Font EmojiCapableFont(Font baseFont)
    {
        float size = baseFont.SizeInPoints;
        if (_emojiFont != null && Math.Abs(_emojiFontSize - size) < 0.01f) return _emojiFont;

        InvalidateFontCache();
        foreach (var family in new[] { EmojiFontName, "Segoe UI" })
        {
            Font candidate;
            try { candidate = new Font(family, size, baseFont.Style, GraphicsUnit.Point); }
            catch (ArgumentException) { continue; }
            if (!string.Equals(candidate.FontFamily.Name, family, StringComparison.OrdinalIgnoreCase))
            {
                candidate.Dispose();
                continue;
            }
            _emojiFont = candidate;
            _emojiFontSize = size;
            return candidate;
        }
        return baseFont;
    }

    // ————————————————————————————————————————————————
    // 窗口外观（DWM）
    // ————————————————————————————————————————————————
    private static Icon? _appIcon;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    /// <summary>让系统标题栏跟随深色主题（Win10 1903+ / Win11；失败则保持系统默认）。</summary>
    public static void ApplyDarkTitleBar(Form form)
    {
        if (!form.IsHandleCreated) return;
        try
        {
            int enabled = 1;
            if (DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }
        catch
        {
            // 旧系统或禁用 DWM：保持系统默认标题栏
        }
    }

    /// <summary>Win11 圆角窗口（DWMWA_WINDOW_CORNER_PREFERENCE=33, round=2）；失败静默忽略。</summary>
    public static void ApplyRoundedCorners(Form form)
    {
        if (!form.IsHandleCreated) return;
        try
        {
            int preference = DwmwcpRound;
            DwmSetWindowAttribute(form.Handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // Win10 及更早：没有该属性，忽略
        }
    }

    /// <summary>一次把深色标题栏 + 圆角都应用到窗口上。</summary>
    public static void ApplyWindowChrome(Form form)
    {
        ApplyDarkTitleBar(form);
        ApplyRoundedCorners(form);
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

    // ————————————————————————————————————————————————
    // 颜色工具
    // ————————————————————————————————————————————————
    /// <summary>线性混合两个颜色（amount=0 取 from，1 取 to）。</summary>
    public static Color Blend(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(from.A + (to.A - from.A) * amount),
            (int)Math.Round(from.R + (to.R - from.R) * amount),
            (int)Math.Round(from.G + (to.G - from.G) * amount),
            (int)Math.Round(from.B + (to.B - from.B) * amount));
    }

    /// <summary>替换颜色的 alpha 通道。</summary>
    public static Color WithAlpha(Color color, int alpha)
        => Color.FromArgb(Math.Clamp(alpha, 0, 255), color.R, color.G, color.B);

    // ————————————————————————————————————————————————
    // 图标（GDI+ 手绘，24×24 设计网格，圆头圆角描边）
    // ————————————————————————————————————————————————
    public static Bitmap GlyphImage(Glyph glyph, int size, Color color, float stroke = 1.6f)
    {
        int px = Math.Max(8, size);
        var bmp = new Bitmap(px, px);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        DrawGlyph(g, glyph, new RectangleF(2f, 2f, px - 4f, px - 4f), color, stroke);
        return bmp;
    }

    /// <summary>在给定矩形里画一个图标；几何按 24×24 网格定义，自动等比缩放并居中。</summary>
    public static void DrawGlyph(Graphics g, Glyph glyph, RectangleF r, Color color, float stroke = 1.6f)
    {
        if (r.Width < 2f || r.Height < 2f) return;

        var savedMode = g.SmoothingMode;
        var savedOffset = g.PixelOffsetMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float side = Math.Min(r.Width, r.Height);
        float u = side / 24f;
        float ox = r.X + (r.Width - side) / 2f;
        float oy = r.Y + (r.Height - side) / 2f;

        PointF P(float x, float y) => new(ox + x * u, oy + y * u);
        RectangleF B(float x, float y, float w, float h) => new(ox + x * u, oy + y * u, w * u, h * u);

        // 小尺寸收细、大尺寸略加粗：同一行图标的光学重量保持一致
        float penWidth = Math.Max(1.05f, stroke * Math.Clamp(u, 0.8f, 1.4f));

        using var pen = new Pen(color, penWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
            MiterLimit = 2f,
        };
        using var brush = new SolidBrush(color);

        switch (glyph)
        {
            case Glyph.Open:
                g.DrawLines(pen, new[]
                {
                    P(2.6f, 20f), P(2.6f, 6.4f), P(9.2f, 6.4f), P(11.4f, 8.9f),
                    P(21.4f, 8.9f), P(21.4f, 20f), P(2.6f, 20f),
                });
                g.DrawLine(pen, P(2.6f, 13.4f), P(21.4f, 13.4f));
                break;

            case Glyph.ExtractTo:
                g.DrawLine(pen, P(12f, 3.2f), P(12f, 13.6f));
                g.DrawLines(pen, new[] { P(7.6f, 9.2f), P(12f, 13.6f), P(16.4f, 9.2f) });
                g.DrawLine(pen, P(3.6f, 17.6f), P(9.2f, 17.6f));
                g.DrawLine(pen, P(14.8f, 17.6f), P(20.4f, 17.6f));
                break;

            case Glyph.ExtractHere:
                g.DrawLines(pen, new[] { P(3.2f, 13.2f), P(3.2f, 20.6f), P(20.8f, 20.6f), P(20.8f, 13.2f) });
                g.DrawLines(pen, new[]
                {
                    P(3.2f, 13.2f), P(7.6f, 13.2f), P(9.4f, 16.2f),
                    P(14.6f, 16.2f), P(16.4f, 13.2f), P(20.8f, 13.2f),
                });
                g.DrawLine(pen, P(12f, 3f), P(12f, 10.4f));
                g.DrawLines(pen, new[] { P(8.6f, 7f), P(12f, 10.4f), P(15.4f, 7f) });
                break;

            case Glyph.Test:
                g.DrawEllipse(pen, B(2.6f, 2.6f, 18.8f, 18.8f));
                g.DrawLines(pen, new[] { P(7.6f, 12.2f), P(10.8f, 15.4f), P(16.6f, 8.8f) });
                break;

            case Glyph.Create:
                g.DrawLines(pen, new[]
                {
                    P(5.6f, 2.6f), P(14.4f, 2.6f), P(18.8f, 7f),
                    P(18.8f, 21.4f), P(5.6f, 21.4f), P(5.6f, 2.6f),
                });
                g.DrawLines(pen, new[] { P(14.4f, 2.6f), P(14.4f, 7f), P(18.8f, 7f) });
                g.DrawLine(pen, P(12.2f, 11.6f), P(12.2f, 17.6f));
                g.DrawLine(pen, P(9.2f, 14.6f), P(15.2f, 14.6f));
                break;

            case Glyph.Add:
                g.DrawLine(pen, P(12f, 4.6f), P(12f, 19.4f));
                g.DrawLine(pen, P(4.6f, 12f), P(19.4f, 12f));
                break;

            case Glyph.Delete:
                g.DrawLine(pen, P(3.6f, 6.4f), P(20.4f, 6.4f));
                g.DrawLines(pen, new[] { P(9.2f, 6.4f), P(9.2f, 3.2f), P(14.8f, 3.2f), P(14.8f, 6.4f) });
                g.DrawLines(pen, new[] { P(5.6f, 6.4f), P(6.8f, 20.8f), P(17.2f, 20.8f), P(18.4f, 6.4f) });
                g.DrawLine(pen, P(10.2f, 10.2f), P(10.6f, 17.4f));
                g.DrawLine(pen, P(13.8f, 10.2f), P(13.4f, 17.4f));
                break;

            case Glyph.Info:
                g.DrawEllipse(pen, B(2.6f, 2.6f, 18.8f, 18.8f));
                g.DrawLine(pen, P(12f, 11.2f), P(12f, 17.2f));
                g.FillEllipse(brush, B(11.1f, 6.6f, 1.8f, 1.8f));
                break;

            case Glyph.Folder:
                using (var folder = new GraphicsPath())
                {
                    folder.AddPolygon(new[]
                    {
                        P(2.6f, 20.2f), P(2.6f, 6.2f), P(9.2f, 6.2f),
                        P(11.4f, 8.8f), P(21.4f, 8.8f), P(21.4f, 20.2f),
                    });
                    g.FillPath(brush, folder);
                    g.DrawPath(pen, folder);
                }
                break;

            case Glyph.File:
                g.DrawLines(pen, new[]
                {
                    P(6f, 2.6f), P(13.8f, 2.6f), P(18.4f, 7.2f),
                    P(18.4f, 21.4f), P(6f, 21.4f), P(6f, 2.6f),
                });
                g.DrawLines(pen, new[] { P(13.8f, 2.6f), P(13.8f, 7.2f), P(18.4f, 7.2f) });
                break;

            case Glyph.Lock:
                using (var body = RoundedRect(B(4.8f, 10.4f, 14.4f, 10.6f), 2.4f * u))
                    g.DrawPath(pen, body);
                g.DrawArc(pen, B(7.4f, 3.6f, 9.2f, 10.4f), 180f, 180f);
                g.DrawLine(pen, P(7.4f, 8.8f), P(7.4f, 10.4f));
                g.DrawLine(pen, P(16.6f, 8.8f), P(16.6f, 10.4f));
                g.FillEllipse(brush, B(11f, 13.2f, 2f, 2f));
                g.DrawLine(pen, P(12f, 15f), P(12f, 17.6f));
                break;

            case Glyph.Settings:
                // 三条滑杆 + 空心旋钮（比齿轮在小尺寸下更清晰）
                g.DrawLine(pen, P(3.6f, 6.6f), P(6.4f, 6.6f));
                g.DrawLine(pen, P(11.6f, 6.6f), P(20.4f, 6.6f));
                g.DrawEllipse(pen, B(6.4f, 4.6f, 5.2f, 4f));

                g.DrawLine(pen, P(3.6f, 12f), P(13.4f, 12f));
                g.DrawLine(pen, P(18.6f, 12f), P(20.4f, 12f));
                g.DrawEllipse(pen, B(13.4f, 10f, 5.2f, 4f));

                g.DrawLine(pen, P(3.6f, 17.4f), P(5.4f, 17.4f));
                g.DrawLine(pen, P(10.6f, 17.4f), P(20.4f, 17.4f));
                g.DrawEllipse(pen, B(5.4f, 15.4f, 5.2f, 4f));
                break;

            case Glyph.Up:
                g.DrawLine(pen, P(12f, 20.4f), P(12f, 4.4f));
                g.DrawLines(pen, new[] { P(5.8f, 10.6f), P(12f, 4.4f), P(18.2f, 10.6f) });
                break;

            case Glyph.Search:
                g.DrawEllipse(pen, B(3.8f, 3.8f, 13.2f, 13.2f));
                g.DrawLine(pen, P(15.4f, 15.4f), P(20.6f, 20.6f));
                break;

            case Glyph.About:
                g.DrawEllipse(pen, B(2.6f, 2.6f, 18.8f, 18.8f));
                g.DrawArc(pen, B(8.2f, 4.8f, 7.6f, 7.6f), 185f, 185f);
                using (var hook = new GraphicsPath())
                {
                    hook.AddBezier(P(15.6f, 9.3f), P(15.4f, 11.6f), P(12f, 10.9f), P(12f, 13.8f));
                    g.DrawPath(pen, hook);
                }
                g.FillEllipse(brush, B(11.1f, 16.8f, 1.8f, 1.8f));
                break;

            case Glyph.Shell:
                using (var terminal = RoundedRect(B(2.6f, 4f, 18.8f, 14.2f), 2.2f * u))
                    g.DrawPath(pen, terminal);
                g.DrawLines(pen, new[] { P(6.8f, 8.6f), P(10.2f, 11.8f), P(6.8f, 15f) });
                g.DrawLine(pen, P(12.2f, 15f), P(16.8f, 15f));
                break;

            case Glyph.Warning:
                using (var triangle = new GraphicsPath())
                {
                    triangle.AddPolygon(new[] { P(12f, 2.8f), P(21.4f, 20.6f), P(2.6f, 20.6f) });
                    g.DrawPath(pen, triangle);
                }
                g.DrawLine(pen, P(12f, 9.2f), P(12f, 14.8f));
                g.FillEllipse(brush, B(11.1f, 17f, 1.8f, 1.8f));
                break;

            case Glyph.Close:
                g.DrawLine(pen, P(6.6f, 6.6f), P(17.4f, 17.4f));
                g.DrawLine(pen, P(17.4f, 6.6f), P(6.6f, 17.4f));
                break;

            case Glyph.Key:
                g.DrawEllipse(pen, B(3.4f, 3.4f, 9f, 9f));
                g.DrawLine(pen, P(10.6f, 10.6f), P(20.4f, 20.4f));
                g.DrawLine(pen, P(16.2f, 16.2f), P(18.9f, 13.5f));
                g.DrawLine(pen, P(18.4f, 18.4f), P(20.9f, 15.9f));
                break;

            case Glyph.Shield:
                using (var shield = new GraphicsPath())
                {
                    shield.AddLine(P(12f, 2.8f), P(20.4f, 6.2f));
                    shield.AddLine(P(20.4f, 6.2f), P(20.4f, 11.6f));
                    shield.AddBezier(P(20.4f, 11.6f), P(20.4f, 16.6f), P(16f, 19.6f), P(12f, 21.4f));
                    shield.AddBezier(P(12f, 21.4f), P(8f, 19.6f), P(3.6f, 16.6f), P(3.6f, 11.6f));
                    shield.AddLine(P(3.6f, 11.6f), P(3.6f, 6.2f));
                    shield.CloseFigure();
                    g.DrawPath(pen, shield);
                }
                break;

            case Glyph.Refresh:
                {
                    g.DrawArc(pen, B(3.8f, 3.8f, 16.4f, 16.4f), 35f, 268f);
                    double endRad = 303.0 * Math.PI / 180.0;
                    var tip = new PointF(
                        (float)(12 + 8.2 * Math.Cos(endRad)),
                        (float)(12 + 8.2 * Math.Sin(endRad)));
                    var dir = new PointF((float)(-Math.Sin(endRad)), (float)Math.Cos(endRad));
                    var perp = new PointF(-dir.Y, dir.X);
                    var baseA = new PointF(tip.X - dir.X * 5.8f + perp.X * 2.9f, tip.Y - dir.Y * 5.8f + perp.Y * 2.9f);
                    var baseB = new PointF(tip.X - dir.X * 5.8f - perp.X * 2.9f, tip.Y - dir.Y * 5.8f - perp.Y * 2.9f);
                    g.FillPolygon(brush, new[] { P(tip.X, tip.Y), P(baseA.X, baseA.Y), P(baseB.X, baseB.Y) });
                    break;
                }

            case Glyph.ChevronRight:
                g.DrawLines(pen, new[] { P(9.4f, 5.4f), P(16f, 12f), P(9.4f, 18.6f) });
                break;

            case Glyph.Archive:
                using (var lid = RoundedRect(B(2.8f, 3.4f, 18.4f, 4.6f), 1.6f * u))
                    g.DrawPath(pen, lid);
                g.DrawLines(pen, new[] { P(4.6f, 8f), P(4.6f, 20.6f), P(19.4f, 20.6f), P(19.4f, 8f) });
                g.DrawLine(pen, P(9.6f, 12.6f), P(14.4f, 12.6f));
                break;

            case Glyph.Sparkle:
                using (var star = new GraphicsPath())
                {
                    star.AddBezier(P(12f, 2.6f), P(13.5f, 8.9f), P(15.1f, 10.5f), P(21.4f, 12f));
                    star.AddBezier(P(21.4f, 12f), P(15.1f, 13.5f), P(13.5f, 15.1f), P(12f, 21.4f));
                    star.AddBezier(P(12f, 21.4f), P(10.5f, 15.1f), P(8.9f, 13.5f), P(2.6f, 12f));
                    star.AddBezier(P(2.6f, 12f), P(8.9f, 10.5f), P(10.5f, 8.9f), P(12f, 2.6f));
                    star.CloseFigure();
                    g.DrawPath(pen, star);
                }
                break;

            case Glyph.Clock:
                g.DrawEllipse(pen, B(2.6f, 2.6f, 18.8f, 18.8f));
                g.DrawLine(pen, P(12f, 12f), P(12f, 6.6f));
                g.DrawLine(pen, P(12f, 12f), P(15.8f, 14.2f));
                break;

            case Glyph.Eject:
                using (var eject = new GraphicsPath())
                {
                    eject.AddPolygon(new[] { P(12f, 4.8f), P(18.8f, 13.4f), P(5.2f, 13.4f) });
                    g.DrawPath(pen, eject);
                }
                g.DrawLine(pen, P(5.2f, 17.6f), P(18.8f, 17.6f));
                break;
        }

        g.SmoothingMode = savedMode;
        g.PixelOffsetMode = savedOffset;
    }

    // ————————————————————————————————————————————————
    // 控件工厂
    // ————————————————————————————————————————————————
    private static readonly ToolTip SharedToolTip = new() { InitialDelay = 420, ReshowDelay = 180 };

    /// <summary>工具栏按钮：34px 高、6px 圆角、悬停/按下自绘，图标悬停变强调色。</summary>
    public static Button ToolButton(string name, string text, Glyph glyph, EventHandler? onClick = null)
    {
        var button = new ThemeToolButton
        {
            Name = name,
            Text = text,
            Glyph = glyph,
        };
        if (onClick != null) button.Click += onClick;
        return button;
    }

    /// <summary>对话框按钮：32px 高、6px 圆角；primary = 强调底 + 白字。</summary>
    public static Button DialogButton(string name, string text, bool primary = false, int width = 96)
        => new ThemeDialogButton(name, text, primary, width);

    /// <summary>34×34 方形图标按钮（路径栏「上一级」之类），自带 tooltip。</summary>
    public static Button IconButton(string name, Glyph glyph, string tooltip)
    {
        var button = new ThemeIconButton
        {
            Name = name,
            Glyph = glyph,
        };
        if (!string.IsNullOrEmpty(tooltip)) SharedToolTip.SetToolTip(button, tooltip);
        return button;
    }

    /// <summary>输入框：无系统边框，1px 圆角描边，聚焦时描边变强调色。</summary>
    public static TextBox MakeTextBox(string name, int width = 200)
    {
        return new ThemedTextBox
        {
            Name = name,
            Width = width,
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
            var rect = new Rectangle(e.Bounds.Left + 9, e.Bounds.Top,
                Math.Max(0, e.Bounds.Width - 14), e.Bounds.Height);
            var font = HasSurrogatePair(text) ? EmojiCapableFont(combo.Font) : combo.Font;
            TextRenderer.DrawText(e.Graphics, text, font, rect, selected ? Text : TextDim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
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
            ForeColor = dim ? TextDim : Text,
            Font = Ui(delta, bold ? FontStyle.Bold : FontStyle.Regular),
        };
    }

    /// <summary>小号分区标题（对话框里的「基本设置」等）。</summary>
    public static Label SectionHeader(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = TextDim,
            Font = Ui(-0.5f, FontStyle.Bold),
            Margin = new Padding(0, 6, 0, 5),
            UseMnemonic = false,
        };
    }

    /// <summary>卡片容器：圆角 SurfaceAlt 底 + 1px Border 描边，内部留 padding。</summary>
    public static Panel Card(int padding = 14) => new ThemeCard(padding);

    /// <summary>徽章/药丸：状态栏「7-Zip 25.01」「已加密」之类。</summary>
    public static Control Badge(string text, Color? accent = null) => new ThemeBadge(text, accent);

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
            Padding = new Padding(2, 4, 2, 4),
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
        item.DropDown.Padding = new Padding(2, 4, 2, 4);
        foreach (ToolStripItem child in item.DropDownItems)
        {
            child.ForeColor = Text;
            if (child is ToolStripMenuItem sub) StyleMenuItem(sub);
        }
    }

    // ————————————————————————————————————————————————
    // 菜单渲染
    // ————————————————————————————————————————————————
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
        public override Color MenuBorder => BorderStrong;
        public override Color ToolStripDropDownBackground => SurfaceAlt;
        public override Color ImageMarginGradientBegin => SurfaceAlt;
        public override Color ImageMarginGradientMiddle => SurfaceAlt;
        public override Color ImageMarginGradientEnd => SurfaceAlt;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => SurfaceAlt;
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
            var g = e.Graphics;
            using var pen = new Pen(BorderStrong);
            var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            g.DrawRectangle(pen, rect);
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
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(2f, 1f, Math.Max(1f, e.Item.Width - 4f), Math.Max(1f, e.Item.Height - 2f));
            using var path = RoundedRect(rect, 4f);
            using var brush = new SolidBrush(e.Item.Pressed ? Selection : SurfaceHover);
            g.FillPath(brush, path);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var bounds = e.Item.ContentRectangle;
            using var pen = new Pen(Border);
            int y = bounds.Top + bounds.Height / 2;
            e.Graphics.DrawLine(pen, bounds.Left + 10, y, Math.Max(bounds.Left + 11, bounds.Right - 6), y);
        }
    }

    // ————————————————————————————————————————————————
    // 圆角工具
    // ————————————————————————————————————————————————
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
        var saved = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(bounds, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
        using var pen = new Pen(border);
        g.DrawPath(pen, path);
        g.SmoothingMode = saved;
    }

    /// <summary>把圆角外的四角填成背景色（子控件里画圆角时用）。</summary>
    internal static void FillOutsideCorners(Graphics g, Rectangle bounds, float radius, Color cornerColor)
    {
        using var path = RoundedRect(new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), radius);
        using var region = new Region(bounds);
        region.Exclude(path);
        using var brush = new SolidBrush(cornerColor);
        g.FillRegion(brush, region);
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
    private const int RowHeightBase = 26;
    private const int HeaderHeightBase = 28;

    private const int WmPaint = 0x000F;
    private const int LvmGetHeader = 0x1000 + 31;
    private const int LvmGetSelectedCount = 0x1000 + 50;

    private readonly ImageList _rowMetrics;
    private HeaderOverlay? _headerOverlay;
    private int _retrieveCount;
    private int _hoverIndex = -1;
    private int _appliedRowHeight = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    public int RetrieveCount => _retrieveCount;

    // VirtualMode 下访问 SelectedIndices/SelectedItems 会遍历全部条目（10 万条要 2-3 秒），
    // 这里直接用 LVM_GETSELECTEDCOUNT（O(1)）替代。
    public int SelectedCount => IsHandleCreated
        ? SendMessage(Handle, LvmGetSelectedCount, IntPtr.Zero, IntPtr.Zero).ToInt32()
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
        BackColor = Theme.SurfaceSunken;
        ForeColor = Theme.Text;
        Font = Theme.Ui();
        HeaderStyle = ColumnHeaderStyle.Clickable;
        ShowItemToolTips = false;
        VirtualMode = true;
        AllowColumnReorder = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.ResizeRedraw, true);

        // Details 视图没有行高 API：用一个 1×N 的 ImageList 把行高顶到 26px。
        // 系统会在 ImageList 高度上再加 1px，所以这里少给 1。
        _rowMetrics = new ImageList
        {
            ImageSize = new Size(1, RowHeightBase - 1),
            ColorDepth = ColorDepth.Depth32Bit,
        };
        SmallImageList = _rowMetrics;
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
        _hoverIndex = -1;
        Invalidate();
    }

    private int ScaleOf(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRowMetrics();
        HookHeader();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _headerOverlay?.ReleaseHandle();
        _headerOverlay = null;
        base.OnHandleDestroyed(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _headerOverlay?.Invalidate();
    }

    private void ApplyRowMetrics()
    {
        int height = Math.Max(16, ScaleOf(RowHeightBase) - 1);
        if (height != _appliedRowHeight)
        {
            _appliedRowHeight = height;
            SmallImageList = null;
            _rowMetrics.ImageSize = new Size(1, height);
            SmallImageList = _rowMetrics;
        }
    }

    /// <summary>把表头控件挂上自绘覆盖层：填掉右侧空白区，并画通栏底部分隔线。</summary>
    private void HookHeader()
    {
        try
        {
            var header = SendMessage(Handle, LvmGetHeader, IntPtr.Zero, IntPtr.Zero);
            if (header == IntPtr.Zero) return;
            _headerOverlay ??= new HeaderOverlay(this);
            _headerOverlay.AssignHandle(header);
        }
        catch (Exception)
        {
            // 非 Details 视图没有表头：忽略
        }
    }

    private int HeaderHeight()
    {
        try
        {
            var header = SendMessage(Handle, LvmGetHeader, IntPtr.Zero, IntPtr.Zero);
            if (header == IntPtr.Zero) return ScaleOf(HeaderHeightBase);
            return GetWindowRect(header, out var rect) ? Math.Max(0, rect.Bottom - rect.Top) : ScaleOf(HeaderHeightBase);
        }
        catch (Exception)
        {
            return ScaleOf(HeaderHeightBase);
        }
    }

    private int TotalColumnWidth()
    {
        int total = 0;
        foreach (ColumnHeader column in Columns) total += column.Width;
        return total;
    }

    /// <summary>表头：系统的主题背景会在未覆盖的区域留一块浅色，这里补成深色。</summary>
    private sealed class HeaderOverlay : NativeWindow
    {
        private const int WmPaint = 0x000F;
        private readonly ThemedListView _owner;

        public HeaderOverlay(ThemedListView owner) => _owner = owner;

        public void Invalidate()
        {
            if (Handle == IntPtr.Zero) return;
            RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0100 | 0x0400);
        }

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != WmPaint) return;
            if (Handle == IntPtr.Zero || _owner.IsDisposed) return;

            GetClientRect(Handle, out var rc);
            int width = rc.Right;
            int height = rc.Bottom;
            if (width <= 0 || height <= 0) return;

            using var g = Graphics.FromHwnd(Handle);
            int painted = _owner.TotalColumnWidth();
            if (painted < width)
            {
                using var brush = new SolidBrush(Theme.SurfaceAlt);
                g.FillRectangle(brush, painted, 0, width - painted, height);
            }
            using var pen = new Pen(Theme.BorderStrong);
            g.DrawLine(pen, 0, height - 1, width, height - 1);
        }
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
        }
        // 用 0 号占位图（1×26）撑出行高；图标全部自绘，不依赖 ImageList。
        item.ImageIndex = 0;
        item.StateImageIndex = -1;
        e.Item = item;
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        var g = e.Graphics;
        var b = e.Bounds;
        using (var back = new SolidBrush(Theme.SurfaceAlt))
            g.FillRectangle(back, b);

        // 底部强调分隔线 + 列间 1px 竖线
        using (var pen = new Pen(Theme.BorderStrong))
            g.DrawLine(pen, b.Left, b.Bottom - 1, b.Right, b.Bottom - 1);
        if (e.ColumnIndex > 0)
        {
            using var pen = new Pen(Theme.Border);
            g.DrawLine(pen, b.Left, b.Top + 7, b.Left, b.Bottom - 9);
        }

        var header = e.Header;
        var align = header?.TextAlign ?? HorizontalAlignment.Left;
        bool sorted = SortColumnIndex >= 0 && e.ColumnIndex == SortColumnIndex;

        var rect = b;
        rect.Inflate(-10, -2);
        if (sorted) rect.Width -= 14;

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        flags |= align switch
        {
            HorizontalAlignment.Right => TextFormatFlags.Right,
            HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
            _ => TextFormatFlags.Left,
        };
        TextRenderer.DrawText(g, header?.Text ?? "", Font, rect, sorted ? Theme.Text : Theme.TextDim, flags);

        if (sorted)
        {
            var saved = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Theme.Accent);
            float cx = b.Right - 14f;
            float cy = b.Top + b.Height / 2f;
            PointF[] tri = SortAscending
                ? new[] { new PointF(cx - 4f, cy + 2.2f), new PointF(cx + 4f, cy + 2.2f), new PointF(cx, cy - 2.8f) }
                : new[] { new PointF(cx - 4f, cy - 2.2f), new PointF(cx + 4f, cy - 2.2f), new PointF(cx, cy + 2.8f) };
            g.FillPolygon(brush, tri);
            g.SmoothingMode = saved;
        }
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        var g = e.Graphics;
        var b = e.Bounds;
        bool selected = e.Item.Selected;
        Color fill;
        if (selected) fill = Theme.Selection;
        else if (e.ItemIndex == _hoverIndex) fill = Theme.SurfaceHover;
        else fill = e.ItemIndex % 2 == 0 ? Theme.SurfaceSunken : Theme.RowAlt;

        using (var brush = new SolidBrush(fill))
            g.FillRectangle(brush, b);

        if (selected)
        {
            using var accent = new SolidBrush(Theme.Accent);
            g.FillRectangle(accent, b.Left, b.Top, 2, b.Height);
        }
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        var source = RowSource;
        if (source == null || e.ItemIndex < 0 || e.ItemIndex >= source.RowCount) return;

        var g = e.Graphics;
        var b = e.Bounds;
        var item = e.Item;
        bool selected = item?.Selected == true;
        int column = e.ColumnIndex;
        var text = source.CellText(e.ItemIndex, column) ?? "";

        bool rightAligned = column < Columns.Count
            ? Columns[column].TextAlign == HorizontalAlignment.Right
            : IsRightAligned(column);

        var rect = new Rectangle(b.Left + 10, b.Top, Math.Max(0, b.Width - 18), b.Height);

        if (column == 0)
        {
            bool isDir = source.IsDirectory(e.ItemIndex);
            bool encrypted = source.IsEncrypted(e.ItemIndex);
            var glyph = encrypted ? Glyph.Lock : isDir ? Glyph.Folder : Glyph.File;
            Color iconColor = encrypted
                ? Theme.Warn
                : isDir
                    ? Theme.WithAlpha(Theme.Accent, 190)
                    : selected ? Theme.TextDim : Theme.TextFaint;

            int iconSize = ScaleOf(16);
            int left = b.Left + ScaleOf(10);
            int top = b.Top + (b.Height - iconSize) / 2;
            Theme.DrawGlyph(g, glyph, new RectangleF(left, top, iconSize, iconSize), iconColor, 1.5f);

            int textLeft = left + iconSize + ScaleOf(8);
            rect = new Rectangle(textLeft, b.Top, Math.Max(0, b.Right - textLeft - ScaleOf(8)), b.Height);
            rightAligned = false;
        }

        if (string.IsNullOrEmpty(text)) return;

        var font = Theme.HasSurrogatePair(text) ? Theme.EmojiCapableFont(Font) : Font;
        Color color = selected
            ? Theme.Text
            : column is 3 or 4 or 5 ? Theme.TextDim : Theme.Text;

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                    (rightAligned ? TextFormatFlags.Right : TextFormatFlags.Left);
        TextRenderer.DrawText(g, text, font, rect, color, flags);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = -1;
        if (e.X >= 0 && e.Y >= 0 && e.X < ClientSize.Width && e.Y < ClientSize.Height)
        {
            var hit = HitTest(e.Location);
            if (hit.Item != null) index = hit.Item.Index;
        }
        SetHoverIndex(index);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHoverIndex(-1);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        SetHoverIndex(-1);
        Invalidate();
    }

    private void SetHoverIndex(int index)
    {
        if (_hoverIndex == index) return;
        int previous = _hoverIndex;
        _hoverIndex = index;
        InvalidateRow(previous);
        InvalidateRow(index);
    }

    private void InvalidateRow(int index)
    {
        if (index < 0 || !IsHandleCreated)
        {
            Invalidate();
            return;
        }
        try
        {
            if (index >= VirtualListSize) { Invalidate(); return; }
            var rect = GetItemRect(index);
            if (rect.Height <= 0)
            {
                Invalidate();
                return;
            }
            rect.Inflate(0, 1);
            Invalidate(rect);
        }
        catch (Exception)
        {
            Invalidate();
        }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // 原生 ListView 自己处理 WM_PAINT，托管 OnPaint 不一定会被调用，
        // 所以空态直接画在窗口 DC 上（必须在 base 之后画，否则会被原生重绘覆盖）。
        if (m.Msg == WmPaint && VirtualListSize == 0 && IsHandleCreated)
        {
            try
            {
                using var g = Graphics.FromHwnd(Handle);
                PaintEmptyState(g);
            }
            catch (Exception)
            {
                // 窗口正在销毁：忽略
            }
        }
    }

    private void PaintEmptyState(Graphics g)
    {
        // 空态：居中图标 + 主标题 + 副标题，整体在列表可视区（表头以下）垂直居中。
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int header = HeaderHeight();
        var area = new Rectangle(0, header, ClientSize.Width, Math.Max(0, ClientSize.Height - header));
        if (area.Width < 80 || area.Height < 90) return;

        var title = string.IsNullOrEmpty(EmptyText) ? "没有打开的归档" : EmptyText;
        var subTitle = EmptySubText ?? "";
        var titleFont = Theme.Ui(3f, FontStyle.Bold);
        var subFont = Theme.Ui();

        var textFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak |
                        TextFormatFlags.NoPrefix | TextFormatFlags.Top;

        int maxTitleWidth = Math.Max(120, area.Width - ScaleOf(80));
        var titleSize = TextRenderer.MeasureText(title, titleFont, new Size(maxTitleWidth, ScaleOf(200)), textFlags);
        var subSize = string.IsNullOrWhiteSpace(subTitle)
            ? Size.Empty
            : TextRenderer.MeasureText(subTitle, subFont,
                new Size(Math.Max(120, area.Width - ScaleOf(140)), ScaleOf(300)), textFlags);

        int iconSize = ScaleOf(48);
        int gapIcon = ScaleOf(16);
        int gapSub = ScaleOf(10);
        int block = iconSize + gapIcon + titleSize.Height + (subSize.Height > 0 ? gapSub + subSize.Height : 0);
        int top = area.Top + Math.Max(ScaleOf(10), (area.Height - block) / 2);

        var iconRect = new RectangleF(area.Left + (area.Width - iconSize) / 2f, top, iconSize, iconSize);
        Theme.DrawGlyph(g, Glyph.Archive, iconRect, Theme.WithAlpha(Theme.TextFaint, 160), 1.5f);

        var titleRect = new Rectangle(area.Left + ScaleOf(40), (int)Math.Round(iconRect.Bottom) + gapIcon,
            Math.Max(40, area.Width - ScaleOf(80)), titleSize.Height + ScaleOf(4));
        TextRenderer.DrawText(g, title, titleFont, titleRect, Theme.TextDim, textFlags);

        if (subSize.Height > 0)
        {
            // 逐行画，行距比默认松一点，副标题更好读
            var lines = subTitle.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int lineHeight = TextRenderer.MeasureText("测", subFont).Height;
            int lineStep = lineHeight + ScaleOf(6);
            int y = titleRect.Bottom + gapSub;
            foreach (var line in lines)
            {
                var lineRect = new Rectangle(area.Left + ScaleOf(70), y,
                    Math.Max(40, area.Width - ScaleOf(140)), lineStep);
                TextRenderer.DrawText(g, line, subFont, lineRect, Theme.TextFaint, textFlags);
                y += lineStep;
            }
        }

        titleFont.Dispose();
        subFont.Dispose();
    }

    private static bool IsRightAligned(int column) => column is 1 or 2;
}

/// <summary>自绘进度条：凹陷轨道 + 强调色填充 + 顶部 1px 高光，支持不定进度。</summary>
internal sealed class FlatProgressBar : Control
{
    private double _value;
    private bool _indeterminate;
    private readonly System.Windows.Forms.Timer _timer;
    private int _offset;

    public FlatProgressBar()
    {
        Name = "progressBar";
        Height = 12;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        _timer = new System.Windows.Forms.Timer { Interval = 33 };
        _timer.Tick += (_, _) =>
        {
            _offset = (_offset + 5) % 120;
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

        using (var baseFill = new SolidBrush(Parent?.BackColor ?? Theme.Surface))
            g.FillRectangle(baseFill, ClientRectangle);

        int trackHeight = Math.Clamp(Height, 4, 10);
        var track = new RectangleF(0, (Height - trackHeight) / 2f, Math.Max(1f, Width), trackHeight);
        float radius = trackHeight / 2f;

        using (var path = Theme.RoundedRect(track, radius))
        using (var back = new SolidBrush(Theme.SurfaceSunken))
        {
            g.FillPath(back, path);
        }

        if (_indeterminate)
        {
            float chunk = Math.Max(36f, Width * 0.3f);
            float x = (Width + chunk) * (_offset / 120f) - chunk;
            var chunkRect = RectangleF.Intersect(track, new RectangleF(x, track.Y, chunk, track.Height));
            if (chunkRect.Width > 1.5f)
            {
                using var path = Theme.RoundedRect(chunkRect, radius);
                using var brush = new LinearGradientBrush(
                    new RectangleF(chunkRect.X, chunkRect.Y, Math.Max(1f, chunkRect.Width), chunkRect.Height),
                    Theme.WithAlpha(Theme.Accent, 60), Theme.Accent, LinearGradientMode.Horizontal);
                g.FillPath(brush, path);
            }
            return;
        }

        float percent = (float)Math.Clamp(_value / 100.0, 0.0, 1.0);
        float fillWidth = track.Width * percent;
        if (fillWidth < 1f) return;

        var fill = new RectangleF(track.X, track.Y, fillWidth, track.Height);
        using (var path = Theme.RoundedRect(fill, radius))
        using (var brush = new LinearGradientBrush(
            new RectangleF(track.X, track.Y, Math.Max(1f, fillWidth), track.Height),
            Theme.AccentDark, Theme.Accent, LinearGradientMode.Horizontal))
        {
            g.FillPath(brush, path);
        }

        // 顶部 1px 高光
        if (fillWidth > 3f)
        {
            var highlight = new RectangleF(fill.X + radius * 0.5f, fill.Y + 0.5f,
                Math.Max(1f, fill.Width - radius), 1f);
            using var path = Theme.RoundedRect(highlight, 0.5f);
            using var brush = new SolidBrush(Color.FromArgb(64, 255, 255, 255));
            g.FillPath(brush, path);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>深色复选框：自绘圆角方框/勾，去掉系统焦点虚线。</summary>
internal sealed class ThemeCheckBox : CheckBox
{
    private bool _hover;

    public ThemeCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false;
        Height = 24;
        ForeColor = Theme.Text;
        // 不设 Color.Transparent：透明背景会让父容器用子控件坐标系重画（深色卡片上会出现错位边线）。
        Cursor = Cursors.Hand;
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        // 不调用 base：CheckBox 的透明背景机制会让父容器（例如深色卡片）
        // 在子控件坐标系里整块重画一次，深色卡片上会留下一条错位的边线。
        using var brush = new SolidBrush(Parent?.BackColor ?? Theme.Background);
        pevent.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // 系统在复选框首行会残留一像素父级边线的混合色（深色卡片上是一条错位细线），
        // 控件完全自绘，这里把首行按父容器底色重涂一次即可，不影响其它像素。
        if (m.Msg == 0x000F && IsHandleCreated && Height > 2)
        {
            try
            {
                using var g = Graphics.FromHwnd(Handle);
                using var brush = new SolidBrush(Parent?.BackColor ?? Theme.Background);
                g.FillRectangle(brush, 0, 0, Width, 1);
            }
            catch { }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var background = Parent?.BackColor ?? Theme.Background;
        using (var bg = new SolidBrush(background))
            g.FillRectangle(bg, ClientRectangle);

        int boxSize = (int)Math.Round(15 * DeviceDpi / 96.0);
        var box = new Rectangle((int)Math.Round(1 * DeviceDpi / 96.0), (Height - boxSize) / 2, boxSize, boxSize);

        Color fillColor = !Enabled
            ? Theme.Surface
            : Checked ? Theme.Accent
            : _hover ? Theme.SurfaceActive : Theme.SurfaceSunken;
        Color borderColor = !Enabled
            ? Theme.Border
            : Checked ? Theme.Accent
            : _hover ? Theme.BorderStrong : Theme.Border;

        using (var path = Theme.RoundedRect(new RectangleF(box.X + 0.5f, box.Y + 0.5f, box.Width - 1f, box.Height - 1f), 4f))
        {
            using var fill = new SolidBrush(fillColor);
            g.FillPath(fill, path);
            using var border = new Pen(borderColor);
            g.DrawPath(border, path);
        }

        if (Checked)
        {
            using var check = new Pen(Enabled ? Color.White : Theme.TextDim, 1.8f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            float x = box.X, y = box.Y;
            g.DrawLines(check, new[]
            {
                new PointF(x + box.Width * 0.24f, y + box.Height * 0.52f),
                new PointF(x + box.Width * 0.43f, y + box.Height * 0.72f),
                new PointF(x + box.Width * 0.78f, y + box.Height * 0.28f),
            });
        }

        var textRect = new Rectangle(box.Right + (int)Math.Round(7 * DeviceDpi / 96.0), 0,
            Math.Max(0, Width - box.Right - 7), Height);
        TextRenderer.DrawText(g, Text, Font, textRect,
            Enabled ? ForeColor : Theme.TextFaint,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        if (Focused && ShowFocusCues && Enabled)
        {
            using var focusPen = new Pen(Theme.WithAlpha(Theme.Accent, 150));
            using var path = Theme.RoundedRect(new RectangleF(box.X - 2.5f, box.Y - 2.5f, box.Width + 5f, box.Height + 5f), 6f);
            g.DrawPath(focusPen, path);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
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

    private bool _hover;

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
        ItemHeight = 24;
        IntegralHeight = false;
        MaxDropDownItems = 12;
        Cursor = Cursors.Hand;
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
        if (rect.Width < 4 || rect.Height < 4) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(Theme.SurfaceAlt))
            g.FillRectangle(back, rect);

        var bounds = new RectangleF(0.5f, 0.5f, rect.Width - 1f, rect.Height - 1f);
        using (var path = Theme.RoundedRect(bounds, 6f))
        {
            Color borderColor = !Enabled ? Theme.Border
                : Focused ? Theme.Accent
                : _hover ? Theme.BorderStrong : Theme.Border;
            using var pen = new Pen(borderColor);
            g.DrawPath(pen, path);
        }
        Theme.FillOutsideCorners(g, rect, 6f, Parent?.BackColor ?? Theme.Background);

        var text = SelectedIndex >= 0 ? Items[SelectedIndex]?.ToString() ?? "" : Text;
        int padLeft = (int)Math.Round(9 * DeviceDpi / 96.0);
        var textRect = new Rectangle(padLeft, 0, Math.Max(0, rect.Width - padLeft - 28), rect.Height);
        var font = Theme.HasSurrogatePair(text) ? Theme.EmojiCapableFont(Font) : Font;
        TextRenderer.DrawText(g, text, font, textRect,
            Enabled ? Theme.Text : Theme.TextFaint,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        using (var chevron = new Pen(
            !Enabled ? Theme.TextFaint : _hover ? Theme.Accent : Theme.TextDim, 1.7f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        })
        {
            float cx = rect.Width - 14f;
            float cy = rect.Height / 2f;
            g.DrawLines(chevron, new[]
            {
                new PointF(cx - 3.6f, cy - 1.9f),
                new PointF(cx, cy + 1.9f),
                new PointF(cx + 3.6f, cy - 1.9f),
            });
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
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

    protected override void OnDropDown(EventArgs e)
    {
        base.OnDropDown(e);
        Invalidate();
    }

    protected override void OnDropDownClosed(EventArgs e)
    {
        base.OnDropDownClosed(e);
        Invalidate();
    }
}

/// <summary>工具栏按钮：34px 高、6px 圆角、悬停/按下自绘，图标悬停变强调色。</summary>
internal sealed class ThemeToolButton : Button
{
    private const int FixedHeight = 34;
    private const float CornerRadius = 6f;

    private Glyph _glyph;
    private bool _hover;
    private bool _pressed;

    public ThemeToolButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = Theme.Ui();
        Cursor = Cursors.Hand;
        AutoSize = false;
        Height = FixedHeight;
        Margin = new Padding(2, 0, 2, 0);
        TextAlign = ContentAlignment.MiddleCenter;
        TabStop = true;
        DoubleBuffered = true;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Glyph Glyph
    {
        get => _glyph;
        set
        {
            if (_glyph == value) return;
            _glyph = value;
            Invalidate();
        }
    }

    private int ScaleOf(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    private void RecalcSize()
    {
        if (string.IsNullOrEmpty(Text))
        {
            Width = ScaleOf(52);
            return;
        }
        var measured = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        int icon = ScaleOf(16);
        int pad = ScaleOf(11);
        int gap = ScaleOf(7);
        Width = pad * 2 + icon + gap + measured.Width + ScaleOf(2);
        Height = ScaleOf(FixedHeight);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        RecalcSize();
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        RecalcSize();
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        RecalcSize();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color outer = Parent?.BackColor ?? Theme.Surface;
        using (var bg = new SolidBrush(outer))
            g.FillRectangle(bg, ClientRectangle);

        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using var path = Theme.RoundedRect(bounds, CornerRadius);

        bool enabled = Enabled;
        Color borderColor;
        if (!enabled)
        {
            borderColor = Theme.Blend(Theme.Surface, Theme.Border, 0.45f);
            using var fill = new SolidBrush(Theme.Surface);
            g.FillPath(fill, path);
        }
        else if (_pressed)
        {
            borderColor = Theme.AccentDark;
            using var fill = new SolidBrush(Theme.SurfaceActive);
            g.FillPath(fill, path);
        }
        else if (_hover)
        {
            borderColor = Theme.BorderStrong;
            using var brush = new LinearGradientBrush(
                new RectangleF(bounds.X, bounds.Y, Math.Max(1f, bounds.Width), Math.Max(1f, bounds.Height)),
                Theme.Blend(Theme.SurfaceHover, Theme.SurfaceActive, 0.5f),
                Theme.SurfaceHover,
                LinearGradientMode.Vertical);
            g.FillPath(brush, path);
        }
        else
        {
            borderColor = Theme.Blend(Theme.Surface, Theme.Border, 0.65f);
            using var fill = new SolidBrush(Theme.Surface);
            g.FillPath(fill, path);
        }

        using (var pen = new Pen(borderColor))
            g.DrawPath(pen, path);

        int iconSize = ScaleOf(16);
        bool hasText = !string.IsNullOrEmpty(Text);
        Size textSize = hasText
            ? TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine)
            : Size.Empty;
        int gap = hasText ? ScaleOf(7) : 0;
        float content = iconSize + gap + (hasText ? textSize.Width : 0);
        float cx = Math.Max(ScaleOf(6), (Width - content) / 2f);

        Color iconColor = !enabled
            ? Theme.TextFaint
            : _hover || _pressed ? Theme.Accent : Theme.TextDim;

        Theme.DrawGlyph(g, _glyph, new RectangleF(cx, (Height - iconSize) / 2f, iconSize, iconSize), iconColor, 1.6f);

        if (hasText)
        {
            var textRect = new Rectangle((int)Math.Round(cx + iconSize + gap), 0,
                Math.Max(1, (int)Math.Ceiling((double)textSize.Width) + ScaleOf(2)), Height);
            TextRenderer.DrawText(g, Text, Font, textRect,
                enabled ? ForeColor : Theme.TextFaint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        if (Focused && ShowFocusCues && enabled)
        {
            var focusRect = new RectangleF(bounds.X + 2.5f, bounds.Y + 2.5f,
                Math.Max(1f, bounds.Width - 5f), Math.Max(1f, bounds.Height - 5f));
            using var focusPath = Theme.RoundedRect(focusRect, Math.Max(2f, CornerRadius - 2f));
            using var focusPen = new Pen(Theme.WithAlpha(Theme.Accent, 170));
            g.DrawPath(focusPen, focusPath);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = false;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!Enabled) { _hover = false; _pressed = false; }
        Invalidate();
    }
}

/// <summary>对话框按钮：32px 高、6px 圆角；primary = 强调底 + 白字。</summary>
internal sealed class ThemeDialogButton : Button
{
    private const float CornerRadius = 6f;

    private readonly Color _initialHover;
    private readonly Color _initialDown;
    private readonly Color _initialBorder;
    private bool _hover;
    private bool _pressed;

    public ThemeDialogButton(string name, string text, bool primary, int width)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Name = name;
        Text = text;
        Font = Theme.Ui();
        Cursor = Cursors.Hand;
        AutoSize = false;
        Size = new Size(width, 32);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        TabStop = true;
        Margin = new Padding(0);

        BackColor = primary ? Theme.Accent : Theme.SurfaceAlt;
        ForeColor = primary ? Color.White : Theme.Text;

        _initialHover = primary ? Theme.AccentHover : Theme.SurfaceHover;
        _initialDown = primary ? Theme.AccentDark : Theme.SurfaceActive;
        _initialBorder = primary ? Theme.Accent : Theme.Border;
        FlatAppearance.MouseOverBackColor = _initialHover;
        FlatAppearance.MouseDownBackColor = _initialDown;
        FlatAppearance.BorderColor = _initialBorder;
    }

    private Color HoverFill => FlatAppearance.MouseOverBackColor != _initialHover
        ? FlatAppearance.MouseOverBackColor
        : Theme.Blend(BackColor, Color.White, 0.10f);

    private Color PressedFill => FlatAppearance.MouseDownBackColor != _initialDown
        ? FlatAppearance.MouseDownBackColor
        : Theme.Blend(BackColor, Color.Black, 0.12f);

    private Color BorderColor => FlatAppearance.BorderColor != _initialBorder
        ? FlatAppearance.BorderColor
        : BackColor == Theme.Accent ? Theme.Accent
        : _hover ? Theme.BorderStrong : Theme.Border;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var bg = new SolidBrush(Parent?.BackColor ?? Theme.Background))
            g.FillRectangle(bg, ClientRectangle);

        float radius = Math.Min(CornerRadius, Math.Max(2f, Height / 2f - 1f));
        var bounds = new RectangleF(0.5f, 0.5f, Math.Max(1f, Width - 1f), Math.Max(1f, Height - 1f));
        using var path = Theme.RoundedRect(bounds, radius);

        bool enabled = Enabled;
        Color fill = !enabled
            ? Theme.Blend(BackColor, Theme.Surface, 0.62f)
            : _pressed ? PressedFill
            : _hover ? HoverFill
            : BackColor;

        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);

        // primary 按钮加一层顶部高光，避免大色块发平
        if (enabled && BackColor == Theme.Accent && bounds.Height > 8)
        {
            using var gloss = new LinearGradientBrush(
                new RectangleF(bounds.X, bounds.Y, Math.Max(1f, bounds.Width), bounds.Height * 0.6f),
                Color.FromArgb(46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255),
                LinearGradientMode.Vertical);
            g.FillPath(gloss, path);
        }

        using (var pen = new Pen(!enabled ? Theme.Border : BorderColor))
            g.DrawPath(pen, path);

        Color textColor = !enabled
            ? Theme.TextFaint
            : ForeColor;
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

        if (Focused && ShowFocusCues && enabled)
        {
            var focusRect = new RectangleF(bounds.X + 2.5f, bounds.Y + 2.5f,
                Math.Max(1f, bounds.Width - 5f), Math.Max(1f, bounds.Height - 5f));
            using var focusPath = Theme.RoundedRect(focusRect, Math.Max(2f, radius - 2f));
            using var focusPen = new Pen(Color.FromArgb(170, 255, 255, 255));
            g.DrawPath(focusPen, focusPath);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = false;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!Enabled) { _hover = false; _pressed = false; }
        Invalidate();
    }
}

/// <summary>34×34 方形图标按钮（路径栏等紧凑位置），自带 hover/pressed 反馈。</summary>
internal sealed class ThemeIconButton : Button
{
    private const float CornerRadius = 6f;

    private Glyph _glyph;
    private bool _hover;
    private bool _pressed;

    public ThemeIconButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = Theme.Surface;
        Font = Theme.Ui();
        Cursor = Cursors.Hand;
        AutoSize = false;
        Size = new Size(34, 34);
        TabStop = true;
        Margin = new Padding(0);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Glyph Glyph
    {
        get => _glyph;
        set
        {
            if (_glyph == value) return;
            _glyph = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var bg = new SolidBrush(Parent?.BackColor ?? Theme.Surface))
            g.FillRectangle(bg, ClientRectangle);

        var bounds = new RectangleF(0.5f, 0.5f, Math.Max(1f, Width - 1f), Math.Max(1f, Height - 1f));
        using var path = Theme.RoundedRect(bounds, CornerRadius);

        bool enabled = Enabled;
        Color borderColor;
        if (!enabled)
        {
            borderColor = Theme.Blend(Theme.Surface, Theme.Border, 0.4f);
            using var fill = new SolidBrush(Theme.Surface);
            g.FillPath(fill, path);
        }
        else if (_pressed)
        {
            borderColor = Theme.AccentDark;
            using var fill = new SolidBrush(Theme.SurfaceActive);
            g.FillPath(fill, path);
        }
        else if (_hover)
        {
            borderColor = Theme.BorderStrong;
            using var fill = new SolidBrush(Theme.SurfaceHover);
            g.FillPath(fill, path);
        }
        else
        {
            borderColor = Theme.Blend(Theme.Surface, Theme.Border, 0.65f);
            using var fill = new SolidBrush(Theme.Surface);
            g.FillPath(fill, path);
        }

        using (var pen = new Pen(borderColor))
            g.DrawPath(pen, path);

        int iconSize = (int)Math.Round(18 * DeviceDpi / 96.0);
        Color iconColor = !enabled
            ? Theme.TextFaint
            : _hover || _pressed ? Theme.Accent : Theme.TextDim;
        Theme.DrawGlyph(g, _glyph,
            new RectangleF((Width - iconSize) / 2f, (Height - iconSize) / 2f, iconSize, iconSize),
            iconColor, 1.6f);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = false;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!Enabled) { _hover = false; _pressed = false; }
        Invalidate();
    }
}

/// <summary>输入框：无系统边框，1px 圆角描边，聚焦变强调色（文本区留 7px 内缩边距）。</summary>
internal sealed class ThemedTextBox : TextBox
{
    private const int WmPaint = 0x000F;
    private const int EmSetMargins = 0x00D3;
    private const int EcLeftMargin = 0x0001;
    private const int EcRightMargin = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public ThemedTextBox()
    {
        // 单行 TextBox 默认会按字体高度自动收缩（BorderStyle=None 时只有 16px），
        // 关掉 AutoSize 才能拿到 26px 的输入行高度；原生单行 EDIT 会把文本垂直居中。
        AutoSize = false;
        BorderStyle = BorderStyle.None;
        Height = 26;
        BackColor = Theme.SurfaceAlt;
        ForeColor = Theme.Text;
        Font = Theme.Ui();
        Cursor = Cursors.IBeam;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTextMargins();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyTextMargins();
        Invalidate();
    }

    private void ApplyTextMargins()
    {
        if (!IsHandleCreated) return;
        int margin = (int)Math.Round(7 * DeviceDpi / 96.0);
        SendMessage(Handle, EmSetMargins,
            (IntPtr)(EcLeftMargin | EcRightMargin),
            (IntPtr)((margin << 16) | (margin & 0xFFFF)));
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != WmPaint || !IsHandleCreated) return;
        using var g = Graphics.FromHwnd(Handle);
        PaintFrame(g);
    }

    private void PaintFrame(Graphics g)
    {
        if (Width < 4 || Height < 4) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        Color borderColor = !Enabled
            ? Theme.Border
            : Focused ? Theme.Accent : Theme.BorderStrong;

        Theme.FillOutsideCorners(g, new Rectangle(0, 0, Width, Height), 6f,
            Parent?.BackColor ?? Theme.Background);

        using var path = Theme.RoundedRect(bounds, 6f);
        using var pen = new Pen(borderColor) { Width = 1f };
        g.DrawPath(pen, path);
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

/// <summary>卡片容器：圆角 SurfaceAlt 底 + 1px Border 描边，内部留 padding，不含标题栏。</summary>
internal sealed class ThemeCard : Panel
{
    private const float CornerRadius = 8f;

    public ThemeCard(int padding)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.SurfaceAlt;
        ForeColor = Theme.Text;
        Padding = new Padding(padding);
        Margin = new Padding(0);
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var clip = e.ClipRectangle;
        if (clip.Width <= 0 || clip.Height <= 0) return;

        // 底子先铺满裁剪区：卡片的子控件重绘时会请求父级在“子控件坐标系”里重画一次，
        // 那时只有裁剪区里这一块需要是卡片底色，绝不能把边框与圆角再画一遍。
        using (var body = new SolidBrush(Theme.SurfaceAlt))
            g.FillRectangle(body, clip);

        // 识别“借子控件 DC 的父级重画”：报告的裁剪区是整张卡片，真实可见区域却只是那个子控件。
        var visible = g.VisibleClipBounds;
        bool foreignDc = clip.Width >= Width - 1 && clip.Height >= Height - 1 &&
                         (visible.Width < Width - 2 || visible.Height < Height - 2);
        if (foreignDc || Width < 8 || Height < 8) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var corner = new SolidBrush(Parent?.BackColor ?? Theme.Background))
        {
            using var region = new Region(new Rectangle(0, 0, Width, Height));
            using var path = Theme.RoundedRect(bounds, CornerRadius);
            region.Exclude(path);
            g.FillRegion(corner, region);
        }

        using (var path = Theme.RoundedRect(bounds, CornerRadius))
        {
            using (var fill = new SolidBrush(Theme.SurfaceAlt))
                g.FillPath(fill, path);
            using (var pen = new Pen(Theme.Border))
                g.DrawPath(pen, path);
        }
    }
}

/// <summary>徽章/药丸：中性 chip 或强调色 chip（状态栏「已加密」「7-Zip 25.01」）。</summary>
internal sealed class ThemeBadge : Control
{
    private readonly string _text;
    private readonly bool _hasAccent;
    private readonly Color _accent;

    public ThemeBadge(string text, Color? accent = null)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _text = text ?? "";
        _hasAccent = accent.HasValue;
        _accent = accent ?? Theme.Accent;
        Font = Theme.Ui(-1f);
        ForeColor = _hasAccent ? _accent : Theme.TextDim;
        Cursor = Cursors.Default;
        TabStop = false;
        Margin = new Padding(0, 0, 6, 0);
        RecalcSize();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        RecalcSize();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        RecalcSize();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        RecalcSize();
    }

    private void RecalcSize()
    {
        var measured = TextRenderer.MeasureText(_text, Font, new Size(600, 40),
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        int padX = (int)Math.Round(9 * DeviceDpi / 96.0);
        Size = new Size(measured.Width + padX * 2, (int)Math.Round(20 * DeviceDpi / 96.0));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var bg = new SolidBrush(Parent?.BackColor ?? Theme.Surface))
            g.FillRectangle(bg, ClientRectangle);

        if (Width < 4 || Height < 4) return;
        float radius = Height / 2f;
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using var path = Theme.RoundedRect(bounds, radius);

        Color fill = _hasAccent ? Theme.Blend(Theme.AccentSoft, _accent, 0.18f) : Theme.SurfaceAlt;
        Color border = _hasAccent ? Theme.WithAlpha(_accent, 120) : Theme.Border;

        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);
        using (var pen = new Pen(border))
            g.DrawPath(pen, path);

        TextRenderer.DrawText(g, _text, Font, ClientRectangle,
            _hasAccent ? _accent : Theme.TextDim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
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
        Theme.ApplyRoundedCorners(this);
    }
}

