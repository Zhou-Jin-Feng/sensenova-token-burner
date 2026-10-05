using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Core;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class ConsumptionPolicyTests
{
    [TestMethod]
    [DataRow(0, 0L)]
    [DataRow(25, 30_000_000L)]
    [DataRow(50, 60_000_000L)]
    [DataRow(75, 90_000_000L)]
    [DataRow(95, 120_000_000L)]
    [DataRow(1, 1_200_000L)]
    [DataRow(40, 48_000_000L)]
    [DataRow(76, 91_500_000L)]
    [DataRow(90, 112_500_000L)]
    public void CalibrationAndInterpolationMatchConfirmedTargets(int percentage, long expectedTokens)
        => Assert.AreEqual(expectedTokens, ConsumptionPolicy.GetTargetTokens(percentage));

    [TestMethod]
    public void IncreasingPercentageCannotReduceOrExceedTarget()
    {
        var previous = 0L;
        for (var percentage = 0; percentage <= 95; percentage++)
        {
            var current = ConsumptionPolicy.GetTargetTokens(percentage);
            Assert.IsTrue(current >= previous);
            Assert.IsTrue(current <= 120_000_000L);
            previous = current;
        }
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(96)]
    [DataRow(int.MinValue)]
    [DataRow(int.MaxValue)]
    public void OutOfRangePercentageIsRejected(int percentage)
        => Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ConsumptionPolicy.GetTargetTokens(percentage));
}
