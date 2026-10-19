// =====================================================================================
//  Windows 8 Charms Bar for Windows 10 IoT Enterprise LTSC 2021 (build 19044)
//
//  - 100% offline, no dependencies, single .exe (built with the csc.exe that ships in Windows)
//  - Written in C# 5 on purpose so the in-box compiler can build it
//  - Zero CPU while idle: no polling. Everything is event driven:
//        * low-level mouse hook  -> hot corners (top-right / bottom-right)
//        * RegisterHotKey        -> Win+C and Ctrl+Alt+C (Esc only while the bar is open)
//        * timers only run while the bar is on screen (animation / clock)
//  - Smooth slide animation: Stopwatch based easing, synced to the DWM refresh (DwmFlush)
//  - Start icon uses your Windows accent colour (Settings > Personalization > Colors)
//
//  Everything you might want to tweak is in the Cfg and Actions classes.
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Win8Charms
{
    // ------------------------------------------------------------------ settings ---
    static class Cfg
    {
        public const int HotCornerPx = 8;          // size of the hot corner zone (px @100% DPI)
        public const int HotCornerDelayMs = 120;   // how long the pointer must rest in the corner
        public const int LeavePx = 40;             // mouse-opened bar closes when pointer is this far left of it
        public const int LeaveDelayMs = 600;       // ... for this long
        public const int ShowMs = 220;             // slide-in duration
        public const int HideMs = 140;             // slide-out duration
        public const int BarWidth = 86;            // px @100% DPI
        public const int ItemHeight = 92;          // px @100% DPI

        public static readonly string[] Labels = new string[] { "Search", "Share", "Start", "Devices", "Settings" };

        public static readonly Color BarBack = Color.FromArgb(17, 17, 17);
        public static readonly Color HoverBack = Color.FromArgb(48, 48, 48);
        public static readonly Color LabelIdle = Color.FromArgb(160, 160, 160);
    }

    // ----------------------------------------------- what each charm does (edit me) ---
    static class Actions
    {
        const byte VK_LWIN = 0x5B, VK_MENU = 0x12, VK_S = 0x53, VK_D = 0x44;

        public static void Run(int index)
        {
            switch (index)
            {
                case 0: Tap(VK_LWIN, VK_S); break;                         // Search  -> Windows Search (Win+S)
                case 1: Launch("ms-settings:crossdevice"); break;          // Share   -> Shared experiences / Nearby sharing
                case 2: Tap(VK_LWIN); break;                               // (fallback only - the Start charm opens the Metro Start screen, see CharmsApp.OnItemClicked)
                case 3: Launch("ms-settings:connecteddevices"); break;     // Devices -> Bluetooth & other devices
                case 4: Launch("ms-settings:"); break;                     // Settings-> Windows Settings
            }
        }

        // clicking the clock/date panel opens the Windows calendar flyout (Win+Alt+D)
        public static void Calendar() { Tap(VK_LWIN, VK_MENU, VK_D); }

        public static void Launch(string uri)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(uri);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }

        static void Tap(params byte[] keys)
        {
            for (int i = 0; i < keys.Length; i++) Key(keys[i], false);
            for (int i = keys.Length - 1; i >= 0; i--) Key(keys[i], true);
        }

        static void Key(byte vk, bool up)
        {
            uint flags = (vk == VK_LWIN ? 1u : 0u) | (up ? 2u : 0u);
            Native.keybd_event(vk, 0, flags, UIntPtr.Zero);
        }
    }

    // ---------------------------------------------------------------------- win32 ---
    static class Native
    {
        public const int WH_MOUSE_LL = 14;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_MBUTTONDOWN = 0x0207;
        public const int WM_XBUTTONDOWN = 0x020B;
        public const int WM_HOTKEY = 0x0312;
        public const int WM_MOUSEACTIVATE = 0x0021;

        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
        [DllImport("dwmapi.dll")]
        public static extern int DwmFlush();
        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint uPeriod);
        [DllImport("kernel32.dll")]
        public static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

        public static void MoveTo(IntPtr h, int x, int y)
        {
            SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        public static void MakeTopmost(IntPtr h)
        {
            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        static bool flushOk = true;
        public static void Flush()
        {
            if (!flushOk) return;
            try { DwmFlush(); } catch { flushOk = false; }
        }
    }

    // ------------------------------------------------------------- system queries ---
    static class Sys
    {
        // The accent colour picked in Settings > Personalization > Colors.
        public static Color Accent()
        {
            Color c = Color.FromArgb(0, 120, 215);
            try
            {
                object v = Registry.GetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Accent",
                    "AccentColorMenu", null);                       // stored as 0xAABBGGRR
                if (v is int)
                {
                    uint abgr = unchecked((uint)(int)v);
                    c = Color.FromArgb((int)(abgr & 0xFF), (int)((abgr >> 8) & 0xFF), (int)((abgr >> 16) & 0xFF));
                }
                else
                {
                    object w = Registry.GetValue(
                        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\DWM", "ColorizationColor", null); // 0xAARRGGBB
                    if (w is int)
                    {
                        uint argb = unchecked((uint)(int)w);
                        c = Color.FromArgb((int)((argb >> 16) & 0xFF), (int)((argb >> 8) & 0xFF), (int)(argb & 0xFF));
                    }
                }
            }
            catch { }
            return c;
        }

        // keeps very dark accent colours visible on the dark bar
        public static Color Visible(Color c)
        {
            double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            if (lum >= 70) return c;
            double k = 0.45;
            return Color.FromArgb((int)(c.R + (255 - c.R) * k), (int)(c.G + (255 - c.G) * k), (int)(c.B + (255 - c.B) * k));
        }

        // 0 = no network, 1 = Wi-Fi, 2 = wired
        public static int NetState()
        {
            try
            {
                bool wifi = false, wired = false;
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    NetworkInterfaceType t = ni.NetworkInterfaceType;
                    if (t == NetworkInterfaceType.Loopback || t == NetworkInterfaceType.Tunnel) continue;
                    string d = ni.Description;
                    if (d.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        d.IndexOf("VMware", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        d.IndexOf("Hyper-V", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        d.IndexOf("Pseudo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        d.IndexOf("TAP-", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (ni.GetIPProperties().GatewayAddresses.Count == 0) continue;
                    if (t == NetworkInterfaceType.Wireless80211) wifi = true; else wired = true;
                }
                return wired ? 2 : (wifi ? 1 : 0);
            }
            catch { return 0; }
        }
    }

    // ------------------------------------------------------------ vector icons ---
    static class Icons
    {
        static Pen RoundPen(Color c, float w)
        {
            Pen p = new Pen(c, w);
            p.StartCap = LineCap.Round;
            p.EndCap = LineCap.Round;
            p.LineJoin = LineJoin.Round;
            return p;
        }

        public static void Search(Graphics g, float x, float y, float s, Color c)
        {
            using (Pen p = RoundPen(c, Math.Max(2f, s * 0.075f)))
            {
                float r = s * 0.28f, cx = x + s * 0.60f, cy = y + s * 0.38f, k = r * 0.7071f;
                g.DrawEllipse(p, cx - r, cy - r, 2 * r, 2 * r);
                g.DrawLine(p, cx - k, cy + k, x + s * 0.10f, y + s * 0.90f);
            }
        }

        // three nodes on a ring (the Windows 8 "Share" glyph)
        public static void Share(Graphics g, float x, float y, float s, Color c, Color bg)
        {
            float cx = x + s / 2f, cy = y + s / 2f, R = s * 0.36f, nr = s * 0.13f, gr = nr + s * 0.07f;
            using (Pen p = RoundPen(c, Math.Max(2f, s * 0.07f)))
                g.DrawEllipse(p, cx - R, cy - R, 2 * R, 2 * R);
            double[] ang = new double[] { -90, 30, 150 };
            using (Brush gap = new SolidBrush(bg))
            using (Brush fill = new SolidBrush(c))
            {
                for (int i = 0; i < ang.Length; i++)
                {
                    double a = ang[i] * Math.PI / 180.0;
                    float px = cx + (float)(R * Math.Cos(a)), py = cy + (float)(R * Math.Sin(a));
                    g.FillEllipse(gap, px - gr, py - gr, 2 * gr, 2 * gr);
                    g.FillEllipse(fill, px - nr, py - nr, 2 * nr, 2 * nr);
                }
            }
        }

        static readonly float[][] Panes = new float[][]
        {
            new float[] { 0.04f, 0.20f, 0.46f, 0.14f, 0.46f, 0.47f, 0.04f, 0.47f },
            new float[] { 0.04f, 0.53f, 0.46f, 0.53f, 0.46f, 0.86f, 0.04f, 0.80f },
            new float[] { 0.52f, 0.13f, 0.96f, 0.06f, 0.96f, 0.47f, 0.52f, 0.47f },
            new float[] { 0.52f, 0.53f, 0.96f, 0.53f, 0.96f, 0.94f, 0.52f, 0.87f }
        };

        // Windows 8 style 4-pane logo
        public static void Start(Graphics g, float x, float y, float s, Color c)
        {
            using (Brush b = new SolidBrush(c))
            {
                for (int i = 0; i < Panes.Length; i++)
                {
                    float[] q = Panes[i];
                    PointF[] pts = new PointF[4];
                    for (int k = 0; k < 4; k++) pts[k] = new PointF(x + q[k * 2] * s, y + q[k * 2 + 1] * s);
                    g.FillPolygon(b, pts);
                }
            }
        }

        public static void Devices(Graphics g, float x, float y, float s, Color c)
        {
            using (Pen p = new Pen(c, Math.Max(2f, s * 0.075f)))
            using (Brush b = new SolidBrush(c))
            {
                g.DrawRectangle(p, x + s * 0.36f, y + s * 0.16f, s * 0.54f, s * 0.68f);
                g.FillRectangle(b, x + s * 0.06f, y + s * 0.42f, s * 0.32f, s * 0.16f);
                g.FillRectangle(b, x + s * 0.50f, y + s * 0.34f, s * 0.26f, s * 0.32f);
            }
        }

        public static void Gear(Graphics g, float x, float y, float s, Color c, Color bg)
        {
            float cx = x + s / 2f, cy = y + s / 2f, R = s * 0.48f, r = s * 0.35f;
            int teeth = 8;
            double[] off = new double[] { -0.30, -0.17, 0.17, 0.30 };
            float[] rad = new float[] { r, R, R, r };
            List<PointF> pts = new List<PointF>();
            for (int i = 0; i < teeth; i++)
            {
                double a = i * 2.0 * Math.PI / teeth;
                for (int k = 0; k < 4; k++)
                    pts.Add(new PointF(cx + (float)(rad[k] * Math.Cos(a + off[k])), cy + (float)(rad[k] * Math.Sin(a + off[k]))));
            }
            using (Brush b = new SolidBrush(c)) g.FillPolygon(b, pts.ToArray());
            float h = s * 0.17f;
            using (Brush hb = new SolidBrush(bg)) g.FillEllipse(hb, cx - h, cy - h, 2 * h, 2 * h);
        }

        // state: 0 none, 1 wifi, 2 wired
        public static void Network(Graphics g, float x, float y, float w, int state)
        {
            Color c = state == 0 ? Color.FromArgb(120, 120, 120) : Color.White;
            if (state == 2)
            {
                using (Pen p = new Pen(c, Math.Max(1.5f, w * 0.09f)))
                using (Brush b = new SolidBrush(c))
                {
                    g.FillRectangle(b, x + w * 0.34f, y + w * 0.04f, w * 0.32f, w * 0.26f);
                    g.DrawLine(p, x + w * 0.5f, y + w * 0.30f, x + w * 0.5f, y + w * 0.52f);
                    g.DrawLine(p, x + w * 0.16f, y + w * 0.52f, x + w * 0.84f, y + w * 0.52f);
                    g.DrawLine(p, x + w * 0.16f, y + w * 0.52f, x + w * 0.16f, y + w * 0.68f);
                    g.DrawLine(p, x + w * 0.84f, y + w * 0.52f, x + w * 0.84f, y + w * 0.68f);
                    g.FillRectangle(b, x + w * 0.04f, y + w * 0.68f, w * 0.26f, w * 0.24f);
                    g.FillRectangle(b, x + w * 0.70f, y + w * 0.68f, w * 0.26f, w * 0.24f);
                }
                return;
            }
            float px = x + w / 2f, py = y + w * 0.88f;
            using (Pen p = RoundPen(c, Math.Max(1.5f, w * 0.10f)))
            using (Brush b = new SolidBrush(c))
            {
                float[] radii = new float[] { w * 0.26f, w * 0.48f, w * 0.70f };
                for (int i = 0; i < radii.Length; i++)
                    g.DrawArc(p, px - radii[i], py - radii[i], 2 * radii[i], 2 * radii[i], 225f, 90f);
                float d = w * 0.08f;
                g.FillEllipse(b, px - d, py - d, 2 * d, 2 * d);
            }
            if (state == 0)
            {
                using (Pen xp = RoundPen(Color.FromArgb(240, 90, 90), Math.Max(1.5f, w * 0.10f)))
                {
                    g.DrawLine(xp, x + w * 0.66f, y + w * 0.50f, x + w * 0.96f, y + w * 0.80f);
                    g.DrawLine(xp, x + w * 0.96f, y + w * 0.50f, x + w * 0.66f, y + w * 0.80f);
                }
            }
        }

        public static void Battery(Graphics g, float x, float y, float w, float h, int pct, bool charging)
        {
            float pw = Math.Max(1f, h * 0.10f), nub = w * 0.09f, bw = w - nub;
            Color body = Color.White;
            using (Pen p = new Pen(body, pw))
            using (Brush wb = new SolidBrush(body))
            {
                g.DrawRectangle(p, x, y, bw, h);
                g.FillRectangle(wb, x + bw, y + h * 0.30f, nub, h * 0.40f);
                float pad = pw + Math.Max(1f, h * 0.08f);
                float fw = (bw - 2 * pad) * Math.Max(0, Math.Min(100, pct)) / 100f;
                Color fc = (pct <= 20 && !charging) ? Color.FromArgb(232, 17, 35) : Color.White;
                using (Brush fb = new SolidBrush(fc))
                    g.FillRectangle(fb, x + pad, y + pad, fw, h - 2 * pad);
            }
            if (charging)
            {
                float[] f = new float[] { 0.58f, 0.04f, 0.32f, 0.56f, 0.50f, 0.56f, 0.40f, 0.96f, 0.70f, 0.42f, 0.52f, 0.42f };
                PointF[] pts = new PointF[6];
                for (int i = 0; i < 6; i++) pts[i] = new PointF(x + bw * f[i * 2], y + h * f[i * 2 + 1]);
                using (Pen halo = new Pen(Cfg.BarBack, Math.Max(1.5f, h * 0.2f)))
                using (Brush wb = new SolidBrush(Color.White))
                {
                    halo.LineJoin = LineJoin.Round;
                    g.DrawPolygon(halo, pts);
                    g.FillPolygon(wb, pts);
                }
            }
        }
    }

    // --------------------------------------------------------------- base window ---
    // borderless, topmost, never steals focus, not in Alt+Tab
    class FlyForm : Form
    {
        public float UiScale = 1f;

        public FlyForm(float scale)
        {
            UiScale = scale;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Cfg.BarBack;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)3; return; }   // MA_NOACTIVATE
            base.WndProc(ref m);
        }

        public Font PxFont(string family, float px, FontStyle style)
        {
            return new Font(family, px * UiScale, style, GraphicsUnit.Pixel);
        }
    }

    // ------------------------------------------------------------ the charms bar ---
    class BarForm : FlyForm
    {
        public int Hover = -1;
        public int Pressed = -1;
        public Color Accent = Color.FromArgb(0, 120, 215);
        public event Action<int> ItemClicked;
        public event Action<int> HotKeyPressed;
        readonly Font labelFont;

        public BarForm(float scale) : base(scale)
        {
            labelFont = PxFont("Segoe UI", 12f, FontStyle.Regular);
        }

        Rectangle ItemRect(int i)
        {
            int ih = (int)(Cfg.ItemHeight * UiScale);
            int top = Math.Max(0, (Height - ih * 5) / 2);
            return new Rectangle(0, top + i * ih, Width, ih);
        }

        int HitTest(Point p)
        {
            for (int i = 0; i < 5; i++) if (ItemRect(i).Contains(p)) return i;
            return -1;
        }

        void InvalidateItem(int i) { if (i >= 0) Invalidate(ItemRect(i)); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = HitTest(e.Location);
            if (i != Hover) { int old = Hover; Hover = i; InvalidateItem(old); InvalidateItem(i); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            int old = Hover; Hover = -1; InvalidateItem(old);
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Pressed = (e.Button == MouseButtons.Left) ? HitTest(e.Location) : -1;
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int p = Pressed; Pressed = -1;
            if (e.Button == MouseButtons.Left && p >= 0 && p == HitTest(e.Location))
            {
                Action<int> h = ItemClicked;
                if (h != null) h(p);
            }
            base.OnMouseUp(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                Action<int> h = HotKeyPressed;
                if (h != null) h(m.WParam.ToInt32());
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Cfg.BarBack);
            using (Pen edge = new Pen(Color.FromArgb(44, 44, 44)))
                g.DrawLine(edge, 0, 0, 0, Height);

            Color accent = Sys.Visible(Accent);
            for (int i = 0; i < 5; i++)
            {
                Rectangle r = ItemRect(i);
                if (!r.IntersectsWith(e.ClipRectangle)) continue;
                bool hov = (i == Hover);
                Color bg = hov ? Cfg.HoverBack : Cfg.BarBack;
                if (hov) using (Brush hb = new SolidBrush(bg)) g.FillRectangle(hb, r.X + 1, r.Y, r.Width - 1, r.Height);

                Color fg = hov ? Color.White : Color.FromArgb(236, 236, 236);
                float s = 36f * UiScale;
                float ix = r.X + (r.Width - s) / 2f, iy = r.Y + r.Height * 0.16f;
                switch (i)
                {
                    case 0: Icons.Search(g, ix, iy, s, fg); break;
                    case 1: Icons.Share(g, ix, iy, s, fg, bg); break;
                    case 2: Icons.Start(g, ix, iy, s, accent); break;
                    case 3: Icons.Devices(g, ix, iy, s, fg); break;
                    case 4: Icons.Gear(g, ix, iy, s, fg, bg); break;
                }
                Rectangle lr = new Rectangle(r.X, (int)(iy + s + 5 * UiScale), r.Width, (int)(20 * UiScale));
                TextRenderer.DrawText(g, Cfg.Labels[i], labelFont, lr, hov ? Color.White : Cfg.LabelIdle,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }
    }

    // ---------------------------------------------- clock / date / battery / network ---
    class InfoForm : FlyForm
    {
        public string Time = "", AmPm = "", Weekday = "", Date = "";
        public int BatteryPct = -1, Net;
        public bool Charging, NoBattery;
        public event Action Clicked;

        readonly Font timeFont, ampmFont, dayFont, smallFont;
        float timeX, ampmX, dateX;
        const TextFormatFlags F = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top;

        public InfoForm(float scale) : base(scale)
        {
            timeFont = PxFont("Segoe UI Light", 54f, FontStyle.Regular);
            ampmFont = PxFont("Segoe UI", 16f, FontStyle.Regular);
            dayFont = PxFont("Segoe UI", 20f, FontStyle.Regular);
            smallFont = PxFont("Segoe UI", 13f, FontStyle.Regular);
        }

        public void Relayout()
        {
            Size big = new Size(2000, 400);
            int tw = TextRenderer.MeasureText("00:00", timeFont, big, F).Width;
            int aw = AmPm.Length > 0 ? TextRenderer.MeasureText(AmPm, ampmFont, big, F).Width : 0;
            int dw = TextRenderer.MeasureText(Weekday, dayFont, big, F).Width;
            int mw = TextRenderer.MeasureText(Date, dayFont, big, F).Width;
            timeX = 16 * UiScale;
            ampmX = timeX + tw + 2 * UiScale;
            dateX = ampmX + (aw > 0 ? aw + 4 * UiScale : 0) + 20 * UiScale;
            Width = (int)(dateX + Math.Max(dw, mw) + 26 * UiScale);
            Height = (int)(104 * UiScale);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Action h = Clicked;
                if (h != null) h();
            }
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Cfg.BarBack);
            float s = UiScale;

            // status row: network + battery
            float ix = 18 * s, iy = 12 * s;
            Icons.Network(g, ix, iy, 19 * s, Net);
            float bx = ix + 19 * s + 14 * s;
            Icons.Battery(g, bx, iy + 3 * s, 24 * s, 12 * s, NoBattery ? 100 : Math.Max(0, BatteryPct), Charging || NoBattery);
            string bt = NoBattery ? "AC" : (BatteryPct >= 0 ? BatteryPct.ToString() + "%" : "");
            if (bt.Length > 0)
                TextRenderer.DrawText(g, bt, smallFont, new Point((int)(bx + 24 * s + 8 * s), (int)(iy)), Color.White, F);

            // big clock
            TextRenderer.DrawText(g, Time, timeFont, new Point((int)timeX, (int)(22 * s)), Color.White, F);
            if (AmPm.Length > 0)
                TextRenderer.DrawText(g, AmPm, ampmFont, new Point((int)ampmX, (int)(44 * s)), Color.FromArgb(210, 210, 210), F);

            // date
            TextRenderer.DrawText(g, Weekday, dayFont, new Point((int)dateX, (int)(37 * s)), Color.White, F);
            TextRenderer.DrawText(g, Date, dayFont, new Point((int)dateX, (int)(63 * s)), Color.FromArgb(210, 210, 210), F);
        }
    }

    // ------------------------------------------------------------------ controller ---
    class CharmsApp : ApplicationContext
    {
        const int ID_WINC = 1, ID_ALTC = 2, ID_ESC = 3;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "Win8CharmsBar";

        readonly float scale;
        readonly BarForm bar;
        readonly InfoForm info;
        readonly NotifyIcon tray;
        readonly Native.HookProc hookProc;
        readonly Timer animTimer, dwellTimer, leaveTimer, infoTimer, trimTimer;
        readonly Stopwatch sw = new Stopwatch();
        readonly int cornerPx;

        IntPtr hook = IntPtr.Zero;
        Rectangle vr;
        Screen cornerScreen;
        bool open, byMouse, inCorner, periodOn, animShow, escReg;
        int tickCount;
        Rectangle barRect, infoRect;
        int offBarX, offInfoX, curBarX, curInfoX, fromBarX, fromInfoX, toBarX, toInfoX;
        double animDur;

        public CharmsApp()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            cornerPx = Math.Max(2, (int)(Cfg.HotCornerPx * scale));
            vr = SystemInformation.VirtualScreen;

            bar = new BarForm(scale);
            info = new InfoForm(scale);
            IntPtr h1 = bar.Handle;      // create the (hidden) windows now so the first show is instant
            IntPtr h2 = info.Handle;
            bar.ItemClicked += OnItemClicked;
            bar.HotKeyPressed += OnHotKey;
            info.Clicked += OnInfoClicked;

            animTimer = MakeTimer(1, OnAnimTick);
            dwellTimer = MakeTimer(Cfg.HotCornerDelayMs, OnDwell);
            leaveTimer = MakeTimer(Cfg.LeaveDelayMs, OnLeave);
            infoTimer = MakeTimer(1000, OnInfoTick);
            trimTimer = MakeTimer(4000, OnTrim);

            Native.RegisterHotKey(bar.Handle, ID_WINC, 0x8 | 0x4000, 0x43);        // Win+C   (may be taken by the shell)
            Native.RegisterHotKey(bar.Handle, ID_ALTC, 0x2 | 0x1 | 0x4000, 0x43);  // Ctrl+Alt+C

            hookProc = MouseHook;
            InstallHook();
            StartScreen.Init();     // loads tile layout + scans installed apps in the background

            Color acc = Sys.Visible(Sys.Accent());
            tray = new NotifyIcon();
            tray.Icon = MakeTrayIcon(acc);
            tray.Text = "Charms Bar  (Win+C / Ctrl+Alt+C / right-edge corners)";
            tray.ContextMenuStrip = BuildMenu();
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) OpenCharms(false, Screen.FromPoint(Cursor.Position));
            };
            tray.Visible = true;

            SystemEvents.DisplaySettingsChanged += delegate { vr = SystemInformation.VirtualScreen; };
            SystemEvents.PowerModeChanged += delegate(object s, PowerModeChangedEventArgs e)
            {
                if (e.Mode == PowerModes.Resume) InstallHook();   // Windows can drop hooks across sleep
            };

            TrimMemory();
        }

        static Timer MakeTimer(int ms, EventHandler tick)
        {
            Timer t = new Timer();
            t.Interval = ms;
            t.Tick += tick;
            return t;
        }

        // ------------------------------------------------------------- hooks ---
        void InstallHook()
        {
            try
            {
                if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
                hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, hookProc, Native.GetModuleHandle(null), 0);
            }
            catch { }
        }

        IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    int msg = wParam.ToInt32();
                    if (msg == Native.WM_MOUSEMOVE)
                    {
                        OnPointerMove(Marshal.ReadInt32(lParam, 0), Marshal.ReadInt32(lParam, 4));
                    }
                    else if (open && (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN ||
                                      msg == Native.WM_MBUTTONDOWN || msg == Native.WM_XBUTTONDOWN))
                    {
                        int x = Marshal.ReadInt32(lParam, 0), y = Marshal.ReadInt32(lParam, 4);
                        if (!barRect.Contains(x, y) && !infoRect.Contains(x, y)) CloseCharms();   // click outside
                    }
                }
                catch { }
            }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        void OnPointerMove(int x, int y)
        {
            // hot corner detection (only does real work when the pointer is on the far-right edge)
            bool c = false;
            if (x >= vr.Right - 1)
            {
                Screen s = Screen.FromPoint(new Point(x, y));
                Rectangle sb = s.Bounds;
                c = (y <= sb.Top + cornerPx) || (y >= sb.Bottom - 1 - cornerPx);
                if (c) cornerScreen = s;
            }
            if (c != inCorner)
            {
                inCorner = c;
                if (c) { if (!open) dwellTimer.Start(); } else dwellTimer.Stop();
            }

            // auto-close when the bar was opened by the mouse and the pointer wanders off
            if (open && byMouse)
            {
                int lp = (int)(Cfg.LeavePx * scale);
                bool inside = (x >= barRect.Left - lp && y >= barRect.Top && y < barRect.Bottom) || infoRect.Contains(x, y);
                if (!inside) { if (!leaveTimer.Enabled) leaveTimer.Start(); }
                else if (leaveTimer.Enabled) leaveTimer.Stop();
            }
        }

        void OnDwell(object s, EventArgs e)
        {
            dwellTimer.Stop();
            if (inCorner && !open) OpenCharms(true, cornerScreen != null ? cornerScreen : Screen.PrimaryScreen);
        }

        void OnLeave(object s, EventArgs e)
        {
            leaveTimer.Stop();
            if (open && byMouse) CloseCharms();
        }

        void OnHotKey(int id)
        {
            if (id == ID_ESC) { CloseCharms(); return; }
            if (open) CloseCharms(); else OpenCharms(false, Screen.FromPoint(Cursor.Position));
        }

        void SetEsc(bool on)
        {
            if (on == escReg) return;
            if (on) escReg = Native.RegisterHotKey(bar.Handle, ID_ESC, 0, 0x1B);
            else { Native.UnregisterHotKey(bar.Handle, ID_ESC); escReg = false; }
        }

        // ------------------------------------------------------- open / close ---
        void OpenCharms(bool viaMouse, Screen scr)
        {
            if (open) return;
            open = true;
            byMouse = viaMouse;
            leaveTimer.Stop();
            dwellTimer.Stop();
            trimTimer.Stop();
            bar.Accent = Sys.Accent();      // pick up theme colour changes every time it opens

            if (!bar.Visible)
            {
                Rectangle b = scr.Bounds, wa = scr.WorkingArea;
                int bw = (int)(Cfg.BarWidth * scale);

                RefreshAll();
                info.Relayout();
                int infoX = wa.Left + (int)(48 * scale);
                int infoY = Math.Min(b.Bottom - (int)(60 * scale), wa.Bottom - (int)(24 * scale)) - info.Height;

                barRect = new Rectangle(b.Right - bw, b.Top, bw, b.Height);
                infoRect = new Rectangle(infoX, infoY, info.Width, info.Height);
                offBarX = b.Right;
                offInfoX = b.Left - info.Width;
                curBarX = offBarX;
                curInfoX = offInfoX;

                bar.Bounds = new Rectangle(offBarX, b.Top, bw, b.Height);
                info.Bounds = new Rectangle(offInfoX, infoY, info.Width, info.Height);
                bar.Show();
                info.Show();
                Native.MakeTopmost(bar.Handle);
                Native.MakeTopmost(info.Handle);
            }
            else
            {
                bar.Invalidate();
            }

            tickCount = 0;
            infoTimer.Start();
            SetEsc(true);
            StartAnim(true);
        }

        void CloseCharms()
        {
            if (!open) return;
            open = false;
            leaveTimer.Stop();
            infoTimer.Stop();
            SetEsc(false);
            StartAnim(false);
        }

        void OnItemClicked(int index)
        {
            CloseCharms();
            if (index == 2)
            {
                try { StartScreen.Toggle(); }
                catch { Actions.Run(2); }       // if anything goes wrong, fall back to the normal Start menu
            }
            else Actions.Run(index);
        }

        void OnInfoClicked()
        {
            CloseCharms();
            Actions.Calendar();
        }

        // ----------------------------------------------------------- animation ---
        void StartAnim(bool show)
        {
            animShow = show;
            fromBarX = curBarX;
            fromInfoX = curInfoX;
            toBarX = show ? barRect.X : offBarX;
            toInfoX = show ? infoRect.X : offInfoX;
            double frac = Math.Abs(toBarX - fromBarX) / (double)Math.Max(1, barRect.Width);
            animDur = (show ? Cfg.ShowMs : Cfg.HideMs) * Math.Max(0.35, Math.Min(1.0, frac));
            sw.Reset();
            sw.Start();
            if (!periodOn) { Native.timeBeginPeriod(1); periodOn = true; }
            animTimer.Start();
        }

        void OnAnimTick(object s, EventArgs e)
        {
            double t = sw.Elapsed.TotalMilliseconds / animDur;
            if (t > 1.0) t = 1.0;
            double k = animShow ? 1.0 - Math.Pow(1.0 - t, 3.0) : t * t;     // ease-out in, ease-in out
            curBarX = fromBarX + (int)Math.Round((toBarX - fromBarX) * k);
            curInfoX = fromInfoX + (int)Math.Round((toInfoX - fromInfoX) * k);
            Native.MoveTo(bar.Handle, curBarX, bar.Top);
            Native.MoveTo(info.Handle, curInfoX, info.Top);

            if (t >= 1.0)
            {
                animTimer.Stop();
                sw.Stop();
                if (periodOn) { Native.timeEndPeriod(1); periodOn = false; }
                if (!open)
                {
                    bar.Hide();
                    info.Hide();
                    trimTimer.Start();
                }
            }
            else
            {
                Native.Flush();     // wait for the next display refresh -> evenly spaced frames
            }
        }

        // ------------------------------------------------- clock/battery/network ---
        void RefreshAll()
        {
            UpdateClock();
            UpdatePower();
            info.Net = Sys.NetState();
        }

        bool UpdateClock()
        {
            DateTime n = DateTime.Now;
            DateTimeFormatInfo f = CultureInfo.CurrentCulture.DateTimeFormat;
            string t, ap = "";
            if (f.ShortTimePattern.IndexOf('H') >= 0) t = n.ToString("HH:mm");
            else { t = n.ToString("h:mm"); ap = n.ToString("tt"); }
            string wd = n.ToString("dddd"), dt = n.ToString(f.MonthDayPattern);
            bool changed = t != info.Time || ap != info.AmPm || wd != info.Weekday || dt != info.Date;
            info.Time = t; info.AmPm = ap; info.Weekday = wd; info.Date = dt;
            return changed;
        }

        bool UpdatePower()
        {
            PowerStatus ps = SystemInformation.PowerStatus;
            bool noBat = (ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) != 0 ||
                         ps.BatteryChargeStatus == BatteryChargeStatus.Unknown;
            float p = ps.BatteryLifePercent;
            int pct = (p >= 0f && p <= 1f) ? (int)Math.Round(p * 100f) : -1;
            bool chg = ps.PowerLineStatus == PowerLineStatus.Online;
            bool changed = noBat != info.NoBattery || pct != info.BatteryPct || chg != info.Charging;
            info.NoBattery = noBat; info.BatteryPct = pct; info.Charging = chg;
            return changed;
        }

        void OnInfoTick(object s, EventArgs e)
        {
            bool ch = UpdateClock();
            tickCount++;
            if (tickCount % 10 == 0)
            {
                ch |= UpdatePower();
                int n = Sys.NetState();
                if (n != info.Net) { info.Net = n; ch = true; }
            }
            if (ch) info.Invalidate();
        }

        // ------------------------------------------------------------- memory ---
        void OnTrim(object s, EventArgs e)
        {
            trimTimer.Stop();
            if (!open) TrimMemory();
        }

        static void TrimMemory()
        {
            try
            {
                GC.Collect();
                using (Process p = Process.GetCurrentProcess())
                    Native.SetProcessWorkingSetSize(p.Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }
        }

        // -------------------------------------------------------- tray / startup ---
        static Icon MakeTrayIcon(Color accent)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Icons.Start(g, 1, 1, 30, accent);
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Items.Add("Show charms", null, delegate { OpenCharms(false, Screen.FromPoint(Cursor.Position)); });
            ToolStripMenuItem st = new ToolStripMenuItem("Run at startup");
            st.Checked = IsStartup();
            st.Click += delegate { SetStartup(!st.Checked); st.Checked = IsStartup(); };
            m.Items.Add(st);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Exit", null, delegate { Quit(); });
            return m;
        }

        static bool IsStartup()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunName) != null;
            }
            catch { return false; }
        }

        static void SetStartup(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
            }
            catch { }
        }

        void Quit()
        {
            try { if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook); } catch { }
            hook = IntPtr.Zero;
            Native.UnregisterHotKey(bar.Handle, ID_WINC);
            Native.UnregisterHotKey(bar.Handle, ID_ALTC);
            SetEsc(false);
            if (periodOn) { Native.timeEndPeriod(1); periodOn = false; }
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool first;
            using (System.Threading.Mutex mutex = new System.Threading.Mutex(true, "Local\\Win8CharmsBar_Singleton", out first))
            {
                if (!first) return;
                try { Native.SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new CharmsApp());
            }
        }
    }
}
