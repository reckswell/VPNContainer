// gtvpn viewer: the windows of a gtvpn vnc session as separate native
// windows, with Windows 11's own frames.
//
//   gtvpn-viewer.exe --vnc 127.0.0.1:P --x11 127.0.0.1:Q [--label TEXT]
//                    [--encoding zrle|tight] [--compress 0-9] [--quality 0-9]
//
// --encoding: what Xvnc sends first: zrle (lossless, the default) or tight
// (JPEG for photo-like areas). --compress: zlib's effort in Xvnc (default 1;
// 0 sends the most bytes for the least CPU there). --quality: Tight's JPEG.
//
// The X display cookie comes in the GTVPN_X_COOKIE environment variable (hex),
// so it never shows on a command line. `gtvpn vnc <host> windows` sets up the
// forwards and starts this. The log is %TEMP%\gtvpn-viewer.log.
using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace GtVpn
{
    static class Keys2
    {
        // X keysym for a Windows virtual key (0: none).
        public static uint KeySym(int vk, int scan, bool ext)
        {
            switch (vk)
            {
                case 0x08: return 0xff08;                         // BackSpace
                case 0x09: return 0xff09;                         // Tab
                case 0x0d: return ext ? 0xff8du : 0xff0du;        // KP_Enter / Return
                case 0x1b: return 0xff1b;                         // Escape
                case 0x20: return 0x20;                           // space
                case 0x2e: return ext ? 0xffffu : 0xff9fu;        // Delete / KP_Delete
                case 0x24: return ext ? 0xff50u : 0xff95u;        // Home / KP_Home
                case 0x23: return ext ? 0xff57u : 0xff9cu;        // End / KP_End
                case 0x21: return ext ? 0xff55u : 0xff9au;        // Prior
                case 0x22: return ext ? 0xff56u : 0xff9bu;        // Next
                case 0x25: return ext ? 0xff51u : 0xff96u;        // Left
                case 0x26: return ext ? 0xff52u : 0xff97u;        // Up
                case 0x27: return ext ? 0xff53u : 0xff98u;        // Right
                case 0x28: return ext ? 0xff54u : 0xff99u;        // Down
                case 0x2d: return ext ? 0xff63u : 0xff9eu;        // Insert / KP_Insert
                case 0x0c: return 0xff9d;                         // KP_Begin (numpad 5, no NumLock)
                case 0x10: return scan == 0x36 ? 0xffe2u : 0xffe1u; // Shift_R / Shift_L
                case 0x11: return ext ? 0xffe4u : 0xffe3u;        // Control_R / Control_L
                case 0x12: return ext ? 0xffeau : 0xffe9u;        // Alt_R / Alt_L
                case 0x5b: return 0xffeb;                         // Super_L
                case 0x5c: return 0xffec;                         // Super_R
                case 0x5d: return 0xff67;                         // Menu
                case 0x14: return 0xffe5;                         // Caps_Lock
                case 0x90: return 0xff7f;                         // Num_Lock
                case 0x91: return 0xff14;                         // Scroll_Lock
                case 0x13: return 0xff13;                         // Pause
                case 0x2c: return 0xff61;                         // Print
                case 0x6a: return 0xffaa;                         // KP_Multiply
                case 0x6b: return 0xffab;                         // KP_Add
                case 0x6d: return 0xffad;                         // KP_Subtract
                case 0x6e: return 0xffae;                         // KP_Decimal
                case 0x6f: return 0xffaf;                         // KP_Divide
            }
            if (vk >= 0x60 && vk <= 0x69) return 0xffb0u + (uint)(vk - 0x60);   // KP_0..KP_9
            if (vk >= 0x70 && vk <= 0x87) return 0xffbeu + (uint)(vk - 0x70);   // F1..F24
            // Mono: a character key's keysym comes from its WM_CHAR (Viewer.PreFilterMessage).
            if (!Native.IsWindows) return 0;
            // Character keys: the character with Shift and Caps Lock but without
            // Ctrl and Alt, so Ctrl+C arrives as Control_L + c, as X programs expect.
            var st = new byte[256];
            Native.GetKeyboardState(st);
            st[0x11] = st[0xa2] = st[0xa3] = 0; st[0x12] = st[0xa4] = st[0xa5] = 0;
            var sb = new StringBuilder(8);
            int n = Native.ToUnicodeEx((uint)vk, (uint)scan, st, sb, sb.Capacity, 4, Native.GetKeyboardLayout(0));
            if (n != 1) return 0;
            char c = sb[0];
            if (c < 0x20) return 0;
            return c < 0x100 ? c : 0x01000000u | c;
        }
    }

    static class Program
    {
        static int Port(string hp, out string host)
        {
            int i = hp.LastIndexOf(':');
            host = i > 0 ? hp.Substring(0, i) : "127.0.0.1";
            return int.Parse(i >= 0 ? hp.Substring(i + 1) : hp);
        }

        static byte[] Hex(string s)
        {
            s = s.Trim();
            if (s.Length % 2 != 0) throw new FormatException("bad cookie");
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return b;
        }

        [STAThread]
        static int Main(string[] args)
        {
            string vnc = null, x11 = null, label = "GT session", encoding = "zrle"; int quality = 8, compress = 1;
            for (int i = 0; i + 1 < args.Length; i += 2)
            {
                if (args[i] == "--vnc") vnc = args[i + 1];
                else if (args[i] == "--x11") x11 = args[i + 1];
                else if (args[i] == "--label") label = args[i + 1];
                else if (args[i] == "--quality") quality = int.Parse(args[i + 1]);
                else if (args[i] == "--compress") compress = int.Parse(args[i + 1]);
                else if (args[i] == "--encoding") encoding = args[i + 1].ToLowerInvariant();
            }
            string cookie = Environment.GetEnvironmentVariable("GTVPN_X_COOKIE");
            Environment.SetEnvironmentVariable("GTVPN_X_COOKIE", null);
            var logPath = Path.Combine(Path.GetTempPath(), "gtvpn-viewer.log");
            var log = new StreamWriter(logPath, false, new UTF8Encoding(false));
            if (vnc == null || x11 == null || string.IsNullOrEmpty(cookie))
            {
                MessageBox.Show("Start it with: gtvpn vnc <host> windows", "gtvpn viewer");
                return 2;
            }
            if (Native.IsWindows)
            {
                try { if (!Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) Native.SetProcessDPIAware(); }
                catch (EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var v = new Viewer(log) { Label = label };
            try
            {
                var screen = SystemInformation.VirtualScreen;
                v.OriginX = screen.X; v.OriginY = screen.Y;
                string vh, xh;
                int vp = Port(vnc, out vh), xp = Port(x11, out xh);

                var rfb = new RfbClient
                {
                    JpegQuality = Math.Max(0, Math.Min(9, quality)),
                    CompressLevel = Math.Max(0, Math.Min(9, compress)),
                    PreferZrle = encoding != "tight",
                };
                v.Rfb = rfb;
                rfb.Updated += v.OnUpdated;
                rfb.Resized += v.OnResized;
                rfb.CursorChanged += v.OnCursor;
                rfb.CutTextReceived += v.OnCutText;
                rfb.Closed += delegate(string why) { v.OnClosed("VNC: " + why); };
                var h = v.Handle;   // the UI thread's window exists before events arrive
                rfb.Connect(vh, vp);
                v.Log("VNC " + rfb.Name + " " + rfb.Width + "x" + rfb.Height + "; asking for " + screen.Width + "x" + screen.Height
                    + "; " + (rfb.PreferZrle ? "zrle" : "tight") + " compress " + rfb.CompressLevel + " quality " + rfb.JpegQuality);
                v.OnResized(rfb.Width, rfb.Height);
                rfb.RequestDesktopSize(screen.Width, screen.Height);

                var x = new XConn();
                v.X = x;
                x.Closed += delegate(string why) { v.OnClosed("X: " + why); };
                // BadWindow (3): a window closed while a request to it was on its way.
                x.AsyncError += delegate(XError e, int sq) { if (e.Code != 3) v.Log("X error " + e.Code + " from request " + e.Major); };
                x.Connect(xh, xp, Hex(cookie));
                var wm = new Wm(x, v) { OriginX = screen.X, OriginY = screen.Y };
                var wa = Screen.PrimaryScreen.WorkingArea;
                wm.WorkArea = new Rectangle(wa.X - screen.X, wa.Y - screen.Y, wa.Width, wa.Height);
                v.Wm = wm;
                Application.AddMessageFilter(v);
                wm.Start();
                v.Log("window manager running; X screen " + x.ScreenW + "x" + x.ScreenH);
            }
            catch (Exception e)
            {
                v.Log("could not start: " + e);
                MessageBox.Show("Could not connect to the session: " + e.Message, "gtvpn viewer");
                return 1;
            }
            // Mono runs WinForms timers only once the main form has loaded,
            // and this window is never shown: as the main form, its timers
            // would never tick and its message loop would spin, using a whole
            // core. So under Mono the loop runs without a main form (Quit ends it).
            if (Native.IsWindows) Application.Run(v);
            else Application.Run(new ApplicationContext());
            return 0;
        }
    }
}
