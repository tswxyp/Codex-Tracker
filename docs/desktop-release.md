# 发布 Windows 安装包

正式安装版适用于 Windows 11 x64。安装程序将托盘、后台采集器和 .NET 10 Desktop Runtime 一起装到当前用户的 `%LOCALAPPDATA%\CodexTrackerDesktop`。账本、金额和设置使用原有的 `%LOCALAPPDATA%\CodexTracker` 数据目录。1.3.3 或源码版用户先退出旧托盘，再运行安装包一次。

## 发布正式版本

1. 在主分支合入代码，更新变更记录和托盘项目的源码版本号。发布构建以标签中的版本号为准。
2. 创建并推送一个递增的正式标签。例如，下一版若为 1.4.1，在对应提交执行 `git tag v1.4.1` 和 `git push origin v1.4.1`。
3. 在 GitHub Actions 的 **Publish Windows installer** 查看构建记录。工作流下载并校验上一正式版的 Windows x64 完整包，用它生成差分包。发布时上传安装程序、完整更新包、差分包、该版本的更新清单和 SHA-256 校验文件。发布前核对清单与各更新包的版本、大小和哈希。首次发布只生成完整更新包和安装程序及其清单。失败的草稿可以重跑工作流。已公开的版本应使用新版本号修订。

[下载最新 Windows 安装包](https://github.com/tswxyp/Codex-Tracker/releases/latest/download/CodexTracker-Setup-x64.exe) · [正式版本列表](https://github.com/tswxyp/Codex-Tracker/releases) · [发布工作流](../.github/workflows/desktop-release.yml)

如果已有标签没有产生构建记录，在 GitHub Actions 中打开该工作流，点 **Run workflow** 并填写已有的正式标签。手动启动也会从该标签对应的代码构建。

## 用户何时收到更新

安装版在启动约 15 秒后查询 GitHub 的公开正式版本，此后每 6 小时检查一次。查询或下载失败后约 30 分钟重试，也可以在托盘点 **检查更新**。从 1.4.3 升级到 1.4.4 起，本机有可用的基础包且补丁总下载量更小时，更新器会优先下载补丁。缺少可用的补丁、基础包或补丁应用失败时，会自动下载完整更新包。后台下载并校验完成后，在托盘点 **立即更新** 即可重启 Tracker。也可以留到下次启动自动应用。正常提交、修改 README 或发布预览版不会触发客户端升级。此功能无需登录 GitHub。源码构建继续使用手动更新。

## 在本机生成同样的发布文件

在 Windows x64、.NET 10 SDK 和 PowerShell 环境中，于仓库根目录运行以下命令。默认输出到 `artifacts/release/v版本号`，可以传入 `-OutputDirectory` 选择另一个空目录。将上一正式版下载的完整包路径传给 `-PreviousPackage` 即可同时生成差分包，首次打包可省略此参数。安装包只包含构建产物及随附许可。

```powershell
.\scripts\build-installer.ps1 -Version 1.4.4 -PreviousPackage .\CodexTrackerDesktop-1.4.3-win-x64-full.nupkg
```

首发安装包未使用 Authenticode 发布者证书签名，Windows 可能显示未知发布者。可以在 Releases 核对安装包 SHA-256 与 `SHA256SUMS.txt`。
