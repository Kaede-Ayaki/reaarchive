// Dialogs.cs — 全部对话框（深色、无设计器、纯代码布局）。
using System.Runtime.ExceptionServices;
using REAArchive.Archive;

namespace REAArchive.Ui;

internal enum MsgKind
{
    Info,
    Warn,
    Error,
    Question,
}

internal static class UiFormat
{
    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalSeconds < 1) return "不到 1 秒";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} 小时 {t.Minutes} 分";
        if (t.TotalMinutes >= 1) return $"{t.Minutes} 分 {t.Seconds} 秒";
        return $"{t.TotalSeconds:0.0} 秒";
    }

    public static string When(DateTime? value) => value?.ToString("yyyy-MM-dd HH:mm") ?? "—";

    public static string Shorten(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace("\r", " ").Replace("\n", " ");
        return text.Length <= max ? text : "…" + text[^Math.Max(1, max - 1)..];
    }
}

internal static class DialogUi
{
    public static Label AddLabel(Control parent, string text, int x, int y, bool dim = false, bool bold = false, float delta = 0f)
    {
        var label = Theme.MakeLabel(text, dim, bold, delta);
        label.Location = new Point(x, y);
        parent.Controls.Add(label);
        return label;
    }

    public static TextBox AddTextBox(Control parent, string name, int x, int y, int width)
    {
        var box = Theme.MakeTextBox(name, width);
        box.Location = new Point(x, y);
        parent.Controls.Add(box);
        return box;
    }

    public static Button AddButton(Control parent, string name, string text, int x, int y, int width = 96, bool primary = false)
    {
        var button = Theme.DialogButton(name, text, primary, width);
        button.Location = new Point(x, y);
        parent.Controls.Add(button);
        return button;
    }

    public static Panel AddHLine(Control parent, int x, int y, int width)
    {
        var line = new Panel { BackColor = Theme.Border, Location = new Point(x, y), Size = new Size(width, 1) };
        parent.Controls.Add(line);
        return line;
    }

    public static void BrowseFolder(IWin32Window owner, TextBox target, string description)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (Directory.Exists(target.Text)) dialog.SelectedPath = target.Text;
        if (dialog.ShowDialog(owner) == DialogResult.OK) target.Text = dialog.SelectedPath;
    }
}

// ————————————————————————————————————————————————————————————————
// 深色消息框
// ————————————————————————————————————————————————————————————————
internal static class MsgBox
{
    public static void Info(IWin32Window? owner, string text, string caption = "提示")
        => Show(owner, text, caption, MsgKind.Info, false, false);

    public static void Warn(IWin32Window? owner, string text, string caption = "提示")
        => Show(owner, text, caption, MsgKind.Warn, false, false);

    public static void Error(IWin32Window? owner, string text, string caption = "操作失败")
        => Show(owner, text, caption, MsgKind.Error, false, true);

    public static bool Confirm(IWin32Window? owner, string text, string caption = "确认", bool danger = false)
        => Show(owner, text, caption, MsgKind.Question, true, danger);

    private static bool Show(IWin32Window? owner, string text, string caption, MsgKind kind, bool ask, bool danger)
    {
        using var form = new MessageForm(text, caption, kind, ask, danger);
        var result = owner != null ? form.ShowDialog(owner) : form.ShowDialog();
        return result == DialogResult.OK;
    }

    private sealed class MessageForm : ThemedForm
    {
        public MessageForm(string text, string caption, MsgKind kind, bool ask, bool danger)
        {
            Text = caption;
            ShowIcon = false;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Background;

            var glyph = kind switch
            {
                MsgKind.Warn => Glyph.Warning,
                MsgKind.Error => Glyph.Warning,
                MsgKind.Question => Glyph.Info,
                _ => Glyph.Info,
            };
            var color = kind switch
            {
                MsgKind.Warn => Theme.Warn,
                MsgKind.Error => Theme.Danger,
                MsgKind.Question => Theme.Accent,
                _ => Theme.Accent,
            };

            var iconBox = new PictureBox
            {
                Image = Theme.GlyphImage(glyph, 38, color, 2f),
                Size = new Size(38, 38),
                SizeMode = PictureBoxSizeMode.Zoom,
                Location = new Point(22, 24),
                BackColor = Color.Transparent,
            };
            Controls.Add(iconBox);

            var bodyFont = Theme.Ui();
            var measured = TextRenderer.MeasureText(text, bodyFont, new Size(470, 1400),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            var label = new Label
            {
                Text = text,
                Font = bodyFont,
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                AutoSize = false,
                Bounds = new Rectangle(76, 22, Math.Max(280, measured.Width + 6), Math.Max(28, measured.Height + 6)),
            };
            Controls.Add(label);

            int clientWidth = Math.Max(400, label.Right + 24);
            int clientHeight = label.Bottom + 72;
            ClientSize = new Size(clientWidth, clientHeight);

            int buttonY = clientHeight - 52;
            var ok = DialogUi.AddButton(this, "btnOk", ask ? "确定" : "确定",
                clientWidth - (ask ? 210 : 122), buttonY, 98, primary: !danger);
            if (danger)
            {
                ok.BackColor = Color.FromArgb(0x8f, 0x36, 0x3d);
                ok.FlatAppearance.BorderColor = Theme.Danger;
                ok.FlatAppearance.MouseOverBackColor = Theme.Danger;
            }
            ok.DialogResult = DialogResult.OK;
            AcceptButton = ok;

            if (ask)
            {
                var cancel = DialogUi.AddButton(this, "btnCancel", "取消", clientWidth - 104, buttonY, 84);
                cancel.DialogResult = DialogResult.Cancel;
                CancelButton = cancel;
            }
            else
            {
                CancelButton = ok;
            }

            KeyPreview = true;
        }
    }
}

// ————————————————————————————————————————————————————————————————
// 密码框（不保存、不写日志）
// ————————————————————————————————————————————————————————————————
internal sealed class PasswordForm : ThemedForm
{
    private readonly TextBox _password;
    public string Password => _password.Text;

