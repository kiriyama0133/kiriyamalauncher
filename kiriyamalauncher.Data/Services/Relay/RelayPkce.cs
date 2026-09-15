using System;
using System.Security.Cryptography;
using System.Text;

namespace kiriyamalauncher.Data;

/// <summary>
/// PKCE（RFC 7636）S256 辅助：生成 code_verifier 与对应的 code_challenge。
/// 与服务端 <c>ComputeCodeChallenge</c> 的算法保持一致（SHA-256 + base64url）。
/// </summary>
public static class RelayPkce
{
    /// <summary>生成一个高熵的 code_verifier（43~128 字符的 base64url 串）。</summary>
    public static string GenerateCodeVerifier()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(48);
        return Base64UrlEncode(bytes);
    }

    /// <summary>由 code_verifier 计算 S256 的 code_challenge。</summary>
    public static string ComputeCodeChallenge(string codeVerifier)
    {
        if (string.IsNullOrWhiteSpace(codeVerifier))
        {
            throw new ArgumentException("Code verifier cannot be empty.", nameof(codeVerifier));
        }

        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
