using System.Security.Cryptography;
using System.Text;

namespace PayrixLauncher.Services;

/// <summary>
/// BQE-compatible DES / TripleDES encrypt / decrypt.
/// BQECore stores encrypted values (AccessToken, passwords, etc.) as lowercase hex strings
/// using DES-ECB or TripleDES-ECB with PKCS7 padding and one of the two standard BQE keys.
/// </summary>
public static class CryptoService
{
    // Standard BQE DES keys (8 bytes each)
    public const string Key1 = "C#E1b@$0";
    public const string Key2 = "E@a2b#$0";

    public static readonly string[] PresetKeys = [Key1, Key2];

    // ── Decrypt ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Tries to decrypt a hex or Base64 cipher string with every preset key,
    /// cipher algorithm (DES + TripleDES), mode, and padding combination.
    /// Returns a list of (label, plainText) for every attempt that produced readable text.
    /// </summary>
    public static List<(string label, string plain)> TryAllPresets(string cipherInput)
    {
        var results = new List<(string, string)>();
        var bytes = TryParseInput(cipherInput);
        if (bytes is null) return results;

        // BQE-native format first (most likely match for BQECore tokens)
        var bqeV2 = BqeDecrypt(cipherInput);
        if (bqeV2.plain is not null && IsPrintable(bqeV2.plain))
            results.Add(($"BQE Native  Key={BqeKey2}/IV={BqeIV2}  (V2 — primary)", bqeV2.plain));

        var bqePwd = BqeDecryptPassword(cipherInput);
        if (bqePwd.plain is not null && IsPrintable(bqePwd.plain)
            && (bqeV2.plain is null || bqePwd.plain != bqeV2.plain))
            results.Add(($"BQE Password  Key={BqeKey1}/IV={BqeIV1}  (V1 — password)", bqePwd.plain));

        // Manual DES / 3DES sweep (ECB + CBC, both preset keys)
        foreach (var key in PresetKeys)
        {
            foreach (var algo in new[] { "DES", "3DES" })
            {
                foreach (var mode in new[] { CipherMode.ECB, CipherMode.CBC })
                {
                    foreach (var padding in new[] { PaddingMode.PKCS7, PaddingMode.None, PaddingMode.Zeros, PaddingMode.ANSIX923 })
                    {
                        var plain = TryDecryptBytes(bytes, key, mode, padding, algo);
                        if (plain is not null && IsPrintable(plain))
                            results.Add(($"Algo={algo}  Key={key}  Mode={mode}  Pad={padding}", plain));
                    }
                }
            }
        }
        return results;
    }

    /// <summary>Decrypt with an explicit key, mode, padding, and algorithm.</summary>
    public static (string? plain, string? error) Decrypt(
        string cipherInput, string key, CipherMode mode = CipherMode.ECB,
        PaddingMode padding = PaddingMode.PKCS7, string algo = "DES")
    {
        var bytes = TryParseInput(cipherInput);
        if (bytes is null)
            return (null, "Input is not valid Hex or Base64.");

        // Try the specified padding first, then fall back to others automatically
        var paddingsToTry = new[] { padding, PaddingMode.PKCS7, PaddingMode.None, PaddingMode.Zeros, PaddingMode.ANSIX923 }
                                .Distinct().ToArray();

        foreach (var pad in paddingsToTry)
        {
            var plain = TryDecryptBytes(bytes, key, mode, pad, algo);
            if (plain is not null)
                return (plain, null);
        }

        // If 3DES was requested but didn't work, also try plain DES (and vice-versa)
        var altAlgo = algo == "3DES" ? "DES" : "3DES";
        foreach (var pad in paddingsToTry)
        {
            var plain = TryDecryptBytes(bytes, key, mode, pad, altAlgo);
            if (plain is not null)
                return (plain, $"Note: decrypted with {altAlgo} (not {algo})");
        }

        return (null, "Decryption failed — no valid key/padding/algorithm combination worked. Try 'Try All Keys'.");
    }

