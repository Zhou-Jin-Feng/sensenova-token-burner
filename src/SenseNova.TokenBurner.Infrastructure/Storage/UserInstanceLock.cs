using SenseNova.TokenBurner.Core;

namespace SenseNova.TokenBurner.Infrastructure.Storage;

/// <summary>进程生命周期持有句柄；异常退出由操作系统释放，遗留文件不代表仍占用。</summary>
public sealed class UserInstanceLock : IDisposable
{
    private readonly FileStream _stream;
    private UserInstanceLock(FileStream stream) => _stream = stream;

    public static UserInstanceLock? TryAcquire(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return new(new FileStream(Path.Combine(Path.GetFullPath(directory), "application.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33) { return null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new LocalStorageException("无法取得应用实例锁，请检查应用数据目录权限。"); }
    }

    public void Dispose() => _stream.Dispose();
}
