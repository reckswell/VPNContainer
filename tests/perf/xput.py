#!/usr/bin/env python3
"""Measure how fast full-window redraws reach an X display, the way Qt5
(Virtuoso's toolkit) sends them: one XPutImage of the whole window per
frame, no shared memory.  Runs on the GT server with nothing but Python 3,
ctypes, libX11 and numpy (RHEL 8's python3-numpy is enough).

    python3 xput.py [seconds] [target_fps] [WxH]      (defaults: 10 180 1600x1000)

It pans a hatched, layered layout picture (like a Virtuoso layout view) by a
few pixels per frame and waits for the X server after each frame (XSync),
so the frames/s it prints is what the display path delivers:

  * DISPLAY=localhost:N (ssh -Y):  every frame crosses the tunnel;
  * DISPLAY=:N of a gtvpn vnc session:  frames land in the server's own X
    server at memory speed, and Xvnc sends what changed to the viewer at
    the rate the link allows (count what reaches the screen separately).

It needs nothing stored on the server:  ssh <host> 'DISPLAY=... python3 - 12 180' < xput.py

It opens one window titled "gtvpn-xput" for the length of the test.
"""
import ctypes
import ctypes.util
import os
import sys
import time

import numpy as np

secs = float(sys.argv[1]) if len(sys.argv) > 1 else 10.0
target = float(sys.argv[2]) if len(sys.argv) > 2 else 180.0
W, H = (int(v) for v in (sys.argv[3] if len(sys.argv) > 3 else "1600x1000").split("x"))

# ---- the layout picture (BGRX), 2.5x the window in each direction ----------
BW, BH = int(W * 2.5), int(H * 2.5)
rng = np.random.RandomState(7)
img = np.zeros((BH, BW, 4), np.uint8)
yy, xx = np.mgrid[0:BH, 0:BW]
patterns = [(xx + yy) % 6 == 0, (xx - yy) % 6 == 0, (xx % 4 == 0) | (yy % 4 == 0),
            (xx + yy) % 3 == 0, np.ones((BH, BW), bool), ((xx // 2 + yy // 2) % 2) == 0]
colours = [(87, 139, 46), (30, 105, 210), (34, 139, 34), (60, 20, 220), (255, 255, 255),
           (255, 144, 30), (0, 215, 255), (214, 112, 218), (209, 206, 0), (47, 255, 173)]
for layer, col in enumerate(colours):
    pat = patterns[layer % len(patterns)]
    for _ in range(int(BW * BH / 9000)):
        horiz = rng.rand() < 0.5
        w, h = (rng.uniform(40, 400), rng.uniform(3, 12)) if horiz else (rng.uniform(3, 12), rng.uniform(40, 400))
        x0, y0 = int(rng.uniform(0, BW - w)), int(rng.uniform(0, BH - h))
        x1, y1 = x0 + int(w), y0 + int(h)
        region = img[y0:y1, x0:x1]
        m = pat[y0:y1, x0:x1]
        region[m, 0], region[m, 1], region[m, 2] = col
        region[0, :, :3] = col; region[-1, :, :3] = col; region[:, 0, :3] = col; region[:, -1, :3] = col
del yy, xx, patterns

# ---- X11 through ctypes ------------------------------------------------------
x = ctypes.CDLL(ctypes.util.find_library("X11") or "libX11.so.6")
vp, ul, ui, i_ = ctypes.c_void_p, ctypes.c_ulong, ctypes.c_uint, ctypes.c_int
x.XOpenDisplay.restype = vp; x.XOpenDisplay.argtypes = [ctypes.c_char_p]
x.XDefaultScreen.argtypes = [vp]
x.XRootWindow.restype = ul; x.XRootWindow.argtypes = [vp, i_]
x.XDefaultVisual.restype = vp; x.XDefaultVisual.argtypes = [vp, i_]
x.XDefaultDepth.argtypes = [vp, i_]
x.XCreateSimpleWindow.restype = ul
x.XCreateSimpleWindow.argtypes = [vp, ul, i_, i_, ui, ui, ui, ul, ul]
x.XStoreName.argtypes = [vp, ul, ctypes.c_char_p]
x.XSelectInput.argtypes = [vp, ul, ctypes.c_long]
x.XMapWindow.argtypes = [vp, ul]
x.XCreateGC.restype = vp; x.XCreateGC.argtypes = [vp, ul, ul, vp]
x.XCreateImage.restype = vp
x.XCreateImage.argtypes = [vp, vp, ui, i_, i_, ctypes.c_char_p, ui, ui, i_, i_]
x.XPutImage.argtypes = [vp, ul, vp, vp, i_, i_, i_, i_, ui, ui]
x.XSync.argtypes = [vp, i_]
x.XPending.argtypes = [vp]
x.XNextEvent.argtypes = [vp, vp]
x.XDestroyWindow.argtypes = [vp, ul]
x.XCloseDisplay.argtypes = [vp]

d = x.XOpenDisplay(None)
if not d:
    sys.exit("cannot open display %r" % os.environ.get("DISPLAY"))
scr = x.XDefaultScreen(d)
if x.XDefaultDepth(d, scr) not in (24, 32):
    sys.exit("needs a 24-bit display")
win = x.XCreateSimpleWindow(d, x.XRootWindow(d, scr), 0, 0, W, H, 0, 0, 0)
x.XStoreName(d, win, b"gtvpn-xput")
x.XSelectInput(d, win, (1 << 15) | (1 << 17))  # Exposure, StructureNotify
x.XMapWindow(d, win)
gc = x.XCreateGC(d, win, 0, None)
buf = ctypes.create_string_buffer(W * H * 4)
xi = x.XCreateImage(d, x.XDefaultVisual(d, scr), 24, 2, 0, buf, W, H, 32, 0)  # ZPixmap
ev = ctypes.create_string_buffer(192)
t_end = time.time() + 2  # wait for the window to be mapped
while time.time() < t_end:
    while x.XPending(d):
        x.XNextEvent(d, ev)
    time.sleep(0.02)

# ---- the measurement ---------------------------------------------------------
step = max(1, W // 200)
period = 1.0 / target if target > 0 else 0
times, frame, ox, oy = [], 0, 0, 0
t0 = time.time(); t_next = t0
while time.time() - t0 < secs:
    ox = (ox + step) % (BW - W); oy = (oy + step // 2) % (BH - H)
    ctypes.memmove(buf, np.ascontiguousarray(img[oy:oy + H, ox:ox + W]).tobytes(), W * H * 4)
    s = time.time()
    x.XPutImage(d, win, gc, xi, 0, 0, 0, 0, W, H)
    x.XSync(d, 0)
    times.append(time.time() - s)
    while x.XPending(d):
        x.XNextEvent(d, ev)
    frame += 1
    t_next += period
    lag = t_next - time.time()
    if lag > 0:
        time.sleep(lag)
    else:
        t_next = time.time()
el = time.time() - t0
times.sort()
pct = lambda p: times[min(len(times) - 1, int(p * len(times)))] * 1000
print("display=%s window=%dx%d frames=%d seconds=%.1f frames_per_s=%.1f putimage_ms_p50=%.1f p95=%.1f max=%.1f"
      % (os.environ.get("DISPLAY"), W, H, frame, el, frame / el, pct(0.5), pct(0.95), times[-1] * 1000))
x.XDestroyWindow(d, win)
x.XCloseDisplay(d)
