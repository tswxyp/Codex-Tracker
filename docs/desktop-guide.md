# 桌面设置、采样与命令行

[回到项目首页](../README.md)

## 语言、币种与盈亏颜色

- 托盘的 **Language** 菜单会显示当前语言。语言默认跟随 Windows，也可以手动切换。
- **输入订阅费用** 使用实际扣款的币种。七天预算也用这个币种。**显示货币** 统一控制卡片里的成本、收入和盈亏。再次选择当前手选币种可以恢复自动选择。报表里选的币种只影响该报表。
- **盈亏颜色** 可以手选。自动配色时，手动选择人民币会使用红盈绿亏，手选美元等其他支持的货币会使用绿盈红亏。货币也设为自动时，按 Windows 显示语言配色，中文为红盈绿亏。
- 跨币种显示使用最新可用的每日参考汇率。首次换算缺少报价时，金额会暂时显示为待计算。[汇率来源](https://api.frankfurter.dev/v1/latest?base=USD)

## 为什么有些用量仍待估？

成本估算要同时知道模型、用量和可用的额度校准样本。缺少依据的部分会列为待估 Token。速度记录缺失时会注明按标准速度估算，记录冲突时该部分待估。推理强度通过实际产生的 Token 体现，输出下方的「其中推理」已经计入输出。后台计时还会区分总历时、活跃、暂停和无法确认的区间。[统计规则与准确性](phase-1-acceptance.md) · [个人套餐权重与速度倍率](https://learn.chatgpt.com/docs/pricing?codex-pricing-plans=individual#token-rates)

## 贡献校准样本

1. 选择只有一台设备使用该账号、只有一个会话执行任务的时段。右键托盘图标，勾选 **我愿意帮助采集并上传用量样本**，在弹窗中选择 **同意并自动上传**。其他聊天可以开着，其他任务和子代理请等采样结束再运行。
2. 周额度用量增加 1、2、3 个百分点时，每档最多取两个不重叠的区间。程序会等待账本写入，并在发送前复核。断网后使用同一回执号重试。
3. 在托盘菜单打开 **查看采样进度**，可以查看各档收到的回执和筛查结果。如果后来发现还有别的设备或任务在用额度，可在本次进度窗口选中相应样本，点 **标记为多任务并排除**。

每份载荷包含随机回执号、额度增量、模型与推理/速度档位、分项 Token 计数，以及可用时的会话累计计数增量。用于筛查的三个本机结果是运行任务的会话数、是否检测到任务重叠、是否有用量无法归属。会话 ID 只在本机使用，账号、设备标识、会话列表、路径、时间戳、标题、正文及财务金额都不会进入载荷。[采样源码](../client/Tracker.Tray/CalibrationSample.cs) · [上传源码](../client/Tracker.Tray/SampleSubmission.cs)

云端会排除本机报告的多会话、重叠任务和归属不清的样本，再核对有效计数、累计增量与模型档位。服务端只能筛查设备上报的观测。其他设备同时使用账号的情况，仍需贡献者自己确认。云端样本的筛查结果和托盘上个人成本的本机校准分别计算。[云端筛查源码](../deploy/calibration-host/worker.js)

取消勾选、暂停监控、锁屏、断开 Windows 会话或退出都会结束授权，清空本机内存中的样本和待重试请求。重新登录需要再次同意。

## 本机日志、数据库与命令行

后台进程默认读取 `%USERPROFILE%\.codex` 下的日志，设置了 `CODEX_HOME` 时优先使用该目录。Tracker 自己的数据库位于 `%LOCALAPPDATA%\CodexTracker\tracker.sqlite`。可以用命令行查看会话、按模型汇总或检查数据质量。先按[首页步骤](../README.md#quick-start)构建桌面版，再在仓库根目录运行以下 PowerShell 命令。

```powershell
$worker = '.\artifacts\phase2\windows-x64\CodexTracker.exe'
& $worker list
& $worker models --json
& $worker quality --json
```

独立采集可使用 `scan` 扫描一次，或使用 `watch --interval 15` 持续扫描。可通过 `--root`、`--device`、`--db` 指定来源、设备和独立数据库，或重复使用 `--file` 选择根目录内的文件。[命令行入口](../client/Tracker.Worker/Program.cs) · [数据库结构](database.md)

## 开发者复核

核心端到端检查使用 Windows x64、.NET 10 SDK、Python 3.10+ 和支持 Zstandard 的 Node.js 24。在仓库根目录执行以下命令。最后一行使用本机已有的稳定日志，在独立数据库中比较响应与累计快照。可复现的覆盖范围取决于本机样本。

```powershell
.\scripts\build.ps1
python -B tests\test_core.py
python -B scripts\verify-real.py --db .\artifacts\my-verification.sqlite
```

托盘实机探针要求 Codex 在前台，侧栏展开并显示可以匹配的聊天。悬停和滚动探针会暂时移动鼠标或滚动列表。已有的实机结果及待验证环境见[侧栏定位记录](phase-2-uia.md)、[命中测试](phase-2-hit-test.md)及[兼容性登记](phase-2-compatibility.md)。