    public PasswordForm(string target, string prompt)
    {
        Text = "需要密码";
        ClientSize = new Size(470, 214);
        ShowIcon = true;

        DialogUi.AddLabel(this, prompt, 20, 18, bold: false);
        DialogUi.AddLabel(this, "归档：" + UiFormat.Shorten(target, 68), 20, 46, dim: true);
        _password = DialogUi.AddTextBox(this, "txtPassword", 20, 76, 424);
        _password.UseSystemPasswordChar = true;
        _password.Focus();

        var show = Theme.MakeCheckBox("chkShowPassword", "显示密码", 200);
        show.Location = new Point(20, 112);
        show.CheckedChanged += (_, _) => _password.UseSystemPasswordChar = !show.Checked;
        Controls.Add(show);

        DialogUi.AddLabel(this, "密码只用于本次操作，不会保存到任何文件。", 20, 144, dim: true);

        var ok = DialogUi.AddButton(this, "btnOk", "确定", 290, 166, 76, primary: true);
        ok.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };
        AcceptButton = ok;
        var cancel = DialogUi.AddButton(this, "btnCancel", "取消", 374, 166, 76);
        cancel.DialogResult = DialogResult.Cancel;
        CancelButton = cancel;
    }
}

internal static class PasswordDialog
{
    public static string? Ask(IWin32Window? owner, string target, string prompt = "该归档已加密，请输入密码：")
    {
        using var form = new PasswordForm(target, prompt);
        var result = owner != null ? form.ShowDialog(owner) : form.ShowDialog();
        return result == DialogResult.OK ? form.Password : null;
    }
}

// ————————————————————————————————————————————————————————————————
// 解压到…
// ————————————————————————————————————————————————————————————————
internal sealed class ExtractResultOptions
{
    public required string Destination { get; init; }
    public required OverwritePolicy Policy { get; init; }
    public string? Password { get; init; }
    public bool OpenAfter { get; init; }
    public bool PreservePaths { get; init; } = true;
}

internal sealed class ExtractDialog : ThemedForm
{
    private readonly TextBox _destination;
    private readonly ComboBox _conflict;
    private readonly TextBox _password;
    private readonly CheckBox _openAfter;
    private readonly CheckBox _preservePaths;
    private ExtractResultOptions? _result;

    public static ExtractResultOptions? Show(IWin32Window? owner, string archivePath, AppSettings settings,
        int selectedCount, bool encrypted, string? knownPassword)
    {
        using var dialog = new ExtractDialog(archivePath, settings, selectedCount, encrypted, knownPassword);
        var result = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return result == DialogResult.OK ? dialog._result : null;
    }

    private ExtractDialog(string archivePath, AppSettings settings, int selectedCount, bool encrypted, string? knownPassword)
    {
        Text = "解压到…";
        ClientSize = new Size(640, 356);
        ShowIcon = true;

        DialogUi.AddLabel(this, "解压到", 22, 16, bold: true, delta: 1.5f);
        var scope = selectedCount > 0 ? $"（仅解压选中的 {selectedCount} 项）" : "（解压全部内容）";
        DialogUi.AddLabel(this, "归档：" + UiFormat.Shorten(Path.GetFileName(archivePath), 52) + "  " + scope,
            22, 48, dim: true);

        DialogUi.AddLabel(this, "目标文件夹", 22, 80);
        _destination = DialogUi.AddTextBox(this, "txtDest", 22, 102, 480);
        _destination.Text = DefaultDestination(archivePath, settings);
        var browse = DialogUi.AddButton(this, "btnBrowseDest", "浏览…", 514, 100, 104);
        browse.Click += (_, _) => DialogUi.BrowseFolder(this, _destination, "选择解压目标文件夹");

        DialogUi.AddLabel(this, "已存在同名文件时", 22, 144);
        _conflict = Theme.MakeCombo("cmbConflict",
            new[] { "覆盖已有文件", "跳过已有文件", "自动重命名（添加序号）" }, 210);
        _conflict.Location = new Point(150, 140);
        _conflict.SelectedIndex = settings.GetOverwritePolicy() switch
        {
            OverwritePolicy.Skip => 1,
            OverwritePolicy.Rename => 2,
            _ => 0,
        };
        Controls.Add(_conflict);

        var passwordLabel = DialogUi.AddLabel(this, encrypted ? "密码" : "密码（未加密可留空）", 22, 186);
        if (!encrypted) passwordLabel.ForeColor = Theme.TextDim;
        _password = DialogUi.AddTextBox(this, "txtPassword", 22, 208, 250);
        _password.UseSystemPasswordChar = true;
        if (encrypted && !string.IsNullOrEmpty(knownPassword)) _password.Text = knownPassword;
        if (!encrypted)
        {
            // 不用 Enabled=false（会变成系统浅灰），改为只读并压暗文字
            _password.ReadOnly = true;
            _password.ForeColor = Theme.TextFaint;
        }

        var show = Theme.MakeCheckBox("chkShowPassword", "显示密码", 120);
        show.Location = new Point(284, 206);
        show.CheckedChanged += (_, _) => _password.UseSystemPasswordChar = !show.Checked;
        Controls.Add(show);

        _preservePaths = Theme.MakeCheckBox("chkPreservePaths", "保留归档内的目录结构", 300);
        _preservePaths.Location = new Point(22, 246);
        _preservePaths.Checked = true;
        Controls.Add(_preservePaths);

        _openAfter = Theme.MakeCheckBox("chkOpenAfter", "完成后打开文件夹", 220);
        _openAfter.Location = new Point(340, 246);
        _openAfter.Checked = settings.OpenFolderAfterExtract;
        Controls.Add(_openAfter);

        var ok = DialogUi.AddButton(this, "btnExtract", "开始解压", 408, 302, 110, primary: true);
        ok.Click += (_, _) => ConfirmAndClose();
        AcceptButton = ok;

        var cancel = DialogUi.AddButton(this, "btnCancel", "取消", 528, 302, 90);
        cancel.DialogResult = DialogResult.Cancel;
        CancelButton = cancel;

        Shown += (_, _) => { _destination.Focus(); _destination.SelectAll(); };
    }

