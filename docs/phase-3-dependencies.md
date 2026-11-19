# Phase 3 第三方依赖

下表由两个发布项目的锁文件与实际还原的 NuGet 包元数据核对。Tracker 自有代码沿用 MIT。各包保留上游 MIT、Apache-2.0 或 PostgreSQL 许可及版权声明。服务端发布随附上游许可和包内第三方 notices；Windows 同步客户端使用操作系统自带的 SQLite 与当前 Windows 用户的 DPAPI。

| 包 | 版本 | 上游许可 |
| --- | --- | --- |
| Microsoft.AspNetCore.Identity.EntityFrameworkCore | 10.0.15 | MIT |
| Microsoft.AspNetCore.Identity.UI | 10.0.15 | MIT |
| Microsoft.Bcl.Cryptography | 10.0.2 | MIT |
| Microsoft.Data.Sqlite.Core | 10.0.15 | MIT |
| Microsoft.EntityFrameworkCore | 10.0.15 | MIT |
| Microsoft.EntityFrameworkCore.Abstractions | 10.0.15 | MIT |
| Microsoft.EntityFrameworkCore.Analyzers | 10.0.15 | MIT |
| Microsoft.EntityFrameworkCore.Relational | 10.0.15 | MIT |
| Microsoft.Extensions.FileProviders.Embedded | 10.0.15 | MIT |
| Microsoft.IdentityModel.Abstractions | 8.23.0 | MIT |
| Microsoft.IdentityModel.JsonWebTokens | 8.23.0 | MIT |
| Microsoft.IdentityModel.Logging | 8.23.0 | MIT |
| Microsoft.IdentityModel.Protocols | 8.23.0 | MIT |
| Microsoft.IdentityModel.Tokens | 8.23.0 | MIT |
| Npgsql | 10.0.3 | PostgreSQL |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | PostgreSQL |
| OpenIddict.Abstractions | 7.9.0 | Apache-2.0 |
| OpenIddict.Core | 7.9.0 | Apache-2.0 |
| OpenIddict.EntityFrameworkCore | 7.9.0 | Apache-2.0 |
| OpenIddict.EntityFrameworkCore.Models | 7.9.0 | Apache-2.0 |
| OpenIddict.Server | 7.9.0 | Apache-2.0 |
| OpenIddict.Server.AspNetCore | 7.9.0 | Apache-2.0 |
| OpenIddict.Validation | 7.9.0 | Apache-2.0 |
| OpenIddict.Validation.AspNetCore | 7.9.0 | Apache-2.0 |
| OpenIddict.Validation.ServerIntegration | 7.9.0 | Apache-2.0 |
| SQLitePCLRaw.core | 2.1.13 | Apache-2.0 |
| SQLitePCLRaw.provider.winsqlite3 | 2.1.11 | Apache-2.0 |
| System.Security.Cryptography.ProtectedData | 10.0.15 | MIT |

[服务器依赖锁](../server/Tracker.Server/packages.lock.json) · [Windows 客户端依赖锁](../client/Tracker.Sync/packages.lock.json) · [本阶段许可文本](licenses/phase3) · [SQLitePCLRaw 许可](licenses/SQLitePCLRaw-Apache-2.0.txt)

测试在隔离目录使用 PostgreSQL 17.12 的 Zonky Windows 二进制、psycopg、requests、cryptography、aiosmtpd。上述测试工具均不打入 Tracker 客户端或 API 发布包。自托管 Compose 使用官方 PostgreSQL 与 Microsoft .NET 镜像。
