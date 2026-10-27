using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace EquipLink;

/// <summary>
/// Saves a password using Windows DPAPI for the current Windows user. This class
/// does not authenticate, start a process, log, or transmit credentials.
/// </summary>
public static class CredentialProtector
{
    public const int MaxSecretLength = 1024;
    private const uint UiForbidden = 0x1;
    private const int HashLength = 32;
    private const int MaxProtectedLength = 32768;
    private const string ProtectError = "비밀번호를 암호화하지 못했습니다. Windows 사용자 환경을 확인해 주세요.";
    private const string UnprotectError = "저장된 비밀번호를 읽을 수 없습니다. 저장할 때의 Windows 사용자와 설비 접속 정보를 확인하거나 비밀번호를 다시 저장해 주세요.";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly byte[] Magic = "EQLPW001"u8.ToArray();
    private static readonly byte[] ZeroBlock = new byte[4096];

    public static string Protect(Equipment equipment, string secret)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        ValidateSecret(secret);
        EnsureWindows();
        byte[]? clear = null, envelope = null, entropy = null, encrypted = null;
        try
        {
            clear = Utf8.GetBytes(secret);
            envelope = new byte[Magic.Length + HashLength + clear.Length];
            Magic.CopyTo(envelope, 0);
            SHA256.HashData(clear, envelope.AsSpan(Magic.Length, HashLength));
            clear.CopyTo(envelope, Magic.Length + HashLength);
            entropy = CreateEntropy(equipment);
            encrypted = Transform(envelope, entropy, protect: true);
            return Convert.ToBase64String(encrypted);
        }
        finally
        {
            Clear(clear); Clear(envelope); Clear(entropy); Clear(encrypted);
        }
    }

    public static string Unprotect(Equipment equipment, string protectedValue)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        EnsureWindows();
        byte[]? encrypted = null, entropy = null, envelope = null;
        try
        {
            if (string.IsNullOrEmpty(protectedValue) || protectedValue.Length > MaxProtectedLength)
                throw new CryptographicException(UnprotectError);
            try { encrypted = Convert.FromBase64String(protectedValue); }
            catch (FormatException) { throw new CryptographicException(UnprotectError); }
            entropy = CreateEntropy(equipment);
            envelope = Transform(encrypted, entropy, protect: false);
            int offset = Magic.Length + HashLength;
            if (envelope.Length < offset || envelope.Length > offset + MaxSecretLength * 4 ||
                !envelope.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                throw new CryptographicException(UnprotectError);

            // DPAPI supplies integrity protection. This versioned payload checksum
            // also rejects malformed/corrupted successful output before it is used.
            Span<byte> checksum = stackalloc byte[HashLength];
            try
            {
                SHA256.HashData(envelope.AsSpan(offset), checksum);
                if (!CryptographicOperations.FixedTimeEquals(checksum, envelope.AsSpan(Magic.Length, HashLength)))
                    throw new CryptographicException(UnprotectError);
            }
            finally { CryptographicOperations.ZeroMemory(checksum); }

            try
            {
                string secret = Utf8.GetString(envelope, offset, envelope.Length - offset);
                ValidateSecret(secret);
                return secret;
            }
            catch (ArgumentException) { throw new CryptographicException(UnprotectError); }
        }
        finally
        {
            Clear(encrypted); Clear(entropy); Clear(envelope);
        }
    }

    public static bool MatchesTarget(Equipment a, Equipment b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Target(a) == Target(b);
    }

    public static void ValidateSecret(string secret)
    {
        if (secret is null || secret.Length > MaxSecretLength)
            throw new ArgumentException("비밀번호는 1,024자 이내로 입력해 주세요.");
        for (int i = 0; i < secret.Length; i++)
        {
            char c = secret[i];
            if (char.IsControl(c) || c is '\u2028' or '\u2029')
                throw new ArgumentException("비밀번호에는 줄바꿈이나 제어 문자를 넣을 수 없습니다.");
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= secret.Length || !char.IsLowSurrogate(secret[i + 1]))
                    throw new ArgumentException("비밀번호에 올바르지 않은 Unicode 문자가 있습니다.");
                i++;
            }
            else if (char.IsLowSurrogate(c))
                throw new ArgumentException("비밀번호에 올바르지 않은 Unicode 문자가 있습니다.");
        }
    }

    private static (string Protocol, string Host, int Port, string Username) Target(Equipment equipment)
    {
        string host = (equipment.Host ?? "").Trim().Trim('[', ']');
        host = IPAddress.TryParse(host, out var address) ? address.ToString().ToLowerInvariant() : host.ToLowerInvariant();
        // Linux usernames are case-sensitive, so no case folding or whitespace
        // normalization is applied to the actual account identifier.
        return ((equipment.Protocol ?? "").Trim().ToUpperInvariant(), host, equipment.Port, equipment.Username ?? "");
    }

    private static byte[] CreateEntropy(Equipment equipment)
    {
        var target = Target(equipment);
        // Length prefixes prevent collisions between fields. Keep this format
        // stable across app releases; display name, group and record Id may change.
        string identity = "EquipLink.Password.v1|" + Field(target.Protocol) + Field(target.Host) +
            Field(target.Port.ToString(CultureInfo.InvariantCulture)) + Field(target.Username);
        byte[] identityBytes = Utf8.GetBytes(identity);
        try { return SHA256.HashData(identityBytes); }
        finally { Clear(identityBytes); }
    }

    private static string Field(string value) => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

    private static byte[] Transform(byte[] value, byte[] entropy, bool protect)
    {
        DataBlob input = default, extra = default, output = default;
        try
        {
            input = Allocate(value);
            extra = Allocate(entropy);
            // Deliberately omit CRYPTPROTECT_LOCAL_MACHINE: only the current
            // Windows user's DPAPI context may recover these credentials.
            bool ok = protect
                ? CryptProtectData(ref input, null, ref extra, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok || output.Length <= 0 || output.Length > MaxProtectedLength || output.Data == IntPtr.Zero)
                throw new CryptographicException(protect ? ProtectError : UnprotectError);
            byte[] result = new byte[output.Length];
            try { Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            catch { Clear(result); throw; }
        }
        finally
        {
            Release(ref input, native: false);
            Release(ref extra, native: false);
            Release(ref output, native: true);
        }
    }

    private static DataBlob Allocate(byte[] bytes)
    {
        DataBlob blob = new() { Length = bytes.Length, Data = Marshal.AllocHGlobal(Math.Max(1, bytes.Length)) };
        try { Marshal.Copy(bytes, 0, blob.Data, bytes.Length); return blob; }
        catch { Release(ref blob, native: false); throw; }
    }

    private static void Release(ref DataBlob blob, bool native)
    {
        if (blob.Data == IntPtr.Zero) return;
        try
        {
            for (int offset = 0; offset < blob.Length;)
            {
                int count = Math.Min(ZeroBlock.Length, blob.Length - offset);
                Marshal.Copy(ZeroBlock, 0, IntPtr.Add(blob.Data, offset), count);
                offset += count;
            }
        }
        finally
        {
            if (native) LocalFree(blob.Data);
            else Marshal.FreeHGlobal(blob.Data);
            blob = default;
        }
    }

    private static void Clear(byte[]? bytes)
    {
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("비밀번호 저장은 Windows에서 지원됩니다.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("Crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("Kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