    private void ConfirmAndClose()
    {
        var destination = _destination.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(destination))
        {
            MsgBox.Warn(this, "请选择解压目标文件夹。", "缺少目标");
            _destination.Focus();
            return;
        }
        try
        {
            destination = Path.GetFullPath(destination);
        }
        catch (Exception ex)
        {
            MsgBox.Warn(this, "目标路径无效：" + ex.Message, "路径无效");
            return;
        }

        _result = new ExtractResultOptions
        {
            Destination = destination,
            Policy = _conflict.SelectedIndex switch
            {
                1 => OverwritePolicy.Skip,
                2 => OverwritePolicy.Rename,
                _ => OverwritePolicy.Overwrite,
            },
            Password = string.IsNullOrEmpty(_password.Text) ? null : _password.Text,
            OpenAfter = _openAfter.Checked,
            PreservePaths = _preservePaths.Checked,
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    public static string DefaultDestination(string archivePath, AppSettings settings)
    {
        var folderName = StripArchiveExtensions(Path.GetFileName(archivePath));
        if (string.IsNullOrWhiteSpace(folderName)) folderName = "解压结果";
        var root = string.IsNullOrWhiteSpace(settings.DefaultExtractDir)
            ? Path.GetDirectoryName(archivePath) ?? Environment.CurrentDirectory
            : Environment.ExpandEnvironmentVariables(settings.DefaultExtractDir.Trim());
        try { return Path.Combine(Path.GetFullPath(root), folderName); }
        catch { return Path.Combine(Path.GetDirectoryName(archivePath) ?? Environment.CurrentDirectory, folderName); }
    }

    public static string StripArchiveExtensions(string fileName)
    {
        string[] known = { ".7z", ".zip", ".rar", ".tar", ".tgz", ".gz", ".bz2", ".tbz2", ".xz", ".txz", ".iso", ".wim", ".cab", ".001" };
        var name = fileName;
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var ext in known)
            {
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && name.Length > ext.Length)
                {
                    name = name[..^ext.Length];
                    changed = true;
                    break;
                }
            }
        }
        return name;
    }
}

// ————————————————————————————————————————————————————————————————
// 新建归档
// ————————————————————————————————————————————————————————————————
internal sealed class CreateDialog : ThemedForm
{
    private static readonly (string Label, ArchiveFormat Format, string Extension)[] Formats =
    {
        ("7Z 归档 (*.7z)", ArchiveFormat.SevenZip, ".7z"),
        ("ZIP 归档 (*.zip)", ArchiveFormat.Zip, ".zip"),
        ("TAR 归档 (*.tar)", ArchiveFormat.Tar, ".tar"),
        ("GZIP 压缩 (*.gz)", ArchiveFormat.GZip, ".gz"),
        ("BZIP2 压缩 (*.bz2)", ArchiveFormat.BZip2, ".bz2"),
        ("XZ 压缩 (*.xz)", ArchiveFormat.Xz, ".xz"),
    };

    private static readonly (string Label, int Level)[] Levels =
    {
        ("仅存储 (0)", 0),
        ("最快 (1)", 1),
        ("标准 (5)", 5),
        ("最大 (7)", 7),
        ("极限 (9)", 9),
    };

    private static readonly (string Label, long? Bytes)[] Volumes =
    {
        ("不分卷", null),
        ("10 MB", 10L * 1024 * 1024),
        ("50 MB", 50L * 1024 * 1024),
        ("100 MB", 100L * 1024 * 1024),
        ("500 MB", 500L * 1024 * 1024),
        ("1 GB", 1L * 1024 * 1024 * 1024),
        ("2 GB", 2L * 1024 * 1024 * 1024),
        ("4 GB", 4L * 1024 * 1024 * 1024),
    };

    private readonly TextBox _path;
    private readonly ComboBox _format;
    private readonly ComboBox _level;
    private readonly ComboBox _volume;
    private readonly TextBox _password;
    private readonly CheckBox _showPassword;
    private readonly CheckBox _encryptNames;
    private readonly ListBox _sources;
    private readonly Label _formatHint;
    private readonly Button _addFolder;
    private readonly AppSettings _settings;
    private string _autoPath = "";
    private CreateOptions? _result;

    public static CreateOptions? Show(IWin32Window? owner, AppSettings settings, IEnumerable<string>? initialSources)
    {
        using var dialog = new CreateDialog(settings, initialSources);
        var result = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return result == DialogResult.OK ? dialog._result : null;
    }

