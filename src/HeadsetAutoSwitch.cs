// HeadsetAutoSwitch - switches the default audio output to your speakers when a wireless headset
// is powered off, and back when it is powered on.
//
// 2.4 GHz dongles stay plugged in when the headset is off, so Windows never notices. We ask the
// dongle instead. Three ways to know the headset's state, tried in this order:
//   1. Learned / custom rules from config.ini        (see --learn)
//   2. Built-in rules for known dongles              (protocols documented by HeadsetControl)
//   3. Endpoint mode: the headset's Windows audio device appears/disappears (Bluetooth, some dongles)
//
// The headset's audio device is found automatically through the dongle's Windows ContainerId.
//
// Usage: HeadsetAutoSwitch.exe [--learn | --list | --test | --uninstall] [headsetName] [speakersName]
//   (double-click)  install to %LOCALAPPDATA%\HeadsetAutoSwitch, add to Startup, start
//   --learn         teach it an unknown headset (turn it off and on once)
//   --list          print detected dongles, audio devices and the active rule (console build)
//   --test          run in the console and print what happens (console build)
//   --uninstall     stop it, remove the Startup shortcut and the installed files
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

[assembly: AssemblyTitle("HeadsetAutoSwitch")]
[assembly: AssemblyProduct("HeadsetAutoSwitch")]
[assembly: AssemblyDescription("Auto-switch audio when your wireless headset turns off/on")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]

static class Program
{
    internal static bool Test;
    internal static void Log(string s)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + s;
        if (Test) { Console.WriteLine(line); return; }
        try // tiny log of what happened, for troubleshooting (kept under 64 KB)
        {
            string path = Path.Combine(Setup.Dir, "log.txt");
            if (File.Exists(path) && new FileInfo(path).Length > 65536) File.Delete(path);
            File.AppendAllText(path, line + "\r\n");
        }
        catch { }
    }
    static Mutex mutex; // held for the process lifetime = single instance

    [STAThread]
    static void Main(string[] args)
    {
        var pos = new List<string>();
        string mode = null;
        foreach (var a in args)
        {
            if (a == "--test") Test = true;
            else if (a.StartsWith("--")) mode = a;
            else pos.Add(a);
        }
        if (mode == "--uninstall") { Setup.Uninstall(); return; }
        if (mode == "--learn") { Learn.Run(); return; }
        if (mode == "--list") { Test = true; Diag.List(); return; }
        if (!Test && !Setup.RunningFromInstallDir()) { Setup.Install(pos); return; }

        var cfg = Config.Load();
        if (pos.Count > 0) cfg.Headset = pos[0];
        if (pos.Count > 1) cfg.Speakers = pos[1];

        bool created;
        mutex = new Mutex(true, "HeadsetAutoSwitch_SingleInstance", out created);
        if (!created) { Log("already running"); return; }
        new Switcher(cfg).Run();
    }

    // Hand idle memory back to Windows so the process stays tiny between switches.
    internal static void Trim()
    {
        GC.Collect();
        Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
    }

    internal static string Hex(byte[] b, int max)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < b.Length && i < max; i++) sb.Append(b[i].ToString("X2"));
        return sb.ToString();
    }
}

// ---------------------------------------------------------------------------------------------
// Rules: how to ask a dongle whether the headset is on. One line each, also used in config.ini:
//   name="..." vid=1038 pid=2232,2253 page=FFC0 iface=3 req=00B0 match=00B0 at=2 off=02 [on=..]
//   req      bytes to send (first byte = report id)          omitted + mode=listen: only listen
//   match    reply must start with these bytes               at: byte index (report id = index 0)
//   off/on   values at `at` meaning off / on (on omitted: anything not `off` = on)
//   timeout=off   no reply means off          mode=endpoint id=...   use a Windows audio device
// ---------------------------------------------------------------------------------------------
class Rule
{
    public string Name = "custom", EndpointId;
    public ushort Vid;
    public List<ushort> Pids = new List<ushort>(), Pages = new List<ushort>();
    public int Iface = -1, At = -1;
    public byte[] Req, Match = new byte[0];
    public List<byte> Off = new List<byte>(), On = new List<byte>();
    public bool TimeoutOff, Listen, Learned;

    public static readonly string[] BuiltIn =
    {
        // SteelSeries (status request 0xB0, HeadsetControl)
        "name=\"Arctis Nova 5 / 5X\" vid=1038 pid=2232,2253 page=FFC0 iface=3 req=00B0 match=00B0 at=2 off=02",
        "name=\"Arctis Nova 3P / 3X Wireless\" vid=1038 pid=2269,226D page=FFC0 iface=3 req=00B0 match=00B0 at=2 off=02",
        "name=\"Arctis Nova 7 / 7X / 7P\" vid=1038 pid=2202,22A1,227E,2206,2258,229E,22AD,223A,22A9,227A,22A4,220A,22A7 page=FFC0 iface=3 req=00B0 match=00B0 at=4 off=00",
        "name=\"Arctis 7+\" vid=1038 pid=220E,2212,2216,2236 page=FFC0 iface=3 req=00B0 match=00B0 at=2 off=01",
        "name=\"Arctis Nova Pro Wireless\" vid=1038 pid=12E0,12E5 iface=4 req=06B0 match=06B0 at=15 off=01",
        "name=\"Arctis 1 / 7X / 7P Wireless\" vid=1038 pid=12B3,12B6,12D7,12D5 iface=3 req=0612 match=06 at=2 off=01",
        "name=\"Arctis Pro Wireless\" vid=1038 pid=1290 iface=0 req=41AA at=1 off=02 on=04",
        // Logitech HID++ (an error reply 0xFF means the headset is off)
        "name=\"Logitech G533\" vid=046D pid=0A66 page=FF43,FF00 req=11FF0701 match=11FF at=2 off=FF",
        "name=\"Logitech G633 / G635 / G733 / G933 / G935\" vid=046D pid=0A5C,0A89,0A5B,0A87,0AB5,0AFE,0B1F page=FF43 req=11FF080A match=11FF at=2 off=FF",
        "name=\"Logitech G PRO / PRO X\" vid=046D pid=0AA7,0AAA,0ABA,0AFB,0AFC page=FF43 req=11FF060D match=11FF at=2 off=FF",
        "name=\"Logitech G535\" vid=046D pid=0AC4 page=000C,FF43 iface=3 req=11FF050D match=11FF at=2 off=FF",
        // Corsair
        "name=\"Corsair Void (wireless)\" vid=1B1C pid=1B27,0A14,0A16,0A17,0A1D,0A1A,1B2A,1B23,1B29,0A55,0A51,0A52,0A38,0A4F,0A2B,0A75,0A56 page=FFC5,FF00 req=C964 at=4 off=00 on=01,02,04,05",
        "name=\"Corsair Virtuoso XT / SE\" vid=1B1C pid=0A64,0A3E page=FF42 iface=3 req=0200 match=01 at=1 off=00",
        // HyperX
        "name=\"HyperX Cloud Alpha Wireless\" vid=03F0 pid=098D req=21BB03 match=21BB03 at=3 off=01",
    };

