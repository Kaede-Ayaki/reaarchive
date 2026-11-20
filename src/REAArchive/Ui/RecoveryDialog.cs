// RecoveryDialog.cs — 密码破译对话框：字典 / 掩码 / 增量三种策略 + 实时进度 + 结果。
// 设计约束：不联网、不写日志、密码只在内存与本窗口内出现，关闭即丢弃。
using System.Drawing.Drawing2D;
using REAArchive.Archive;

namespace REAArchive.Ui;

internal sealed class RecoveryDialog : ThemedForm
{
    private readonly string _archivePath;

    private readonly ComboBox _cboStrategy;
    private readonly Panel _strategyHost;
    private readonly Panel _paneDictionary;
    private readonly Panel _paneMask;
    private readonly Panel _paneIncremental;
    private readonly Label _lblVerifier;
    private readonly Label _lblEstimate;
    private readonly TextBox _txtPrefix;
    private readonly TextBox _txtMaxAttempts;

    private readonly ListBox _lstWordlists;
    private readonly Label _lblWordlistEmpty;
    private readonly CheckBox _chkCapitalize;
    private readonly CheckBox _chkUpper;
    private readonly CheckBox _chkAppendDigits;
    private readonly CheckBox _chkAppendYear;

    private readonly ComboBox _cboMaskPreset;
    private readonly TextBox _txtMask;

    private readonly TextBox _txtCharset;
    private readonly NumericUpDown _numMin;
    private readonly NumericUpDown _numMax;

    private readonly FlatProgressBar _bar;
    private readonly Label _lblTried;
    private readonly Label _lblSpeed;
    private readonly Label _lblEta;
    private readonly Label _lblCurrent;
    private readonly Label _lblResult;
    private readonly Panel _resultBar;

    private readonly Button _btnStart;
    private readonly Button _btnStop;
    private readonly Button _btnCopy;

    private CancellationTokenSource? _cts;
    private readonly PasswordRecoveryService _recovery = new();
    private bool _running;
    private string? _foundPassword;

    /// <summary>找到的密码（未找到为 null）。主窗口据此决定是否用它继续解压。</summary>
    public string? FoundPassword => _foundPassword;

