// GearBattery: Windows tray icons showing the battery of wireless gear.
//   ATK X1 Pro mouse, ATK A87 Pro keyboard (dongle or cable), Logitech G435 headset.
// Protocol details: docs/PROTOCOLS.md. Build: build.ps1. CLI: GearBattery.exe --print | --json
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

static class Hid
{
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, out CAPS c);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int f);
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr h, IntPtr d, ref Guid g, int i, ref DID did);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr h, ref DID did, IntPtr buf, int size, out int req, IntPtr d);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr p, uint c, uint f, IntPtr t);

    [StructLayout(LayoutKind.Sequential)] struct DID { public int cbSize; public Guid g; public int flags; public IntPtr r; }
    [StructLayout(LayoutKind.Sequential)]
    struct CAPS
    {
        public ushort Usage, UsagePage, InLen, OutLen, FeatLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] r;
        public ushort a, b, c, d, e, f, g, h, i, j;
    }

    // Returns device path of the collection with this pid + usage page, or null. inLen = input report length.
    public static string Find(string pid, ushort usagePage, out int inLen)
    {
        inLen = 0;
        Guid g; HidD_GetHidGuid(out g);
        IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
        IntPtr buf = Marshal.AllocHGlobal(2048);
        try
        {
            var did = new DID(); did.cbSize = Marshal.SizeOf(did);
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did); i++)
            {
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                int req;
                if (!SetupDiGetDeviceInterfaceDetail(set, ref did, buf, 2048, out req, IntPtr.Zero)) continue;
                string path = Marshal.PtrToStringUni(buf + 4);
                if (path.ToLowerInvariant().IndexOf("vid_" + pid) < 0) continue;
                using (var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid) continue;
                    IntPtr p; CAPS c;
                    if (!HidD_GetPreparsedData(h, out p)) continue;
                    HidP_GetCaps(p, out c); HidD_FreePreparsedData(p);
                    if (c.UsagePage == usagePage) { inLen = c.InLen; return path; }
                }
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(buf); SetupDiDestroyDeviceInfoList(set); }
    }

    // Writes one output report and waits for an input report accepted by match. Null on timeout/error.
    public static byte[] Query(string path, int inLen, byte[] report, Func<byte[], bool> match, int timeoutMs)
    {
        var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero); // RW, overlapped
        if (h.IsInvalid) return null;
        try
        {
            using (var fs = new FileStream(h, FileAccess.ReadWrite, 1, true))
            {
                fs.Write(report, 0, report.Length);
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (true)
                {
                    var b = new byte[inLen];
                    var t = fs.ReadAsync(b, 0, inLen);
                    int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0 || !t.Wait(left)) return null; // disposing the stream cancels the pending read
                    if (match(b)) return b;
                }
            }
        }
        catch { return null; }
    }

    // Raw read/write stream for multi-step protocols. Caller disposes.
    public static FileStream Open(string path)
    {
        var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        return h.IsInvalid ? null : new FileStream(h, FileAccess.ReadWrite, 1, true);
    }
}

