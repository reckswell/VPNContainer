// A small X11 client that is the window manager of the gtvpn vnc session,
// so that each of its top-level windows can be shown as a separate native
// window here. It speaks the X protocol over the forwarded display socket
// (with the session's MIT-MAGIC-COOKIE-1), never reparents and draws no
// frames: the native windows have the frames. It keeps the session's
// window positions equal to the native ones (X root = this computer's
// virtual screen), so menus and dialogs open where they belong.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace GtVpn
{
    sealed class XError : Exception
    {
        public readonly int Code, Major;
        public XError(int code, int major) : base("X error " + code + " (request " + major + ")") { Code = code; Major = major; }
    }

    sealed class XConn
    {
        TcpClient tcp; Stream s;
        readonly object wlock = new object();
        int seq;
        sealed class Pending { public readonly ManualResetEvent Done = new ManualResetEvent(false); public byte[] Reply; public XError Error; }
        readonly Dictionary<int, Pending> pending = new Dictionary<int, Pending>();
        public readonly BlockingCollection<byte[]> Events = new BlockingCollection<byte[]>();
        public uint Root, IdBase, IdMask; uint nextId;
        public int ScreenW, ScreenH;
        public event Action<string> Closed;
        public event Action<XError, int> AsyncError;   // error, seq

        public void Connect(string host, int port, byte[] cookie)
        {
            tcp = new TcpClient(); tcp.NoDelay = true; tcp.Connect(host, port);
            s = tcp.GetStream();
            var name = Encoding.ASCII.GetBytes("MIT-MAGIC-COOKIE-1");
            var b = Req.Setup();
            b.U16(11); b.U16(0); b.U16(name.Length); b.U16(cookie.Length); b.U16(0);
            b.Bytes(name); b.Pad(); b.Bytes(cookie); b.Pad();
            var bytes = b.Raw.ToArray();
            s.Write(bytes, 0, bytes.Length);
            var head = ReadN(8);
            int extra = (head[6] | (head[7] << 8)) * 4;
            var body = ReadN(extra);
            if (head[0] != 1)
            {
                int rl = head[1];
                throw new IOException("the session's X server refused the connection: " + Encoding.ASCII.GetString(body, 0, Math.Min(rl, body.Length)));
            }
            IdBase = U32(body, 4); IdMask = U32(body, 8);
            int vlen = U16(body, 16), nscreens = body[20], nformats = body[21];
            int o = 32 + ((vlen + 3) & ~3) + 8 * nformats;
            if (nscreens < 1) throw new IOException("the session's X server has no screen");
            Root = U32(body, o); ScreenW = U16(body, o + 20); ScreenH = U16(body, o + 22);
            var t = new Thread(ReadLoop) { IsBackground = true, Name = "x11" };
            t.Start();
        }

        public uint NewId() { return IdBase | (nextId++ & IdMask); }
        public void Close() { try { tcp.Close(); } catch { } }

        // Sends a request; with wantReply, returns a handle to wait on.
        Pending Send(Req r, bool wantReply, out int sq)
        {
            var bytes = r.Build();
            Pending p = null;
            lock (wlock)
            {
                sq = ++seq & 0xffff;
                if (wantReply)
                {
                    p = new Pending();
                    lock (pending) pending[sq] = p;
                }
                s.Write(bytes, 0, bytes.Length);
            }
            return p;
        }

        public void Do(Req r) { int sq; Send(r, false, out sq); }

        public byte[] Call(Req r)
        {
            int sq; var p = Send(r, true, out sq);
            if (!p.Done.WaitOne(15000)) throw new IOException("the session's X server stopped answering");
            if (p.Error != null) throw p.Error;
            return p.Reply;
        }

        // Many requests at once (one round trip); null where one failed.
        public byte[][] CallAll(IList<Req> rs)
        {
            var ps = new Pending[rs.Count];
            for (int i = 0; i < rs.Count; i++) { int sq; ps[i] = Send(rs[i], true, out sq); }
            var res = new byte[rs.Count][];
            for (int i = 0; i < rs.Count; i++)
            {
                if (!ps[i].Done.WaitOne(15000)) throw new IOException("the session's X server stopped answering");
                res[i] = ps[i].Error == null ? ps[i].Reply : null;
            }
            return res;
        }

        // Runs a request without a reply and reports whether it failed
        // (GetInputFocus afterwards as the fence).
        public XError Check(Req r)
        {
            int sq;
            var p = new Pending();
            lock (wlock)
            {
                var bytes = r.Build();
                sq = ++seq & 0xffff;
                lock (pending) pending[sq] = p;
                s.Write(bytes, 0, bytes.Length);
            }
            Call(new Req(43));
            lock (pending) pending.Remove(sq);
            return p.Error;
        }

        void ReadLoop()
        {
            string why = "the session's X connection closed";
            try
            {
                while (true)
                {
                    var b = ReadN(32);
                    int code = b[0] & 0x7f;
                    int sq = U16(b, 2);
                    if (code == 1)
                    {
                        int extra = (int)U32(b, 4) * 4;
                        byte[] full = b;
                        if (extra > 0) { full = new byte[32 + extra]; Buffer.BlockCopy(b, 0, full, 0, 32); var e = ReadN(extra); Buffer.BlockCopy(e, 0, full, 32, extra); }
                        Pending p; lock (pending) { if (pending.TryGetValue(sq, out p)) pending.Remove(sq); }
                        if (p != null) { p.Reply = full; p.Done.Set(); }
                    }
                    else if (code == 0)
                    {
                        var err = new XError(b[1], b[10]);
                        Pending p; lock (pending) { if (pending.TryGetValue(sq, out p)) pending.Remove(sq); }
                        if (p != null) { p.Error = err; p.Done.Set(); }
                        else { var ae = AsyncError; if (ae != null) ae(err, sq); }
                    }
                    else Events.Add(b);
                }
            }
            catch (Exception e) { why = e.Message; }
            Events.CompleteAdding();
            lock (pending) foreach (var p in pending.Values) { p.Error = new XError(-1, 0); p.Done.Set(); }
            var c = Closed; if (c != null) c(why);
        }

        byte[] ReadN(int n)
        {
            var b = new byte[n]; int o = 0;
            while (o < n) { int k = s.Read(b, o, n - o); if (k <= 0) throw new IOException("the session's X connection closed"); o += k; }
            return b;
        }

        public static int U16(byte[] b, int o) { return b[o] | (b[o + 1] << 8); }
        public static short S16(byte[] b, int o) { return (short)(b[o] | (b[o + 1] << 8)); }
        public static uint U32(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }
    }

    // An X request, little endian; Build() fills in the length.
    sealed class Req
    {
        public readonly MemoryStream Raw = new MemoryStream();
        public Req(int opcode, int data = 0) { U8(opcode); U8(data); U16(0); }
        Req() { }
        // The connection setup: byte order 'l' (little endian), then its fields.
        public static Req Setup() { var r = new Req(); r.U8(0x6c); r.U8(0); return r; }
        public Req U8(int v) { Raw.WriteByte((byte)v); return this; }
        public Req U16(int v) { U8(v); U8(v >> 8); return this; }
        public Req U32(uint v) { U16((int)(v & 0xffff)); U16((int)(v >> 16)); return this; }
        public Req I32(int v) { return U32((uint)v); }
        public Req Bytes(byte[] b) { Raw.Write(b, 0, b.Length); return this; }
        public Req Pad() { while (Raw.Length % 4 != 0) Raw.WriteByte(0); return this; }
        public byte[] Build()
        {
            Pad();
            var b = Raw.ToArray();
            int n = b.Length / 4;
            b[2] = (byte)n; b[3] = (byte)(n >> 8);
            return b;
        }
    }

    // What the window manager tells the viewer (all on the WM thread).
    interface IWmSink
    {
        void Add(XWin w);                     // a window to show (managed or popup)
        void Remove(uint id);
        void Title(uint id, string title);
        void MoveResize(uint id, int x, int y, int w, int h);  // the client moved or resized it
        void XGeometry(uint id, int x, int y, int w, int h);   // where it is in the session now
        void Activate(uint id);
        void SetState(uint id, bool maximize, bool minimize);
        void Replaced();                      // another window manager took over
    }

    sealed class XWin
    {
        public uint Id;
        public bool Popup;                    // override-redirect: menus, tooltips
        public int X, Y, W, H;
        public string Title = "";
        public uint TransientFor;
        public int MinW, MinH, MaxW, MaxH;
        public bool TakeFocus, DeleteWindow, InputHint = true;
        public bool Dialog;
    }

    sealed class Wm
    {
        readonly XConn x;
        readonly IWmSink sink;
        public int OriginX, OriginY;           // the virtual screen's corner, in screen coordinates
        public System.Drawing.Rectangle WorkArea; // primary screen work area, X coordinates
        readonly Dictionary<uint, XWin> wins = new Dictionary<uint, XWin>();   // shown windows
        readonly Dictionary<uint, int[]> geo = new Dictionary<uint, int[]>(); // every root child: x y w h override
        uint me;
        uint A_WM_PROTOCOLS, A_WM_DELETE_WINDOW, A_WM_TAKE_FOCUS, A_WM_STATE, A_WM_CHANGE_STATE, A_NET_WM_NAME, A_UTF8_STRING,
             A_NET_SUPPORTED, A_NET_SUPPORTING_WM_CHECK, A_NET_ACTIVE_WINDOW, A_NET_WM_STATE, A_NET_WM_STATE_MAXIMIZED_VERT,
             A_NET_WM_STATE_MAXIMIZED_HORZ, A_NET_WM_STATE_HIDDEN, A_NET_FRAME_EXTENTS, A_NET_REQUEST_FRAME_EXTENTS,
             A_NET_WM_WINDOW_TYPE, A_NET_WM_WINDOW_TYPE_DIALOG, A_NET_CLIENT_LIST, A_NET_MOVERESIZE_WINDOW, A_NET_CLOSE_WINDOW, A_WM_S0;
        const uint WM_NAME = 39, WM_TRANSIENT_FOR = 68, WM_NORMAL_HINTS = 40, WM_HINTS = 35, ATOM = 4, CARDINAL = 6, WINDOW = 33, STRING = 31;

        public Wm(XConn x, IWmSink sink) { this.x = x; this.sink = sink; }

        uint Atom(string name)
        {
            var b = Encoding.ASCII.GetBytes(name);
            var r = x.Call(new Req(16, 0).U16(b.Length).U16(0).Bytes(b));
            return XConn.U32(r, 8);
        }

        public void Start()
        {
            string[] names = { "WM_PROTOCOLS", "WM_DELETE_WINDOW", "WM_TAKE_FOCUS", "WM_STATE", "WM_CHANGE_STATE", "_NET_WM_NAME", "UTF8_STRING",
                "_NET_SUPPORTED", "_NET_SUPPORTING_WM_CHECK", "_NET_ACTIVE_WINDOW", "_NET_WM_STATE", "_NET_WM_STATE_MAXIMIZED_VERT",
                "_NET_WM_STATE_MAXIMIZED_HORZ", "_NET_WM_STATE_HIDDEN", "_NET_FRAME_EXTENTS", "_NET_REQUEST_FRAME_EXTENTS",
                "_NET_WM_WINDOW_TYPE", "_NET_WM_WINDOW_TYPE_DIALOG", "_NET_CLIENT_LIST", "_NET_MOVERESIZE_WINDOW", "_NET_CLOSE_WINDOW", "WM_S0" };
            var reqs = new List<Req>();
            foreach (var n in names) { var b = Encoding.ASCII.GetBytes(n); reqs.Add(new Req(16, 0).U16(b.Length).U16(0).Bytes(b)); }
            var res = x.CallAll(reqs);
            var a = new uint[names.Length];
            for (int i = 0; i < a.Length; i++) a[i] = XConn.U32(res[i], 8);
            int k = 0;
            A_WM_PROTOCOLS = a[k++]; A_WM_DELETE_WINDOW = a[k++]; A_WM_TAKE_FOCUS = a[k++]; A_WM_STATE = a[k++]; A_WM_CHANGE_STATE = a[k++];
            A_NET_WM_NAME = a[k++]; A_UTF8_STRING = a[k++]; A_NET_SUPPORTED = a[k++]; A_NET_SUPPORTING_WM_CHECK = a[k++];
            A_NET_ACTIVE_WINDOW = a[k++]; A_NET_WM_STATE = a[k++]; A_NET_WM_STATE_MAXIMIZED_VERT = a[k++]; A_NET_WM_STATE_MAXIMIZED_HORZ = a[k++];
            A_NET_WM_STATE_HIDDEN = a[k++]; A_NET_FRAME_EXTENTS = a[k++]; A_NET_REQUEST_FRAME_EXTENTS = a[k++]; A_NET_WM_WINDOW_TYPE = a[k++];
            A_NET_WM_WINDOW_TYPE_DIALOG = a[k++]; A_NET_CLIENT_LIST = a[k++]; A_NET_MOVERESIZE_WINDOW = a[k++]; A_NET_CLOSE_WINDOW = a[k++]; A_WM_S0 = a[k++];

            // Our own (invisible) window: the WM_S0 owner and EWMH check window.
            me = x.NewId();
            x.Do(new Req(1, 0).U32(me).U32(x.Root).U16(unchecked((ushort)-100)).U16(unchecked((ushort)-100)).U16(1).U16(1).U16(0).U16(2).U32(0).U32(0));
            // Take over from the session's window manager (metacity): owning
            // WM_S0 tells it to leave; then the root's redirect is ours.
            x.Do(new Req(22).U32(me).U32(A_WM_S0).U32(0));
            XError err = null;
            for (int i = 0; i < 50; i++)
            {
                err = x.Check(new Req(2).U32(x.Root).U32(0x800).U32(0x100000 | 0x80000 | 0x400000));
                if (err == null) break;
                Thread.Sleep(100);
            }
            if (err != null) throw new IOException("another window manager keeps running in the session");
            SetCardinals(x.Root, A_NET_SUPPORTING_WM_CHECK, WINDOW, me);
            SetCardinals(me, A_NET_SUPPORTING_WM_CHECK, WINDOW, me);
            SetUtf8(me, A_NET_WM_NAME, "gtvpn");
            SetCardinals(x.Root, A_NET_SUPPORTED, ATOM, A_NET_WM_NAME, A_NET_ACTIVE_WINDOW, A_NET_WM_STATE, A_NET_WM_STATE_MAXIMIZED_VERT,
                A_NET_WM_STATE_MAXIMIZED_HORZ, A_NET_WM_STATE_HIDDEN, A_NET_FRAME_EXTENTS, A_NET_REQUEST_FRAME_EXTENTS, A_NET_CLIENT_LIST,
                A_NET_MOVERESIZE_WINDOW, A_NET_CLOSE_WINDOW, A_NET_SUPPORTING_WM_CHECK);
            // Windows that are already there (metacity has given them back).
            var tree = x.Call(new Req(15).U32(x.Root));
            int nkids = XConn.U16(tree, 16);
            for (int i = 0; i < nkids; i++)
            {
                uint w = XConn.U32(tree, 32 + 4 * i);
                if (w == me) continue;
                byte[] attr, g;
                try { attr = x.Call(new Req(3).U32(w)); g = x.Call(new Req(14).U32(w)); }
                catch (XError) { continue; }
                bool over = attr[27] != 0;
                geo[w] = new[] { (int)XConn.S16(g, 12), (int)XConn.S16(g, 14), XConn.U16(g, 16), XConn.U16(g, 18), over ? 1 : 0 };
                if (attr[26] != 2) continue;      // not viewable
                if (over) ShowPopup(w); else Manage(w, true);
            }
            var t = new Thread(EventLoop) { IsBackground = true, Name = "wm" };
            t.Start();
        }

        void EventLoop()
        {
            try
            {
                foreach (var e in x.Events.GetConsumingEnumerable())
                {
                    try { lock (wins) Handle(e); }
                    catch (XError) { }  // the window went away meanwhile
                }
            }
            catch (InvalidOperationException) { }
        }

        void Handle(byte[] e)
        {
            int code = e[0] & 0x7f;
            switch (code)
            {
                case 16: // CreateNotify
                {
                    uint w = XConn.U32(e, 8);
                    geo[w] = new[] { (int)XConn.S16(e, 12), (int)XConn.S16(e, 14), XConn.U16(e, 16), XConn.U16(e, 18), e[22] };
                    break;
                }
                case 17: // DestroyNotify
                {
                    uint w = XConn.U32(e, 8);
                    geo.Remove(w);
                    if (wins.Remove(w)) { sink.Remove(w); ClientList(); }
                    break;
                }
                case 18: // UnmapNotify
                {
                    uint w = XConn.U32(e, 8);
                    XWin xw;
                    if (wins.TryGetValue(w, out xw))
                    {
                        wins.Remove(w); sink.Remove(w);
                        if (!xw.Popup) { SetCardinals(w, A_WM_STATE, A_WM_STATE, 0, 0); ClientList(); }
                    }
                    break;
                }
                case 19: // MapNotify
                {
                    uint ev = XConn.U32(e, 4), w = XConn.U32(e, 8);
                    if (ev == x.Root && e[12] != 0 && !wins.ContainsKey(w) && w != me) ShowPopup(w);
                    break;
                }
                case 20: // MapRequest
                {
                    uint w = XConn.U32(e, 8);
                    if (!wins.ContainsKey(w)) Manage(w, false);
                    else x.Do(new Req(8).U32(w));
                    break;
                }
                case 22: // ConfigureNotify
                {
                    uint ev = XConn.U32(e, 4), w = XConn.U32(e, 8);
                    if (ev != x.Root) break;
                    int gx = XConn.S16(e, 16), gy = XConn.S16(e, 18), gw = XConn.U16(e, 20), gh = XConn.U16(e, 22);
                    geo[w] = new[] { gx, gy, gw, gh, e[26] };
                    XWin xw;
                    if (wins.TryGetValue(w, out xw))
                    {
                        xw.X = gx; xw.Y = gy; xw.W = gw; xw.H = gh;
                        sink.XGeometry(w, gx, gy, gw, gh);
                        if (xw.Popup) sink.MoveResize(w, gx, gy, gw, gh);
                    }
                    break;
                }
                case 23: // ConfigureRequest
                {
                    uint w = XConn.U32(e, 8);
                    int mask = XConn.U16(e, 26);
                    var r = new Req(12).U32(w).U16(mask & 0x7f).U16(0);
                    int[] g; geo.TryGetValue(w, out g);
                    int nx = g != null ? g[0] : 0, ny = g != null ? g[1] : 0, nw = g != null ? g[2] : 1, nh = g != null ? g[3] : 1;
                    if ((mask & 1) != 0) { nx = XConn.S16(e, 16); r.I32(nx); }
                    if ((mask & 2) != 0) { ny = XConn.S16(e, 18); r.I32(ny); }
                    if ((mask & 4) != 0) { nw = XConn.U16(e, 20); r.U32((uint)nw); }
                    if ((mask & 8) != 0) { nh = XConn.U16(e, 22); r.U32((uint)nh); }
                    if ((mask & 16) != 0) r.U32((uint)XConn.U16(e, 24));
                    if ((mask & 32) != 0) r.U32(XConn.U32(e, 12));
                    if ((mask & 64) != 0) r.U32(e[1]);
                    x.Do(r);
                    XWin xw;
                    if (wins.TryGetValue(w, out xw) && !xw.Popup && (mask & 15) != 0) sink.MoveResize(w, nx, ny, nw, nh);
                    break;
                }
                case 28: // PropertyNotify
                {
                    uint w = XConn.U32(e, 4), atom = XConn.U32(e, 8);
                    XWin xw;
                    if (!wins.TryGetValue(w, out xw)) break;
                    if (atom == A_NET_WM_NAME || atom == WM_NAME) { xw.Title = GetTitle(w); sink.Title(w, xw.Title); }
                    break;
                }
                case 29: // SelectionClear: another window manager (metacity, or a
                         // second viewer) took the session over
                    if (XConn.U32(e, 12) == A_WM_S0) sink.Replaced();
                    break;
                case 33: // ClientMessage
                {
                    uint w = XConn.U32(e, 4), type = XConn.U32(e, 8);
                    if (type == A_NET_ACTIVE_WINDOW && wins.ContainsKey(w)) sink.Activate(w);
                    else if (type == A_NET_REQUEST_FRAME_EXTENTS) SetCardinals(w, A_NET_FRAME_EXTENTS, CARDINAL, 0, 0, 0, 0);
                    else if (type == A_NET_CLOSE_WINDOW && wins.ContainsKey(w)) Close(w);
                    else if (type == A_WM_CHANGE_STATE && XConn.U32(e, 12) == 3 && wins.ContainsKey(w)) sink.SetState(w, false, true);
                    else if (type == A_NET_WM_STATE && wins.ContainsKey(w))
                    {
                        uint action = XConn.U32(e, 12), p1 = XConn.U32(e, 16), p2 = XConn.U32(e, 20);
                        bool max = p1 == A_NET_WM_STATE_MAXIMIZED_VERT || p1 == A_NET_WM_STATE_MAXIMIZED_HORZ || p2 == A_NET_WM_STATE_MAXIMIZED_HORZ || p2 == A_NET_WM_STATE_MAXIMIZED_VERT;
                        if (max) sink.SetState(w, action != 0, false);
                    }
                    else if (type == A_NET_MOVERESIZE_WINDOW && wins.ContainsKey(w))
                    {
                        uint fl = XConn.U32(e, 12);
                        int[] g; geo.TryGetValue(w, out g); if (g == null) break;
                        int nx = (fl & 0x100) != 0 ? (int)XConn.U32(e, 16) : g[0], ny = (fl & 0x200) != 0 ? (int)XConn.U32(e, 20) : g[1];
                        int nw = (fl & 0x400) != 0 ? (int)XConn.U32(e, 24) : g[2], nh = (fl & 0x800) != 0 ? (int)XConn.U32(e, 28) : g[3];
                        Configure(w, nx, ny, nw, nh); sink.MoveResize(w, nx, ny, nw, nh);
                    }
                    break;
                }
            }
        }

        // ---- windows --------------------------------------------------------
        void Manage(uint w, bool mapped)
        {
            var reqs = new List<Req> {
                new Req(14).U32(w),                                                         // geometry
                GetProp(w, A_NET_WM_NAME, A_UTF8_STRING), GetProp(w, WM_NAME, 0),
                GetProp(w, WM_TRANSIENT_FOR, WINDOW), GetProp(w, WM_NORMAL_HINTS, 0),
                GetProp(w, A_WM_PROTOCOLS, ATOM), GetProp(w, WM_HINTS, 0), GetProp(w, A_NET_WM_WINDOW_TYPE, ATOM),
            };
            var r = x.CallAll(reqs);
            if (r[0] == null) return;                     // gone already
            var xw = new XWin { Id = w };
            xw.X = XConn.S16(r[0], 12); xw.Y = XConn.S16(r[0], 14); xw.W = XConn.U16(r[0], 16); xw.H = XConn.U16(r[0], 18);
            string t = PropString(r[1]); if (t.Length == 0) t = PropString(r[2]);
            xw.Title = t;
            uint[] tf = PropU32(r[3]); if (tf.Length > 0) xw.TransientFor = tf[0];
            uint[] hints = PropU32(r[4]);
            bool userPos = false;
            if (hints.Length >= 9)
            {
                uint f = hints[0];
                userPos = (f & 5) != 0;
                if ((f & 16) != 0) { xw.MinW = (int)hints[5]; xw.MinH = (int)hints[6]; }
                if ((f & 32) != 0) { xw.MaxW = (int)hints[7]; xw.MaxH = (int)hints[8]; }
            }
            foreach (uint p in PropU32(r[5])) { if (p == A_WM_DELETE_WINDOW) xw.DeleteWindow = true; if (p == A_WM_TAKE_FOCUS) xw.TakeFocus = true; }
            uint[] wmh = PropU32(r[6]);
            if (wmh.Length >= 2 && (wmh[0] & 1) != 0) xw.InputHint = wmh[1] != 0;
            foreach (uint p in PropU32(r[7])) if (p == A_NET_WM_WINDOW_TYPE_DIALOG) xw.Dialog = true;
            if (xw.TransientFor != 0) xw.Dialog = true;

            if (!mapped)
            {
                // Place it like Windows would: over its parent, else centred in
                // the work area; keep the native title bar on the screen.
                if (!userPos || (xw.X == 0 && xw.Y == 0))
                {
                    XWin parent; System.Drawing.Rectangle around = WorkArea;
                    if (xw.TransientFor != 0 && wins.TryGetValue(xw.TransientFor, out parent)) around = new System.Drawing.Rectangle(parent.X, parent.Y, parent.W, parent.H);
                    xw.X = around.X + (around.Width - xw.W) / 2; xw.Y = around.Y + (around.Height - xw.H) / 2;
                }
            }
            // Windows found already on the screen (mapped without a window
            // manager, or left by the previous one) get the same rule.
            int top = WorkArea.Top + 32;
            if (!mapped || xw.Y < top || xw.X < WorkArea.Left)
            {
                if (xw.Y < top) xw.Y = top;
                if (xw.X < WorkArea.Left) xw.X = WorkArea.Left;
                Configure(xw.Id, xw.X, xw.Y, xw.W, xw.H);
            }
            x.Do(new Req(2).U32(w).U32(0x800).U32(0x400000));          // PropertyChange
            SetCardinals(w, A_NET_FRAME_EXTENTS, CARDINAL, 0, 0, 0, 0);
            SetCardinals(w, A_WM_STATE, A_WM_STATE, 1, 0);
            wins[w] = xw;
            geo[w] = new[] { xw.X, xw.Y, xw.W, xw.H, 0 };
            if (!mapped) x.Do(new Req(8).U32(w));
            ClientList();
            sink.Add(xw);
        }

        void ShowPopup(uint w)
        {
            int[] g;
            if (!geo.TryGetValue(w, out g))
            {
                var r = x.Call(new Req(14).U32(w));
                g = new[] { (int)XConn.S16(r, 12), (int)XConn.S16(r, 14), XConn.U16(r, 16), XConn.U16(r, 18), 1 };
                geo[w] = g;
            }
            var xw = new XWin { Id = w, Popup = true, X = g[0], Y = g[1], W = g[2], H = g[3] };
            wins[w] = xw;
            sink.Add(xw);
        }

        void ClientList()
        {
            var ids = new List<uint>();
            foreach (var w in wins.Values) if (!w.Popup) ids.Add(w.Id);
            SetCardinals(x.Root, A_NET_CLIENT_LIST, WINDOW, ids.ToArray());
        }

        // ---- requests from the viewer (any thread) ----------------------------
        public void Configure(uint w, int nx, int ny, int nw, int nh)
        {
            x.Do(new Req(12).U32(w).U16(15).U16(0).I32(nx).I32(ny).U32((uint)Math.Max(1, nw)).U32((uint)Math.Max(1, nh)));
        }

        public void Move(uint w, int nx, int ny) { x.Do(new Req(12).U32(w).U16(3).U16(0).I32(nx).I32(ny)); }

        public void Focus(uint w, XWin xw)
        {
            x.Do(new Req(12).U32(w).U16(64).U16(0).U32(0));            // raise
            if (xw == null || xw.InputHint) x.Do(new Req(42, 2).U32(w).U32(0));
            if (xw != null && xw.TakeFocus) SendProtocol(w, A_WM_TAKE_FOCUS);
            SetCardinals(x.Root, A_NET_ACTIVE_WINDOW, WINDOW, w);
        }

        public void Lower(uint w) { x.Do(new Req(12).U32(w).U16(64).U16(0).U32(1)); }

        public void Close(uint w)
        {
            XWin xw; bool known;
            lock (wins) known = wins.TryGetValue(w, out xw);
            if (known && xw.DeleteWindow) SendProtocol(w, A_WM_DELETE_WINDOW);
            else x.Do(new Req(113).U32(w));
        }

        public void SetMaximized(uint w, bool on)
        {
            if (on) SetCardinals(w, A_NET_WM_STATE, ATOM, A_NET_WM_STATE_MAXIMIZED_VERT, A_NET_WM_STATE_MAXIMIZED_HORZ);
            else SetCardinals(w, A_NET_WM_STATE, ATOM);
        }

        void SendProtocol(uint w, uint atom)
        {
            var r = new Req(25, 0).U32(w).U32(0);
            r.U8(33).U8(32).U16(0).U32(w).U32(A_WM_PROTOCOLS).U32(atom).U32(0).U32(0).U32(0).U32(0);
            x.Do(r);
        }

        // ---- properties -----------------------------------------------------
        static Req GetProp(uint w, uint prop, uint type)
        {
            return new Req(20, 0).U32(w).U32(prop).U32(type).U32(0).U32(65536);
        }

        string GetTitle(uint w)
        {
            var r = x.CallAll(new List<Req> { GetProp(w, A_NET_WM_NAME, A_UTF8_STRING), GetProp(w, WM_NAME, 0) });
            string t = PropString(r[0]);
            return t.Length > 0 ? t : PropString(r[1]);
        }

        string PropString(byte[] r)
        {
            if (r == null || r[1] != 8) return "";
            int n = (int)XConn.U32(r, 16);
            uint type = XConn.U32(r, 8);
            return type == A_UTF8_STRING ? Encoding.UTF8.GetString(r, 32, n) : Encoding.GetEncoding(28591).GetString(r, 32, n);
        }

        static uint[] PropU32(byte[] r)
        {
            if (r == null || r[1] != 32) return new uint[0];
            int n = (int)XConn.U32(r, 16);
            var v = new uint[n];
            for (int i = 0; i < n; i++) v[i] = XConn.U32(r, 32 + 4 * i);
            return v;
        }

        void SetCardinals(uint w, uint prop, uint type, params uint[] vals)
        {
            var r = new Req(18, 0).U32(w).U32(prop).U32(type).U8(32).U8(0).U16(0).U32((uint)vals.Length);
            foreach (uint v in vals) r.U32(v);
            x.Do(r);
        }

        void SetUtf8(uint w, uint prop, string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            x.Do(new Req(18, 0).U32(w).U32(prop).U32(A_UTF8_STRING).U8(8).U8(0).U16(0).U32((uint)b.Length).Bytes(b));
        }
    }
}
