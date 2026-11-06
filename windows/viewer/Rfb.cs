// A VNC (RFB 3.8) client for TigerVNC's Xvnc: no authentication (the
// session is reached only through gtvpn's private forwards), 32-bit pixels,
// Tight encoding with JPEG, CopyRect, the cursor shape, and desktop resizes.
//
// The framebuffer is an int[] of 0x00RRGGBB pixels (B, G, R, X in memory,
// the layout of a 32-bit Windows DIB). The receive thread decodes into it
// under FbLock and then reports the changed rectangles.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GtVpn
{
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
        public bool ServerCanResize;
        bool continuous;
        public bool UseContinuous = Environment.GetEnvironmentVariable("GTVPN_VIEWER_CU") != "0";
        uint screenId;
        int wantW, wantH;
        readonly Inflater[] zs = { new Inflater(), new Inflater(), new Inflater(), new Inflater() };
        byte[] zin = new byte[1 << 16], zout = new byte[1 << 16];
        public long Frames, Bytes;

        public void Connect(string host, int port)
        {
            tcp = new TcpClient();
            tcp.NoDelay = true;
            tcp.ReceiveBufferSize = 4 << 20;
            tcp.Connect(host, port);
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
            // Tight, CopyRect, Raw; ExtendedDesktopSize, DesktopSize, LastRect,
            // the cursor shape, Fence and ContinuousUpdates; JPEG quality and
            // zlib level.
            var enc = new List<int> { 7, 1, 0, -308, -223, -224, -239, -312, -313, -32 + JpegQuality, -256 + CompressLevel };
            if (!UseContinuous) enc.Remove(-313);
            var m = new MemoryStream();
            m.WriteByte(2); m.WriteByte(0); PutU16(m, enc.Count);
            foreach (int e in enc) PutU32(m, (uint)e);
            Write(m.ToArray());
            RequestUpdate(false);
            var t = new Thread(Loop) { IsBackground = true, Name = "rfb" };
            t.Start();
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
                            if ((flags & 0x80000000u) != 0) Fence(flags & 7u, payload);
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
                    case 0: Raw(x, y, w, h); dirty.Add(new Rectangle(x, y, w, h)); break;
                    case 1:
                        int sx = ReadU16(), sy = ReadU16();
                        lock (FbLock) CopyRect(sx, sy, x, y, w, h);
                        dirty.Add(new Rectangle(x, y, w, h)); break;
                    case 7: Tight(x, y, w, h); dirty.Add(new Rectangle(x, y, w, h)); break;
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
            var u = Updated; if (u != null && dirty.Count > 0) u(dirty);
        }

        void Resize(int w, int h)
        {
            lock (FbLock) { Width = w; Height = h; Fb = new int[w * h]; }
        }

        void Raw(int x, int y, int w, int h)
        {
            byte[] row = new byte[w * 4];
            for (int r = 0; r < h; r++)
            {
                ReadInto(row, 0, row.Length);
                lock (FbLock) Buffer.BlockCopy(row, 0, Fb, ((y + r) * Width + x) * 4, row.Length);
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
                lock (FbLock)
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
            lock (FbLock)
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
                    lock (FbLock)
                        for (int r = 0; r < bd.Height; r++)
                            Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)r * bd.Stride), Fb, (y + r) * Width + x, bd.Width);
                }
                finally { bmp.UnlockBits(bd); }
            }
        }

        // ---- I/O ------------------------------------------------------------
        void Write(byte[] b) { lock (wlock) ns.Write(b, 0, b.Length); }

        void Fill()
        {
            if (rpos == rlen) { rpos = 0; rlen = 0; }
            int n = ns.Read(rbuf, rlen, rbuf.Length - rlen);
            if (n <= 0) throw new IOException("the VNC connection closed");
            rlen += n; Bytes += n;
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
}
