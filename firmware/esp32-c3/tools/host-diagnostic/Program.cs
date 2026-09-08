using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Frlg.Trade.Core;

if (args.Length != 2 || args[0] is not ("serial" or "auth" or "pia" or "room")) {
    Console.Error.WriteLine("Usage: C3.Diagnostic <serial|auth|pia|room> COM<number>"); return 2;
}
using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(240));
using var device = new SerialDevice(args[1], cancel.Token);
bool begun = false;
try {
    device.Handshake(); begun = true;
    if (device.Model != "esp32c3") throw new InvalidOperationException("Expected an ESP32-C3 device");
    Console.WriteLine("C3 protocol v1 handshake and session begin: OK");
    for (int i = 0; i < 50; ++i) device.Command("LDN_SCAN " + (i % 11 + 1));
    foreach (var bad in new[] { "LDN_TEST_UNKNOWN", "LDN_CONFIG invalid", "LDN_SCAN 0", "LDN_SCAN 12" }) {
        bool rejected = false;
        try { device.Command(bad); } catch (ConnectionException) { rejected = true; }
        if (!rejected) throw new InvalidDataException("Invalid command accepted");
    }
    device.Command("LDN_SCAN 1");
    Console.WriteLine("50 channel changes, malformed commands and recovery: OK");
    void Status() { foreach (string line in device.Command("LDN_STATUS")) if (line.StartsWith("LDN_C3_STATS")) Console.WriteLine(line); }
    Status();
    if (args[0] == "serial") return 0;
    var keys = KeyFile.LoadDefault();
    string Text(SerialFrame f) => Encoding.UTF8.GetString(f.Payload);
    LdnNetwork? Advertisement(SerialFrame f) {
        if (f.Kind != 3) return null;
        var fields = Text(f).Split(' ');
        if (fields.Length != 4 || fields[0] != "LDN_ADV") return null;
        try { return LdnKeys.Decode(keys, Bin.Hex(fields[3]), Bin.Hex(fields[1].Replace(":", "")), int.Parse(fields[2])); }
        catch (Exception e) when (e is InvalidDataException or FormatException or System.Security.Cryptography.CryptographicException or Org.BouncyCastle.Crypto.InvalidCipherTextException) { return null; }
    }
    void Diagnostic(SerialFrame f) {
        if (f.Kind != 3) return;
        string text = Text(f);
        if (text.StartsWith("LDN_KEY_STATUS") || text.StartsWith("LDN_AUTH_PORT") || text.StartsWith("LDN_DISCONNECTED") || text.StartsWith("LDN_ERROR")) Console.WriteLine(text);
    }
    LdnNetwork? room = null;
    var scan = Stopwatch.StartNew();
    while (room == null && scan.Elapsed.TotalSeconds < 25) {
        foreach (int ch in new[] { 1, 6, 11, 2, 3, 4, 5, 7, 8, 9, 10 }) {
            device.Command($"LDN_SCAN {ch}");
            var dwell = Stopwatch.StartNew();
            while (dwell.ElapsedMilliseconds < 600 && room == null) {
                foreach (var f in device.Drain()) {
                    var candidate = Advertisement(f);
                    if (candidate?.CommunicationId == TradeSession.FireRedId && candidate.Policy == 0 && candidate.Members.Count < candidate.Maximum) room = candidate;
                }
                Thread.Sleep(2);
            }
            if (room != null) break;
        }
    }
    if (room == null) throw new TimeoutException("No authenticated FireRed Leader advertisement found");
    Console.WriteLine($"Room broadcast verified: channel={room.Channel}, protocol={room.Protocol}");
    var derived = new LdnKeys(keys, room.Protocol);
    var auth = new LdnAuthentication(room, derived);
    device.Command($"LDN_CONFIG {room.Channel} {Convert.ToHexString(room.Ssid).ToLowerInvariant()} {Bin.Mac(room.Host)} {Convert.ToHexString(derived.Data(room))}", 8);
    byte[]? mac = null;
    LdnNetwork? membership = null;
    bool accepted = false;
    var join = Stopwatch.StartNew();
    double nextStatus = 0, nextAuth = double.PositiveInfinity;
    int attempts = 0;
    while (join.Elapsed.TotalSeconds < 40 && !(accepted && membership != null)) {
        double now = join.Elapsed.TotalSeconds;
        if (now >= nextStatus) {
            foreach (string line in device.Command("LDN_STATUS")) {
                if (line.StartsWith("LDN_LINK 1 ") && mac == null) { mac = Bin.Hex(line.Split(' ')[2].Replace(":", "")); nextAuth = now; Console.WriteLine("Wireless association: OK"); }
                if (line.StartsWith("LDN_C3_STATS")) Console.WriteLine(line);
            }
            nextStatus = now + 2;
        }
        if (now >= nextAuth && !accepted && attempts < 5) {
            device.Command("LDN_TX " + Convert.ToHexString(auth.Request)); nextAuth = now + 1; attempts++;
            Console.WriteLine($"Authentication request {attempts} sent");
        }
        foreach (var f in device.Drain()) {
            Diagnostic(f);
            if (f.Kind == 3 && Text(f).StartsWith("LDN_RX ")) {
                var fields = Text(f).Split(' ');
                if (fields.Length == 3 && Bin.Hex(fields[1].Replace(":", "")).AsSpan().SequenceEqual(room.Host)) {
                    auth.Accept(Bin.Hex(fields[2])); accepted = true;
                    Console.WriteLine("Authenticated response and challenge: OK");
                }
            }
            var current = Advertisement(f);
            if (current != null && current.Id.AsSpan().SequenceEqual(room.Id) && current.Random.AsSpan().SequenceEqual(room.Random) && current.Host.AsSpan().SequenceEqual(room.Host) && mac != null && current.Members.Any(m => m.Mac.AsSpan().SequenceEqual(mac))) membership = current;
        }
        Thread.Sleep(2);
    }
    if (!accepted || membership == null || mac == null) throw new TimeoutException("LDN association/authentication did not complete");
    var ours = membership.Members.Single(m => m.Mac.AsSpan().SequenceEqual(mac));
    var leader = membership.Members.Single(m => m.Index == 0);
    device.Command($"LDN_NET {ours.Ip} {leader.Ip}");
    foreach (var peer in membership.Members.Where(m => m.Index != ours.Index)) device.Command($"LDN_NEIGH {peer.Ip} {Convert.ToHexString(peer.Mac)}");
    Console.WriteLine("Room membership and UDP interface setup: OK");
    if (args[0] is "pia" or "room") {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "assets", "party"))) root = root.Parent;
        if (root == null) throw new DirectoryNotFoundException("Run this diagnostic from the source tree");
        var party = new byte[]?[] { File.ReadAllBytes(Path.Combine(root.FullName, "assets/party/mewtwo.pk3")),
            File.ReadAllBytes(Path.Combine(root.FullName, "assets/party/deoxys.pk3")), null, null, null, null };
        var engine = new TradeEngine(party, 1);
        var elapsed = Stopwatch.StartNew();
        string run = Path.Combine(root.FullName, "local", "esp32-c3", "runs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
        Directory.CreateDirectory(run);
        engine.Committed += (data, _) => {
            File.WriteAllBytes(Path.Combine(run, "received.pk3"), data);
            Console.WriteLine("Trade committed; received.pk3 saved in the local diagnostic run");
        };
        using var capture = new StreamWriter(Path.Combine(run, "pia.jsonl")) { AutoFlush = true };
        capture.WriteLine(JsonSerializer.Serialize(new { rec = "meta", ip = ours.Ip, host = leader.Ip, ssid_hex = Convert.ToHexString(room.Ssid) }));
        void Capture(string dir, byte[] data, string source, string destination) => capture.WriteLine(JsonSerializer.Serialize(new { rec = "pkt", t = elapsed.Elapsed.TotalSeconds, dir, src = source + ":12345", dst = destination + ":12345", hex = Convert.ToHexString(data) }));
        using var sim = new Simulator(room.Ssid, mac, room.Host, ours.Ip, leader.Ip, engine,
            (data, destination) => { device.SendDatagram(data, destination); Capture("out", data, ours.Ip, destination); });
        sim.Log += message => Console.WriteLine($"{elapsed.Elapsed.TotalSeconds:F2}s {message}");
        engine.Log += message => Console.WriteLine($"{elapsed.Elapsed.TotalSeconds:F2}s {message}");
        double nextTick = 0, nextPing = 0, nextReport = 5;
        while (elapsed.Elapsed.TotalSeconds < (args[0] == "room" ? 180 : 15) && !sim.HostDisconnected) {
            double now = elapsed.Elapsed.TotalSeconds;
            if (now >= nextPing) { device.Command("LDN_PING"); nextPing = now + 1; }
            foreach (var f in device.Drain()) {
                Diagnostic(f);
                if (f.Kind == 5 && f.Payload.Length >= 4) {
                    string source = new System.Net.IPAddress(f.Payload[..4]).ToString();
                    Capture("in", f.Payload[4..], source, ours.Ip);
                    sim.Receive(f.Payload[4..], source);
                }
            }
            if (now >= nextTick) { sim.Tick(); nextTick = now + 1 / 59.727; }
            if (now >= nextReport) {
                Status(); Console.WriteLine($"{now:F1}s receive_next={sim.Reliable.ReceiveNext:x4} gap={sim.Reliable.HasGap} pending={sim.Reliable.Pending.Count} room={engine.HostInSeat} serial_bad={device.BadFrames}");
                nextReport = now + 5;
            }
            if (args[0] == "pia" && now >= 3 && sim.ReceivedPackets >= 10 && sim.SentPackets >= 10) break;
            Thread.Sleep(1);
        }
        Console.WriteLine($"Pia packets: received={sim.ReceivedPackets} sent={sim.SentPackets} decrypt_failed={sim.DecryptFailures}");
        if (sim.ReceivedPackets < 10 || sim.SentPackets < 10 || sim.DecryptFailures != 0)
            throw new InvalidDataException("Bidirectional encrypted Pia traffic did not verify");
        if (args[0] == "room" && !engine.HostInSeat) throw new InvalidDataException("Room entry did not complete");
        if (args[0] == "room") Console.WriteLine($"Room entry verified; trade commits={engine.Commits}");
    }
    Status();
    Console.WriteLine($"Serial invalid frames: {device.BadFrames}");
    return 0;
} catch (Exception e) {
    Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); return 1;
} finally {
    if (begun) { try { device.Stop(); Console.WriteLine("Session stopped: OK"); } catch (Exception e) { Console.Error.WriteLine("Stop: " + e.Message); } }
}
