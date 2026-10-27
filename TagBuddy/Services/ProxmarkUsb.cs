#if ANDROID
using Android.App;
using Android.Content;
using Android.Hardware.Usb;

namespace TagBuddy.Services;

/// <summary>Experimental: talks to a Proxmark3 attached by USB-OTG. The Proxmark enumerates as a USB-CDC serial
/// device (vendor 0x9AC4). This opens that serial interface over Android's USB-Host API and sends the Proxmark's
/// own "NG" framed ping/version command, to prove whether the phone can speak to the device at all. It is a
/// diagnostic step toward a fuller client, not the client itself.</summary>
///
/// <remarks>
/// <para>The "NG" wire format (from the Proxmark3 source, <c>include/pm3_cmd.h</c>): a host→device command is an
/// 8-byte preamble — magic <c>0x61334D50</c> ("PM3a", little-endian so the bytes read 50 4D 33 61), then a
/// 16-bit field holding a 15-bit length with the top bit set to mean "NG", then the 16-bit command — followed by
/// the data and a 2-byte postamble. Over USB the link is reliable, so the postamble carries the magic
/// <c>0x3361</c> ("a3") in place of a real CRC and the device does not check it. A device→host response mirrors
/// this with magic <c>0x62334D50</c> ("PM3b"); seeing that header back is the proof the two can talk.</para>
/// </remarks>
public sealed class ProxmarkUsb
{
    /// <summary>Vendor ids a Proxmark3 enumerates with: 0x9AC4 (proxmark.org / RDV4 / the community firmware's
    /// signed descriptor) and 0x2D2D (the Proxmark3 Easy, product 0x504D = "PM").</summary>
    public static readonly int[] VendorIds = [0x9AC4, 0x2D2D];

    private static bool IsProxmark(UsbDevice d) => Array.IndexOf(VendorIds, d.VendorId) >= 0;

    private const string PermissionAction = "com.barcodebuddy.app.USB_PERMISSION";

    // Command ids, from include/pm3_cmd.h.
    private const ulong CmdVersion = 0x0107;
    private const ulong CmdPing = 0x0109;
    private const ulong CmdDebugPrintString = 0x0100;
    private const ulong CmdAck = 0x00ff;
    private const ulong CmdHf14aReader = 0x0385;    // CMD_READER_ISO_14443a
    private const ulong CmdMagicGetBlock = 0x0606;  // CMD_MIFARE_CGETBLOCK (gen1a backdoor read)
    private const ulong CmdMagicSetBlock = 0x0605;  // CMD_MIFARE_CSETBLOCK (gen1a backdoor write)

    // ISO14A_COMMAND flags (include/mifare.h). OLD firmware takes these as arg0 of the reader command.
    private const ulong Iso14aConnect = 0x0001;
    private const ulong Iso14aNoDisconnect = 0x0002;

    // Magic-card work flags (armsrc/mifarecmd.c): WUPC|HALT|INIT|OFF = a single backdoor operation on a gen1a card.
    private const ulong MagicSingle = 0x1E;

    /// <summary>Block 0 is 16 bytes: a 4-byte UID card holds UID(4) + BCC(1) + SAK(1) + ATQA(2) + manufacturer(8),
    /// where BCC is the XOR of the four UID bytes.</summary>
    public const int BlockSize = 16;

    public static byte Bcc4(ReadOnlySpan<byte> uid4) => (byte)(uid4[0] ^ uid4[1] ^ uid4[2] ^ uid4[3]);

    // This Proxmark3 Easy firmware speaks the OLD protocol: a fixed 544-byte frame — a 64-bit command, then three
    // 64-bit args, then 512 data bytes, all little-endian, with no magic. (The newer "NG" framing got back an
    // "unknown command" reply, which is how we learned the firmware is OLD-only.) OLD commands are also accepted
    // by newer firmware, so sending OLD is the safe choice across versions.
    private const int OldFrameSize = 544;

    /// <summary>Raised step by step as a connection is attempted, so the page can show progress. Lines may arrive
    /// on a background thread.</summary>
    public event Action<string>? Log;
    private void Say(string line) => Log?.Invoke(line);

    private static UsbManager? Manager =>
        Android.App.Application.Context?.GetSystemService(Context.UsbService) as UsbManager;

