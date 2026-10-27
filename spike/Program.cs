using System.IO.Ports;

// Proxmark3 protocol spike. Usage:
//   dotnet run --                     -> list COM ports
//   dotnet run -- COM5 version        -> send OLD CMD_VERSION, dump reply
//   dotnet run -- COM5 read           -> send OLD CMD_HF_ISO14443A_READER (ISO14A_CONNECT), dump reply
//   dotnet run -- COM5 read 3         -> same, arg0 = 3 (CONNECT|NO_DISCONNECT)
//   dotnet run -- COM5 ng-version     -> send an NG CMD_VERSION frame instead (to confirm OLD vs NG)
//   dotnet run -- COM5 raw 50 4D ...  -> send literal bytes
//   dotnet run -- COM5 oldcmd 0x0385 1   -> send any OLD command id with arg0
//
// OLD frame: 544 bytes = uint64 cmd + uint64 arg[3] + 512 data, little-endian, no magic.
// NG frame : "PM3a" magic + (len|0x8000) + cmd16 + data + 0x3361 postamble.

const int OldFrameSize = 544;

if (args.Length == 0)
{
    Console.WriteLine("COM ports: " + string.Join(", ", SerialPort.GetPortNames().OrderBy(x => x)));
    Console.WriteLine("Usage: dotnet run -- <PORT> <version|read [arg0]|ng-version|oldcmd <id> [arg0]|raw <bytes...>>");
    return;
}

string port = args[0];
string cmd = args.Length > 1 ? args[1].ToLowerInvariant() : "version";

// old <id> <arg0> <arg1> <arg2> [data bytes...]  — fully general OLD command.
byte[] frame = cmd switch
{
    "version"    => OldFrame(0x0107),
    "read"       => OldFrame(0x0385, args.Length > 2 ? ParseU(args[2]) : 1),   // CMD_READER_ISO_14443a, arg0=flags
    "ng-version" => NgFrame(0x0107),
    "oldcmd"     => OldFrame(ParseU(args[2]), args.Length > 3 ? ParseU(args[3]) : 0),
    "old"        => OldFrameFull(args),                                        // id arg0 arg1 arg2 [data...]
    "raw"        => args.Skip(2).Select(s => (byte)ParseU(s)).ToArray(),
    _            => OldFrame(0x0107),
};

static byte[] OldFrameFull(string[] a)
{
    ulong id = ParseU(a[2]);
    ulong a0 = a.Length > 3 ? ParseU(a[3]) : 0;
    ulong a1 = a.Length > 4 ? ParseU(a[4]) : 0;
    ulong a2 = a.Length > 5 ? ParseU(a[5]) : 0;
    var data = a.Skip(6).Select(s => (byte)ParseU(s)).ToArray();
    return OldFrame(id, a0, a1, a2, data);
}

Console.WriteLine($"Opening {port} …");
using var sp = new SerialPort(port, 115200)
{
    ReadTimeout = 300,
    WriteTimeout = 1000,
    DtrEnable = true,
    RtsEnable = true,
};
sp.Open();
Thread.Sleep(150);

// Drain anything already queued (a fresh connect, or leftovers).
Drain(sp, "before");

// magicget: on ONE open, poll the HF reader until a card couples, then immediately read block 0 via the gen1a
// backdoor. Catches a brief coupling window that separate invocations would miss.
if (cmd == "magicget")
{
    byte[] readF = OldFrame(0x0385, 1);
    byte[] cgetF = OldFrame(0x0606, 0x1E, 0, 0);
    for (int i = 1; i <= 40; i++)
    {
        Drain(sp, "poll");
        sp.Write(readF, 0, readF.Length);
        var rr = ReadFor(sp, 500);
        ulong a0 = rr.Length >= 16 ? BitConverter.ToUInt64(rr, 8) : 0;
        if (a0 >= 1)
        {
            string uid = rr.Length >= 36 ? Hex(rr.AsSpan(32, 4).ToArray()) : "?";
            Console.WriteLine($"poll {i}: CARD uid={uid} -> firing backdoor read of block 0");
            Drain(sp, "pre-cget");
            sp.Write(cgetF, 0, cgetF.Length);
            var cr = ReadFor(sp, 1500);
            Console.WriteLine($"CGETBLOCK reply {cr.Length} bytes:");
            DumpOld(cr);
            return;
        }
        if (i % 5 == 0) Console.WriteLine($"poll {i}: no card yet…");
    }
    Console.WriteLine("Never coupled in 40 tries.");
    return;
}

