using System.Security.Cryptography;

namespace Mutter.Web.Services;

/// <summary>
/// 更新清单的 ECDSA P-256 签名校验。发布流水线用私钥对 update.json 的原始字节签名，
/// 客户端内置/配置公钥。镜像源必须通过签名校验，避免镜像同时篡改压缩包与校验和。
/// </summary>
public static class UpdateSignature
{
    public static bool TryValidatePublicKey(string publicKeyText, out string error)
    {
        error = string.Empty;
        try
        {
            using var key = CreatePublicKey(publicKeyText);
            if (key is null)
            {
                error = "公钥格式无效";
                return false;
            }

            if (key.KeySize != 256)
            {
                error = $"公钥必须是 ECDSA P-256（当前 {key.KeySize} 位）";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            error = $"公钥格式无效：{ex.Message}";
            return false;
        }
    }

    public static bool TryVerify(
        ReadOnlySpan<byte> data,
        string signatureText,
        string publicKeyText,
        out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(publicKeyText))
        {
            error = "未配置清单签名公钥";
            return false;
        }

        try
        {
            using var key = CreatePublicKey(publicKeyText);
            if (key is null)
            {
                error = "公钥格式无效";
                return false;
            }

            var signature = DecodeBase64(signatureText);
            if (signature.Length == 0)
            {
                error = "签名内容为空";
                return false;
            }

            if (!key.VerifyData(
                    data,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence))
            {
                error = "清单签名校验失败";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            error = $"清单签名校验失败：{ex.Message}";
            return false;
        }
    }

    private static ECDsa? CreatePublicKey(string text)
    {
        var normalized = (text ?? string.Empty).Trim();
        if (normalized.Length == 0)
            return null;

        normalized = normalized
            .Replace("-----BEGIN PUBLIC KEY-----", string.Empty, StringComparison.Ordinal)
            .Replace("-----END PUBLIC KEY-----", string.Empty, StringComparison.Ordinal);
        normalized = new string(normalized.Where(c => !char.IsWhiteSpace(c)).ToArray());

        var bytes = Convert.FromBase64String(normalized);
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(bytes, out _);
        return key;
    }

    private static byte[] DecodeBase64(string text)
    {
        var normalized = new string((text ?? string.Empty)
            .Where(c => !char.IsWhiteSpace(c))
            .ToArray());
        if (normalized.Length == 0)
            return [];

        // 签名文件通常以 Base64 文本保存；兼容直接粘贴二进制 Base64 的情况。
        return Convert.FromBase64String(normalized);
    }
}
