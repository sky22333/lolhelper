# LoLHelper

面向 Windows 10/11 的轻量 LoL 国际服汉化助手。

## 功能

- 一键应用简体中文：写入 `zh_CN` 并锁定配置，防止客户端改回。
- 锁定同时使用**只读属性**与**拒绝写入的 ACL**，两者缺一不可。
- 解除锁定：保留当前语言，允许客户端更新配置。
- 从 Riot 官方 CDN 下载台服安装器。
- 下载完成与启动安装前均验证 Authenticode 签名与 Riot Games 发布者。
- 按签名与发布者识别 Riot 进程，确认后可批量关闭。

## 构建与验证

需要 Windows 与 .NET SDK：

```powershell
dotnet build LoLHelper.csproj -c Release
dotnet run --project Tests\LoLHelper.Tests.csproj -c Release
dotnet run --project Tests\LoLHelper.Tests.csproj -c Release -- --ui-smoke
```

单文件产物：`dist\LoLHelper.exe`。程序要求管理员权限，启动时会显示 UAC。

本工具不是 Riot Games 官方产品，不修改游戏二进制文件，也不绕过 Vanguard。
