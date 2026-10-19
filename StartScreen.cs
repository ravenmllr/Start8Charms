// =====================================================================================
//  Windows 8 style Start screen - opened ONLY by the Start charm of the Charms Bar.
//  (The Windows key / taskbar button still open the normal Windows 10 Start menu.)
//
//  - Apps come from Windows itself (shell:AppsFolder), so Win32 + packaged apps are all
//    there and launching is done by Windows. No live tiles.
//  - Tiles: Small / Medium / Wide / Large. Right-click a tile -> app bar -> Larger / Smaller /
//    Unpin. Drag a tile to re-order. Down-arrow (bottom-left) -> All apps -> right-click -> Pin.
//  - Top right: user name + picture, power button (Sleep / Hibernate / Shut down / Restart /
//    Sign out), search button.
//  - Zero CPU when idle; repaints only on interaction. Layout is saved in
//    %AppData%\Win8Charms\start.txt
//  - C# 5 only (so the compiler that ships in Windows can build it).
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Win8Charms
{
    // ------------------------------------------------------------------ settings ---
    static class SCfg
    {
        public static readonly Color Back = Color.FromArgb(0, 24, 64);       // Windows 8 default navy
        public static readonly Color Ring = Color.FromArgb(0, 38, 94);       // faint bubbles
        public static readonly Color BarBack = Color.FromArgb(31, 31, 31);   // app bar
        public const int AutoGroupSize = 16;                                  // tiles per group on first run
    }

    // ---------------------------------------------------------- shell COM interop ---
    [StructLayout(LayoutKind.Sequential)]
    struct NSize { public int cx; public int cy; }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, out IShellItem rgelt, out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems ppenum);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NSize size, int flags, out IntPtr phbm);
    }

    static class ShellApi
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        public static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);
        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool LockWorkStation();
        [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetUserNameEx(int nameFormat, StringBuilder lpNameBuffer, ref uint nSize);
    }

    // ------------------------------------------------------------------ app model ---
    class AppItem
    {
        public string Id = "", Name = "", Letter = "?";
        public Bitmap Icon;
        public Color Tint = Color.FromArgb(38, 114, 236);
        public bool Special;        // the built-in Desktop tile
    }

    class Pin
    {
        public string Id = "";
        public int Size = 1;        // 0 small, 1 medium, 2 wide, 3 large
        public int Group;
    }

    class Cell
    {
        public AppItem App;
        public Pin Pin;
        public Rectangle R;         // page coordinates (before scrolling)
    }

    class PopItem
    {
        public string Text;
        public bool Enabled;
        public Action Run;
        public PopItem(string text, bool enabled, Action run) { Text = text; Enabled = enabled; Run = run; }
    }

    class BarBtn
    {
        public string Text = "";
        public int Kind;            // 0 unpin, 1 larger, 2 smaller, 3 pin
        public Rectangle R;
        public Action Run;
    }

    // ------------------------------------------------- installed apps (via Windows) ---
    static class Catalog
    {
        public static readonly object Gate = new object();
        public static volatile bool Scanned;
        public static List<AppItem> Apps = new List<AppItem>();

        static readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>();
        static DateTime lastScan = DateTime.MinValue;
        static System.Threading.Thread worker;

        static readonly Color[] Palette = new Color[]
        {
            Color.FromArgb(38, 114, 236),   // blue
            Color.FromArgb(0, 158, 40),     // green
            Color.FromArgb(176, 30, 70),    // crimson
            Color.FromArgb(217, 83, 41),    // orange
            Color.FromArgb(95, 50, 170),    // purple
            Color.FromArgb(105, 170, 18),   // lime
            Color.FromArgb(0, 140, 150),    // teal
            Color.FromArgb(190, 40, 60)     // red
        };

        public static bool Stale { get { return (DateTime.UtcNow - lastScan).TotalMinutes > 5; } }
        public static bool Busy { get { System.Threading.Thread w = worker; return w != null && w.IsAlive; } }

        public static void ScanAsync()
        {
            if (Busy) return;
            System.Threading.Thread t = new System.Threading.Thread(delegate() { try { Scan(); } catch { } });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.IsBackground = true;
            t.Priority = System.Threading.ThreadPriority.BelowNormal;
            worker = t;
            t.Start();
        }

        static void Scan()
        {
            List<AppItem> found = new List<AppItem>();
            try
            {
                IShellItem folder = null;
                IEnumShellItems en = null;
                try
                {
                    Guid iid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
                    ShellApi.SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref iid, out folder);
                    Guid bhid = new Guid("94f60519-2850-4924-aa5a-d15e84868039");      // BHID_EnumItems
                    Guid riid = new Guid("70629033-e363-4a28-a567-0db78006e6d7");      // IEnumShellItems
                    IntPtr ppv;
                    if (folder.BindToHandler(IntPtr.Zero, ref bhid, ref riid, out ppv) == 0 && ppv != IntPtr.Zero)
                    {
                        en = (IEnumShellItems)Marshal.GetObjectForIUnknown(ppv);
                        Marshal.Release(ppv);
                        HashSet<string> seen = new HashSet<string>();
                        while (true)
                        {
                            IShellItem it;
                            uint got;
                            int hr = en.Next(1, out it, out got);
                            if (hr != 0 || got == 0 || it == null) break;
                            try
                            {
                                AppItem a = Read(it);
                                if (a != null && seen.Add(a.Id)) found.Add(a);
                            }
                            catch { }
                            finally { Marshal.ReleaseComObject(it); }
                        }
                    }
                }
                finally
                {
                    if (en != null) Marshal.ReleaseComObject(en);
                    if (folder != null) Marshal.ReleaseComObject(folder);
                }
            }
            catch { }

            // built-in Desktop tile (shows the wallpaper like Windows 8 did)
            AppItem d = new AppItem();
            d.Id = "builtin:desktop";
            d.Name = "Desktop";
            d.Letter = "D";
            d.Special = true;
            d.Tint = Color.FromArgb(0, 112, 140);
            found.Add(d);

            found.Sort(delegate(AppItem x, AppItem y)
            {
                return string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase);
            });

            lock (Gate)
            {
                Apps = found;
                lastScan = DateTime.UtcNow;
                Scanned = true;
            }
            try { Wall.Refresh(); } catch { }
        }

        static AppItem Read(IShellItem it)
        {
            string name, id;
            if (it.GetDisplayName(0u, out name) != 0 || string.IsNullOrEmpty(name)) return null;
            if (it.GetDisplayName(0x80018001u, out id) != 0 || string.IsNullOrEmpty(id)) return null;     // SIGDN_PARENTRELATIVEPARSING
            if (id.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)) id = id.Substring(17);
            if (name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)) return null;

            AppItem a = new AppItem();
            a.Id = id;
            a.Name = name;
            a.Letter = name.Substring(0, 1).ToUpper();
            int h = 17;
            for (int i = 0; i < name.Length; i++) h = h * 31 + name[i];
            a.Tint = Palette[(int)((uint)h % (uint)Palette.Length)];

            Bitmap ic;
            lock (Gate) { icons.TryGetValue(id, out ic); }
            if (ic == null)
            {
                try
                {
                    IShellItemImageFactory f = it as IShellItemImageFactory;
                    if (f != null)
                    {
                        NSize sz;
                        sz.cx = 96;
                        sz.cy = 96;
                        IntPtr hbm;
                        if (f.GetImage(sz, 0x4, out hbm) == 0 && hbm != IntPtr.Zero)   // SIIGBF_ICONONLY
                        {
                            ic = FromHBitmap(hbm);
                            if (ic != null) lock (Gate) { icons[id] = ic; }
                        }
                    }
                }
                catch { }
            }
            a.Icon = ic;
            return a;
        }

        // HBITMAP (32bpp with alpha) -> managed Bitmap that keeps its transparency
        static Bitmap FromHBitmap(IntPtr hbm)
        {
            Bitmap tmp = null;
            try
            {
                tmp = Image.FromHbitmap(hbm);
                if (Image.GetPixelFormatSize(tmp.PixelFormat) < 32) return new Bitmap(tmp);
                Rectangle r = new Rectangle(0, 0, tmp.Width, tmp.Height);
                BitmapData d = tmp.LockBits(r, ImageLockMode.ReadOnly, tmp.PixelFormat);
                try
                {
                    using (Bitmap wrap = new Bitmap(d.Width, d.Height, d.Stride, PixelFormat.Format32bppArgb, d.Scan0))
                        return new Bitmap(wrap);
                }
                finally { tmp.UnlockBits(d); }
            }
            catch { return null; }
            finally
            {
                if (tmp != null) tmp.Dispose();
                ShellApi.DeleteObject(hbm);
            }
        }
    }

    // ----------------------------------------------------------- wallpaper (Desktop tile) ---
    static class Wall
    {
        static readonly object gate = new object();
        static string path;
        static DateTime stamp;
        static Bitmap bmp;

        public static Bitmap Current { get { lock (gate) { return bmp; } } }

        public static void Refresh()
        {
            try
            {
                string p = null;
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                {
                    if (k != null) p = k.GetValue("WallPaper") as string;
                }
                if (string.IsNullOrEmpty(p) || !File.Exists(p))
                {
                    string t = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        @"Microsoft\Windows\Themes\TranscodedWallpaper");
                    p = File.Exists(t) ? t : null;
                }
                if (p == null) { lock (gate) { bmp = null; path = null; } return; }

                DateTime st = File.GetLastWriteTimeUtc(p);
                lock (gate) { if (bmp != null && p == path && st == stamp) return; }

                Bitmap nb;
                using (Image im = Image.FromFile(p))
                {
                    int w = 640;
                    int h = Math.Max(1, im.Height * w / Math.Max(1, im.Width));
                    nb = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                    using (Graphics g = Graphics.FromImage(nb))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(im, 0, 0, w, h);
                    }
                }
                lock (gate) { bmp = nb; path = p; stamp = st; }     // old bitmap is left to the GC (UI thread may still be drawing it)
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- user profile ---
    static class UserInfo
    {
        public static string DisplayName()
        {
            try
            {
                StringBuilder sb = new StringBuilder(256);
                uint n = 256;
                if (ShellApi.GetUserNameEx(3, sb, ref n) && sb.Length > 0) return sb.ToString();     // NameDisplay
            }
            catch { }
            return Environment.UserName;
        }

        public static Bitmap Avatar(int px)
        {
            string[] names = new string[] { "Image192", "Image208", "Image240", "Image96", "Image448", "Image424", "Image48", "Image40", "Image32" };
            try
            {
                string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\" + sid))
                {
                    if (k != null)
                    {
                        for (int i = 0; i < names.Length; i++)
                        {
                            string p = k.GetValue(names[i]) as string;
                            if (!string.IsNullOrEmpty(p) && File.Exists(p))
                            {
                                Bitmap b = Load(p, px);
                                if (b != null) return b;
                            }
                        }
                    }
                }
            }
            catch { }
            try
            {
                string pd = Environment.GetEnvironmentVariable("ProgramData");
                if (!string.IsNullOrEmpty(pd))
                {
                    string d = Path.Combine(pd, @"Microsoft\User Account Pictures\user.png");
                    if (File.Exists(d)) return Load(d, px);
                }
            }
            catch { }
            return null;
        }

        static Bitmap Load(string path, int px)
        {
            try
            {
                using (Image im = Image.FromFile(path))
                {
                    Bitmap b = new Bitmap(px, px, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(im, 0, 0, px, px);
                    }
                    return b;
                }
            }
            catch { return null; }
        }
    }

    // ------------------------------------------------------------------ power menu ---
    static class PowerOps
    {
        public static void Sleep() { try { Application.SetSuspendState(PowerState.Suspend, false, false); } catch { } }
        public static void Hibernate() { try { Application.SetSuspendState(PowerState.Hibernate, false, false); } catch { } }
        public static void ShutDown() { Run("/s /t 0"); }
        public static void Restart() { Run("/r /t 0"); }
        public static void SignOut() { Run("/l"); }
        public static void Lock() { try { ShellApi.LockWorkStation(); } catch { } }

        // hibernation must be enabled once with:  powercfg /hibernate on   (administrator)
        public static bool HibernateEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("HibernateEnabled");
                        return v is int && (int)v != 0;
                    }
                }
            }
            catch { }
            return false;
        }

        static void Run(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("shutdown.exe", args);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch { }
        }

        // Windows 8 "Desktop" tile: minimise everything (MinimizeAll never toggles back)
        public static void ShowDesktop()
        {
            try
            {
                Type t = Type.GetTypeFromProgID("Shell.Application");
                object sh = Activator.CreateInstance(t);
                t.InvokeMember("MinimizeAll", BindingFlags.InvokeMethod, null, sh, null);
            }
            catch { Actions.Launch("shell:::{3080F90D-D7AD-11D9-BD98-0000947B0257}"); }
        }
    }

    // ------------------------------------------------- tile layout (saved on disk) ---
    static class PinStore
    {
        public static readonly List<Pin> Pins = new List<Pin>();
        static readonly HashSet<string> known = new HashSet<string>();

        static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Win8Charms\start.txt"); }
        }

        public static void Load()
        {
            Pins.Clear();
            known.Clear();
            try
            {
                if (!File.Exists(FilePath)) return;
                string[] lines = File.ReadAllLines(FilePath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string[] f = lines[i].Split('\t');
                    if (f.Length >= 4 && f[0] == "P")
                    {
                        Pin p = new Pin();
                        p.Id = f[1];
                        int.TryParse(f[2], out p.Size);
                        int.TryParse(f[3], out p.Group);
                        if (p.Size < 0 || p.Size > 3) p.Size = 1;
                        Pins.Add(p);
                    }
                    else if (f.Length >= 2 && f[0] == "K") known.Add(f[1]);
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                List<string> lines = new List<string>();
                foreach (Pin p in Pins) lines.Add("P\t" + p.Id + "\t" + p.Size + "\t" + p.Group);
                foreach (string k in known) lines.Add("K\t" + k);
                File.WriteAllLines(path, lines.ToArray());
            }
            catch { }
        }

        // drop pins of removed apps, auto-pin apps we have never seen before
        public static void Sync(List<AppItem> apps)
        {
            HashSet<string> ids = new HashSet<string>();
            foreach (AppItem a in apps) ids.Add(a.Id);
            bool changed = Pins.RemoveAll(delegate(Pin p) { return !ids.Contains(p.Id); }) > 0;

            if (ids.Contains("builtin:desktop") && !known.Contains("builtin:desktop"))
            {
                known.Add("builtin:desktop");
                Pin d = new Pin();
                d.Id = "builtin:desktop";
                d.Size = 2;
                d.Group = 0;
                Pins.Insert(0, d);
                changed = true;
            }
            foreach (AppItem a in apps)
            {
                if (known.Contains(a.Id)) continue;
                known.Add(a.Id);
                AddPin(a.Id);
                changed = true;
            }
            if (changed) Save();
        }

        static void AddPin(string id)
        {
            Pin p = new Pin();
            p.Id = id;
            p.Size = 1;
            if (Pins.Count > 0)
            {
                int g = Pins[Pins.Count - 1].Group, n = 0;
                foreach (Pin q in Pins) if (q.Group == g) n++;
                p.Group = n >= SCfg.AutoGroupSize ? g + 1 : g;
            }
            Pins.Add(p);
        }

        public static bool IsPinned(string id)
        {
            foreach (Pin p in Pins) if (p.Id == id) return true;
            return false;
        }

        public static void Add(string id) { if (!IsPinned(id)) AddPin(id); }

        public static void Unpin(string id) { Pins.RemoveAll(delegate(Pin p) { return p.Id == id; }); }

        public static void Move(Pin p, Pin before)
        {
            if (p == before) return;
            Pins.Remove(p);
            int i = Pins.IndexOf(before);
            if (i < 0) i = Pins.Count;
            Pins.Insert(i, p);
            p.Group = before.Group;
        }
    }

    // ------------------------------------------------- entry point used by the charms ---
    static class StartScreen
    {
        static StartForm form;
        static Timer wait;

        public static void Init()
        {
            PinStore.Load();
            Catalog.ScanAsync();
        }

        public static void Toggle()
        {
            if (form != null && form.IsOpen) { form.Dismiss(); return; }
            if (!Catalog.Scanned)
            {
                // first scan still running: never block the UI thread (the mouse hook lives on it)
                Catalog.ScanAsync();
                if (wait == null)
                {
                    wait = new Timer();
                    wait.Interval = 150;
                    wait.Tick += delegate { if (Catalog.Scanned) { wait.Stop(); Open(); } };
                }
                wait.Start();
                return;
            }
            Open();
        }

        static void Open()
        {
            if (form == null) form = new StartForm();
            List<AppItem> apps;
            lock (Catalog.Gate) { apps = Catalog.Apps; }
            form.Present(Screen.FromPoint(Cursor.Position), apps);
            if (Catalog.Stale) Catalog.ScanAsync();      // refresh in the background for next time
        }
    }

    // ===================================================================== the window ===
    class StartForm : Form
    {
        const TextFormatFlags TF = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top;
        const int HDR_NONE = 0, HDR_AVATAR = 1, HDR_POWER = 2, HDR_SEARCH = 3, HDR_TOGGLE = 4;

        // decorative bubbles, reference size 1366x768: cx, cy, radius, stroke
        static readonly float[][] Rings = new float[][]
        {
            new float[] { 520, 25, 105, 5 }, new float[] { 1010, 88, 64, 4 }, new float[] { 705, 87, 12, 3 },
            new float[] { 375, 20, 12, 3 }, new float[] { 470, 150, 40, 3 }, new float[] { 958, 168, 46, 3 },
            new float[] { 160, 690, 56, 4 }, new float[] { 480, 705, 62, 4 }, new float[] { 680, 770, 38, 3 },
            new float[] { 890, 735, 28, 3 }, new float[] { 1290, 650, 70, 4 }, new float[] { 60, 380, 30, 3 }
        };

        // geometry / state
        float S = 1f, fontsFor = -1f;
        int W, H, rows, pitch, gap, tilesTop, barH, contentW;
        double maxScroll, scrollX, scrollT, barT, barTarget, op, opT, lastT;
        Rectangle screenRect;
        bool shown, closing, activated, allMode;
        Color accent = Color.FromArgb(0, 120, 215);

        List<AppItem> allList = new List<AppItem>();
        readonly Dictionary<string, AppItem> byId = new Dictionary<string, AppItem>();
        readonly List<Cell> cells = new List<Cell>();
        readonly List<BarBtn> bars = new List<BarBtn>();
        Cell hover, selected, pressCell, dropTarget;
        Point pressPt, dragPt, dragOff;
        bool dragging, panning, thumbDrag;
        int panX, thumbGrab, barPress = -1, hdrPress, barHover = -1;
        double panScroll;

        string name1 = "", name2 = "";
        Bitmap avatar, bg, wall;
        Rectangle rcAvatar, rcPower, rcSearch, rcToggle, rcThumb;

        List<PopItem> menu;
        Rectangle rcMenu;
        int menuHover = -1, menuItemH;

        Font fTitle, fName1, fName2, fTile, fLetter, fMenu, fBar, fItem;
        readonly Timer anim;
        readonly Stopwatch clock = Stopwatch.StartNew();

        public StartForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = SCfg.Back;
            Text = "Start";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            anim = new Timer();
            anim.Interval = 1;
            anim.Tick += OnAnim;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW;      // keep it out of Alt+Tab
                return cp;
            }
        }

        public bool IsOpen { get { return shown && !closing; } }

        // ------------------------------------------------------------ show / hide ---
        public void Present(Screen scr, List<AppItem> apps)
        {
            if (shown)
            {
                if (closing) { closing = false; opT = 1; StartAnim(); ShellApi.SetForegroundWindow(Handle); }
                return;
            }
            allList = apps;
            byId.Clear();
            foreach (AppItem a in apps) byId[a.Id] = a;
            PinStore.Sync(apps);

            screenRect = scr.Bounds;
            W = screenRect.Width;
            H = screenRect.Height;
            S = Math.Max(0.75f, Math.Min(3f, H / 768f));        // same scaling idea Windows 8 used (140% at 1080p)
            gap = Math.Max(2, (int)Math.Round(8 * S));
            pitch = (int)Math.Round(64 * S);
            barH = (int)Math.Round(112 * S);
            if (fontsFor != S) MakeFonts();
            accent = Sys.Visible(Sys.Accent());
            LoadUser();
            wall = Wall.Current;
            BuildBackground();

            allMode = false;
            selected = null;
            hover = null;
            menu = null;
            barT = 0;
            barTarget = 0;
            scrollX = 0;
            scrollT = 0;
            Relayout();

            Bounds = screenRect;
            op = 0;
            opT = 1;
            Opacity = 0;
            closing = false;
            shown = true;
            activated = false;
            Show();
            Native.MakeTopmost(Handle);
            ShellApi.SetForegroundWindow(Handle);
            Activate();
            StartAnim();
        }

        public void Dismiss()
        {
            if (!shown || closing) return;
            closing = true;
            opT = 0;
            menu = null;
            barTarget = 0;
            StartAnim();
        }

        void FinishClose()
        {
            Hide();
            shown = false;
            closing = false;
            selected = null;
            hover = null;
            menu = null;
            dragging = false;
            panning = false;
            pressCell = null;
            cells.Clear();
            Opacity = 1.0;
            op = 1;
            if (bg != null) { bg.Dispose(); bg = null; }
            if (avatar != null) { avatar.Dispose(); avatar = null; }
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            activated = true;
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (activated && shown && !closing) Dismiss();      // e.g. the normal Start menu or another app took focus
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Dismiss(); }      // Alt+F4
            base.OnFormClosing(e);
        }

        // ----------------------------------------------------------------- set-up ---
        Font Fnt(string family, float px) { return new Font(family, px * S, FontStyle.Regular, GraphicsUnit.Pixel); }

        void MakeFonts()
        {
            fTitle = Fnt("Segoe UI Light", 56);
            fName1 = Fnt("Segoe UI Light", 26);
            fName2 = Fnt("Segoe UI", 14);
            fTile = Fnt("Segoe UI", 12.5f);
            fLetter = Fnt("Segoe UI Light", 44);
            fMenu = Fnt("Segoe UI", 15);
            fBar = Fnt("Segoe UI", 12.5f);
            fItem = Fnt("Segoe UI", 15);
            fontsFor = S;
        }

        void LoadUser()
        {
            string full = UserInfo.DisplayName().Trim();
            int sp = full.LastIndexOf(' ');
            if (sp > 0) { name1 = full.Substring(0, sp); name2 = full.Substring(sp + 1); }
            else { name1 = full; name2 = ""; }
            if (avatar != null) avatar.Dispose();
            avatar = UserInfo.Avatar((int)Math.Round(80 * S));

            int m = (int)Math.Round(46 * S), ico = (int)Math.Round(24 * S), cy = (int)Math.Round(80 * S);
            rcSearch = new Rectangle(W - m - ico, cy - ico / 2, ico, ico);
            rcPower = new Rectangle(rcSearch.Left - (int)Math.Round(36 * S) - ico, cy - ico / 2, ico, ico);
            int av = (int)Math.Round(40 * S);
            rcAvatar = new Rectangle(rcPower.Left - (int)Math.Round(29 * S) - av, cy - av / 2, av, av);
            int bt = (int)Math.Round(32 * S);
            rcToggle = new Rectangle((int)Math.Round(46 * S), H - (int)Math.Round(72 * S), bt, bt);
        }

        void BuildBackground()
        {
            if (bg != null) bg.Dispose();
            bg = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bg))
            {
                g.Clear(SCfg.Back);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                for (int i = 0; i < Rings.Length; i++)
                {
                    float[] c = Rings[i];
                    float cx = c[0] * S, cy = c[1] * S, r = c[2] * S;
                    using (Pen p = new Pen(SCfg.Ring, Math.Max(1.5f, c[3] * S)))
                        g.DrawEllipse(p, cx - r, cy - r, 2 * r, 2 * r);
                }
            }
        }

        // ----------------------------------------------------------------- layout ---
        static void UnitSize(int size, out int w, out int h)
        {
            switch (size)
            {
                case 0: w = 1; h = 1; break;     // small
                case 2: w = 4; h = 2; break;     // wide
                case 3: w = 4; h = 4; break;     // large
                default: w = 2; h = 2; break;    // medium
            }
        }

        static bool Free(List<bool[]> occ, int c, int r, int w, int h)
        {
            for (int i = c; i < c + w && i < occ.Count; i++)
                for (int j = r; j < r + h; j++)
                    if (occ[i][j]) return false;
            return true;
        }

        static void Place(List<bool[]> occ, int halfRows, int w, int h, out int pc, out int pr)
        {
            for (int c = 0; ; c++)
            {
                for (int r = 0; r + h <= halfRows; r++)
                {
                    if (!Free(occ, c, r, w, h)) continue;
                    while (occ.Count < c + w) occ.Add(new bool[halfRows]);
                    for (int i = c; i < c + w; i++)
                        for (int j = r; j < r + h; j++)
                            occ[i][j] = true;
                    pc = c;
                    pr = r;
                    return;
                }
            }
        }

        void Relayout()
        {
            string selId = selected != null ? selected.App.Id : null;
            cells.Clear();
            hover = null;
            int left = (int)Math.Round(120 * S);
            tilesTop = (int)Math.Round(184 * S);
            int availH = H - tilesTop - (int)Math.Round(80 * S);

            if (!allMode)
            {
                rows = Math.Max(2, Math.Min(8, (availH + gap) / (2 * pitch)));
                int halfRows = rows * 2;
                List<bool[]> occ = null;
                int curGroup = int.MinValue, groupX = left, endX = left;
                int groupGap = (int)Math.Round(56 * S);
                foreach (Pin p in PinStore.Pins)
                {
                    AppItem a;
                    if (!byId.TryGetValue(p.Id, out a)) continue;
                    if (occ == null || p.Group != curGroup)
                    {
                        if (occ != null) groupX = groupX + occ.Count * pitch - gap + groupGap;
                        occ = new List<bool[]>();
                        curGroup = p.Group;
                    }
                    int w, h, c, r;
                    UnitSize(p.Size, out w, out h);
                    Place(occ, halfRows, w, h, out c, out r);
                    Cell cell = new Cell();
                    cell.App = a;
                    cell.Pin = p;
                    cell.R = new Rectangle(groupX + c * pitch, tilesTop + r * pitch, w * pitch - gap, h * pitch - gap);
                    cells.Add(cell);
                    endX = groupX + occ.Count * pitch - gap;
                }
                contentW = endX + left;
            }
            else
            {
                int itemW = (int)Math.Round(268 * S), itemH = (int)Math.Round(56 * S);
                int g2 = (int)Math.Round(6 * S), colGap = (int)Math.Round(24 * S);
                int perCol = Math.Max(1, (availH + g2) / (itemH + g2));
                int n = 0;
                foreach (AppItem a in allList)
                {
                    Cell cell = new Cell();
                    cell.App = a;
                    int col = n / perCol, row = n % perCol;
                    cell.R = new Rectangle(left + col * (itemW + colGap), tilesTop + row * (itemH + g2), itemW, itemH);
                    cells.Add(cell);
                    n++;
                }
                int cols = (n + perCol - 1) / perCol;
                contentW = left + cols * (itemW + colGap) + (int)Math.Round(96 * S);
            }

            maxScroll = Math.Max(0, contentW - W);
            scrollT = Math.Max(0, Math.Min(maxScroll, scrollT));
            scrollX = Math.Max(0, Math.Min(maxScroll, scrollX));

            selected = null;
            if (selId != null)
            {
                foreach (Cell c2 in cells) if (c2.App.Id == selId) { selected = c2; break; }
            }
            if (selected != null) BuildBar();
            else if (barTarget > 0) { barTarget = 0; StartAnim(); }
        }

        Cell HitCell(Point p)
        {
            int px = p.X + (int)Math.Round(scrollX);
            for (int i = 0; i < cells.Count; i++)
                if (cells[i].R.Contains(px, p.Y)) return cells[i];
            return null;
        }

        void InvalidateCell(Cell c)
        {
            if (c == null) return;
            int sx = (int)Math.Round(scrollX), m = (int)Math.Round(6 * S);
            Invalidate(new Rectangle(c.R.X - sx - m, c.R.Y - m, c.R.Width + 2 * m, c.R.Height + 2 * m));
        }

        // -------------------------------------------------------------- animation ---
        void StartAnim()
        {
            if (anim.Enabled) return;
            lastT = clock.Elapsed.TotalSeconds;
            anim.Start();
        }

        void OnAnim(object sender, EventArgs e)
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt = Math.Min(0.05, Math.Max(0.001, now - lastT));
            lastT = now;
            bool active = false, repaint = false;

            if (Math.Abs(op - opT) > 0.0005)
            {
                double step = dt / (opT > op ? 0.20 : 0.14);
                op = op < opT ? Math.Min(opT, op + step) : Math.Max(opT, op - step);
                Opacity = op;
                active = true;
                if (op <= 0.0 && opT <= 0.0)
                {
                    anim.Stop();
                    FinishClose();
                    return;
                }
            }

            double d = scrollT - scrollX;
            if (Math.Abs(d) > 0.4) { scrollX += d * (1 - Math.Exp(-dt * 16)); active = true; repaint = true; }
            else if (d != 0) { scrollX = scrollT; repaint = true; }

            double bd = barTarget - barT;
            if (Math.Abs(bd) > 0.003) { barT += bd * (1 - Math.Exp(-dt * 18)); active = true; repaint = true; }
            else if (bd != 0) { barT = barTarget; repaint = true; }

            if (repaint) Invalidate();
            if (active) Native.Flush();       // sync to the display refresh
            else anim.Stop();
        }

        void ScrollBy(double dx)
        {
            scrollT = Math.Max(0, Math.Min(maxScroll, scrollT + dx));
            StartAnim();
        }

        // ---------------------------------------------------------------- painting ---
        static void DrawCover(Graphics g, Bitmap im, Rectangle d)
        {
            float ar = d.Width / (float)d.Height, ir = im.Width / (float)im.Height;
            RectangleF src;
            if (ir > ar) { float w = im.Height * ar; src = new RectangleF((im.Width - w) / 2f, 0, w, im.Height); }
            else { float h = im.Width / ar; src = new RectangleF(0, (im.Height - h) / 2f, im.Width, h); }
            g.DrawImage(im, new RectangleF(d.X, d.Y, d.Width, d.Height), src, GraphicsUnit.Pixel);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (bg != null) g.DrawImage(bg, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
            else g.Clear(SCfg.Back);
            if (cells.Count == 0 && !shown) return;

            DrawHeader(g);

            int sx = (int)Math.Round(scrollX);
            g.SmoothingMode = SmoothingMode.None;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            Rectangle clip = e.ClipRectangle;
            for (int i = 0; i < cells.Count; i++)
            {
                Cell c = cells[i];
                Rectangle r = new Rectangle(c.R.X - sx, c.R.Y, c.R.Width, c.R.Height);
                if (!r.IntersectsWith(clip)) continue;
                if (dragging && c == pressCell) continue;
                if (allMode) DrawListItem(g, c, r);
                else DrawTile(g, c, r, c == pressCell && c == hover && !dragging && MouseButtons == MouseButtons.Left);
            }

            if (dragging && pressCell != null && !allMode)
            {
                if (dropTarget != null)
                {
                    Rectangle t = new Rectangle(dropTarget.R.X - sx, dropTarget.R.Y, dropTarget.R.Width, dropTarget.R.Height);
                    using (Pen p = new Pen(Color.FromArgb(230, 255, 255, 255), Math.Max(3f, 3 * S)))
                    {
                        p.Alignment = PenAlignment.Inset;
                        g.DrawRectangle(p, t.X, t.Y, t.Width - 1, t.Height - 1);
                    }
                }
                Rectangle gr = new Rectangle(dragPt.X - dragOff.X, dragPt.Y - dragOff.Y, pressCell.R.Width, pressCell.R.Height);
                DrawTile(g, pressCell, gr, false);
            }

            DrawToggle(g);
            DrawScrollbar(g);
            DrawBar(g);
            DrawMenu(g);
        }

        void DrawHeader(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            TextRenderer.DrawText(g, allMode ? "Apps" : "Start", fTitle, new Point((int)Math.Round(116 * S), (int)Math.Round(41 * S)), Color.White, TF);

            int right = rcAvatar.Left - (int)Math.Round(10 * S);
            int boxW = (int)Math.Round(500 * S);
            TextFormatFlags rf = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.Right | TextFormatFlags.Top;
            if (name2.Length > 0)
            {
                TextRenderer.DrawText(g, name1, fName1, new Rectangle(right - boxW, (int)Math.Round(48 * S), boxW, (int)Math.Round(36 * S)), Color.White, rf);
                TextRenderer.DrawText(g, name2, fName2, new Rectangle(right - boxW, (int)Math.Round(83 * S), boxW, (int)Math.Round(20 * S)), Color.White, rf);
            }
            else
            {
                TextRenderer.DrawText(g, name1, fName1, new Rectangle(right - boxW, (int)Math.Round(64 * S), boxW, (int)Math.Round(36 * S)), Color.White, rf);
            }

            if (avatar != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(avatar, rcAvatar);
            }
            else DrawDefaultAvatar(g, rcAvatar);

            // power icon
            float cx = rcPower.X + rcPower.Width / 2f, cy = rcPower.Y + rcPower.Height / 2f + 1f, rad = rcPower.Width * 0.40f;
            using (Pen p = new Pen(Color.White, Math.Max(2f, 2 * S)))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                g.DrawArc(p, cx - rad, cy - rad, 2 * rad, 2 * rad, 305f, 290f);
                g.DrawLine(p, cx, cy - rad * 1.15f, cx, cy - rad * 0.1f);
            }
            // search icon
            Icons.Search(g, rcSearch.X, rcSearch.Y, rcSearch.Width, Color.White);
        }

        void DrawDefaultAvatar(Graphics g, Rectangle r)
        {
            using (Brush b = new SolidBrush(Color.FromArgb(74, 74, 74))) g.FillRectangle(b, r);
            GraphicsState st = g.Save();
            g.SetClip(r, CombineMode.Intersect);
            using (Brush w = new SolidBrush(Color.White))
            {
                float hr = r.Width * 0.17f;
                g.FillEllipse(w, r.X + r.Width / 2f - hr, r.Y + r.Height * 0.32f - hr, 2 * hr, 2 * hr);
                g.FillEllipse(w, r.X + r.Width * 0.20f, r.Y + r.Height * 0.58f, r.Width * 0.60f, r.Height * 0.80f);
            }
            g.Restore(st);
        }

        void DrawToggle(Graphics g)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle t = rcToggle;
            float cx = t.X + t.Width / 2f, cy = t.Y + t.Height / 2f, a = 6f * S;
            float dir = allMode ? -1f : 1f;      // down arrow on Start, up arrow on Apps
            using (Pen p = new Pen(Color.FromArgb(215, 255, 255, 255), Math.Max(1.5f, 2 * S)))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                g.DrawEllipse(p, t);
                g.DrawLine(p, cx, cy - a * dir, cx, cy + a * dir);
                g.DrawLine(p, cx - a * 0.8f, cy + a * 0.2f * dir, cx, cy + a * dir);
                g.DrawLine(p, cx + a * 0.8f, cy + a * 0.2f * dir, cx, cy + a * dir);
            }
            g.SmoothingMode = old;
        }

        void DrawScrollbar(Graphics g)
        {
            rcThumb = Rectangle.Empty;
            if (maxScroll <= 0) return;
            int tw = (int)Math.Max(60 * S, (double)W * W / contentW);
            int tx = (int)Math.Round(scrollX / maxScroll * (W - tw));
            rcThumb = new Rectangle(tx, H - (int)Math.Round(14 * S), tw, (int)Math.Round(6 * S));
            using (Brush b = new SolidBrush(Color.FromArgb(thumbDrag ? 120 : 70, 255, 255, 255)))
                g.FillRectangle(b, rcThumb);
        }

        void DrawSelected(Graphics g, Rectangle r)
        {
            using (Pen p = new Pen(Color.White, Math.Max(2f, 2 * S)))
            {
                p.Alignment = PenAlignment.Inset;
                g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
            }
            int bs = (int)Math.Round(22 * S);
            Rectangle bd = new Rectangle(r.Right - bs, r.Y, bs, bs);
            using (Brush b = new SolidBrush(accent)) g.FillRectangle(b, bd);
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen cp = new Pen(Color.White, Math.Max(2f, 2 * S)))
            {
                g.DrawLines(cp, new PointF[]
                {
                    new PointF(bd.X + bs * 0.24f, bd.Y + bs * 0.52f),
                    new PointF(bd.X + bs * 0.43f, bd.Y + bs * 0.72f),
                    new PointF(bd.X + bs * 0.78f, bd.Y + bs * 0.30f)
                });
            }
            g.SmoothingMode = old;
        }

        void DrawTile(Graphics g, Cell c, Rectangle r, bool pressed)
        {
            AppItem a = c.App;
            int kind = c.Pin != null ? c.Pin.Size : 1;
            if (pressed) r.Inflate(-(int)Math.Round(3 * S), -(int)Math.Round(3 * S));
            using (Brush b = new SolidBrush(a.Tint)) g.FillRectangle(b, r);

            if (a.Special && wall != null) DrawCover(g, wall, r);
            else
            {
                int isz = (int)Math.Round((kind == 0 ? 30 : (kind == 3 ? 84 : 52)) * S);
                int dy = kind == 0 ? 0 : (int)Math.Round(8 * S);
                if (a.Icon != null)
                    g.DrawImage(a.Icon, new Rectangle(r.X + (r.Width - isz) / 2, r.Y + (r.Height - isz) / 2 - dy, isz, isz));
                else
                    TextRenderer.DrawText(g, a.Letter, fLetter, new Rectangle(r.X, r.Y - dy, r.Width, r.Height), Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            if (kind >= 1)
            {
                Rectangle lr = new Rectangle(r.X + (int)Math.Round(10 * S), r.Bottom - (int)Math.Round(23 * S), r.Width - (int)Math.Round(18 * S), (int)Math.Round(18 * S));
                TextRenderer.DrawText(g, a.Name, fTile, lr, Color.White,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            if (c == hover && !pressed && selected != c)
            {
                using (Pen p = new Pen(Color.FromArgb(120, 255, 255, 255), Math.Max(2f, 2 * S)))
                {
                    p.Alignment = PenAlignment.Inset;
                    g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                }
            }
            if (c == selected) DrawSelected(g, r);
        }

        void DrawListItem(Graphics g, Cell c, Rectangle r)
        {
            bool hov = (c == hover);
            if (hov || c == selected)
                using (Brush hb = new SolidBrush(Color.FromArgb(hov ? 46 : 30, 255, 255, 255))) g.FillRectangle(hb, r);

            int ts = r.Height - (int)Math.Round(8 * S);
            Rectangle tr = new Rectangle(r.X + (int)Math.Round(4 * S), r.Y + (r.Height - ts) / 2, ts, ts);
            using (Brush tb = new SolidBrush(c.App.Tint)) g.FillRectangle(tb, tr);
            int isz = (int)Math.Round(30 * S);
            if (c.App.Icon != null)
                g.DrawImage(c.App.Icon, new Rectangle(tr.X + (ts - isz) / 2, tr.Y + (ts - isz) / 2, isz, isz));
            else
                TextRenderer.DrawText(g, c.App.Letter, fMenu, tr, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            int tx = tr.Right + (int)Math.Round(12 * S);
            TextRenderer.DrawText(g, c.App.Name, fItem, new Rectangle(tx, r.Y, r.Right - tx - (int)Math.Round(6 * S), r.Height), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            if (c == selected) DrawSelected(g, r);
        }

        // ---------------------------------------------------------------- app bar ---
        int BarSlide() { return (int)Math.Round(barH * (1.0 - barT)); }
        bool InBar(Point p) { return barT > 0.02 && p.Y >= H - (int)Math.Round(barH * barT); }
        Rectangle BarRect(BarBtn b) { return new Rectangle(b.R.X, b.R.Y + BarSlide(), b.R.Width, b.R.Height); }

        int BarHit(Point p)
        {
            for (int i = 0; i < bars.Count; i++)
                if (BarRect(bars[i]).Contains(p)) return i;
            return -1;
        }

        void AddBar(string text, int kind, ref int x, int w, int y, Action run)
        {
            BarBtn b = new BarBtn();
            b.Text = text;
            b.Kind = kind;
            b.R = new Rectangle(x, y, w, barH);
            b.Run = run;
            bars.Add(b);
            x += w + (int)Math.Round(6 * S);
        }

        void BuildBar()
        {
            bars.Clear();
            if (selected == null) return;
            int x = (int)Math.Round(46 * S), w = (int)Math.Round(124 * S), y = H - barH;
            string id = selected.App.Id;
            if (!allMode)
            {
                Pin pin = selected.Pin;
                AddBar("Unpin from Start", 0, ref x, w, y, delegate
                {
                    PinStore.Unpin(id);
                    PinStore.Save();
                    selected = null;
                    barTarget = 0;
                    StartAnim();
                    Relayout();
                    Invalidate();
                });
                if (pin.Size < 3)
                    AddBar("Larger", 1, ref x, w, y, delegate { pin.Size++; PinStore.Save(); Relayout(); Invalidate(); });
                if (pin.Size > 0)
                    AddBar("Smaller", 2, ref x, w, y, delegate { pin.Size--; PinStore.Save(); Relayout(); Invalidate(); });
            }
            else if (PinStore.IsPinned(id))
            {
                AddBar("Unpin from Start", 0, ref x, w, y, delegate
                {
                    PinStore.Unpin(id);
                    PinStore.Save();
                    Deselect();
                });
            }
            else
            {
                AddBar("Pin to Start", 3, ref x, w, y, delegate
                {
                    PinStore.Add(id);
                    PinStore.Save();
                    Deselect();
                });
            }
        }

        void SelectCell(Cell c)
        {
            selected = c;
            BuildBar();
            barTarget = 1;
            StartAnim();
            Invalidate();
        }

        void Deselect()
        {
            if (selected == null && barTarget == 0) return;
            selected = null;
            barTarget = 0;
            StartAnim();
            Invalidate();
        }

        void DrawGlyph(Graphics g, int kind, Rectangle ci)
        {
            float cx = ci.X + ci.Width / 2f, cy = ci.Y + ci.Height / 2f, u = S;
            using (Pen p = new Pen(Color.White, Math.Max(2f, 2 * S)))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                if (kind == 0 || kind == 3)
                {
                    g.DrawLine(p, cx - 7 * u, cy + 7 * u, cx + 1 * u, cy - 1 * u);                  // needle
                    g.DrawLine(p, cx - 2 * u, cy - 7 * u, cx + 7 * u, cy + 2 * u);                  // head
                    if (kind == 0) g.DrawLine(p, cx - 9 * u, cy - 9 * u, cx + 9 * u, cy + 9 * u);   // unpin slash
                }
                else if (kind == 1)       // larger: arrow pointing out to the top-right
                {
                    g.DrawLine(p, cx - 7 * u, cy + 7 * u, cx + 7 * u, cy - 7 * u);
                    g.DrawLine(p, cx + 7 * u, cy - 7 * u, cx + 7 * u, cy - 1 * u);
                    g.DrawLine(p, cx + 7 * u, cy - 7 * u, cx + 1 * u, cy - 7 * u);
                }
                else                       // smaller: arrow pointing in to the bottom-left
                {
                    g.DrawLine(p, cx + 7 * u, cy - 7 * u, cx - 7 * u, cy + 7 * u);
                    g.DrawLine(p, cx - 7 * u, cy + 7 * u, cx - 7 * u, cy + 1 * u);
                    g.DrawLine(p, cx - 7 * u, cy + 7 * u, cx - 1 * u, cy + 7 * u);
                }
            }
        }

        void DrawBar(Graphics g)
        {
            if (barT <= 0.01) return;
            int by = H - barH + BarSlide();
            using (Brush b = new SolidBrush(SCfg.BarBack)) g.FillRectangle(b, 0, by, W, barH);
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < bars.Count; i++)
            {
                Rectangle r = BarRect(bars[i]);
                int d = (int)Math.Round(40 * S);
                Rectangle ci = new Rectangle(r.X + (r.Width - d) / 2, r.Y + (int)Math.Round(24 * S), d, d);
                if (i == barHover)
                    using (Brush hb = new SolidBrush(Color.FromArgb(48, 255, 255, 255))) g.FillEllipse(hb, ci);
                using (Pen p = new Pen(Color.White, Math.Max(2f, 2 * S))) g.DrawEllipse(p, ci);
                DrawGlyph(g, bars[i].Kind, ci);
                TextRenderer.DrawText(g, bars[i].Text, fBar,
                    new Rectangle(r.X, ci.Bottom + (int)Math.Round(7 * S), r.Width, (int)Math.Round(18 * S)), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            g.SmoothingMode = old;
        }

        // --------------------------------------------------------------- popup menu ---
        void ShowMenu(List<PopItem> items, int right, int top)
        {
            int tw = 0;
            for (int i = 0; i < items.Count; i++)
                tw = Math.Max(tw, TextRenderer.MeasureText(items[i].Text, fMenu, new Size(2000, 200), TF).Width);
            menuItemH = (int)Math.Round(40 * S);
            int w = Math.Max((int)Math.Round(122 * S), tw + (int)Math.Round(44 * S));
            int h = items.Count * menuItemH + (int)Math.Round(4 * S);
            int x = Math.Max(4, Math.Min(right, W - 4) - w);
            rcMenu = new Rectangle(x, top, w, h);
            menu = items;
            menuHover = -1;
            Invalidate();
        }

        void CloseMenu()
        {
            if (menu == null) return;
            menu = null;
            Invalidate();
        }

        int MenuHit(Point p)
        {
            if (menu == null || !rcMenu.Contains(p)) return -1;
            int i = (p.Y - rcMenu.Y - (int)Math.Round(2 * S)) / menuItemH;
            return (i >= 0 && i < menu.Count) ? i : -1;
        }

        void DrawMenu(Graphics g)
        {
            if (menu == null) return;
            g.FillRectangle(Brushes.White, rcMenu);
            int b = (int)Math.Round(2 * S);
            for (int i = 0; i < menu.Count; i++)
            {
                Rectangle ir = new Rectangle(rcMenu.X + b, rcMenu.Y + b + i * menuItemH, rcMenu.Width - 2 * b, menuItemH);
                if (i == menuHover && menu[i].Enabled)
                    using (Brush hb = new SolidBrush(Color.FromArgb(222, 222, 222))) g.FillRectangle(hb, ir);
                TextRenderer.DrawText(g, menu[i].Text, fMenu,
                    new Rectangle(ir.X + (int)Math.Round(18 * S), ir.Y, ir.Width - (int)Math.Round(18 * S), ir.Height),
                    menu[i].Enabled ? Color.Black : Color.FromArgb(150, 150, 150),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            using (Pen p = new Pen(Color.FromArgb(10, 10, 20), Math.Max(2f, 2 * S)))
            {
                p.Alignment = PenAlignment.Inset;
                g.DrawRectangle(p, rcMenu.X, rcMenu.Y, rcMenu.Width - 1, rcMenu.Height - 1);
            }
        }

        void OpenPowerMenu()
        {
            List<PopItem> m = new List<PopItem>();
            m.Add(new PopItem("Sleep", true, delegate { Dismiss(); PowerOps.Sleep(); }));
            m.Add(new PopItem("Hibernate", PowerOps.HibernateEnabled(), delegate { Dismiss(); PowerOps.Hibernate(); }));
            m.Add(new PopItem("Shut down", true, delegate { Dismiss(); PowerOps.ShutDown(); }));
            m.Add(new PopItem("Restart", true, delegate { Dismiss(); PowerOps.Restart(); }));
            m.Add(new PopItem("Sign out", true, delegate { Dismiss(); PowerOps.SignOut(); }));
            ShowMenu(m, rcSearch.Right + (int)Math.Round(4 * S), rcPower.Bottom + (int)Math.Round(20 * S));
        }

        void OpenUserMenu()
        {
            List<PopItem> m = new List<PopItem>();
            m.Add(new PopItem("Change account picture", true, delegate { Dismiss(); Actions.Launch("ms-settings:yourinfo"); }));
            m.Add(new PopItem("Lock", true, delegate { Dismiss(); PowerOps.Lock(); }));
            m.Add(new PopItem("Sign out", true, delegate { Dismiss(); PowerOps.SignOut(); }));
            ShowMenu(m, rcAvatar.Right + (int)Math.Round(120 * S), rcAvatar.Bottom + (int)Math.Round(14 * S));
        }

        // ------------------------------------------------------------------ input ---
        int HeaderHit(Point p)
        {
            int s = (int)Math.Round(8 * S);
            if (Rectangle.Inflate(rcSearch, s, s).Contains(p)) return HDR_SEARCH;
            if (Rectangle.Inflate(rcPower, s, s).Contains(p)) return HDR_POWER;
            if (rcAvatar.Contains(p)) return HDR_AVATAR;
            if (rcToggle.Contains(p)) return HDR_TOGGLE;
            return HDR_NONE;
        }

        void OnHeader(int kind)
        {
            switch (kind)
            {
                case HDR_SEARCH: Dismiss(); Actions.Run(0); break;
                case HDR_POWER: OpenPowerMenu(); break;
                case HDR_AVATAR: OpenUserMenu(); break;
                case HDR_TOGGLE: SetMode(!allMode); break;
            }
        }

        void SetMode(bool apps)
        {
            allMode = apps;
            selected = null;
            barTarget = 0;
            scrollX = 0;
            scrollT = 0;
            Relayout();
            Invalidate();
            StartAnim();
        }

        void LaunchCell(Cell c)
        {
            AppItem a = c.App;
            Dismiss();
            if (a.Special) PowerOps.ShowDesktop();
            else Actions.Launch("shell:AppsFolder\\" + a.Id);
        }

        void SetThumb(int mx)
        {
            if (maxScroll <= 0) return;
            int tw = Math.Max(1, rcThumb.Width);
            double f = (double)(mx - thumbGrab) / Math.Max(1, W - tw);
            scrollT = scrollX = Math.Max(0, Math.Min(maxScroll, f * maxScroll));
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Point p = e.Location;
            if (menu != null) { if (!rcMenu.Contains(p)) CloseMenu(); return; }

            if (e.Button == MouseButtons.Right)
            {
                if (InBar(p)) return;
                Cell rc = HitCell(p);
                if (rc != null) SelectCell(rc); else Deselect();
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            if (InBar(p)) { barPress = BarHit(p); return; }
            hdrPress = HeaderHit(p);
            if (hdrPress != HDR_NONE) return;
            if (maxScroll > 0 && p.Y >= H - (int)Math.Round(24 * S))
            {
                thumbDrag = true;
                thumbGrab = rcThumb.Contains(p) ? p.X - rcThumb.X : rcThumb.Width / 2;
                SetThumb(p.X);
                return;
            }
            pressCell = HitCell(p);
            pressPt = p;
            dragging = false;
            if (pressCell == null) { panning = true; panX = p.X; panScroll = scrollX; }
            else InvalidateCell(pressCell);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point p = e.Location;
            if (thumbDrag) { SetThumb(p.X); return; }
            if (panning)
            {
                scrollT = scrollX = Math.Max(0, Math.Min(maxScroll, panScroll - (p.X - panX)));
                Invalidate();
                return;
            }
            if (pressCell != null && !allMode)
            {
                if (!dragging)
                {
                    int dx = p.X - pressPt.X, dy = p.Y - pressPt.Y, th = (int)Math.Round(8 * S);
                    if (dx * dx + dy * dy > th * th)
                    {
                        dragging = true;
                        dragOff = new Point(pressPt.X + (int)Math.Round(scrollX) - pressCell.R.X, pressPt.Y - pressCell.R.Y);
                        if (selected != null) Deselect();
                    }
                }
                if (dragging)
                {
                    dragPt = p;
                    Cell t = HitCell(p);
                    dropTarget = (t == pressCell) ? null : t;
                    Invalidate();
                    return;
                }
            }
            if (menu != null)
            {
                int mh = MenuHit(p);
                if (mh != menuHover) { menuHover = mh; Invalidate(rcMenu); }
                return;
            }
            int bh = InBar(p) ? BarHit(p) : -1;
            if (bh != barHover) { barHover = bh; Invalidate(new Rectangle(0, H - barH, W, barH)); }
            Cell c = InBar(p) ? null : HitCell(p);
            if (c != hover) { InvalidateCell(hover); hover = c; InvalidateCell(c); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != null) { InvalidateCell(hover); hover = null; }
            if (barHover != -1) { barHover = -1; Invalidate(new Rectangle(0, H - barH, W, barH)); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Point p = e.Location;
            if (menu != null)
            {
                if (e.Button == MouseButtons.Left)
                {
                    int mi = MenuHit(p);
                    if (mi >= 0 && menu[mi].Enabled)
                    {
                        Action run = menu[mi].Run;
                        CloseMenu();
                        if (run != null) run();
                    }
                }
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            if (thumbDrag) { thumbDrag = false; Invalidate(); return; }
            if (barPress >= 0)
            {
                int bp = barPress, b = BarHit(p);
                barPress = -1;
                if (b == bp && b >= 0 && bars[b].Run != null) bars[b].Run();
                return;
            }
            if (hdrPress != HDR_NONE)
            {
                int hp = hdrPress, h = HeaderHit(p);
                hdrPress = HDR_NONE;
                if (h == hp) OnHeader(hp);
                return;
            }
            if (panning)
            {
                bool moved = Math.Abs(p.X - panX) > (int)Math.Round(4 * S);
                panning = false;
                if (!moved) Deselect();
                return;
            }
            if (pressCell != null)
            {
                Cell pc = pressCell;
                pressCell = null;
                if (dragging)
                {
                    dragging = false;
                    Cell t = dropTarget;
                    dropTarget = null;
                    if (t != null && !allMode && pc.Pin != null && t.Pin != null)
                    {
                        PinStore.Move(pc.Pin, t.Pin);
                        PinStore.Save();
                        Relayout();
                    }
                    Invalidate();
                    return;
                }
                if (HitCell(p) == pc)
                {
                    if (selected != null) { if (selected == pc) Deselect(); else SelectCell(pc); }
                    else LaunchCell(pc);
                }
                else InvalidateCell(pc);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (menu != null) return;
            ScrollBy(-e.Delta / 120.0 * 256 * S);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (shown && !closing)
            {
                switch (keyData)
                {
                    case Keys.Escape:
                        if (menu != null) CloseMenu();
                        else if (selected != null) Deselect();
                        else if (allMode) SetMode(false);
                        else Dismiss();
                        return true;
                    case Keys.Right: ScrollBy(256 * S); return true;
                    case Keys.Left: ScrollBy(-256 * S); return true;
                    case Keys.PageDown: ScrollBy(W * 0.8); return true;
                    case Keys.PageUp: ScrollBy(-W * 0.8); return true;
                    case Keys.Home: ScrollBy(-maxScroll); return true;
                    case Keys.End: ScrollBy(maxScroll); return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
