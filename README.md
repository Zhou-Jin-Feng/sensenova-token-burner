# SenseNova Token Burner

轻量 Windows 11 x64 多任务面板：添加多个 SenseNova API key，分别配置目标和计划，单独或全部运行、暂停、继续、停止。C# + WinForms、自包含 .NET 安装包，无需 Codex、Python、SDK 或浏览器运行层。

新版多key源码、必要模拟验收和0.2.0-preview.1本地候选安装包已完成（35.62MiB），尚未推送或发布；GitHub当前公开[v0.1.0-preview.1](https://github.com/Zhou-Jin-Feng/sensenova-token-burner/releases/tag/v0.1.0-preview.1)仍为旧单key版。旧preview.2候选未发布，不能作为新版多key安装包。

## 使用

1. 填 API key，可选备注如 key-1、key-2，点击“添加并加密保存”。只有 key 必填，固定官方地址和 Flash-Lite 活动模型。
2. 选择任务，设置独立比例、token目标、启用与间隔并保存。同账户的 key 可填写相同额度组。
3. 点击“运行所选”或“运行全部”；运行全部前展示组目标合计与风险。
4. 可单独/全部暂停、继续、停止，或为所选任务显式启用计划。关闭/最小化进入托盘，“退出”才收尾结束。

默认95%对应1.2亿tokens，50%对应6000万，保留0–95%滑条及四档预设，也可输入自定义目标。默认间隔5小时；保存和重开不启动。固定服务地址 `https://token.sensenova.cn/v1`。

每个 key 独立配置和检查点，同一进程共享最多3笔请求；同 key 不重叠，一个错误不停止其他任务。任务完成显示左下角提示，隐藏时调用系统托盘通知。重开可“继续剩余目标”，沿用原目标与已确认用量；未知结果先人工核查，确认不发请求、不恢复服务端同一请求。

API key按当前Windows用户DPAPI加密保存，列表遮蔽，不写入普通设置/日志/运行记录。升级保留原key、配置及历史。说明：[首次使用](docs/首次使用.md)。

## 积分与验证边界

1.2亿tokens/约58,000点为特定条件的经验估算；超过可能扣通用积分，低于可能未用满专属积分，任何目标都不能保证绝对安全或精确返赠。实际扣减、额度和到账以SenseNova官方记录为准。多key不等于多份独立积分池，5小时仅为运行计划，官方额度是滚动窗口。

新版16项行为及受影响引擎基线共134项通过，补充继续全部验证通过；Release构建0警告/0错误。一次3key/34万字符mock资源采样峰107.11MiB、最多3在途，最终在途/未知均0。旧299项测试保留，不重复长等待或完整真实消耗。详见[新版MVP验收](docs/新版MVP验收.md)。

新版真实key/积分消耗、实际升级、无SDK新机器和完整外部输入/视觉未验证；安装包未签名。本轮不新增真实调用。历史已批准单请求校准观测输入217828、输出1024，本地代码已将输入预留提高至240000，保留超限保护；单样本不等于tokenizer上限。

## 文档

- [新版MVP权威范围](docs/新版MVP范围.md)
- [新版MVP验收](docs/新版MVP验收.md)
- [首次使用](docs/首次使用.md)
- [项目任务清单](docs/项目任务清单.md)
- [需求与实现方案](docs/需求与实现方案.md)
- [基础架构与开发](docs/基础架构与开发.md)
- [Windows安装包](docs/Windows安装包.md)
- [SenseNova官方文档](https://platform.sensenova.cn/docs)

旧单key引擎、保护、调度、恢复、资源和窗口报告保留在docs作为历史基线，不能代替新版验收。

## 开发

.NET SDK `10.0.401` 由global.json固定，项目内工具/缓存保持私有。在PowerShell运行：

```powershell
& .\scripts\dev.ps1 restore
& .\scripts\dev.ps1 build
& .\scripts\dev.ps1 test
& .\scripts\dev.ps1 run
```

开发run显式使用进程内HTTP mock，只接受虚拟key（如 `mock-only-not-a-real-key-1`），不联网、不监听端口；开发目录在.local/mock-profile。普通启动使用真实HTTP，但添加/保存/重开不请求；只有显式运行或启用计划才自动校验模型并发消耗请求。

用户目录为 `%LOCALAPPDATA%\SenseNova.TokenBurner\User`，tasks.json不含key，新任务位于Tasks/标识/，凭据是DPAPI密文，每任务run-state.json含检查点及最多20条历史。旧任务继续使用原路径。仓库不得包含真实凭据、私人配置或运行记录，agent/与.local/均忽略。

新增依赖时才执行 `& .\scripts\dev.ps1 restore -UpdateLockFiles`，本轮未增加运行依赖。旧资源脚本保留作历史工具，新MVP仅做一次代表性最大并发采样，不以长模拟或重复诊断作为交付前置。