    // Read-only status requests the learn wizard may try on unknown dongles of these vendors.
    public static readonly Dictionary<ushort, string[]> Probes = new Dictionary<ushort, string[]>
    {
        { 0x1038, new[] { "00B0", "06B0" } },                       // SteelSeries status
        { 0x046D, new[] { "11FF001A00005A", "10FF001A00005A" } },   // HID++ root ping
        { 0x1B1C, new[] { "C964" } },                               // Corsair legacy status
        { 0x03F0, new[] { "21BB03" } },                             // HyperX connection status
    };

    public static List<Rule> All(Config cfg)
    {
        var list = new List<Rule>(cfg.Rules);
        foreach (var s in BuiltIn) list.Add(Parse(s));
        return list;
    }

    public static Rule Parse(string line)
    {
        try
        {
            var r = new Rule();
            foreach (var kv in Tokens(line))
            {
                string k = kv.Key, v = kv.Value;
                if (k == "name") r.Name = v;
                else if (k == "vid") r.Vid = Convert.ToUInt16(v, 16);
                else if (k == "pid") foreach (var p in v.Split(',')) r.Pids.Add(Convert.ToUInt16(p, 16));
                else if (k == "page") foreach (var p in v.Split(',')) r.Pages.Add(Convert.ToUInt16(p, 16));
                else if (k == "iface") r.Iface = int.Parse(v);
                else if (k == "req") r.Req = Bytes(v);
                else if (k == "match") r.Match = Bytes(v);
                else if (k == "at") r.At = int.Parse(v);
                else if (k == "off") foreach (var p in v.Split(',')) r.Off.Add(Convert.ToByte(p, 16));
                else if (k == "on") foreach (var p in v.Split(',')) if (p.Length > 0) r.On.Add(Convert.ToByte(p, 16));
                else if (k == "timeout") r.TimeoutOff = v == "off";
                else if (k == "mode") r.Listen = v == "listen";
                else if (k == "id") r.EndpointId = v;
                else if (k == "src") r.Learned = v == "learned";
            }
            if (r.EndpointId == null && r.Vid == 0) return null;
            return r;
        }
        catch { return null; }
    }

    public string ToLine()
    {
        var sb = new StringBuilder("name=\"" + Name.Replace("\"", "'") + "\"");
        if (EndpointId != null) sb.Append(" mode=endpoint id=" + EndpointId);
        else
        {
            sb.Append(" vid=" + Vid.ToString("X4"));
            if (Pids.Count > 0) sb.Append(" pid=" + string.Join(",", Pids.ConvertAll(p => p.ToString("X4"))));
            if (Pages.Count > 0) sb.Append(" page=" + string.Join(",", Pages.ConvertAll(p => p.ToString("X4"))));
            if (Iface >= 0) sb.Append(" iface=" + Iface);
            if (Listen) sb.Append(" mode=listen");
            if (Req != null) sb.Append(" req=" + Program.Hex(Req, 64));
            if (Match.Length > 0) sb.Append(" match=" + Program.Hex(Match, 64));
            if (At >= 0) sb.Append(" at=" + At);
            if (Off.Count > 0) sb.Append(" off=" + string.Join(",", Off.ConvertAll(b => b.ToString("X2"))));
            if (On.Count > 0) sb.Append(" on=" + string.Join(",", On.ConvertAll(b => b.ToString("X2"))));
            if (TimeoutOff) sb.Append(" timeout=off");
        }
        if (Learned) sb.Append(" src=learned");
        return sb.ToString();
    }

    public bool Fits(HidIface h)
    {
        if (EndpointId != null || h.Vid != Vid) return false;
        if (Pids.Count > 0 && !Pids.Contains(h.Pid)) return false;
        if (Iface >= 0 && h.Iface != Iface) return false;
        if (Pages.Count > 0 ? !Pages.Contains(h.UsagePage) : h.UsagePage < 0xFF00) return false;
        return Req == null || h.OutLen > 0;
    }

    // -2 = not a reply for this rule, -1 = unknown, 0 = off, 1 = on
    public int Evaluate(byte[] r)
    {
        if (!StartsWith(r, Match)) return -2;
        if (At < 0) return 1;
        if (At >= r.Length) return -1;
        byte b = r[At];
        if (Off.Contains(b)) return 0;
        if (On.Count > 0) return On.Contains(b) ? 1 : -1;
        return 1;
    }

    public static bool StartsWith(byte[] r, byte[] prefix)
    {
        if (r.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++) if (r[i] != prefix[i]) return false;
        return true;
    }

    public static byte[] Bytes(string hex)
    {
        hex = hex.Replace(" ", "");
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return b;
    }

    static List<KeyValuePair<string, string>> Tokens(string s)
    {
        var list = new List<KeyValuePair<string, string>>();
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && s[i] == ' ') i++;
            int eq = s.IndexOf('=', i);
            if (eq < 0) break;
            string key = s.Substring(i, eq - i).Trim().ToLowerInvariant();
            i = eq + 1;
            string val;
            if (i < s.Length && s[i] == '"')
            {
                int end = s.IndexOf('"', i + 1);
                if (end < 0) end = s.Length;
                val = s.Substring(i + 1, end - i - 1);
                i = end + 1;
            }
            else
            {
                int end = s.IndexOf(' ', i);
                if (end < 0) end = s.Length;
                val = s.Substring(i, end - i);
                i = end;
            }
            list.Add(new KeyValuePair<string, string>(key, val));
        }
        return list;
    }
}

// config.ini next to the installed exe. Plain key=value lines.
class Config
{
    public string Headset, Speakers;
    public List<Rule> Rules = new List<Rule>();
    public static string FilePath { get { return Path.Combine(Setup.Dir, "config.ini"); } }

    const string Template =
        "# HeadsetAutoSwitch settings. Restart the app (or run the installer again) after editing.\r\n" +
        "#\r\n" +
        "# headset=<part of the headset output's name>   only needed if it isn't found automatically\r\n" +
        "# speakers=<part of the fallback output's name> default: your built-in speakers\r\n" +
        "#\r\n" +
        "# Extra detection rules (HeadsetAutoSwitch.exe --learn writes these for you):\r\n" +
        "# rule=name=\"My headset\" vid=1038 pid=2232 page=FFC0 req=00B0 match=00B0 at=2 off=02\r\n";