    /// <summary>A Proxmark (any device from its vendor) currently attached, or null.</summary>
    public static UsbDevice? Attached()
    {
        var list = Manager?.DeviceList;
        if (list is null) return null;
        foreach (var d in list.Values)
            if (IsProxmark(d)) return d;
        return null;
    }

    /// <summary>True when something that looks like a Proxmark is plugged in right now.</summary>
    public static bool IsAttached() => Attached() is not null;

    // --- auto-open: MainActivity notes a USB-attach intent, the shell navigates here on the next chance ---

    /// <summary>Raised when a Proxmark is plugged in or unplugged, so the shell can show or hide the RF tab. The
    /// handler should re-check <see cref="IsAttached"/> rather than trust the event, since events can be missed.</summary>
    public static event Action? PresenceChanged;

    /// <summary>Set when an attach arrived before the shell could act on it, so the RF tab can be opened on show.</summary>
    public static bool PendingAttach { get; private set; }

    public static void NoteAttached()
    {
        PendingAttach = true;
        PresenceChanged?.Invoke();
    }

    public static void NoteDetached()
    {
        PendingAttach = false;
        CloseLink();
        PresenceChanged?.Invoke();
    }

    public static void ClearPending() => PendingAttach = false;

    // --- the persistent connection: opened once while the Proxmark is attached and reused for every command ---
    // Opening and closing the CDC serial for each command left the write endpoint dead after the first use, so the
    // link is kept open and shared (guarded by _gate) until the device is unplugged.

    private static readonly object _gate = new();
    private static Link? _link;
    private static int _linkDeviceId = -1;

    private static void CloseLink()
    {
        lock (_gate)
        {
            _link?.Dispose();
            _link = null;
            _linkDeviceId = -1;
        }
    }

    /// <summary>Ensures the shared link is open to the attached Proxmark, asks permission if needed, then runs
    /// <paramref name="action"/> on it under the lock, returning <paramref name="onFail"/> if the link can't open.
    /// A hard USB failure drops the link so the next call reopens.</summary>
    private async Task<T> WithLinkAsync<T>(Func<Link, T> action, T onFail)
    {
        var mgr = Manager;
        var device = Attached();
        if (mgr is null || device is null) { Say("No Proxmark connected."); return onFail; }
        if (!mgr.HasPermission(device) && !await RequestPermissionAsync(mgr, device)) { Say("Permission was denied."); return onFail; }

        return await Task.Run(() =>
        {
            lock (_gate)
            {
                if (_link is null || _linkDeviceId != device.DeviceId)
                {
                    _link?.Dispose();
                    _link = Open(mgr, device);
                    _linkDeviceId = _link is null ? -1 : device.DeviceId;
                    if (_link is not null) Say("Connected to the Proxmark.");
                }
                if (_link is null) return onFail;

                try { return action(_link); }
                catch (Java.IO.IOException ex) { Say($"USB I/O error: {ex.Message}"); CloseLink(); return onFail; }
                catch (Exception ex) { Say($"Unexpected error: {ex.Message}"); CloseLink(); return onFail; }
            }
        });
    }

    /// <summary>Asks the Proxmark for its version over the shared link, reporting through <see cref="Log"/>. Returns
    /// true when the device replied (the link works).</summary>
    public Task<bool> ProbeAsync() => WithLinkAsync(Probe, false);

    private Task<bool> RequestPermissionAsync(UsbManager mgr, UsbDevice device)
    {
        var tcs = new TaskCompletionSource<bool>();
        var context = Android.App.Application.Context;
        var receiver = new PermissionReceiver(granted => tcs.TrySetResult(granted));

        var filter = new IntentFilter(PermissionAction);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            context.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
        else
            context.RegisterReceiver(receiver, filter);
        receiver.Unregister = () => { try { context.UnregisterReceiver(receiver); } catch { /* already gone */ } };

        // USB permission needs the system to fill in the device extra, so the PendingIntent must be mutable. The
        // explicit Mutable flag exists only on API 31+; below that a PendingIntent is mutable by default anyway.
        var intent = new Intent(PermissionAction).SetPackage(context.PackageName);
        var flags = OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : PendingIntentFlags.UpdateCurrent;
        var pending = PendingIntent.GetBroadcast(context, 0, intent, flags);
        mgr.RequestPermission(device, pending);
        return tcs.Task;
    }