    public RecoveryDialog(string archivePath, ArchiveFormat format, IArchiveService? service)
    {
        _archivePath = archivePath;

        Text = "密码破译";
        ClientSize = new Size(768, 636);
        ShowIcon = true;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;

        // —— 头部 ——
        DialogUi.AddLabel(this, "找回归档密码", 20, 16, bold: true, delta: 3f);
        var pathLabel = DialogUi.AddLabel(this, UiFormat.Shorten(archivePath, 92), 20, 46, dim: true);
        pathLabel.MaximumSize = new Size(720, 0);
        DialogUi.AddHLine(this, 20, 74, 728);

        // —— 策略 ——
        DialogUi.AddLabel(this, "策略", 20, 88, dim: true);
        _cboStrategy = Theme.MakeCombo("cboStrategy",
            new[] { "字典攻击（词表）", "掩码（已知字符类型）", "增量暴力（逐位穷举）" }, 210);
        _cboStrategy.Location = new Point(70, 84);
        _cboStrategy.SelectedIndexChanged += (_, _) => SyncStrategyPane();
        Controls.Add(_cboStrategy);

        _lblVerifier = new Label
        {
            Name = "lblVerifier",
            AutoSize = false,
            Bounds = new Rectangle(300, 84, 448, 26),
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            Font = Theme.Ui(-0.5f),
        };
        Controls.Add(_lblVerifier);

        // —— 策略面板容器 ——
        _strategyHost = new Panel
        {
            Bounds = new Rectangle(20, 118, 728, 196),
            BackColor = Theme.Background,
        };
        Controls.Add(_strategyHost);

        // 字典
        _paneDictionary = NewPane();
        _lstWordlists = new ListBox
        {
            Name = "lstWordlists",
            Dock = DockStyle.Fill,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None,
            Font = Theme.Ui(),
            IntegralHeight = false,
        };
        var listFrame = new Panel
        {
            Bounds = new Rectangle(0, 26, 546, 132),
            BackColor = Theme.Border,
            Padding = new Padding(1),
        };
        listFrame.Controls.Add(_lstWordlists);
        DialogUi.AddLabel(_paneDictionary, "词表文件（每行一个候选密码，UTF-8 / GB18030）", 0, 2, dim: true, delta: -1f);
        _paneDictionary.Controls.Add(listFrame);

        _lblWordlistEmpty = new Label
        {
            Name = "lblWordlistEmpty",
            Bounds = new Rectangle(1, 1, 544, 130),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.TextFaint,
            BackColor = Theme.SurfaceAlt,
            Font = Theme.Ui(-0.5f),
            Text = "尚未添加词表 —— 点右侧「添加词表…」\n常用字典：rockyou.txt、常见密码 Top1000",
        };
        listFrame.Controls.Add(_lblWordlistEmpty);
        _lblWordlistEmpty.BringToFront();
        SyncWordlistPlaceholder();
        var addWord = DialogUi.AddButton(_paneDictionary, "btnAddWordlist", "添加词表…", 556, 26, 168);
        addWord.Click += (_, _) => AddWordlists();
        var removeWord = DialogUi.AddButton(_paneDictionary, "btnRemoveWordlist", "移除选中", 556, 64, 168);
        removeWord.Click += (_, _) =>
        {
            if (_lstWordlists.SelectedIndex >= 0) _lstWordlists.Items.RemoveAt(_lstWordlists.SelectedIndex);
            SyncWordlistPlaceholder();
            UpdateEstimate();
        };
        _chkCapitalize = Theme.MakeCheckBox("chkCapitalize", "首字母大写", 150);
        _chkCapitalize.Location = new Point(0, 164);
        _chkCapitalize.CheckedChanged += (_, _) => UpdateEstimate();
        _paneDictionary.Controls.Add(_chkCapitalize);
        _chkUpper = Theme.MakeCheckBox("chkUpper", "全大写", 120);
        _chkUpper.Location = new Point(156, 164);
        _chkUpper.CheckedChanged += (_, _) => UpdateEstimate();
        _paneDictionary.Controls.Add(_chkUpper);
        _chkAppendDigits = Theme.MakeCheckBox("chkAppendDigits", "追加数字 0-9", 150);
        _chkAppendDigits.Location = new Point(282, 164);
        _chkAppendDigits.CheckedChanged += (_, _) => UpdateEstimate();
        _paneDictionary.Controls.Add(_chkAppendDigits);
        _chkAppendYear = Theme.MakeCheckBox("chkAppendYear", "追加年份 1990-2030", 180);
        _chkAppendYear.Location = new Point(430, 164);
        _chkAppendYear.CheckedChanged += (_, _) => UpdateEstimate();
        _paneDictionary.Controls.Add(_chkAppendYear);
        _strategyHost.Controls.Add(_paneDictionary);

        // 掩码
        _paneMask = NewPane();
        DialogUi.AddLabel(_paneMask, "掩码", 0, 4, dim: true);
        _txtMask = Theme.MakeTextBox("txtMask", 380);
        _txtMask.Location = new Point(44, 0);
        _txtMask.Text = "?d?d?d?d";
        _txtMask.TextChanged += (_, _) => UpdateEstimate();
        _paneMask.Controls.Add(_txtMask);

        _cboMaskPreset = Theme.MakeCombo("cboMaskPreset", new[]
        {
            "自定义",
            "纯数字 4 位    ?d?d?d?d",
            "纯数字 6 位    ?d?d?d?d?d?d",
            "纯数字 8 位    ?d?d?d?d?d?d?d?d",
            "小写 4 位      ?l?l?l?l",
            "小写 6 位      ?l?l?l?l?l?l",
            "小写+数字 6 位 ?l?l?l?l?d?d",
            "大小写+数字 8 位 ?u?l?l?l?l?d?d?d",
        }, 300);
        _cboMaskPreset.Location = new Point(438, 0);
        _cboMaskPreset.SelectedIndexChanged += (_, _) =>
        {
            if (_cboMaskPreset.SelectedIndex <= 0) return;
            var text = _cboMaskPreset.SelectedItem?.ToString() ?? "";
            int at = text.LastIndexOf("    ", StringComparison.Ordinal);
            if (at >= 0) _txtMask.Text = text[(at + 4)..].Trim();
            UpdateEstimate();
        };
        _paneMask.Controls.Add(_cboMaskPreset);

        var maskHelp = DialogUi.AddLabel(_paneMask,
            "?l 小写  ?u 大写  ?d 数字  ?s 符号  ?a 全部可打印  ?b 空格    其余字符按字面量",
            0, 40, dim: true, delta: -1f);
        maskHelp.ForeColor = Theme.TextFaint;
        DialogUi.AddLabel(_paneMask,
            "例：公司名 + 4 位数字 → Abc?d?d?d?d      纯 6 位字母 → ?l?l?l?l?l?l",
            0, 66, dim: true, delta: -1f).ForeColor = Theme.TextFaint;
        _strategyHost.Controls.Add(_paneMask);

        // 增量
        _paneIncremental = NewPane();
        DialogUi.AddLabel(_paneIncremental, "字符集", 0, 4, dim: true);
        _txtCharset = Theme.MakeTextBox("txtCharset", 380);
        _txtCharset.Location = new Point(52, 0);
        _txtCharset.Text = "0123456789";
        _txtCharset.TextChanged += (_, _) => UpdateEstimate();
        _paneIncremental.Controls.Add(_txtCharset);

        DialogUi.AddLabel(_paneIncremental, "最小长度", 448, 4, dim: true, delta: -1f);
        _numMin = new NumericUpDown
        {
            Name = "numMinLength",
            Bounds = new Rectangle(448, 26, 60, 26),
            Minimum = 1,
            Maximum = 20,
            Value = 1,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.Ui(),
        };
        _numMin.ValueChanged += (_, _) => UpdateEstimate();
        _paneIncremental.Controls.Add(_numMin);

        DialogUi.AddLabel(_paneIncremental, "最大长度", 520, 4, dim: true, delta: -1f);
        _numMax = new NumericUpDown
        {
            Name = "numMaxLength",
            Bounds = new Rectangle(520, 26, 60, 26),
            Minimum = 1,
            Maximum = 20,
            Value = 6,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.Ui(),
        };
        _numMax.ValueChanged += (_, _) => UpdateEstimate();
        _paneIncremental.Controls.Add(_numMax);

        DialogUi.AddLabel(_paneIncremental,
            "从小到大逐位穷举。字符集 10 个数字、长度 6 位 = 100 万次候选；再加小写字母就上 20 亿次，先想好再跑。",
            0, 64, dim: true, delta: -1f).ForeColor = Theme.TextFaint;
        _strategyHost.Controls.Add(_paneIncremental);
        _paneMask.Visible = false;
        _paneIncremental.Visible = false;

        // —— 通用选项 ——
        DialogUi.AddLabel(this, "已确定前缀", 20, 326, dim: true, delta: -1f);
        _txtPrefix = DialogUi.AddTextBox(this, "txtPrefix", 92, 322, 150);
        _txtPrefix.PlaceholderText = "可留空";
        _txtPrefix.TextChanged += (_, _) => UpdateEstimate();

        DialogUi.AddLabel(this, "最多尝试", 258, 326, dim: true, delta: -1f);
        _txtMaxAttempts = DialogUi.AddTextBox(this, "txtMaxAttempts", 318, 322, 90);
        _txtMaxAttempts.PlaceholderText = "不限";
        _txtMaxAttempts.TextChanged += (_, _) => UpdateEstimate();

        _lblEstimate = new Label
        {
            Name = "lblEstimate",
            AutoSize = false,
            Bounds = new Rectangle(424, 322, 324, 26),
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            Font = Theme.Ui(-0.5f),
            Text = "",
        };
        Controls.Add(_lblEstimate);

        DialogUi.AddHLine(this, 20, 356, 728);

        // —— 进度 ——
        _bar = new FlatProgressBar { Name = "progressBar", Bounds = new Rectangle(20, 376, 728, 8), Value = 0 };
        Controls.Add(_bar);

        _lblTried = MakeStat("lblTried", 20, 394, 190);
        _lblSpeed = MakeStat("lblSpeed", 210, 394, 170);
        _lblEta = MakeStat("lblEta", 380, 394, 180);
        _lblCurrent = new Label
        {
            Name = "lblCurrentCandidate",
            AutoSize = false,
            Bounds = new Rectangle(560, 394, 188, 26),
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Theme.TextFaint,
            BackColor = Color.Transparent,
            Font = Theme.Mono(-0.5f),
            AutoEllipsis = true,
        };
        Controls.Add(_lblCurrent);

        _resultBar = new Panel
        {
            Name = "resultBar",
            Bounds = new Rectangle(20, 428, 4, 62),
            BackColor = Theme.TextFaint,
        };
        Controls.Add(_resultBar);

        _lblResult = new Label
        {
            Name = "lblRecoveryResult",
            AutoSize = false,
            Bounds = new Rectangle(26, 428, 722, 62),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.TextDim,
            BackColor = Theme.Surface,
            Font = Theme.Ui(),
            Padding = new Padding(14, 0, 14, 0),
            Text = "尚未开始。",
        };
        Controls.Add(_lblResult);

        // —— 按钮 ——
        _btnStart = DialogUi.AddButton(this, "btnStartRecovery", "开始破译", 20, 508, 118, primary: true);
        _btnStart.Click += async (_, _) => await StartAsync();
        _btnStop = DialogUi.AddButton(this, "btnStopRecovery", "停止", 146, 508, 88);
        _btnStop.Enabled = false;
        _btnStop.Click += (_, _) => StopRecovery();
        _btnCopy = DialogUi.AddButton(this, "btnCopyPassword", "复制密码", 566, 508, 96);
        _btnCopy.Enabled = false;
        _btnCopy.Click += (_, _) => CopyPassword();
        var close = DialogUi.AddButton(this, "btnCloseRecovery", "关闭", 670, 508, 78);
        close.Click += (_, _) => Close();

        DialogUi.AddLabel(this, "只在本机尝试组合，不联网猜解。密码只出现在本窗口，关闭后不保留。",
            20, 556, dim: true, delta: -1f).ForeColor = Theme.TextFaint;

        CancelButton = close;
        AcceptButton = _btnStart;

        SyncStrategyPane();
        UpdateVerifierLabel(service);
        UpdateEstimate();
    }

