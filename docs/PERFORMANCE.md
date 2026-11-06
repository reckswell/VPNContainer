# Cadence performance: smooth windows from a GT server

Virtuoso runs on a GT server (for example ece-linlab19) and its windows are
shown on your computer. This page is about making that display path fast.
It changes nothing in Cadence.

**Short version:** use `gtvpn xpra <host>` instead of `ssh -Y` for
Virtuoso. Virtuoso then draws on the GT server itself, and only compressed
pictures of what changed cross the tunnel, to a viewer that runs natively on
your computer. The session keeps running when the tunnel or VS Code
disconnects. Setup and daily use are in the
[README](../README.md#smoother-cadence-gtvpn-xpra).

## Why Virtuoso is slow over `ssh -Y`

With `ssh -Y`, Virtuoso is an X11 client of the X server on your computer
(WSLg on Windows), and every X11 request crosses the tunnel:

```
Virtuoso (GT) -> X11 -> ssh -> GlobalProtect tunnel -> container -> WSLg -> your screen
```

Three things add up, measured from a Windows PC to ece-linlab19:

- **Round trips.** Each request that waits for an answer costs a full round
  trip, 7 ms here. A toolkit makes dozens of them to open a form or a menu.
- **Bandwidth.** Virtuoso's GUI is Qt5, which paints windows in its own
  memory and sends the result as pictures. Locally Qt shares that memory
  with the X server (MIT-SHM), so this is free; remotely every redraw is a
  full, uncompressed picture: 6.4 MB for a 1600x1000 layout view, 14.7 MB
  for a maximized one at 2560x1440. The tunnel carries 113 Mbit/s
  (14 MB/s), so a single pan step of a maximized layout window needs about
  a second. ssh's compression helps 2.5x and is already on.
- **The tunnel itself.** GT's gateway gives this client a TLS-only tunnel
  (no ESP/UDP), so X11-over-ssh is TCP inside TCP, and a lost packet stalls
  everything behind it for a while.

On top of that, WSLg shows remote windows through an RDP connection capped
at 60 frames per second. That cap does not matter while X11 delivers 6.

## Options considered

| Option | Verdict |
|---|---|
| `ssh -Y` with compression (the default) | Fine for core-X programs (xterm, older Cadence forms), slow for Qt5 canvases: 6 frames/s in the lab |
| Faster ssh cipher (aes128-gcm) | No gain: the link, not encryption, is the limit |
| WSLg at 144 Hz (`.wslgconfig`) | Raises a ceiling X11 never reaches here; needs `wsl --shutdown`, which also stops Docker Desktop and the tunnel. Not worth it |
| VcXsrv, X410 or MobaXterm instead of WSLg | The same X11 protocol over the same tunnel, so the same limits, plus an X server listening on your computer |
| **xpra** (chosen) | X server on the GT server, compressed updates (lossless when still, video while moving), native windows on your computer at its refresh rate, sessions survive disconnects. Installs in your GT home without root |
| X2Go / NoMachine (NX) | Good compression, but the server side must be installed by GT's admins (not on ece-linlab19) |
| TigerVNC / TurboVNC (+ VirtualGL) | One desktop in one window, not native windows; TurboVNC is not installed there. Measured below as a reference |
| VirtualGL (`vglrun`), installed on ece-linlab19 | Only speeds up OpenGL. Virtuoso's layout canvas is 2D, so it has nothing to accelerate |
| FastX, ThinLinc | Commercial, server installed by admins |
| Waypipe | Wayland only; Virtuoso is X11 |

## Measurements

`tests/perf/` is a two-container lab that reproduces the link (7 ms X11
round trip, 113 Mbit/s, measured from a Windows PC to ece-linlab19) and two
Virtuoso-like canvases: a Qt5 layout view (1600x1000, hatched layers, full
redraw on every pan step, like Virtuoso's) and a core-X one (8000 stippled
rectangles drawn with X11 requests). `x11lat` presses a key with XTest on
the "desktop" X server and watches XDamage there, so it measures what you
see: key press to pixels on your screen.

| Path | Qt5 canvas: key to full redraw | Qt5: frames/s while panning | Open a 40-field form | core-X canvas: key to redraw, frames/s |
|---|---|---|---|---|
| Local (no network) | 4 ms | 180 | 12 ms | 12 ms, 112 |
| `ssh -Y`, compression on (the default) | 188 ms | 5.8 | 122 ms | 21 ms, 114 |
| `ssh -Y`, compression off | 468 ms | 2.0 | 245 ms | 21 ms, 81 |
| `ssh -Y`, compression off, aes128-gcm | 468 ms | 2.0 | 242 ms | |
| TigerVNC (one desktop in one window) | 91 ms | 12.9 | | 63 ms, 25 |
| xpra, lab viewer (see below) | 114 ms | 9.8 | 345 ms | |

What the numbers say:

- Over `ssh -Y`, the Qt5 canvas is limited by the link: 6.4 MB per frame at
  14 MB/s, 2.5 times less with compression. Faster ciphers change nothing.
  Core-X drawing (small requests, drawn by your X server) is fine, which is
  why xterm and old-style forms feel normal while layout views crawl.
- Anything that draws on the GT server and sends compressed pictures (VNC,
  xpra) is faster for Qt5 canvases.
- The xpra row is limited by the lab, not by the link. `xpra info` during
  the pan shows the server encoding a 1600x1000 frame in about 35 ms
  (WebP, 46 Mpixel/s on the lab's shared CPUs) and the lab's viewer
  decoding at only 9 Mpixel/s (software, no GPU, the same four virtual
  CPUs). A desktop PC decodes 10 to 20 times faster and paints with OpenGL,
  so the real numbers are measured on the real setup (below).

Run it yourself:

```bash
# xpra for both ends of the lab, from the same package list gtvpn installs on GT
micromamba create -y -p /tmp/xpra-env --file image/rootfs/usr/local/share/gtvpn/xpra/xpra-linux-64.lock
cd tests/perf
docker build -t perf/lab .
XENV=/tmp/xpra-env ./lab.sh            # RTT_MS=6 MBIT=113 by default
XENV=/tmp/xpra-env ./bench.sh          # ONLY=ssh / xpra / vnc / local to pick
```

On a real setup, `tests/perf/xput.py` (Python 3 and numpy, both on GT's
servers) does the same kind of full-window redraws on any `DISPLAY` and
prints the frames per second that display path delivers:
`python3 xput.py 12 180` in a terminal on the server, once with the `ssh -Y`
display and once with the xpra session's.

## How `gtvpn xpra` is built

- **Server:** `xpra seamless` with an Xvfb display on the GT server, started
  by `gtvpn-xpra` in the container over its shared connection (one Duo
  prompt per host for 8 hours, as for `ssh`). xpra 6.2 comes from
  conda-forge, installed with micromamba into `~/.local/gtvpn-xpra` from a
  pinned package list (`image/rootfs/usr/local/share/gtvpn/xpra/`), each
  package checked against its MD5; no root, about 850 MB.
- **Viewer:** xpra on your computer. On Windows `gtvpn xpra` installs
  xpra.org's portable build in `%LOCALAPPDATA%\Programs\Xpra` (no admin;
  the SHA-1 published next to it is checked, and the 200 MB download
  resumes after a dropped connection, which happened on the first real
  test). winget only has 6.3, which keeps light title bars in dark mode;
  6.5 follows the Windows theme. The viewer runs `ssh gtvpn` (the
  container's key-only, loopback sshd) with `gtvpn-xpra-proxy`, which relays the stream over the
  container's connection to `xpra _proxy` on the server. Nothing listens on
  the GT server or your computer, and without an open connection the proxy
  fails at once instead of signing in.
- **Display name:** like `gtvpn display`, the session's display is written
  to `~/.gtvpn-display` on the server, so VS Code terminals set `DISPLAY`
  the same way.
- **Windows look native:** the viewer's windows are ordinary Windows
  windows, so Windows draws their frames (Windows 11 title bar, caption
  buttons, rounded corners, snap layouts), and `--title=@title@` keeps each
  window's own title instead of adding " on <host>". With `ssh -Y`, WSLg
  draws a Linux-style frame instead.
- **Viewer settings** (`gtvpn`, `xpra_viewer_args`): `--min-quality=70`
  keeps thin lines readable while moving, `--auto-refresh-delay=0.1` sends a
  lossless picture 0.1 s after a window stops changing, `--refresh-rate=auto`
  follows your monitor (179 Hz here). Add your own with
  `GTVPN_XPRA_VIEWER_ARGS` in `.env`, for example `--min-quality=90` for
  sharper pans on a fast connection or `--video-scaling=off` to never send
  scaled-down video.
