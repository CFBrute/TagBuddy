using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TagBuddy.Services;

/// <summary>Private tags: text encrypted with a passphrase, so only someone who knows it can read the tag, and only
/// in this app. Everything happens on the phone; the passphrase is never stored or sent anywhere.
///
/// <para>The key is derived from the passphrase with PBKDF2-SHA256 and a random salt, deliberately slow so that
/// guessing passphrases from a copied tag is expensive. The text is encrypted with AES-256-GCM, which also detects
/// a wrong passphrase or a changed tag: such a tag does not open, rather than showing garbage.</para>
///
/// <para>Layout, stored as an NDEF record of type <see cref="MimeType"/>: version (1 byte), PBKDF2 iterations
/// (4 bytes, big-endian), salt (16), nonce (12), the encrypted UTF-8 text, and the GCM tag (16). The first 33
/// bytes are authenticated too, so the iteration count and salt cannot be altered unnoticed.</para></summary>
public static class SealedText
{
    /// <summary>The NDEF record type. Other phones find no app for it and show nothing.</summary>
    public const string MimeType = "application/vnd.tagbuddy.sealed";

    /// <summary>About a quarter to half a second on a phone: unnoticed when writing or opening one tag, and a
    /// real cost for anyone trying millions of passphrases.</summary>
    public const int DefaultIterations = 200_000;

    /// <summary>The shortest passphrase the app accepts. A copied tag can be attacked offline, so short ones are
    /// a false sense of privacy.</summary>
    public const int MinPassphraseLength = 8;

    private const byte Version = 1;
    private const int SaltSize = 16, NonceSize = 12, TagSize = 16, KeySize = 32;
    private const int HeaderSize = 1 + 4 + SaltSize + NonceSize;

    /// <summary>Bytes added to the text: header and authentication tag.</summary>
    public const int Overhead = HeaderSize + TagSize;

    /// <summary>A tag cannot make the app work forever: iteration counts outside this range are not opened.</summary>
    private const int MinIterations = 1_000, MaxIterations = 5_000_000;

    /// <summary>Encrypts <paramref name="text"/> with <paramref name="passphrase"/>. Every call uses a new salt
    /// and nonce, so the same text and passphrase give different bytes each time.</summary>
    public static byte[] Seal(string text, string passphrase, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(passphrase)) throw new ArgumentException("A passphrase is needed.", nameof(passphrase));
        if (iterations is < MinIterations or > MaxIterations) throw new ArgumentOutOfRangeException(nameof(iterations));

        var plain = Encoding.UTF8.GetBytes(text);
        var sealedBytes = new byte[HeaderSize + plain.Length + TagSize];
        sealedBytes[0] = Version;
        BinaryPrimitives.WriteInt32BigEndian(sealedBytes.AsSpan(1, 4), iterations);
        var salt = sealedBytes.AsSpan(5, SaltSize);
        var nonce = sealedBytes.AsSpan(5 + SaltSize, NonceSize);
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);

        var key = DeriveKey(passphrase, salt, iterations);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plain, sealedBytes.AsSpan(HeaderSize, plain.Length),
                sealedBytes.AsSpan(HeaderSize + plain.Length, TagSize), sealedBytes.AsSpan(0, HeaderSize));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
        return sealedBytes;
    }

    /// <summary>Decrypts a sealed record. False for a wrong passphrase, a changed or damaged tag, or bytes that
    /// are not a sealed record at all.</summary>
    public static bool TryOpen(byte[] sealedBytes, string passphrase, out string text)
    {
        text = "";
        if (!LooksSealed(sealedBytes) || string.IsNullOrEmpty(passphrase)) return false;

        int iterations = BinaryPrimitives.ReadInt32BigEndian(sealedBytes.AsSpan(1, 4));
        var salt = sealedBytes.AsSpan(5, SaltSize);
        var nonce = sealedBytes.AsSpan(5 + SaltSize, NonceSize);
        int length = sealedBytes.Length - Overhead;
        var plain = new byte[length];

        var key = DeriveKey(passphrase, salt, iterations);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, sealedBytes.AsSpan(HeaderSize, length), sealedBytes.AsSpan(HeaderSize + length, TagSize),
                plain, sealedBytes.AsSpan(0, HeaderSize));
            text = Encoding.UTF8.GetString(plain);
            return true;
        }
        catch (CryptographicException)
        {
            return false;   // wrong passphrase, or the bytes were changed
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Whether the bytes have the shape of a sealed record this version of the app can try to open.</summary>
    public static bool LooksSealed(byte[]? data)
    {
        if (data is null || data.Length < Overhead || data[0] != Version) return false;
        int iterations = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(1, 4));
        return iterations is >= MinIterations and <= MaxIterations;
    }

    /// <summary>The passphrase is normalised first, so "é" typed as one character or as e plus an accent opens
    /// the same tag whichever keyboard typed it.</summary>
    private static byte[] DeriveKey(string passphrase, ReadOnlySpan<byte> salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC)),
            salt, iterations, HashAlgorithmName.SHA256, KeySize);
}
