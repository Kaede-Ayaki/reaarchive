# REAArchive 引擎自测（tests/EngineTests）

零 NuGet 的控制台 Exe 工程，`ProjectReference` 主工程 `REAArchive.csproj`，只调用 `REAArchive.Archive.IArchiveService`
（不碰 UI）。断言是自写的（`TestHarness.cs`），全部跑完打印 `PASS n / FAIL n / SKIP n`，**FAIL > 0 时退出码为 1**。

## 运行

```powershell
pwsh scripts/build.ps1
dotnet run --project tests/EngineTests -c Release
```

可选参数：

```powershell
# 只跑组名包含关键字的用例（组名见下表）
dotnet run --project tests/EngineTests -c Release -- 六格式

# 保留临时目录（失败排查用）
dotnet run --project tests/EngineTests -c Release -- --keep

# 追加与外部对照 CLI 的性能对照（设置 REA_BENCH_REF_CLI 指向该 CLI 才会运行，否则 SKIP；程序本身不依赖它）
dotnet run --project tests/EngineTests -c Release -- --bench
```

> 注意：如果 `REAArchive.exe` 正在运行，`dotnet build` 可能因为 exe 被占用而失败（Windows 文件锁）。
> 关闭正在运行的程序后重跑，或只测引擎时改用：
> `dotnet build tests/EngineTests -c Release -p:UseAppHost=false` + `dotnet tests/EngineTests/bin/Release/net9.0-windows/EngineTests.dll`

## 覆盖范围（19 组）

| 组 | 内容 |
| --- | --- |
| 引擎信息 | `EngineAvailable` / `EngineVersion` / `CreatableFormats` / 7z.exe 路径 |
| 六格式矩阵 | 7z、zip、tar、gz、bz2、xz 各跑「创建 → 列表 → 解压 → 逐字节比对 → 完整性测试」；含中文/日文/emoji/空格文件名、空文件、空目录、CRC、进度事件 |
| 深目录与长路径 | 24 层嵌套；解压目标路径 > 240 字符（`\\?\`）真实落盘并比对 |
| 大文件 200MB | 写入 200MB → 创建 → 列表大小 → 解压 → SHA256 逐字节比对（空间不足自动 SKIP） |
| 10k 小文件 | 10,000 个文件创建 + 列表（记录冷/热耗时） |
| 分卷 | `-v100000b` 生成多卷 → 列表第一卷 → 解压比对 |
| 选择解压与冲突策略 | 只解压选中条目；覆盖/跳过/重命名三种策略 |
| 进度信息 | Phase / Percent / CurrentItem / Speed 事件 |
| 进度单调性（20k 小文件） | B04 回归：20,000 个 64B 文件解压/压缩，Percent 单调不减、不提前 100%、最后才到 100%；同时校验 `ListResult.TotalSize` |
| 追加与删除 | `AddAsync`（7z a）、`DeleteAsync`（7z d）、不存在条目静默跳过、目录前缀连带删除 |
| 加密归档追加删除 | 密码透传、文件名加密不被破坏、无密码不挂起 |
| 覆盖重建 | 同路径 `CreateAsync` 只有新内容（不追加）；不同格式覆盖同名目标 |
| 非法参数 | RAR 不可创建、源不存在、gz 压目录、空源列表 → 中文 `ArchiveException` |
| 不存在与损坏 | 不存在文件、随机垃圾、截断 7z、文件夹当归档 → 中文异常 |
| 密码错误 | 7z 数据加密 / 文件名加密 / ZIP 加密；错误密码 → “密码错误”；ZIP 非 ASCII 密码提前报错 |
| 单文件读取 | `ReadEntryAsync` 内容一致、超 `maxBytes`、条目不存在、目录不能预览 |
| 取消 | 2 秒后 Cancel → `OperationCanceledException`；旧归档不被破坏；临时目录清理；**5 秒内轮询确认无残留 7z 进程** |
| 目录穿越 | `../`、`..\..\`、`/abs`、`C:\`、NTFS ADS 冒号、`sub/../../` 全部拒绝且不落盘 |
| 非法字符消毒 | `\| ? < >`、结尾点号、保留设备名（CON）→ `_` 替换 + warning |
| 格式识别 | 扩展名（含 `.7z.001` 分卷）、文件头（7z/PK/Rar!/ustar/gzip/BZh/xz）兜底、随机数据 Unknown |

## 本机基线（2026-11-19，Windows / .NET 9.0.201 / 7-Zip 25.01 x64）

```
PASS 271 / FAIL 0 / SKIP 0      全量约 60s（含 20k 小文件进度用例的 16s 真实解压）
```

- 取消用例：2 秒后 Cancel，**2.06–2.22s 返回** `OperationCanceledException`；Kill 后进程收尾 **8–11ms**；残留 7z 进程 **0**（连续 5 次单跑均通过）；原归档内容保持不变。
- 六格式小数据集：创建 50–80ms，解压 100–115ms（含进程启动 + 列表校验）。
- 200MB 单文件（7z -mx0 store）：创建 256–260ms（≈780 MB/s），解压 236–244ms（≈840 MB/s）。
- 10,000 小文件：创建 1424–1589ms，列表 **冷 83–91ms / 热 84–92ms**（≈ 8–9µs/条目）。
- 100,000 条目列表：**302ms**（契约要求 ≤2s）。

### B04 进度语义（20k 小文件实测）

7z 的 `-bsp1` 百分比按压缩流字节估算，对“两万个几十字节的小文件”会提前报 100%（真正写盘还要十几秒）。
引擎现在按条目数换算（条目多/平均体积小时），单文件才用字节百分比；**成功结束前 Percent 封顶 99，只有 7z 正常退出后才发 100**；
两者都无法估算时（如未预列表的 `TestAsync`）给 `-1`。实测采样（20,000 × 64B）：

```
 idx      t(ms)   Percent      BytesDone / Total         当前项
   0       188      0.0%   0 B /   1.22 MB
   1       258      0.1%   1.13 KB /   1.22 MB   f00018.txt
   2       328      0.5%   5.75 KB /   1.22 MB   f00092.txt
   3       397      1.0%   12.19 KB /   1.22 MB  f00195.txt
   4       468      1.4%   17.94 KB /   1.22 MB  f00287.txt
   5       538      1.9%   23.63 KB /   1.22 MB  f00378.txt
   6       608      2.3%   29.31 KB /   1.22 MB  f00469.txt
   7       678      2.8%   34.69 KB /   1.22 MB  f00555.txt
   8       748      3.2%   39.94 KB /   1.22 MB  f00639.txt
   9       818      3.6%   45.44 KB /   1.22 MB  f00727.txt
  24      1868     10.4%   129.5 KB /   1.22 MB   f02072.txt
  47      3478     20.4%   255.38 KB /   1.22 MB  f04086.txt
  70      5088     30.3%   379 KB /   1.22 MB     f06064.txt
  94      6768     40.4%   504.88 KB /   1.22 MB  f08078.txt
 117      8377     50.4%   630.38 KB /   1.22 MB  f10086.txt
 139      9921     60.0%   750.5 KB /   1.22 MB   f12008.txt
 164     11671     70.3%   878.19 KB /   1.22 MB  f14051.txt
 187     13281     80.1%   1001.5 KB /   1.22 MB  f16024.txt
 211     14961     90.3%   1.1 MB /   1.22 MB     f18058.txt
 236     16681    100.0%   1.22 MB /   1.22 MB    f19999.txt
