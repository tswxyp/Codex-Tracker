# Codex Tracker

Windows 本地 Codex 用量统计工具，采用 MIT 许可。**Phase 1** 包含独立采集器、SQLite 账本和只读 WPF 任务列表，已在 Windows 11 x64 实测。支持活动/归档的 JSONL 与 Zstandard rollout，默认只访问本机。

### 从源码启动托盘客户端

在 Windows 上安装 .NET 10 SDK，在项目根目录执行以下 PowerShell 命令，然后把鼠标移到 Codex 左侧的会话行。

```powershell
.\scripts\build-phase2.ps1
& .\artifacts\phase2\windows-x64\CodexTracker.Tray.exe
```

历史验收报告引用的本地交付包、实机数据库和探针输出保存在维护者本机。以下构建脚本可在自己的设备上生成对应版本的程序。

### 自愿贡献校准样本（本地预览）

右键托盘图标可在「退出」上方打开「关于」，查看正在运行的版本和 GitHub 项目。只有这个账号仅在一台设备上工作时，才适合在「登录 Windows 时启动」下方勾选采样。点「确认采集」后，菜单会出现「查看采样进度（仅本机）」。获得一份合格样本后停止采样，点「查看本次样本（仅本机）」可查看所有字段。

样本对应周额度表连续增加一个百分点期间单个任务的用量。样本包含模型、推理强度、速度档及输入、输出、缓存读写、其中推理的 Token 计数。速度或强度未记录时显示「未记录」。会话标识仅用于本机核对任务归属，账号、设备、会话标识、标题、正文、路径、金额和时间戳都不进入样本。只有用户本人能够确认其他设备是否同时使用该账号。该观测属于估算依据。

本版的自愿采样完全在本机运行，没有上传接口或待发送队列。确认前的历史用量不会补入样本。取消勾选、暂停监控、锁屏、断开 Windows 会话或退出 Tracker 后，授权和内存样本都会清除。下次使用需要重新确认。现有本机统计账本独立于自愿采样。[采样源码](client/Tracker.Tray/CalibrationSample.cs)

