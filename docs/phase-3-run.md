# Phase 3 账号与多设备同步候选版

本阶段新增 ASP.NET Core 自托管服务和独立 Windows 同步客户端。Phase 1 Worker 继续写原有 SQLite v2。首次登录后可选择本机线程；同步程序按明确授权的线程导出统计。本机采集和原生聊天列表可以独立离线运行。

## 自托管

**容器部署。** 在安装 Docker Compose 的 Linux 主机上，从源码进入 `deploy/phase3`。准备部署者自己的域名、受信任 TLS 证书、SMTP 发件账号以及以下文件。推荐目录仅部署者可读。容器内运行用户 app 必须能读取挂载的证书与 secret、写入两个持久卷；单机初版使用一个 API 实例。

| 文件 | 内容 |
| --- | --- |
| `secrets/server.pfx`、`secrets/pfx_password` | 对应域名的 RSA PFX（含私钥、签名和密钥加密能力）和口令。样例将该证书同时用于 Kestrel、OpenIddict 和密钥环加密。提前安排续期；替换证书时保留旧私钥与密钥环用于数据恢复或采用换代重授权 |
| `secrets/owner_password`、`secrets/runtime_password` | 两个独立生成的数据库口令，初次建库时设置。日常 API 使用非所有者角色 tracker_api，数据库仅在容器内部网络监听 |
| `secrets/smtp_password` | SMTP 密码；无需 SMTP 认证的私有中继可提供空文件。部署配置默认启用 STARTTLS |
| `.env` | 填写下表所示环境配置，保持 issuer 与代际稳定；用户访问地址必须和 issuer 完全一致 |

```dotenv
TRACKER_ISSUER=https://tracker.example.org:8443
TRACKER_PORT=8443
TRACKER_EPOCH=替换为一次生成并持久保存的UUID
TRACKER_SMTP_HOST=替换为SMTP主机
TRACKER_SMTP_PORT=587
TRACKER_SMTP_FROM=替换为发件邮箱
TRACKER_SMTP_USER=替换为SMTP账号
TRACKER_REGISTRATION=true
```

执行 `docker compose up --build -d`。首次部署按数据库、迁移作业、API 顺序启动。到 `/api/v1/capabilities` 核对 issuer 和协议版本，再访问主页注册并从邮件完成验证。自用账号注册完后可将注册开关改回 false 并重建 API 容器。镜像标签与摘要在 Compose 和 Dockerfile 中锁定。部署者可以独立构建并使用自己的服务器。

**直接运行。** 服务端发布包需要 ASP.NET Core Runtime 10；PostgreSQL 17 已用于集成验证。在目标库中预置迁移角色和独立运行角色，参照 `deploy/phase3/00-runtime-role.sh` 将运行角色所需的表读写、序列读权限设为迁移角色的默认授权。以环境变量或 secret 文件提供下列配置，先用迁移凭据运行 `dotnet Tracker.Server.dll --migrate`，然后用运行角色凭据运行 `dotnet Tracker.Server.dll`。迁移命令支持重复执行。运行角色须无 SUPERUSER、BYPASSRLS 及业务表所有者权限。

| 配置 | 值 |
| --- | --- |
| `ConnectionStrings__Tracker` | 当前步骤使用的 Npgsql 连接串。也可分别提供 `Pg__Host/Database/User/Password` |
| `Tracker__Issuer`、`Tracker__Epoch` | 固定 HTTPS 根地址和恢复代际 UUID |
| `Tracker__Certificate`、`Tracker__CertificatePassword` | PFX 路径与口令 |
| `Tracker__KeyDirectory`、`Tracker__SecurityDirectory` | 独立持久目录，分别存储加密的 Data Protection 密钥环及设备撤销记录；运行角色须可读写 |
| `Tracker__OpenRegistration`、`ASPNETCORE_URLS` | 默认关闭注册；明确设置 Kestrel HTTPS 监听地址，例如 `https://localhost:8443` |
| `Smtp__Host/Port/From/User/Password/StartTls` | 部署者的邮件配置。找回链接、注册验证及设备授权均使用该实例 |

