// A stand-in for WSLg's X window manager (weston's xwm), for run-vnc.sh.
//
// It frames each window that maps (the client area 6,27 inside the frame)
// and, as weston's xwm does:
//  - puts a new window's frame where the window asked to be;
//  - takes a window's own move (ConfigureRequest x,y) as the frame's place,
//    and answers it with a synthetic ConfigureNotify;
//  - after a drag that Windows showed by itself (WSLg's "local move"), moves
//    the frame and tells the window nothing: no ConfigureNotify at all; or,
//    with a fourth value of 1, a synthetic ConfigureNotify as at a drop
//    (which Mono, with a button still down, takes for the start of a drag).
// That last one is asked for through the root window's _FAKEWM_MOVE
// property (CARDINAL: client window, x, y of its client area[, notify]):
//   xprop -root -f _FAKEWM_MOVE 32c -set _FAKEWM_MOVE "<window>, <x>, <y>"
//
//   mcs -out:fakewm.exe tests/lab/fakewm.cs && DISPLAY=:N mono fakewm.exe
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

static class FakeWm
{
    const string X = "libX11.so.6";
    [DllImport(X)] static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(X)] static extern IntPtr XDefaultRootWindow(IntPtr dpy);
    [DllImport(X)] static extern int XSelectInput(IntPtr dpy, IntPtr w, IntPtr mask);
    [DllImport(X)] static extern int XSync(IntPtr dpy, bool discard);
    [DllImport(X)] static extern int XFlush(IntPtr dpy);
    [DllImport(X)] static extern int XNextEvent(IntPtr dpy, IntPtr ev);
    [DllImport(X)] static extern int XGetGeometry(IntPtr dpy, IntPtr d, out IntPtr root, out int x, out int y, out uint w, out uint h, out uint bw, out uint depth);
    [DllImport(X)] static extern IntPtr XCreateSimpleWindow(IntPtr dpy, IntPtr parent, int x, int y, uint w, uint h, uint bw, ulong border, ulong bg);
    [DllImport(X)] static extern int XReparentWindow(IntPtr dpy, IntPtr w, IntPtr parent, int x, int y);
    [DllImport(X)] static extern int XMapWindow(IntPtr dpy, IntPtr w);
    [DllImport(X)] static extern int XUnmapWindow(IntPtr dpy, IntPtr w);
    [DllImport(X)] static extern int XMoveWindow(IntPtr dpy, IntPtr w, int x, int y);
    [DllImport(X)] static extern int XResizeWindow(IntPtr dpy, IntPtr w, uint width, uint height);
    [DllImport(X)] static extern int XConfigureWindow(IntPtr dpy, IntPtr w, uint mask, IntPtr changes);
    [DllImport(X)] static extern int XDestroyWindow(IntPtr dpy, IntPtr w);
    [DllImport(X)] static extern int XAddToSaveSet(IntPtr dpy, IntPtr w);
    [DllImport(X)] static extern int XSendEvent(IntPtr dpy, IntPtr w, bool propagate, IntPtr mask, IntPtr ev);
    [DllImport(X)] static extern IntPtr XInternAtom(IntPtr dpy, string name, bool onlyIfExists);
    [DllImport(X)] static extern int XGetWindowProperty(IntPtr dpy, IntPtr w, IntPtr prop, IntPtr offset, IntPtr length, bool delete,
        IntPtr type, out IntPtr actualType, out int format, out IntPtr n, out IntPtr after, out IntPtr data);
    [DllImport(X)] static extern int XFree(IntPtr p);
    delegate int ErrorHandler(IntPtr dpy, IntPtr ev);
    [DllImport(X)] static extern IntPtr XSetErrorHandler(ErrorHandler h);

    const int L = 6, T = 27, R = 6, B = 6;              // the frame around the client area
    static IntPtr dpy, root;
    static readonly Dictionary<IntPtr, IntPtr> frameOf = new Dictionary<IntPtr, IntPtr>();
    static bool otherWm;
    static readonly ErrorHandler onError = delegate(IntPtr d, IntPtr e)
    {
        if (Marshal.ReadByte(e, 32) == 10) otherWm = true;   // BadAccess: another window manager
        return 0;                                             // a window gone meanwhile (BadWindow): fine
    };

    static int Main()
    {
        dpy = XOpenDisplay(IntPtr.Zero);
        if (dpy == IntPtr.Zero) { Console.Error.WriteLine("fakewm: no display"); return 1; }
        root = XDefaultRootWindow(dpy);
        XSetErrorHandler(onError);
        // SubstructureRedirect | SubstructureNotify | PropertyChange
        XSelectInput(dpy, root, new IntPtr((1 << 20) | (1 << 19) | (1 << 22)));
        XSync(dpy, false);
        if (otherWm) { Console.Error.WriteLine("fakewm: another window manager runs"); return 1; }
        var move = XInternAtom(dpy, "_FAKEWM_MOVE", false);
        Console.Error.WriteLine("fakewm: running");
        var ev = Marshal.AllocHGlobal(192);
        while (true)
        {
            XNextEvent(dpy, ev);
            int type = Marshal.ReadInt32(ev, 0);
            switch (type)
            {
                case 20: Map(Marshal.ReadIntPtr(ev, 40)); break;                    // MapRequest
                case 23: Configure(ev); break;                                      // ConfigureRequest
                case 18:                                                            // UnmapNotify
                {
                    IntPtr f, w = Marshal.ReadIntPtr(ev, 40);
                    if (Marshal.ReadIntPtr(ev, 32) != root && frameOf.TryGetValue(w, out f)) XUnmapWindow(dpy, f);
                    break;
                }
                case 17:                                                            // DestroyNotify
                {
                    IntPtr f, w = Marshal.ReadIntPtr(ev, 40);
                    if (frameOf.TryGetValue(w, out f)) { frameOf.Remove(w); XDestroyWindow(dpy, f); }
                    break;
                }
                case 28:                                                            // PropertyNotify
                    if (Marshal.ReadIntPtr(ev, 32) == root && Marshal.ReadIntPtr(ev, 40) == move && Marshal.ReadInt32(ev, 56) == 0) LocalMove(move);
                    break;
            }
            XFlush(dpy);
        }
    }

    static void Map(IntPtr w)
    {
        IntPtr f;
        if (!frameOf.TryGetValue(w, out f))
        {
            IntPtr r; int x, y; uint cw, ch, bw, depth;
            if (XGetGeometry(dpy, w, out r, out x, out y, out cw, out ch, out bw, out depth) == 0) return;
            f = XCreateSimpleWindow(dpy, root, x, y, cw + L + R, ch + T + B, 0, 0, 0x707070);
            // SubstructureRedirect | SubstructureNotify: the client's own requests come here
            XSelectInput(dpy, f, new IntPtr((1 << 20) | (1 << 19)));
            XAddToSaveSet(dpy, w);
            XReparentWindow(dpy, w, f, L, T);
            frameOf[w] = f;
        }
        XMapWindow(dpy, w);
        XMapWindow(dpy, f);
    }

    static void Configure(IntPtr ev)
    {
        IntPtr w = Marshal.ReadIntPtr(ev, 40), f;
        int x = Marshal.ReadInt32(ev, 48), y = Marshal.ReadInt32(ev, 52);
        int width = Marshal.ReadInt32(ev, 56), height = Marshal.ReadInt32(ev, 60);
        long mask = Marshal.ReadInt64(ev, 88);
        if (!frameOf.TryGetValue(w, out f))
        {
            // Not framed (yet): as asked. XWindowChanges: x y width height border sibling stack_mode.
            var ch = Marshal.AllocHGlobal(40);
            Marshal.WriteInt32(ch, 0, x); Marshal.WriteInt32(ch, 4, y); Marshal.WriteInt32(ch, 8, width); Marshal.WriteInt32(ch, 12, height);
            Marshal.WriteInt32(ch, 16, Marshal.ReadInt32(ev, 64)); Marshal.WriteIntPtr(ch, 24, Marshal.ReadIntPtr(ev, 72)); Marshal.WriteInt32(ch, 32, Marshal.ReadInt32(ev, 80));
            XConfigureWindow(dpy, w, (uint)(mask & 0x7f), ch);
            Marshal.FreeHGlobal(ch);
            return;
        }
        IntPtr r; int fx, fy, cx, cy; uint fw, fh, cw, chh, bw, depth;
        XGetGeometry(dpy, f, out r, out fx, out fy, out fw, out fh, out bw, out depth);
        XGetGeometry(dpy, w, out r, out cx, out cy, out cw, out chh, out bw, out depth);
        if ((mask & 1) != 0) fx = x;                    // the window's own x, y: the frame's place
        if ((mask & 2) != 0) fy = y;
        if ((mask & 4) != 0) cw = (uint)Math.Max(1, width);
        if ((mask & 8) != 0) chh = (uint)Math.Max(1, height);
        XMoveWindow(dpy, f, fx, fy);
        XResizeWindow(dpy, f, cw + L + R, chh + T + B);
        XResizeWindow(dpy, w, cw, chh);
        Notify(w, fx + L, fy + T, (int)cw, (int)chh);
    }

    // The synthetic ConfigureNotify weston's xwm sends (the client area's place).
    static void Notify(IntPtr w, int x, int y, int cw, int ch)
    {
        var n = Marshal.AllocHGlobal(192);
        for (int i = 0; i < 192; i += 8) Marshal.WriteInt64(n, i, 0);
        Marshal.WriteInt32(n, 0, 22);
        Marshal.WriteIntPtr(n, 32, w); Marshal.WriteIntPtr(n, 40, w);
        Marshal.WriteInt32(n, 48, x); Marshal.WriteInt32(n, 52, y);
        Marshal.WriteInt32(n, 56, cw); Marshal.WriteInt32(n, 60, ch);
        XSendEvent(dpy, w, false, new IntPtr(1 << 17), n);
        Marshal.FreeHGlobal(n);
    }

    static void LocalMove(IntPtr move)
    {
        IntPtr type, n, after, data; int format;
        if (XGetWindowProperty(dpy, root, move, IntPtr.Zero, new IntPtr(4), false, IntPtr.Zero, out type, out format, out n, out after, out data) != 0) return;
        if (data == IntPtr.Zero) return;
        // Format 32 comes as longs.
        long count = n.ToInt64();
        if (format == 32 && (count == 3 || count == 4))
        {
            IntPtr f, w = new IntPtr(Marshal.ReadInt64(data, 0));
            int x = (int)Marshal.ReadInt64(data, 8), y = (int)Marshal.ReadInt64(data, 16);
            bool notify = count == 4 && Marshal.ReadInt64(data, 24) != 0;
            if (frameOf.TryGetValue(w, out f))
            {
                XMoveWindow(dpy, f, x - L, y - T);
                if (notify)
                {
                    IntPtr r; int cx, cy; uint cw, ch, bw, depth;
                    XGetGeometry(dpy, w, out r, out cx, out cy, out cw, out ch, out bw, out depth);
                    Notify(w, x, y, (int)cw, (int)ch);
                }
            }
            Console.Error.WriteLine("fakewm: local move of 0x" + w.ToInt64().ToString("x") + " to +" + x + "+" + y + (notify ? ", announced" : "") + (f == IntPtr.Zero ? " (not framed)" : ""));
        }
        XFree(data);
    }
}
