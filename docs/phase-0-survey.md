# Codex Tracker · Phase 0 只读技术勘察

## 勘察计划与验收标准

1. 确认 Windows 和 Codex 包版本、运行进程及可见窗口。验收依据为包管理器、进程与交互桌面的实际观测。
2. 定位会话、索引与数据库，以只读方式检查结构。按格式、事件类型、字段覆盖及异常分类计数。每条判断注明样本范围与缺口。
3. 对同一组不可变的记录重复核算，验证累计与增量、缓存子集、模型上下文、时间边界和身份关联；区分官方值、估算和未知。
4. 探测 Windows UI Automation 可见的窗口、侧栏、行、边界和身份信息，检查标题映射的歧义，并评估穿透悬浮层与降级路径。
5. 核对参考项目及候选依赖许可，据此提出本地存储、云端同步与 Windows 技术选型，列出进入开发前仍需复测的项目。

证据记录结构为「检查项、方法、观测范围、结果、限制」。数据草案分别记录来源设备、原始会话、事件身份、观测时间、模型上下文、计数、来源与可信度。报告仅保存聚合结果和结构，不收录会话正文、标题、真实标识、凭据或客户数据。

## 结论

**2026-11-18，Windows 11 实机。** 本机可以离线读取逐响应 Token，也可以通过 Windows UI Automation（UIA）定位当前可见的聊天行和当前会话。两个问题会直接影响用户看到的数字。

- **同一次模型响应有两套记录。** 在 967 条逐响应记录中，710 条与两秒内的旧格式快照同时匹配本次及累计的全部六个计数。统计引擎必须选用一个计量来源，并用另一个对账。
- **相同的标题可能指向不同的会话。** 实测 19 个符合聊天行结构的可见按钮，18 个标题在本地候选记录中唯一，1 个对应两个 ID。可以为可靠匹配的行设计原位标签，歧义行应暂缓显示。点击穿透、跨屏与布局变动仍需实机原型验收。
- **当前数据可支持 Token 账，暂不足以形成完整的官方 Credits 账。** 窗口限额、账户余额、官方界面中的聊天估算和账期实际支出各有不同口径。涉及价格或资源占比时，应保留来源、范围和未知项。

## 环境和数据入口

