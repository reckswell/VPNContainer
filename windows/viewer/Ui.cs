// The native side: one Windows window per window of the session, painted
// from the VNC framebuffer at that window's place in the session, with the
// mouse and keyboard sent back through VNC.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace GtVpn
{
    static class Native
    {
        public static readonly bool IsWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, IntPtr bits);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("user32.dll")] public static extern bool GetKeyboardState(byte[] state);
        [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, StringBuilder buf, int n, uint flags, IntPtr layout);
        [DllImport("user32.dll")] public static extern IntPtr CreateIconIndirect(ref ICONINFO info);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Windows 11 frames: dark title bars when the system is in dark mode,
        // and square corners for menus and other popups (their content is
        // drawn square by the program in the session).
        public static void StyleFrame(IntPtr hwnd, bool popup)
        {
            if (!IsWindows) return;
            try
            {
                int v;
                if (popup) { v = 1; DwmSetWindowAttribute(hwnd, 33, ref v, 4); }      // DWMWA_WINDOW_CORNER_PREFERENCE: do not round
                else if (DarkMode()) { v = 1; DwmSetWindowAttribute(hwnd, 20, ref v, 4); } // DWMWA_USE_IMMERSIVE_DARK_MODE
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        static bool DarkMode()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object o = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return o is int && (int)o == 0;
                }
            }
            catch { return false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

        public static IntPtr Dib(int w, int h, out IntPtr bits)
        {
            var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            return CreateDIBSection(IntPtr.Zero, ref bmi, 0, out bits, IntPtr.Zero, 0);
        }
    }

    // The copy of the framebuffer that is painted: a DIB section on Windows
    // (BitBlt), a Bitmap elsewhere (Mono, for the lab).
    sealed class Mirror
    {
        int w, h;
        IntPtr dc, bmp, bits;
        Bitmap gb;
        int[] px;
        GCHandle pin;

        public void Resize(int nw, int nh)
        {
            Free(); w = nw; h = nh;
            if (w <= 0 || h <= 0) return;
            if (Native.IsWindows)
            {
                bmp = Native.Dib(w, h, out bits);
                dc = Native.CreateCompatibleDC(IntPtr.Zero);
                Native.SelectObject(dc, bmp);
            }
            else
            {
                px = new int[w * h];
                pin = GCHandle.Alloc(px, GCHandleType.Pinned);
                gb = new Bitmap(w, h, w * 4, PixelFormat.Format32bppRgb, pin.AddrOfPinnedObject());
            }
        }

        void Free()
        {
            if (dc != IntPtr.Zero) { Native.DeleteDC(dc); dc = IntPtr.Zero; }
            if (bmp != IntPtr.Zero) { Native.DeleteObject(bmp); bmp = IntPtr.Zero; }
            if (gb != null) { gb.Dispose(); gb = null; }
            if (pin.IsAllocated) pin.Free();
            px = null;
        }

        public void Copy(int[] fb, int fbw, int fbh, Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, Math.Min(w, fbw), Math.Min(h, fbh)));
            if (r.Width <= 0 || r.Height <= 0) return;
            if (Native.IsWindows)
            {
                for (int y = r.Y; y < r.Bottom; y++)
                    Marshal.Copy(fb, y * fbw + r.X, new IntPtr(bits.ToInt64() + ((long)y * w + r.X) * 4), r.Width);
            }
            else
            {
                // Elsewhere (Mono, for testing) the bitmap wraps a pinned array:
                // Mono's LockBits on part of a bitmap writes to the wrong rows.
                // Its pixels are premultiplied, so the unused top byte must read
                // as opaque.
                for (int y = r.Y; y < r.Bottom; y++)
                {
                    int s = y * fbw + r.X, d = y * w + r.X;
                    for (int k = 0; k < r.Width; k++) px[d + k] = fb[s + k] | unchecked((int)0xff000000);
                }
            }
        }

        public void Save(string path) { if (gb != null) gb.Save(path, ImageFormat.Png); }

        public void Paint(Graphics g, Rectangle dst, int sx, int sy)
        {
            if (Native.IsWindows && dc != IntPtr.Zero)
            {
                IntPtr hdc = g.GetHdc();
                try { Native.BitBlt(hdc, dst.X, dst.Y, dst.Width, dst.Height, dc, sx, sy, 0x00CC0020); }
                finally { g.ReleaseHdc(hdc); }
            }
            else if (gb != null) g.DrawImage(gb, dst, new Rectangle(sx, sy, dst.Width, dst.Height), GraphicsUnit.Pixel);
        }
    }

    sealed class XForm : Form
    {
        public readonly XWin Win;
        public Rectangle XRect { get; set; } // where the window is in the session (framebuffer coordinates)
        readonly Viewer v;
        public bool ClosingFromX, Applying;
        public DateTime LastSent;

        public XForm(Viewer v, XWin w, XForm owner)
        {
            this.v = v; Win = w;
            XRect = new Rectangle(w.X, w.Y, w.W, w.H);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
            StartPosition = FormStartPosition.Manual;
            BackColor = SystemColors.Control;
            Text = w.Title;
            if (w.Popup) { FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; }
            else
            {
                bool fixedSize = w.MinW > 0 && w.MinW == w.MaxW && w.MinH == w.MaxH;
                FormBorderStyle = fixedSize ? FormBorderStyle.FixedSingle : FormBorderStyle.Sizable;
                MaximizeBox = !fixedSize;
                if (owner != null) { Owner = owner; ShowInTaskbar = false; MinimizeBox = false; }
                if (v.AppIcon != null) Icon = v.AppIcon;
                if (w.MinW > 0 || w.MinH > 0) MinimumSize = Size.Empty; // set after the frame is known
            }
            if (v.CurrentCursor != null) Cursor = v.CurrentCursor;
        }

        protected override bool ShowWithoutActivation { get { return Win != null && Win.Popup; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                if (Win != null && Win.Popup) cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return cp;
            }
        }

        // Puts the client area at the session position x, y with size w x h.
        public void Place(int x, int y, int w, int h)
        {
            Applying = true;
            try
            {
                var want = new Point(x + v.OriginX, y + v.OriginY);
                ClientSize = new Size(Math.Max(1, w), Math.Max(1, h));
                Location = want;
                if (!Win.Popup)
                {
                    var c = PointToScreen(Point.Empty);
                    Location = new Point(Location.X - (c.X - want.X), Location.Y - (c.Y - want.Y));
                }
            }
            finally { Applying = false; }
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.StyleFrame(Handle, Win != null && Win.Popup); }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e) { v.PaintForm(this, e.Graphics, e.ClipRectangle); }

        protected override void OnMouseMove(MouseEventArgs e) { v.Pointer(this, e.X, e.Y, 0); }
        protected override void OnMouseDown(MouseEventArgs e) { v.Pointer(this, e.X, e.Y, 0); }
        protected override void OnMouseUp(MouseEventArgs e) { v.Pointer(this, e.X, e.Y, 0); }
        protected override void OnMouseWheel(MouseEventArgs e) { v.Pointer(this, e.X, e.Y, e.Delta); }

        protected override void OnActivated(EventArgs e) { base.OnActivated(e); if (!Win.Popup) v.FormActivated(this); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); v.Deactivated(); }

        protected override void OnMove(EventArgs e) { base.OnMove(e); if (!Applying && IsHandleCreated) v.NativeMoved(this, false); }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Applying || !IsHandleCreated) return;
            if (WindowState == FormWindowState.Minimized) { v.Minimized(this); return; }
            v.NativeMoved(this, false);
        }
        protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); v.NativeMoved(this, true); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!ClosingFromX && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; v.CloseRequested(this); return; }
            base.OnFormClosing(e);
        }
    }

    // The main (hidden) window, the tray icon and the glue between VNC, the
    // window manager and the native windows.
    sealed class Viewer : Form, IWmSink, IMessageFilter
    {
        public RfbClient Rfb;
        public Wm Wm;
        public XConn X;
        public int OriginX, OriginY;
        public Icon AppIcon;
        public Cursor CurrentCursor;
        readonly Mirror mirror = new Mirror();
        readonly Dictionary<uint, XForm> forms = new Dictionary<uint, XForm>();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly object dirtyLock = new object();
        List<Rectangle> dirty = new List<Rectangle>();
        bool flushQueued, resizeQueued;
        readonly Dictionary<int, uint> keysDown = new Dictionary<int, uint>();
        public string Label = "GT session";
        public bool Debug = Environment.GetEnvironmentVariable("GTVPN_VIEWER_DEBUG") == "1";
        readonly TextWriter log;
        long paints, lastFrames, lastBytes, lastPaints; DateTime lastStat = DateTime.Now;

        public Viewer(TextWriter log)
        {
            this.log = log;
            ShowInTaskbar = false; WindowState = FormWindowState.Minimized; FormBorderStyle = FormBorderStyle.FixedToolWindow;
            Opacity = 0; Size = new Size(1, 1);
            try { AppIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            tray.Icon = AppIcon ?? SystemIcons.Application;
            tray.Text = "gtvpn viewer";
            var menu = new ContextMenu();
            menu.MenuItems.Add("Quit the viewer (Cadence keeps running on the server)", delegate { Quit("closed from the tray"); });
            tray.ContextMenu = menu;
            tray.Visible = true;
            var timer = new System.Windows.Forms.Timer { Interval = 5000 };
            timer.Tick += delegate { Stats(); };
            timer.Start();
        }

        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }

        public void Log(string s) { lock (log) { log.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + s); log.Flush(); } }

        void Stats()
        {
            if (Rfb == null) return;
            var now = DateTime.Now; double dt = (now - lastStat).TotalSeconds; lastStat = now;
            long f = Rfb.Frames, b = Rfb.Bytes, p = Interlocked.Read(ref paints);
            if (Debug) { Log("tick frames " + f); lock (Rfb.FbLock) mirror.Save(Path.Combine(Path.GetTempPath(), "gtvpn-mirror.png")); }
            if (f != lastFrames)
                Log(string.Format("updates/s {0:F1}  MB/s {1:F2}  paints/s {2:F1}  windows {3}", (f - lastFrames) / dt, (b - lastBytes) / dt / 1e6, (p - lastPaints) / dt, forms.Count));
            lastFrames = f; lastBytes = b; lastPaints = p;
            tray.Text = ("gtvpn viewer: " + Label).Substring(0, Math.Min(63, ("gtvpn viewer: " + Label).Length));
        }

        public void Quit(string why)
        {
            Log("quit: " + why);
            tray.Visible = false;
            try { if (X != null) X.Close(); } catch { }
            try { if (Rfb != null) Rfb.Close(); } catch { }
            Application.Exit();
        }

        // ---- VNC side (receive thread) --------------------------------------
        public void OnUpdated(List<Rectangle> rs)
        {
            lock (dirtyLock)
            {
                dirty.AddRange(rs);
                if (flushQueued) return;
                flushQueued = true;
            }
            BeginInvoke(new Action(Flush));
        }

        public void OnResized(int w, int h)
        {
            lock (dirtyLock) { resizeQueued = true; dirty.Add(new Rectangle(0, 0, w, h)); if (flushQueued) return; flushQueued = true; }
            BeginInvoke(new Action(Flush));
        }

        void Flush()
        {
            List<Rectangle> rs;
            bool resize;
            lock (dirtyLock) { rs = dirty; dirty = new List<Rectangle>(); flushQueued = false; resize = resizeQueued; resizeQueued = false; }
            if (Debug)
            {
                var u = Rectangle.Empty; foreach (var r in rs) u = u.IsEmpty ? r : Rectangle.Union(u, r);
                Log("flush " + rs.Count + " rects in " + u + (resize ? " resize" : "") + " fb " + Rfb.Width + "x" + Rfb.Height);
            }
            lock (Rfb.FbLock)
            {
                if (resize) mirror.Resize(Rfb.Width, Rfb.Height);
                foreach (var r in rs) mirror.Copy(Rfb.Fb, Rfb.Width, Rfb.Height, r);
                if (Debug && resize)
                {
                    mirror.Save(Path.Combine(Path.GetTempPath(), "gtvpn-mirror0.png"));
                    using (var b = new Bitmap(Rfb.Width, Rfb.Height, PixelFormat.Format32bppRgb))
                    {
                        var bd = b.LockBits(new Rectangle(0, 0, Rfb.Width, Rfb.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                        Marshal.Copy(Rfb.Fb, 0, bd.Scan0, Rfb.Width * Rfb.Height);
                        b.UnlockBits(bd);
                        b.Save(Path.Combine(Path.GetTempPath(), "gtvpn-fb0.png"), ImageFormat.Png);
                    }
                }
            }
            // The timer below is starved while updates stream in (timer
            // messages wait for an empty queue), so the stats come from here too.
            if ((DateTime.Now - lastStat).TotalSeconds >= 5) Stats();
            foreach (var f in forms.Values)
            {
                foreach (var r in rs)
                {
                    var i = Rectangle.Intersect(r, f.XRect);
                    if (i.Width > 0 && i.Height > 0)
                    {
                        f.Invalidate(new Rectangle(i.X - f.XRect.X, i.Y - f.XRect.Y, i.Width, i.Height));
                        if (Debug) Log("invalidate " + f.Win.Id.ToString("x") + " " + i);
                    }
                }
            }
        }

        public void OnCursor(CursorShape c)
        {
            if (!Native.IsWindows) return;
            BeginInvoke(new Action(delegate
            {
                try
                {
                    var cur = MakeCursor(c);
                    CurrentCursor = cur;
                    foreach (var f in forms.Values) f.Cursor = cur;
                }
                catch (Exception e) { Log("cursor: " + e.Message); }
            }));
        }

        static Cursor MakeCursor(CursorShape c)
        {
            int w = Math.Max(1, c.W), h = Math.Max(1, c.H);
            IntPtr bits;
            IntPtr color = Native.Dib(w, h, out bits);
            var px = new int[w * h];
            int stride = (c.W + 7) / 8;
            for (int y = 0; y < c.H; y++)
                for (int x = 0; x < c.W; x++)
                {
                    bool on = (c.Mask[y * stride + (x >> 3)] & (0x80 >> (x & 7))) != 0;
                    px[y * w + x] = on ? unchecked((int)0xff000000) | (c.Pixels[y * c.W + x] & 0xffffff) : 0;
                }
            Marshal.Copy(px, 0, bits, px.Length);
            IntPtr mask = Native.CreateBitmap(w, h, 1, 1, IntPtr.Zero);
            var ii = new Native.ICONINFO { fIcon = false, xHotspot = c.HotX, yHotspot = c.HotY, hbmMask = mask, hbmColor = color };
            IntPtr hc = Native.CreateIconIndirect(ref ii);
            Native.DeleteObject(color); Native.DeleteObject(mask);
            return new Cursor(hc);
        }

        public void PaintForm(XForm f, Graphics g, Rectangle clip)
        {
            Interlocked.Increment(ref paints);
            if (Debug) Log("paint " + f.Win.Id.ToString("x") + " " + clip + " xrect " + f.XRect);
            // The part of the client area that the session's window covers comes
            // from the framebuffer; the rest (while a resize catches up) is blank.
            var inside = Rectangle.Intersect(clip, new Rectangle(0, 0, f.XRect.Width, f.XRect.Height));
            if (inside.Width > 0 && inside.Height > 0)
                lock (Rfb.FbLock) mirror.Paint(g, inside, f.XRect.X + inside.X, f.XRect.Y + inside.Y);
            using (var region = new Region(clip))
            {
                region.Exclude(inside);
                using (var b = new SolidBrush(SystemColors.Control)) g.FillRegion(b, region);
            }
        }

        // ---- input ----------------------------------------------------------
        public void Pointer(XForm f, int x, int y, int wheel)
        {
            int mask = 0;
            var mb = Control.MouseButtons;
            if ((mb & MouseButtons.Left) != 0) mask |= 1;
            if ((mb & MouseButtons.Middle) != 0) mask |= 2;
            if ((mb & MouseButtons.Right) != 0) mask |= 4;
            int sx = f.XRect.X + x, sy = f.XRect.Y + y;
            // A click gives the window the session's focus even where the
            // native window was not activated (an owned window, or Mono).
            if (mask != 0 && f != focused && !f.Win.Popup) FormActivated(f);
            try
            {
                Rfb.Pointer(sx, sy, mask);
                for (int n = Math.Abs(wheel) / 120; n > 0; n--)
                {
                    int b = wheel > 0 ? 8 : 16;
                    Rfb.Pointer(sx, sy, mask | b);
                    Rfb.Pointer(sx, sy, mask);
                }
            }
            catch (IOException) { }
        }

        public bool PreFilterMessage(ref Message m)
        {
            int msg = m.Msg;
            if (msg < 0x100 || msg > 0x109) return false;
            var c = Control.FromChildHandle(m.HWnd);
            // Keys for a session window: one of ours, or (Mono sends keys to a
            // window of its own) whichever session window has the focus.
            var f = (c == null ? focused : c.FindForm()) as XForm;
            if (f == null) return false;
            int vk = m.WParam.ToInt32() & 0xff;
            long lp = m.LParam.ToInt64();
            if (msg == 0x104 && vk == 0x73) { ReleaseAll(); return false; }            // Alt+F4: close the window
            if (msg == 0x100 || msg == 0x104) { SendKeyDown(vk, (int)((lp >> 16) & 0xff), (lp & 0x01000000) != 0); return true; }
            if (msg == 0x101 || msg == 0x105) { SendKeyUp(vk); return true; }
            return true;                                                              // WM_CHAR etc.: handled as keys
        }

        void SendKeyDown(int vk, int scan, bool ext)
        {
            uint ks;
            if (!keysDown.TryGetValue(vk, out ks))
            {
                ks = Keys2.KeySym(vk, scan, ext);
                if (Debug) Log("key " + vk.ToString("x") + " -> keysym " + ks.ToString("x"));
                if (ks == 0) return;
                keysDown[vk] = ks;
            }
            try { Rfb.Key(ks, true); } catch (IOException) { }
        }

        void SendKeyUp(int vk)
        {
            uint ks;
            if (!keysDown.TryGetValue(vk, out ks)) return;
            keysDown.Remove(vk);
            try { Rfb.Key(ks, false); } catch (IOException) { }
        }

        void ReleaseAll()
        {
            foreach (var ks in keysDown.Values) try { Rfb.Key(ks, false); } catch (IOException) { }
            keysDown.Clear();
        }

        // ---- native window events ---------------------------------------------
        XForm focused;

        public void FormActivated(XForm f) { focused = f; Wm.Focus(f.Win.Id, f.Win); }

        public void Deactivated()
        {
            BeginInvoke(new Action(delegate { if (!(Form.ActiveForm is XForm)) { ReleaseAll(); focused = null; } }));
        }

        public void Minimized(XForm f) { Wm.Lower(f.Win.Id); }

        public void CloseRequested(XForm f) { Wm.Close(f.Win.Id); }

        public void NativeMoved(XForm f, bool final)
        {
            if (f.Win.Popup || f.WindowState == FormWindowState.Minimized) return;
            var now = DateTime.Now;
            if (!final && (now - f.LastSent).TotalMilliseconds < 15) return;
            f.LastSent = now;
            var c = f.PointToScreen(Point.Empty);
            int x = c.X - OriginX, y = c.Y - OriginY, w = f.ClientSize.Width, h = f.ClientSize.Height;
            if (x == f.Win.X && y == f.Win.Y && w == f.Win.W && h == f.Win.H) return;
            f.Win.X = x; f.Win.Y = y; f.Win.W = w; f.Win.H = h;
            if (w == f.XRect.Width && h == f.XRect.Height) Wm.Move(f.Win.Id, x, y);
            else Wm.Configure(f.Win.Id, x, y, w, h);
            Wm.SetMaximized(f.Win.Id, f.WindowState == FormWindowState.Maximized);
        }

        // ---- the window manager's side (WM thread) ------------------------------
        void Ui(Action a)
        {
            try { BeginInvoke(new Action(delegate { try { a(); } catch (Exception e) { Log("ui: " + e); } })); }
            catch (InvalidOperationException) { }
        }

        public void Add(XWin w)
        {
            Ui(delegate
            {
                XForm old;
                if (forms.TryGetValue(w.Id, out old)) { old.ClosingFromX = true; old.Close(); forms.Remove(w.Id); }
                XForm owner = null;
                if (w.TransientFor != 0) forms.TryGetValue(w.TransientFor, out owner);
                if (owner == null && w.Dialog && Form.ActiveForm is XForm) owner = (XForm)Form.ActiveForm;
                var f = new XForm(this, w, owner);
                forms[w.Id] = f;
                f.CreateControl();
                var h = f.Handle;
                f.Place(w.X, w.Y, w.W, w.H);
                if (!w.Popup)
                {
                    // Record where the native window really is (Windows may
                    // have adjusted it) and move the session's window there.
                    var c = f.PointToScreen(Point.Empty);
                    int nx = c.X - OriginX, ny = c.Y - OriginY;
                    if (nx != w.X || ny != w.Y) { w.X = nx; w.Y = ny; Wm.Move(w.Id, nx, ny); }
                }
                f.Show();
                Log((w.Popup ? "popup " : "window ") + w.Id.ToString("x") + " \"" + w.Title + "\" " + w.W + "x" + w.H + "+" + w.X + "+" + w.Y);
            });
        }

        public void Remove(uint id)
        {
            Ui(delegate
            {
                XForm f;
                if (!forms.TryGetValue(id, out f)) return;
                forms.Remove(id);
                f.ClosingFromX = true;
                f.Close();
                f.Dispose();
            });
        }

        public void Title(uint id, string title) { Ui(delegate { XForm f; if (forms.TryGetValue(id, out f)) f.Text = title; }); }

        public void MoveResize(uint id, int x, int y, int w, int h)
        {
            Ui(delegate
            {
                XForm f;
                if (!forms.TryGetValue(id, out f)) return;
                f.Win.X = x; f.Win.Y = y; f.Win.W = w; f.Win.H = h;
                if (f.WindowState != FormWindowState.Normal && !f.Win.Popup) return;
                f.Place(x, y, w, h);
            });
        }

        public void XGeometry(uint id, int x, int y, int w, int h)
        {
            Ui(delegate
            {
                XForm f;
                if (!forms.TryGetValue(id, out f)) return;
                var r = new Rectangle(x, y, w, h);
                if (r == f.XRect) return;
                f.XRect = r;
                f.Invalidate();
            });
        }

        public void Activate(uint id) { Ui(delegate { XForm f; if (forms.TryGetValue(id, out f)) { f.Activate(); } }); }

        public void SetState(uint id, bool maximize, bool minimize)
        {
            Ui(delegate
            {
                XForm f;
                if (!forms.TryGetValue(id, out f) || f.Win.Popup) return;
                f.WindowState = minimize ? FormWindowState.Minimized : maximize ? FormWindowState.Maximized : FormWindowState.Normal;
            });
        }

        public void OnClosed(string why) { Ui(delegate { Quit(why); }); }

        public void Replaced() { Ui(delegate { Quit("another window manager took over the session"); }); }
    }
}