// Logitech G435 Lightspeed headset, dongle 046d:0adf. Protocol ported from
// github.com/deddroid/logitech-g435-battery (MIT), "adf" path. Reports voltage only; no charging flag.
// 64-byte frames: [cmd, bodyLen, crcLo, crcHi, body...], CRC-16/XMODEM over the frame with CRC bytes zeroed.
static class G435
{
    static ushort Crc(byte[] d)
    {
        int crc = 0;
        foreach (byte b in d)
        {
            crc ^= b << 8;
            for (int i = 0; i < 8; i++) crc = ((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1) & 0xFFFF;
        }
        return (ushort)crc;
    }

    static byte[] Frame(byte cmd, params byte[] body)
    {
        var f = new byte[64]; f[0] = cmd; f[1] = (byte)body.Length; Array.Copy(body, 0, f, 4, body.Length);
        ushort c = Crc(f); f[2] = (byte)c; f[3] = (byte)(c >> 8);
        return f;
    }

    // "PA" command: 50 41 category seq len payload
    static byte[] Pa(byte cat, byte seq, byte len, params byte[] p)
    {
        var b = new byte[5 + p.Length]; b[0] = 0x50; b[1] = 0x41; b[2] = cat; b[3] = seq; b[4] = len;
        Array.Copy(p, 0, b, 5, p.Length);
        return Frame(0x80, b);
    }

    public static Reading Read()
    {
        int len; string path = Hid.Find("046d&pid_0adf", 0xFF00, out len);
        if (path == null) return Reading.Off();
        try
        {
            using (var fs = Hid.Open(path))
            {
                if (fs == null) return Reading.Off();
                Task<int> pending = null; var buf = new byte[65];
                Func<byte[], int, List<byte>> transact = (frame, ms) =>
                {
                    var o = new byte[65]; Array.Copy(frame, 0, o, 1, 64); fs.Write(o, 0, 65);
                    var got = new List<byte>(); var end = DateTime.UtcNow.AddMilliseconds(ms);
                    while (true)
                    {
                        int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
                        if (left <= 0) break;
                        if (pending == null) { buf = new byte[65]; pending = fs.ReadAsync(buf, 0, 65); }
                        if (!pending.Wait(left)) break; // keep the read pending into the next step
                        pending = null; got.AddRange(buf);
                    }
                    return got;
                };
                transact(Frame(0x84, 1), 50);
                transact(Frame(0x85, 1), 50);
                transact(Frame(0x80, 0x50, 0x41, 0x06, 0x01, 0x0d, 0x00, 0x25, 0x34, 0x12, 0x5a, 0x5a, 0x01, 0x00, 0x00, 0x00, 0xf0, 0x00, 0x00, 0x00), 450);
                transact(Pa(0x0e, 2, 1, 0xe0), 120);       // get remote mode
                transact(Pa(0x0e, 3, 2, 0xe1, 0x01), 120); // remote mode 1: battery query reaches the headset
                transact(Pa(0x0e, 4, 1, 0xe0), 120);
                var r = transact(Pa(0x06, 5, 1, 0x00, 0x31), 250); // get battery
                try { transact(Frame(0x85, 2), 50); } catch { }
                int mv = ParseMv(r);
                return mv < 0 ? Reading.Off() : Reading.On(Percent(mv), false);
            }
        }
        catch { return Reading.Off(); }
    }

    // Finds "07 00 06 (seq|80) 00 mvLo mvHi" with a plausible voltage. -1 if absent (headset off).
    public static int ParseMv(List<byte> b)
    {
        for (int i = 0; i + 7 <= b.Count; i++)
            if (b[i] == 7 && b[i + 1] == 0 && b[i + 2] == 6 && (b[i + 3] & 0x80) != 0 && b[i + 4] == 0)
            {
                int mv = b[i + 5] | b[i + 6] << 8;
                if (mv >= 2500 && mv <= 4600) return mv;
            }
        return -1;
    }

    static readonly int[,] Curve = { {4200,100},{4150,95},{4100,90},{4050,85},{4000,80},{3950,75},{3900,70},
        {3850,62},{3800,55},{3750,45},{3700,35},{3650,25},{3600,15},{3550,10},{3500,5},{3400,0} };

    public static int Percent(int mv)
    {
        if (mv >= 4200) return 100;
        for (int i = 0; i + 1 < Curve.GetLength(0); i++)
        {
            int hiMv = Curve[i, 0], hiP = Curve[i, 1], loMv = Curve[i + 1, 0], loP = Curve[i + 1, 1];
            if (mv >= loMv) return (int)Math.Round(loP + (double)(mv - loMv) / (hiMv - loMv) * (hiP - loP), MidpointRounding.AwayFromZero);
        }
        return 0;
    }
}

class Reading
{
    public bool Online; public int Level; public bool Charging;
    public static Reading Off() { return new Reading(); }
    public static Reading On(int level, bool charging) { var r = new Reading(); r.Online = true; r.Level = level; r.Charging = charging; return r; }
}

static class Gear
{
    // 101B = 8K wireless dongle, 1016 = mouse plugged in by cable (charging). Same protocol on both.
    public static Reading Mouse()
    {
        var r = MouseVia("373b&pid_101b");
        return r.Online ? r : MouseVia("373b&pid_1016");
    }

    static Reading MouseVia(string pid)
    {
        int len; string path = Hid.Find(pid, 0xFF02, out len);
        if (path == null) return Reading.Off();
        var r = new byte[17]; r[0] = 8; r[1] = 4;
        int sum = 0; for (int i = 0; i < 16; i++) sum += r[i];
        r[16] = (byte)(0x55 - (sum & 0xFF));
        var b = Hid.Query(path, len, r, x => x[0] == 8 && x[1] == 4, 1500);
        if (b == null || b[2] != 0) return Reading.Off();
        return Reading.On(b[6], b[7] != 0);
    }

    // 111F = 2.4 GHz dongle (ask dongle if the keyboard is connected first), 111E = keyboard on its USB cable.
    public static Reading Keyboard()
    {
        int len; string path = Hid.Find("373b&pid_111f", 0xFF60, out len);
        if (path != null)
        {
            var s = new byte[33]; s[1] = 0xAA; s[2] = 0xFD;
            var st = Hid.Query(path, len, s, x => x[1] == 0x55 && x[2] == 0xFD, 1000);
            if (st != null && st[3] != 2) // 2 = keyboard disconnected / asleep
            {
                var viaDongle = KeyboardBattery(path, len);
                if (viaDongle.Online) return viaDongle;
            }
        }
        path = Hid.Find("373b&pid_111e", 0xFF60, out len);
        return path == null ? Reading.Off() : KeyboardBattery(path, len);
    }

