# Phase 3 HTTPS API、账号与设备安全（设计稿）

服务器地址由用户指定。API v1 仅接受 HTTPS，issuer 与首次确认的服务实例绑定。浏览器负责注册、登录、改密、找回和设备批准；Windows 客户端使用操作系统浏览器，不接触账号密码。账号页面和协议端点由同一自托管实例提供。[总计划](phase-3-plan.md) · [数据库与同步协议](phase-3-data-sync.md)

## Identity 评估与账号生命周期

建议采用 ASP.NET Core Identity 管理用户、邮箱、密码哈希、安全戳、验证/找回令牌、锁定和浏览器会话。桌面授权另选支持 OAuth 2.0 Device Authorization Grant 的开源实现，首选评估 OpenIddict 的服务器、令牌存储、撤销和版本支持。采用此流程后客户端无共享秘密，也不用监听本地回调端口。代价是首次绑定需要一次浏览器确认及短时轮询。Identity 自带账号 API 的 bearer token 不直接承担设备授权协议。开发前须用拟锁定版本验证设备流、刷新轮换/重用检测和许可证，设备归属与即时撤销由业务授权层保证。[ASP.NET Core Identity](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity?view=aspnetcore-10.0) · [OpenIddict 文档](https://documentation.openiddict.com/) · [RFC 8628](https://www.rfc-editor.org/rfc/rfc8628)

| 操作 | 流程和可观察结果 |
| --- | --- |
| 注册与验证 | 以邮箱注册，服务端规范化并保证唯一，邮件发送短期、单用途验证链接；验证完成后才可批准设备。注册/找回对已有及未知邮箱统一响应，按来源和账号限流。用户名可选，支持查看和修改本人资料；换邮箱需近期重新认证并验证新地址。 |
| 登录 | 服务端用 Identity 版本化 PasswordHasher（PBKDF2、独立随机盐、可升级工作因子）验证。部署前按机器校准开销；成功时按需要重哈希。建议初始密码策略为至少 15 个字符、支持长口令和密码管理器、允许至少 64 字符、不静默截断。失败退避、限流及限时锁定，日志只存结果。 |
| 浏览器会话 | Cookie 设 Secure、HttpOnly、适合顶层授权导航的 SameSite，设置空闲及绝对时限。表单使用防伪令牌，改密、解绑、恢复绑定等敏感操作要求近期重新验证。退出网页销毁浏览器会话；界面另提供“退出此设备”和“退出所有设备”。 |
| 更改密码 | 已登录且验证当前密码后更新哈希/安全戳，撤销已有浏览器会话和设备令牌族，要求重新登录授权；安全变更通知投递已验证邮箱。 |
| 找回密码 | 请求统一返回 202。通过预配置 HTTPS 域名发送建议 15 分钟过期的一次性链接；存储用途/失效状态，原子消费，成功后换安全戳并撤销全部会话/设备授权。有效链接及明文令牌禁止进入访问日志、Referer 或第三方资源。 |
| 桌面退出及账号切换 | 立即停止旧分区上传/下载，取消在途回调对新账号的写入，清除活动凭据并锁定旧缓存。在线撤销该 grant；离线退出只保留受 Windows 保护、仅能撤销旧授权的一次性撤销句柄，重连优先送达；服务端确认前界面显示“远端退出待确认”。切换时新 issuer/sub 建独立分区和空范围。其他设备的授权由各自持有人控制。 |

## 一台电脑如何成为授权设备

1. 客户端校验部署地址和 TLS，读取协议元数据；首次绑定生成随机候选 Device ID 与 Windows 当前用户保护的绑定密钥。建议用 CNG 的不可导出非对称密钥，公钥仅作设备绑定证明。注册时关联到最终批准的账号；已有分区的恢复申请须同时证明原密钥持有。设备名初始使用用户确认的别名，可在注册后更名。
2. 客户端通过 device authorization endpoint 请求所需 scope，得到 device_code、user_code、到期时间、验证 URI 与轮询间隔；device_code 仅留本机短期内存。打开部署者的浏览器验证页。用户核对服务域名、显示的短码、设备别名/OS 和所申请权限，登录后显式批准或拒绝。设备 ID 碰撞不会使新机器继承历史授权。
3. 客户端按协议轮询 token endpoint；处理 authorization_pending、slow_down、access_denied、expired_token。批准码一次消费，批准后绑定账号、设备、公钥和 grant。服务端在签发首次授权前验证挑战签名（绑定 grant、issuer、nonce 和有效期，nonce 仅消费一次）。签发短期不透明访问令牌及轮换刷新令牌。建议访问 10 分钟、刷新闲置 30 天、最长 90 天，均由自托管端配置。刷新时以成熟库管理原子轮换和重用检测；并发刷新在客户端串行化，丢失新令牌且无法安全恢复时重新授权。
4. API 每次从已校验令牌得出 sub、device_id、grant_id、audience、scope 和 epoch，再核对当前设备/授权/账号状态。只有当前账号与当前设备能新增该来源；拉取可见同账号已获准汇总数据。访问令牌放内存，持久刷新材料按 Windows 用户和 issuer/sub/device 隔离，非当前分区不能自动解锁并上传。
5. 在已登录浏览器中可重命名或解绑任一自己的设备。解绑在一个事务撤销该设备全部 grant 和刷新族、将设备标为 revoked，并写入安全审计。之后即使访问令牌尚未过期，push、pull、快照、heartbeat 和 refresh 也会拒绝；重新授权需要用户在浏览器重新确认。历史账项与来源保留。普通重新登录沿用可验证的原设备身份；正式解绑后再次配对采用新 Device ID。
6. 客户端建议活动时每 60 秒发送一次心跳。服务端以自己的 UTC 时间更新 Last Active；已授权设备在最近 3 分钟内有成功认证活动则显示 Online，超时显示 Offline，撤销优先显示“已解绑”。Last Sync 仅在所有本轮允许上传的操作均得到持久回执、且拉取已处理到本轮固定上界后，经完成端点确认才更新。无新事件的成功同步可以更新时间；失败或单独心跳不能更新。Registration Time 和设备列表由服务端提供。

## 拟议端点

路径相对该实例。Cookie 页面可按 UI 国际化，OAuth 地址从元数据发现。桌面写操作返回持久结果或可重试的标准错误。分页令牌签名并绑定账号/授权、代际、查询及有效期；访问前仍检查当前授权。

| 方法与路径 | 调用身份 | 输入、输出与语义 |
| --- | --- | --- |
| GET /.well-known/oauth-authorization-server；GET /api/v1/capabilities | 匿名；只读 | issuer、认证端点、协议版本、最大批量/字节、支持的 DTO 和 API 地址；注册端不向客户端下发可绕过 TLS 验证的参数 |
| POST /account/register、/account/login、/account/logout；POST /account/verify-email | 浏览器表单 | 注册/验证/登录/退出；成功建立或撤销 cookie；提示不泄露账号是否存在 |
| GET/PATCH /api/v1/me；POST /account/change-password、/account/change-email、/account/forgot-password、/account/reset-password、/account/logout-all | 本人浏览器；找回申请和令牌消费允许匿名 | 本人资料、邮件/密码生命周期；更改资料按 ETag 校验版本；所有 cookie 写操作防 CSRF |
| POST /connect/deviceauthorization；GET/POST /connect/verify；POST /connect/token | 桌面公开客户端；批准为已验证浏览器会话 | RFC 8628 的发起、确认与设备码交换；token 端点也支持 refresh_token 轮换；绑定挑战随授权事务传递 |
| POST /connect/revocation；POST /api/v1/device-logout | 当前授权持有人 / 单用撤销句柄 | 标准撤销 refresh token，或用只能指向该 grant 的句柄完成离线退出；重复调用幂等 |
| GET /api/v1/devices；PATCH /api/v1/devices/{id}；DELETE /api/v1/devices/{id} | 本人浏览器；本人设备凭 devices.read 可读 | 列表含 device_id、name、os、client_version、registered_at、last_sync、last_active、status；改名与解绑由浏览器执行，带 If-Match。已解绑重复请求给出既有状态 |
| POST /api/v1/devices/self/heartbeat | 活动设备 devices.self | 仅允许 OS 系列和受限版本号，设备从令牌识别；204，更新服务端 Last Active |
| GET /api/v1/sync/state | 活动设备 sync.read | 返回 epoch、head、最早可拉取游标、协商版本；账号身份来自令牌 |
| POST /api/v1/sync/push | 活动设备 sync.write | epoch、operations[]；200 返回逐 op 持久结果或逐项错误（含 op_id、状态、版本/seq）。设备/账号主体不可由请求体指定 |
| GET /api/v1/sync/changes?epoch=…&after=…&until=…&limit=… | 活动设备 sync.read | 响应含有序变更组、until、next、has_more；首页省略 until；陈旧游标返回 410 并提供快照入口 |
| POST /api/v1/sync/snapshots；GET /api/v1/sync/snapshots/{id}/pages/{n} | 活动设备 sync.read | 创建冻结快照票据并返回 epoch、watermark、页数、页摘要/总摘要；每页可重取；只含该账号的白名单副本和当前设备的来源恢复清单 |
| POST /api/v1/sync/complete | 活动设备 sync.write | 上传/拉取一轮完成后提交 epoch、已落盘游标及本轮 until；服务验证代际与水位范围，记录本设备 Last Sync。客户端仅在本轮无待重试/停放的失败操作时调用 |
| GET /api/v1/projects、/api/v1/task-groups、/api/v1/conflicts | 活动设备 sync.read 或本人浏览器 | 按签名游标分页，资源带 version；冲突只返回本账号受限字段和证据引用，反向引用同样验权 |

项目、组、成员和冲突的离线写入统一走 push，操作类型为 project.create/update/delete、group.create/update/delete、member.set/remove、conflict.resolve。创建用客户端随机对象 ID、base_version=0；修改/删除须提供读取时的 base_version、op_id 和明确允许变更的字段。版本匹配则加一；陈旧操作持久保存双方值和版本，返回 recorded_conflict 与 conflict_id，其他操作仍可处理。处理人可选择保留当前值、接受提议或明确输入新值，针对当前版本提交新 op；同时编辑解决方案仍会触发版本校验。统计归属冲突只能依据新的可核验证据重投影或保留待核实，项目管理员的重命名不能提升 Token 可信度。

建议每批至多 200 个 op、未压缩请求 512 KiB，每个拉取页至多 200 个提交组，单组过大改发快照重置通知。HTTP 400/422 对应无效格式/字段、413 超限可拆批、401 过期可尝试刷新、403 已撤销或 scope 不足、404 资源不存在或跨账号不可见、409 代际/修订冲突、410 游标或票据过期、429/503 可按 Retry-After 重试。设备流 OAuth 错误遵循协议；同步域整批错误使用 Problem Details（type、status、枚举 code、内部 trace_id）。批内返回的错误按相同分类处理，不回显被拒绝的输入。握手发现无共同协议时停传保留 Outbox。

## 上行、下行及日志的数据边界

| 通道 | 允许的字段 | 限制 |
| --- | --- | --- |
| 计量、核对与计时 | 源 Session/线程/轮次/响应等结构化标识、父子依据、类型、模型、UTC 时间和区间、六项计数、核对所需前驱/累计向量与序号、规则版本、可信度及原因枚举 | 每类 DTO 单独定义可选与必填字段；ID 必须来自已知结构标识字段、长度受限；文件路径派生的未知身份只在本地作内部映射，禁止直接上传。模型用受支持标识目录；自由文本及无法识别模型归 UNKNOWN。未知原因只传 OTHER，不附原值。 |
| 用户维护的资料 | 人工输入并确认同步的项目名、任务组名和其 ID/版本/关联 | 名称不预填聊天标题，限制长度，按普通文本显示；不得附备注、代码、客户合同或本机路径。 |
| 服务控制面 | 注册邮箱/可选账号名、密码仅交给自托管认证端点；设备别名、OS、客户端版本、心跳与同步时间；访问/刷新凭证仅用于相应 Tracker 授权端点 | 客户端不会读取或发送 OpenAI 凭据；设备名可自订，不需 MAC、硬件序列号、Windows 用户名或计算机清单。 |
| 审计与诊断 | 操作类型、计数、耗时、状态码、内部追踪 ID、必要的账号/设备内部 ID | 默认关闭代理/API/邮件链路的正文与查询串日志；禁止记录密码、令牌、邮件链接、名称、原始事件、完整请求及被拒绝内容。可配置限期和访问角色。 |

白名单从字段构造 DTO，缺省拒绝未知键及嵌套扩展。禁止直接序列化 v2 的 Event.data、SourceRecord.raw_metering、SourceFile.context、DataQuality.detail 或任何原始记录。提示词、模型回复、默认聊天标题、源码、工具内容、原始 Codex JSONL、SQLite 文件和 OpenAI 密钥不得进入请求、冲突附件、同步日志或导出备份。客户端仅向已确认 issuer 的受信任端点发送凭据，域名变更按新服务重新绑定。

## 威胁与验收证据

| 风险与影响 | 控制 | 验证 |
| --- | --- | --- |
| 租户或设备 ID 混用，泄露或污染他人统计 | 认证上下文确定账号；复合外键、每条查询的账号条件及 PostgreSQL FORCE ROW LEVEL SECURITY 双重约束。事务内 SET LOCAL 可信账号；池化连接不保持租户上下文。运行角色非表所有者，无 BYPASSRLS；后台作业显式按租户开事务 | 双账号碰撞资源 ID，覆盖直查、写、分页、关系、快照与复用连接；在应用过滤缺失的测试查询中仍只见本账号 |
| 密码泄漏、撞库、找回链接滥用 | Identity 哈希与安全戳、按账号/IP 限流、统一响应、单用限时邮件链接、HTTPS 与可信代理 | 校验存储形式、失败锁定、链接重用/到期、改密后旧 Cookie 及设备令牌失效 |
| 诱导批准、截获刷新材料、解绑后继续写入 | 核对域名/短码和设备，短时授权及限速、绑定挑战、受保护的刷新材料、轮换与重用检测、即时校验设备状态。访问令牌在失窃且未撤销的有效期内仍属持有人凭据 | 拒绝/过期/重复批准、令牌轮换重用、撤销与并发事务顺序、旧令牌的读写/心跳访问 |
| 跨站浏览器操作或不受信任代理 | CSRF 防伪令牌、受限 CORS、精确 issuer/audience、可信转发代理、固定域名邮件及授权页 | 无防伪令牌的管理操作被拒，伪 Host 不改写邮件/批准目标 |
| 重放、矛盾计量、滥用已授权设备 | op 回执、来源修订及规范键三层约束；同 ID 异值隔离，大小/频率限制。获授权设备仍可能上报伪造计量，保留来源与可撤销授权；可信度描述证据质量 | 重发与冲突账项可复算；限制生效；隔离项不增加可归属总量 |
| 经默认标题、未知字段、异常日志泄密 | 严格 DTO、客户端和 API 双侧校验、错误代码枚举、日志最小化、备份加密 | 在每种禁止字段放不同哨兵；检查请求、数据库、冲突表、代理/API 日志、导出与还原备份 |
| 备份回滚使旧授权复活或项目编辑被覆盖 | 独立安全撤销记录、恢复换代、强制重授权、对账含已确认来源和编辑冲突 | 恢复到撤销之前，旧凭证仍被拒；不同步恢复前旧编辑，保留人工确认 |
| 部署者、API 主机或本机登录用户被控制 | TLS、最小权限、密钥/备份分权、更新依赖及审计。部署者能读取其 PostgreSQL 中允许同步的统计；已控制 API 的进程能设置租户上下文，当前 Windows 用户能读取自己的本地数据。将这些信任边界写入部署和授权说明 | 固定到 U 的普通请求无法读取 V 的行；运行角色无法改 schema，API 主机无法读取离机备份解密密钥；检查审计和恢复访问控制 |

[自动化验收设计](phase-3-acceptance.md)