    private Panel NewPane() => new()
    {
        Bounds = new Rectangle(0, 0, 728, 196),
        BackColor = Theme.Background,
    };

    private Label MakeStat(string name, int x, int y, int width)
    {
        var label = new Label
        {
            Name = name,
            AutoSize = false,
            Bounds = new Rectangle(x, y, width, 26),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            Font = Theme.Ui(-0.5f),
            Text = "",
        };
        Controls.Add(label);
        return label;
    }

    private void UpdateVerifierLabel(IArchiveService? service)
    {
        bool native = _recovery.SupportsNativeVerify(_archivePath);
        string fmt = FormatUtil.FormatName(service?.DetectFormat(_archivePath) ?? ArchiveFormat.Unknown);
        _lblVerifier.Text = native
            ? $"{fmt} · 进程内校验（约 50 万次/秒）"
            : $"{fmt} · 子进程校验（约 15 次/秒，慢）";
        _lblVerifier.ForeColor = native ? Theme.Ok : Theme.Warn;
    }

    private void SyncStrategyPane()
    {
        int index = Math.Max(0, _cboStrategy.SelectedIndex);
        _paneDictionary.Visible = index == 0;
        _paneMask.Visible = index == 1;
        _paneIncremental.Visible = index == 2;
        UpdateEstimate();
    }

