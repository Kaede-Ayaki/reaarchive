# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 与语义化版本。

## [1.0.0] - 2026-11-19

首个公开版本。

### Added

- **归档浏览**：7z / zip / rar（只读）/ tar / gz / tgz / bz2 / xz / iso / wim；目录树进出、路径栏、列排序、搜索过滤、CRC 列。
- **解压**：解压到…（目标目录、覆盖/跳过/重命名、密码）、解压到当前文件夹、只解选中项、完成后打开文件夹；解压前磁盘空间预检。
- **压缩**：新建 7z/zip/tar/gz/bz2/xz，级别 0/1/5/7/9，密码，7z 连文件名加密，分卷设置；向已有归档追加；删除归档内条目。
- **校验**：完整性测试（7z `t`）。
- **安全**：目录穿越 / NTFS ADS / 非法字符拦截、长路径 `\\?\`、密码不落日志。
- **外壳**：可逆的 HKCU 右键菜单（10 个扩展名 × 5 项 + 压缩项）、`REAArchive.Archive` ProgID 与"打开方式"候选。
- **命令行**：`-l` / `-e` / `--extract-here` / `--extract-to` / `--extract-ask` / `--test` / `--compress-7z|zip` / `--install-shell` / `--uninstall-shell` / `--shell-status`。
- **打包**：`scripts/pack.ps1` 一键发布单文件自包含 exe（内嵌 7-Zip 引擎）并安装到 `%LOCALAPPDATA%\Programs\REAArchive`，创建桌面/开始菜单快捷方式。
- **架构**：引擎层与 UI 层以 `IArchiveService` 契约解耦；零 NuGet 依赖，离线可构建。

### Fixed

- 右键菜单「解压…」被 CLI 参数判定截走（改为 GUI 分支）。
- 10 万条目 CLI 列表 29–32s（逐行 flush）→ **1.6–2.0s**（缓冲输出）。
- 大量小文件解压时进度提前 100%（改为按条目数/字节估算，退出前封顶 99，正常结束才 100）。
- 状态栏「已加密 / 只读」标记被状态刷新覆盖。
- 对象树双击改名偶发失效（面板重建抢走 `dblclick`；改为指纹比对 + pointerdown 兜底）。
- solid 7z 下状态栏「压缩后」总量偏小（改用归档实际大小）。
- 字号设置不落盘（改为即时保存）。
- 右键菜单压缩时弹模态框卡住资源管理器（shell 模式改自动改名 + 完成后自动关闭）。

### Known issues

- RAR 只能解压不能创建（7-Zip 免费版限制）。
- 10 万条目的 GUI **冷启动**约 5.2s（进程启动 ~2.4s + 首次列表 ~2.5s）；进程内刷新 1.11s。
- 分卷拆分仅提供界面设置，未做完整功能实测。
- 密码会短暂出现在本机 `7z` 进程命令行。
- BZip2 头不含原始大小，列表 `TotalSize=0` 按未知处理。
- emoji 文件名在列表中显示为方块（复制/解压不受影响）。

[1.0.0]: https://github.com/Kaede-Ayaki/reaarchive/releases/tag/v1.0.0