    private CreateDialog(AppSettings settings, IEnumerable<string>? initialSources)
    {
        _settings = settings;
        Text = "新建归档";
        ClientSize = new Size(720, 552);
        ShowIcon = true;

        DialogUi.AddLabel(this, "归档文件", 22, 18);
        _path = DialogUi.AddTextBox(this, "txtPath", 22, 42, 540);
        var browse = DialogUi.AddButton(this, "btnBrowsePath", "浏览…", 574, 40, 124);
        browse.Click += (_, _) => BrowseSavePath();

        DialogUi.AddLabel(this, "格式", 22, 84);
        _format = Theme.MakeCombo("cmbFormat", Formats.Select(f => f.Label), 156);
        _format.Location = new Point(66, 80);
        Controls.Add(_format);
        _format.SelectedIndexChanged += (_, _) => OnFormatChanged();

        DialogUi.AddLabel(this, "压缩级别", 242, 84);
        _level = Theme.MakeCombo("cmbLevel", Levels.Select(l => l.Label), 128);
        _level.Location = new Point(322, 80);
        _level.SelectedIndex = 2;
        Controls.Add(_level);

        DialogUi.AddLabel(this, "分卷大小", 472, 84);
        _volume = Theme.MakeCombo("cmbVolume", Volumes.Select(v => v.Label), 112);
        _volume.Location = new Point(556, 80);
        Controls.Add(_volume);

        DialogUi.AddLabel(this, "密码", 22, 122);
        _password = DialogUi.AddTextBox(this, "txtPassword", 66, 118, 200);
        _password.UseSystemPasswordChar = true;
        _password.TextChanged += (_, _) => UpdateFormatUi();
        _showPassword = Theme.MakeCheckBox("chkShowPassword", "显示", 100);
        _showPassword.Location = new Point(276, 116);
        _showPassword.CheckedChanged += (_, _) => _password.UseSystemPasswordChar = !_showPassword.Checked;
        Controls.Add(_showPassword);

        _encryptNames = Theme.MakeCheckBox("chkEncryptNames", "连文件名一起加密（仅 7z）", 260);
        _encryptNames.Location = new Point(390, 116);
        Controls.Add(_encryptNames);

        _formatHint = DialogUi.AddLabel(this,
            "提示：GZ / BZ2 / XZ 只支持单个文件；TAR / GZ / BZ2 / XZ 不支持密码；分卷仅 7Z / ZIP。",
            22, 154, dim: true);
        _formatHint.MaximumSize = new Size(676, 0);

        DialogUi.AddLabel(this, "要压缩的文件和文件夹", 22, 186, bold: true);
        _addFolder = DialogUi.AddButton(this, "btnAddFolder", "添加文件夹", 526, 180, 106);
        _addFolder.Click += (_, _) => AddFolder();
        var addFiles = DialogUi.AddButton(this, "btnAddFiles", "添加文件", 412, 180, 104);
        addFiles.Click += (_, _) => AddFiles();
        var remove = DialogUi.AddButton(this, "btnRemove", "移除", 642, 180, 56);
        remove.Click += (_, _) => RemoveSelected();

        _sources = new ListBox
        {
            Name = "lstSources",
            Location = new Point(22, 216),
            Size = new Size(676, 240),
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.Ui(),
            IntegralHeight = false,
            HorizontalScrollbar = true,
            SelectionMode = SelectionMode.MultiExtended,
        };
        Controls.Add(_sources);

        DialogUi.AddLabel(this, "压缩完成后会在主窗口自动打开新归档。", 22, 466, dim: true);

        var ok = DialogUi.AddButton(this, "btnCreateArchive", "创建", 490, 502, 110, primary: true);
        ok.Click += (_, _) => ConfirmAndClose();
        AcceptButton = ok;

        var cancel = DialogUi.AddButton(this, "btnCancel", "取消", 610, 502, 88);
        cancel.DialogResult = DialogResult.Cancel;
        CancelButton = cancel;

        if (initialSources != null) AddSources(initialSources);

        if (_sources.Items.Count == 0)
        {
            var defaultDir = string.IsNullOrWhiteSpace(_settings.DefaultExtractDir)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : _settings.DefaultExtractDir;
            _autoPath = Path.Combine(defaultDir, "新建归档" + Formats[0].Extension);
            _path.Text = _autoPath;
        }

        UpdateFormatUi();
    }

    private void BrowseSavePath()
    {
        var format = Formats[Math.Max(0, _format.SelectedIndex)];
        using var dialog = new SaveFileDialog
        {
            Title = "新建归档",
            Filter = $"{format.Label}|*{format.Extension}|所有文件 (*.*)|*.*",
            FileName = Path.GetFileName(_path.Text),
            InitialDirectory = SafeDirectory(_path.Text),
            OverwritePrompt = false,
            AddExtension = true,
            DefaultExt = format.Extension,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _path.Text = dialog.FileName;
            _autoPath = "";
        }
    }

    private static string SafeDirectory(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            return Directory.Exists(dir) ? dir! : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
        catch { return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }
    }

