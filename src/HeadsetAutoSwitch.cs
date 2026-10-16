// HeadsetAutoSwitch - switches the default audio output between a SteelSeries Arctis Nova 5
// (2.4GHz dongle) and your speakers when the headset is powered off / on.
//
// The dongle stays plugged in when the headset is off, so Windows never notices. Instead we poll
// the dongle's vendor HID interface (MI_03, usage page 0xFFC0) with the 0xB0 status request
// (protocol documented by the HeadsetControl project). Status byte 0x02 = headset offline.
//
// Usage: HeadsetAutoSwitch.exe [headsetName] [speakersName] [--test | --uninstall]
//   (double-click)  installs itself to %LOCALAPPDATA%\HeadsetAutoSwitch, adds a Startup shortcut, starts
//   headsetName     substring of the headset output's name   (default "Arctis Nova 5")
//   speakersName    substring of the fallback output's name  (default "Realtek")
//   --test          run in place and print status changes (use the -debug console build)
//   --uninstall     stop it, remove the Startup shortcut and the installed files
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

[assembly: AssemblyTitle("HeadsetAutoSwitch")]
[assembly: AssemblyProduct("HeadsetAutoSwitch")]
[assembly: AssemblyDescription("Auto-switch audio when your wireless headset turns off/on")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

static class Program
{
    internal static bool Test;
    internal static void Log(string s) { if (Test) Console.WriteLine(DateTime.Now.ToString("HH:mm:ss ") + s); }

    const int PollMs = 2000;

    [STAThread]
    static void Main(string[] args)
    {
        var pos = new List<string>();
        bool uninstall = false;
        foreach (var a in args)
        {
            if (a == "--test") Test = true;
            else if (a == "--uninstall") uninstall = true;
            else pos.Add(a);
        }
        if (uninstall) { Setup.Uninstall(); return; }
        if (!Test && !Setup.RunningFromInstallDir()) { Setup.Install(pos); return; }

        string headsetName = pos.Count > 0 ? pos[0] : "Arctis Nova 5";
        string speakerName = pos.Count > 1 ? pos[1] : "Realtek";

        bool created;
        mutex = new Mutex(true, "HeadsetAutoSwitch_SingleInstance", out created);
        if (!created) { Log("already running"); return; }
        Log("watching '" + headsetName + "', fallback '" + speakerName + "'");

        int last = -1; // -1 unknown, 0 offline, 1 online
        int pending = -1, pendingCount = 0;
        while (true)
        {
            int s = Hid.QueryOnline(); // 1 online, 0 offline, -1 read failed
            if (s >= 0)
            {
                // require 2 matching reads before acting (debounce)
                if (s == pending) pendingCount++; else { pending = s; pendingCount = 1; }
                if (s != last && pendingCount >= 2)
                {
                    string target = s == 1 ? headsetName : speakerName;
                    Log("headset " + (s == 1 ? "ON" : "OFF") + " -> " + Audio.SetDefaultByName(target));
                    last = s;
                    Trim();
                }
            }
            Thread.Sleep(PollMs);
        }
    }
    static Mutex mutex; // held for the process lifetime = single instance

    // Hand idle memory back to Windows so the process stays tiny between switches.
    static void Trim()
    {
        GC.Collect();
        SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
    }
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr p, IntPtr min, IntPtr max);
}

// Self-install: copy to %LOCALAPPDATA%, add a Startup-folder shortcut, launch. No registry, no admin.
static class Setup
{
    const string Name = "HeadsetAutoSwitch";
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
    static readonly string Exe = Path.Combine(Dir, Name + ".exe");
    static readonly string Lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Name + ".lnk");
    static string Self { get { return Assembly.GetExecutingAssembly().Location; } }

    public static bool RunningFromInstallDir()
    {
        return string.Equals(Path.GetFullPath(Self), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase);
    }

    public static void Install(List<string> names)
    {
        try
        {
            StopOthers();
            Directory.CreateDirectory(Dir);
            File.Copy(Self, Exe, true);
            string argStr = "";
            foreach (var n in names) argStr += "\"" + n + "\" ";
            CreateShortcut(Lnk, Exe, argStr.Trim());
            Process.Start(new ProcessStartInfo(Exe, argStr.Trim()) { WorkingDirectory = Dir, UseShellExecute = false });
            Box("Installed and running in the background.\n\nTurn your headset off and the sound moves to your speakers; " +
                "turn it on and it comes back. It starts with Windows automatically.\n\n" +
                "To remove it, run:  HeadsetAutoSwitch.exe --uninstall", 0x40);
        }
        catch (Exception e) { Box("Install failed: " + e.Message, 0x10); }
    }

    public static void Uninstall()
    {
        StopOthers();
        try { File.Delete(Lnk); } catch { }
        if (RunningFromInstallDir())
            // can't delete ourselves while running: let cmd do it a moment after we exit
            Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + Dir + "\"")
                { CreateNoWindow = true, UseShellExecute = false });
        else
            try { Directory.Delete(Dir, true); } catch { }
        Box("HeadsetAutoSwitch was removed.", 0x40);
    }

    static void StopOthers()
    {
        int me = Process.GetCurrentProcess().Id;
        foreach (var p in Process.GetProcessesByName(Name))
            if (p.Id != me) try { p.Kill(); p.WaitForExit(3000); } catch { }
    }

    static void CreateShortcut(string lnk, string target, string args)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(t);
        object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
        Type st = sc.GetType();
        st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
        st.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { args });
        st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
        st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Auto-switch audio when your headset turns off/on" });
        st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
    }

    static void Box(string text, uint icon) { MessageBoxW(IntPtr.Zero, text, Name, icon); }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);
}