    private bool Probe(Link link)
    {
        Drain(link.Conn, link.In);   // clear any backlog the previous command left, so this write is accepted
        var request = OldFrame(CmdVersion);
        if (link.Conn.BulkTransfer(link.Out, request, request.Length, 1000) < 0)
            throw new Java.IO.IOException("the version request could not be written");

        var reply = ReadReply(link.Conn, link.In, 2000);
        if (reply.Length == 0)
        {
            Say("No reply. The port is open but the Proxmark sent nothing back — it may need external power over OTG.");
            return false;
        }

        ulong cmd = reply.Length >= 8 ? BitConverter.ToUInt64(reply, 0) : 0;
        string text = AsciiRun(reply);
        Say("✓ The Proxmark replied — this phone CAN talk to the device.");
        Say($"Reply command: 0x{cmd:X}.");
        if (text.Length > 3) Say($"Text: {text}");
        return true;
    }

    /// <summary>Reads a block through the gen1a magic backdoor (block 0 is the UID block). Returns the 16 bytes, or
    /// null if the card isn't a gen1a magic card or no card coupled.</summary>
    public Task<byte[]?> MagicReadBlockAsync(int blockNo) => WithLinkAsync(link => MagicReadBlock(link, blockNo), null);

    /// <summary>Writes 16 bytes to a block through the gen1a magic backdoor, then reads it back to verify. Returns
    /// the verified bytes on success, or null on failure.</summary>
    public Task<byte[]?> MagicWriteBlockAsync(int blockNo, byte[] block16) => WithLinkAsync(link => MagicWriteBlock(link, blockNo, block16), null);

    /// <summary>Writes several full blocks to a gen1a magic tag through the backdoor (data, trailers/keys, block 0 —
    /// anything), in one coupling session. Returns true only if every block was written.</summary>
    public Task<bool> MagicWriteBlocksAsync(IReadOnlyDictionary<int, byte[]> blocks) => WithLinkAsync(link => MagicWriteBlocks(link, blocks), false);

    private bool MagicWriteBlocks(Link link, IReadOnlyDictionary<int, byte[]> blocks)
    {
        if (blocks.Count == 0) { Say("Nothing to write."); return false; }
        if (!WaitForCard(link)) { Say("No tag on the antenna. Hold the tag flat on the coil and try again."); return false; }

        int done = 0;
        foreach (var (block, data) in blocks.OrderBy(b => b.Key))
        {
            if (data.Length != BlockSize) { Say($"blk {block}: not {BlockSize} bytes, skipped."); continue; }
            Drain(link.Conn, link.In);
            var request = OldFrame(CmdMagicSetBlock, 0, MagicSingle, (ulong)block, data);
            if (link.Conn.BulkTransfer(link.Out, request, request.Length, 1000) < 0) throw new Java.IO.IOException("the write command could not be written");

            var reply = ReadReply(link.Conn, link.In, 2000);
            ulong cmd = reply.Length >= 8 ? BitConverter.ToUInt64(reply, 0) : 0;
            ulong arg0 = reply.Length >= 16 ? BitConverter.ToUInt64(reply, 8) : 0;
            if (cmd == CmdAck && arg0 >= 1) { done++; Say($"blk {block}: written"); }
            else { Say($"blk {block}: write FAILED (cmd 0x{cmd:X})"); }
        }
        Say($"Wrote {done} of {blocks.Count} blocks.");
        return done == blocks.Count;
    }

    /// <summary>Polls the HF reader until a card couples (the gen1a card's antenna is weak, so a read often misses).</summary>
    private bool WaitForCard(Link link, int tries = 40)
    {
        var request = OldFrame(CmdHf14aReader, Iso14aConnect);
        for (int i = 0; i < tries; i++)
        {
            Drain(link.Conn, link.In);
            if (link.Conn.BulkTransfer(link.Out, request, request.Length, 1000) < 0) throw new Java.IO.IOException("the read command could not be written");
            var reply = ReadReply(link.Conn, link.In, 500);
            ulong arg0 = reply.Length >= 16 ? BitConverter.ToUInt64(reply, 8) : 0;
            if (arg0 >= 1) return true;
        }
        return false;
    }