    // ── Encrypt ─────────────────────────────────────────────────────────────

    /// <summary>Encrypt plain text → lowercase hex string (BQE format).</summary>
    public static (string? cipherHex, string? error) Encrypt(
        string plainText, string key, CipherMode mode = CipherMode.ECB,
        OutputFormat outFmt = OutputFormat.Hex, string algo = "DES")
    {
        if (string.IsNullOrEmpty(plainText))
            return (null, "Input is empty.");

        try
        {
            var cipher = EncryptBytes(Encoding.UTF8.GetBytes(plainText), key, mode, algo);
            if (cipher is null)
                return (null, $"Key must be exactly 8 bytes (got {Encoding.UTF8.GetByteCount(key)}).");

            var result = outFmt == OutputFormat.Base64
                ? Convert.ToBase64String(cipher)
                : Convert.ToHexString(cipher).ToLowerInvariant();

            return (result, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string? TryDecryptBytes(byte[] cipherBytes, string key, CipherMode mode,
        PaddingMode padding, string algo = "DES")
    {
        var keyBytes = GetKeyBytes(key, algo);
        if (keyBytes is null) return null;

        try
        {
            SymmetricAlgorithm des = algo == "3DES"
                ? TripleDES.Create()
                : DES.Create();

            using (des)
            {
                des.Key     = keyBytes;
                des.Mode    = mode;
                des.Padding = padding;
                if (mode == CipherMode.CBC) des.IV = algo == "3DES"
                    ? keyBytes.Take(8).ToArray()
                    : keyBytes;

                using var dec = des.CreateDecryptor();
                var plainBytes = dec.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);

                // For PaddingMode.None or Zeros, strip trailing null bytes
                if (padding == PaddingMode.None || padding == PaddingMode.Zeros)
                    plainBytes = plainBytes.TakeWhile((b, i) => i == 0 || b != 0 || plainBytes.Skip(i).Any(x => x != 0)).ToArray();

                return Encoding.UTF8.GetString(plainBytes);
            }
        }
        catch { return null; }
    }

    private static byte[]? EncryptBytes(byte[] plainBytes, string key, CipherMode mode, string algo = "DES")
    {
        var keyBytes = GetKeyBytes(key, algo);
        if (keyBytes is null) return null;

        try
        {
            SymmetricAlgorithm des = algo == "3DES"
                ? TripleDES.Create()
                : DES.Create();

            using (des)
            {
                des.Key     = keyBytes;
                des.Mode    = mode;
                des.Padding = PaddingMode.PKCS7;
                if (mode == CipherMode.CBC) des.IV = algo == "3DES"
                    ? keyBytes.Take(8).ToArray()
                    : keyBytes;

                using var enc = des.CreateEncryptor();
                return enc.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
            }
        }
        catch { return null; }
    }

    private static byte[]? TryParseInput(string input)
    {
        input = input.Trim();

        // Strip URL-encoding artifacts that can appear when copy-pasting
        // (e.g., %2B → +, %2F → /, %3D → =)
        if (input.Contains('%'))
            input = Uri.UnescapeDataString(input);

        // Try hex first (even length, all hex chars)
        if (input.Length % 2 == 0 && input.All(c => Uri.IsHexDigit(c)))
        {
            try { return Convert.FromHexString(input); } catch { }
        }

        // Try standard Base64 — normalise line breaks and whitespace
        var b64 = input.Replace("\r", "").Replace("\n", "").Replace(" ", "");

        // Handle URL-safe Base64 (- → +, _ → /)
        var standardB64 = b64.Replace('-', '+').Replace('_', '/');

        // Try with natural padding, then forced padding at 4-byte boundary
        foreach (var candidate in new[] { standardB64, b64 })
        {
            var padded = candidate.PadRight((candidate.Length + 3) / 4 * 4, '=');
            try
            {
                var decoded = Convert.FromBase64String(padded);
                if (decoded.Length > 0) return decoded;
            }
            catch { }
        }

        return null;
    }

    private static byte[]? GetKeyBytes(string key, string algo = "DES")
    {
        var b = Encoding.UTF8.GetBytes(key);
        if (b.Length != 8) return null;

        if (algo == "3DES")
        {
            // Expand 8-byte key to 24-byte TripleDES key (K1+K2+K3 = K+K+K for 112-bit effective strength)
            var expanded = new byte[24];
            Buffer.BlockCopy(b, 0, expanded, 0, 8);
            Buffer.BlockCopy(b, 0, expanded, 8, 8);
            Buffer.BlockCopy(b, 0, expanded, 16, 8);
            return expanded;
        }

        return b;
    }

    private static bool IsPrintable(string s) =>
        s.Length > 0 && s.All(c => c >= 32 || c == '\t' || c == '\n' || c == '\r');

    public enum OutputFormat { Hex, Base64 }

    // ── SHA-256 Hashing (BQECoreSharedLib.Hashing) ──────────────────────────

    /// <summary>Computes SHA-256 hash — exact replica of Hashing.ComputeSha256Hash().</summary>
    public static string Sha256Hash(string data)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(data));
        return string.Concat(bytes.Select(b => b.ToString("x2")));
    }