static class Hid
{
    // Arctis Nova 5 / Nova 5X base stations, vendor interface 3
    static readonly string[] Ids = { "vid_1038&pid_2232&mi_03", "vid_1038&pid_2253&mi_03" };
    static SafeFileHandle handle;
    static int inLen, outLen;
    static string lastRaw;

    // 1 = headset online, 0 = offline (or dongle unplugged), -1 = transient error
    public static int QueryOnline()
    {
        if (handle == null)
        {
            handle = Open();
            if (handle == null) return 0; // dongle not present -> treat as off
        }
        try
        {
            var req = new byte[outLen];
            req[0] = 0x00; req[1] = 0xB0; // report id 0, status request
            uint n;
            if (!WriteFile(handle, req, (uint)req.Length, out n, IntPtr.Zero)) throw new Exception("write");
            for (int i = 0; i < 8; i++)
            {
                var buf = new byte[inLen];
                var h = handle;
                var t = Task.Run(() => { uint r; return ReadFile(h, buf, (uint)buf.Length, out r, IntPtr.Zero); });
                if (!t.Wait(1000)) { CancelIoEx(handle, IntPtr.Zero); t.Wait(500); return -1; }
                if (!t.Result) throw new Exception("read");
                // reply: [report id 0] B0 <status> ...
                int k = buf[0] == 0xB0 ? 0 : (buf.Length > 1 && buf[1] == 0xB0 ? 1 : -1);
                if (k < 0) continue; // unrelated report, keep reading
                if (Program.Test)
                {
                    string raw = BitConverter.ToString(buf, 0, Math.Min(8, buf.Length));
                    if (raw != lastRaw) { Program.Log("dongle: " + raw); lastRaw = raw; }
                }
                return buf[k + 1] == 0x02 ? 0 : 1;
            }
            return -1;
        }
        catch
        {
            handle.Dispose(); handle = null;
            return -1;
        }
    }

    static SafeFileHandle Open()
    {
        Guid g; HidD_GetHidGuid(out g);
        IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
        if (set == new IntPtr(-1)) return null;
        try
        {
            var did = new SP_DEVICE_INTERFACE_DATA(); did.cbSize = Marshal.SizeOf(did);
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did); i++)
            {
                uint req;
                SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out req, IntPtr.Zero);
                IntPtr det = Marshal.AllocHGlobal((int)req);
                try
                {
                    Marshal.WriteInt32(det, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref did, det, req, out req, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(det + 4);
                    string lp = path.ToLowerInvariant();
                    bool match = false;
                    foreach (var id in Ids) if (lp.Contains(id)) match = true;
                    if (!match) continue;

                    var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (h.IsInvalid) continue;
                    IntPtr pp;
                    if (!HidD_GetPreparsedData(h, out pp)) { h.Dispose(); continue; }
                    var caps = new byte[64]; // HIDP_CAPS
                    HidP_GetCaps(pp, caps);
                    HidD_FreePreparsedData(pp);
                    if (BitConverter.ToUInt16(caps, 2) != 0xFFC0) { h.Dispose(); continue; }
                    inLen = BitConverter.ToUInt16(caps, 4);
                    outLen = BitConverter.ToUInt16(caps, 6);
                    return h;
                }
                finally { Marshal.FreeHGlobal(det); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int flags; public IntPtr reserved; }
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, byte[] caps);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, uint idx, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr det, uint size, out uint req, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string p, uint acc, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, byte[] b, uint n, out uint w, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, byte[] b, uint n, out uint r, IntPtr ov);
    [DllImport("kernel32.dll")] static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);
}

static class Audio
{
    // Makes the first active output whose name contains `name` the default for all roles.
    public static string SetDefaultByName(string name)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
        IMMDeviceCollection col;
        en.EnumAudioEndpoints(0 /*render*/, 1 /*active*/, out col);
        uint count; col.GetCount(out count);
        for (uint i = 0; i < count; i++)
        {
            IMMDevice dev; col.Item(i, out dev);
            string friendly = FriendlyName(dev);
            if (friendly.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
            string id; dev.GetId(out id);
            var pc = (IPolicyConfig)new PolicyConfigCo();
            for (int role = 0; role < 3; role++) pc.SetDefaultEndpoint(id, role); // console, multimedia, communications
            return friendly;
        }
        return "(no active output matching '" + name + "')";
    }

    static string FriendlyName(IMMDevice dev)
    {
        IPropertyStore ps; dev.OpenPropertyStore(0, out ps);
        var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 }; // PKEY_Device_FriendlyName
        PROPVARIANT pv; ps.GetValue(ref key, out pv);
        string s = pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) : "";
        PropVariantClear(ref pv);
        return s ?? "";
    }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);
    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
    [StructLayout(LayoutKind.Sequential)] struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p, p2; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection col);
        int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice dev);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        int GetCount(out uint n);
        int Item(uint i, out IMMDevice dev);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        int OpenPropertyStore(int access, out IPropertyStore ps);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        int GetCount(out int n);
        int GetAt(int i, out PROPERTYKEY k);
        int GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
    }
    // Undocumented but long-stable interface used by every audio-switcher tool.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        int GetMixFormat(string a, IntPtr b);
        int GetDeviceFormat(string a, int b, IntPtr c);
        int ResetDeviceFormat(string a);
        int SetDeviceFormat(string a, IntPtr b, IntPtr c);
        int GetProcessingPeriod(string a, int b, IntPtr c, IntPtr d);
        int SetProcessingPeriod(string a, IntPtr b);
        int GetShareMode(string a, IntPtr b);
        int SetShareMode(string a, IntPtr b);
        int GetPropertyValue(string a, IntPtr b, IntPtr c);
        int SetPropertyValue(string a, IntPtr b, IntPtr c);
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        int SetEndpointVisibility(string a, int b);
    }
}
