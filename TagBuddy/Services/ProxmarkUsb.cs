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
        PresenceChanged?.Invoke();
    }

    public static void ClearPending() => PendingAttach = false;

    /// <summary>Finds the Proxmark, asks for permission, opens its serial interface and sends one ping (and, if that
    /// is answered, a version request), reporting each step through <see cref="Log"/>.</summary>
    public async Task ProbeAsync()
    {
        var mgr = Manager;
        if (mgr is null) { Say("This phone has no USB host service."); return; }

        var device = Attached();
        if (device is null)
        {
            Say("No Proxmark found (looking for USB vendor 0x9AC4 or 0x2D2D).");
            var all = mgr.DeviceList?.Values.ToList() ?? [];
            Say(all.Count == 0
                ? "No USB devices are attached at all. Plug the Proxmark in with an OTG cable and tap again."
                : "Attached USB devices: " + string.Join(", ", all.Select(d => $"{d.VendorId:X4}:{d.ProductId:X4} {d.ProductName}")));
            return;
        }

        Say($"Found {device.VendorId:X4}:{device.ProductId:X4}  {device.ProductName ?? "Proxmark3"}  ({device.InterfaceCount} interface(s)).");

        if (!mgr.HasPermission(device))
        {
            Say("Asking Android for permission to use the device…");
            if (!await RequestPermissionAsync(mgr, device)) { Say("Permission was denied — cannot open it."); return; }
        }
        Say("Permission granted.");

        await Task.Run(() => Probe(mgr, device));
    }

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

    private void Probe(UsbManager mgr, UsbDevice device)
    {
        UsbDeviceConnection? conn = null;
        UsbInterface? data = null, comm = null;
        try
        {
            conn = mgr.OpenDevice(device);
            if (conn is null) { Say("Could not open the device (OpenDevice returned nothing)."); return; }
            Say("Opened the device.");

            for (int i = 0; i < device.InterfaceCount; i++)
            {
                var intf = device.GetInterface(i);
                if (intf.InterfaceClass == UsbClass.CdcData) data = intf;
                else if (intf.InterfaceClass == UsbClass.Comm) comm = intf;
            }
            data ??= FirstInterfaceWithBulk(device);   // some firmware reports the data class oddly; fall back to endpoints
            if (data is null) { Say("No interface with bulk endpoints — this does not look like a USB serial device."); return; }

            UsbEndpoint? epIn = null, epOut = null;
            for (int e = 0; e < data.EndpointCount; e++)
            {
                var ep = data.GetEndpoint(e);
                if (ep is null || ep.Type != UsbAddressing.XferBulk) continue;
                if (ep.Direction == UsbAddressing.In) epIn = ep; else epOut = ep;
            }
            if (epIn is null || epOut is null) { Say("The data interface has no bulk IN/OUT pair."); return; }
            Say($"Bulk endpoints: IN 0x{(int)epIn.Address:X2}, OUT 0x{(int)epOut.Address:X2}.");

            if (comm is not null) conn.ClaimInterface(comm, true);
            if (!conn.ClaimInterface(data, true)) { Say("Could not claim the data interface."); return; }
            Say("Claimed the serial interface.");

            // CDC control: set 115200 8N1 (ignored on a USB-virtual port, but harmless) and raise DTR/RTS.
            int commIndex = comm?.Id ?? 0;
            var lineCoding = new byte[] { 0x00, 0xC2, 0x01, 0x00, 0x00, 0x00, 0x08 };
            int r1 = conn.ControlTransfer((UsbAddressing)0x21, 0x20, 0, commIndex, lineCoding, lineCoding.Length, 200);
            int r2 = conn.ControlTransfer((UsbAddressing)0x21, 0x22, 0x03, commIndex, null, 0, 200);
            Say($"CDC setup: line-coding {(r1 < 0 ? "not accepted" : "ok")}, DTR/RTS {(r2 < 0 ? "not accepted" : "ok")}.");

            // Drain anything the device volunteered on connect, so the reply isn't confused with a startup banner.
            Drain(conn, epIn);

            var request = OldFrame(CmdVersion);
            int w = conn.BulkTransfer(epOut, request, request.Length, 1000);
            if (w < 0) { Say("Writing the version request failed (bulk OUT returned -1)."); return; }
            Say($"Sent version request ({w} bytes, OLD protocol).");

            var reply = ReadReply(conn, epIn, 2000);
            if (reply.Length == 0)
            {
                Say("No reply. The phone opened the port but the Proxmark sent nothing back — the device may need external power over OTG, or this phone's USB host cannot drive it.");
                return;
            }

            Say($"Got {reply.Length} bytes.");
            ulong cmd = reply.Length >= 8 ? BitConverter.ToUInt64(reply, 0) : 0;
            string text = AsciiRun(reply);
            Say("✓ The Proxmark replied — this phone CAN talk to the device.");
            Say($"Reply command: 0x{cmd:X}.");
            if (text.Length > 3) Say($"Text: {text}");
            Say($"Raw: {Hex(reply)}");
        }
        catch (Java.IO.IOException ex)
        {
            Say($"USB I/O error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Say($"Unexpected error: {ex.Message}");
        }
        finally
        {
            try { if (data is not null) conn?.ReleaseInterface(data); } catch { }
            try { if (comm is not null) conn?.ReleaseInterface(comm); } catch { }
            conn?.Close();
        }
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

    private static void Drain(UsbDeviceConnection conn, UsbEndpoint epIn)
    {
        var buf = new byte[256];
        for (int i = 0; i < 4; i++)
            if (conn.BulkTransfer(epIn, buf, buf.Length, 50) <= 0) return;
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
