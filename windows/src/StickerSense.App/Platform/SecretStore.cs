using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace StickerSense.App.Platform;

/// <summary>
/// Stores small secrets (the GIPHY API key) encrypted with DPAPI for the current Windows user,
/// instead of in plain-text settings.
/// </summary>
internal sealed class SecretStore(string directory)
{
    private static readonly byte[] Entropy = "StickerSense GIPHY key v1"u8.ToArray();

    private string GiphyPath => Path.Combine(directory, "giphy-key.bin");

    public string ReadGiphyKey()
    {
        try
        {
            if (!File.Exists(GiphyPath)) return "";
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(GiphyPath), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    public void WriteGiphyKey(string key)
    {
        key = key.Trim();
        try
        {
            if (key.Length == 0)
            {
                File.Delete(GiphyPath);
                return;
            }
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(GiphyPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Leave the old key; Settings will show it wasn't saved when reopened.
        }
    }
}
