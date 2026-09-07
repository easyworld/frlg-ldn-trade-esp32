using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Frlg.Trade.Core;

public sealed record LdnMember(byte[] Mac, string Ip, string Name, int Index);
public sealed class LdnNetwork
{
    public int Protocol, Version, Channel, Security, Policy, AppVersion, Maximum;
    public byte[] Id = [], Ssid = [], Random = [], Host = [], ApplicationData = [];
    public ulong CommunicationId, Challenge;
    public List<LdnMember> Members = [];
    public byte[] LittleId()
    { var id = (byte[])Id.Clone(); Bin.W64(id, 0, CommunicationId); Bin.W16(id, 10, Bin.B16(Id, 10)); return id; }
}

public sealed class LdnKeys(KeyFile file, int protocol)
{
    public static readonly byte[] ChallengeKey = Bin.Hex("f84b487fb37251c263bf11609036589266af70ca79b44c93c7370c5769c0f602");
    public byte[] Derive(byte[] data, bool advertise = false)
    {
        var key = file.Get(protocol == 1 ? "master_key_00" : "master_key_12");
        key = Bin.Ecb(key, file.Get("aes_kek_generation_source"));
        key = Bin.Ecb(key, Bin.Hex(advertise ? "191884743e24c77d87c69e4207d0c438" : "f1e7018419a84f711da714c2cf919c9c"));
        key = Bin.Ecb(key, file.Get("aes_key_generation_source"));
        return Bin.Ecb(key, SHA256.HashData(data)[..16]);
    }
    public byte[] Data(LdnNetwork network) => Derive(Bin.Join(network.Random, Bin.Hex(
        "fcb6f6adb9dfea66aca9c326149d2b3b08a781895cbf78f720d78b85a57584a99665d237797b2a41ddef14063ec28d259143af7832fb3cbcf2759cbfbdc81d8c")));

    public static LdnNetwork Decode(KeyFile keys, byte[] raw, byte[] host, int channel)
    {
        if (raw.Length < 68 || raw.Length > 1536 || !raw.AsSpan(0, 8).SequenceEqual(Bin.Hex("7f0022aa04000101")))
            throw new InvalidDataException("Invalid LDN advertisement header");
        var network = new LdnNetwork { Host = host, Channel = channel, Id = raw[12..44], Version = raw[44] };
        if (network.Version is < 2 or > 4) throw new InvalidDataException("Unsupported LDN version");
        network.Protocol = raw[45] switch { 2 => 1, 3 => 3, _ => throw new InvalidDataException("Unauthenticated advertisement") };
        network.CommunicationId = Bin.B64(network.Id); network.Ssid = network.Id[16..32];
        var key = new LdnKeys(keys, network.Protocol).Derive(network.Id, true);
        int size = Bin.B16(raw, 46);
        byte[] data;
        if (network.Protocol == 3)
        {
            if (raw.Length != 68 + size) throw new InvalidDataException("Advertisement size mismatch");
            data = Bin.Gcm(key, Bin.Join(raw[48..52], new byte[8]), Bin.Join(raw[68..], raw[52..68]), raw[12..52], false);
            if (data.Length < 42) throw new InvalidDataException("Truncated advertisement");
            network.Random = data[..16]; network.Challenge = Bin.B64(data, 16);
            network.Security = data[24]; network.Policy = data[25]; network.AppVersion = Bin.B16(data, 26);
            int encodedChannel = Bin.B16(data, 36) & 1023;
            if (encodedChannel != 0 && encodedChannel != channel) throw new InvalidDataException("Channel mismatch");
            network.Maximum = data[38]; int count = data[39];
            if (count > 8 || network.Maximum > 8 || count > network.Maximum || data.Length < 42 + 48 * count)
                throw new InvalidDataException("Invalid member count");
            for (int i = 0; i < count; i++)
            {
                int o = 40 + 48 * i, index = data[o + 10];
                if (index > 7 || network.Members.Any(m => m.Index == index)) throw new InvalidDataException("Invalid member index");
                network.Members.Add(new(data[(o + 4)..(o + 10)], new IPAddress(data[o..(o + 4)]).ToString(), Encoding.UTF8.GetString(data, o + 12, 32).TrimEnd('\0'), index));
            }
            int appAt = 40 + 48 * count, appSize = Bin.B16(data, appAt);
            if (appSize > 384 || appAt + 2 + appSize != data.Length) throw new InvalidDataException("Invalid app data");
            network.ApplicationData = data[(appAt + 2)..];
        }
        else
        {
            if (size != 1280 || raw.Length != 52 + 32 + size) throw new InvalidDataException("Advertisement size mismatch");
            var plain = Bin.Ctr(key, raw[48..52], raw[52..]); data = plain[32..];
            if (!CryptographicOperations.FixedTimeEquals(plain[..32], SHA256.HashData(Bin.Join(raw[12..52], new byte[32], data))))
                throw new CryptographicException("Advertisement SHA mismatch");
            network.Random = data[..16]; network.Security = Bin.B16(data, 16); network.Policy = data[18];
            network.Maximum = data[22]; network.AppVersion = Bin.B16(data, 68);
            for (int i = 0; i < 8; i++)
            {
                int o = 24 + i * 56;
                if (data[o + 10] != 0) network.Members.Add(new(data[(o + 4)..(o + 10)], new IPAddress(data[o..(o + 4)]).ToString(), Encoding.UTF8.GetString(data, o + 12, 32).TrimEnd('\0'), i));
            }
            int sizeApp = Bin.B16(data, 474);
            if (sizeApp > 384) throw new InvalidDataException("Invalid app size");
            network.ApplicationData = data[476..(476 + sizeApp)]; network.Challenge = Bin.B64(data, 1272);
        }
        if (network.Security != 1) throw new InvalidDataException("Production security is required");
        return network;
    }
}

