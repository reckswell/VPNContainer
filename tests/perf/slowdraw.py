#!/usr/bin/env python3
"""A window that draws late, to test what a viewer shows while a window is
resized: black background (and no bit gravity, so the X server clears all of
it on a resize), and after each expose it waits, then draws a red ramp with
white stripes over the whole window, as a slow program would.

    python3 slowdraw.py delay_ms WxH+X+Y [title]      (needs numpy and libX11)
"""
import ctypes, sys, time
import numpy as np
X = ctypes.cdll.LoadLibrary('libX11.so.6')
X.XOpenDisplay.restype = ctypes.c_void_p; X.XOpenDisplay.argtypes = [ctypes.c_char_p]
for f in ('XDefaultRootWindow', 'XCreateSimpleWindow', 'XDefaultGC', 'XDefaultVisual', 'XCreateImage'): getattr(X, f).restype = ctypes.c_void_p
X.XCreateSimpleWindow.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int, ctypes.c_int, ctypes.c_uint, ctypes.c_uint, ctypes.c_uint, ctypes.c_ulong, ctypes.c_ulong]
X.XDefaultRootWindow.argtypes = [ctypes.c_void_p]; X.XDefaultGC.argtypes = [ctypes.c_void_p, ctypes.c_int]; X.XDefaultVisual.argtypes = [ctypes.c_void_p, ctypes.c_int]
X.XSelectInput.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_long]; X.XMapWindow.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
X.XStoreName.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_char_p]; X.XNextEvent.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
X.XPending.argtypes = [ctypes.c_void_p]; X.XFlush.argtypes = [ctypes.c_void_p]; X.XSync.argtypes = [ctypes.c_void_p, ctypes.c_int]
X.XCreateImage.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_int, ctypes.c_void_p, ctypes.c_uint, ctypes.c_uint, ctypes.c_int, ctypes.c_int]
X.XPutImage.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_uint, ctypes.c_uint]
delay = float(sys.argv[1]) / 1000
g = sys.argv[2]; wh, x0, y0 = g.split('+')[0], int(g.split('+')[1]), int(g.split('+')[2]); W, H = (int(v) for v in wh.split('x'))
d = X.XOpenDisplay(None); root = X.XDefaultRootWindow(d)
win = X.XCreateSimpleWindow(d, root, x0, y0, W, H, 0, 0, 0x000000)
X.XStoreName(d, win, (sys.argv[3] if len(sys.argv) > 3 else 'gtvpn-slowdraw').encode()); X.XSelectInput(d, win, (1 << 15) | (1 << 17)); X.XMapWindow(d, win); X.XFlush(d)
ev = ctypes.create_string_buffer(192); keep = None
def draw(w, h):
    global keep
    xx = np.arange(w, dtype=np.uint32)
    row = (((xx * 255 // max(w - 1, 1)) << 16) | 0x4080).astype(np.uint32)   # red ramp on green-blue
    img = np.tile(row, (h, 1)); img[::8, :] = 0xffffff                        # white stripes every 8 rows
    keep = np.ascontiguousarray(img)
    xi = X.XCreateImage(d, X.XDefaultVisual(d, 0), 24, 2, 0, keep.ctypes.data, w, h, 32, w * 4)
    X.XPutImage(d, win, X.XDefaultGC(d, 0), xi, 0, 0, 0, 0, w, h); X.XSync(d, 0)
while True:
    X.XNextEvent(d, ev)
    t = ctypes.cast(ev, ctypes.POINTER(ctypes.c_int))[0]
    if t == 22:                                               # ConfigureNotify
        W, H = ctypes.c_int.from_buffer(ev, 56).value, ctypes.c_int.from_buffer(ev, 60).value
    elif t == 12 and ctypes.c_int.from_buffer(ev, 56).value == 0:   # Expose, last of a run
        while X.XPending(d):
            X.XNextEvent(d, ev)
            t2 = ctypes.cast(ev, ctypes.POINTER(ctypes.c_int))[0]
            if t2 == 22: W, H = ctypes.c_int.from_buffer(ev, 56).value, ctypes.c_int.from_buffer(ev, 60).value
        time.sleep(delay); draw(W, H)
