# 本地数据库 v2

Tracker 独立使用 SQLite WAL，开启外键和 FULL 同步。默认文件位于 `%LOCALAPPDATA%\CodexTracker\tracker.sqlite`。Worker 通过应用标识 `0x43545831` 识别自己的库。每个迁移单独事务提交，设置 `PRAGMA user_version`；遇到更高版本会停止写入。版本 1 建立统计表，版本 2 增加继承边界及可读的来源记录 ID。[初始 SQL](../database/001-local.sql) · [v2 SQL](../database/002-lineage.sql)

| 对象 | 主键、用途与数据来源 |
| --- | --- |
| Device、Project | 设备 ID 和规范数据根一对一绑定。默认 ID 从本机数据根导出，也可显式指定。Project 仅保留项目 ID、名称和 Session 引用。 |
| Session | 主键为设备 ID 和逻辑线程 ID。运行会话 ID、父线程、分叉来源、继承结束序号/偏移、子代理起始序号各自保存。来源不明时按输入文件隔离归属。 |
| SourceFile | 同设备内相对路径唯一。代际、解压后完整行偏移、物理大小、修改时间、明文检查点首尾摘要及 JSON 上下文用于续读。 |
| Event | 规范化事件版本的 SHA-256 ID、设备、线程、轮次、UTC 毫秒、类别、逻辑键和计量字段。类型涵盖元数据、上下文、响应、快照、压缩、时间转换和模型活动。带响应 ID 的不同观测可共用逻辑键，同时保留字段冲突。 |
| SourceRecord、EventSource | 物理来源键由文件 ID、代际和起始偏移组成。另存结束偏移、原行摘要与事件外键。计量行原文保留在 `raw_metering`，含原有使用量及限额字段；其余选取的事件仅保存统计相关字段。源聊天正文、指令和工具输出不进入数据库。 |
| TokenLedger | 设备、线程和账项 ID 联合唯一。存证据事件 ID、模型/推理/速度、六项 Token、入账方法及可信度。只有可核算的条目进入此表。响应与快照的交叉验证保存在独立表。 |
| Reconciliation | 每个逻辑快照有一个核对状态，可链接到确切的响应证据。重复、回退、基线未知、差异、跨线程匹配可分别查询。 |
| ModelUsage | 按设备、线程、模型、推理及速度实时聚合账本。可选计数只要有未知就返回 NULL。总数来自源 total，缓存与推理作为拆分列。 |
| TimingInterval、PowerGap、TaskTiming | 时间全部是 UTC Unix 毫秒。保存模型工作与暂停区间的并集、可取得的本机睡眠区间，以及线程的首次开始、最后边界、完成、总历时、活跃、已知暂停与未知时长。状态反映最后一次观测。 |
| DataQuality | 保存原因、可信度及能确定的设备/来源/事件。文件损坏和半行按设备、文件代际和偏移定位；统计规则产生的问题按事件定位。 |

Worker 按批次把事件、物理来源、投影和检查点一起提交。进程被终止时，未提交批次由 SQLite 回滚。重复观测不会重复入账。截断、转档或文件消失后保留已采集的历史。数据集合隔离到设备，两个设备镜像目录各自保有完整统计；跨设备合并属于后续同步阶段。

## 常用查询

```sql
SELECT model, input, output, cache_read, cache_write, reasoning, total
FROM ModelUsage WHERE device_id = ? AND thread_id = ?;

SELECT l.entry_id, l.method, o.source_record_id, f.path, o.offset
FROM TokenLedger l JOIN EventSource o ON o.event_id = l.event_id
JOIN SourceFile f ON f.id = o.source_id
WHERE l.device_id = ? AND l.thread_id = ?;

SELECT code, confidence, count(*) FROM DataQuality GROUP BY code, confidence;
```

同一事件可有多条物理来源，按来源联查用于溯源。汇总费用或 Token 时从 TokenLedger 或 ModelUsage 查询，避免因一对多关联重复求和。本机验收数据库含真实会话标识和原始计量元数据，已排除在版本控制之外。
