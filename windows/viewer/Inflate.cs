// zlib inflater for the Tight encoding's four persistent streams.
//
// Xvnc ends the compressed data of every rectangle with a sync flush, so
// each chunk holds whole deflate blocks and ends on a byte boundary. Between
// chunks the only state is the 32 KB history window (and whether the zlib
// header is still to come), which keeps this small. Decoding follows zlib's
// reference "puff" decoder, with a lookup table for the short codes.
using System;

namespace GtVpn
{
    sealed class Inflater
    {
        const int WSIZE = 32768, WMASK = WSIZE - 1;
        readonly byte[] win = new byte[WSIZE];
        int wpos;
        bool needHeader = true;

        byte[] inb; int ipos, iend; uint bitbuf; int bitcnt;
        byte[] outb; int opos, oend;

        public void Reset() { needHeader = true; wpos = 0; }

        // Inflates input[off, off+len) into output[ooff, ooff+olen); the chunk
        // must produce exactly olen bytes.
        public void Inflate(byte[] input, int off, int len, byte[] output, int ooff, int olen)
        {
            inb = input; ipos = off; iend = off + len; bitbuf = 0; bitcnt = 0;
            outb = output; opos = ooff; oend = ooff + olen;
            if (needHeader)
            {
                if (len < 2) throw new InvalidOperationException("inflate: no zlib header");
                int cmf = inb[ipos++], flg = inb[ipos++];
                if ((cmf & 0x0f) != 8 || ((cmf << 8) | flg) % 31 != 0 || (flg & 0x20) != 0)
                    throw new InvalidOperationException("inflate: bad zlib header");
                needHeader = false;
            }
            while (ipos < iend || bitcnt >= 8)
            {
                int last = Bits(1);
                int type = Bits(2);
                if (type == 0) Stored();
                else if (type == 1) Codes(FixedLen, FixedDist);
                else if (type == 2) Dynamic();
                else throw new InvalidOperationException("inflate: bad block type");
                if (last == 1) { needHeader = true; ipos = iend; break; } // stream ended (not used by Xvnc)
            }
            if (opos != oend)
                throw new InvalidOperationException("inflate: produced " + (opos - ooff) + " of " + olen + " bytes");
        }

        int Bits(int need)
        {
            while (bitcnt < need)
            {
                if (ipos >= iend) throw new InvalidOperationException("inflate: out of input");
                bitbuf |= (uint)inb[ipos++] << bitcnt;
                bitcnt += 8;
            }
            int v = (int)(bitbuf & ((1u << need) - 1));
            bitbuf >>= need; bitcnt -= need;
            return v;
        }

        void Put(byte b)
        {
            if (opos >= oend) throw new InvalidOperationException("inflate: more data than expected");
            outb[opos++] = b; win[wpos] = b; wpos = (wpos + 1) & WMASK;
        }

        void Stored()
        {
            // To the byte boundary; whole bytes already in the bit buffer (the
            // decoder reads ahead) go back to the input.
            ipos -= bitcnt >> 3;
            bitbuf = 0; bitcnt = 0;
            if (iend - ipos < 4) throw new InvalidOperationException("inflate: short stored block");
            int n = inb[ipos] | (inb[ipos + 1] << 8);
            int nn = inb[ipos + 2] | (inb[ipos + 3] << 8);
            ipos += 4;
            if (n != (~nn & 0xffff)) throw new InvalidOperationException("inflate: bad stored length");
            if (iend - ipos < n) throw new InvalidOperationException("inflate: short stored data");
            for (int i = 0; i < n; i++) Put(inb[ipos++]);
        }

        // A canonical Huffman code: counts per length and symbols in order,
        // plus a table for codes of up to FAST bits (bit-reversed index).
        sealed class Huff
        {
            public const int FAST = 9;
            public readonly short[] Count = new short[16];
            public readonly short[] Symbol;
            public readonly int[] Fast = new int[1 << FAST]; // (len << 16) | symbol, 0 = slow path
            public Huff(int n) { Symbol = new short[n]; }
        }

