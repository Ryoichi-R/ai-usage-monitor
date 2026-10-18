using System.Security.Cryptography;
using System.Text;

namespace AiUsageMonitor.Claude.Mac;

public sealed record MacClaudeBinaryIdentity(string Token)
{
    public string Key => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Token)));

    // Publisher trust is established by the native strict signature requirement before returning this token.
    public static MacClaudeBinaryIdentity Parse(string token)
    {
        string[] parts = token.Trim().Split('|');
        if (token.Length > 600 || parts.Length != 5 || parts[0] != "2" ||
            string.IsNullOrEmpty(parts[1]) || parts[1].Length > 128 || parts[2].Length > 128 ||
            parts[1].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_') ||
            parts[2].Any(c => !char.IsAsciiLetterOrDigit(c)) ||
            !int.TryParse(parts[3], out int algorithm) || algorithm <= 0 ||
            parts[4].Length is < 2 or > 256 || parts[4].Length % 2 != 0 ||
            parts[4].Any(c => !char.IsAsciiHexDigit(c)))
            throw new IOException("CLI_IDENTITY_INVALID");
        return new(string.Join('|', parts[..4]) + "|" + parts[4].ToLowerInvariant());
    }
}