    private byte[]? MagicReadBlock(Link link, int blockNo)
    {
        if (!WaitForCard(link)) { Say("No tag on the antenna. Hold a tag flat on the Proxmark's coil and try again."); return null; }
        var block = MagicReadBlockRaw(link, blockNo, MagicSingle);
        if (block is null) Say("That tag isn't a gen1a magic card — it has no UID backdoor.");
        return block;
    }

    /// <summary>One CGETBLOCK, assuming the card is already coupled. <paramref name="workFlags"/> is MagicSingle for a
    /// standalone read, or a field-on sequence for a dump. Returns the 16 block bytes, or null on any failure.</summary>
    private byte[]? MagicReadBlockRaw(Link link, int blockNo, ulong workFlags)
    {
        // CGETBLOCK: arg0 = work flags, arg2 = block number (arg1 unused).
        var request = OldFrame(CmdMagicGetBlock, workFlags, 0, (ulong)blockNo);
        if (link.Conn.BulkTransfer(link.Out, request, request.Length, 1000) < 0) throw new Java.IO.IOException("the read command could not be written");

        var reply = ReadReply(link.Conn, link.In, 1500);
        ulong cmd = reply.Length >= 8 ? BitConverter.ToUInt64(reply, 0) : 0;
        ulong arg0 = reply.Length >= 16 ? BitConverter.ToUInt64(reply, 8) : 0;
        return cmd == CmdAck && arg0 >= 1 && reply.Length >= 32 + BlockSize ? reply[32..(32 + BlockSize)] : null;
    }

    /// <summary>A card read over the Proxmark: its 14443-A identity and, for a magic card, every block.</summary>
    public sealed record Dump(byte[] Uid, byte[] Atqa, byte Sak, IReadOnlyDictionary<int, byte[]> Blocks)
    {
        public bool IsMagic => Blocks.Count > 0;
    }

    /// <summary>Reads the card on the antenna: its identity always, and (if it's a gen1a magic card) a full block
    /// dump through the backdoor — no keys needed. Returns null if nothing coupled.</summary>
    public Task<Dump?> ReadCardAsync() => WithLinkAsync(ReadCard, null);

    private Dump? ReadCard(Link link)
    {
        if (ReadIdentity(link) is not { } id)
        {
            Say("No tag on the antenna. Hold a tag flat on the Proxmark's coil and try again.");
            return null;
        }
        var (uid, atqa, sak) = id;
        Say($"Tag {string.Join(":", uid.Select(b => b.ToString("X2")))}  ·  SAK {sak:X2}");

        // Probe the backdoor with block 0. If it answers, the tag is a gen1a magic card — dump every block.
        int count = sak == 0x18 ? 256 : 64;   // 4K vs 1K
        var blocks = new Dictionary<int, byte[]>();
        var b0 = MagicReadBlockRaw(link, 0, 0x0A);  // INIT|WUPC: field on + unlock, held open
        if (b0 is null)
        {
            Say("Not a gen1a magic tag — showing its identity only (reading its data would need the tag's keys).");
            return new Dump(uid, atqa, sak, blocks);
        }

        blocks[0] = b0;
        for (int b = 1; b < count; b++)
        {
            ulong wf = b == count - 1 ? 0x14UL : 0x00UL;   // last block HALT|OFF, the rest keep the field on
            if (MagicReadBlockRaw(link, b, wf) is { } data) blocks[b] = data;
            if (b % 16 == 0) Say($"read {blocks.Count}/{count} blocks…");
        }
        Say($"Read {blocks.Count} of {count} blocks.");
        return new Dump(uid, atqa, sak, blocks);
    }

