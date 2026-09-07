using System.Buffers.Binary;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Frlg.Trade.Core;

public static class Bin
{
    public static ushort U16(ReadOnlySpan<byte> b, int o = 0) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    public static uint U32(ReadOnlySpan<byte> b, int o = 0) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    public static ulong U64(ReadOnlySpan<byte> b, int o = 0) => BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
    public static ushort B16(ReadOnlySpan<byte> b, int o = 0) => BinaryPrimitives.ReadUInt16BigEndian(b[o..]);
    public static uint B32(ReadOnlySpan<byte> b, int o = 0) => BinaryPrimitives.ReadUInt32BigEndian(b[o..]);
    public static ulong B64(ReadOnlySpan<byte> b, int o = 0) => BinaryPrimitives.ReadUInt64BigEndian(b[o..]);
    public static void W16(Span<byte> b, int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(b[o..], (ushort)v);
    public static void W32(Span<byte> b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b[o..], v);
    public static void W64(Span<byte> b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b[o..], v);
    public static void WB16(Span<byte> b, int o, int v) => BinaryPrimitives.WriteUInt16BigEndian(b[o..], (ushort)v);
    public static void WB32(Span<byte> b, int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b[o..], v);
    public static void WB64(Span<byte> b, int o, ulong v) => BinaryPrimitives.WriteUInt64BigEndian(b[o..], v);
    public static byte[] Hex(string hex) => Convert.FromHexString(hex);
    public static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    public static byte[] Pad(byte[] data, int length, byte value = 0)
    { var result = Enumerable.Repeat(value, length).ToArray(); data.AsSpan(0, Math.Min(data.Length, length)).CopyTo(result); return result; }
    public static string Mac(byte[] bytes) => string.Join(':', bytes.Select(b => b.ToString("x2")));
    public static bool Less(int a, int b) => ((b - a) & 65535) is > 0 and < 32768;
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in data) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ (0xedb88320u & (0u - (crc & 1))); }
        return ~crc;
    }
    public static byte[] Ecb(byte[] key, byte[] data, bool encrypt = false)
    { using var aes = Aes.Create(); aes.Key = key; return encrypt ? aes.EncryptEcb(data, PaddingMode.None) : aes.DecryptEcb(data, PaddingMode.None); }
    public static byte[] Gcm(byte[] key, byte[] nonce, byte[] input, byte[] aad, bool encrypt, int tagBytes = 16)
    {
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(encrypt, new AeadParameters(new KeyParameter(key), tagBytes * 8, nonce, aad));
        var output = new byte[cipher.GetOutputSize(input.Length)];
        int n = cipher.ProcessBytes(input, 0, input.Length, output, 0);
        n += cipher.DoFinal(output, n);
        return output[..n];
    }
    public static byte[] Ctr(byte[] key, byte[] nonce4, byte[] input)
    {
        var counter = new byte[16]; nonce4.CopyTo(counter, 0);
        var result = new byte[input.Length];
        for (int i = 0; i < input.Length; i += 16)
        {
            var mask = Ecb(key, counter, true);
            for (int j = 0; j < Math.Min(16, input.Length - i); j++) result[i + j] = (byte)(input[i + j] ^ mask[j]);
            for (int j = 15; j >= 4 && ++counter[j] == 0; j--) { }
        }
        return result;
    }
}