项目的 Cloudflare Worker 目前仅提供公开状态查询。打开状态页可见 `uploadsEnabled: false`。Worker 拒绝 POST 请求，预留的 D1 数据库尚未与 Worker 绑定。[托管状态](https://codex-tracker-calibration.tswxyp.workers.dev/health) · [Worker 源码](deploy/calibration-host/worker.js)

## 项目盈利管理

Phase 5 候选版在自托管实例的 `/finance` 提供客户项目、合同、分次回款和支出流水。用任务组关联已同步的线程或运行会话，按全部或单一账期核对到账、未收合同款、项目利润及未归属成本，可导出 CSV 和 XLSX。Windows 端继续使用 Phase 4 客户端。

[服务端构建脚本](scripts/build-phase5.ps1) · [升级和使用](docs/phase-5-run.md) · [验收报告](docs/phase-5-acceptance.md)

## 成本核算

Phase 4 候选版在自托管实例的 `/costs` 提供订阅账单、模型价表、Credits 凭据、重置记录和核算快照。账期资源占比按同口径 Credits 计算。Windows 客户端同步后，匹配的侧栏任务可显示本账期费用；悬停可看数据截至时间。完整账期覆盖或价格依据不足时会显示待核算项。

[客户端与服务端构建脚本](scripts/build-phase4.ps1) · [使用与升级](docs/phase-4-run.md) · [验收记录](docs/phase-4-acceptance.md)

## 账号与多设备同步

Phase 3 候选版提供可选的自托管 HTTPS 服务和 Windows 同步客户端。部署者提供 PostgreSQL、证书和邮件服务。在台式机和笔记本分别登录同一 Tracker 账号，经浏览器批准设备，再用 `allow` 明确选择要同步的本机线程。两端离线时各自记录用量，重连后按响应去重。授权、项目和模型汇总可在自托管实例查看。

[同步客户端构建脚本](scripts/build-phase3.ps1) · [部署和使用](docs/phase-3-run.md) · [本次测试与待验项](docs/phase-3-implementation.md)

## 原生聊天列表增强

聊天列表保留 Codex 的原生标题。鼠标移到会话行即可查看累计 Token、对应成本、缓存命中率、模型及收支。卡片跟随聊天列表的浅色和深色。首次读取时显示加载状态，同名或其他设备的会话会提示匹配情况。部分用量缺少模型或权重时，卡片列出已能估算的成本和待估 Token。

Tracker 对齐账号七天额度连续增长一个百分点的时段与本机同期用量，估出每 1% 对应的加权 Token 用量。聊天价值 = 聊天加权用量 ÷ 每 1% 对应的加权用量 × 每周订阅费 ÷ 100。每周订阅费按账期费用 × 7 ÷ 账期天数计算。模型、普通输入、缓存读及输出采用个人 Codex 权重，速度未记录时按标准速度参考。其他设备同期使用会影响估值；样本不足时悬停显示原因。[个人 Codex 的 Token 权重与速度倍率](https://learn.chatgpt.com/docs/pricing?codex-pricing-plans=individual#token-rates)

输出下方的「其中推理」已经包含在输出及累计 Token 中。推理强度通过实际产生的 Token 体现。套餐速度倍率只用于成本估算，例如 GPT-6.2 Sol 和 Astra 的 Standard、Fast、Ultrafast 权重分别为 1、2、6。卡片在模型名称旁标出已识别的加速档及倍率。Tracker 用同一数据根下的会话、轮次和模型匹配本机诊断中的请求档位，后台刷新并只在本地缓存匹配键和档位。速度记录冲突的部分待估。会话用量或额度校准样本有速度缺失时，卡片和近期盈亏会标明标准档假设。[个人套餐速度倍率](https://learn.chatgpt.com/docs/pricing#speed-multipliers)

在卡片点 **填写会话收入** 或 **填写项目收入**，窗口会直接显示这条会话或所属项目的名称。选中已有入账记录可编辑，清空金额可撤销该笔记录。未填写到账日的金额计入累计收入，填入实际到账日并保存后，也会计入对应日期的报表。会话盈亏只扣本会话成本。项目默认汇总已录的会话收入，手工登记的项目收入优先，项目成本包括零收入会话。

托盘的 **Language** 入口标出当前语言，默认跟随 Windows。**显示货币** 统一控制卡片上的成本、收入和盈亏。英语界面自动通过 HTTPS 向 IPinfo 查询出口 IP 所在国家，查询失败时沿用 Windows 地区。再次选择当前手动币种可恢复自动选择。**盈亏颜色** 可手动指定。使用自动配色时，手选人民币为红盈绿亏，手选美元及其他支持的币种为绿盈红亏。币种也由系统自动选择时，按 Windows 显示语言配色，中文红盈绿亏，其他语言绿盈红亏。报表内手选币种只影响该报表。

在 **输入订阅费用** 选择扣款币种，填写每月实付金额。七天预算也使用扣款币种，新设置按每月 30 天估算。卡片和报表跨币种显示时，Tracker 在后台使用最新可用的每日参考汇率。初次换算暂时缺少报价时，该金额显示「待计算」。[每日参考汇率](https://api.frankfurter.dev/v1/latest?base=USD)

卡片上的 **近期盈亏** 只显示该会话。双击托盘图标或从托盘菜单打开 **近期盈亏** 可查看全部本机收支。可选近 7/30/90 天、本月或自定义日期及报表币种。缺少计算依据的金额显示 N/A。收入按到账日，成本按本机用量发生日，以当前订阅和可用校准样本估算。配置自托管同步服务后，托盘菜单还会显示 **自托管账单**，用于打开服务端的成本核算页面。

当前版本可由 `scripts/build-phase2.ps1 -OutputDirectory artifacts/sidebar/windows-x64` 构建。安装 .NET 10 Desktop Runtime 后运行该目录的 `CodexTracker.Tray.exe` 即可。

Phase 2 核心功能有条件通过验收，当前开放个人试用。Windows 11 笔记本深色、100% 缩放下的文字、悬停、点击及滚动已获实机确认。125% 缩放补验暂缓，其余环境按登记表跟进。[实机补验登记](docs/phase-2-compatibility.md)

Windows 11 x64 安装 .NET 10 Desktop Runtime 后，打开 `artifacts/phase2/windows-x64/CodexTracker.Tray.exe`。首次采集回填完成前，数字会逐步出现。打开 Codex 即可在匹配行上查看统计，窄行可直接悬停。右键系统托盘图标可暂停、恢复、退出或启用登录启动。默认库是实时 Tracker 库。完整实测矩阵及兼容限制见 [Phase 2 验收](docs/phase-2-acceptance.md)。

在 Windows x64 的 PowerShell 中复验，可执行以下命令。最后三条实机探针需要托盘已运行，且 Codex 位于前台、侧栏展开并显示已匹配的聊天。hover 和 scroll 会暂时移动鼠标或滚动列表。

```powershell
.\scripts\build-phase2.ps1
dotnet restore tests/Tracker.Tray.Tests --locked-mode
dotnet build tests/Tracker.Tray.Tests -c Release --no-restore
$test = '.\tests\Tracker.Tray.Tests\bin\Release\net10.0-windows\Tracker.Tray.Tests.exe'
& $test
python -B tests/test_tray_worker.py
& $test relocate
& $test hover
& $test scroll
```

## Phase 1 运行

运行已构建程序需要 Windows x64 和 .NET 10 Desktop Runtime。以下 PowerShell 命令在项目根目录执行。

```powershell
$app = '.\artifacts\phase1\windows-x64'
& "$app\CodexTracker.Viewer.exe" '.\artifacts\phase1\data\tracker.sqlite'
```

上面的任务列表打开随本地交付提供的真实验收样本库。自行采集时，使用以下命令。首次扫描会回填历史，大型压缩档可能耗时。Ctrl+C 结束，下一次从最近一次已提交的检查点恢复。

```powershell
& "$app\CodexTracker.exe" scan
& "$app\CodexTracker.exe" list
& "$app\CodexTracker.exe" models --json
& "$app\CodexTracker.exe" quality --json
& "$app\CodexTracker.exe" watch --interval 15
```

采集根目录优先使用 `CODEX_HOME`，默认回退到 `%USERPROFILE%\.codex`。Tracker 库默认保存在 `%LOCALAPPDATA%\CodexTracker\tracker.sqlite`。可用 `--root`、`--device`、`--db` 指定来源和独立数据库，或用可重复的 `--file` 选择根目录内的文件。直接打开 WPF 程序即可查看默认库，也可以在窗口中选择其他 Tracker 数据库。窗口关闭后，独立启动的 Worker 会继续运行。

## 构建与测试

使用 Windows x64 和 .NET 10 SDK 构建。下载源代码的用户先执行构建命令，验收样本数据库属于本机数据，不在源码分发范围内。端到端测试另用 Python 3.10+ 和支持 Zstandard 的 Node.js 24。

```powershell
.\scripts\build.ps1
python -B tests\test_core.py
python -B scripts\verify-real.py --db .\artifacts\my-verification.sqlite
```

最后一条会挑选本机已有且稳定的小型 rollout 建立新库，独立比较响应和旧快照。样本不足时无法复现相同覆盖率。固定脱敏夹具可在没有 Codex 历史的机器上运行。

[Phase 1 验收与准确率](docs/phase-1-acceptance.md) · [数据库结构](docs/database.md) · [架构决策](ARCHITECTURE.md) · [实现计划](docs/phase-1-plan.md) · [Phase 0 勘察](docs/phase-0-survey.md) · [变更记录](CHANGELOG.md) · [第三方许可](docs/third-party.md)