    // Reply [02 96 24 level charge]. The firmware gives no level while charging (ATK HUB hides it too): Level = -1.
    static Reading KeyboardBattery(string path, int len)
    {
        var r = new byte[33]; r[1] = 0x02; r[2] = 0x96; r[3] = 0x24;
        var b = Hid.Query(path, len, r, x => x[1] == 0x02 && x[2] == 0x96 && x[3] == 0x24, 1500);
        if (b == null) return Reading.Off();
        return b[5] != 0 ? Reading.On(-1, true) : Reading.On(b[4], false);
    }
}

class Tray
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    readonly NotifyIcon icon = new NotifyIcon();
    readonly string name, glyph;
    Reading last;

    public Tray(string name, string glyph, ContextMenuStrip menu)
    {
        this.name = name; this.glyph = glyph; icon.ContextMenuStrip = menu; icon.Visible = true;
        Show(Reading.Off());
    }

    public void Show(Reading r)
    {
        // Device asleep: keep showing the last known value, greyed out.
        bool stale = !r.Online && last != null;
        // Charging with unknown level (-1): keep the last known level.
        if (r.Online && r.Level < 0) r = Reading.On(last != null ? last.Level : -1, r.Charging);
        if (r.Online) last = r;
        Reading v = r.Online ? r : last;

        string text = v == null ? "-" : v.Level < 0 ? "+" : v.Level.ToString();
        Color col = v == null || stale ? Color.Gray
                  : v.Charging ? Color.LimeGreen
                  : v.Level <= 20 ? Color.Red
                  : Color.White;

        using (var bmp = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(bmp))
        using (var font = new Font("Segoe UI", text.Length >= 3 ? 14f : 19f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var glyphFont = new Font("Segoe MDL2 Assets", 12f, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(col))
        using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.DrawString(glyph, glyphFont, Brushes.White, new RectangleF(0, 0, 32, 13), fmt);
            g.DrawString(text, font, brush, new RectangleF(-4, 11, 40, 22), fmt);
            IntPtr h = bmp.GetHicon();
            Icon old = icon.Icon;
            icon.Icon = Icon.FromHandle(h);
            if (old != null) { DestroyIcon(old.Handle); old.Dispose(); }
        }

        string tip = name + ": " + (v == null ? "not connected" : v.Level < 0 ? "level unknown" : v.Level + "%");
        if (v != null && v.Charging) tip += " (charging)";
        if (stale) tip += " (asleep, last known)";
        icon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
    }

    public void Hide() { icon.Visible = false; }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // CLI mode: "GearBattery.exe --print" (text) or "GearBattery.exe --json"; pipe it (| Out-String) since it's a GUI exe.
        if (args.Length > 0 && (args[0] == "--print" || args[0] == "--json"))
        {
            bool json = Array.IndexOf(args, "--json") >= 0;
            var lines = new List<string>();
            foreach (var p in new[] {
                Tuple.Create("mouse", "ATK X1 Pro", (Func<Reading>)Gear.Mouse),
                Tuple.Create("keyboard", "ATK A87 Pro", (Func<Reading>)Gear.Keyboard),
                Tuple.Create("headset", "Logitech G435", (Func<Reading>)G435.Read) })
            {
                var r = p.Item3();
                string level = r.Online && r.Level >= 0 ? r.Level.ToString() : "null";
                if (json)
                    lines.Add("  {\"id\": \"" + p.Item1 + "\", \"name\": \"" + p.Item2 + "\", \"online\": " + (r.Online ? "true" : "false")
                        + ", \"level\": " + level + ", \"charging\": " + (r.Online && r.Charging ? "true" : "false") + "}");
                else
                    Console.WriteLine(p.Item1 + ": " + (!r.Online ? "offline" : (r.Level >= 0 ? r.Level + "%" : "level unknown") + (r.Charging ? " (charging)" : "")));
            }
            if (json) Console.WriteLine("[\n" + string.Join(",\n", lines) + "\n]");
            return;
        }
        Application.EnableVisualStyles();
        var menu = new ContextMenuStrip();
        var kbd = new Tray("A87 Pro", "", menu);   // Segoe MDL2 KeyboardClassic
        var mouse = new Tray("X1 Pro", "", menu);  // Segoe MDL2 Mouse
        var headset = new Tray("G435", "", menu); // Segoe MDL2 Headphone
        var timer = new Timer { Interval = 100 };
        bool busy = false;
        int tick = 0;

        // Headset read takes ~1.2 s and talks to the headset radio, so only every 5 minutes (or on Refresh).
        Func<bool, Task> poll = async (withHeadset) =>
        {
            if (busy) return; busy = true;
            try
            {
                mouse.Show(await Task.Run(() => Gear.Mouse()));
                kbd.Show(await Task.Run(() => Gear.Keyboard()));
                if (withHeadset) headset.Show(await Task.Run(() => G435.Read()));
            }
            finally { busy = false; }
        };

        menu.Items.Add("Refresh", null, async (s, e) => await poll(true));
        menu.Items.Add("Exit", null, (s, e) => { mouse.Hide(); kbd.Hide(); headset.Hide(); Application.Exit(); });
        timer.Tick += async (s, e) => { timer.Interval = 60000; await poll(tick++ % 5 == 0); };
        timer.Start();
        Application.Run();
    }
}