    /// <summary>Polls the HF reader until a card couples, returning its UID/ATQA/SAK (null if none in time).</summary>
    private (byte[] Uid, byte[] Atqa, byte Sak)? ReadIdentity(Link link)
    {
        var request = OldFrame(CmdHf14aReader, Iso14aConnect);
        for (int i = 0; i < 40; i++)
        {
            Drain(link.Conn, link.In);
            if (link.Conn.BulkTransfer(link.Out, request, request.Length, 1000) < 0) throw new Java.IO.IOException("the read command could not be written");
            var reply = ReadReply(link.Conn, link.In, 500);
            ulong cmd = reply.Length >= 8 ? BitConverter.ToUInt64(reply, 0) : 0;
            ulong arg0 = reply.Length >= 16 ? BitConverter.ToUInt64(reply, 8) : 0;
            if (cmd == CmdAck && arg0 >= 1 && reply.Length >= 32 + 14)
            {
                var d = reply.AsSpan(32);
                int uidlen = Math.Clamp((int)d[10], 0, 10);
                return (d.Slice(0, uidlen).ToArray(), d.Slice(11, 2).ToArray(), d[13]);
            }
        }
        return null;
    }

    private byte[]? MagicWriteBlock(Link link, int blockNo, byte[] block16)
    {
        if (block16.Length != BlockSize) { Say($"A block is {BlockSize} bytes."); return null; }
        if (!WaitForCard(link)) { Say("No tag on the antenna. Hold the tag flat on the coil and try again."); return null; }

        Drain(link.Conn, link.In);
        // CSETBLOCK: arg0 = needWipe (0), arg1 = work flags, arg2 = block number; data = the 16 bytes.
        var request = OldFrame(CmdMagicSetBlock, 0, MagicSingle, (ulong)blockNo, block16);
        if (link.Conn.BulkTransfer(link.Out, request, request.Length, 1000) < 0) throw new Java.IO.IOException("the write command could not be written");

        var reply = ReadReply(link.Conn, link.In, 2000);
        ulong cmd = reply.Length >= 8 ? BitConverter.ToUInt64(reply, 0) : 0;
        ulong arg0 = reply.Length >= 16 ? BitConverter.ToUInt64(reply, 8) : 0;
        if (cmd != CmdAck || arg0 < 1)
        {
            Say(AsciiRun(reply).Contains("wupC1")
                ? "Write failed: that tag isn't a gen1a magic card."
                : $"Write failed (cmd 0x{cmd:X}, arg0 {arg0}).");
            return null;
        }

        // Read the block back so the caller can confirm it took (card is already coupled).
        Drain(link.Conn, link.In);
        var check = MagicReadBlockRaw(link, blockNo, MagicSingle);
        if (check is null) { Say("Wrote the block but could not read it back to verify."); return null; }
        return check;
    }


    /// <summary>An open, claimed, CDC-configured connection to the Proxmark; releasing it on dispose.</summary>
    private sealed class Link(UsbDeviceConnection conn, UsbEndpoint epIn, UsbEndpoint epOut, UsbInterface data, UsbInterface? comm) : IDisposable
    {
        public UsbDeviceConnection Conn { get; } = conn;
        public UsbEndpoint In { get; } = epIn;
        public UsbEndpoint Out { get; } = epOut;

        public void Dispose()
        {
            try { Conn.ReleaseInterface(data); } catch { }
            try { if (comm is not null) Conn.ReleaseInterface(comm); } catch { }
            Conn.Close();
        }
    }

    /// <summary>Opens the device, finds and claims the CDC serial interface, raises DTR/RTS and drains any startup
    /// chatter. Returns null (after logging why) if any step fails.</summary>
    private Link? Open(UsbManager mgr, UsbDevice device)
    {
        var conn = mgr.OpenDevice(device);
        if (conn is null) { Say("Could not open the device (OpenDevice returned nothing)."); return null; }

        UsbInterface? data = null, comm = null;
        for (int i = 0; i < device.InterfaceCount; i++)
        {
            var intf = device.GetInterface(i);
            if (intf.InterfaceClass == UsbClass.CdcData) data = intf;
            else if (intf.InterfaceClass == UsbClass.Comm) comm = intf;
        }
        data ??= FirstInterfaceWithBulk(device);
        if (data is null) { Say("No interface with bulk endpoints — not a USB serial device?"); conn.Close(); return null; }

        UsbEndpoint? epIn = null, epOut = null;
        for (int e = 0; e < data.EndpointCount; e++)
        {
            var ep = data.GetEndpoint(e);
            if (ep is null || ep.Type != UsbAddressing.XferBulk) continue;
            if (ep.Direction == UsbAddressing.In) epIn = ep; else epOut = ep;
        }
        if (epIn is null || epOut is null) { Say("The data interface has no bulk IN/OUT pair."); conn.Close(); return null; }

        if (comm is not null) conn.ClaimInterface(comm, true);
        if (!conn.ClaimInterface(data, true)) { Say("Could not claim the data interface."); conn.Close(); return null; }

        // CDC control: 115200 8N1 (ignored on a USB-virtual port, harmless) and raise DTR/RTS.
        int commIndex = comm?.Id ?? 0;
        var lineCoding = new byte[] { 0x00, 0xC2, 0x01, 0x00, 0x00, 0x00, 0x08 };
        conn.ControlTransfer((UsbAddressing)0x21, 0x20, 0, commIndex, lineCoding, lineCoding.Length, 200);
        conn.ControlTransfer((UsbAddressing)0x21, 0x22, 0x03, commIndex, null, 0, 200);

        Drain(conn, epIn);
        return new Link(conn, epIn, epOut, data, comm);
    }

