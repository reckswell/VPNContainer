// A VNC (RFB 3.8) client for TigerVNC's Xvnc: no authentication (the
// session is reached only through gtvpn's private forwards), 32-bit pixels,
// the ZRLE and Tight (with JPEG) encodings, CopyRect, the cursor shape, and
// desktop resizes.
//
// The framebuffer is an int[] of 0x00RRGGBB pixels (B, G, R, X in memory,
// the layout of a 32-bit Windows DIB). The receive thread decodes into it
// under FbLock and then reports the changed rectangles.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GtVpn
{
    // Linux (Mono): ACK each piece of data at once (TCP_QUICKACK), set
    // again after every read, since the kernel goes back to delayed ACKs on
    // its own. ssh's local forward to the session's Unix socket leaves Nagle
    // on for its end of the connection to this viewer, so the last small
    // piece of an update waited for the ACK of the piece before, which Linux
    // delays up to 40 ms when nothing goes back: Xvnc's round-trip pings came
    // back late, its congestion control held back, and updates came about 43
    // a second instead of 170 (lab, the same Nagle setting on that hop).
    static class QuickAck
    {
        [DllImport("libc", SetLastError = true)] static extern int setsockopt(int fd, int level, int name, ref int value, int len);
        static bool off = Native.IsWindows;
        public static bool On { get { return !off; } }
        public static void Arm(Socket s)
        {
            if (off) return;
            try { int one = 1; if (setsockopt(s.Handle.ToInt32(), 6 /* IPPROTO_TCP */, 12 /* TCP_QUICKACK */, ref one, 4) != 0) off = true; }
            catch (Exception) { off = true; }
        }
    }

    sealed class CursorShape
    {
        public int HotX, HotY, W, H;
        public int[] Pixels;   // 0x00RRGGBB
        public byte[] Mask;    // 1 bit per pixel, rows padded to bytes, 1 = opaque
    }

    sealed class RfbClient
    {
        TcpClient tcp;
        Stream ns;
        readonly object wlock = new object();
        readonly byte[] rbuf = new byte[1 << 16];
        int rpos, rlen;

        public readonly object FbLock = new object();
        public int Width, Height;
        public int[] Fb = new int[0];
        public string Name = "";

        public event Action<List<Rectangle>> Updated;   // receive thread, FbLock not held
        public event Action<int, int> Resized;          // receive thread
        public event Action<CursorShape> CursorChanged; // receive thread
        public event Action<string> Closed;             // receive thread
        public event Action<string> CutTextReceived;    // receive thread: the session's clipboard

        public int JpegQuality = 8, CompressLevel = 1;
        // ZRLE first (lossless): for CAD pictures (few colours, fine hatching)
        // it costs Xvnc about half the compression work of Tight, whose 8-bit
        // palette indices make zlib Xvnc's main cost. Tight sends JPEG for
        // photo-like areas instead.
        public bool PreferZrle = true;
        public bool ServerCanResize;
        bool continuous;
        // GTVPN_VIEWER_CU=0: no ContinuousUpdates (an update request after
        // each update instead); nofence: no Fence either. To compare.
        static readonly string cuMode = Environment.GetEnvironmentVariable("GTVPN_VIEWER_CU");
        public bool UseContinuous = cuMode != "0" && cuMode != "nofence";
        public bool UseFence = cuMode != "nofence";
        uint screenId;
        int wantW, wantH;
        readonly ZStream[] zs = { new ZStream(), new ZStream(), new ZStream(), new ZStream() };
        readonly ZStream zrle = new ZStream();
        byte[] zin = new byte[1 << 16], zout = new byte[1 << 16];
        public long Frames, Bytes;
        public long Pixels;                  // the area of the rectangles sent as pictures (Raw, Tight, ZRLE), for CompressTuner
        // Where the receive thread's time goes, for the viewer's log (Stopwatch
        // ticks): waiting on the socket, waiting for FbLock, in Updated, and
        // in writes (from either thread); and the most bytes left unread in
        // the socket after a read since the last look (Backlog()).
        public long ReadTicks, FbWaitTicks, CallTicks, WriteTicks;
        int maxBacklog;
        public int Backlog() { return Interlocked.Exchange(ref maxBacklog, 0); }

        // Our own Fence requests (Ping), which Xvnc answers as it reaches
        // them: the round trip is the link's delay plus whatever waits ahead
        // of the answer, such as pictures queued on the way when the link is
        // the limit. Kept for 10 s as they come (RoundTrips), and as the
        // least per 10 s for 5 minutes (IdleRoundTrip: the link's own delay).
        public bool FenceSeen;               // the server sent a Fence: it has the extension
        public bool UsePing = true;          // --ping off: none (to compare)
        readonly long[] pingSent = new long[16];
        int pingSeq;
        readonly object rttLock = new object();
        readonly List<KeyValuePair<long, double>> rtts = new List<KeyValuePair<long, double>>();
        readonly double[] rttMin = new double[30];
        readonly long[] rttMinAt = new long[30];

        public void Ping()
        {
            if (!UsePing || !UseFence || !FenceSeen) return;
            int s = Interlocked.Increment(ref pingSeq);
            pingSent[s & 15] = Stopwatch.GetTimestamp();
            try { Write(new byte[] { 248, 0, 0, 0, 0x80, 0, 0, 0, 5, (byte)'g', (byte)(s >> 24), (byte)(s >> 16), (byte)(s >> 8), (byte)s }); }
            catch (IOException) { } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
        }

        void Pong(byte[] p)
        {
            int s = (p[1] << 24) | (p[2] << 16) | (p[3] << 8) | p[4], ahead = Thread.VolatileRead(ref pingSeq) - s;
            if (ahead < 0 || ahead >= 16) return;               // too old to know when it went
            long now = Stopwatch.GetTimestamp(), bucket = now / Stopwatch.Frequency / 10 + 1;
            double ms = (now - pingSent[s & 15]) * 1000.0 / Stopwatch.Frequency;
            lock (rttLock)
            {
                rtts.Add(new KeyValuePair<long, double>(now, ms));
                int old = 0;
                while (old < rtts.Count && now - rtts[old].Key > 10 * Stopwatch.Frequency) old++;
                if (old > 0) rtts.RemoveRange(0, old);
                int i = (int)(bucket % 30);
                if (rttMinAt[i] != bucket) { rttMinAt[i] = bucket; rttMin[i] = ms; }
                else if (ms < rttMin[i]) rttMin[i] = ms;
            }
        }

        // The round trips answered since a time (Stopwatch ticks), up to 10 s back.
        public List<double> RoundTrips(long since)
        {
            var l = new List<double>();
            lock (rttLock) foreach (var r in rtts) if (r.Key > since) l.Add(r.Value);
            return l;
        }

        // The least round trip of the last 5 minutes, or NaN.
        public double IdleRoundTrip()
        {
            long bucket = Stopwatch.GetTimestamp() / Stopwatch.Frequency / 10 + 1; double m = double.NaN;
            lock (rttLock)
                for (int i = 0; i < 30; i++)
                    if (rttMinAt[i] != 0 && bucket - rttMinAt[i] < 30 && !(rttMin[i] >= m)) m = rttMin[i];
            return m;
        }

        public static double Median(List<double> l)
        {
            if (l.Count == 0) return double.NaN;
            l.Sort();
            return l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2;
        }

        // FbLock for the receive thread, counting the wait when the UI holds it.
        struct FbHold : IDisposable
        {
            readonly RfbClient c;
            public FbHold(RfbClient c)
            {
                this.c = c;
                if (!Monitor.TryEnter(c.FbLock))
                {
                    long t = Stopwatch.GetTimestamp();
                    Monitor.Enter(c.FbLock);
                    c.FbWaitTicks += Stopwatch.GetTimestamp() - t;
                }
            }
            public void Dispose() { Monitor.Exit(c.FbLock); }
        }

        public void Connect(string host, int port)
        {
            tcp = new TcpClient();
            tcp.NoDelay = true;
            tcp.ReceiveBufferSize = 4 << 20;
            tcp.Connect(host, port);
            QuickAck.Arm(tcp.Client);
            ns = tcp.GetStream();
            string ver = Encoding.ASCII.GetString(Read(12));
            if (!ver.StartsWith("RFB 003.")) throw new IOException("not a VNC server: " + ver.Trim());
            // Xvnc answers in version 3.3 only to turn a connection away.
            if (ver == "RFB 003.003\n" && ReadU32() == 0) throw new IOException("the VNC server refused: " + ReadString());
            Write(Encoding.ASCII.GetBytes("RFB 003.008\n"));
            int n = ReadU8();
            if (n == 0) throw new IOException("the VNC server refused: " + ReadString());
            byte[] types = Read(n);
            if (Array.IndexOf(types, (byte)1) < 0)
                throw new IOException("the VNC session wants a password (only sessions started by gtvpn vnc work)");
            Write(new byte[] { 1 });
            if (ReadU32() != 0) throw new IOException("the VNC server refused: " + ReadString());
            Write(new byte[] { 1 });                     // ClientInit: shared
            Width = ReadU16(); Height = ReadU16();
            Read(16);                                    // the server's pixel format
            Name = ReadString();
            Fb = new int[Width * Height];
            // SetPixelFormat: 32 bpp, depth 24, little endian, true colour, R<<16 G<<8 B.
            Write(new byte[] { 0, 0, 0, 0, 32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0 });
            // ZRLE, Tight, CopyRect, Raw; ExtendedDesktopSize, DesktopSize,
            // LastRect, the cursor shape, Fence and ContinuousUpdates; JPEG
            // quality and zlib level.
            SendEncodings();
            RequestUpdate(false);
            var t = new Thread(Loop) { IsBackground = true, Name = "rfb" };
            t.Start();
        }

        void SendEncodings()
        {
            var enc = new List<int> { 16, 7, 1, 0, -308, -223, -224, -239, -312, -313, -32 + JpegQuality, -256 + CompressLevel };
            if (!PreferZrle) { enc.Remove(16); enc.Insert(1, 16); }
            if (!UseContinuous) enc.Remove(-313);
            if (!UseFence) enc.Remove(-312);
            var m = new MemoryStream();
            m.WriteByte(2); m.WriteByte(0); PutU16(m, enc.Count);
            foreach (int e in enc) PutU32(m, (uint)e);
            Write(m.ToArray());
        }

        // A new zlib level for what Xvnc sends from now on (it takes a new
        // SetEncodings at any time).
        public void SetCompress(int level)
        {
            if (level == CompressLevel) return;
            CompressLevel = level;
            try { SendEncodings(); } catch (IOException) { }
        }

        // Ask the server to make its desktop this size (ExtendedDesktopSize).
        public void RequestDesktopSize(int w, int h)
        {
            wantW = w; wantH = h;
            if (!ServerCanResize || (w == Width && h == Height)) return;
            var m = new MemoryStream();
            m.WriteByte(251); m.WriteByte(0); PutU16(m, w); PutU16(m, h);
            m.WriteByte(1); m.WriteByte(0);
            PutU32(m, screenId); PutU16(m, 0); PutU16(m, 0); PutU16(m, w); PutU16(m, h); PutU32(m, 0);
            Write(m.ToArray());
        }

        public void Pointer(int x, int y, int buttons)
        {
            x = Math.Max(0, Math.Min(Width - 1, x)); y = Math.Max(0, Math.Min(Height - 1, y));
            Write(new byte[] { 5, (byte)buttons, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y });
        }

        public void Key(uint keysym, bool down)
        {
            Write(new byte[] { 4, (byte)(down ? 1 : 0), 0, 0, (byte)(keysym >> 24), (byte)(keysym >> 16), (byte)(keysym >> 8), (byte)keysym });
        }

        public void CutText(string text)
        {
            byte[] b = Encoding.GetEncoding(28591).GetBytes(text.Replace("\r\n", "\n"));
            var m = new MemoryStream();
            m.WriteByte(6); m.WriteByte(0); m.WriteByte(0); m.WriteByte(0); PutU32(m, (uint)b.Length); m.Write(b, 0, b.Length);
            Write(m.ToArray());
        }

        public void Close() { try { tcp.Close(); } catch { } }

        void EnableContinuous()
        {
            int w = Width, h = Height;
            Write(new byte[] { 150, 1, 0, 0, 0, 0, (byte)(w >> 8), (byte)w, (byte)(h >> 8), (byte)h });
        }

        // The answer to the server's Fence: its payload back, with the flags
        // we honour (we handle messages in order, which satisfies all three).
        void Fence(uint flags, byte[] payload)
        {
            var m = new MemoryStream();
            m.WriteByte(248); m.WriteByte(0); m.WriteByte(0); m.WriteByte(0);
            PutU32(m, flags); m.WriteByte((byte)payload.Length); m.Write(payload, 0, payload.Length);
            Write(m.ToArray());
        }

        void RequestUpdate(bool incremental)
        {
            int w = Width, h = Height;
            Write(new byte[] { 3, (byte)(incremental ? 1 : 0), 0, 0, 0, 0, (byte)(w >> 8), (byte)w, (byte)(h >> 8), (byte)h });
        }

        void Loop()
        {
            string why = "the VNC connection closed";
            try
            {
                while (true)
                {
                    int type = ReadU8();
                    switch (type)
                    {
                        case 0: Update(); break;
                        case 1: ReadU8(); ReadU16(); Read(ReadU16() * 6); break;  // colour map: unused
                        case 2: break;                                             // bell
                        case 3:
                            Read(3);
                            int len = (int)ReadU32();
                            if (len < 0) { Read(-len); break; }                   // extended clipboard: not asked for
                            string text = Encoding.GetEncoding(28591).GetString(Read(len));
                            var ct = CutTextReceived; if (ct != null) ct(text.Replace("\n", "\r\n"));
                            break;
                        case 150:
                            // The server can push updates without a request per
                            // frame (no round trip between frames); it paces them
                            // with Fence messages.
                            if (!continuous) { continuous = true; EnableContinuous(); }
                            break;
                        case 248:
                            Read(3);
                            uint flags = ReadU32();
                            byte[] payload = Read(ReadU8());
                            FenceSeen = true;
                            if ((flags & 0x80000000u) != 0) Fence(flags & 7u, payload);
                            else if (payload.Length == 5 && payload[0] == (byte)'g') Pong(payload);
                            break;
                        default: throw new IOException("unknown VNC message " + type);
                    }
                }
            }
            catch (Exception e) { why = e.Message; }
            var c = Closed; if (c != null) c(why);
        }

        void Update()
        {
            ReadU8();
            int n = ReadU16();
            var dirty = new List<Rectangle>();
            bool resized = false;
            for (int i = 0; n == 0xffff || i < n; i++)
            {
                int x = ReadU16(), y = ReadU16(), w = ReadU16(), h = ReadU16();
                int enc = (int)ReadU32();
                if (enc == -224) break;                              // LastRect
                switch (enc)
                {
                    case 0: Raw(x, y, w, h); dirty.Add(new Rectangle(x, y, w, h)); Pixels += (long)w * h; break;
                    case 1:
                        int sx = ReadU16(), sy = ReadU16();
                        using (new FbHold(this)) CopyRect(sx, sy, x, y, w, h);
                        dirty.Add(new Rectangle(x, y, w, h)); break;
                    case 7: Tight(x, y, w, h); dirty.Add(new Rectangle(x, y, w, h)); Pixels += (long)w * h; break;
                    case 16: Zrle(x, y, w, h); dirty.Add(new Rectangle(x, y, w, h)); Pixels += (long)w * h; break;
                    case -239: CursorShape(x, y, w, h); break;
                    case -223: Resize(w, h); resized = true; break;
                    case -308:
                        int screens = ReadU8(); Read(3);
                        for (int s = 0; s < screens; s++)
                        {
                            uint id = ReadU32(); Read(8); ReadU32();
                            if (s == 0) screenId = id;
                        }
                        bool first = !ServerCanResize;
                        ServerCanResize = true;
                        if (y == 0 && (w != Width || h != Height)) { Resize(w, h); resized = true; }
                        if (first && wantW > 0) RequestDesktopSize(wantW, wantH);
                        break;
                    default: throw new IOException("unexpected VNC encoding " + enc);
                }
            }
            Frames++;
            if (resized)
            {
                var r = Resized; if (r != null) r(Width, Height);
                if (continuous) EnableContinuous();
                RequestUpdate(false);
            }
            else if (!continuous) RequestUpdate(true);
            var u = Updated;
            if (u != null && dirty.Count > 0) { long t = Stopwatch.GetTimestamp(); u(dirty); CallTicks += Stopwatch.GetTimestamp() - t; }
        }

        void Resize(int w, int h)
        {
            using (new FbHold(this)) { Width = w; Height = h; Fb = new int[w * h]; }
        }

        void Raw(int x, int y, int w, int h)
        {
            byte[] row = new byte[w * 4];
            for (int r = 0; r < h; r++)
            {
                ReadInto(row, 0, row.Length);
                using (new FbHold(this)) Buffer.BlockCopy(row, 0, Fb, ((y + r) * Width + x) * 4, row.Length);
            }
        }

        void CopyRect(int sx, int sy, int dx, int dy, int w, int h)
        {
            if (dy > sy)
                for (int r = h - 1; r >= 0; r--) Array.Copy(Fb, (sy + r) * Width + sx, Fb, (dy + r) * Width + dx, w);
            else
                for (int r = 0; r < h; r++) Array.Copy(Fb, (sy + r) * Width + sx, Fb, (dy + r) * Width + dx, w);
        }

        void CursorShape(int hx, int hy, int w, int h)
        {
            var c = new CursorShape { HotX = hx, HotY = hy, W = w, H = h, Pixels = new int[w * h] };
            byte[] px = Read(w * h * 4);
            Buffer.BlockCopy(px, 0, c.Pixels, 0, px.Length);
            c.Mask = Read((w + 7) / 8 * h);
            var e = CursorChanged; if (e != null) e(c);
        }

        // ---- Tight ----------------------------------------------------------
        int CompactLength()
        {
            int b = ReadU8(), len = b & 0x7f;
            if ((b & 0x80) != 0)
            {
                b = ReadU8(); len |= (b & 0x7f) << 7;
                if ((b & 0x80) != 0) len |= ReadU8() << 14;
            }
            return len;
        }

        void Tight(int x, int y, int w, int h)
        {
            int ctl = ReadU8();
            for (int i = 0; i < 4; i++) if ((ctl & (1 << i)) != 0) zs[i].Reset();
            int type = ctl >> 4;
            if (type == 8)
            {
                byte[] p = Read(3);
                int c = (p[0] << 16) | (p[1] << 8) | p[2];
                using (new FbHold(this))
                    for (int r = 0; r < h; r++)
                    {
                        int o = (y + r) * Width + x;
                        for (int k = 0; k < w; k++) Fb[o + k] = c;
                    }
                return;
            }
            if (type == 9) { Jpeg(x, y, w, h, CompactLength()); return; }
            if (type > 9) throw new IOException("unsupported Tight compression " + type);
            int stream = type & 3, filter = 0;
            if ((type & 4) != 0) filter = ReadU8();
            int[] palette = null; int rowSize;
            if (filter == 1)
            {
                int nc = ReadU8() + 1;
                byte[] p = Read(nc * 3);
                palette = new int[nc];
                for (int i = 0; i < nc; i++) palette[i] = (p[i * 3] << 16) | (p[i * 3 + 1] << 8) | p[i * 3 + 2];
                rowSize = nc == 2 ? (w + 7) / 8 : w;
            }
            else if (filter == 0 || filter == 2) rowSize = w * 3;
            else throw new IOException("unknown Tight filter " + filter);
            int size = rowSize * h;
            if (zout.Length < size) zout = new byte[size];
            if (size < 12) ReadInto(zout, 0, size);
            else
            {
                int clen = CompactLength();
                if (zin.Length < clen) zin = new byte[clen];
                ReadInto(zin, 0, clen);
                zs[stream].Inflate(zin, 0, clen, zout, 0, size);
            }
            using (new FbHold(this))
            {
                if (palette != null)
                {
                    bool two = palette.Length == 2;
                    for (int r = 0; r < h; r++)
                    {
                        int o = (y + r) * Width + x, s = r * rowSize;
                        if (two) for (int k = 0; k < w; k++) Fb[o + k] = palette[(zout[s + (k >> 3)] >> (7 - (k & 7))) & 1];
                        else for (int k = 0; k < w; k++) Fb[o + k] = palette[Math.Min(zout[s + k], palette.Length - 1)];
                    }
                }
                else if (filter == 0)
                {
                    for (int r = 0; r < h; r++)
                    {
                        int o = (y + r) * Width + x, s = r * rowSize;
                        for (int k = 0; k < w; k++, s += 3) Fb[o + k] = (zout[s] << 16) | (zout[s + 1] << 8) | zout[s + 2];
                    }
                }
                else Gradient(x, y, w, h);
            }
        }

        void Gradient(int x, int y, int w, int h)
        {
            var prev = new int[w * 3]; var cur = new int[w * 3];
            for (int r = 0; r < h; r++)
            {
                int s = r * w * 3, o = (y + r) * Width + x;
                for (int k = 0; k < w; k++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int left = k > 0 ? cur[(k - 1) * 3 + c] : 0;
                        int up = prev[k * 3 + c];
                        int ul = k > 0 ? prev[(k - 1) * 3 + c] : 0;
                        int est = left + up - ul;
                        if (est < 0) est = 0; else if (est > 255) est = 255;
                        cur[k * 3 + c] = (zout[s + k * 3 + c] + est) & 0xff;
                    }
                    Fb[o + k] = (cur[k * 3] << 16) | (cur[k * 3 + 1] << 8) | cur[k * 3 + 2];
                }
                var t = prev; prev = cur; cur = t;
            }
        }

        void Jpeg(int x, int y, int w, int h, int len)
        {
            if (zin.Length < len) zin = new byte[len];
            ReadInto(zin, 0, len);
            using (var ms = new MemoryStream(zin, 0, len))
            using (var bmp = new Bitmap(ms))
            {
                var bd = bmp.LockBits(new Rectangle(0, 0, Math.Min(w, bmp.Width), Math.Min(h, bmp.Height)), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                try
                {
                    using (new FbHold(this))
                        for (int r = 0; r < bd.Height; r++)
                            Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)r * bd.Stride), Fb, (y + r) * Width + x, bd.Width);
                }
                finally { bmp.UnlockBits(bd); }
            }
        }

        // ---- ZRLE -----------------------------------------------------------
        // One zlib stream for the connection; per rectangle a length and the
        // compressed tiles (64x64, rows of tiles): raw, solid, packed palette
        // (2-16 colours), RLE, or palette RLE. A pixel is 3 bytes, B G R.
        byte[] zr = new byte[1 << 16];

        void Zrle(int x, int y, int w, int h)
        {
            int clen = (int)ReadU32();
            if (clen < 0 || clen > 64 << 20) throw new IOException("bad ZRLE length " + clen);
            if (zin.Length < clen) zin = new byte[clen];
            ReadInto(zin, 0, clen);
            int n = zrle.InflateAll(zin, 0, clen, ref zr);
            byte[] b = zr;
            var pal = new int[128];
            int p = 0;
            using (new FbHold(this))
            {
                int[] fb = Fb; int stride = Width;
                for (int ty = y; ty < y + h; ty += 64)
                {
                    int th = Math.Min(64, y + h - ty);
                    for (int tx = x; tx < x + w; tx += 64)
                    {
                        int tw = Math.Min(64, x + w - tx);
                        int sub = b[p++];
                        if (sub == 0)
                        {
                            for (int r = 0; r < th; r++)
                            {
                                int o = (ty + r) * stride + tx;
                                for (int k = 0; k < tw; k++, p += 3) fb[o + k] = b[p] | (b[p + 1] << 8) | (b[p + 2] << 16);
                            }
                        }
                        else if (sub == 1)
                        {
                            int c = b[p] | (b[p + 1] << 8) | (b[p + 2] << 16); p += 3;
                            for (int r = 0; r < th; r++)
                            {
                                int o = (ty + r) * stride + tx;
                                for (int k = 0; k < tw; k++) fb[o + k] = c;
                            }
                        }
                        else if (sub <= 16)
                        {
                            for (int i = 0; i < sub; i++, p += 3) pal[i] = b[p] | (b[p + 1] << 8) | (b[p + 2] << 16);
                            int bits = sub == 2 ? 1 : sub <= 4 ? 2 : 4, mask = (1 << bits) - 1, rowBytes = (tw * bits + 7) >> 3;
                            for (int r = 0; r < th; r++)
                            {
                                int o = (ty + r) * stride + tx, shift = 0, cur = 0, q = p;
                                for (int k = 0; k < tw; k++)
                                {
                                    if (shift == 0) { cur = b[q++]; shift = 8; }
                                    shift -= bits;
                                    fb[o + k] = pal[(cur >> shift) & mask];
                                }
                                p += rowBytes;
                            }
                        }
                        else if (sub == 128 || sub >= 130)
                        {
                            bool usePal = sub >= 130;
                            if (usePal) for (int i = 0; i < sub - 128; i++, p += 3) pal[i] = b[p] | (b[p + 1] << 8) | (b[p + 2] << 16);
                            int r = 0, k = 0, o = ty * stride + tx;
                            while (r < th)
                            {
                                int c, len = 1;
                                if (usePal)
                                {
                                    int idx = b[p++];
                                    c = pal[idx & 127];
                                    if ((idx & 128) != 0) { int v; do { v = b[p++]; len += v; } while (v == 255); }
                                }
                                else
                                {
                                    c = b[p] | (b[p + 1] << 8) | (b[p + 2] << 16); p += 3;
                                    int v; do { v = b[p++]; len += v; } while (v == 255);
                                }
                                while (len-- > 0)
                                {
                                    if (r >= th) throw new IOException("ZRLE run past its tile");
                                    fb[o + k] = c;
                                    if (++k == tw) { k = 0; r++; o += stride; }
                                }
                            }
                        }
                        else throw new IOException("unknown ZRLE tile type " + sub);
                        if (p > n) throw new IOException("ZRLE data ended early");
                    }
                }
            }
        }

        // ---- I/O ------------------------------------------------------------
        void Write(byte[] b)
        {
            long t = Stopwatch.GetTimestamp();
            lock (wlock) ns.Write(b, 0, b.Length);
            Interlocked.Add(ref WriteTicks, Stopwatch.GetTimestamp() - t);
        }

        void Fill()
        {
            if (rpos == rlen) { rpos = 0; rlen = 0; }
            long t = Stopwatch.GetTimestamp();
            int n = ns.Read(rbuf, rlen, rbuf.Length - rlen);
            ReadTicks += Stopwatch.GetTimestamp() - t;
            if (n <= 0) throw new IOException("the VNC connection closed");
            QuickAck.Arm(tcp.Client);
            rlen += n; Bytes += n;
            int left = tcp.Available;
            if (left > maxBacklog) maxBacklog = left;
        }

        void ReadInto(byte[] dst, int off, int n)
        {
            while (n > 0)
            {
                if (rpos == rlen) Fill();
                int k = Math.Min(n, rlen - rpos);
                Buffer.BlockCopy(rbuf, rpos, dst, off, k);
                rpos += k; off += k; n -= k;
            }
        }

        byte[] Read(int n) { var b = new byte[n]; ReadInto(b, 0, n); return b; }
        int ReadU8() { if (rpos == rlen) Fill(); return rbuf[rpos++]; }
        int ReadU16() { int a = ReadU8(); return (a << 8) | ReadU8(); }
        uint ReadU32() { uint a = (uint)ReadU16(); return (a << 16) | (uint)ReadU16(); }
        string ReadString() { return Encoding.UTF8.GetString(Read((int)ReadU32())); }

        static void PutU16(Stream m, int v) { m.WriteByte((byte)(v >> 8)); m.WriteByte((byte)v); }
        static void PutU32(Stream m, uint v) { PutU16(m, (int)(v >> 16)); PutU16(m, (int)(v & 0xffff)); }
    }

    // Chooses zlib's level for what Xvnc sends, 0 or 1, while pictures
    // stream. Level 0 costs Xvnc about half the CPU of level 1 and sends
    // two to three times the bytes. Level 1 only helps where the link holds
    // the pictures back, and Xvnc's CPU is also what draws the programs: on
    // the test PC through the tunnel (1200x800 at 180 frames a second),
    // level 1 took Xvnc's whole core, the drawing program fell from 151 to
    // 51 frames a second and the screen from 46 to 35 changes a second,
    // although more pixels arrived (62 Mpx/s against 44). So the tuner
    // stays at level 0 unless the link is the limit, which shows as the
    // answers to its pings (RfbClient.Ping) coming back at least 20 ms
    // slower than on an idle link: Xvnc's congestion control lets pictures
    // queue on the way until its own pings take 25-50 ms longer, while an
    // Xvnc busy compressing delayed them less in the lab (9-19 ms). Then it
    // tries level 1 for 2 s and keeps it if 15% more pixels arrived a
    // second while the link still carried most of the bytes it did at level
    // 0 (80%: still the limit, rather than Xvnc), or 2.5 times the pixels. At
    // level 1 it tries level 0 the same way. It looks again 20 s later, and
    // twice as long after each try where the level stays (up to 160 s).
    sealed class CompressTuner
    {
        struct Sample { public long At; public double Px, Bytes; }
        readonly RfbClient rfb;
        long last, lastPx, lastBytes;
        DateTime nextTry;
        readonly List<Sample> seen = new List<Sample>();
        int trying = -1, skip, wait = 20;
        Sample before; double beforeRtt;
        bool toldKeepsUp;
        public int Tries, Changes;
        const double BusyPx = 2e6;           // pixels a second: about 17 changes of 400x300
        const int Ticks = 8;                 // samples of 250 ms compared
        const double QueuedMs = 20;          // a round trip this much over an idle link's: pictures queue on the way
        public CompressTuner(RfbClient r) { rfb = r; nextTry = DateTime.Now.AddSeconds(3); }

        static Sample Avg(List<Sample> l)
        {
            var a = new Sample { At = l[0].At };
            foreach (var v in l) { a.Px += v.Px / l.Count; a.Bytes += v.Bytes / l.Count; }
            return a;
        }
        static string Ms(double v) { return double.IsNaN(v) ? "unknown" : v.ToString("F0") + " ms"; }
        static string Show(int level, Sample s) { return "level " + level + " " + (s.Px / 1e6).ToString("F1") + " Mpx/s at " + (s.Bytes / 1e6).ToString("F1") + " MB/s"; }

        // Whether level 1 does better than level 0, from 2 s of each and the
        // round trip at level 0.
        bool OneWins(Sample one, Sample zero, double zeroRtt, double idle)
        {
            return zeroRtt >= idle + QueuedMs && one.Px > zero.Px * 1.15 && (one.Bytes >= zero.Bytes * 0.8 || one.Px >= zero.Px * 2.5);
        }

        // Every 250 ms (the UI thread); what to log, or null.
        public string Tick()
        {
            long now = Stopwatch.GetTimestamp(), px = Interlocked.Read(ref rfb.Pixels), by = Interlocked.Read(ref rfb.Bytes);
            double dt = (double)(now - last) / Stopwatch.Frequency;
            if (last == 0 || dt <= 0) { last = now; lastPx = px; lastBytes = by; return null; }
            var s = new Sample { At = last, Px = (px - lastPx) / dt, Bytes = (by - lastBytes) / dt };
            last = now; lastPx = px; lastBytes = by;
            var t = DateTime.Now;
            bool busy = s.Px >= BusyPx;
            if (trying < 0)
            {
                if (!busy) { seen.Clear(); return null; }
                seen.Add(s); if (seen.Count > Ticks) seen.RemoveAt(0);
                if (seen.Count < Ticks || t < nextTry) return null;
                double rtt = RfbClient.Median(rfb.RoundTrips(seen[0].At)), idle = rfb.IdleRoundTrip();
                if (rfb.CompressLevel == 0 && !(rtt >= idle + QueuedMs))
                {
                    // The link carries what Xvnc sends: level 1 would only cost Xvnc's CPU.
                    seen.Clear(); nextTry = t.AddSeconds(5);
                    if (toldKeepsUp) return null;
                    toldKeepsUp = true;
                    return "zlib level 0 stays: the link keeps up (round trip " + Ms(rtt) + " busy, " + Ms(idle) + " idle)";
                }
                before = Avg(seen); beforeRtt = rtt; seen.Clear();
                trying = 1 - rfb.CompressLevel; skip = 2;   // (the first samples after the switch are still the old level's)
                rfb.SetCompress(trying); Tries++;
                return null;
            }
            int from = 1 - trying;
            if (!busy) { rfb.SetCompress(from); trying = -1; seen.Clear(); nextTry = t.AddSeconds(5); return null; }
            if (skip > 0) { skip--; return null; }
            seen.Add(s);
            if (seen.Count < Ticks) return null;
            var tried = Avg(seen);
            double triedRtt = RfbClient.Median(rfb.RoundTrips(seen[0].At)), idleNow = rfb.IdleRoundTrip();
            seen.Clear();
            int to = trying; trying = -1;
            bool one = to == 1 ? OneWins(tried, before, beforeRtt, idleNow) : OneWins(before, tried, triedRtt, idleNow);
            string what = Show(to, tried) + " against " + Show(from, before) + "; round trip at level 0 "
                + Ms(to == 0 ? triedRtt : beforeRtt) + " (" + Ms(idleNow) + " idle)";
            if ((one ? 1 : 0) == to) { Changes++; wait = 20; nextTry = t.AddSeconds(wait); return "zlib level " + to + " now: " + what; }
            rfb.SetCompress(from);
            nextTry = t.AddSeconds(wait); wait = Math.Min(160, wait * 2);
            return Tries <= 3 ? "zlib level " + from + " stays: " + what : null;
        }
    }
}