    private void AddFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择要压缩的文件",
            Multiselect = true,
            Filter = "所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddSources(dialog.FileNames);
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择要压缩的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddSources(new[] { dialog.SelectedPath });
    }

    private void AddSources(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string full;
            try { full = Path.GetFullPath(path.Trim().Trim('"')); }
            catch { continue; }
            if (!File.Exists(full) && !Directory.Exists(full)) continue;
            if (!_sources.Items.Cast<string>().Any(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase)))
                _sources.Items.Add(full);
        }
        UpdateSuggestedPath();
        UpdateFormatUi();
    }

    private void RemoveSelected()
    {
        var selected = _sources.SelectedItems.Cast<string>().ToList();
        foreach (var item in selected) _sources.Items.Remove(item);
        UpdateSuggestedPath();
        UpdateFormatUi();
    }

    private void UpdateSuggestedPath()
    {
        var ext = Formats[Math.Max(0, _format.SelectedIndex)].Extension;
        string stem;
        string? dir = null;
        if (_sources.Items.Count == 1)
        {
            var source = (string)_sources.Items[0]!;
            stem = Directory.Exists(source)
                ? new DirectoryInfo(Path.TrimEndingDirectorySeparator(source)).Name
                : Path.GetFileNameWithoutExtension(source);
            if (string.IsNullOrWhiteSpace(stem)) stem = "归档";
            dir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(source));
        }
        else
        {
            stem = "新建归档";
            if (_sources.Items.Count > 1)
            {
                var first = (string)_sources.Items[0]!;
                dir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(first));
            }
        }

        if (string.IsNullOrWhiteSpace(_path.Text) ||
            (!string.IsNullOrEmpty(_autoPath) && string.Equals(_path.Text, _autoPath, StringComparison.OrdinalIgnoreCase)))
        {
            var baseDir = dir ?? (string.IsNullOrWhiteSpace(_settings.DefaultExtractDir)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : _settings.DefaultExtractDir);
            _autoPath = Path.Combine(baseDir, stem + ext);
            _path.Text = _autoPath;
            _path.SelectionStart = _path.Text.Length;
        }
    }

    private ArchiveFormat SelectedFormat => Formats[Math.Max(0, _format.SelectedIndex)].Format;

    private static bool IsSingleStream(ArchiveFormat format)
        => format is ArchiveFormat.GZip or ArchiveFormat.BZip2 or ArchiveFormat.Xz;

    private void OnFormatChanged()
    {
        var chosen = Formats[Math.Max(0, _format.SelectedIndex)];
        var current = _path.Text.Trim();
        if (!string.IsNullOrEmpty(current))
        {
            var ext = Path.GetExtension(current);
            if (string.IsNullOrEmpty(ext) || Formats.Any(f => string.Equals(f.Extension, ext, StringComparison.OrdinalIgnoreCase)))
            {
                var newPath = Path.ChangeExtension(current, chosen.Extension);
                _path.Text = newPath;
                if (string.Equals(current, _autoPath, StringComparison.OrdinalIgnoreCase)) _autoPath = newPath;
            }
        }
        UpdateFormatUi();
    }

    private void UpdateFormatUi()
    {
        var format = SelectedFormat;
        bool supportsPassword = format is ArchiveFormat.SevenZip or ArchiveFormat.Zip;
        bool supportsVolumes = format is ArchiveFormat.SevenZip or ArchiveFormat.Zip;

        _password.Enabled = true;
        _password.ReadOnly = !supportsPassword;
        _password.ForeColor = supportsPassword ? Theme.Text : Theme.TextFaint;
        _showPassword.Enabled = supportsPassword;
        _volume.Enabled = supportsVolumes;
        _encryptNames.Enabled = format == ArchiveFormat.SevenZip && _password.Text.Length > 0;
        if (!_encryptNames.Enabled) _encryptNames.Checked = false;
        _addFolder.Enabled = !IsSingleStream(format);
        _formatHint.Text = IsSingleStream(format)
            ? "当前格式为单文件压缩流：只能添加 1 个文件，不能添加文件夹、不支持密码与分卷。"
            : "提示：TAR / GZ / BZ2 / XZ 不支持密码；分卷仅 7Z / ZIP；连文件名加密仅 7Z。";
    }

    private void ConfirmAndClose()
    {
        var path = _path.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path))
        {
            MsgBox.Warn(this, "请先指定要创建的归档文件路径。", "缺少路径");
            _path.Focus();
            return;
        }

        var format = SelectedFormat;
        var extension = Formats[Math.Max(0, _format.SelectedIndex)].Extension;
        if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            path = Path.ChangeExtension(path, extension);
        try { path = Path.GetFullPath(path); }
        catch (Exception ex)
        {
            MsgBox.Warn(this, "归档路径无效：" + ex.Message, "路径无效");
            return;
        }

        var sources = _sources.Items.Cast<string>().ToList();
        if (sources.Count == 0)
        {
            MsgBox.Warn(this, "请至少添加一个文件或文件夹。", "没有源文件");
            return;
        }
        if (IsSingleStream(format))
        {
            if (sources.Count != 1 || Directory.Exists(sources[0]))
            {
                MsgBox.Warn(this, "GZ / BZ2 / XZ 是单文件压缩流，只能压缩 1 个文件。\n" +
                                  "要压缩文件夹或多个文件，请改用 7Z / ZIP / TAR。", "格式不支持");
                return;
            }
        }

        if (File.Exists(path))
        {
            if (!MsgBox.Confirm(this, "目标归档已存在：\n" + path + "\n\n继续将覆盖重建该归档（先写临时文件，成功后替换）。",
                    "覆盖已有归档")) return;
        }

        string? password = _password.Enabled && _password.Text.Length > 0 ? _password.Text : null;
        var level = Levels[Math.Max(0, _level.SelectedIndex)].Level;
        long? volume = null;
        if (_volume.Enabled) volume = Volumes[Math.Max(0, _volume.SelectedIndex)].Bytes;

        _result = new CreateOptions
        {
            ArchivePath = path,
            Format = format,
            Level = level,
            Password = password,
            EncryptHeaders = format == ArchiveFormat.SevenZip && password != null && _encryptNames.Checked,
            VolumeSize = volume,
            Sources = sources,
            BaseDirectory = null,
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}

// ————————————————————————————————————————————————————————————————
// 进度对话框（百分比 / 当前文件 / 速度 / 剩余时间 / 真取消）
// ————————————————————————————————————————————————————————————————
internal sealed class ProgressDialog : ThemedForm
{
    private readonly Func<IProgress<ProgressInfo>, CancellationToken, Task> _work;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _title;
    private readonly Label _phase;
    private readonly Label _file;
    private readonly Label _percent;
    private readonly Label _speed;
    private readonly Label _eta;
    private readonly FlatProgressBar _bar;
    private readonly Button _cancel;
    private Exception? _error;
    private bool _canceled;
    private bool _running;
    private bool _finished;
    private DateTime _lastUpdate = DateTime.MinValue;
    private readonly DateTime _started = DateTime.UtcNow;

    internal Exception? Failure => _error;
    internal bool Canceled => _canceled;

    public static void Run(IWin32Window? owner, string title,
        Func<IProgress<ProgressInfo>, CancellationToken, Task> work)
    {
        using var dialog = new ProgressDialog(title, work);
        var result = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        _ = result;
        var error = dialog.Failure;
        var canceled = dialog.Canceled;
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        if (canceled) throw new OperationCanceledException("用户取消了操作。");
    }

