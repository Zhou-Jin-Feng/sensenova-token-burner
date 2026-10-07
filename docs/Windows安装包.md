# Windows 安装包

## 下载与校验

从 GitHub Release 下载三个文件：

- `SenseNova.TokenBurner-<版本>-win-x64-setup.exe`：安装程序
- `SHA256SUMS.txt`：安装程序的 SHA-256
- `FIRST_USE.md`：首次使用说明（安装后程序目录里有同样内容的 `首次使用.md`）

安装程序未签名。下载后建议在 PowerShell 里核对哈希，与 `SHA256SUMS.txt` 一致再运行：

```powershell
Get-FileHash .\SenseNova.TokenBurner-1.0.0-win-x64-setup.exe -Algorithm SHA256
```

Windows 可能提示“未知发布者”或 SmartScreen 拦截，请确认下载来源后再选择继续。

## 系统要求

- Windows 11 x64（安装程序要求 10.0.22000 及以上）。
- 自带 .NET 10.0.12 运行时，无需另外安装 .NET、Python 或开发工具。
- 运行需要联网和有效的 SenseNova API key。

## 安装、升级与卸载

- 安装：中文向导，仅为当前用户安装，不需要管理员权限。默认目录 `%LOCALAPPDATA%\Programs\SenseNova Token Burner`，会创建开始菜单入口，桌面快捷方式可选。不会设置开机启动、服务或计划任务。
- 升级：先从面板或托盘“退出”正在运行的旧版本（安装程序不会强制关闭它），再运行新安装程序覆盖安装。配置、加密 key 和运行记录都会保留。
- 降级：新版本写入的配置旧版本可能无法读取；如果打算退回旧版本，升级前请先备份用户数据目录。
- 卸载：在 Windows“设置 → 应用”中卸载。用户数据不会被删除；如果需要彻底清除，再手动删除 `%LOCALAPPDATA%\SenseNova.TokenBurner`。

用户数据（`%LOCALAPPDATA%\SenseNova.TokenBurner\User`）和程序目录分开存放。API key 按当前 Windows 用户以 DPAPI 加密，只能在同一台电脑、同一个 Windows 用户下解密；换电脑需要重新添加 key。

## 包内容

自包含多文件发布，不裁剪，包含程序、.NET 运行时、`首次使用.md` 和 `ThirdPartyNotices`（.NET 及 DPAPI 组件的许可说明）。构建脚本会检查并拒绝把配置、凭据、运行记录、日志、调试符号或私有目录打进包里。

## 自行构建

需要 SDK 10.0.401（见 global.json）和 PowerShell 7。在项目根目录执行：

```powershell
& .\scripts\prepare-installer-tool.ps1
& .\scripts\build-installer.ps1 -Version 1.0.0
```

`prepare-installer-tool.ps1` 在忽略目录 `.local/tools` 里准备便携版 Inno Setup 7.1.0：先按官方 GitHub release 的 SHA-256 和 Authenticode 签名核对，便携模式不注册卸载项或快捷方式（[release](https://github.com/jrsoftware/issrc/releases/tag/is-7_1_0)、[许可证](https://github.com/jrsoftware/issrc/blob/is-7_1_0/license.txt)）。

`build-installer.ps1` 以 Release/win-x64/self-contained 发布 Desktop，固定运行时 10.0.12，用各项目的 `packages.win-x64.lock.json` 按锁定模式还原；再用 `installer/windows.iss` 编译安装程序。

输出位于 `.local/releases/<版本>-<UTC 时间>-<随机 ID>/`：

- `publish/`：发布目录；
- `artifacts/`：安装程序、`SHA256SUMS.txt` 和 `首次使用.md`；
- 构建日志；
- `manifest.json`：记录包内文件、字节数、构建时的源码提交，以及工作区是否有未提交改动。

## 已知限制

- 程序和安装程序均未代码签名。
- 只在开发机上验证过；没有在全新的 Windows 系统、其他设备或真实高 DPI 屏幕上做完整安装验收。
- 官方积分的扣减来源和返赠到账以 SenseNova 官方记录为准，客户端无法查询或保证。
