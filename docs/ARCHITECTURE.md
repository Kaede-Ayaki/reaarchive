# REAArchive 架构

> Windows WinForms（.NET 9）· 零 NuGet · 单进程 · 不联网

## 分层

```
REAArchive.exe
├─ Program.cs            入口 / 命令行分流（GUI、-l、-e）
├─ Ui/
│  ├─ MainForm.cs        主窗口：菜单、工具栏、路径栏、列表、状态栏、拖放、快捷键
│  ├─ Dialogs.cs         解压 / 新建 / 添加 / 设置 / 关于 / 密码 / 进度
│  ├─ Theme.cs           深色主题与控件工厂（统一配色、Dpi、圆角、图标）
│  ├─ Settings.cs        %APPDATA%\REAArchive\settings.json
│  ├─ ShellIntegration.cs HKCU 右键菜单（安装/卸载，可逆）
│  └─ EngineBridge.cs    引擎定位（IArchiveService 反射兜底，未就绪不崩）
└─ Archive/              引擎层（无 WinForms 依赖）
   ├─ ArchiveTypes.cs    ★ 契约：IArchiveService + 数据 record（Lead 维护）
   ├─ ArchiveService.cs  编排：列表/解压/创建/追加/删除/测试/读取；进度与取消
   ├─ SevenZipRunner.cs  7z.exe 子进程（无窗口、UTF-8、stdin 关闭、Kill 整树、退出码）
   ├─ SevenZipListParser.cs  `-slt` 输出 → ArchiveEntryInfo
   ├─ SevenZipEngine.cs  引擎探测（路径、版本、可用性）
   ├─ PathGuard.cs       目录穿越 / ADS / 非法字符 / 长路径 `\\?\`
   └─ FormatDetector.cs  扩展名 + 文件头识别
```

依赖方向：UI → Archive；Archive 不引用 UI（便于自测与将来换壳）。

## 关键决策

| 决策 | 选择 | 理由 |
|---|---|---|
| 归档引擎 | 随包 **7-Zip 25.01** `7z.exe` 子进程 | 格式覆盖最广（7z/zip/rar/tar/gz/bz2/xz/iso/wim…）、性能与 7-Zip 命令行一致、LGPL 可分发；不必自己解析格式（安全风险高、代码量大） |
| 不自己解析 zip | 连 zip 也交给 7z | GBK/UTF-8 中文名、加密 zip、分卷等边角都由 7-Zip 处理，避免"自研解析器吃掉用户文件" |
| 进程隔离 | 每次操作一个 7z 进程 | 崩溃/取消不影响主程序；取消 = Kill 整棵进程树 |
| 进度 | 解析 `-bsp1` 的 `NN%` | 7-Zip 官方支持的机器可读进度 |
| 密码 | `-p<pwd>` 传参 | 7z 控制台无安全 stdin 通道；**已知限制**：密码会短暂出现在本机进程命令行（README 已如实写明） |
| 覆盖语义 | CreateAsync = 临时文件 + 原子替换；AddAsync = 追加 | 防止"新建到已存在归档"变成静默追加；防止中途失败毁原归档 |
| 安全 | 解压前 `PathGuard` 校验每个条目 | 目录穿越/ADS 是压缩软件最经典的攻击面，必须在落盘前拦截 |
| 无网络 | 无 HttpClient/WebRequest/Socket；无 Updater | 这是产品承诺 |

## 一次"解压"的数据流

```
MainForm（点"解压到…"）
  → Dialogs.ExtractDialog 收集目标目录/冲突策略/密码
  → ArchiveService.ExtractAsync(archive, options, progress, ct)
       ├─ FormatDetector.DetectFormat（扩展名/文件头）
       ├─ 7z.exe x -y -bsp1 -bb1 -sccUTF-8 -o"<dest>" [-p<pw>] "<archive>"
       │     stdout 逐行解析 "NN%" / 当前文件 → IProgress<ProgressInfo>
       │     每个落盘项先经 PathGuard 校验（穿越/ADS/非法字符/长路径）
       └─ 退出码 → 成功 / 中文 ArchiveException
  → 进度对话框结束 → 可"打开文件夹"；状态栏更新总耗时与速度
```

## 一次"新建归档"

```
工具栏"新建" → CreateDialog（格式/级别/密码/加密文件名/分卷/待压缩列表）
  → ArchiveService.CreateAsync
       ├─ 目标已存在 → 写 <archive>.tmp，成功后 File.Move 原子替换
       ├─ 7z a -t<fmt> -mx<N> [-p] [-mhe=on] [-v..] -w<base> "<tmp>" @listfile
       └─ 进度解析 → 完成 → 主窗口自动打开新归档
```

## 规模

引擎 7 个文件（约 1.6k 行）+ UI 8 个文件（约 2.2k 行）+ 自测 8 个文件（约 1.2k 行）。
无任何 NuGet 依赖；构建产物 147KB（不含随包 7-Zip 引擎 2.4MB）。