    private ProgressDialog(string title, Func<IProgress<ProgressInfo>, CancellationToken, Task> work)
    {
        _work = work;
        _title = title;
        Text = title;
        ClientSize = new Size(580, 240);
        ShowIcon = true;
        ControlBox = true;

        var titleLabel = new Label
        {
            Text = title,
            Font = Theme.Ui(1.5f, FontStyle.Bold),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(22, 18),
        };
        Controls.Add(titleLabel);

        _phase = new Label
        {
            Text = "准备中…",
            Font = Theme.Ui(),
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(22, 50),
        };
        Controls.Add(_phase);

        _file = new Label
        {
            Name = "lblCurrentFile",
            Text = "—",
            Font = Theme.Ui(),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            AutoSize = false,
            AutoEllipsis = true,
            Bounds = new Rectangle(22, 76, 536, 22),
        };
        Controls.Add(_file);

        _bar = new FlatProgressBar
        {
            Name = "progressBar",
            Location = new Point(22, 108),
            Size = new Size(536, 12),
        };
        Controls.Add(_bar);

        _percent = new Label
        {
            Name = "lblPercent",
            Text = "0%",
            Font = Theme.Ui(1.5f, FontStyle.Bold),
            ForeColor = Theme.Accent,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(22, 132),
        };
        Controls.Add(_percent);

        _speed = new Label
        {
            Text = "速度 —",
            Font = Theme.Ui(),
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(110, 134),
        };
        Controls.Add(_speed);

        _eta = new Label
        {
            Text = "剩余 —",
            Font = Theme.Ui(),
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(330, 134),
        };
        Controls.Add(_eta);

        _cancel = DialogUi.AddButton(this, "btnCancel", "取消", 454, 186, 104);
        _cancel.Click += (_, _) => RequestCancel();

        Shown += (_, _) => _ = RunWorkAsync();
        FormClosing += OnFormClosing;
    }