public sealed class LdnAuthentication
{
    private readonly LdnNetwork network;
    private readonly LdnKeys keys;
    private readonly byte[] random = RandomNumberGenerator.GetBytes(16);
    private readonly byte[] nonce = RandomNumberGenerator.GetBytes(8), device = RandomNumberGenerator.GetBytes(8);
    private bool verified;
    public byte[] Request { get; }
    public LdnAuthentication(LdnNetwork network, LdnKeys keys)
    {
        this.network = network; this.keys = keys;
        var payload = new byte[network.Version >= 3 ? 868 : 64];
        Encoding.UTF8.GetBytes("EMU").CopyTo(payload, 0); Bin.WB16(payload, 32, network.AppVersion);
        if (network.Version >= 3)
        {
            var body = new byte[720]; Bin.W64(body, 8, network.Challenge); nonce.CopyTo(body, 16); device.CopyTo(body, 24);
            HMACSHA256.HashData(LdnKeys.ChallengeKey, body).CopyTo(payload, 104);
            body.CopyTo(payload, 148);
        }
        var header = new byte[72]; header[0] = (byte)network.Version;
        header[1] = (byte)payload.Length; header[4] = (byte)(payload.Length >> 8); header[5] = (byte)(network.Protocol == 3 ? 1 : 0);
        network.LittleId().CopyTo(header, 8); network.Random.CopyTo(header, 40); random.CopyTo(header, 56);
        if (network.Protocol == 3)
        {
            var encrypted = Bin.Gcm(keys.Derive(random), header[..12], payload, header, true);
            payload = Bin.Join(encrypted[^16..], encrypted[..^16]);
        }
        Request = Bin.Join(Bin.Hex("0022aa010200"), header, payload);
    }
    public void Accept(byte[] frame)
    {
        if (frame.Length < 78 || !frame.AsSpan(0, 6).SequenceEqual(Bin.Hex("0022aa010200"))) throw new InvalidDataException("Invalid authentication frame");
        var header = frame[6..78];
        if (header[0] != network.Version || header[3] != 1 || header[5] != (network.Protocol == 3 ? 1 : 0) ||
            !header.AsSpan(8, 32).SequenceEqual(network.LittleId()) || !header.AsSpan(40, 16).SequenceEqual(network.Random) || !header.AsSpan(56, 16).SequenceEqual(random))
            throw new InvalidDataException("Authentication session mismatch");
        int size = header[1] | header[4] << 8;
        if (frame.Length != 78 + size + (network.Protocol == 3 ? 16 : 0)) throw new InvalidDataException("Authentication size mismatch");
        var payload = network.Protocol == 3 ? Bin.Gcm(keys.Derive(random), header[..12], Bin.Join(frame[94..], frame[78..94]), header, false) : frame[78..];
        if (header[2] != 0) throw new ConnectionException($"LDN authentication rejected: {header[2]}");
        if (network.Version >= 3)
        {
            if (verified && payload.Length == 132) return;
            if (payload.Length != 388) throw new InvalidDataException("Missing authentication challenge");
            var challenge = payload[132..];
            if (!CryptographicOperations.FixedTimeEquals(challenge[4..36], HMACSHA256.HashData(LdnKeys.ChallengeKey, challenge[48..])) ||
                !challenge.AsSpan(56, 8).SequenceEqual(nonce) || !challenge.AsSpan(64, 8).SequenceEqual(device))
                throw new CryptographicException("Authentication challenge mismatch");
        }
        verified = true;
    }
}
public sealed class ConnectionException(string message) : IOException(message);