// magicdump: poll until coupled, then read all 64 blocks (1K) via the backdoor, per block, reporting how many
// succeed — tests whether coupling holds for a whole dump, or whether we need a field-on sequence.
if (cmd == "magicdump")
{
    byte[] readF = OldFrame(0x0385, 1);
    bool coupled = false;
    for (int i = 0; i < 40 && !coupled; i++)
    {
        Drain(sp, "poll");
        sp.Write(readF, 0, readF.Length);
        var rr = ReadFor(sp, 500);
        coupled = (rr.Length >= 16 ? BitConverter.ToUInt64(rr, 8) : 0) >= 1;
    }
    if (!coupled) { Console.WriteLine("Never coupled."); return; }
    Console.WriteLine("coupled -> dumping 64 blocks");

    int ok = 0;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    for (int b = 0; b < 64; b++)
    {
        sp.Write(OldFrame(0x0606, 0x1E, 0, (ulong)b), 0, OldFrameSize);
        var cr = ReadFor(sp, 1200);
        ulong rcmd = cr.Length >= 8 ? BitConverter.ToUInt64(cr, 0) : 0;
        ulong a0 = cr.Length >= 16 ? BitConverter.ToUInt64(cr, 8) : 0;
        if (rcmd == 0x00ff && a0 >= 1 && cr.Length >= 48)
        {
            ok++;
            Console.WriteLine($"blk {b,2}: {string.Join(" ", cr.AsSpan(32, 16).ToArray().Select(x => x.ToString("X2")))}");
        }
        else
        {
            string t = new string(cr.Where(x => x is >= 0x20 and < 0x7F).Select(x => (char)x).ToArray());
            Console.WriteLine($"blk {b,2}: FAIL ({(t.Length > 2 ? t : $"cmd 0x{rcmd:X} a0 {a0}")})");
        }
    }
    Console.WriteLine($"\n{ok}/64 blocks read in {sw.ElapsedMilliseconds} ms");
    return;
}

// magicdump2: fast dump — one unlock, field stays on across all 64 blocks. First block INIT|WUPC (0x0A, field on +
// unlock, no halt/off), middle blocks workFlags=0 (just read), last block HALT|OFF (0x14, clean up).
if (cmd == "magicdump2")
{
    byte[] readF = OldFrame(0x0385, 1);
    bool coupled = false;
    for (int i = 0; i < 40 && !coupled; i++)
    {
        Drain(sp, "poll");
        sp.Write(readF, 0, readF.Length);
        var rr = ReadFor(sp, 500);
        coupled = (rr.Length >= 16 ? BitConverter.ToUInt64(rr, 8) : 0) >= 1;
    }
    if (!coupled) { Console.WriteLine("Never coupled."); return; }
    Console.WriteLine("coupled -> fast dump");

    int ok = 0;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    for (int b = 0; b < 64; b++)
    {
        ulong wf = b == 0 ? 0x0AUL : b == 63 ? 0x14UL : 0x00UL;
        sp.Write(OldFrame(0x0606, wf, 0, (ulong)b), 0, OldFrameSize);
        var cr = ReadFor(sp, 1200);
        ulong rcmd = cr.Length >= 8 ? BitConverter.ToUInt64(cr, 0) : 0;
        ulong a0 = cr.Length >= 16 ? BitConverter.ToUInt64(cr, 8) : 0;
        if (rcmd == 0x00ff && a0 >= 1 && cr.Length >= 48) { ok++; Console.WriteLine($"blk {b,2}: {string.Join(" ", cr.AsSpan(32, 16).ToArray().Select(x => x.ToString("X2")))}"); }
        else { string t = new string(cr.Where(x => x is >= 0x20 and < 0x7F).Select(x => (char)x).ToArray()); Console.WriteLine($"blk {b,2}: FAIL ({(t.Length > 2 ? t : $"cmd 0x{rcmd:X} a0 {a0}")})"); }
    }
    Console.WriteLine($"\n{ok}/64 blocks read in {sw.ElapsedMilliseconds} ms");
    return;
}

// magicset [16 hex bytes]: poll until coupled, write block 0 via the gen1a backdoor, then read it back to verify.
// With no bytes given it re-writes the CURRENT block 0 (a no-op) — the safe way to prove the write mechanism.
if (cmd == "magicset")
{
    byte[] block = args.Length >= 18
        ? args.Skip(2).Take(16).Select(s => (byte)ParseU(s)).ToArray()
        : [0x38, 0xC7, 0xE7, 0xEF, 0xF7, 0x08, 0x04, 0x00, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69];
    Console.WriteLine($"Will write block 0 = {Hex(block)}");

    byte[] readF = OldFrame(0x0385, 1);
    byte[] setF  = OldFrame(0x0605, 0, 0x1E, 0, block);   // CSETBLOCK: arg0=needWipe(0), arg1=workFlags, arg2=blockNo
    byte[] cgetF = OldFrame(0x0606, 0x1E, 0, 0);
    for (int i = 1; i <= 40; i++)
    {
        Drain(sp, "poll");
        sp.Write(readF, 0, readF.Length);
        var rr = ReadFor(sp, 500);
        if ((rr.Length >= 16 ? BitConverter.ToUInt64(rr, 8) : 0) >= 1)
        {
            Console.WriteLine($"poll {i}: coupled -> writing block 0");
            Drain(sp, "pre-set");
            sp.Write(setF, 0, setF.Length);
            var sr = ReadFor(sp, 2000);
            Console.WriteLine($"CSETBLOCK reply {sr.Length} bytes:");
            DumpOld(sr);

            Drain(sp, "pre-verify");
            sp.Write(cgetF, 0, cgetF.Length);
            var vr = ReadFor(sp, 1500);
            Console.WriteLine($"\nverify read-back {vr.Length} bytes:");
            DumpOld(vr);
            return;
        }
        if (i % 5 == 0) Console.WriteLine($"poll {i}: no card yet…");
    }
    Console.WriteLine("Never coupled in 40 tries.");
    return;
}