    /// <summary>Returns true when input is a valid 64-char lowercase hex SHA-256 digest.</summary>
    public static bool IsValidSha256Hash(string data) =>
        data.Length == 64 && data.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));

    /// <summary>
    /// Verifies plainText against a SHA-256 hash.
    /// Returns (matched, computedHash).
    /// </summary>
    public static (bool matched, string computedHash) Sha256Verify(string plainText, string hashToCompare)
    {
        var computed = Sha256Hash(plainText);
        return (string.Equals(computed, hashToCompare.Trim(), StringComparison.OrdinalIgnoreCase), computed);
    }

    // ── BQE-Native (exact replica of BQECoreSharedLib.Encryption) ───────────

    // V2 keys (primary — matches Encryption.Key_2 / IV_2)
    public const string BqeKey2 = "C#E1b@$0";
    public const string BqeIV2  = "E@a2b#$0";

    // V1 keys (legacy — matches Encryption.Key / IV, used for PasswordEncrypt)
    public const string BqeKey1 = "BQES2011";
    public const string BqeIV1  = "BQES2011";

    /// <summary>
    /// Exact replica of BQECoreSharedLib.Encryption.Encrypt().
    /// Prepends a 5-char zero-padded length header, then DES-CBC encodes with
    /// Key_2 / IV_2 ("C#E1b@$0" / "E@a2b#$0"), returns Base64.
    /// </summary>
    public static (string? cipher, string? error) BqeEncrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return (null, "Input is empty.");
        if (plainText.Length > 92160)
            return (null, "Data too large (max 90 KB).");

        try
        {
            var withHeader = string.Format("{0,5:00000}", plainText.Length) + plainText;
            var result = BqeEncryptInternal(withHeader, BqeKey2, BqeIV2);
            return result is null
                ? (null, "DES encryption failed.")
                : (result, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>
    /// Exact replica of BQECoreSharedLib.Encryption.Decrypt().
    /// Tries V2 key first, falls back to V1. Strips the 5-char length header.
    /// </summary>
    public static (string? plain, string? error) BqeDecrypt(string cipherBase64)
    {
        if (string.IsNullOrEmpty(cipherBase64))
            return (null, "Input is empty.");

        try
        {
            // Try V2 first
            var result = BqeDecryptInternal(cipherBase64, BqeKey2, BqeIV2);
            if (result is null)
                result = BqeDecryptInternal(cipherBase64, BqeKey1, BqeIV1); // fallback V1

            if (result is null)
                return (null, "Decryption failed with both V1 and V2 keys.");

            // Strip 5-char length header
            if (result.Length >= 5 && int.TryParse(result[..5].Trim(), out var len)
                && len >= 0 && 5 + len <= result.Length)
                result = result.Substring(5, len);

            return (result, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>
    /// Exact replica of BQECoreSharedLib.Encryption.PasswordEncrypt().
    /// Uses V1 key ("BQES2011" / "BQES2011"), prepends length header, returns Base64.
    /// </summary>
    public static (string? cipher, string? error) BqePasswordEncrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return (null, "Input is empty.");

        try
        {
            var withHeader = string.Format("{0,5:00000}", plainText.Length) + plainText;
            var result = BqeEncryptInternal(withHeader, BqeKey1, BqeIV1);
            return result is null
                ? (null, "DES encryption failed.")
                : (result, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>
    /// Decrypts a password encrypted with BqePasswordEncrypt (V1 key only).
    /// </summary>
    public static (string? plain, string? error) BqeDecryptPassword(string cipherBase64)
    {
        if (string.IsNullOrEmpty(cipherBase64))
            return (null, "Input is empty.");

        try
        {
            var result = BqeDecryptInternal(cipherBase64, BqeKey1, BqeIV1);
            if (result is null)
                return (null, "Decryption failed with V1 key.");

            if (result.Length >= 5 && int.TryParse(result[..5].Trim(), out var len)
                && len >= 0 && 5 + len <= result.Length)
                result = result.Substring(5, len);

            return (result, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    // ── BQE internal helpers ─────────────────────────────────────────────────

    /// <summary>
    /// DES-CBC encrypt using ASCII encoding (mirrors BQECoreSharedLib.EncryptWithoutDataLength).
    /// Returns Base64 string, or null on failure.
    /// </summary>
    private static string? BqeEncryptInternal(string plainText, string key, string iv)
    {
        try
        {
            var keyBytes = Encoding.ASCII.GetBytes(key);
            var ivBytes  = Encoding.ASCII.GetBytes(iv);
            var data     = Encoding.ASCII.GetBytes(plainText);

            using var des = DES.Create();
            des.Key     = keyBytes;
            des.IV      = ivBytes;
            des.Mode    = CipherMode.CBC;
            des.Padding = PaddingMode.PKCS7;

            using var enc    = des.CreateEncryptor();
            using var mIn    = new System.IO.MemoryStream(data);
            using var mOut   = new System.IO.MemoryStream();
            using var cs     = new CryptoStream(mIn, enc, CryptoStreamMode.Read);

            var buffer = new byte[1024];
            int read;
            while ((read = cs.Read(buffer, 0, buffer.Length)) > 0)
                mOut.Write(buffer, 0, read);

            return mOut.Length > 0
                ? Convert.ToBase64String(mOut.GetBuffer(), 0, (int)mOut.Length)
                : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// DES-CBC decrypt (mirrors BQECoreSharedLib.DecryptWithoutDataLength).
    /// Returns ASCII-decoded string, or null on failure.
    /// </summary>
    private static string? BqeDecryptInternal(string cipherBase64, string key, string iv)
    {
        try
        {
            var keyBytes = Encoding.ASCII.GetBytes(key);
            var ivBytes  = Encoding.ASCII.GetBytes(iv);
            var data     = Convert.FromBase64String(cipherBase64);

            using var des = DES.Create();
            des.Key     = keyBytes;
            des.IV      = ivBytes;
            des.Mode    = CipherMode.CBC;
            des.Padding = PaddingMode.PKCS7;

            using var dec  = des.CreateDecryptor();
            using var mOut = new System.IO.MemoryStream();
            using var cs   = new CryptoStream(mOut, dec, CryptoStreamMode.Write);

            cs.Write(data, 0, data.Length);
            cs.FlushFinalBlock();

            return Encoding.ASCII.GetString(mOut.GetBuffer(), 0, (int)mOut.Length);
        }
        catch { return null; }
    }
}