    private void SetResult(Color color, string text)
    {
        _lblResult.ForeColor = color;
        _lblResult.Text = text;
        _resultBar.BackColor = color;
    }

    private void SyncWordlistPlaceholder()
    {
        if (_lblWordlistEmpty != null)
            _lblWordlistEmpty.Visible = _lstWordlists.Items.Count == 0;
    }

    private void AddWordlists()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择词表文件",
            Filter = "词表 (*.txt;*.lst;*.dic)|*.txt;*.lst;*.dic|所有文件 (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        foreach (var file in dialog.FileNames)
        {
            if (_lstWordlists.Items.Contains(file)) continue;
            _lstWordlists.Items.Add(file);
        }
        SyncWordlistPlaceholder();
        UpdateEstimate();
    }

    private void CopyPassword()
    {
        if (string.IsNullOrEmpty(_foundPassword)) return;
        try
        {
            Clipboard.SetText(_foundPassword);
            SetResult(Theme.Ok, "密码已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            MsgBox.Warn(this, "复制失败：" + ex.Message);
        }
    }

    private RecoveryOptions BuildOptions()
    {
        long maxAttempts = 0;
        _ = long.TryParse(_txtMaxAttempts.Text.Trim(), out maxAttempts);

        var strategy = _cboStrategy.SelectedIndex switch
        {
            1 => RecoveryStrategy.Mask,
            2 => RecoveryStrategy.Incremental,
            _ => RecoveryStrategy.Dictionary,
        };

        var variants = WordVariant.None;
        if (_chkCapitalize.Checked) variants |= WordVariant.Capitalize;
        if (_chkUpper.Checked) variants |= WordVariant.Upper;
        if (_chkAppendDigits.Checked) variants |= WordVariant.AppendDigits;
        if (_chkAppendYear.Checked) variants |= WordVariant.AppendYear;

        var wordlists = _lstWordlists.Items.Cast<object>().Select(o => o.ToString() ?? "").ToArray();

        return new RecoveryOptions
        {
            ArchivePath = _archivePath,
            Strategy = strategy,
            WordlistPaths = wordlists,
            Variants = variants,
            Mask = _txtMask.Text.Trim(),
            Charset = _txtCharset.Text,
            MinLength = (int)_numMin.Value,
            MaxLength = (int)_numMax.Value,
            KnownPrefix = string.IsNullOrEmpty(_txtPrefix.Text) ? null : _txtPrefix.Text,
            MaxAttempts = maxAttempts,
        };
    }

    private void UpdateEstimate()
    {
        if (_lblEstimate == null) return;
        try
        {
            var options = BuildOptions();
            long total = _recovery.EstimateCandidates(options);
            if (total < 0)
            {
                _lblEstimate.Text = "候选总数：未知（字典流式读取）";
            }
            else
            {
                _lblEstimate.Text = total == 0
                    ? "候选总数：0 —— 当前设置有误"
                    : "候选总数：" + total.ToString("N0");
            }
            _lblEstimate.ForeColor = Theme.TextDim;
        }
        catch (ArchiveException ex)
        {
            _lblEstimate.Text = ex.Message;
            _lblEstimate.ForeColor = Theme.Danger;
        }
        catch
        {
            _lblEstimate.Text = "";
        }
    }

    private async Task StartAsync()
    {
        if (_running) return;

        RecoveryOptions options;
        try
        {
            options = BuildOptions();
        }
        catch (Exception ex)
        {
            MsgBox.Warn(this, ex.Message);
            return;
        }

        if (options.Strategy == RecoveryStrategy.Dictionary && (options.WordlistPaths == null || options.WordlistPaths.Count == 0))
        {
            MsgBox.Warn(this, "字典模式至少要添加一个词表文件。");
            return;
        }

        if (options.MinLength > options.MaxLength)
        {
            MsgBox.Warn(this, "最小长度不能大于最大长度。");
            return;
        }

        _foundPassword = null;
        _btnCopy.Enabled = false;
        SetRunning(true);
        _bar.Value = 0;
        SetResult(Theme.TextDim, "正在尝试…（随时可点「停止」）");

        var service = _recovery;
        var progress = new Progress<RecoveryProgress>(OnProgress);
        _cts = new CancellationTokenSource();

        try
        {
            var result = await service.RecoverAsync(options, progress, _cts.Token);
            if (result.Found)
            {
                _foundPassword = result.Password;
                _bar.Value = 100;
                SetResult(Theme.Ok, $"找到密码：{result.Password}    用 {result.Tried:N0} 次尝试、{UiFormat.Duration(result.Elapsed)}（{result.Verifier}）");
                _btnCopy.Enabled = true;
                _lblCurrent.Text = "";
            }
            else if (result.Exhausted)
            {
                SetResult(Theme.Warn, $"已尝试 {result.Tried:N0} 次仍未命中（{UiFormat.Duration(result.Elapsed)}）。可以放宽策略、加词表或加大长度范围再试。");
            }
            else
            {
                SetResult(Theme.Warn, "已停止。");
            }
        }
        catch (OperationCanceledException)
        {
            SetResult(Theme.Warn, "已停止。");
        }
        catch (ArchiveException ex)
        {
            SetResult(Theme.Danger, ex.Message);
        }
        catch (Exception ex)
        {
            SetResult(Theme.Danger, "破译过程出错：" + ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    private void SetRunning(bool running)
    {
        _running = running;
        _btnStart.Enabled = !running;
        _btnStop.Enabled = running;
        _cboStrategy.Enabled = !running;
        _txtMask.ReadOnly = running;
        _txtCharset.ReadOnly = running;
        _txtPrefix.ReadOnly = running;
    }

    private void StopRecovery()
    {
        try { _cts?.Cancel(); } catch { /* 已释放 */ }
        _btnStop.Enabled = false;
        SetResult(Theme.Warn, "正在停止…");
    }

    private void OnProgress(RecoveryProgress p)
    {
        _lblTried.Text = "已尝试 " + p.Tried.ToString("N0");
        _lblSpeed.Text = p.AttemptsPerSecond > 0
            ? "速度 " + p.AttemptsPerSecond.ToString("N0") + " 次/秒"
            : "速度 —";
        _lblEta.Text = p.Eta is { } eta && eta > TimeSpan.Zero && eta < TimeSpan.FromDays(365)
            ? "剩余约 " + UiFormat.Duration(eta)
            : "剩余 —";
        _lblCurrent.Text = p.CurrentCandidate ?? "";

        if (p.Percent >= 0)
            _bar.Value = (int)Math.Clamp(p.Percent, 0, 99);
        else if (p.TotalCandidates > 0)
            _bar.Value = (int)Math.Clamp(p.Tried * 100.0 / p.TotalCandidates, 0, 99);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_running)
        {
            var stop = MsgBox.Confirm(this, "破译仍在进行，关闭窗口会中止它。确定关闭吗？");
            if (!stop)
            {
                e.Cancel = true;
                return;
            }
            try { _cts?.Cancel(); } catch { /* ignore */ }
            _running = false;
        }
        base.OnFormClosing(e);
    }
}
