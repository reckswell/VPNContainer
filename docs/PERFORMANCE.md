# Cadence performance: smooth windows from a GT server

Virtuoso runs on a GT server (for example ece-linlab19) and its windows are
shown on your computer. This page is about making that display path fast.
It changes nothing in Cadence.

**Short version:** use `gtvpn vnc <host>` instead of `ssh -Y` for
Virtuoso. Virtuoso then draws on the GT server itself, in a VNC session of
the server's own TigerVNC (nothing is installed there), and only compressed
pictures of what changed cross the tunnel, to a viewer on your computer.
The session keeps running when the tunnel or VS Code disconnects. Setup and
daily use are in the [README](../README.md#smoother-cadence-gtvpn-vnc).

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

On the real setup (a Windows PC, ece-linlab19, `ssh -Y` through the
container and WSLg), `tests/perf/xput.py` delivers 6.2 to 6.3 full
1600x1000 redraws per second, 158 to 161 ms each: what the lab below
predicts.

## Options considered

One rule shapes this: nothing may be installed on the GT server, not even
in your home folder. So the server side has to be something GT's servers
already have. ece-linlab19 has TigerVNC's `Xvnc` 1.15, the metacity, mwm
and mutter window managers, VirtualGL, and FastX 4 (whose service is not
running).

| Option | Verdict |
|---|---|
| `ssh -Y` with compression (the default) | Fine for core-X programs (xterm, older Cadence forms), slow for Qt5 canvases: 6 frames/s |
| Faster ssh cipher (aes128-gcm) | No gain: the link, not encryption, is the limit |
| WSLg at 144 Hz (`.wslgconfig`) | Raises a ceiling X11 never reaches here; needs `wsl --shutdown`, which also stops Docker Desktop and the tunnel. Not worth it |
| VcXsrv, X410 or MobaXterm instead of WSLg | The same X11 protocol over the same tunnel, so the same limits, plus an X server listening on your computer |
| **TigerVNC** (chosen): the server's own `Xvnc` | X server on the GT server, compressed updates (Tight: JPEG while moving, lossless when still), sessions survive disconnects. One desktop in one window, with Linux-style title bars inside |
| xpra | The best fit otherwise: separate native windows on your computer, video compression while moving. But it is not on GT's servers, and a home-folder install (850 MB) is ruled out |
| FastX 4 | Installed on ece-linlab19, with native windows; but its server is not running (only port 22 answers), and it needs its own sign-in. Worth asking GT ECE about |
| X2Go / NoMachine (NX) | Good compression, but the server side must be installed by GT's admins (not on ece-linlab19) |
| TurboVNC (+ VirtualGL) | Faster encoder than TigerVNC's, but not installed there |
| VirtualGL (`vglrun`), installed on ece-linlab19 | Only speeds up OpenGL. Virtuoso's layout canvas is 2D, so it has nothing to accelerate |
| Waypipe | Wayland only; Virtuoso is X11 |

### Window frames

With `ssh -Y`, each Cadence window is a WSLg window, and WSLg's compositor
(Weston) draws its frame: square corners, Linux-style buttons. WSLg has no
setting for it (microsoft/wslg issues 530 and 1371). With `gtvpn vnc`, the
viewer is one ordinary Windows window (Windows 11 title bar, rounded
corners, snap layouts), and Cadence's windows inside it get metacity's
frames. Native frames for each Cadence window need a viewer that turns
remote windows into local ones (xpra or FastX), which needs a server side
GT does not run today.

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
- The xpra row is limited by the lab, not by the link: `xpra info` showed
  the server encoding a 1600x1000 frame in about 35 ms and the lab's viewer
  decoding at only 9 Mpixel/s (software, on the same four shared virtual
  CPUs). So the numbers that count are measured on the real setup.

Run it yourself:

```bash
# xpra for the lab's xpra rows (only in the lab containers, never on GT)
micromamba create -y -p /tmp/xpra-env --file tests/perf/xpra-linux-64.lock
cd tests/perf
docker build -t perf/lab .
XENV=/tmp/xpra-env ./lab.sh            # RTT_MS=6 MBIT=113 by default
XENV=/tmp/xpra-env ./bench.sh          # ONLY=ssh / xpra / vnc / local to pick
```

On a real setup, `tests/perf/xput.py` (Python 3 and numpy, both on GT's
servers) does the same kind of full-window redraws on any `DISPLAY` and
prints the frames per second that display path delivers. It runs from
standard input, so nothing is stored on the server; from a container
terminal:

```bash
ssh ece-linlab19.ece.gatech.edu 'DISPLAY=localhost:10.0 python3 - 12 180' < tests/perf/xput.py   # ssh -Y (gtvpn display)
ssh ece-linlab19.ece.gatech.edu 'DISPLAY=:N python3 - 12 180' < tests/perf/xput.py             # the gtvpn vnc session
```

For the VNC session, xput.py's number is how fast the server-side X server
takes frames; what reaches your screen is what the viewer shows, so count
changes on the screen too (a screen sampler, or the viewer's F8 >
"Connection info", which shows the measured line speed and encoding).

## How `gtvpn vnc` is built

- **Server:** `Xvnc` (TigerVNC, from the server's own packages) on a
  private Unix socket, no TCP port, no VNC password (the 0700 folder is the
  boundary), `-FrameRate 180` (Xvnc sends at most that many updates per
  second; the default is 60), a 1920x1080 desktop that follows the
  viewer's window size (`-AcceptSetDesktopSize`), and the server's
  metacity for title bars and moving windows. Started by `gtvpn-vnc` in
  the container over its shared connection (one Duo prompt per host for 8
  hours, as for `ssh`).
- **Viewer:** TigerVNC's viewer on your computer, which `gtvpn vnc` starts
  with `-AutoSelect=1` (it measures the line and picks the encoding and
  picture quality) and `-RemoteResize=1`. Add
  your own options with `GTVPN_VNC_VIEWER_ARGS` in `.env`, for example
  `-FullScreen=1`, or `-AutoSelect=0 -PreferredEncoding=Tight
  -QualityLevel=9` for sharper pictures while panning. On Windows the
  viewer is TigerVNC's single-file build, downloaded once into
  `%LOCALAPPDATA%\Programs\TigerVNC` and checked against a pinned SHA-256.
- **Path:** viewer -> `127.0.0.1:P` on your computer -> `ssh gtvpn` (the
  container's key-only, loopback sshd) -> the container's `127.0.0.1:P` ->
  the container's shared ssh connection -> the session's socket on the GT
  server. Your computer never talks to GT.
- **Display name:** like `gtvpn display`, the session's display is written
  to `~/.gtvpn-display` on the server, so VS Code terminals set `DISPLAY`
  the same way.