    private static UsbInterface? FirstInterfaceWithBulk(UsbDevice device)
    {
        for (int i = 0; i < device.InterfaceCount; i++)
        {
            var intf = device.GetInterface(i);
            for (int e = 0; e < intf.EndpointCount; e++)
                if (intf.GetEndpoint(e)?.Type == UsbAddressing.XferBulk) return intf;
        }
        return null;
    }

    /// <summary>Reads and discards everything the device still has queued, until the bus is quiet. A command such as
    /// version makes the Proxmark send many packets; if they are left unread its USB TX buffer stays full and it
    /// stops servicing our writes, so the next command's write times out. Draining first keeps the link healthy.</summary>
    private static void Drain(UsbDeviceConnection conn, UsbEndpoint epIn)
    {
        var buf = new byte[512];
        int quiet = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1500)
        {
            int n = conn.BulkTransfer(epIn, buf, buf.Length, 120);
            if (n > 0) quiet = 0;
            else if (++quiet >= 2) break;   // two quiet reads in a row: the backlog is cleared
        }
    }

    /// <summary>Reads from the bulk IN endpoint, accumulating until a lull or a full OLD frame, up to the timeout.</summary>
    private static byte[] ReadReply(UsbDeviceConnection conn, UsbEndpoint epIn, int totalTimeoutMs)
    {
        var all = new List<byte>();
        var buf = new byte[512];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < totalTimeoutMs)
        {
            int n = conn.BulkTransfer(epIn, buf, buf.Length, 250);
            if (n > 0)
            {
                all.AddRange(buf[..n]);
                if (all.Count >= OldFrameSize) break;   // a whole OLD frame is in hand
            }
            else if (all.Count > 0)
            {
                break;   // got something, then a gap — that's the end of the reply
            }
        }
        return all.ToArray();
    }

    /// <summary>Builds a host→device OLD frame: 64-bit command, three 64-bit args, 512 data bytes (544 total, LE).</summary>
    private static byte[] OldFrame(ulong cmd, ulong arg0 = 0, ulong arg1 = 0, ulong arg2 = 0, byte[]? data = null)
    {
        var f = new byte[OldFrameSize];
        BitConverter.TryWriteBytes(f.AsSpan(0), cmd);
        BitConverter.TryWriteBytes(f.AsSpan(8), arg0);
        BitConverter.TryWriteBytes(f.AsSpan(16), arg1);
        BitConverter.TryWriteBytes(f.AsSpan(24), arg2);
        if (data is not null) Array.Copy(data, 0, f, 32, Math.Min(data.Length, 512));
        return f;
    }

    /// <summary>The longest run of printable ASCII in the bytes (the firmware banner lives in the reply's data area).</summary>
    private static string AsciiRun(byte[] b)
    {
        string best = "", cur = "";
        foreach (var x in b)
        {
            if (x is >= 0x20 and < 0x7F) { cur += (char)x; if (cur.Length > best.Length) best = cur; }
            else cur = "";
        }
        return best;
    }

    private static string Hex(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));

    private sealed class PermissionReceiver(Action<bool> onResult) : BroadcastReceiver
    {
        public Action? Unregister;

        public override void OnReceive(Context? context, Intent? intent)
        {
            Unregister?.Invoke();
            onResult(intent?.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false) ?? false);
        }
    }
}
#endif