Console.WriteLine($"TX ({frame.Length} bytes): {Hex(frame.AsSpan(0, Math.Min(frame.Length, 16)).ToArray())}{(frame.Length > 16 ? " …" : "")}");
sp.Write(frame, 0, frame.Length);

// Read everything the device sends back for ~2.5 s.
var rx = ReadFor(sp, 2500);
Console.WriteLine($"\nRX {rx.Length} bytes:");
DumpOld(rx);

static void Drain(SerialPort sp, string when)
{
    var buf = new byte[1024];
    int total = 0;
    var until = DateTime.UtcNow.AddMilliseconds(400);
    while (DateTime.UtcNow < until)
    {
        try { int n = sp.Read(buf, 0, buf.Length); total += n; until = DateTime.UtcNow.AddMilliseconds(200); }
        catch (TimeoutException) { break; }
    }
    if (total > 0) Console.WriteLine($"(drained {total} stale bytes {when} the command)");
}

static byte[] ReadFor(SerialPort sp, int ms)
{
    var all = new List<byte>();
    var buf = new byte[1024];
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < until)
    {
        try { int n = sp.Read(buf, 0, buf.Length); if (n > 0) all.AddRange(buf[..n]); }
        catch (TimeoutException) { if (all.Count > 0) break; }   // data then a gap = reply complete
    }
    return all.ToArray();
}

static void DumpOld(byte[] b)
{
    if (b.Length == 0) { Console.WriteLine("(nothing)"); return; }
    if (b.Length >= 8)
    {
        ulong c = BitConverter.ToUInt64(b, 0);
        Console.WriteLine($"  OLD cmd = 0x{c:X}");
        if (b.Length >= 32)
            Console.WriteLine($"  arg0 = 0x{BitConverter.ToUInt64(b, 8):X}  arg1 = 0x{BitConverter.ToUInt64(b, 16):X}  arg2 = 0x{BitConverter.ToUInt64(b, 24):X}");
    }
    // hex + ascii, 16 per line
    for (int i = 0; i < b.Length; i += 16)
    {
        var line = b.AsSpan(i, Math.Min(16, b.Length - i)).ToArray();
        string hex = string.Join(" ", line.Select(x => x.ToString("X2"))).PadRight(16 * 3);
        string asc = new string(line.Select(x => x is >= 0x20 and < 0x7F ? (char)x : '.').ToArray());
        Console.WriteLine($"  {i:D4}: {hex} {asc}");
    }
}

static byte[] OldFrame(ulong cmd, ulong arg0 = 0, ulong arg1 = 0, ulong arg2 = 0, byte[]? data = null)
{
    var f = new byte[OldFrameSize];
    BitConverter.TryWriteBytes(f.AsSpan(0), cmd);
    BitConverter.TryWriteBytes(f.AsSpan(8), arg0);
    BitConverter.TryWriteBytes(f.AsSpan(16), arg1);
    BitConverter.TryWriteBytes(f.AsSpan(24), arg2);
    if (data is { Length: > 0 }) Array.Copy(data, 0, f, 32, Math.Min(data.Length, 512));
    return f;
}

static byte[] NgFrame(ushort cmd, byte[]? data = null)
{
    data ??= [];
    ushort lenWord = (ushort)((data.Length & 0x7FFF) | 0x8000);
    var f = new byte[8 + data.Length + 2];
    f[0] = 0x50; f[1] = 0x4D; f[2] = 0x33; f[3] = 0x61;
    f[4] = (byte)(lenWord & 0xFF); f[5] = (byte)(lenWord >> 8);
    f[6] = (byte)(cmd & 0xFF); f[7] = (byte)(cmd >> 8);
    data.CopyTo(f, 8);
    int crc = 8 + data.Length;
    f[crc] = 0x61; f[crc + 1] = 0x33;
    return f;
}

static ulong ParseU(string s) =>
    s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToUInt64(s[2..], 16)
        : Convert.ToUInt64(s, 10);

static string Hex(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));
