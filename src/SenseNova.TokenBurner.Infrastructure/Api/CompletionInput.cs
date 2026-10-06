using System.Globalization;
using System.Text;
using SenseNova.TokenBurner.Core;

namespace SenseNova.TokenBurner.Infrastructure.Api;

/// <summary>沿用旧脚本的大段技术文本结构；预留参考真实单请求留余量，并非tokenizer结果或费用硬上限。</summary>
public static class CompletionInput
{
    // 2026-10-06真实340000字符请求输入217828 tokens；留约10%余量，仍低于262144上下文。
    public const long EstimatedInputTokens = 240_000;
    public static CompletionRequest Create(CancellationToken cancellationToken = default)
    {
        string[] topics = ["跨区域清算与一致性", "时钟校准与事件因果", "高并发内存屏障", "日志复制与故障恢复", "混沌工程与容量规划"];
        var input = new StringBuilder(RequestBaseline.InputCharacterTarget + 1024);
        input.Append("# 分布式系统技术参数与架构规范\n请阅读以下生成的技术文本，简要概括核心架构、故障处理与性能约束。\n");
        for (var index = 1; input.Length < RequestBaseline.InputCharacterTarget; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            input.Append(CultureInfo.InvariantCulture, $"""

                ## 第 {index:00000} 节：{topics[index % topics.Length]} (指纹: 0x{Random.Shared.NextInt64():x16})
                - 拓扑物理节点：亚太中心 DC-TY3 (Tokyo), 欧洲中心 DC-FR2 (Frankfurt), 北美中心 DC-NY4 (Secaucus)
                - 光纤往返时延 RTT 基线：{0.8 + Random.Shared.NextDouble() * 234.2:F2} ms | 硬件时钟抖动 Jitter: ±{0.05 + Random.Shared.NextDouble() * 14.75:F2} ms
                - PTP IEEE 1588v2 硬件晶振温漂：{-14.8 + Random.Shared.NextDouble() * 29.6:F3} ms | 温漂斜率系数: {1e-7 + Random.Shared.NextDouble() * 3.9e-6:E2}
                - 核心指标：QPS 吞吐基线={Random.Shared.Next(120000, 600001)}/s, P99.99 延迟={1.1 + Random.Shared.NextDouble() * 6.7:F2} us
                - 内存屏障指令周期：Acquire-Release={Random.Shared.Next(8, 13)} Cycles, MFENCE={Random.Shared.Next(32, 56)} Cycles
                - 分布式 Quorum 状态机：节点总数=5, 存活节点=5, WAL 日志位点 LSN={10000000 + index * 256}
                - 论述内容：在面对高频并发写争用时，核心撮合引擎通过无锁原子 CAS 配合退避策略降低上下文切换。在跨洋广域网网络分区时，混合逻辑时钟 HLC 通过校准物理时间与逻辑序号保障事件因果一致性。需分别验证故障恢复、时延、数据一致性与吞吐边界。

                """);
        }
        return new(input.ToString(0, RequestBaseline.InputCharacterTarget), RequestBaseline.MaximumOutputTokens, EstimatedInputTokens);
    }
}
