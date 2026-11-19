# 本地客户端第三方组件

项目自产代码使用根目录的 MIT License。Windows 可运行目录随附本页和 `licenses/` 下的上游许可文本。

| 实际恢复的组件 | 许可和声明 |
| --- | --- |
| Microsoft.Data.Sqlite.Core 10.0.9 | MIT；© Microsoft Corporation，保留 .NET Foundation 及贡献者声明。 |
| SQLitePCLRaw.core、SQLitePCLRaw.provider.winsqlite3 2.1.11 | Apache-2.0；Copyright 2014–2024 SourceGear, LLC。 |
| Interop.UIAutomationClient 10.19041.0 | MIT，Copyright (c) 2019 Roman。随包保留上游 LICENSE.txt，调用 Windows 内置 UI Automation。 |
| ZstdSharp.Port 0.8.8 | MIT；Copyright Oleg Stepanischev。附 Zstandard 上游 BSD 声明。 |

版本、传递依赖和包内容哈希见各项目的 `packages.lock.json`。生产客户端调用 Windows 自带的 `winsqlite3.dll`，使用单独安装的 .NET 10 Desktop Runtime。Python 和 Node.js 仅供提取脱敏夹具、独立核验和自动测试使用。
