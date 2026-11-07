#!/usr/bin/env python3
"""Widens a window on DISPLAY by dw pixels, then samples its new strip every
few milliseconds for ms and prints the share of samples where the strip is
mostly black (0x000000), as a viewer showing slowdraw.py's window would show
the session's cleared background. Exit status 0; the caller judges.

    python3 stripwatch.py window_id dw ms      (needs libX11; TRACE=1 prints samples)
"""
import ctypes, sys, time
X = ctypes.cdll.LoadLibrary('libX11.so.6')
X.XOpenDisplay.restype = ctypes.c_void_p; X.XOpenDisplay.argtypes = [ctypes.c_char_p]
X.XGetImage.restype = ctypes.c_void_p
X.XGetImage.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int, ctypes.c_int, ctypes.c_uint, ctypes.c_uint, ctypes.c_ulong, ctypes.c_int]
X.XResizeWindow.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_uint, ctypes.c_uint]
X.XFlush.argtypes = [ctypes.c_void_p]; X.XSync.argtypes = [ctypes.c_void_p, ctypes.c_int]
X.XGetWindowAttributes.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_void_p]
X.XFree.argtypes = [ctypes.c_void_p]
win, dw, dur = int(sys.argv[1], 0), int(sys.argv[2]), float(sys.argv[3]) / 1000
d = X.XOpenDisplay(None)
attr = ctypes.create_string_buffer(256); X.XGetWindowAttributes(d, win, attr)
w, h = ctypes.c_int.from_buffer(attr, 8).value, ctypes.c_int.from_buffer(attr, 12).value
X.XResizeWindow(d, win, w + dw, h); X.XFlush(d)
t0 = time.monotonic(); samples = []
while time.monotonic() - t0 < dur:
    X.XSync(d, 0)
    im = X.XGetImage(d, win, w + 4, 4, dw - 8, h - 8, 0xffffff, 2)
    if not im: samples.append((time.monotonic() - t0, -1)); continue
    data = ctypes.c_void_p.from_address(im + 16).value; bpl = ctypes.c_int.from_address(im + 44).value
    n = (dw - 8) * (h - 8); black = 0
    buf = (ctypes.c_uint32 * (bpl // 4 * (h - 8))).from_address(data)
    for yy in range(0, h - 8, 4):
        for xx in range(0, dw - 8, 4):
            if buf[yy * (bpl // 4) + xx] & 0xffffff == 0: black += 1
    tot = ((h - 8 + 3) // 4) * ((dw - 8 + 3) // 4)
    samples.append((time.monotonic() - t0, black / tot, buf[0] & 0xffffff, buf[(h // 2) * (bpl // 4) + (dw // 2)] & 0xffffff))
    X.XFree(ctypes.c_void_p(data)); X.XFree(ctypes.c_void_p(im))
    time.sleep(0.003)
import os
if os.environ.get('TRACE'):
    for s in samples[::4]: print("  %4d ms black %.2f  px %06x %06x" % (int(s[0] * 1000), s[1], s[2], s[3]))
mostly = [s for s in samples if s[1] > 0.5]
print("samples %d, mostly black %d (%.0f%%), black from %s to %s ms" % (len(samples), len(mostly), 100.0 * len(mostly) / max(1, len(samples)),
      int(mostly[0][0] * 1000) if mostly else '-', int(mostly[-1][0] * 1000) if mostly else '-'))
