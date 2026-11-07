// The native side: one Windows window per window of the session, painted
// from the VNC framebuffer at that window's place in the session, with the
// mouse and keyboard sent back through VNC.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
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
        [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO info);
        [StructLayout(LayoutKind.Sequential)] struct FLASHWINFO { public int cbSize; public IntPtr hwnd; public uint flags, count, timeout; }

        // Windows 11 frames: dark title bars when the system is in dark mode,
        // and square corners for menus and other popups (their content is
        // drawn square by the program in the session).
        // redraw: the window is on the screen already (shown, or the Windows
        // theme changed), and its title bar must be drawn again to show it.
        public static void StyleFrame(IntPtr hwnd, bool popup, bool redraw)
        {
            if (!IsWindows) return;
            try
            {
                int v;
                if (popup) { v = 1; DwmSetWindowAttribute(hwnd, 33, ref v, 4); }      // DWMWA_WINDOW_CORNER_PREFERENCE: do not round
                else
                {
                    v = DarkMode() ? 1 : 0;
                    DwmSetWindowAttribute(hwnd, 20, ref v, 4);                          // DWMWA_USE_IMMERSIVE_DARK_MODE
                    // SWP_NOSIZE | NOMOVE | NOZORDER | NOACTIVATE | FRAMECHANGED
                    if (redraw) SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        // The taskbar groups windows by their AppUserModelID: give each X
        // program its own (and no pinning: a pinned button could not start it).
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint n);
            [PreserveSig] int GetAt(uint i, IntPtr key);
            [PreserveSig] int GetValue(ref PropKey key, IntPtr value);
            [PreserveSig] int SetValue(ref PropKey key, IntPtr value);
            [PreserveSig] int Commit();
        }
        [StructLayout(LayoutKind.Sequential, Pack = 4)] struct PropKey { public Guid Fmt; public uint Pid; }
        [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

        // The button's name is the relaunch display name (else Windows shows
        // the viewer's file name); it needs a relaunch command beside it,
        // which is never used, as pinning is off.
        public static void SetAppId(IntPtr hwnd, string program)
        {
            if (!IsWindows) return;
            var id = new StringBuilder("GtVpn.X.");
            foreach (char c in program) if (id.Length < 100 && (char.IsLetterOrDigit(c) || c == '-' || c == '.')) id.Append(c < 128 ? c : '_');
            IPropertyStore ps = null;
            IntPtr pv = Marshal.AllocCoTaskMem(24);
            try
            {
                var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
                if (SHGetPropertyStoreForWindow(hwnd, ref iid, out ps) != 0 || ps == null) return;
                var k = new PropKey { Fmt = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3") };   // PKEY_AppUserModel_*
                for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
                Marshal.WriteInt16(pv, 0, 11); Marshal.WriteInt16(pv, 8, -1);        // VT_BOOL true
                k.Pid = 9; ps.SetValue(ref k, pv);                                   // PreventPinning
                // These before the ID, which makes the taskbar read them.
                SetString(ps, k, 2, "\"" + Application.ExecutablePath + "\"", pv);   // RelaunchCommand
                // "virtuoso" -> "Virtuoso", as Windows programs' names read.
                SetString(ps, k, 4, char.ToUpperInvariant(program[0]) + program.Substring(1), pv); // RelaunchDisplayNameResource
                SetString(ps, k, 5, id.ToString(), pv);                              // ID
            }
            catch (Exception) { }
            finally
            {
                if (ps != null) Marshal.ReleaseComObject(ps);
                Marshal.FreeCoTaskMem(pv);
            }
        }

        static void SetString(IPropertyStore ps, PropKey k, uint pid, string value, IntPtr pv)
        {
            IntPtr str = Marshal.StringToCoTaskMemUni(value);
            try
            {
                for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
                Marshal.WriteInt16(pv, 0, 31); Marshal.WriteIntPtr(pv, 8, str);      // VT_LPWSTR
                k.Pid = pid;
                ps.SetValue(ref k, pv);
            }
            finally { Marshal.FreeCoTaskMem(str); }
        }

        // A window's own icon from _NET_WM_ICON (width, height, ARGB pixels,
        // repeated per size): the size nearest 32x32 that is not smaller, as
        // a .ico in memory.
        public static Icon MakeIcon(uint[] p)
        {
            int best = -1, bw = 0, bh = 0;
            for (int i = 0; i + 2 < p.Length; )
            {
                int w = (int)p[i], h = (int)p[i + 1];
                if (w <= 0 || h <= 0 || w > 4096 || h > 4096 || (long)i + 2 + (long)w * h > p.Length) break;
                if (w <= 256 && h <= 256 && (best < 0 || (bw < 32 ? w > bw : w >= 32 && w < bw))) { best = i; bw = w; bh = h; }
                i += 2 + w * h;
            }
            if (best < 0) return null;
            int mask = (bw + 31) / 32 * 4 * bh, size = 40 + bw * bh * 4 + mask;
            var ms = new MemoryStream();
            var o = new BinaryWriter(ms);
            o.Write((short)0); o.Write((short)1); o.Write((short)1);                // ICONDIR
            o.Write((byte)(bw & 255)); o.Write((byte)(bh & 255)); o.Write((byte)0); o.Write((byte)0);
            o.Write((short)1); o.Write((short)32); o.Write(size); o.Write(22);      // ICONDIRENTRY
            o.Write(40); o.Write(bw); o.Write(bh * 2); o.Write((short)1); o.Write((short)32);
            o.Write(0); o.Write(bw * bh * 4 + mask); o.Write(0); o.Write(0); o.Write(0); o.Write(0);
            for (int y = bh - 1; y >= 0; y--)                                       // bottom-up BGRA
                for (int x = 0; x < bw; x++) o.Write(p[best + 2 + y * bw + x]);
            o.Write(new byte[mask]);
            ms.Position = 0;
            try { return new Icon(ms); } catch (Exception) { return null; }
        }

        // A window that opened while you were in another program: flash its
        // taskbar button until you switch to it (Windows does not let it come
        // to the front by itself).
        public static void Flash(IntPtr hwnd)
        {
            if (!IsWindows) return;
            var f = new FLASHWINFO { hwnd = hwnd, flags = 0x2 | 0xC };              // FLASHW_TRAY | FLASHW_TIMERNOFG
            f.cbSize = Marshal.SizeOf(f);
            try { FlashWindowEx(ref f); } catch (EntryPointNotFoundException) { }
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

    // Mono: paints the mirror straight into a window with XPutImage, on
    // Mono's own X connection. Through System.Drawing each paint goes through
    // libgdiplus and cairo (a pixmap, an upload and a composite), and under
    // WSLg that path, not the session, limited how many changes a second
    // reached the screen. Anything unexpected (another Mono, another X
    // visual) keeps the System.Drawing path.
    static class XDirect
    {
        const string X = "libX11.so.6";
        [DllImport(X)] static extern int XDefaultScreen(IntPtr dpy);
        [DllImport(X)] static extern IntPtr XDefaultVisual(IntPtr dpy, int screen);
        [DllImport(X)] static extern int XDefaultDepth(IntPtr dpy, int screen);
        [DllImport(X)] static extern IntPtr XDefaultRootWindow(IntPtr dpy);
        [DllImport(X)] static extern IntPtr XCreateGC(IntPtr dpy, IntPtr d, UIntPtr mask, IntPtr values);
        [DllImport(X)] static extern IntPtr XCreateImage(IntPtr dpy, IntPtr visual, uint depth, int format, int offset, IntPtr data, uint w, uint h, int pad, int bpl);
        [DllImport(X)] static extern int XPutImage(IntPtr dpy, IntPtr d, IntPtr gc, IntPtr img, int sx, int sy, int dx, int dy, uint w, uint h);
        [DllImport(X)] static extern int XFlush(IntPtr dpy);
        [DllImport(X)] static extern int XFree(IntPtr p);

        static IntPtr dpy, gc, visual;
        static object xlock;
        static MethodInfo fromHandle;
        static PropertyInfo client;
        public static bool Ok;

        // Null once it can paint, else why not. Mono's X connection exists
        // once the first window has been created.
        public static string Init()
        {
            try
            {
                if (IntPtr.Size != 8 || !BitConverter.IsLittleEndian) return "not a 64-bit little-endian process";
                const BindingFlags any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var a = typeof(Control).Assembly;
                var x = a.GetType("System.Windows.Forms.XplatUIX11");
                var hwnd = a.GetType("System.Windows.Forms.Hwnd");
                if (x == null || hwnd == null) return "not Mono's X11 WinForms";
                var df = x.GetField("DisplayHandle", any); var lf = x.GetField("XlibLock", any);
                fromHandle = hwnd.GetMethod("ObjectFromHandle", any, null, new[] { typeof(IntPtr) }, null);
                client = hwnd.GetProperty("ClientWindow", any);
                if (df == null || lf == null || fromHandle == null || client == null) return "Mono's X11 internals have changed";
                dpy = (IntPtr)df.GetValue(null); xlock = lf.GetValue(null);
                if (dpy == IntPtr.Zero || xlock == null) return "Mono has no X connection yet";
                lock (xlock)
                {
                    int scr = XDefaultScreen(dpy);
                    visual = XDefaultVisual(dpy, scr);
                    // Visual: ext_data, visualid, class, then the colour masks.
                    long r = Marshal.ReadInt64(visual, 24), g = Marshal.ReadInt64(visual, 32), b = Marshal.ReadInt64(visual, 40);
                    int depth = XDefaultDepth(dpy, scr);
                    if (depth != 24 || r != 0xff0000 || g != 0xff00 || b != 0xff)
                        return "X visual of depth " + depth + ", masks " + r.ToString("x") + " " + g.ToString("x") + " " + b.ToString("x");
                    gc = XCreateGC(dpy, XDefaultRootWindow(dpy), UIntPtr.Zero, IntPtr.Zero);
                }
                Ok = gc != IntPtr.Zero;
                return Ok ? null : "XCreateGC failed";
            }
            catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
        }

        // An XImage over pixels that stay the caller's (0xXXRRGGBB ints).
        public static IntPtr Image(IntPtr data, int w, int h)
        {
            IntPtr img;
            lock (xlock) img = XCreateImage(dpy, visual, 24, 2 /* ZPixmap */, 0, data, (uint)w, (uint)h, 32, w * 4);
            if (img != IntPtr.Zero) Marshal.WriteInt32(img, 24, 0);   // byte_order: LSBFirst, as the ints are; Xlib converts if the server differs
            return img;
        }

        public static void FreeImage(IntPtr img)
        {
            Marshal.WriteIntPtr(img, 16, IntPtr.Zero);   // XImage.data: the pixels are not Xlib's to free
            XFree(img);
        }

        // The X window that holds a control's client area.
        public static IntPtr Window(Control c)
        {
            try
            {
                var h = fromHandle.Invoke(null, new object[] { c.Handle });
                return h == null ? IntPtr.Zero : (IntPtr)client.GetValue(h, null);
            }
            catch (Exception) { return IntPtr.Zero; }
        }

        public static void Put(IntPtr win, IntPtr img, int sx, int sy, int dx, int dy, int w, int h)
        {
            lock (xlock) XPutImage(dpy, win, gc, img, sx, sy, dx, dy, (uint)w, (uint)h);
        }

        public static void Flush() { if (Ok) lock (xlock) XFlush(dpy); }
    }

    // The copy of the framebuffer that is painted: a DIB section on Windows
    // (BitBlt), a Bitmap elsewhere (Mono), which XDirect can also put into
    // X windows directly.
    sealed class Mirror
    {
        int w, h;
        IntPtr dc, bmp, bits, ximg;
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
                if (XDirect.Ok) ximg = XDirect.Image(pin.AddrOfPinnedObject(), w, h);
            }
        }

        void Free()
        {
            if (dc != IntPtr.Zero) { Native.DeleteDC(dc); dc = IntPtr.Zero; }
            if (bmp != IntPtr.Zero) { Native.DeleteObject(bmp); bmp = IntPtr.Zero; }
            if (gb != null) { gb.Dispose(); gb = null; }
            if (ximg != IntPtr.Zero) { XDirect.FreeImage(ximg); ximg = IntPtr.Zero; }
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

        // Copies the pixels of src to dst here only (not in the framebuffer, which
        // must stay what the server has): what Xvnc will send once it has moved a
        // window, shown a network round trip early. The rectangles may overlap.
        public void Move(Rectangle src, Point dst)
        {
            int dx = dst.X - src.X, dy = dst.Y - src.Y;
            var all = new Rectangle(0, 0, w, h);
            var s = Rectangle.Intersect(src, all);
            var d = Rectangle.Intersect(new Rectangle(s.X + dx, s.Y + dy, s.Width, s.Height), all);
            if (d.Width <= 0 || d.Height <= 0) return;
            s = new Rectangle(d.X - dx, d.Y - dy, d.Width, d.Height);
            var buf = new int[d.Width * d.Height];
            for (int y = 0; y < s.Height; y++)
            {
                if (Native.IsWindows) Marshal.Copy(new IntPtr(bits.ToInt64() + ((long)(s.Y + y) * w + s.X) * 4), buf, y * d.Width, d.Width);
                else Array.Copy(px, (s.Y + y) * w + s.X, buf, y * d.Width, d.Width);
            }
            for (int y = 0; y < d.Height; y++)
            {
                if (Native.IsWindows) Marshal.Copy(buf, y * d.Width, new IntPtr(bits.ToInt64() + ((long)(d.Y + y) * w + d.X) * 4), d.Width);
                else Array.Copy(buf, y * d.Width, px, (d.Y + y) * w + d.X, d.Width);
            }
        }

        // The share of pixels, on every third row of r, that are the same here as
        // in the framebuffer (colour only).
        public double Same(int[] fb, int fbw, int fbh, Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, Math.Min(w, fbw), Math.Min(h, fbh)));
            if (r.Width <= 0 || r.Height <= 0) return 1;
            long same = 0, all = 0;
            var row = new int[r.Width];
            for (int y = r.Y + 1; y < r.Bottom; y += 3)
            {
                if (Native.IsWindows) Marshal.Copy(new IntPtr(bits.ToInt64() + ((long)y * w + r.X) * 4), row, 0, r.Width);
                else Array.Copy(px, y * w + r.X, row, 0, r.Width);
                int o = y * fbw + r.X;
                for (int i = 0; i < r.Width; i++) { all++; if (((fb[o + i] ^ row[i]) & 0xffffff) == 0) same++; }
            }
            return all == 0 ? 1 : (double)same / all;
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

        // Mono: fills the part of clip (client-area coordinates) outside the
        // session window's size with that window's edge carried outwards: its
        // last column to the right, its last row below, its corner pixel in
        // the corner. While a resize catches up, the border being dragged then
        // shows the window's own edge, not a grey band. xr is the session
        // window in the framebuffer. False if it cannot, and then nothing is
        // drawn.
        public bool PutEdges(IntPtr win, Rectangle xr, Rectangle clip)
        {
            if (ximg == IntPtr.Zero || win == IntPtr.Zero || xr.Width <= 0 || xr.Height <= 0) return false;
            int ex = xr.Right - 1, ey = xr.Bottom - 1;
            if (xr.X < 0 || xr.Y < 0 || ex >= w || ey >= h) return false;   // its edge is not in the framebuffer
            const int far = 1 << 20;
            var right = Rectangle.Intersect(clip, new Rectangle(xr.Width, 0, far, far));
            var below = Rectangle.Intersect(clip, new Rectangle(0, xr.Height, xr.Width, far));
            if (right.Width > 0 && right.Height > 0)
            {
                var t = Scratch(right.Width * right.Height);
                for (int y = 0; y < right.Height; y++)
                {
                    int c = px[(xr.Y + Math.Min(right.Y + y, xr.Height - 1)) * w + ex];
                    for (int x = 0, o = y * right.Width; x < right.Width; x++) t[o + x] = c;
                }
                PutScratch(win, right);
            }
            if (below.Width > 0 && below.Height > 0)
            {
                var t = Scratch(below.Width * below.Height);
                Array.Copy(px, ey * w + xr.X + below.X, t, 0, below.Width);
                for (int y = 1; y < below.Height; y++) Array.Copy(t, 0, t, y * below.Width, below.Width);
                PutScratch(win, below);
            }
            return true;
        }

        int[] scratch;
        GCHandle scratchPin;

        int[] Scratch(int n)
        {
            if (scratch == null || scratch.Length < n)
            {
                if (scratchPin.IsAllocated) scratchPin.Free();
                scratch = new int[Math.Max(n, 1 << 16)];
                scratchPin = GCHandle.Alloc(scratch, GCHandleType.Pinned);
            }
            return scratch;
        }

        void PutScratch(IntPtr win, Rectangle dst)
        {
            IntPtr img = XDirect.Image(scratchPin.AddrOfPinnedObject(), dst.Width, dst.Height);
            if (img == IntPtr.Zero) return;
            XDirect.Put(win, img, 0, 0, dst.X, dst.Y, dst.Width, dst.Height);
            XDirect.FreeImage(img);
        }

        // Mono: puts the pixels at (sx, sy) into the X window win at dst
        // (XDirect). False if it cannot, and then nothing is drawn.
        public bool Put(IntPtr win, Rectangle dst, int sx, int sy)
        {
            if (ximg == IntPtr.Zero || win == IntPtr.Zero) return false;
            // Xlib trims a source rectangle that leaves the image without
            // moving its destination, so it is trimmed here, on both sides.
            var s = Rectangle.Intersect(new Rectangle(sx, sy, dst.Width, dst.Height), new Rectangle(0, 0, w, h));
            if (s.Width > 0 && s.Height > 0)
                XDirect.Put(win, ximg, s.X, s.Y, dst.X + s.X - sx, dst.Y + s.Y - sy, s.Width, s.Height);
            return true;
        }
    }

    sealed class XForm : Form
    {
        public readonly XWin Win;
        public Rectangle XRect { get; set; } // where the window is in the session (framebuffer coordinates)
        readonly Viewer v;
        public bool ClosingFromX, Applying;
        public bool Ready;                   // placed where the session's window is: from now on, report moves
        public int PlannedX, PlannedY, FrameUsedX, FrameUsedY;   // Mono: where its client area should land, and the frame allowed for
        public bool MeasureFrame;            // Mono: learn the window manager's frame from where it lands
        public DateTime ShownAt;
        public DateTime LastSent;
        public bool InSizeMove;              // inside Windows' move/size loop (dragging the title bar or a border)
        // A move of the session's window whose pixels are still on their way:
        // until then this window shows a local copy (Mirror.Move), and the
        // framebuffer's updates inside XRect, which are from before the move,
        // are kept out of the picture.
        public bool Guarded, MoveConfirmed;
        public DateTime GuardStart, GuardUntil;
        public readonly List<Point> Requested = new List<Point>();  // moves sent to the session, not yet confirmed
        public IntPtr XClient;               // Mono: the X window of the client area, for XDirect
        // Mono: WSLg resizes a window step by step. One resize at a time goes
        // to the session: the size asked for and when (Empty: none), and the
        // newest size from here, which waits until the session confirms.
        public Size ResizeSent;
        public DateTime ResizeSentAt;
        public Rectangle? ResizeNext;
        public Size Painted;                 // Mono: the client size last painted (OnResize puts only what is new)

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
                // Frameless: splash screens, and windows that ask for no title
                // bar (Motif hints). Dialogs have no maximize button, as on Windows.
                FormBorderStyle = w.Frameless ? FormBorderStyle.None : fixedSize ? FormBorderStyle.FixedSingle : FormBorderStyle.Sizable;
                MaximizeBox = !fixedSize && !w.Dialog && !w.NoMaximize;
                MinimizeBox = !w.NoMinimize;
                if (owner != null) { Owner = owner; ShowInTaskbar = false; MinimizeBox = false; }
                var xi = w.Icon != null ? Native.MakeIcon(w.Icon) : null;
                if (xi != null) Icon = xi; else if (v.AppIcon != null) Icon = v.AppIcon;
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
                if (Win != null && Win.Popup)
                {
                    cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                    // Mono makes a WS_POPUP window without a caption override-redirect,
                    // as the session's popup is: an X window manager (WSLg's) then
                    // leaves it where it is, instead of placing it as a window.
                    if (!Native.IsWindows) cp.Style = unchecked((int)0x80000000) | (cp.Style & ~0x00CF0000);
                }
                // A frameless window as WS_POPUP: Windows gives a WS_OVERLAPPED
                // window a caption while it creates it. Keep the system menu and
                // minimize, so its taskbar button can minimize it.
                else if (Win != null && Win.Frameless) cp.Style = unchecked((int)0x80000000) | (cp.Style & ~0x00CF0000) | 0x00080000 | 0x00020000;
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
                // Windows knows the frame before the window shows. Mono under
                // an X window manager (WSLg's) does not, and reports the client
                // area's place inside the window instead: there the window
                // manager places the frame at want, and moves follow from there.
                if (!Win.Popup && (Native.IsWindows || Visible))
                {
                    var c = PointToScreen(Point.Empty);
                    Location = new Point(Location.X - (c.X - want.X), Location.Y - (c.Y - want.Y));
                }
            }
            finally { Applying = false; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (XDirect.Ok) XClient = XDirect.Window(this);
            Native.StyleFrame(Handle, Win != null && Win.Popup, false);
            // One taskbar button per program (WM_CLASS), as Windows programs
            // have, rather than every window under the viewer's.
            if (Win != null && !Win.Popup && Win.Class.Length > 0) Native.SetAppId(Handle, Win.Class);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            XClient = IntPtr.Zero;
            base.OnHandleDestroyed(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (Win.Popup) return;
            Native.StyleFrame(Handle, false, true);
            if (Form.ActiveForm == null) Native.Flash(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // WM_SETTINGCHANGE "ImmersiveColorSet": Windows switched between
            // light and dark mode.
            if (m.Msg == 0x001A && !Win.Popup && m.LParam != IntPtr.Zero && Marshal.PtrToStringUni(m.LParam) == "ImmersiveColorSet")
                Native.StyleFrame(Handle, false, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e) { v.PaintForm(this, e.Graphics, e.ClipRectangle); }

        protected override void OnMouseMove(MouseEventArgs e) { v.Pointer(this, e.X, e.Y, 0); }
        protected override void OnMouseDown(MouseEventArgs e) { v.Pointer(this, e.X, e.Y, 0); }
        protected override void OnMouseUp(MouseEventArgs e) { v.Pointer(this, e.X, e.Y, 0); }
        protected override void OnMouseWheel(MouseEventArgs e) { v.Pointer(this, e.X, e.Y, e.Delta); }

        protected override void OnActivated(EventArgs e) { base.OnActivated(e); if (!Win.Popup) v.FormActivated(this); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); v.Deactivated(); }

        protected override void OnMove(EventArgs e) { base.OnMove(e); if (!Applying && Ready) v.NativeMoved(this, false); }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // Not before Ready: while Windows creates a frameless window it
            // briefly gives it a caption at the default size and place.
            if (WindowState != FormWindowState.Minimized) v.PaintNew(this);
            if (Applying || !Ready) return;
            if (WindowState == FormWindowState.Minimized) { v.Minimized(this); return; }
            v.NativeMoved(this, false);
        }
        // WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE: a drag by the title bar or a
        // border, or the keyboard's Move and Size.
        protected override void OnResizeBegin(EventArgs e) { base.OnResizeBegin(e); InSizeMove = true; }
        protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); InSizeMove = false; if (Ready) v.NativeMoved(this, true); }

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
        // Under Mono, parts of a window that another session window covers
        // are not painted: there the framebuffer has the other window's
        // pixels, and each paint is a picture sent to this computer's X server
        // (WSLg's sends it on to Windows). On Windows a hidden paint costs
        // next to nothing, and painting there stays as it was.
        readonly bool skipCovered = !Native.IsWindows;
        readonly TextWriter log;
        long paints, lastFrames, lastBytes, lastPaints; DateTime lastStat = DateTime.Now; TimeSpan lastCpu;
        // Ends the guard of a moved window when no update comes to do it.
        readonly System.Windows.Forms.Timer guardTimer = new System.Windows.Forms.Timer { Interval = 50 };
        // Sends a waiting resize when the session has not confirmed the one before in time.
        readonly System.Windows.Forms.Timer resizeTimer = new System.Windows.Forms.Timer { Interval = 25 };
        const int ResizeWaitMs = 100;        // Mono: the longest a resize waits for the one before to be confirmed
        const int GuardMs = 300;             // the longest a moved window waits for its pixels

        public Viewer(TextWriter log)
        {
            guardTimer.Tick += delegate { CheckGuards(); };
            resizeTimer.Tick += delegate { CheckResizes(); };
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
            // The timer and Flush both call this: one right after the other
            // would give rates over a few milliseconds.
            var now = DateTime.Now; double dt = (now - lastStat).TotalSeconds;
            if (dt < 1) return;
            lastStat = now;
            long f = Rfb.Frames, b = Rfb.Bytes, p = Interlocked.Read(ref paints);
            var cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            if (Debug) { Log("tick frames " + f); lock (Rfb.FbLock) mirror.Save(Path.Combine(Path.GetTempPath(), "gtvpn-mirror.png")); }
            if (f != lastFrames)
                Log(string.Format("updates/s {0:F1}  MB/s {1:F2}  paints/s {2:F1}  cpu {3:F0}%  windows {4}", (f - lastFrames) / dt, (b - lastBytes) / dt / 1e6,
                    (p - lastPaints) / dt, (cpu - lastCpu).TotalSeconds / dt * 100, forms.Count));
            lastFrames = f; lastBytes = b; lastPaints = p; lastCpu = cpu;
            tray.Text = ("gtvpn viewer: " + Label).Substring(0, Math.Min(63, ("gtvpn viewer: " + Label).Length));
        }

        public void Quit(string why) { Quit(why, false); }

        bool quitting;

        public void Quit(string why, bool tell)
        {
            if (quitting) return;   // closing one connection closes the other too
            quitting = true;
            Log("quit: " + why);
            if (tell && Native.IsWindows)
                MessageBox.Show("The connection to the session closed (" + why + ").\n\nCadence keeps running on the server. " +
                    "Reopen its windows with:  gtvpn vnc <host> windows", "gtvpn viewer: " + Label, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            rs = Bands(rs);
            if (Debug)
            {
                var u = Rectangle.Empty; foreach (var r in rs) u = u.IsEmpty ? r : Rectangle.Union(u, r);
                Log("flush " + rs.Count + " rects in " + u + (resize ? " resize" : "") + " fb " + Rfb.Width + "x" + Rfb.Height);
            }
            lock (Rfb.FbLock)
            {
                if (resize) mirror.Resize(Rfb.Width, Rfb.Height);
                // A moved window's rectangle keeps its local copy until its
                // pixels arrive (CheckGuards): updates there are from before the move.
                var keep = new List<Rectangle>();
                if (!resize) foreach (var f in forms.Values) if (f.Guarded) keep.Add(f.XRect);
                foreach (var r in rs)
                    foreach (var p in Outside(r, keep)) mirror.Copy(Rfb.Fb, Rfb.Width, Rfb.Height, p);
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
            if (resize) foreach (var f in forms.Values) f.Guarded = false;
            bool put = false;
            foreach (var f in forms.Values)
            {
                if (f.Guarded) continue;
                // Mono: straight into the window (XDirect) while it is on the
                // screen, rather than a paint message and System.Drawing.
                bool direct = f.XClient != IntPtr.Zero && f.Visible && f.WindowState != FormWindowState.Minimized, edge = false;
                List<Rectangle> over = null;
                foreach (var r in rs)
                {
                    var i = Rectangle.Intersect(r, f.XRect);
                    if (i.Width <= 0 || i.Height <= 0) continue;
                    if (skipCovered && over == null && Wm != null) over = Wm.Over(f.Win.Id);
                    foreach (var p in over == null || over.Count == 0 ? new List<Rectangle> { i } : Outside(i, over))
                    {
                        var local = new Rectangle(p.X - f.XRect.X, p.Y - f.XRect.Y, p.Width, p.Height);
                        if (direct && mirror.Put(f.XClient, local, p.X, p.Y))
                        {
                            put = true; Interlocked.Increment(ref paints);
                            if (p.Right == f.XRect.Right || p.Bottom == f.XRect.Bottom) edge = true;
                        }
                        else f.Invalidate(local);
                        if (Debug) Log((direct ? "put " : "invalidate ") + f.Win.Id.ToString("x") + " " + p);
                    }
                }
                // While a resize catches up, the strip beyond the session's
                // window shows its edge: that follows the edge's new pixels.
                var cs = f.ClientSize;
                if (edge && (cs.Width > f.XRect.Width || cs.Height > f.XRect.Height))
                    mirror.PutEdges(f.XClient, f.XRect, new Rectangle(Point.Empty, cs));
            }
            if (put) XDirect.Flush();
            CheckGuards();
        }

        // Xvnc sends a large change as bands of at most 65536 pixels each: a
        // run of bands over the same columns becomes one rectangle again (one
        // XPutImage under Mono, rather than 15 for a 1200x800 window).
        static List<Rectangle> Bands(List<Rectangle> rs)
        {
            if (rs.Count < 2) return rs;
            var a = new List<Rectangle>(rs);
            a.Sort(delegate(Rectangle p, Rectangle q)
            {
                return p.X != q.X ? p.X.CompareTo(q.X) : p.Width != q.Width ? p.Width.CompareTo(q.Width) : p.Y.CompareTo(q.Y);
            });
            var o = new List<Rectangle>();
            var cur = a[0];
            for (int i = 1; i < a.Count; i++)
            {
                var r = a[i];
                if (r.X == cur.X && r.Width == cur.Width && r.Y <= cur.Bottom) cur = Rectangle.Union(cur, r);
                else { o.Add(cur); cur = r; }
            }
            o.Add(cur);
            return o;
        }

        // The parts of r outside every rectangle in cut.
        static List<Rectangle> Outside(Rectangle r, List<Rectangle> cut)
        {
            var pieces = new List<Rectangle> { r };
            foreach (var c in cut)
            {
                var next = new List<Rectangle>();
                foreach (var p in pieces)
                {
                    var i = Rectangle.Intersect(p, c);
                    if (i.Width <= 0 || i.Height <= 0) { next.Add(p); continue; }
                    if (i.Y > p.Y) next.Add(new Rectangle(p.X, p.Y, p.Width, i.Y - p.Y));                   // above
                    if (i.Bottom < p.Bottom) next.Add(new Rectangle(p.X, i.Bottom, p.Width, p.Bottom - i.Bottom)); // below
                    if (i.X > p.X) next.Add(new Rectangle(p.X, i.Y, i.X - p.X, i.Height));                  // left
                    if (i.Right < p.Right) next.Add(new Rectangle(i.Right, i.Y, p.Right - i.Right, i.Height)); // right
                }
                pieces = next;
            }
            return pieces;
        }

        // A pure move of the session's window to r: Xvnc copies its pixels there
        // a network round trip later. Until then the picture here gets the same
        // copy at once (so nothing flashes), and updates inside r, still from
        // before the move, stay out of it.
        void ShiftXRect(XForm f, Rectangle r)
        {
            var from = f.XRect;
            if (r == from) return;
            lock (Rfb.FbLock) mirror.Move(from, r.Location);
            f.XRect = r;
            if (!f.Guarded) f.GuardStart = DateTime.Now;
            f.Guarded = true; f.MoveConfirmed = false;
            f.GuardUntil = DateTime.Now.AddMilliseconds(GuardMs);
            guardTimer.Start();
        }

        // A moved window paints from the framebuffer again once the session has
        // confirmed the move and the framebuffer shows the window there (what
        // the local copy shows), or after GuardMs at the latest.
        void CheckGuards()
        {
            bool any = false;
            foreach (var f in forms.Values)
            {
                if (!f.Guarded) continue;
                bool late = DateTime.Now >= f.GuardUntil, landed = false;
                if (!late && f.MoveConfirmed) lock (Rfb.FbLock) landed = mirror.Same(Rfb.Fb, Rfb.Width, Rfb.Height, f.XRect) >= 0.995;
                if (!late && !landed) { any = true; continue; }
                f.Guarded = false;
                lock (Rfb.FbLock) mirror.Copy(Rfb.Fb, Rfb.Width, Rfb.Height, f.XRect);
                f.Invalidate();
                Log("moved " + f.Win.Id.ToString("x") + " to +" + f.XRect.X + "+" + f.XRect.Y + ": " +
                        (landed ? "its pixels arrived after " : "stopped waiting for its pixels after ") + (int)(DateTime.Now - f.GuardStart).TotalMilliseconds + " ms");
            }
            if (!any) guardTimer.Stop();
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
            // (The mirror is the UI thread's own: painting it takes no lock, so
            // updates go on arriving while a paint takes long.)
            if (inside.Width > 0 && inside.Height > 0 && !mirror.Put(f.XClient, inside, f.XRect.X + inside.X, f.XRect.Y + inside.Y))
                mirror.Paint(g, inside, f.XRect.X + inside.X, f.XRect.Y + inside.Y);
            f.Painted = f.ClientSize;
            if (inside == clip || mirror.PutEdges(f.XClient, f.XRect, clip)) return;
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
            if (!Native.IsWindows)
            {
                if (msg == 0x100 || msg == 0x104)
                {
                    if (vk == 0x0d || vk == 0x11 || vk == 0x12) lp &= ~0x01000000L;   // see MonoKeyDown
                    int r = MonoKeyDown(vk);
                    if (r == 1) return true;
                    if (r == 2) return false;          // not filtered, so that TranslateMessage makes its WM_CHAR
                }
                else if (msg == 0x102 || msg == 0x106) { MonoChar(m.WParam.ToInt32()); return true; }
            }
            if (msg == 0x100 || msg == 0x104) { SendKeyDown(vk, (int)((lp >> 16) & 0xff), (lp & 0x01000000) != 0); return true; }
            if (msg == 0x101 || msg == 0x105) { SendKeyUp(vk); return true; }
            return true;                                                              // WM_CHAR etc.: handled as keys
        }

        // Mono (Linux, e.g. through WSLg) has no ToUnicodeEx: it sends each
        // key press as WM_KEYDOWN, and TranslateMessage makes a WM_CHAR with
        // the character of the keyboard layout for a character key. Such a key
        // goes to the session as that character's keysym when the WM_CHAR
        // arrives; with Ctrl or Alt it goes at once as the unshifted letter or
        // digit (Control_L + c), as X programs expect. Other keys go as KeySym
        // has them, except that Mono marks Return, Ctrl and Alt as extended
        // keys too: they go as Return, Control_L and Alt_L.
        int pendingVk = -1;

        // 0: not a character key; 1: sent; 2: wait for its WM_CHAR.
        int MonoKeyDown(int vk)
        {
            pendingVk = -1;
            if (Keys2.KeySym(vk, 0, false) != 0) return 0;
            var mods = Control.ModifierKeys & (Keys.Control | Keys.Alt);
            if (mods != 0 && mods != (Keys.Control | Keys.Alt) && ((vk >= 0x41 && vk <= 0x5a) || (vk >= 0x30 && vk <= 0x39)))
            {
                uint ks;
                if (!keysDown.TryGetValue(vk, out ks))
                    keysDown[vk] = ks = vk >= 0x41 && (Control.ModifierKeys & Keys.Shift) == 0 ? (uint)(vk + 0x20) : (uint)vk;
                try { Rfb.Key(ks, true); } catch (IOException) { }
                return 1;
            }
            pendingVk = vk;
            return 2;
        }

        void MonoChar(int wp)
        {
            int k = pendingVk; pendingVk = -1;
            char c = (char)(wp & 0xffff);
            if (k < 0 || c < 0x20 || c == 0x7f) return;
            uint ks;
            if (!keysDown.TryGetValue(k, out ks)) keysDown[k] = ks = c < 0x100 ? c : 0x01000000u | c;
            if (Debug) Log("key " + k.ToString("x") + " char " + ((int)c).ToString("x") + " -> keysym " + ks.ToString("x"));
            try { Rfb.Key(ks, true); } catch (IOException) { }
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

        // The session's stacking follows Windows': where two windows overlap,
        // the framebuffer holds only the upper one's pixels, so the window in
        // front here must be in front there too (with its dialogs above it).
        public void FormActivated(XForm f)
        {
            focused = f;
            Wm.Focus(f.Win.Id, f.Win);
            foreach (var o in f.OwnedForms) { var xf = o as XForm; if (xf != null && xf.Visible) Wm.Raise(xf.Win.Id); }
        }

        public void Deactivated()
        {
            BeginInvoke(new Action(delegate { if (!(Form.ActiveForm is XForm)) { ReleaseAll(); focused = null; } }));
        }

        public void Minimized(XForm f) { Wm.Lower(f.Win.Id); }

        public void CloseRequested(XForm f) { Wm.Close(f.Win.Id); }

        public void NativeMoved(XForm f, bool final)
        {
            if (f.Win.Popup || f.WindowState == FormWindowState.Minimized) return;
            var c = f.PointToScreen(Point.Empty);
            int x = c.X - OriginX, y = c.Y - OriginY, w = f.ClientSize.Width, h = f.ClientSize.Height;
            if (!Native.IsWindows)
            {
                if (c.X < -10000 || c.Y < -10000) return;      // Mono's step off the screen while a window maps
                // Mono's first report of a new window is its frame's place, before
                // the window manager has framed it: not a move.
                if ((f.FrameUsedX | f.FrameUsedY) != 0 && x == f.PlannedX - f.FrameUsedX && y == f.PlannedY - f.FrameUsedY
                    && (DateTime.Now - f.ShownAt).TotalSeconds < 2) return;
                // Where the window manager put the new window's client area:
                // its frame, for the windows that come later. (A report at the
                // frame's own place comes before it is framed: not that one.)
                if (f.MeasureFrame)
                {
                    int fx = x - f.PlannedX + f.FrameUsedX, fy = y - f.PlannedY + f.FrameUsedY;
                    if (fx >= 0 && fx <= 64 && fy >= 0 && fy <= 96 && fx + fy > 0)
                    {
                        f.MeasureFrame = false;
                        if (fx != Wm.FrameX || fy != Wm.FrameY) { Wm.SaveFrame(fx, fy); Log("window manager's frame: client area at +" + fx + "+" + fy); }
                    }
                }
            }
            // While a window is dragged, Windows moves its picture like any
            // other window's, and the session's window stays where it is until
            // the drag ends: moving it at every step would paint the window from
            // framebuffer pixels that are a network round trip behind, so its
            // content would trail its frame. (Compared with the size last asked
            // for: dragging a maximized window out resizes it first.)
            if (w == f.Win.W && h == f.Win.H && f.InSizeMove && !final) return;
            // Resizing by a border sends at most one change per 15 ms.
            var now = DateTime.Now;
            if (!final && f.InSizeMove && (now - f.LastSent).TotalMilliseconds < 15) return;
            f.LastSent = now;
            Send(f, x, y, w, h);
        }

        // Asks the session to put the window here (client area, session coordinates).
        void Send(XForm f, int x, int y, int w, int h)
        {
            if (x == f.Win.X && y == f.Win.Y && w == f.Win.W && h == f.Win.H) { f.ResizeNext = null; return; }
            bool move = w == f.XRect.Width && h == f.XRect.Height;
            // Mono: WSLg's resize steps each come here (Windows' own loop above
            // has InSizeMove). While the session has not confirmed the size
            // asked for, the newest one waits (SendNext): asking for every step
            // had the program draw sizes already gone, and the window's content
            // trailed its border further behind.
            if (!move && !Native.IsWindows && f.ResizeSent != Size.Empty && (DateTime.Now - f.ResizeSentAt).TotalMilliseconds < ResizeWaitMs)
            {
                f.ResizeNext = new Rectangle(x, y, w, h);
                resizeTimer.Start();
                return;
            }
            f.ResizeNext = null;
            f.Win.X = x; f.Win.Y = y; f.Win.W = w; f.Win.H = h;
            if (move)
            {
                // A move: the window keeps showing what it shows (Windows moved
                // its picture), and its pixels move with it here at once.
                ShiftXRect(f, new Rectangle(x, y, w, h));
                f.Requested.Add(new Point(x, y));
                if (f.Requested.Count > 16) f.Requested.RemoveAt(0);
                Wm.Move(f.Win.Id, x, y);
            }
            else
            {
                Wm.Configure(f.Win.Id, x, y, w, h);
                if (!Native.IsWindows) { f.ResizeSent = new Size(w, h); f.ResizeSentAt = DateTime.Now; resizeTimer.Start(); }
            }
            Wm.SetMaximized(f.Win.Id, f.WindowState == FormWindowState.Maximized);
        }

        // Mono: the waiting resize, once the one before is confirmed or late.
        void SendNext(XForm f)
        {
            var next = f.ResizeNext;
            if (next == null) return;
            var n = next.Value;
            f.ResizeNext = null;
            Send(f, n.X, n.Y, n.Width, n.Height);
        }

        void CheckResizes()
        {
            bool any = false;
            foreach (var f in forms.Values)
            {
                if (f.ResizeSent == Size.Empty) continue;
                if ((DateTime.Now - f.ResizeSentAt).TotalMilliseconds < ResizeWaitMs) { any = true; continue; }
                f.ResizeSent = Size.Empty;           // not confirmed in time (the program may keep another size)
                SendNext(f);
                if (f.ResizeSent != Size.Empty) any = true;
            }
            if (!any) resizeTimer.Stop();
        }

        // Mono: right after the window grows, its new part gets the picture
        // (and the edge beyond the session's window) at once, before the X
        // server's expose comes back through the message loop.
        public void PaintNew(XForm f)
        {
            if (f.XClient == IntPtr.Zero || !f.Visible) return;
            var cs = f.ClientSize; var old = f.Painted;
            f.Painted = cs;
            if (cs.Width <= old.Width && cs.Height <= old.Height) return;
            var news = new List<Rectangle>();
            if (cs.Width > old.Width) news.Add(new Rectangle(old.Width, 0, cs.Width - old.Width, cs.Height));
            if (cs.Height > old.Height) news.Add(new Rectangle(0, old.Height, Math.Min(old.Width, cs.Width), cs.Height - old.Height));
            var xr = new Rectangle(0, 0, f.XRect.Width, f.XRect.Height);
            foreach (var r in news)              // (the mirror is the UI thread's own, as in PaintForm)
            {
                var inside = Rectangle.Intersect(r, xr);
                if (inside.Width > 0 && inside.Height > 0) mirror.Put(f.XClient, inside, f.XRect.X + inside.X, f.XRect.Y + inside.Y);
                if (inside != r) mirror.PutEdges(f.XClient, f.XRect, r);
            }
            XDirect.Flush();
        }

        // ---- the window manager's side (WM thread) ------------------------------
        void Ui(Action a)
        {
            try { BeginInvoke(new Action(delegate { try { a(); } catch (Exception e) { Log("ui: " + e); } })); }
            catch (InvalidOperationException) { }
        }

        public void Add(XWin w)
        {
            // Where the window is now: the window manager's thread keeps
            // updating w while this waits for the UI thread.
            int gx = w.X, gy = w.Y, gw = w.W, gh = w.H;
            Ui(delegate
            {
                XForm old;
                if (forms.TryGetValue(w.Id, out old)) { old.ClosingFromX = true; old.Close(); forms.Remove(w.Id); }
                XForm owner = null;
                if (w.TransientFor != 0) forms.TryGetValue(w.TransientFor, out owner);
                if (owner == null && w.Dialog && Form.ActiveForm is XForm) owner = (XForm)Form.ActiveForm;
                var f = new XForm(this, w, owner);
                forms[w.Id] = f;
                // Mono writes the window's place into its size hints (as given
                // by the user) when it creates it, and WSLg's window manager
                // puts the frame there: so it is placed before it exists, with
                // the frame's size allowed for once it is known.
                int px = gx, py = gy;
                if (!Native.IsWindows && !w.Popup && !w.Frameless)
                {
                    f.PlannedX = gx; f.PlannedY = gy; f.MeasureFrame = true;
                    f.FrameUsedX = Math.Max(0, Wm.FrameX); f.FrameUsedY = Math.Max(0, Wm.FrameY);
                    px -= f.FrameUsedX; py -= f.FrameUsedY;
                }
                if (!Native.IsWindows) f.Place(px, py, gw, gh);
                f.CreateControl();
                var h = f.Handle;
                f.Place(px, py, gw, gh);
                if (!w.Popup && Native.IsWindows)
                {
                    // Record where the native window really is (Windows may
                    // have adjusted it) and move the session's window there.
                    // (Under Mono that is known once it shows: OnMove reports it.)
                    var c = f.PointToScreen(Point.Empty);
                    int nx = c.X - OriginX, ny = c.Y - OriginY;
                    w.W = gw; w.H = gh; w.X = nx; w.Y = ny;
                    if (nx != gx || ny != gy) Wm.Move(w.Id, nx, ny);
                }
                f.Ready = true;
                f.ShownAt = DateTime.Now;
                f.Show();
                Log((w.Popup ? "popup " : "window ") + w.Id.ToString("x") + " \"" + w.Title + "\" " + w.W + "x" + w.H + "+" + w.X + "+" + w.Y
                    + (w.Class.Length > 0 ? " (" + w.Class + ")" : ""));
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

        public void Uncovered(int x, int y, int w, int h)
        {
            if (skipCovered) OnUpdated(new List<Rectangle> { new Rectangle(x, y, w, h) });
        }

        public void SetIcon(uint id, uint[] icon)
        {
            Ui(delegate { XForm f; if (forms.TryGetValue(id, out f)) { var i = Native.MakeIcon(icon); if (i != null) f.Icon = i; } });
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
                bool confirmed = f.ResizeSent != Size.Empty && r.Size == f.ResizeSent;
                Geometry(f, r);
                // The session has the size asked for: the newest one goes next.
                if (confirmed) { f.ResizeSent = Size.Empty; SendNext(f); }
            });
        }

        void Geometry(XForm f, Rectangle r)
        {
            // The session confirms a move made from here: the latest one
            // lets the window take the framebuffer's pixels again once they
            // show it there (CheckGuards); an older one, which arrives while
            // the window has moved on, changes nothing.
            int k = r.Size == f.XRect.Size ? f.Requested.IndexOf(r.Location) : -1;
            if (k >= 0)
            {
                if (k == f.Requested.Count - 1) f.MoveConfirmed = true;
                f.Requested.RemoveRange(0, k + 1);
                return;
            }
            if (r == f.XRect) return;
            // The program moved its own window: its pixels follow the same way.
            if (r.Size == f.XRect.Size) { ShiftXRect(f, r); f.MoveConfirmed = true; return; }
            f.XRect = r;
            f.Invalidate();
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

        public void OnClosed(string why) { Ui(delegate { Quit(why, true); }); }

        // ---- clipboard: Windows <-> the session (text) ------------------------
        string fromSession;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (Native.IsWindows) Native.AddClipboardFormatListener(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x031D || Rfb == null) return;                 // WM_CLIPBOARDUPDATE
            string t = null;
            try { if (Clipboard.ContainsText()) t = Clipboard.GetText(); } catch (ExternalException) { }
            if (string.IsNullOrEmpty(t) || t == fromSession) return;
            try { Rfb.CutText(t); } catch (IOException) { }
        }

        public void OnCutText(string text)
        {
            Ui(delegate
            {
                if (string.IsNullOrEmpty(text)) return;
                fromSession = text;
                try { Clipboard.SetText(text); } catch (ExternalException) { }
            });
        }

        public void Replaced() { Ui(delegate { Quit("another window manager took over the session"); }); }
    }
}
