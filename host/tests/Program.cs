using System.Text.Json;
using Frlg.Trade.Core;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
int checks = 0;
void Check(bool condition, string label) { checks++; if (!condition) throw new Exception(label); }
void Equal(byte[] a, byte[] b, string label) => Check(a.AsSpan().SequenceEqual(b), label);
void Reject(Action action, string label)
{
    try { action(); }
    catch (Exception e) when (e is InvalidDataException or System.Security.Cryptography.CryptographicException or Org.BouncyCastle.Crypto.InvalidCipherTextException)
    { checks++; return; }
    throw new Exception(label);
}

if (args.Length >= 2 && args[0] == "--device")
{
    using var device = new SerialDevice(args[1], CancellationToken.None);
    device.Handshake(); Console.WriteLine($"Device handshake: {device.Model}; protocol v1");
    device.Command("LDN_SCAN 1"); Console.WriteLine("Dynamic scan: OK");
    try { device.Command("LDN_TEST_UNKNOWN"); throw new Exception("Unknown command accepted"); }
    catch (ConnectionException e) when (e.Message.Contains("UNKNOWN_COMMAND")) { Console.WriteLine("Unknown command rejection: OK"); }
    try { device.Command("LDN_CONFIG invalid"); throw new Exception("Invalid config accepted"); }
    catch (ConnectionException) { Console.WriteLine("Invalid config rejection: OK"); }
    device.Command("LDN_SCAN 1"); Console.WriteLine("Command recovery: OK");
    device.Stop(); Console.WriteLine("Stop: OK"); return;
}
if (args.Length >= 2 && args[0] == "--live")
{
    var party = new byte[]?[] { File.ReadAllBytes(Path.Combine(root, "assets/party/mewtwo.pk3")), File.ReadAllBytes(Path.Combine(root, "assets/party/deoxys.pk3")), null, null, null, null };
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(8));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    string path = Path.Combine(root, "local/runs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-native-console");
    new TradeSession(e =>
    {
        var message = JsonSerializer.SerializeToElement(e); string kind = message.GetProperty("event").GetString()!;
        if (kind is "log" or "phase") Console.WriteLine(message.GetProperty("message").GetString());
        else Console.WriteLine(kind);
    }).Run(args[1], party, 1, path, cancellation.Token);
    return;
}