    private async Task RunWorkAsync()
    {
        _running = true;
        try
        {
            var progress = new Progress<ProgressInfo>(OnProgress);
            await _work(progress, _cts.Token).ConfigureAwait(true);
            _finished = true;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException)
        {
            _canceled = true;
            _finished = true;
            DialogResult = DialogResult.Cancel;
            Close();
        }
        catch (Exception ex)
        {
            _error = ex;
            _finished = true;
            DialogResult = DialogResult.Abort;
            Close();
        }
        finally
        {
            _running = false;
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_finished || !_running) return;
        if (e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.None)
        {
            e.Cancel = true;
            RequestCancel();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            RequestCancel();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void RequestCancel()
    {
        if (!_running || _cts.IsCancellationRequested) return;
        _cancel.Enabled = false;
        _cancel.Text = "正在取消…";
        _phase.Text = "正在取消，请稍候…";
        _bar.Indeterminate = true;
        try { _cts.Cancel(); } catch { /* 已释放 */ }
    }

    private void OnProgress(ProgressInfo info)
    {
        if (_finished) return;
        var now = DateTime.UtcNow;
        if (info.Percent < 100 && (now - _lastUpdate).TotalMilliseconds < 33) return;
        _lastUpdate = now;

        if (!string.IsNullOrWhiteSpace(info.Phase)) _phase.Text = info.Phase;
        if (info.CurrentItem != null) _file.Text = UiFormat.Shorten(info.CurrentItem, 90);

        if (info.Percent >= 0 && info.Percent <= 100)
        {
            _bar.Indeterminate = false;
            _bar.Value = info.Percent;
            _percent.Text = $"{info.Percent:0}%";
        }
        else
        {
            _bar.Indeterminate = true;
            _percent.Text = "处理中…";
        }

        if (info.SpeedBytesPerSecond > 0) _speed.Text = "速度 " + FormatUtil.Speed(info.SpeedBytesPerSecond);
        else if (info.BytesDone > 0)
        {
            var elapsed = Math.Max(0.2, (DateTime.UtcNow - _started).TotalSeconds);
            _speed.Text = "速度 " + FormatUtil.Speed(info.BytesDone / elapsed);
        }

        if (info.Percent >= 0 && info.Percent < 100 && info.BytesTotal > 0 && info.BytesDone > 0)
        {
            var elapsed = (DateTime.UtcNow - _started).TotalSeconds;
            if (elapsed > 0.3)
            {
                var rate = info.BytesDone / elapsed;
                var remain = (info.BytesTotal - info.BytesDone) / Math.Max(1, rate);
                _eta.Text = rate > 1 ? "剩余 " + FormatSpan(TimeSpan.FromSeconds(remain)) : "剩余 —";
            }
        }
        else if (info.Percent >= 100)
        {
            _eta.Text = "剩余 0 秒";
        }
    }

    private static string FormatSpan(TimeSpan span)
    {
        if (span.TotalSeconds < 1) return "不到 1 秒";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
        if (span.TotalMinutes >= 1) return $"{span.Minutes} 分 {span.Seconds} 秒";
        return $"{span.TotalSeconds:0} 秒";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}

// ————————————————————————————————————————————————————————————————
// 设置
// ————————————————————————————————————————————————————————————————
internal sealed class SettingsDialog : ThemedForm
{
    private readonly TextBox _defaultDir;
    private readonly ComboBox _conflict;
    private readonly CheckBox _openAfter;
    private readonly TextBox _tempDir;
    private readonly CheckBox _cleanTemp;
    private readonly NumericUpDown _fontSize;
    private readonly ComboBox _level;
    private readonly AppSettings _settings;
    private AppSettings? _result;

    public static AppSettings? Show(IWin32Window? owner, AppSettings current)
    {
        using var dialog = new SettingsDialog(current);
        var result = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return result == DialogResult.OK ? dialog._result : null;
    }

    private SettingsDialog(AppSettings current)
    {
        _settings = current.Clone();
        Text = "设置";
        ClientSize = new Size(680, 470);
        ShowIcon = true;

        DialogUi.AddLabel(this, "设置", 22, 16, bold: true, delta: 2f);
        DialogUi.AddLabel(this, "全部设置只保存在本机 %APPDATA%\\REAArchive\\settings.json", 22, 46, dim: true);

        DialogUi.AddLabel(this, "默认解压目录（留空 = 归档所在目录）", 22, 84);
        _defaultDir = DialogUi.AddTextBox(this, "txtDefaultDir", 22, 108, 500);
        _defaultDir.Text = _settings.DefaultExtractDir;
        var browseDefault = DialogUi.AddButton(this, "btnBrowseDefault", "浏览…", 534, 106, 104);
        browseDefault.Click += (_, _) => DialogUi.BrowseFolder(this, _defaultDir, "选择默认解压目录");

        DialogUi.AddLabel(this, "已存在同名文件时", 22, 150);
        _conflict = Theme.MakeCombo("cmbConflict", new[] { "覆盖已有文件", "跳过已有文件", "自动重命名" }, 200);
        _conflict.Location = new Point(150, 146);
        _conflict.SelectedIndex = _settings.GetOverwritePolicy() switch
        {
            OverwritePolicy.Skip => 1,
            OverwritePolicy.Rename => 2,
            _ => 0,
        };
        Controls.Add(_conflict);

        _openAfter = Theme.MakeCheckBox("chkOpenAfter", "解压完成后自动打开目标文件夹", 360);
        _openAfter.Location = new Point(22, 184);
        _openAfter.Checked = _settings.OpenFolderAfterExtract;
        Controls.Add(_openAfter);

        DialogUi.AddLabel(this, "临时目录（预览/编辑中转用）", 22, 224);
        _tempDir = DialogUi.AddTextBox(this, "txtTempDir", 22, 248, 500);
        _tempDir.Text = _settings.TempDir;
        _tempDir.PlaceholderText = _settings.ResolvedTempDir();
        var browseTemp = DialogUi.AddButton(this, "btnBrowseTemp", "浏览…", 534, 246, 104);
        browseTemp.Click += (_, _) => DialogUi.BrowseFolder(this, _tempDir, "选择临时目录");

        _cleanTemp = Theme.MakeCheckBox("chkCleanTemp", "退出时清理本程序创建的临时文件", 400);
        _cleanTemp.Location = new Point(22, 286);
        _cleanTemp.Checked = _settings.CleanTempOnExit;
        Controls.Add(_cleanTemp);

        DialogUi.AddLabel(this, "界面字号", 22, 326);
        _fontSize = new NumericUpDown
        {
            Name = "numFontSize",
            Location = new Point(110, 322),
            Width = 90,
            Minimum = 8,
            Maximum = 14,
            DecimalPlaces = 1,
            Increment = 0.5m,
            Value = (decimal)Math.Clamp(_settings.FontSize, 8f, 14f),
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.Ui(),
        };
        Controls.Add(_fontSize);
        // C12：值一变/输入一变就落盘，避免 NumericUpDown 未失焦、未回车时读不到。
        _fontSize.ValueChanged += (_, _) => SaveFontSettingNow();
        _fontSize.KeyUp += (_, _) => SaveFontSettingNow();
        _fontSize.Leave += (_, _) => SaveFontSettingNow();
        DialogUi.AddLabel(this, "pt（8–14，保存后立即生效）", 212, 326, dim: true);

        DialogUi.AddLabel(this, "默认压缩级别", 22, 360);
        _level = Theme.MakeCombo("cmbCompressLevel",
            new[] { "仅存储 (0)", "最快 (1)", "标准 (5)", "最大 (7)", "极限 (9)" }, 160);
        _level.Location = new Point(150, 356);
        _level.SelectedIndex = _settings.CompressionLevel switch
        {
            0 => 0,
            1 => 1,
            7 => 3,
            9 => 4,
            _ => 2,
        };
        Controls.Add(_level);
        DialogUi.AddLabel(this, "右键“压缩为 7z/ZIP”使用此级别", 326, 360, dim: true);

        var save = DialogUi.AddButton(this, "btnSave", "保存", 442, 414, 104, primary: true);
        save.Click += (_, _) => ConfirmAndClose();
        AcceptButton = save;
        var cancel = DialogUi.AddButton(this, "btnCancel", "取消", 558, 414, 90);
        cancel.DialogResult = DialogResult.Cancel;
        CancelButton = cancel;
    }

    private float CurrentFontSize()
    {
        var text = _fontSize.Text.Trim();
        if (float.TryParse(text, out var parsed))
            return Math.Clamp(parsed, 8f, 14f);
        return Math.Clamp((float)_fontSize.Value, 8f, 14f);
    }

    private void SaveFontSettingNow()
    {
        _settings.FontSize = CurrentFontSize();
        SettingsStore.Save(_settings, out _);
    }

    private void ConfirmAndClose()
    {
        string Expanded(TextBox box)
        {
            var text = box.Text.Trim().Trim('"');
            if (string.IsNullOrEmpty(text)) return "";
            try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(text)); }
            catch (Exception ex)
            {
                MsgBox.Warn(this, "路径无效：" + ex.Message, "路径无效");
                box.Focus();
                return "\0";
            }
        }

        var defaultDir = Expanded(_defaultDir);
        if (defaultDir == "\0") return;
        var tempDir = Expanded(_tempDir);
        if (tempDir == "\0") return;

        _settings.DefaultExtractDir = defaultDir;
        _settings.TempDir = tempDir;
        _settings.SetOverwritePolicy(_conflict.SelectedIndex switch
        {
            1 => OverwritePolicy.Skip,
            2 => OverwritePolicy.Rename,
            _ => OverwritePolicy.Overwrite,
        });
        _settings.OpenFolderAfterExtract = _openAfter.Checked;
        _settings.CleanTempOnExit = _cleanTemp.Checked;
        _settings.FontSize = CurrentFontSize();
        _settings.CompressionLevel = _level.SelectedIndex switch
        {
            0 => 0,
            1 => 1,
            3 => 7,
            4 => 9,
            _ => 5,
        };

        _result = _settings;
        DialogResult = DialogResult.OK;
        Close();
    }
}

// ————————————————————————————————————————————————————————————————
// 关于
// ————————————————————————————————————————————————————————————————
internal sealed class AboutDialog : ThemedForm
{
    public AboutDialog(string engineText)
    {
        Text = "关于 REAArchive";
        ClientSize = new Size(600, 404);
        ShowIcon = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;

        var icon = new PictureBox
        {
            Image = Theme.AppIcon.ToBitmap(),
            Size = new Size(52, 52),
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(24, 22),
            BackColor = Color.Transparent,
        };
        Controls.Add(icon);

        var title = new Label
        {
            Text = "REAArchive",
            Font = Theme.Ui(7f, FontStyle.Bold),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(92, 20),
        };
        Controls.Add(title);

        var subtitle = new Label
        {
            Text = "纯净压缩 · 版本 1.1.0",
            Font = Theme.Ui(),
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(94, 54),
        };
        Controls.Add(subtitle);

        DialogUi.AddHLine(this, 24, 92, 552);

        var info = new Label
        {
            Text = "引擎：" + engineText + "\n运行环境：.NET 9 · Windows Forms · PerMonitorV2 高 DPI\n依赖：零第三方 NuGet，仅使用 Windows 自带组件",
            Font = Theme.Ui(),
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(24, 108),
        };
        Controls.Add(info);

        var declaration = new Panel
        {
            Location = new Point(24, 188),
            Size = new Size(552, 128),
            BackColor = Theme.SurfaceAlt,
        };
        Controls.Add(declaration);
        var accent = new Panel { Location = new Point(0, 0), Size = new Size(3, 128), BackColor = Theme.Accent };
        declaration.Controls.Add(accent);
        var headline = new Label
        {
            Text = "本程序不联网、不检查更新、无广告、无遥测。",
            Font = Theme.Ui(2f, FontStyle.Bold),
            ForeColor = Theme.Accent,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(20, 16),
        };
        declaration.Controls.Add(headline);
        var body = new Label
        {
            Text = "不会在后台访问网络或写入注册表；右键菜单只在您主动点击“安装右键菜单”时\n写入 HKCU，并可一键卸载。\n除您指定的解压/压缩目标外，本程序不修改任何文件。",
            Font = Theme.Ui(),
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(20, 50),
        };
        declaration.Controls.Add(body);

        var license = new Label
        {
            Text = "7-Zip 引擎 © Igor Pavlov，LGPL v2.1+，原样随包分发（Resources\\7z\\License.txt）。",
            Font = Theme.Ui(),
            ForeColor = Theme.TextFaint,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(24, 332),
        };
        Controls.Add(license);

        var ok = DialogUi.AddButton(this, "btnOk", "关闭", 486, 356, 90, primary: true);
        ok.DialogResult = DialogResult.OK;
        AcceptButton = ok;
        CancelButton = ok;
    }
}

// ————————————————————————————————————————————————————————————————
// 条目信息
// ————————————————————————————————————————————————————————————————
internal static class EntryInfoDialog
{
    public static void Show(IWin32Window? owner, string archivePath, ArchiveFormat format, string engineText,
        IReadOnlyList<ArchiveEntryInfo> entries)
    {
        using var dialog = new InfoForm(archivePath, format, engineText, entries);
        if (owner != null) dialog.ShowDialog(owner);
        else dialog.ShowDialog();
    }

