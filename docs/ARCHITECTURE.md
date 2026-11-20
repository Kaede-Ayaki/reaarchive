# REAArchive 架构

> Windows WinForms（.NET 9）· 零 NuGet · 单进程 · 不联网

## 分层

```
REAArchive.exe
├─ Program.cs            入口 / 命令行分流（GUI、-l、-e）
├─ Ui/
│  ├─ MainForm.cs        主窗口：菜单、分组工具栏、信息条、路径栏、列表、状态栏、拖放、快捷键
│  ├─ InfoStrip.cs       归档信息条（图标 + 名称 + 格式/加密/只读徽章 + 统计，纯自绘）
│  ├─ Dialogs.cs         解压 / 新建 / 添加 / 设置 / 关于 / 密码 / 进度
│  ├─ RecoveryDialog.cs  密码破译对话框（字典 / 掩码 / 增量 + 实时进度 + 结果）
│  ├─ Theme.cs           深色设计系统与控件工厂（配色令牌、手绘图元、自绘 ListView/按钮/输入框）
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
   ├─ FormatDetector.cs  扩展名 + 文件头识别
   ├─ PasswordRecovery.cs  破译编排：候选遍历、进度节流、7z 预言机、取消
   ├─ PasswordCandidates.cs 惰性候选生成（字典/掩码/增量）+ 纯算数估算
   └─ ZipPasswordVerifier.cs 进程内 ZIP 校验（ZipCrypto + WinZip AES + ZIP64）
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
| 密码破译通道 | `.zip` 进程内校验，其余走 `7z t` 预言机 | ZipCrypto 无密钥派生，进程内可跑到 ~50 万次/秒；7z 格式 2^19 轮 SHA-256 派生，单次就几十毫秒，起子进程与自实现结果同量级，不值得自研 7z 解析器 |
| 破译密码编码 | 同一字符串按 UTF-8 / GB18030 / Latin-1 三套字节依次试 | ZipCrypto 的密码按字节参与派生且无编码声明，7-Zip 在不同代码页下写入的字节可能不同；不做三套会在中文密码上假阴性 |
| UI 绘制 | 全部 GDI+ 自绘，零图片资源 | 深色主题下原生控件观感不可控；自绘才能统一圆角/悬停/禁用态，并且不引入任何外部文件（纯净性） |

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
