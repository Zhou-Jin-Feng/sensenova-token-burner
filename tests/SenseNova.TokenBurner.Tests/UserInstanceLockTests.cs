using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Infrastructure.Storage;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class UserInstanceLockTests
{
    [TestMethod]
    public void SecondInstanceIsRejectedAndLeftoverFileDoesNotPreventReopen()
    {
        using var folder = new TestFolder();
        using (var owner = UserInstanceLock.TryAcquire(folder.Path))
        {
            Assert.IsNotNull(owner);
            Assert.IsNull(UserInstanceLock.TryAcquire(folder.Path));
        }
        Assert.IsTrue(File.Exists(Path.Combine(folder.Path, "application.lock")));
        using var reopened = UserInstanceLock.TryAcquire(folder.Path);
        Assert.IsNotNull(reopened);
    }

    [TestMethod]
    public async Task AnotherProcessCannotAcquireParentOwnedLock()
    {
        using var folder = new TestFolder();
        using var owner = UserInstanceLock.TryAcquire(folder.Path);
        Assert.IsNotNull(owner);
        using var process = StartProbe(folder.Path, "try { $s = [System.IO.File]::Open((Join-Path $env:TASK_INSTANCE_DIRECTORY 'application.lock'), 'OpenOrCreate', 'ReadWrite', 'None'); $s.Dispose(); exit 3 } catch [System.IO.IOException] { exit 0 }");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(0, process.ExitCode);
    }

    [TestMethod]
    public async Task AbruptChildExitReleasesItsLockWithoutDeletingFile()
    {
        using var folder = new TestFolder();
        var ready = Path.Combine(folder.Path, "ready");
        var release = Path.Combine(folder.Path, "release");
        using var process = StartProbe(folder.Path,
            "$s = [System.IO.File]::Open((Join-Path $env:TASK_INSTANCE_DIRECTORY 'application.lock'), 'OpenOrCreate', 'ReadWrite', 'None'); [System.IO.File]::WriteAllText((Join-Path $env:TASK_INSTANCE_DIRECTORY 'ready'), 'ready'); $deadline = [DateTime]::UtcNow.AddSeconds(10); while (-not [System.IO.File]::Exists((Join-Path $env:TASK_INSTANCE_DIRECTORY 'release')) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 20 }; [Environment]::Exit(23)");
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(ready) && !process.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10);
        // 在断言前通知子进程退出，避免失败时遗留运行的测试 helper。
        var wasReady = File.Exists(ready);
        var locked = wasReady ? UserInstanceLock.TryAcquire(folder.Path) : null;
        await File.WriteAllTextAsync(release, "release");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        locked?.Dispose();
        Assert.IsTrue(wasReady);
        Assert.IsNull(locked);
        Assert.AreEqual(23, process.ExitCode);
        using var reopened = UserInstanceLock.TryAcquire(folder.Path);
        Assert.IsNotNull(reopened);
    }

    private static Process StartProbe(string directory, string code)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(code)) })
            start.ArgumentList.Add(argument);
        start.Environment["TASK_INSTANCE_DIRECTORY"] = directory;
        return Process.Start(start) ?? throw new InvalidOperationException("验证进程无法启动。");
    }
}
