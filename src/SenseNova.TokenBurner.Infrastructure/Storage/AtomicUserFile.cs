using SenseNova.TokenBurner.Core;

namespace SenseNova.TokenBurner.Infrastructure.Storage;

internal static class AtomicUserFile
{
    public const int MaximumBytes = 65_536;

    public static async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                4096, FileOptions.Asynchronous);
            if (stream.Length > MaximumBytes) throw new LocalStorageException("本地数据文件过大，未读取。");
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public static async Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken,
        bool waitForReplacement = false)
    {
        if (data.Length > MaximumBytes) throw new LocalStorageException("本地数据超出保存上限。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, data, cancellationToken);
            // Windows替换可能受短暂读锁/文件过滤器占用影响；仅运行检查点启用，最多等待500ms。
            // 持续权限错误仍失败，不改变ACL；临时文件只写一次，等待不会重复模型请求。
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { File.Move(temporaryPath, path, overwrite: true); break; }
                catch (Exception exception) when (waitForReplacement && attempt < 10
                    && exception is IOException or UnauthorizedAccessException
                    && (exception.HResult & 0xffff) is 5 or 32 or 33)
                { await Task.Delay(50, cancellationToken).ConfigureAwait(false); }
            }
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}