        static Huff Build(byte[] lengths, int off, int n)
        {
            var h = new Huff(n);
            for (int s = 0; s < n; s++) h.Count[lengths[off + s]]++;
            h.Count[0] = 0;
            var offs = new short[16];
            for (int l = 1; l < 15; l++) offs[l + 1] = (short)(offs[l] + h.Count[l]);
            for (int s = 0; s < n; s++) if (lengths[off + s] != 0) h.Symbol[offs[lengths[off + s]]++] = (short)s;
            // Fast table: walk the canonical codes of length <= FAST.
            int code = 0, index = 0;
            for (int len = 1; len <= Huff.FAST; len++)
            {
                for (int k = 0; k < h.Count[len]; k++, code++, index++)
                {
                    int rev = 0;
                    for (int b = 0; b < len; b++) if ((code & (1 << b)) != 0) rev |= 1 << (len - 1 - b);
                    for (int fill = rev; fill < (1 << Huff.FAST); fill += 1 << len)
                        h.Fast[fill] = (len << 16) | h.Symbol[index];
                }
                code <<= 1;
            }
            return h;
        }

        int Decode(Huff h)
        {
            // Fast path: peek FAST bits if they are available.
            while (bitcnt < Huff.FAST && ipos < iend) { bitbuf |= (uint)inb[ipos++] << bitcnt; bitcnt += 8; }
            if (bitcnt >= Huff.FAST)
            {
                int e = h.Fast[bitbuf & ((1 << Huff.FAST) - 1)];
                if (e != 0) { int l = e >> 16; bitbuf >>= l; bitcnt -= l; return e & 0xffff; }
            }
            int code = 0, first = 0, index = 0;
            for (int len = 1; len <= 15; len++)
            {
                code |= Bits(1);
                int count = h.Count[len];
                if (code - count < first) return h.Symbol[index + (code - first)];
                index += count; first += count; first <<= 1; code <<= 1;
            }
            throw new InvalidOperationException("inflate: bad code");
        }

        static readonly short[] LBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
        static readonly byte[] LExt = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
        static readonly short[] DBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
        static readonly byte[] DExt = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };

        static readonly Huff FixedLen, FixedDist;
        static Inflater()
        {
            var l = new byte[288 + 30];
            int s = 0;
            for (; s < 144; s++) l[s] = 8;
            for (; s < 256; s++) l[s] = 9;
            for (; s < 280; s++) l[s] = 7;
            for (; s < 288; s++) l[s] = 8;
            FixedLen = Build(l, 0, 288);
            for (s = 0; s < 30; s++) l[288 + s] = 5;
            FixedDist = Build(l, 288, 30);
        }

        void Codes(Huff lencode, Huff distcode)
        {
            while (true)
            {
                int sym = Decode(lencode);
                if (sym < 256) { Put((byte)sym); continue; }
                if (sym == 256) return;
                sym -= 257;
                if (sym >= 29) throw new InvalidOperationException("inflate: bad length");
                int len = LBase[sym] + Bits(LExt[sym]);
                int ds = Decode(distcode);
                if (ds >= 30) throw new InvalidOperationException("inflate: bad distance");
                int dist = DBase[ds] + Bits(DExt[ds]);
                if (dist > WSIZE) throw new InvalidOperationException("inflate: distance too far");
                if (oend - opos < len) throw new InvalidOperationException("inflate: more data than expected");
                for (int i = 0; i < len; i++)
                {
                    byte b = win[(wpos - dist) & WMASK];
                    outb[opos++] = b; win[wpos] = b; wpos = (wpos + 1) & WMASK;
                }
            }
        }

        static readonly byte[] Order = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

        void Dynamic()
        {
            int nlen = Bits(5) + 257, ndist = Bits(5) + 1, ncode = Bits(4) + 4;
            if (nlen > 286 || ndist > 30) throw new InvalidOperationException("inflate: bad counts");
            var lengths = new byte[320];
            for (int i = 0; i < ncode; i++) lengths[Order[i]] = (byte)Bits(3);
            for (int i = ncode; i < 19; i++) lengths[Order[i]] = 0;
            var lencode = Build(lengths, 0, 19);
            int index = 0;
            var ll = new byte[nlen + ndist];
            while (index < nlen + ndist)
            {
                int sym = Decode(lencode);
                if (sym < 16) { ll[index++] = (byte)sym; continue; }
                int len = 0, rep;
                if (sym == 16)
                {
                    if (index == 0) throw new InvalidOperationException("inflate: repeat with no first length");
                    len = ll[index - 1]; rep = 3 + Bits(2);
                }
                else if (sym == 17) rep = 3 + Bits(3);
                else rep = 11 + Bits(7);
                if (index + rep > nlen + ndist) throw new InvalidOperationException("inflate: too many lengths");
                while (rep-- > 0) ll[index++] = (byte)len;
            }
            if (ll[256] == 0) throw new InvalidOperationException("inflate: no end-of-block code");
            Codes(Build(ll, 0, nlen), Build(ll, nlen, ndist));
        }
    }
}