    private sealed class InfoForm : ThemedForm
    {
        public InfoForm(string archivePath, ArchiveFormat format, string engineText, IReadOnlyList<ArchiveEntryInfo> entries)
        {
            Text = "归档信息";
            ClientSize = new Size(700, 520);
            ShowIcon = true;

            DialogUi.AddLabel(this, "归档信息", 22, 16, bold: true, delta: 1.5f);

            var totalSize = entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
            var text = new System.Text.StringBuilder();
            text.AppendLine("归档：" + archivePath);
            text.AppendLine("格式：" + FormatUtil.FormatName(format) + "    引擎：" + engineText);
            text.AppendLine($"条目：{entries.Count} 项（文件 {entries.Count(e => !e.IsDirectory)}，目录 {entries.Count(e => e.IsDirectory)}）");
            text.AppendLine("总大小：" + FormatUtil.Size(totalSize));
            text.AppendLine(new string('─', 66));

            int shown = 0;
            foreach (var entry in entries)
            {
                if (shown >= 20)
                {
                    text.AppendLine($"…… 还有 {entries.Count - shown} 项未显示");
                    break;
                }
                text.AppendLine("路径：" + entry.Path);
                text.AppendLine("类型：" + (entry.IsDirectory ? "文件夹" : "文件") +
                                (string.IsNullOrEmpty(entry.Method) ? "" : "    方法：" + entry.Method) +
                                (entry.Encrypted ? "    [加密]" : ""));
                if (!entry.IsDirectory)
                {
                    text.AppendLine("大小：" + FormatUtil.Size(entry.Size) +
                                    "    压缩后：" + FormatUtil.Size(entry.CompressedSize));
                }
                text.AppendLine("修改时间：" + UiFormat.When(entry.Modified) + "    CRC：" + (entry.Crc ?? "—"));
                text.AppendLine();
                shown++;
            }

            var box = new TextBox
            {
                Name = "txtInfo",
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = Theme.Mono(),
                BackColor = Theme.SurfaceAlt,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Bounds = new Rectangle(22, 50, 656, 408),
                Text = text.ToString().Replace("\n", "\r\n"),
            };
            Controls.Add(box);

            var ok = DialogUi.AddButton(this, "btnOk", "关闭", 588, 472, 90, primary: true);
            ok.DialogResult = DialogResult.OK;
            AcceptButton = ok;
            CancelButton = ok;

            box.Select(0, 0);
        }
    }
}