    public static Config Load()
    {
        var c = new Config();
        try
        {
            foreach (var raw in File.ReadAllLines(FilePath))
            {
                string l = raw.Trim();
                if (l.Length == 0 || l[0] == '#' || l[0] == ';') continue;
                int eq = l.IndexOf('=');
                if (eq < 0) continue;
                string k = l.Substring(0, eq).Trim().ToLowerInvariant(), v = l.Substring(eq + 1).Trim();
                if (k == "headset" && v.Length > 0) c.Headset = v;
                else if (k == "speakers" && v.Length > 0) c.Speakers = v;
                else if (k == "rule") { var r = Rule.Parse(v); if (r != null) c.Rules.Add(r); }
            }
        }
        catch { }
        return c;
    }

    static List<string> Lines()
    {
        try { return new List<string>(File.ReadAllLines(FilePath)); }
        catch { return new List<string>(Template.Split(new[] { "\r\n" }, StringSplitOptions.None)); }
    }

    static void Save(List<string> lines)
    {
        Directory.CreateDirectory(Setup.Dir);
        File.WriteAllLines(FilePath, lines.ToArray());
    }

    public static void EnsureExists() { if (!File.Exists(FilePath)) Save(Lines()); }

    public static void Set(string key, string value)
    {
        var lines = Lines();
        lines.RemoveAll(l => l.Trim().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
        lines.Add(key + "=" + value);
        Save(lines);
    }

    public static void SaveLearned(Rule r)
    {
        var lines = Lines();
        lines.RemoveAll(l => l.Contains("src=learned") || l.StartsWith("# learned "));
        lines.Add("# learned " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        lines.Add("rule=" + r.ToLine());
        Save(lines);
    }
}

// ---------------------------------------------------------------------------------------------
// The state machine: poll the headset, switch outputs on changes.
// ---------------------------------------------------------------------------------------------
class Switcher
{
    readonly Config cfg;
    Rule rule;
    HidIface dev;
    Guid container = Guid.Empty;
    bool hadDevice;
    DateTime nextScan = DateTime.MinValue;

    int state = -1, pend = -1, pendCount;
    readonly string[] saved = new string[3]; // default output per role while the headset was on
    string offPref;                          // what the user listens to while the headset is off

    volatile int passive = -1;
    volatile byte[] reply;
    readonly AutoResetEvent replyEv = new AutoResetEvent(false);

    public Switcher(Config c) { cfg = c; }

    public void Run()
    {
        Program.Log("HeadsetAutoSwitch " + Assembly.GetExecutingAssembly().GetName().Version);
        while (true)
        {
            try { Step(Query()); }
            catch (Exception e) { Program.Log("error: " + e.Message); }
            Thread.Sleep(2000);
        }
    }

    // --- detection -------------------------------------------------------------------------------

    public static Rule FindDevice(Config cfg, out HidIface opened, Action<HidIface, byte[]> onReport)
    {
        opened = null;
        var hids = Hid.Scan();
        List<Audio.Endpoint> eps = null;
        foreach (var r in Rule.All(cfg))
        {
            if (r.EndpointId != null)
            {
                if (eps == null) eps = Audio.List(0xF);
                if (eps.Exists(e => e.Id == r.EndpointId)) return r;
                continue;
            }
            foreach (var h in hids)
            {
                if (!r.Fits(h)) continue;
                h.OnReport = onReport;
                if (!h.Open(r.Req != null)) continue;
                if (r.Req != null && !h.Write(r.Req)) { h.Close(); continue; } // wrong collection
                opened = h;
                return r;
            }
        }
        return null;
    }

    void Attach()
    {
        if (dev != null) { dev.Close(); dev = null; }
        rule = FindDevice(cfg, out dev, OnReport);
        container = dev != null ? dev.Container : Guid.Empty;
        if (rule != null && rule.EndpointId != null)
        {
            var ep = Audio.List(0xF).Find(e => e.Id == rule.EndpointId);
            if (ep != null) container = ep.Container;
        }
        if (rule != null) { hadDevice = true; Program.Log("using rule: " + rule.Name + (dev != null ? "  [" + dev + "]" : "")); }
        else if (cfg.Headset != null) Program.Log("no known dongle; watching output '" + cfg.Headset + "' (endpoint mode)");
        else Program.Log("no known headset dongle found (run --learn to teach it yours)");
        Program.Trim();
    }

    int Query()
    {
        if ((rule == null || (dev != null && !dev.Alive)) && DateTime.Now >= nextScan)
        {
            Attach();
            nextScan = DateTime.Now.AddSeconds(dev == null && rule == null ? 10 : 0);
        }
        if (rule == null)
        {
            if (cfg.Headset != null) return EndpointState(null, cfg.Headset);
            return hadDevice ? 0 : -1; // dongle unplugged -> off
        }
        if (rule.EndpointId != null) return EndpointState(rule.EndpointId, null);
        if (dev == null || !dev.Alive) return 0;
        if (rule.Listen) return passive;

        reply = null;
        replyEv.Reset();
        if (!dev.Write(rule.Req)) { dev.Close(); return -1; }
        if (!replyEv.WaitOne(1000)) return rule.TimeoutOff ? 0 : -1;
        var r = reply;
        return r == null ? -1 : Math.Max(-1, rule.Evaluate(r));
    }

    void OnReport(HidIface h, byte[] r)
    {
        var ru = rule;
        if (ru == null) return;
        int v = ru.Evaluate(r);
        if (v == -2) return;
        if (ru.Listen) { if (v >= 0) passive = v; return; }
        if (Program.Test && v >= 0 && (lastRaw == null || lastRaw != Program.Hex(r, 8))) { lastRaw = Program.Hex(r, 8); Program.Log("dongle: " + lastRaw); }
        reply = r;
        replyEv.Set();
    }
    string lastRaw;

    static int EndpointState(string id, string name)
    {
        foreach (var e in Audio.List(0xF))
            if (id != null ? e.Id == id : e.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 && e.State == 1)
                return e.State == 1 ? 1 : 0;
        return 0;
    }

    // --- switching -------------------------------------------------------------------------------

    void Step(int s)
    {
        if (s < 0) return;
        if (s == pend) pendCount++; else { pend = s; pendCount = 1; }
        bool eventDriven = rule == null || rule.Listen || rule.EndpointId != null;
        int need = (s == 0 && !eventDriven) ? 2 : 1; // debounce "off" readings from polled dongles
        if (pendCount < need) return;

        if (s == state)
        {
            if (s == 1) for (int role = 0; role < 3; role++) { var d = Audio.GetDefault(role); if (d != null) saved[role] = d; }
            else { var d = Audio.GetDefault(0); if (d != null && !HeadsetIds().Contains(d)) offPref = d; }
            return;
        }
        bool initial = state == -1;
        state = s;
        if (s == 0) GoOff(initial); else GoOn(initial);
        Program.Trim();
    }

    List<string> HeadsetIds()
    {
        var ids = new List<string>();
        foreach (var e in Audio.List(1))
        {
            bool mine = (container != Guid.Empty && container != Audio.MachineContainer && e.Container == container)
                || (rule != null && rule.EndpointId == e.Id)
                || (cfg.Headset != null && e.Name.IndexOf(cfg.Headset, StringComparison.OrdinalIgnoreCase) >= 0);
            if (mine) ids.Add(e.Id);
        }
        return ids;
    }

    void GoOff(bool initial)
    {
        var heads = HeadsetIds();
        string cur = Audio.GetDefault(0);
        if (initial)
        {
            for (int role = 0; role < 3; role++) saved[role] = Audio.GetDefault(role);
            if (cur != null && !heads.Contains(cur)) { Program.Log("headset OFF at start; output already not the headset"); return; }
        }
        var fb = PickFallback(heads);
        if (fb == null) { Program.Log("headset OFF, but no other output to switch to"); return; }
        for (int role = 0; role < 3; role++) Audio.SetDefault(fb.Id, role);
        Program.Log("headset OFF -> " + fb.Name);
    }

    void GoOn(bool initial)
    {
        var heads = HeadsetIds();
        if (heads.Count == 0) { Program.Log("headset ON, but its audio device isn't active yet"); state = -1; return; }
        string cur = Audio.GetDefault(0);
        if (initial && cur != null && (heads.Contains(cur) || Audio.IsVirtual(Audio.Name(cur))))
        {
            Program.Log("headset ON at start; output already fine"); return;
        }
        if (!initial && cur != null && !heads.Contains(cur)) offPref = cur;
        var active = Audio.List(1);
        string best = PreferredHeadset(heads, active);
        string log = null;
        for (int role = 0; role < 3; role++)
        {
            string target = saved[role] != null && active.Exists(e => e.Id == saved[role]) ? saved[role] : best;
            Audio.SetDefault(target, role);
            if (role == 0) log = Audio.Name(target);
        }
        Program.Log("headset ON -> " + log);
    }

    static string PreferredHeadset(List<string> heads, List<Audio.Endpoint> active)
    {
        string best = heads[0];
        int bestScore = int.MinValue;
        foreach (var e in active)
        {
            if (!heads.Contains(e.Id)) continue;
            int score = (e.FormFactor == 3 || e.FormFactor == 5 ? 2 : 0) + (e.Name.IndexOf("chat", StringComparison.OrdinalIgnoreCase) >= 0 ? -3 : 0);
            if (score > bestScore) { bestScore = score; best = e.Id; }
        }
        return best;
    }

    Audio.Endpoint PickFallback(List<string> heads)
    {
        var cands = Audio.List(1).FindAll(e => !heads.Contains(e.Id));
        if (cands.Count == 0) return null;
        if (cfg.Speakers != null)
        {
            var m = cands.Find(e => e.Name.IndexOf(cfg.Speakers, StringComparison.OrdinalIgnoreCase) >= 0);
            if (m != null) return m;
        }
        if (offPref != null) { var m = cands.Find(e => e.Id == offPref); if (m != null) return m; }
        Audio.Endpoint best = null;
        int bestScore = int.MinValue;
        foreach (var e in cands)
        {
            int score = 0;
            if (e.Container == Audio.MachineContainer) score += 4;               // built into the PC
            if (e.FormFactor == 1) score += 3;                                   // speakers
            if (e.Name.IndexOf("speaker", StringComparison.OrdinalIgnoreCase) >= 0) score += 2;
            if (e.FormFactor == 3 || e.FormFactor == 5) score -= 2;              // other headphones
            if (Audio.IsVirtual(e.Name)) score -= 10;
            if (score > bestScore) { bestScore = score; best = e; }
        }
        return best;
    }
}

// ---------------------------------------------------------------------------------------------
// Learn wizard: records what the dongle and Windows report while the user turns the headset
// off and on, then finds the signal that tells the two states apart.
// ---------------------------------------------------------------------------------------------
static class Learn
{
    class Rx { public long T; public HidIface H; public byte[] D; }
    class Probe { public long T; public HidIface H; public byte[] Req, Resp; }

    static readonly Stopwatch sw = new Stopwatch();
    static readonly List<Rx> rx = new List<Rx>();
    static readonly List<Probe> probes = new List<Probe>();
    static readonly object gate = new object();
    static HidIface waitingOn;
    static byte[] waitReply;
    static readonly AutoResetEvent waitEv = new AutoResetEvent(false);
    static volatile bool stop;
    static StringBuilder log = new StringBuilder();

    public static void Run()
    {
        if (!Setup.Ask("Learn mode teaches HeadsetAutoSwitch how YOUR headset says \"I'm on\" or \"I'm off\".\n\n" +
            "1. Plug in the dongle and turn the headset ON.\n2. Click OK, then follow the 2 short steps.\n\nIt takes about a minute.")) return;
        Setup.StopOthers();

        var cands = Candidates();
        log.AppendLine("HeadsetAutoSwitch " + Assembly.GetExecutingAssembly().GetName().Version + " learn log " + DateTime.Now);
        foreach (var h in cands)
        {
            h.OnReport = OnReport;
            bool rw = h.Open(true) || h.Open(false);
            log.AppendLine("iface " + h + (rw ? "" : "  (could not open)"));
        }
        sw.Start();
        var prober = new Thread(() => ProbeLoop(cands)) { IsBackground = true };
        prober.Start();

        long t0 = sw.ElapsedMilliseconds;
        Thread.Sleep(6000);
        var e1 = Snapshot("ON");
        long t1 = sw.ElapsedMilliseconds;
        Setup.Box("Step 1 of 2\n\nTurn the headset OFF now.\nWait until it is fully off (about 5 seconds), then click OK.", 0x40);
        long t2 = sw.ElapsedMilliseconds;
        Thread.Sleep(6000);
        var e2 = Snapshot("OFF");
        long t3 = sw.ElapsedMilliseconds;
        Setup.Box("Step 2 of 2\n\nTurn the headset back ON now.\nWait until it connects (about 5-10 seconds), then click OK.", 0x40);
        long t4 = sw.ElapsedMilliseconds;
        Thread.Sleep(6000);
        var e3 = Snapshot("ON again");
        long t5 = sw.ElapsedMilliseconds;
        stop = true;
        prober.Join(3000);
        foreach (var h in cands) h.Close();
        log.AppendLine(string.Format("phases ms: on {0}-{1}  off-switch {1}-{2}  off {2}-{3}  on-switch {3}-{4}  on {4}-{5}", t0, t1, t2, t3, t4, t5));

        string how;
        Rule r = FromEndpoints(e1, e2, e3, out how)
            ?? FromProbes(t0, t1, t2, t3, t4, t5, out how)
            ?? FromEvents(t0, t1, t3, t5, out how);

        lock (gate)
        {
            foreach (var x in rx) log.AppendLine(x.T + " rx " + x.H.Short + " " + Program.Hex(x.D, 64));
            foreach (var p in probes) log.AppendLine(p.T + " probe " + p.H.Short + " " + Program.Hex(p.Req, 16) + " -> " + (p.Resp == null ? "timeout" : Program.Hex(p.Resp, 64)));
        }
        string logPath = Path.Combine(Setup.Dir, "learn-log.txt");
        try { Directory.CreateDirectory(Setup.Dir); File.WriteAllText(logPath, log.ToString() + (r != null ? "\r\nresult: " + r.ToLine() + "\r\n" : "\r\nresult: nothing found\r\n")); } catch { }

        if (r == null)
        {
            Setup.Box("Sorry, I couldn't find a signal that changes when your headset turns off.\n\n" +
                "Your dongle may need vendor software to report it. A log was saved to:\n" + logPath +
                "\n\nPlease open an issue on GitHub (bahaa-b7a/HeadsetAutoSwitch) and attach it, so support can be added.", 0x30);
            Setup.StartInstalled();
            return;
        }
        r.Learned = true;
        Config.SaveLearned(r);
        if (File.Exists(Setup.Exe)) Setup.StartInstalled(); else Setup.Install(new List<string>(), true);
        Setup.Box("Learned it!\n\nYour headset: " + r.Name + "\nMethod: " + how + "\n\nHeadsetAutoSwitch is running and will switch automatically from now on.", 0x40);
    }

    // HID interfaces that belong to an audio device (same ContainerId as an output), minus mice/keyboards.
    static List<HidIface> Candidates()
    {
        var containers = new HashSet<Guid>();
        foreach (var e in Audio.List(0xF))
            if (e.Container != Guid.Empty && e.Container != Audio.MachineContainer) containers.Add(e.Container);
        var list = new List<HidIface>();
        foreach (var h in Hid.Scan())
        {
            bool audio = containers.Contains(h.Container) || Rule.Probes.ContainsKey(h.Vid);
            if (audio && h.UsagePage != 0x01 && h.UsagePage != 0x0D && h.InLen > 0) list.Add(h);
        }
        return list;
    }

    static void OnReport(HidIface h, byte[] d)
    {
        lock (gate)
        {
            if (waitingOn == h && waitReply == null) { waitReply = d; waitEv.Set(); return; }
            rx.Add(new Rx { T = sw.ElapsedMilliseconds, H = h, D = d });
        }
    }

    static void ProbeLoop(List<HidIface> cands)
    {
        while (!stop)
        {
            foreach (var h in cands)
            {
                if (stop || h.OutLen == 0 || !h.Alive) continue;
                foreach (var req in ProbesFor(h))
                {
                    lock (gate) { waitingOn = h; waitReply = null; }
                    waitEv.Reset();
                    long t = sw.ElapsedMilliseconds;
                    if (!h.Write(req)) { lock (gate) waitingOn = null; continue; }
                    waitEv.WaitOne(400);
                    lock (gate) { probes.Add(new Probe { T = t, H = h, Req = req, Resp = waitReply }); waitingOn = null; }
                }
            }
            Thread.Sleep(700);
        }
    }

    static List<byte[]> ProbesFor(HidIface h)
    {
        var list = new List<byte[]>();
        var seen = new HashSet<string>();
        foreach (var s in Rule.BuiltIn)
        {
            var r = Rule.Parse(s);
            if (r.Vid == h.Vid && r.Req != null && seen.Add(Program.Hex(r.Req, 64)) && r.Pids.Contains(h.Pid)) list.Add(r.Req);
        }
        string[] vendor;
        if (Rule.Probes.TryGetValue(h.Vid, out vendor))
            foreach (var s in vendor) if (seen.Add(s)) list.Add(Rule.Bytes(s));
        return list;
    }

    static Dictionary<string, Audio.Endpoint> Snapshot(string label)
    {
        var d = new Dictionary<string, Audio.Endpoint>();
        foreach (var e in Audio.List(0xF)) { d[e.Id] = e; log.AppendLine("endpoint [" + label + "] state=" + e.State + " " + e.Name); }
        return d;
    }

    // 1) The headset's Windows audio device itself appears/disappears.
    static Rule FromEndpoints(Dictionary<string, Audio.Endpoint> on1, Dictionary<string, Audio.Endpoint> off, Dictionary<string, Audio.Endpoint> on2, out string how)
    {
        how = "Windows audio device appears/disappears";
        foreach (var kv in on1)
        {
            Audio.Endpoint o, a;
            if (kv.Value.State != 1 || !on2.TryGetValue(kv.Key, out a) || a.State != 1) continue;
            if (off.TryGetValue(kv.Key, out o) && o.State == 1) continue;
            return new Rule { Name = kv.Value.Name, EndpointId = kv.Key };
        }
        return null;
    }

    // 2) A status request whose reply differs between on and off.
    static Rule FromProbes(long t0, long t1, long t2, long t3, long t4, long t5, out string how)
    {
        how = "status request to the dongle";
        var groups = new Dictionary<string, List<Probe>>();
        lock (gate)
            foreach (var p in probes)
            {
                string key = p.H.Path + "|" + Program.Hex(p.Req, 64);
                if (!groups.ContainsKey(key)) groups[key] = new List<Probe>();
                groups[key].Add(p);
            }
        foreach (var g in groups.Values)
        {
            var on = g.FindAll(p => (p.T >= t0 && p.T < t1) || (p.T >= t4 && p.T < t5));
            var off = g.FindAll(p => p.T >= t2 + 1000 && p.T < t3);
            if (on.Count < 2 || off.Count < 2) continue;
            var h = g[0].H;
            var rule = new Rule { Name = h.Name, Vid = h.Vid, Req = g[0].Req, Iface = h.Iface };
            rule.Pids.Add(h.Pid); rule.Pages.Add(h.UsagePage);

            if (on.TrueForAll(p => p.Resp != null) && off.TrueForAll(p => p.Resp == null))
            {
                rule.Match = CommonPrefix(on.ConvertAll(p => p.Resp), 2);
                rule.TimeoutOff = true;
                how = "status request (no reply when off)";
                return rule;
            }
            if (!on.TrueForAll(p => p.Resp != null) || !off.TrueForAll(p => p.Resp != null)) continue;
            int len = int.MaxValue;
            foreach (var p in g) if (p.Resp != null) len = Math.Min(len, p.Resp.Length);
            for (int k = 1; k < len; k++)
            {
                var onV = new HashSet<byte>(); var offV = new HashSet<byte>();
                foreach (var p in on) onV.Add(p.Resp[k]);
                foreach (var p in off) offV.Add(p.Resp[k]);
                if (onV.Count > 2 || offV.Count > 1 || onV.Overlaps(offV)) continue;
                var all = on.ConvertAll(p => p.Resp); all.AddRange(off.ConvertAll(p => p.Resp));
                rule.Match = CommonPrefix(all, Math.Min(k, 3));
                rule.At = k;
                rule.Off.AddRange(offV);
                rule.On.AddRange(onV);
                return rule;
            }
        }
        return null;
    }

    // 3) The dongle announces the change on its own (an unsolicited report).
    static Rule FromEvents(long t0, long t1, long t3, long t5, out string how)
    {
        how = "on/off messages from the dongle";
        List<Rx> all;
        lock (gate) all = new List<Rx>(rx);
        var ifaces = new List<HidIface>();
        foreach (var x in all) if (!ifaces.Contains(x.H)) ifaces.Add(x.H);
        foreach (var h in ifaces)
        {
            var steadyOn = new HashSet<string>();
            var offSide = new HashSet<string>();
            foreach (var x in all)
                if (x.H == h)
                {
                    if (x.T >= t0 && x.T < t1) steadyOn.Add(Program.Hex(x.D, 64));
                    else if (x.T >= t1 && x.T < t3) offSide.Add(Program.Hex(x.D, 64));
                }
            var offEv = all.Find(x => x.H == h && x.T >= t1 && x.T < t3 && !steadyOn.Contains(Program.Hex(x.D, 64)));
            if (offEv == null) continue;
            foreach (var onEv in all)
            {
                if (onEv.H != h || onEv.T < t3 || onEv.T >= t5 || offSide.Contains(Program.Hex(onEv.D, 64))) continue;
                if (onEv.D.Length != offEv.D.Length || onEv.D[0] != offEv.D[0]) continue;
                int d = 1;
                while (d < onEv.D.Length && onEv.D[d] == offEv.D[d]) d++;
                if (d >= onEv.D.Length) continue;
                var r = new Rule { Name = h.Name, Vid = h.Vid, Iface = h.Iface, Listen = true, At = d };
                r.Pids.Add(h.Pid); r.Pages.Add(h.UsagePage);
                r.Match = new byte[d];
                Array.Copy(offEv.D, r.Match, d);
                r.Off.Add(offEv.D[d]);
                r.On.Add(onEv.D[d]);
                return r;
            }
        }
        return null;
    }

    static byte[] CommonPrefix(List<byte[]> list, int max)
    {
        int n = max;
        foreach (var b in list) n = Math.Min(n, b.Length);
        for (int i = 0; i < n; i++)
            foreach (var b in list)
                if (b[i] != list[0][i]) { n = i; break; }
        var p = new byte[n];
        Array.Copy(list[0], p, n);
        return p;
    }
}

static class Diag
{
    public static void List()
    {
        Console.WriteLine("HeadsetAutoSwitch " + Assembly.GetExecutingAssembly().GetName().Version + "\n");
        var eps = Audio.List(0xF);
        var containers = new HashSet<Guid>();
        foreach (var e in eps) containers.Add(e.Container);
        Console.WriteLine("HID interfaces of audio devices:");
        foreach (var h in Hid.Scan())
            if (h.Container != Audio.MachineContainer && containers.Contains(h.Container) && h.UsagePage != 0x01)
                Console.WriteLine("  " + h);
        Console.WriteLine("\nAudio outputs (state 1 = active):");
        string def = Audio.GetDefault(0);
        foreach (var e in eps)
            Console.WriteLine("  " + (e.Id == def ? "* " : "  ") + "state=" + e.State + " ff=" + e.FormFactor +
                (e.Container == Audio.MachineContainer ? " built-in" : "") + "  " + e.Name);
        HidIface dev;
        var cfg = Config.Load();
        var r = Switcher.FindDevice(cfg, out dev, null);
        Console.WriteLine("\nRule: " + (r == null ? "none (try --learn)" : r.ToLine()));
        if (dev != null) { Console.WriteLine("Dongle: " + dev); dev.Close(); }
        Console.WriteLine("Config: " + Config.FilePath);
    }
}

// ---------------------------------------------------------------------------------------------
// HID access (overlapped I/O so a background reader and writes can share one handle)
// ---------------------------------------------------------------------------------------------
class HidIface
{
    public string Path, Name = "";
    public ushort Vid, Pid, UsagePage, Usage;
    public int Iface = -1, InLen, OutLen;
    public Guid Container;
    public Action<HidIface, byte[]> OnReport;
    public volatile bool Alive;
    SafeFileHandle h;
    volatile bool closing;

    public string Short { get { return Vid.ToString("X4") + ":" + Pid.ToString("X4") + " mi" + Iface + " " + UsagePage.ToString("X4"); } }
    public override string ToString()
    {
        return Short + "/" + Usage.ToString("X4") + " in=" + InLen + " out=" + OutLen + (Name.Length > 0 ? "  " + Name : "");
    }

    public bool Open(bool write)
    {
        Close();
        closing = false;
        h = Native.CreateFile(Path, write ? 0xC0000000 : 0x80000000, 3, IntPtr.Zero, 3, 0x40000000 /*OVERLAPPED*/, IntPtr.Zero);
        if (h.IsInvalid) { h = null; return false; }
        Alive = true;
        new Thread(ReadLoop) { IsBackground = true }.Start();
        return true;
    }

    void ReadLoop()
    {
        var handle = h;
        int errors = 0;
        try
        {
            while (!closing)
            {
                var buf = new byte[InLen];
                int n = Native.Io(handle, false, buf, Timeout.Infinite);
                if (n < 0)
                {
                    // device unplugged (keeps failing) or Close() cancelled the read
                    if (closing || ++errors >= 5) break;
                    Thread.Sleep(100);
                    continue;
                }
                errors = 0;
                if (n == 0) continue;
                if (n < buf.Length) Array.Resize(ref buf, n);
                var cb = OnReport;
                if (cb != null) try { cb(this, buf); } catch { }
            }
        }
        catch { }
        Alive = false;
        handle.Dispose(); // the reader owns the handle's lifetime
    }

    public bool Write(byte[] data)
    {
        var handle = h;
        if (handle == null || OutLen == 0) return false;
        var b = new byte[OutLen];
        Array.Copy(data, b, Math.Min(data.Length, OutLen));
        return Native.Io(handle, true, b, 1000) > 0;
    }

    public void Close()
    {
        closing = true;
        var handle = h;
        h = null;
        if (handle != null) try { Native.CancelIoEx(handle, IntPtr.Zero); } catch (ObjectDisposedException) { } // reader disposes it
    }
}

static class Hid
{
    public static List<HidIface> Scan()
    {
        var list = new List<HidIface>();
        Guid g; Native.HidD_GetHidGuid(out g);
        IntPtr set = Native.SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
        if (set == new IntPtr(-1)) return list;
        try
        {
            var did = new Native.SP_DEVICE_INTERFACE_DATA(); did.cbSize = Marshal.SizeOf(did);
            for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did); i++)
            {
                var info = new Native.SP_DEVINFO_DATA(); info.cbSize = Marshal.SizeOf(info);
                uint req;
                Native.SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out req, ref info);
                IntPtr det = Marshal.AllocHGlobal((int)req);
                try
                {
                    Marshal.WriteInt32(det, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref did, det, req, out req, ref info)) continue;
                    var d = new HidIface { Path = Marshal.PtrToStringUni(det + 4) };
                    string lp = d.Path.ToLowerInvariant();
                    d.Vid = HexAfter(lp, "vid_");
                    d.Pid = HexAfter(lp, "pid_");
                    int mi = lp.IndexOf("&mi_");
                    if (mi >= 0) d.Iface = Convert.ToInt32(lp.Substring(mi + 4, 2), 16);
                    if (d.Vid == 0) continue;
                    d.Container = ContainerOf(set, ref info);
                    if (!Caps(d)) continue;
                    list.Add(d);
                }
                finally { Marshal.FreeHGlobal(det); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    static ushort HexAfter(string s, string key)
    {
        int i = s.IndexOf(key);
        if (i < 0 || i + key.Length + 4 > s.Length) return 0;
        try { return Convert.ToUInt16(s.Substring(i + key.Length, 4), 16); } catch { return 0; }
    }

    static bool Caps(HidIface d)
    {
        using (var h = Native.CreateFile(d.Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
        {
            if (h.IsInvalid) return false;
            IntPtr pp;
            if (!Native.HidD_GetPreparsedData(h, out pp)) return false;
            var caps = new byte[64]; // HIDP_CAPS
            Native.HidP_GetCaps(pp, caps);
            Native.HidD_FreePreparsedData(pp);
            d.Usage = BitConverter.ToUInt16(caps, 0);
            d.UsagePage = BitConverter.ToUInt16(caps, 2);
            d.InLen = BitConverter.ToUInt16(caps, 4);
            d.OutLen = BitConverter.ToUInt16(caps, 6);
            var sb = new StringBuilder(256);
            if (Native.HidD_GetProductString(h, sb, 512)) d.Name = sb.ToString();
            return true;
        }
    }

    static Guid ContainerOf(IntPtr set, ref Native.SP_DEVINFO_DATA info)
    {
        var key = new Native.DEVPROPKEY { fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 2 };
        var buf = new byte[16];
        uint type, req;
        if (Native.SetupDiGetDevicePropertyW(set, ref info, ref key, out type, buf, 16, out req, 0)) return new Guid(buf);
        return Guid.Empty;
    }
}

// ---------------------------------------------------------------------------------------------
// Windows audio outputs
// ---------------------------------------------------------------------------------------------
static class Audio
{
    public class Endpoint { public string Id, Name = ""; public int State; public uint FormFactor; public Guid Container; }
    public static readonly Guid MachineContainer = new Guid("00000000-0000-0000-ffff-ffffffffffff");
    static readonly string[] VirtualWords = { "nvidia broadcast", "sonar", "vb-audio", "voicemeeter", "cable", "virtual", "steam streaming", "wave link", "oculus", "nahimic", "broadcast" };
    static IMMDeviceEnumerator en;
    static IMMDeviceEnumerator En { get { return en ?? (en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo()); } }

    public static bool IsVirtual(string name)
    {
        string n = (name ?? "").ToLowerInvariant();
        foreach (var w in VirtualWords) if (n.Contains(w)) return true;
        return false;
    }

    // stateMask: 1 = active, 0xF = all
    public static List<Endpoint> List(int stateMask)
    {
        var list = new List<Endpoint>();
        IMMDeviceCollection col;
        if (En.EnumAudioEndpoints(0 /*render*/, stateMask, out col) != 0) return list;
        uint count; col.GetCount(out count);
        for (uint i = 0; i < count; i++)
        {
            IMMDevice dev;
            if (col.Item(i, out dev) != 0) continue;
            var e = new Endpoint();
            dev.GetId(out e.Id);
            dev.GetState(out e.State);
            IPropertyStore ps;
            if (dev.OpenPropertyStore(0, out ps) == 0)
            {
                PROPVARIANT pv;
                if (Get(ps, "a45c254e-df1c-4efd-8020-67d146a850e0", 14, out pv)) { if (pv.vt == 31) e.Name = Marshal.PtrToStringUni(pv.p) ?? ""; PropVariantClear(ref pv); }
                if (Get(ps, "8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2, out pv)) { if (pv.vt == 72 && pv.p != IntPtr.Zero) e.Container = (Guid)Marshal.PtrToStructure(pv.p, typeof(Guid)); PropVariantClear(ref pv); }
                if (Get(ps, "1da5d803-d492-4edd-8c23-e0c0ffee7f0e", 0, out pv)) { if (pv.vt == 19) e.FormFactor = (uint)((long)pv.p & 0xFFFFFFFF); PropVariantClear(ref pv); }
            }
            list.Add(e);
        }
        return list;
    }

    static bool Get(IPropertyStore ps, string fmtid, int pid, out PROPVARIANT pv)
    {
        var key = new PROPERTYKEY { fmtid = new Guid(fmtid), pid = pid };
        return ps.GetValue(ref key, out pv) == 0;
    }

    public static string Name(string id)
    {
        var e = List(0xF).Find(x => x.Id == id);
        return e != null ? e.Name : id;
    }

    public static string GetDefault(int role)
    {
        IMMDevice d;
        if (En.GetDefaultAudioEndpoint(0, role, out d) != 0 || d == null) return null;
        string id; d.GetId(out id);
        return id;
    }

    public static void SetDefault(string id, int role) // 0 console, 1 multimedia, 2 communications
    {
        if (id == null) return;
        var pc = (IPolicyConfig)new PolicyConfigCo();
        pc.SetDefaultEndpoint(id, role);
    }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);
    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
    [StructLayout(LayoutKind.Sequential)] struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p, p2; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection col);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice dev);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint n);
        [PreserveSig] int Item(uint i, out IMMDevice dev);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore ps);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }
    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int n);
        [PreserveSig] int GetAt(int i, out PROPERTYKEY k);
        [PreserveSig] int GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
    }
    // Undocumented but long-stable interface used by every audio-switcher tool.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(string a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(string a);
        [PreserveSig] int SetDeviceFormat(string a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(string a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(string a, IntPtr b);
        [PreserveSig] int GetShareMode(string a, IntPtr b);
        [PreserveSig] int SetShareMode(string a, IntPtr b);
        [PreserveSig] int GetPropertyValue(string a, IntPtr b, IntPtr c);
        [PreserveSig] int SetPropertyValue(string a, IntPtr b, IntPtr c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility(string a, int b);
    }
}

// ---------------------------------------------------------------------------------------------
// Self-install: copy to %LOCALAPPDATA%, add a Startup-folder shortcut, launch. No registry, no admin.
// ---------------------------------------------------------------------------------------------
static class Setup
{
    const string AppName = "HeadsetAutoSwitch";
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    public static readonly string Exe = Path.Combine(Dir, AppName + ".exe");
    static readonly string Lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), AppName + ".lnk");
    static string Self { get { return Assembly.GetExecutingAssembly().Location; } }

    public static bool RunningFromInstallDir()
    {
        return string.Equals(Path.GetFullPath(Self), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase);
    }

    public static void Install(List<string> names, bool quiet = false)
    {
        try
        {
            StopOthers();
            Directory.CreateDirectory(Dir);
            if (!RunningFromInstallDir()) File.Copy(Self, Exe, true);
            Config.EnsureExists();
            if (names.Count > 0) Config.Set("headset", names[0]);
            if (names.Count > 1) Config.Set("speakers", names[1]);
            CreateShortcut(Lnk, Exe);

            HidIface dev;
            var rule = Switcher.FindDevice(Config.Load(), out dev, null);
            if (dev != null) dev.Close();
            StartInstalled();
            if (quiet) return;
            if (rule == null && names.Count == 0)
            {
                if (Ask("Installed and running in the background.\n\nYour headset's dongle isn't in the built-in list yet. " +
                    "Teach it now? It takes about a minute: you turn the headset off and on once.")) Learn.Run();
                return;
            }
            Box("Installed and running in the background.\n\n" + (rule != null ? "Detected: " + rule.Name + "\n\n" : "") +
                "Turn your headset off and the sound moves to your speakers; turn it on and it comes back. " +
                "It starts with Windows automatically.\n\nTo remove it, run:  HeadsetAutoSwitch.exe --uninstall", 0x40);
        }
        catch (Exception e) { Program.Log("install failed: " + e); Box("Install failed: " + e.Message, 0x10); }
    }

    public static void StartInstalled()
    {
        if (File.Exists(Exe)) Process.Start(new ProcessStartInfo(Exe) { WorkingDirectory = Dir, UseShellExecute = false });
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

    public static void StopOthers()
    {
        int me = Process.GetCurrentProcess().Id;
        foreach (var p in Process.GetProcessesByName(AppName))
            if (p.Id != me) try { p.Kill(); p.WaitForExit(3000); } catch { }
    }

    static void CreateShortcut(string lnk, string target)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(t);
        object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
        Type st = sc.GetType();
        st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
        st.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { "" });
        st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
        st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Auto-switch audio when your headset turns off/on" });
        st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
    }

    public static void Box(string text, uint icon) { Native.MessageBoxW(IntPtr.Zero, text, AppName, icon | 0x50000 /*TOPMOST|FOREGROUND*/); }
    public static bool Ask(string text) { return Native.MessageBoxW(IntPtr.Zero, text, AppName, 0x40 | 1 | 0x50000) == 1; }
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int flags; public IntPtr reserved; }
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA { public int cbSize; public Guid classGuid; public uint devInst; public IntPtr reserved; }
    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, byte[] caps);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] public static extern bool HidD_GetProductString(SafeFileHandle h, StringBuilder s, int bytes);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll")] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, uint idx, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr det, uint size, out uint req, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] public static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA info, ref DEVPROPKEY key, out uint type, byte[] buf, uint size, out uint req, uint flags);
    [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern SafeFileHandle CreateFile(string p, uint acc, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, IntPtr b, uint n, IntPtr r, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, IntPtr b, uint n, IntPtr w, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ov, out uint n, bool wait);
    [DllImport("kernel32.dll")] public static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr p, IntPtr min, IntPtr max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);

    // One overlapped read or write. Returns bytes transferred, -1 on error, -2 on timeout.
    public static int Io(SafeFileHandle h, bool write, byte[] data, int timeoutMs)
    {
        if (h == null || h.IsClosed) return -1;
        try { return IoCore(h, write, data, timeoutMs); }
        catch (ObjectDisposedException) { return -1; } // closed by another thread mid-call
    }

    static int IoCore(SafeFileHandle h, bool write, byte[] data, int timeoutMs)
    {
        int ovSize = IntPtr.Size == 8 ? 32 : 20, evOffset = IntPtr.Size == 8 ? 24 : 16;
        using (var ev = new ManualResetEvent(false))
        {
            IntPtr ov = Marshal.AllocHGlobal(ovSize), buf = Marshal.AllocHGlobal(data.Length);
            try
            {
                for (int i = 0; i < ovSize; i++) Marshal.WriteByte(ov, i, 0);
                Marshal.WriteIntPtr(ov, evOffset, ev.SafeWaitHandle.DangerousGetHandle());
                if (write) Marshal.Copy(data, 0, buf, data.Length);
                bool ok = write ? WriteFile(h, buf, (uint)data.Length, IntPtr.Zero, ov) : ReadFile(h, buf, (uint)data.Length, IntPtr.Zero, ov);
                if (!ok && Marshal.GetLastWin32Error() != 997 /*IO_PENDING*/) return -1;
                uint n;
                if (!ev.WaitOne(timeoutMs))
                {
                    CancelIoEx(h, ov);
                    GetOverlappedResult(h, ov, out n, true);
                    return -2;
                }
                if (!GetOverlappedResult(h, ov, out n, false)) return -1;
                if (!write) Marshal.Copy(buf, data, 0, (int)n);
                return (int)n;
            }
            finally { Marshal.FreeHGlobal(buf); Marshal.FreeHGlobal(ov); }
        }
    }
}