```

共 237 个进度事件（70ms 节流），单调不减，首次出现 100% 就是最后一个事件。

### B08：ListResult.TotalSize 准确性

- 对 7z / zip / tar / gz / xz：等于所有非目录条目 `Size` 之和，实测与源文件总字节完全一致（六格式断言 + 20k solid 7z：20,000 × 64B）。
- 对 **bzip2**：容器头不存原始大小，`TotalSize` 为 0（只有 `CompressedSize` 可用）；UI 做磁盘预检时需要特判该格式。

### 性能对照（`--bench`）

500MB + 20,000 小文件，7z -mx1；两次运行的区间：

| 场景 | REAArchive | 对照 CLI | 比值 |
| --- | --- | --- | --- |
| 500MB 列表 | 48–60ms | 51–213ms | 0.23–1.16× |
| 500MB 解压 | 426–591ms | 422–617ms | 0.96–1.01× |
| 20k 列表 | 137–141ms | 150–151ms | 0.91–0.94× |
| 20k 解压 | 17.3–17.8s | 12.8–13.3s | 1.33–1.35× |

> 20k 小文件解压的差距来自引擎本身：同一数据集直接跑 `7z x` 也要 16.4–19.4s，而本引擎与 `7z x` 持平（开销 ≈ 0）。
> 契约 §0.4 要求「不低于 7-Zip 命令行（同一引擎）」——满足；与对照 CLI 的 ±30% 在 20k 小文件场景为 1.33×，500MB 场景更快。

## 说明

- 临时文件写在 `%TEMP%\reaarchive-tests\<时间戳>-<pid>\`，跑完自动删除（`--keep` 保留）。
- 大文件/性能用例需要 ≥2GB 临时空间，空间不足会自动 SKIP 而不是 FAIL。
- **请串行运行**：取消用例会检查 `7z.exe` 进程数，同时跑两份测试会互相干扰（假阳性）。
- 密码会短暂出现在 7z.exe 的进程命令行（Windows 限制），测试里使用的都是临时假密码。