foreach (int size in new[] { 0, 1, 254, 255, 1476, 4080 })
{
    var payload = Enumerable.Range(0, size).Select(i => (byte)i).ToArray();
    var frame = new SerialFrame(4, 123, 456, payload); var bytes = SerialCodec.Encode(frame);
    var read = SerialCodec.Decode(bytes.AsSpan(0, bytes.Length - 1));
    Check(read != null && read.Session == 456 && read.Request == 123, "Serial header roundtrip"); Equal(payload, read!.Payload, "Serial payload roundtrip");
    bytes[bytes.Length / 2] ^= 0x22; Check(SerialCodec.Decode(bytes.AsSpan(0, bytes.Length - 1)) == null, "CRC rejects damaged frames");
}
Check(Bin.Crc32(System.Text.Encoding.ASCII.GetBytes("123456789")) == 0xcbf43926, "Standard CRC32 vector");
Reject(() => SerialCodec.Encode(new(1, 1, 1, new byte[4081])), "Oversized serial payload accepted");
Check(SerialCodec.Decode([0]) == null && SerialCodec.Decode([255, 1]) == null, "Malformed COBS rejected");
var sender = new ReliableLink { Next = 65534, Low = 65534 };
var receiver = new ReliableLink { ReceiveNext = 65534 };
foreach (int expected in new[] { 65534, 65535, 0 }) Check(sender.Queue([1], 7, 0).Sequence == expected, "Reliable sequence wraps");
receiver.Receive(0); receiver.Receive(0); receiver.Receive(65535);
Check(receiver.ReceiveNext == 65534 && receiver.HasGap, "Gap and duplicate do not advance receive cursor");
for (int i = 0; i < 3; i++) sender.Acknowledge(receiver.Ack(), 10);
Check(sender.Pending.Count == 3 && sender.Pending[0].Acked && sender.Pending[65535].Acked, "Selective ACK retains missing head");
var resent = sender.Retransmit(11, 2);
Check(resent.Count == 1 && resent[0].Sequence == 65534, "Three gap ACKs retransmit only missing packet");
receiver.Receive(65534); receiver.Receive(65535);
Check(receiver.ReceiveNext == 1 && !receiver.HasGap, "Contiguous receive catches up across wrap");
sender.Acknowledge(receiver.Ack(), 20);
Check(sender.Pending.Count == 0 && sender.SendLow == 1, "Cumulative ACK drains send window");
var shifted = new ReliableLink();
shifted.Receive(0x12, 0x11);
Check(shifted.ReceiveNext == 0x11 && shifted.HasGap, "Peer send-window base preserves missing first packet");
shifted.Receive(0x11, 0x11);
Check(shifted.ReceiveNext == 0x13 && !shifted.HasGap && Bin.B16(shifted.Ack(), 2) == 0x13, "Non-default peer sequence advances cumulative ACK");
shifted.Receive(0x11, 0x11);
shifted.Receive(0x15, 0x15);
Check(shifted.ReceiveNext == 0x13 && shifted.HasGap, "Later window bases and duplicates cannot reset receive state");
shifted.Receive(0x13, 0x13); shifted.Receive(0x14, 0x14);
Check(shifted.ReceiveNext == 0x16 && !shifted.HasGap, "Missing packets still close later receive gaps");
var shiftedWrap = new ReliableLink();
shiftedWrap.Receive(0, 65535); shiftedWrap.Receive(65535, 65535);
Check(shiftedWrap.ReceiveNext == 1 && !shiftedWrap.HasGap, "Peer-selected receive base wraps correctly");
sender.Queue([2], 7, 100);
Check(sender.Retransmit(100 + sender.Rto - 1, 1).Count == 0, "No premature retransmit");
Check(sender.Retransmit(100 + sender.Rto + 1, 1).Count == 1, "RTO recovers unacknowledged packet");
string temp = Path.Combine(Path.GetTempPath(), "frlg-native-test-" + Guid.NewGuid());
Directory.CreateDirectory(temp);
try
{
    string exe = Path.Combine(temp, "app"), home = Path.Combine(temp, "home");
    Directory.CreateDirectory(exe); Directory.CreateDirectory(Path.Combine(home, ".switch"));
    try { KeyFile.Find(exe, home); throw new Exception("Missing keys accepted"); } catch (MissingKeysException) { checks++; }
    string homeFile = Path.Combine(home, ".switch", "prod.keys"), exeFile = Path.Combine(exe, "prod.keys");
    File.WriteAllText(homeFile, "placeholder"); Check(KeyFile.Find(exe, home) == homeFile, "Home fallback");
    File.WriteAllText(exeFile, "invalid"); Check(KeyFile.Find(exe, home) == exeFile, "Exe takes precedence");
    try { new KeyFile(KeyFile.Find(exe, home)); throw new Exception("Invalid keys accepted"); } catch (InvalidDataException) { checks++; }
    using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "host/tests/fixtures/vectors.json")));
    var v = vectors.RootElement;
    File.WriteAllLines(exeFile, v.GetProperty("keys").EnumerateObject().Select(p => p.Name + " = " + p.Value.GetString()));
    var keys = new KeyFile(exeFile);
    foreach (var a in v.GetProperty("advertisements").EnumerateArray())
    {
        var raw = Bin.Hex(a.GetProperty("raw").GetString()!); var network = LdnKeys.Decode(keys, raw, Bin.Hex("020000000001"), 1);
        Check(network.Protocol == a.GetProperty("protocol").GetInt32() && network.AppVersion == 88 && network.Members.Count == 1, "LDN advertisement fields");
        Equal(new LdnKeys(keys, network.Protocol).Data(network), Bin.Hex(a.GetProperty("dataKey").GetString()!), "LDN data key matches reference");
        raw[^1] ^= 1;
        Reject(() => LdnKeys.Decode(keys, raw, Bin.Hex("020000000001"), 1), "Tampered LDN advertisement accepted");
        if (network.Protocol == 3)
        {
            var derived = new LdnKeys(keys, 3); var auth = new LdnAuthentication(network, derived);
            byte[] header = auth.Request[6..78], requestKey = derived.Derive(header[56..72]);
            var plain = Bin.Gcm(requestKey, header[..12], Bin.Join(auth.Request[94..], auth.Request[78..94]), header, false);
            var response = new byte[388];
            plain.AsSpan(164, 16).CopyTo(response.AsSpan(188, 16));
            System.Security.Cryptography.HMACSHA256.HashData(LdnKeys.ChallengeKey, response.AsSpan(180)).CopyTo(response, 136);
            header[1] = 132; header[4] = 1; header[3] = 1;
            byte[] WrapResponse(byte[] payload)
            {
                var encrypted = Bin.Gcm(requestKey, header[..12], payload, header, true);
                return Bin.Join(Bin.Hex("0022aa010200"), header, encrypted[^16..], encrypted[..^16]);
            }
            var valid = WrapResponse(response); auth.Accept(valid); checks++;
            var damaged = (byte[])valid.Clone(); damaged[78] ^= 1;
            Reject(() => auth.Accept(damaged), "Tampered authentication tag accepted");
            damaged = (byte[])valid.Clone(); damaged[46] ^= 1;
            Reject(() => auth.Accept(damaged), "Foreign authentication session accepted");
            response[188] ^= 1;
            System.Security.Cryptography.HMACSHA256.HashData(LdnKeys.ChallengeKey, response.AsSpan(180)).CopyTo(response, 136);
            Reject(() => auth.Accept(WrapResponse(response)), "Validly signed but foreign challenge accepted");
        }
    }
    Equal(Rfu.PlayerBlock(), Bin.Hex(v.GetProperty("player").GetString()!), "LinkPlayer fixture");
    Equal(Rfu.TrainerCard(), Bin.Hex(v.GetProperty("card").GetString()!), "Trainer card fixture");
    var ni = Rfu.GameData(); foreach (var n in v.GetProperty("ni").EnumerateArray()) Equal(ni.Dequeue(), Bin.Hex(n.GetString()!), "NI fixture");
    var party = new byte[]?[] { File.ReadAllBytes(Path.Combine(root, "assets/party/mewtwo.pk3")), File.ReadAllBytes(Path.Combine(root, "assets/party/deoxys.pk3")), null, null, null, null };
    for (int i = 0; i < 2; i++) Equal(TradeEngine.ToWire(party[i]!), Bin.Hex(v.GetProperty("party")[i].GetString()!), "PK3 wire encoding fixture");
    using var crypto = new PiaCrypto(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
    using (var sim = new Simulator(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(),
        Bin.Hex("020000000002"), Bin.Hex("020000000001"), "169.254.1.2", "169.254.1.1", new TradeEngine(party, 1), (_, _) => { }))
    {
        var peer = new ReliableLink { Next = 0x11, Low = 0x11 };
        var first = peer.Queue([0, 0], 15, 0);
        var message = PiaCrypto.Message(new(10, peer.Wrap(first)));
        var datagram = crypto.Encrypt(message, "169.254.1.1", 0xc493, 0x7620, 1, 1, 0, 0);
        sim.Receive(datagram, "169.254.1.1");
        Check(sim.ReceivedPackets == 1 && sim.Reliable.ReceiveNext == 0x12 && !sim.Reliable.HasGap,
            "Authenticated Pia packet initializes peer sequence instead of waiting for fff0");
        sim.Receive(datagram, "169.254.1.1");
        Check(sim.Reliable.ReceiveNext == 0x12, "Retransmitted opening packet does not reset peer sequence");
    }
    foreach (var row in v.GetProperty("pia").EnumerateArray())
    {
        var plain = Bin.Hex(row.GetProperty("plain").GetString()!); var encrypted = Bin.Hex(row.GetProperty("encrypted").GetString()!);
        Equal(crypto.Decrypt(encrypted, "169.254.1.1"), plain, "Pia GCM8 decryption fixture");
        Equal(crypto.Encrypt(plain, "169.254.1.1", 0x7620, 0xc493, 0, (ulong)plain.Length + 1, 0x50, 2), encrypted, "Pia GCM8 encryption fixture");
        Equal(crypto.Decompress(Bin.Hex(row.GetProperty("compressed").GetString()!)), plain, "Zstd decompression fixture");
        Equal(crypto.Decompress(crypto.Compress(plain)), plain, "Native zstd roundtrip");
        encrypted[21] ^= 1;
        Reject(() => crypto.Decrypt(encrypted, "169.254.1.1"), "Tampered Pia tag accepted");
    }
    string path = Path.Combine(root, "local/native-tests/engine.json");
    if (File.Exists(path))
    {
        using var records = JsonDocument.Parse(File.ReadAllText(path)); var engine = new TradeEngine(party, 1); int index = 0;
        foreach (var record in records.RootElement.EnumerateArray())
        {
            index++;
            if (record.TryGetProperty("slots", out var slots)) engine.Feed(slots.EnumerateArray().Select(s => Bin.Hex(s.GetString()!)).ToArray());
            else if (record.TryGetProperty("tick", out var words))
            {
                var actual = engine.Tick(); var expected = words.EnumerateArray().Select(w => w.GetInt32()).ToArray();
                Check(actual.SequenceEqual(expected), $"Trade engine differs at step {index}: {string.Join(',', actual)} expected {string.Join(',', expected)}");
            }
            else engine.Sit();
        }
        Console.WriteLine($"Trade engine replay: {index} steps, commits={engine.Commits}");
    }
    else Console.WriteLine("Private trade replay skipped: local/native-tests/engine.json is not present");
}
finally { Directory.Delete(temp, true); }
Console.WriteLine($"PASS {checks} checks");
