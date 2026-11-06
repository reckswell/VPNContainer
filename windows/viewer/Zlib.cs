// The zlib streams of the Tight and ZRLE encodings, inflated by the zlib
// inside .NET's DeflateStream (native code, about twice as fast as inflating
// in C#).
//
// Xvnc ends the compressed data of every rectangle with a sync flush, so a
// rectangle's bytes inflate completely on their own, and the stream simply
// continues with the next rectangle's. Feed hands DeflateStream the bytes
// received so far; bytes it has not taken yet stay queued for the next
// rectangle, so a sync marker split across rectangles is never lost.
using System;
using System.IO;
using System.IO.Compression;

namespace GtVpn
{
    sealed class ZStream
    {
        sealed class Feed : Stream
        {
            byte[] buf = new byte[1 << 16];
            int pos, end;

            public void Push(byte[] b, int off, int len)
            {
                if (pos == end) { pos = 0; end = 0; }
                if (buf.Length - end < len)
                {
                    int have = end - pos;
                    if (buf.Length < have + len) { var nb = new byte[Math.Max(buf.Length * 2, have + len)]; Buffer.BlockCopy(buf, pos, nb, 0, have); buf = nb; }
                    else Buffer.BlockCopy(buf, pos, buf, 0, have);
                    pos = 0; end = have;
                }
                Buffer.BlockCopy(b, off, buf, end, len);
                end += len;
            }

            public void Clear() { pos = 0; end = 0; }

            public override int Read(byte[] b, int off, int n)
            {
                int k = Math.Min(n, end - pos);
                Buffer.BlockCopy(buf, pos, b, off, k);
                pos += k;
                return k;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); }
            public override void SetLength(long v) { throw new NotSupportedException(); }
            public override void Write(byte[] b, int o, int n) { throw new NotSupportedException(); }
        }

        readonly Feed feed = new Feed();
        DeflateStream ds;
        bool needHeader = true;

        // The server started this stream afresh.
        public void Reset()
        {
            if (ds != null) ds.Dispose();
            ds = null; needHeader = true; feed.Clear();
        }

        // Inflates input[off, off+len), which must produce exactly olen bytes,
        // into output[ooff, ...).
        public void Inflate(byte[] input, int off, int len, byte[] output, int ooff, int olen)
        {
            Start(input, off, len);
            int got = 0;
            while (got < olen)
            {
                int n = ds.Read(output, ooff + got, olen - got);
                if (n <= 0) throw new IOException("zlib: the data ended after " + got + " of " + olen + " bytes");
                got += n;
            }
        }

        // Inflates all of input[off, off+len) into output (made larger when it
        // must be) and returns the number of bytes.
        public int InflateAll(byte[] input, int off, int len, ref byte[] output)
        {
            Start(input, off, len);
            int got = 0;
            while (true)
            {
                if (got == output.Length) Array.Resize(ref output, output.Length * 2);
                int n = ds.Read(output, got, output.Length - got);
                if (n <= 0) return got;
                got += n;
            }
        }

        void Start(byte[] input, int off, int len)
        {
            if (needHeader)
            {
                // The 2-byte zlib header; DeflateStream reads raw deflate data.
                if (len < 2) throw new IOException("zlib: no header");
                int cmf = input[off], flg = input[off + 1];
                if ((cmf & 0x0f) != 8 || ((cmf << 8) | flg) % 31 != 0 || (flg & 0x20) != 0)
                    throw new IOException("zlib: bad header");
                off += 2; len -= 2; needHeader = false;
            }
            if (ds == null) ds = new DeflateStream(feed, CompressionMode.Decompress, true);
            feed.Push(input, off, len);
        }
    }
}