| 检查项 | 本机实测 |
| --- | --- |
| 系统 | Windows 11 Pro 25H2，x64，构建 26200.9974。单显示器 1920×1080，目标窗口 DPI 96，最大化。 |
| Desktop 版本 | 应用包 `OpenAI.Codex` 版本 `26.1111.2228.0`，安装包内应用清单版本 `26.1111.21308`。有窗口的进程为包内的 `ChatGPT.exe`，产品名 Codex。单凭进程名 `codex.exe` 会漏掉主窗口。 |
| Codex 数据根 | 本次进程环境未设置 `CODEX_HOME`，实际使用 `%USERPROFILE%\.codex`。以下相对路径均从该目录起算。 |
| 活动会话目录 | `sessions\` 有 29 个 `.jsonl` 和 1,937 个 `.jsonl.zst`，合计 13.018 GiB。文件按日期分布，单个压缩文件可超过 1 GiB。 |
| 归档目录 | `archived_sessions\` 有 14 个 `.jsonl.zst`，合计 0.394 GiB。移入归档或改为压缩格式后，仍须使用同一个逻辑事件身份。 |
| 标题索引 | `session_index.jsonl` 有 506 条可解析记录，涉及 477 个不同 ID；历史记录中有 67 个标题对应多个 ID。字段为 `id`、`thread_name`、`updated_at`。 |
| 应用诊断 | 本机另有 `%LOCALAPPDATA%\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\Local\Codex\Logs` 和 `logs_2.sqlite`。勘察只列目录及数据库结构；Token 证据来自 rollout。 |

### SQLite 提供哪些证据

| 位置和表 | 读取结果 | 适合用途 |
| --- | --- | --- |
| `state_5.sqlite / threads` | 基库查询返回 1,955 行，均有 `rollout_path`，1,953 行有 `model`，1,940 行有非零 `tokens_used`。另有 `id`、`name`、`title`、归档状态及时间。 | 线程定位、索引补充和交叉核对。历史模型拆分需要逐轮记录。 |
| `state_5.sqlite / thread_spawn_edges` | 869 条边，83 个不同父线程，869 个不同子线程。 | 保留有向父子关系，追溯并行工作。 |
| `sqlite/codex.db / local_thread_catalog` | 489 行，3 个不同 `host_id`，全部有 `display_title`；44 个标题各对应多个线程。 | 使用主机、项目和标题限定候选。Codex 主机 ID 与 Tracker 设备 ID 分开。 |
| `thread_history_1.sqlite / thread_turns` | 一次查询读取到 4,382 轮，其中 3,836 轮有开始时间、4,272 轮有完成时间、4,268 轮有时长。可见完成、运行中、中断及失败状态。 | 核对轮次起止及状态。相邻查询期间 Codex 仍在写入。 |
| 其他内部库 | 确认存在 `codex-dev.db`、会话摘要库、队列库等；`thread_items` 和桌面时间线含正文载荷字段。 | 只使用列白名单。开发库可与正式库重叠，禁止简单并表求和。 |

本次 SQLite 连接使用 URI `mode=ro&immutable=1`，仅用于基库结构和有范围的观测，不创建源库的 WAL 或共享内存文件。运行中部分库有未合并 WAL，因此这些读数不能代表同一时刻的完整在线视图。正式采集以 rollout 为主；需要实时读取内部库时，必须先验证能看到已提交 WAL 的一致只读快照、锁竞争及版本回退。[SQLite URI](https://www.sqlite.org/uri.html) · [WAL 读取条件](https://sqlite.org/wal.html)

## 日志实测与计量

**样本方法。** 同一轮选取全部 29 个明文文件；将普通目录中压缩大小不超过 1 MiB 的 1,680 个文件按相对路径排序，等距选 48 个；归档目录同条件的 12 个全部选取。每个文件最多分析 16 MiB **解压后**前缀，单条超过 2 MiB 就跳过，尾部半行不参与解析。此次观测了 383,670,220 字节，涉及 89 个文件；71 个读至末尾，18 个只读前缀。成功解析 29,314 条记录，跳过 56 条超长行，进入解析的完整行无 JSON 错误。各文件首个已读事件的日期从 7 月 1 日至 11 月 18 日。以下数量是这批样本中的源记录数，不能作为整机账单。

| 检查项 | 实测证据 | 对统计的影响 |
| --- | --- | --- |
| 逐响应记录 | 28 个文件内有 967 条 `token_usage_record`，全部有 `thread_id / session_id / turn_id / root_turn_id / response_id`、外层 `ordinal` 和 `usage / turn_token_usage / thread_token_usage`。967 个 `(thread_id,response_id)` 组合均唯一。 | 优先使用 `usage` 入账；轮次和线程累计值用于校验。相同账号内跨设备使用同一逻辑键。 |
| 旧格式快照 | 83 个文件含有效 `event_msg.token_count`，共 3,847 条，均有 `total_token_usage` 和 `last_token_usage`。102 次连续快照六个累计字段全相同，且这 102 次的 `last` 均为正。 | 旧版不能逐行相加 `last`。有连续、同源基线时取累计差；重复快照贡献为零。首次看到非零累计且起点不明时，保留期初未知量，避免把历史记在当前账期。 |
| 两种表示交叉核验 | 967 条逐响应记录中，有 710 条在同一文件的 ±2 秒内唯一匹配到快照，响应向量等于 `last` 且线程向量等于 `total`。其余记录在这一规则下未匹配。旧式递增样本中也有 2 个五字段差量与 `last` 不一致。 | 配对窗口是本次诊断方法。正式账本按来源、线程、轮次和已覆盖区间对账。缺口或重叠不清时展示待核对量，禁止叠加或仅按时间消重。 |
| 缓存与推理 | 3,847 次累计观测均满足 `total = input + output`、`cached_input ≤ input`、`reasoning_output ≤ output`。六个字段均存在；3,840 次累计缓存读非零，`cache_write_input_tokens` 在本批逐响应及累计记录里均为零。 | 缓存读属于输入，推理属于输出。分别展示，但不再加入总数。保留“观测到零”和“记录缺字段”的区别；非零缓存写的计费归类尚待样本。 |
| 模型、推理与速度 | 1,069 条 `turn_context` 均有 `turn_id / model / effort`，共观察到 8 个模型名称，4 个文件出现多个模型上下文。967 条逐响应记录均可在各自文件内关联到唯一的轮次模型，覆盖 6 个模型。旧版快照中有 161 条没有同一元数据段的前置模型上下文。抽查的用量、轮次及其设置未给出可用的速度档位。 | 按响应所属线程、轮次和模型拆账。上下文推断注明来源；无法确定的模型或速度保留 `UNKNOWN`。 |
| 分叉、继承与子代理 | 98 条会话元数据中，46 条 `id` 与 `session_id` 不同，19 条有 `forked_from_id`，13 条有 `history_base.thread_id` 和结束边界。45 条有父线程的元数据，其 `id` 与文件名相符，而 `session_id` 不符。502 条旧式快照处于元数据 ID 与当前物理文件名不符的段。 | 将物理文件、逻辑线程与运行会话分开。对子代理保留独立用量，对继承段作来源追溯。仅靠文件名、目录、标题或共享的 `session_id` 都不足以判定归属。 |
| 来源回查 | 另一次定向检查中，438 个响应引用了与物理文件名不同的 4 个线程。找到 4 个同 ID 文件，在有限前缀内未复现相同响应。 | ID 不同只说明来源需要核对，不能据此删除数据或把它全部计入父任务。保留原始归属证据和未归属池。 |
| 重扫与压缩 | 对同一批固定读取边界扫描两次，聚合计数和 3,847 个快照来源键的集合摘要一致，第二次新增来源键为 0。样本含 23 条 `compacted`；补查中有 13 个可比较的压缩边界，边界前后总计相同。 | 将上下文压缩与计数器归零分别处理。此测试验证只读重放；持久化、崩溃重试、转档和真实双机同步仍需后续测试。 |

### 耗时的证据边界

读取到 1,057 次 `task_started`、1,023 次 `task_complete`、11 次 `turn_aborted`。1,016 个完成事件有 `duration_ms`；994 个同时有起止时间和时长，其中 993 个与时间差相差不超过 1 秒。完成事件指向一轮；后续继续同一聊天时，会话总历时可以继续增长。

总历时采用首次开始至最终完成的跨度，运行中单独显示截至最后观测的跨度。实际执行时间采用可证实执行区间的并集，父子并行只占用一次墙钟时间；人工计时独立保存。休眠、网络等待及退出原因在历史日志中经常不可区分，不能把整轮墙钟时长当成活跃时长。后续常驻客户端可记录挂起、恢复、进程退出和心跳，以事件闭合区间；历史空档标记为未知，带阈值的推算标记为估算。暂停时长和未知时长分别保留。

## Credits、重置与成本可得性

| 数据 | 可用来源与判定 |
| --- | --- |
| 限额与余额 | 3,847 个快照均有 `rate_limits.primary.used_percent / window_minutes / resets_at`；3,817 个有账户 `credits` 对象，3,751 个有非空 `balance`。这些字段描述账号或限额窗口。样本未发现带金额、账期及线程归属的官方实扣事件。 |
| 官方支持的接口 | Codex App Server 文档列出 `thread/list`、`thread/read`、`thread/tokenUsage/updated`、`account/rateLimits/read`。可在受支持的自有连接中验证字段与覆盖范围。此次未建立连接，第三方能否旁听当前 Desktop 私有连接的事件尚无证据。[App Server 文档](https://developers.openai.com/codex/app-server/) |
| 官方界面 | 官方帮助说明，适用账号可以在聊天标题栏查看生命周期 Credits、模型、推理和速度。官方将该聊天视图描述为估算，可能遗漏子代理、工具和后台工作。可用于人工对账；应注明“官方界面估算 / 生命周期”，不能直接填入“本账期官方实扣”。[官方用量说明](https://help.openai.com/en/articles/20001478-reviewing-work-and-codex-usage-and-using-personal-analytics-in-chatgpt-desktop) |
| Global Reset | 样本未发现明确标识 Global Reset 的用量事件。官方说明免费自动重置对获选账号直接生效，银行式重置的历史也不列出 Global Reset。仅凭下次刷新时间变化或余额跳变，原因记 `UNKNOWN`。可手工记录来源及前后额度，确认为 Global 后现金支出为 0，原 Billing ID 延续。[官方重置说明](https://help.openai.com/en/articles/20001498-how-resets-work-in-codex) |
| API 等效成本 | 按模型、日期、速度及互斥计价类别查版本化价格表，缺少价格或适用关系的部分保留 `UNKNOWN`。订阅支出采用账单实付记录；API 等效值只作比较。[公开 API 价格](https://developers.openai.com/api/docs/pricing) |

首屏百分比应写明“本账期资源占比”。在账号、账期和所有设备范围一致、覆盖完整时，优先采用实扣 Credits 比例。缺实扣时，整期采用同一版本权重计算“任务加权 Credits ÷ 全部任务加权 Credits”，标记 `ESTIMATED`。包括未归属项目和其他已绑定设备；分母为零、权重未知或设备仍欠同步时，显示不可计算或明确标为本机暂计。全生命周期数不能直接落到当前账期。

## Windows 原生侧栏勘察

1. **窗口与行。** 从应用包内主窗口句柄取得 `Chrome_WidgetWin_1`，通过 UIA 枚举到 Chromium 无障碍树。一次快照有 701 个后代节点、109 个 ListItem、318 个 Button。项目组按钮支持 ExpandCollapse，聊天行按钮支持 Invoke。用 ListItem → Group → Button 结构、侧栏列表祖先、可见性及边界交叉识别，避开同名项目组。
2. **标题与当前会话。** 本次 19 个符合该结构的可见聊天按钮均为 324×30 物理像素。按精确标题与索引、正式目录及线程名交叉匹配，18 个唯一，1 个有两个候选。仅凭标题唯一仍属候选映射，宜结合主机及项目验证。被测侧栏按钮未暴露可核对的线程 UUID。原生 COM UIA 读到恰好 1 个可见按钮具有 `AriaProperties.current=page`，且其标题唯一；标准 SelectionItem 没有给出选中行。
3. **滚动、稳定性。** 枚举可见容器发现 1 个可纵向滚动的 ScrollPattern。相隔 0.7 秒的两次静态采样中，候选按钮的边界和 ARIA 属性集合一致。勘察未滚动侧栏、切换会话或改变窗口。滚动偏移、裁切、虚拟列表复用及更新后的恢复仍需动态测试。
4. **覆盖层的空间限制。** 行高约 30 像素，外部窗口无法为原列表增加行距。只有确认存在空白区域时才放紧凑统计标签，完整模型、时间、成本及明细可放在悬停窗中。无法安全容纳标签时，采用与侧栏相邻的独立浮面板，并显示降级原因。示意图中的多行排版尚不能视为原样可实现。
5. **命中测试。** 提议用不激活的分层窗口，采用 Windows 的 `WS_EX_LAYERED / WS_EX_TRANSPARENT / WS_EX_NOACTIVATE`，以屏幕光标所在的原始行矩形触发详情，保持标签本身穿透。只在目标前台窗口可见且行实际位于视口时显示。监听结构、窗口、滚动及 DPI 变化，重取元素和屏幕物理边界；应用切换或定位失效即隐藏。本阶段未创建覆盖窗口，鼠标命中、主题、混合 DPI 均待实测。[Windows 分层窗口](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features) · [高 DPI](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows)

**降级次序。** 先查标准 UIA 控件和模式，再查原生 UIA 的 ARIA 属性。此次后者解决了当前行识别。若未来版本缺少这些属性，可验证 MSAA/IAccessible 的可访问状态与边界；该接口此次未经实测。仍无法确认身份或区域时，保留独立面板和人工关联。窗口坐标、CSS 类名、焦点和列表序号都不能单独充当身份。[UIA 元素属性](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-automation-element-propids)

## 参考项目与依赖许可

以下检查来自实际读取的上游许可文本与仓库树。此次未复制代码、引入依赖或发布二进制。

| 参考项目 | 许可及使用决定 |
| --- | --- |
| TokenTracker | 根许可 MIT，可参考 rollout、压缩档和统计入口。Windows 工程声明了 Microsoft.Web.WebView2；将来若复用，还要核查具体 SDK 和 Runtime 分发条款。本阶段采用 Windows 原生方案，无需引入该依赖。[固定版本许可](https://github.com/xiufengsun/TokenTracker/blob/87e8d3bdf642de312417821ebf1036c0f5f91fe7/LICENSE) |
| agent-pulse | GitHub 返回 `NOASSERTION`。正文采用 AGPLv3 与单独商业授权双轨，无法把其实现作为本项目自有 MIT 代码直接发布。仅参考功能设计，采集与同步独立实现。[固定版本许可](https://github.com/Dipen-Dedania/agent-pulse/blob/d822947d78e4c13f7db267676a18982dbe52f2f2/LICENSE.md) |
| CharaDock | GitHub 返回 `NOASSERTION`，正文为 Apache-2.0。复用代码需保留许可、适用声明及修改说明；仓库另有 SIL OFL 1.1 字体和 MIT 子组件。仅参考交互行为。[根许可](https://github.com/ochisamu/CharaDock/blob/9e37f5cf7b7a3e1b5bddfc0314fb65b4f4f4d4d5/LICENSE) · [字体](https://github.com/ochisamu/CharaDock/blob/9e37f5cf7b7a3e1b5bddfc0314fb65b4f4f4d4d5/assets/fonts/LICENSE-NotoSansJP.txt) |
| codex-statusbar | 根许可及其单列的上游 overlay 许可均为 MIT，版权人不同。如复用相关部分需同时保留适用声明。可参考 Windows 状态栏与悬浮交互。[根许可](https://github.com/dongyue-19/codex-statusbar/blob/17f0ddd25512b9a823b56a37da325d36ca53971f/LICENSE) · [上游声明](https://github.com/dongyue-19/codex-statusbar/blob/17f0ddd25512b9a823b56a37da325d36ca53971f/LICENSE-codex-token-overlay) |

候选栈已核对上游条款：WPF、ASP.NET Core、Microsoft.Data.Sqlite 为 MIT；SQLite 核心为公有领域；SQLitePCLRaw 为 Apache-2.0；PostgreSQL 与 Npgsql 采用 PostgreSQL License；ZstdSharp 项目及包声明为 MIT，Zstandard 上游提供 BSD 许可。实施时须锁定版本，核查实际恢复的传递依赖、原生二进制、运行时及安装包的第三方声明。本次结果是选型检查，发布依赖树审计尚未发生。[WPF](https://github.com/dotnet/wpf/blob/main/LICENSE.TXT) · [ASP.NET Core](https://github.com/dotnet/aspnetcore/blob/main/LICENSE.txt) · [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore/blob/main/LICENSE.txt) · [SQLite](https://sqlite.org/copyright.html) · [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw/blob/main/LICENSE.TXT) · [PostgreSQL](https://www.postgresql.org/about/licence/) · [Npgsql](https://github.com/npgsql/npgsql/blob/main/LICENSE) · [ZstdSharp](https://github.com/oleg-st/ZstdSharp/blob/master/LICENSE) · [Zstandard](https://github.com/meta/zstd/blob/dev/LICENSE)

## 阶段验收与复核

| 验收项 | 本次结果 | 下一次需要验证的边界 |
| --- | --- | --- |
| 环境、格式、来源可追溯 | 包和清单版本、目录、SQLite 表、两种日志格式及归档均已只读确认 | 同时打开多个 Codex 窗口、自定义数据根和不同版本 |
| Token、模型、缓存可核对 | 真实样本双读一致；逐响应与旧快照有实证重叠；多模型及分叉可识别 | 原子落库和重启、损坏或截断后续写、转档重放、累计下降、非零缓存写、跨设备同事件 |
| 历时和活跃时间分开 | 起止和轮次时长可用；中断及缺字段可见 | 睡眠、离线、重启、长等待和并行区间合并 |
| 原生会话列表可定位 | 当前布局动态提供标题、行矩形、滚动容器及当前行。稳定 ID、歧义消解与标签空间有边界 | Windows 10/11、缩放 100%/125%/150%、多屏、滚动、折叠、主题、最大化/还原，以及每个原生按钮的点击穿透 |
| 云端、费用与开源路径 | 自托管方案与数据结构已形成；官方实际支出、速度和 Global Reset 的证据缺口已记录 | 两台设备、冲突与恢复、真实账单与价格、发布许可清单及安装包 |

复核时先记录版本、样本大小边界及时间，再按上述选样和计数口径重读；UIA 依次查主窗口、列表、按钮及 ARIA 属性。活跃机器的条数会变化，应比较同一冻结样本和同一布局。全程仅将文档写到项目工作区，未启动采集器、构建覆盖层、连接自托管服务或改动 Codex 配置。技术选型、表结构和下一阶段准入条件见[架构决策](../ARCHITECTURE.md)。