## Windows 端

解压 Windows 同步客户端，安装 x64 .NET 10 Runtime。确认 Phase 1/2 已在该电脑上建立本机统计库。以下 PowerShell 命令使用测试服务域名和示例设备名。首次登录会打开系统浏览器，需要核对设备别名、短码和服务域名后批准。使用私有 CA 时，在登录命令中附加 `--ca C:\certs\tracker-root.pem`，仍会验证主机名。需要手动打开浏览器可用 `--no-browser`。

```powershell
$sync = '.\Tracker.Sync.exe'
$db = Join-Path $env:LOCALAPPDATA 'CodexTracker\tracker.sqlite'
& $sync login 'https://tracker.example.org:8443' '台式机'
& $sync threads $db
& $sync allow $db '替换为本地设备ID' '替换为线程ID'
& $sync collect
& $sync sync
& $sync status
& $sync show event
& $sync devices
```

在另一台 Windows 电脑重复登录与选择来源。多人共用电脑时，先执行 `logout`，用 `--profile 另一个本机配置名` 登录其他账号。来源范围与队列按服务器和账号隔离。同一统计库、设备根和线程的授权在同一状态目录中唯一。默认状态目录为 `%LOCALAPPDATA%\CodexTracker\sync`。可用 `--state` 指定隔离目录用于测试，切勿将状态库放入 Codex 数据根。

网页登录和设备授权使用部署者创建的 Tracker 账号。两台电脑需要访问同一个 HTTPS 地址。设备页列出系统版本、客户端版本、在线状态、最近活动及最近完整同步时间，支持退出全部网页和设备会话。

`watch` 作为独立进程循环采集和同步，Ctrl+C 停止。断网时继续将已授权数据写入 Outbox，恢复后自动补传；`collect` 可单独离线运行。采集、同步和账号管理共享本机状态锁，执行交互管理命令前可先停止当前 watch。退出时先清除本地刷新凭据；远端暂不可达时留下仅能撤销旧 grant 的签名收据，调用 `flush-logout` 或下次登录/同步时补交。设备解绑后，原授权立即失效；要在同一账号下显式转用新设备身份，退出后使用新的 `--profile` 名称加 `--rebind` 重新批准。此操作会重新核对原已选范围与本地编辑记录。

## 项目、任务组与可信度

浏览器访问 `/devices` 重命名或解绑自己的设备。`/dashboard` 查看账号汇总，也可以按人工维护的项目或任务组过滤。相同 Session 跨设备按响应键去重；不同 Session 需显式加入组。冲突和缺乏证据的快照单列，缓存与推理字段不重复加到总 Token。

同步客户端支持 `show project`、`show group`、`show member`、`show conflict`、`show timing`。以下 PowerShell 7 示例离线建立项目、任务组与成员，再同步。每次编辑使用从本地副本读到的版本，创建使用版本 0。

```powershell
$p = [guid]::NewGuid().ToString()
$g = [guid]::NewGuid().ToString()
$m = [guid]::NewGuid().ToString()
& $sync edit project.create $p 0 '{"name":"演示项目"}'
& $sync edit group.create $g 0 (@{name='版本发布';project_id=$p} | ConvertTo-Json -Compress)
& $sync edit member.set $m 0 (@{group_id=$g;thread_id='替换为线程ID'} | ConvertTo-Json -Compress)
& $sync sync
```

同时更名会生成含当前值和提议值的冲突。在最新资源版本上重新提交 `project.update` 等操作，可在 JSON 内携带 `conflict_id`，成功时同一事务关闭对应冲突。选择保留现值可单独执行 `edit conflict.resolve 冲突UUID 冲突版本`。计量矛盾需要来源修订或撤回。所有上行数据从白名单字段构造，原始 JSONL、标题、提示词、源码、计量原文、文件路径和 OpenAI 凭据均无上行字段。

## 备份、回退和恢复

