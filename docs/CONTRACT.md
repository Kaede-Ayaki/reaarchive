# 契约：REAArchive（纯净压缩）v1

> Lead 维护。`archive-engineer` 与 `archive-ui` 并行开发，一切跨层调用以 `src/REAArchive/Archive/ArchiveTypes.cs`
> （契约即代码）与本文件为准。需要变更先 `send_message target="lead"`。

## 0. 产品与硬约束

**REAArchive** = 纯净压缩软件。Windows 桌面 WinForms 应用，.NET 9。

必须满足用户的三个诉求：**纯净无广告 / 性能好 / 不提示升级**。因此：

1. **绝不联网**：不检查更新、无广告位、无遥测、无"推荐安装"、无会员入口。代码里不得出现任何 HTTP 客户端。
2. **无更新器**：不打包 Updater，不写自启项，不弹"有新版本"。
3. **只读用户文件**：除用户明确指定的解压目录/新建归档外，不写任何位置；注册表**仅在用户点"安装右键菜单"时**写 HKCU 且可一键卸载。
4. 性能：大归档列表 ≤2s（10 万条目内），解压速度不低于 7-Zip 命令行（同一引擎），UI 不卡死（全部 async）。
5. 编码正确：GBK/UTF-8 中文文件名都不能乱码（7-Zip `-sccUTF-8` + 解码链路）。
6. 安全：解压时**防目录穿越**（`..\` 跳出目标目录必须拒绝）；密码不得写入日志/配置文件。
7. 零 NuGet 依赖（离线可编译）：只用 BCL + WinForms。

## 1. 技术栈与目录

- `src/REAArchive/REAArchive.csproj`：`net9.0-windows`、`UseWindowsForms`、AssemblyName `REAArchive`。
- 随包引擎：`Resources/7z/{7z.exe,7z.dll,License.txt,README-ENGINE.txt}`（7-Zip 25.01，LGPL，原样分发）。
- 文件所有权：
  - Lead → `Archive/ArchiveTypes.cs`、`REAArchive.csproj`、`Resources/**`、`scripts/**`、`docs/**`、`README.md`
  - `archive-engineer` → `Archive/**`（除 ArchiveTypes.cs）、`tests/**`
  - `archive-ui` → `Program.cs`、`Ui/**`
  - `qa-verifier` → `qa/**`
- 构建：`pwsh scripts/build.ps1`（`dotnet build -c Release`），产物 `src/REAArchive/bin/Release/net9.0-windows/REAArchive.exe`。

## 2. 引擎层（archive-engineer）

实现命名空间 `REAArchive.Archive`，**必须**提供：

    /// <summary>新建归档（目标已存在 = 覆盖重建：写临时文件成功后原子替换）。</summary>
    /// <summary>向已存在归档追加（7z a）。UI 的"添加"按钮直调此方法，不要用"解包重建"兜底。</summary>
    /// <summary>从归档删除条目（7z d）。</summary>
```
（完整签名以 `ArchiveTypes.cs` 为准：`CreateAsync / AddAsync / DeleteAsync / ExtractAsync / ListAsync / TestAsync / ReadEntryAsync`。）

**裁决（2026-11-19）**：§3.2.5 的"添加/删除"由引擎直接提供 `AddAsync`/`DeleteAsync`（7z `a` / `d`），
UI 不得用"解压→重建→替换"绕过引擎；`CreateAsync` 对已存在目标必须是**覆盖重建**（先写临时文件再原子替换），
`AddAsync` 才是追加。UI 用 `Ui/EngineBridge.cs` 反射定位 `IArchiveService` 实现是允许的（引擎未就绪时不崩）。

实现要点：

1. **唯一引擎 = 7z.exe 子进程**（`Resources/7z/7z.exe`），不要自己写格式解析（zip 也交给 7z，保证 GBK 兼容）。
2. **进程调用规范**：
   - 一律 `CreateNoWindow = true`、`RedirectStandardOutput/Error = true`、`StandardOutputEncoding = UTF8`。
   - 列表：`7z l -slt -sccUTF-8 -p<password> "<archive>"`；解析 `----------` 之后每个 `Key = Value` 块。
   - 解压：`7z x -y -bsp1 -bb1 -sccUTF-8 [-o"<dest>"] [-p<pw>] "<archive>" [selected...]`；`-bsp1` 的 `NN%` 用于进度。
   - 新建：`7z a -t{7z|zip|tar|gzip|bzip2|xz} -mx{N} [-p<pw>] [-mhe=on] [-v<size>b] "<out>" -w"<base>" <sources...>`（用 `@listfile` 传大量文件，listfile 用 UTF-8 + `-scsUTF-8`）。
   - 测试：`7z t`。
   - 读取单文件：`7z e -so "<archive>" "<entry>"`。
   - **密码不落日志**；命令行里带 `-p` 的进程对象用完即弃（在 README 已知限制里注明"密码会短暂出现在本机进程命令行"）。
3. **进度**：解析 `-bsp1` 百分比 + 当前项；同时按输出文件名累计。`ProgressInfo.Percent` 无法估算时给 -1。
4. **取消**：`CancellationToken` 注册 `Process.Kill(entireProcessTree: true)`；取消抛 `OperationCanceledException`。
5. **错误映射**：7z 退出码 0=成功、1=警告、2=致命、7=命令行错误、8=内存、255=用户取消；密码错误/损坏要给出**中文可读**的 `ArchiveException`（例如"密码错误或文件损坏"）。
6. **目录穿越防护**：解压前对每个 entry 的规范化目标路径做 `Path.GetFullPath` 前缀校验，越界抛 `ArchiveException`。文件名含 `:`/`|` 等非法字符要消毒并可警告。
7. **长路径**：目标路径 > 240 字符时用 `\\?\` 前缀。
8. **UTF-8/GBK**：控制台输出用 `-sccUTF-8`；若仍出现 `?`，记录 warning 不崩溃。
9. `EngineAvailable`：找不到 7z.exe 返回 false；`EngineVersion` 用 `7z i` 或 `7z` 首行解析。
10. `DetectFormat`：先扩展名（.7z/.zip/.rar/.tar/.gz/.tgz/.bz2/.xz/.iso/.wim），再读文件头（`7z¼²`、`PK\x03\x04`、`Rar!`、`ustar`、`\x1F\x8B`、`BZh`、`\xFD7zXZ`）兜底。

自测（`tests/`）：`dotnet run --project tests/EngineTests` 或 `dotnet test`（无 NuGet 断言库 → 用纯 `Console` + 退出码，或 xunit 不可用则自写断言）。
必须覆盖：列表/创建/解压/测试四操作 × {7z, zip, tar, gz, bz2, xz}；中文文件名；10k 小文件；深目录；空文件/空目录；损坏归档；错误密码；取消；目录穿越（恶意归档用 7z 命令行构造 `../evil.txt`）。

## 3. UI 层（archive-ui）

### 3.1 主窗口（深色专业风格）
```
┌ 菜单：文件 编辑 查看 工具 帮助 ───────────────────────────────┐
│ 工具栏：打开 | 解压到… | 解压到当前 | 测试 | 新建 | 添加 | 删除 | 信息 │
│ 路径栏（归档内路径，可点回上级）                                      │
│ ┌ 列表（Details）───────────────────────────────────────┐ │
│ │ 名称 | 大小 | 压缩后 | 类型 | 修改时间 | CRC          │ │
│ └───────────────────────────────────────────────────────┘ │
│ 状态栏：N 个文件 / 总计 X / 压缩后 Y / 引擎 7-Zip 25.01 / 耗时       │
└──────────────────────────────────────────────────────────────┘
```
- 深色主题（#1e2228 系）、系统字体（Microsoft YaHei UI 9pt）、高 DPI。
- 双击目录 = 进入；双击文件 = 解到临时目录并用 shell 打开（临时目录在退出时清理，设置可关）。
- 拖放：把文件/文件夹拖进窗口 = 若无打开的归档则新建归档流程；有归档则"添加"。
- 打开归档支持命令行参数（`REAArchive.exe "x.7z"`）。

### 3.2 必须可用的功能（QA 逐项点验）
1. 打开 zip/7z/tar/gz/bz2/xz/rar（rar 只读）并正确列出（含中文名、目录树、大小、CRC）。
2. 解压到…（选目标、冲突策略 覆盖/跳过/重命名、密码框）→ 真实落盘 + 完成后可"打开文件夹"。
3. 解压到当前文件夹（右键菜单/工具栏）。
4. 新建归档：格式 7z/zip/tar/gz/bz2/xz、压缩级别 0/1/5/7/9、密码、7z 连文件名加密、分卷大小 → 真实生成文件。
5. 向已打开归档"添加"文件/文件夹；"删除"选中条目；"测试"完整性。
6. 多选、全选、按列排序、搜索过滤框（按名称）。
7. 进度对话框：百分比、当前文件、速度、剩余时间、取消按钮（取消后进程真被杀掉）。
8. 设置：默认解压目录、冲突策略、解压后打开文件夹、临时目录、字号；**"关于"页明确写"本程序不联网、不检查更新、无广告"**。
9. 右键菜单集成（工具菜单里）：安装/卸载 HKCU 下的"用 REAArchive 解压/压缩"，可逆，需用户确认。
10. 错误处理：损坏归档、密码错误、无权限、磁盘满 → 中文提示，不崩溃、不弹英文栈。

### 3.3 对外接口（UI 侧给 QA/自动化）
- 窗口标题包含 `REAArchive`；主窗体 `Control.Name`：`MainForm`。
- 关键控件 Name：`lvEntries`（列表）、`txtPath`（路径栏）、`btnExtractTo`、`btnTest`、`btnCreate`、`btnAdd`、`btnDelete`、`progressBar`、`lblStatus`、`txtFilter`。
- 命令行：`REAArchive.exe <archive>`；`REAArchive.exe -e <archive> <dest>`（解压到指定目录后退出，用于自动化）；`-l <archive>`（列表输出到 stdout 后退出）。
- 所有耗时操作 `async`，UI 线程不阻塞；进度通过 `IProgress<ProgressInfo>` 回主线程。

## 4. 验收（QA）
- 引擎自测全绿；`scripts/build.ps1` 零警告（或警告可解释）。
- 真鼠标/键盘点验 §3.2 十项；`-l`/`-e` 命令行可用。
- 性能对照：同一 500MB/2 万文件测试集，本程序解压耗时与同类商业压缩软件 CLI 同量级（±30% 内）；10 万条目列表 ≤2s。
- 无网络：用 `Get-NetTCPConnection -OwningProcess <pid>` 与静态扫描（代码中无 HttpClient/WebRequest）双重验证。
- 无更新/广告：静态扫描二进制字符串不得含 `update`/`ad` 相关下载逻辑；`About` 明确声明。
