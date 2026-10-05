using System.Security.Cryptography;
using System.Text;
using SenseNova.TokenBurner.Core;

namespace SenseNova.TokenBurner.Infrastructure.Storage;

public sealed class DpapiCredentialStore(string directory) : ICredentialStore
{
    private readonly string _path = Path.Combine(Path.GetFullPath(directory), "mock-credential.dat");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SenseNova.TokenBurner.MockProfile.v1");

    public async Task SaveAsync(string credential, CancellationToken cancellationToken = default)
    {
        ValidateCredential(credential);
        var plaintext = Encoding.UTF8.GetBytes(credential);
        try
        {
            var ciphertext = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            await AtomicUserFile.WriteAsync(_path, ciphertext, cancellationToken);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        { throw new LocalStorageException("测试凭据无法加密保存，请检查当前 Windows 用户与数据目录。"); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        byte[]? plaintext = null;
        try
        {
            var ciphertext = await AtomicUserFile.ReadAsync(_path, cancellationToken);
            if (ciphertext is null) return null;
            plaintext = ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
            var credential = Encoding.UTF8.GetString(plaintext);
            ValidateCredential(credential);
            return credential;
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
        { throw new LocalStorageException("已保存的测试凭据无法恢复，请重新保存测试凭据。"); }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    internal static void ValidateCredential(string credential)
    {
        if (string.IsNullOrWhiteSpace(credential) || credential.Length > 8192 || credential.Any(char.IsWhiteSpace)
            || credential.Any(char.IsControl)) throw new ArgumentException("凭据为空或包含不支持的字符。");
    }
}
