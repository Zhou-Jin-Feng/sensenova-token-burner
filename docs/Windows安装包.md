# Windows预览安装包

更新：2026-10-06。新版多key MVP候选为0.2.0-preview.1，准备状态另见末节；自带.NET10.0.12运行时。当前GitHub公开仅旧单key[v0.1.0-preview.1](https://github.com/Zhou-Jin-Feng/sensenova-token-burner/releases/tag/v0.1.0-preview.1)。旧preview.2候选35.60MiB未提交/发布，不能当新版下载。下文保留历史安装与校准记录。

新版候选自包含发布及安装器编译成功：Setup 37,347,879字节（35.62MiB），安装文件117.70MiB，SHA-256 `5541702d2870a8bc96864825f39cbd61736ec729fa749c130ed7aea6ea11d731`。版本0.2.0-preview.1含多key/备注、独立目标计划、全局并发、额度组、剩余目标恢复及通知；新使用说明同步随包。不含tasks/settings/run-state/凭据文件、agent/.local或调试符号。安装/升级/真实key未执行，新包未推送或发布；源码验收见[新版MVP验收](新版MVP验收.md)。

新版包括多key/备注、独立配置、共享并发、额度组、剩余恢复和完成通知，详见[新版MVP范围](新版MVP范围.md)。旧首版范围保留作历史，使用步骤见[首次使用](首次使用.md)。

## 安装与用户数据

双击Setup按中文向导安装，默认目录为`%LOCALAPPDATA%\Programs\SenseNova Token Burner`，当前用户安装，无需管理员权限。开始菜单提供入口，桌面快捷方式可选。安装不设置开机启动、服务或计划任务，不强制关闭运行中的程序；升级前应从面板或托盘“退出”。

配置、加密key与运行证据位于`%LOCALAPPDATA%\SenseNova.TokenBurner\User`。程序目录和用户数据分开，卸载不删除用户数据；本轮未读写已有真实用户目录。

包内没有私有agent资料、项目.local缓存、key、运行日志或PDB，附带首次使用说明及.NET随包许可。应用及Setup未签名，来源和Windows提示需由使用者核对；这不能写成无提示安装验收。开源许可证选择仍待确认，本轮没有擅自添加项目许可证。

## 可复现打包

准备固定SDK10.0.401，然后在PowerShell运行：

```powershell
& .\scripts\prepare-installer-tool.ps1
& .\scripts\build-installer.ps1
```

默认版本`0.1.0-preview.1`，可传`-Version`；编译器可传`-CompilerPath`。Inno Setup7.1.0只在忽略目录.local/tools准备，官方下载按GitHub release SHA-256和Authenticode核对，portable模式不注册工具卸载项或快捷方式。官方来源：[release](https://github.com/jrsoftware/issrc/releases/tag/is-7_1_0)、[portable模式实现](https://github.com/jrsoftware/issrc/blob/is-7_1_0/isportable.iss)、[许可证](https://github.com/jrsoftware/issrc/blob/is-7_1_0/license.txt)。

Windows安装脚本位于`installer/windows.iss`。发布从Desktop执行Release/win-x64/self-contained，多文件包，不启用裁剪；带固定10.0.12运行时。win-x64依赖使用三个项目各自的`packages.win-x64.lock.json`，开发锁不变；首次缺锁生成，之后locked还原。首次生成新的发布锁需复核后提交，普通构建不更新已有锁。

输出位于`.local/releases/<version>-<UTC-id>/`：publish目录、artifacts目录、构建日志及manifest.json。artifacts包含Setup.exe、SHA256SUMS.txt和首次使用.md。manifest记录包内文件、字节数、构建时源码HEAD及是否含未提交修改；本次构建时包含未提交的托盘和打包改动，这些改动随后已提交并推送。保留原manifest，不把后续HEAD回填为构建来源。

## 本轮必要验证

- 自包含publish与Inno编译成功；includedFrameworks固定Microsoft.NETCore.App及Microsoft.WindowsDesktop.App10.0.12。
- 新建隔离目标，在没有已有本产品卸载注册项的前提下静默安装成功，退出0；277个安装文件SHA-256与发布目录一致。
- 临时将子进程DOTNET_ROOT设为不存在、PATH限为System32，自包含入口成功加载。使用未识别参数在实例锁、用户数据与HTTP之前按设计返回1；这只证明入口加载，不能当完整窗口运行。
- 注册卸载目标确认属于本轮新建工作区后，通过自身卸载器卸载成功，退出0；本产品临时注册项和可执行文件已清理。没有删除用户数据或结束已有进程。
- 包内私有路径0，SHA256SUMS与Setup吻合；本次无真实key或请求。不重复引擎全量测试和资源测量，沿用托盘299项已验证基线。

本机产物目录：`.local/releases/0.1.0-preview.1-20261006-084117-f6e0a6e9/artifacts/`。Setup SHA-256：

```text
da74f9d74d1198d9dabb439788556bd42c8fc5c4bf6c17c97e47a27dbc8c080a
```

静默安装/卸载原始结果在`.local/verification/installer/ff6aecf349514475821237a29675847d/result.json`。自查结论`PASS WITH NOTES`：本地安装产物和必要静默检查通过，完整向导/外部窗口、填key运行与真实官方扣减/返赠未验证，也未在无SDK的新Windows系统复验。旧模拟实例会触发单实例保护，完整打开确认需要正常退出旧实例；不能强杀绕过。

## 后续基本启动与真实短调用

2026-10-06：旧模拟实例由用户正常退出后，现有Setup在当前用户默认目录新安装成功（退出0）；正式入口创建`SenseNova Token Burner`窗体，进程正常响应。完整向导/外部鼠标输入、视觉和无SDK新系统仍未验证。

用户批准真实key小规模测试后，固定官方地址GET模型成功，返回9个模型；活动模型`sensenova-6.8-flash-lite`上下文262144、最大输出65536。复用该安装包Core/Infrastructure DLL，独立临时运行记录和外层HTTP限制，合计仅1GET与1POST，均HTTP200，无自动重试或周期计划。短请求8字符、最大输出32；官方usage输入87、输出32、总119 tokens。持久引擎暂停后停止，Stopped/UserStop、完成1笔、0在途/未知/预留，最终落盘usage一致，探针退出0。

测试通过的是正式HTTP/引擎/落盘路径，不是实际点击安装窗口或34万字符输入校准；不证明专属积分扣减、通用积分返赠或任意初始余额下不扣通用积分。原请求、回答正文和key均未写入报告，未修改真实用户配置或旧脚本。最新最小复查结论`PASS WITH NOTES`：可准备预览MVP；上述边界在Release说明中保留，不追加消耗或反复测试。

首个MVP采用预览版交付，上述边界在[Release说明](https://github.com/Zhou-Jin-Feng/sensenova-token-burner/releases/tag/v0.1.0-preview.1)保留。原异常原因/长期等待、普通窗口等待102.41MiB略超目标等限制保留，见 [托盘报告](托盘与退出.md)、[资源报告](资源基线测量.md)。

## 单笔生产输入校准与修正候选

2026-10-06获准再执行仅1笔34万字符生产请求，最大输出1024，无额外GET、重试或周期。HTTP200，约31.35秒；实际输入217828、输出1024、总218852 tokens，超过旧版200000输入/201024总预留。因此`0.1.0-preview.1`可能在首批请求结算后触发BudgetEstimateExceeded并停止新增，真实HTTP成功不代表整轮可持续运行。

旧本地候选0.1.0-preview.2输入预留240000，总241024；观测上方约10%余量，仍小于262144上下文。离线回放与52项通过，未追加真实调用。候选未发布；该修正已纳入新版多key，不能将尚未公开的preview.2链接当下载入口。

单样本只说明此次输入在新估计内，不是tokenizer保证或积分上限；完整目标、积分扣减来源和返赠仍未验证。

修正版安装包：`SenseNova.TokenBurner-0.1.0-preview.2-win-x64-setup.exe`约35.60MiB，安装文件277项；SHA-256为`4b33255f4896f35ed057361add001b9c4aa1dadaa38e7183fb251c4c4c4e4315`。自包含发布及安装器编译成功，打包DLL离线预检确认240000输入/241024总预留、0请求。没有覆盖正在运行的preview.1，也不重复安装/资源或真实调用；本轮未验证修正版实际升级。发布附件的大小与GitHub SHA-256在上传时核对。