1. 对 PostgreSQL 定期做完整备份，按自定 RPO 配置 WAL 归档。例如 `docker compose exec -T db pg_dump -U tracker_owner -d tracker -Fc -f /tmp/tracker.dump`，再用 `docker compose cp db:/tmp/tracker.dump ./backups/tracker.dump` 取回备份。数据库备份、密钥环、证书私钥与密码分别加密保管；设备撤销日志与摘要另行持久备份，恢复时使用最新可信版本。客户端退出同步进程后备份独立同步目录和本机 Tracker 库，运行中的 SQLite 采用在线备份。
2. 恢复时停接请求，在隔离的新数据库内恢复。例如用 `pg_restore --exit-on-error --no-owner -U tracker_owner -d tracker` 将备份导入空库。保留原实例供核对。为恢复实例设置新 `Tracker__Epoch`，用迁移凭据运行 `dotnet Tracker.Server.dll --recover`。它作废旧授权和已恢复账号的密码、锁定账号，并核验证书外的设备撤销记录。新代际使旧浏览器会话和邮件令牌失效。
3. 如果独立撤销记录缺失，配置新的空安全目录并运行 `--recover-frozen`。所有恢复出的旧设备保持解绑。两种恢复路径均须由运维人员通过独立渠道核实账号与邮箱，然后运行 `dotnet Tracker.Server.dll --release-account 账号UUID 已核实邮箱`。用户经邮件找回密码，重新批准设备；保留原密钥且设备身份仍有效时可复用原来源命名空间。
4. 用运行角色启动恢复后的 API。各客户端退出旧登录，重新登录并 `sync`，用云端快照核对整个本机来源清单（包括已收到 ACK 的观测与墓碑）。查看 `review` 中的本机项目编辑意图，核实后用 `review-apply 原操作UUID 当前版本` 或 `review-dismiss 原操作UUID` 处理。云端来源版本更高且和本机不一致时用 `scopes` 查看冻结记录，核实原库完整后用 `resume-source 范围编号` 恢复差分。已消失的原始事件或替换过的来源文件会中止该源的导出。
5. 校验各账号可归属用量、来源数、待核实记录、设备撤销状态、项目版本及本地游标。只存在于恢复点之后且没有客户端或独立备份的变更属于恢复缺口。生产 RPO/RTO 要以部署者环境实际演练为准。

## 构建与复测

从仓库运行 `scripts/build-phase3.ps1`，产生独立的服务端、Windows 客户端发布目录。端到端测试使用 Python requests、cryptography、aiosmtpd、psycopg[binary]，以及真实 PostgreSQL 的 initdb 和 pg_ctl。设置 `TRACKER_DOTNET` 为 .NET 10 SDK 的 dotnet 路径，`TRACKER_PGBIN` 为 PostgreSQL bin 目录。先对两个项目执行 Release build，再运行 `python -X utf8 -B tests/test_phase3.py`。测试生成临时端口、账号、邮箱、证书和数据库，不使用用户的 Codex 正文或真实账号。

要复测发布产物，在源码根目录先运行 `scripts/build-phase3.ps1`，然后执行以下命令。测试还会调用 Phase 2 已发布的 Windows Worker，可从 `scripts/build-phase2.ps1` 构建。客户端归档位于 `artifacts/phase3/CodexTracker-Sync-Windows-x64.zip`。

```powershell
$env:TRACKER_SERVER_DLL = (Resolve-Path 'artifacts/phase3/server/Tracker.Server.dll').Path
$env:TRACKER_SYNC_DLL = (Resolve-Path 'artifacts/phase3/windows-x64/Tracker.Sync.dll').Path
python -X utf8 -B tests/test_phase3.py
```

当某个已授权库离线或被替换时，`sync` 会先同步其余已确认的差分，退出码 2 表示仍有来源待处理。可用 `scopes` 查看隔离原因；恢复原来的文件后再复核并执行 `resume-source`。最近完整同步时间只在该轮所有来源均可处理时更新。
