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

| Path | Qt5 canvas: key to full redraw | Qt5: frames/s while panning | Open a 40-field form | core-X canvas: key to redraw |
|---|---|---|---|---|
| Local (no network) | 4 ms | 180 | 12 ms | 12 ms |
| `ssh -Y`, compression on (default) | 188 ms | 5.8 | 122 ms | 21 ms |
| `ssh -Y`, compression off | 468 ms | 2.0 | 245 ms | |
| `ssh -Y`, compression off, aes128-gcm | 468 ms | 2.0 | 242 ms | |
| xpra (lab: software viewer on 4 shared CPUs) | 114 ms | 8.8 | 345 ms | |

The xpra row is limited by the lab, not by the link: there the viewer
paints with the CPU on the same four virtual CPUs that also run the
server's video encoder. On a desktop PC the viewer paints with the GPU.

Run it yourself:

```bash
cd tests/perf
docker build -t perf/lab .
XENV=/path/to/xpra-env ./lab.sh        # an xpra conda env, see lab.sh
XENV=/path/to/xpra-env ./bench.sh      # ONLY=ssh / xpra / vnc / local to pick
```

## How `gtvpn xpra` is built

- **Server:** `xpra seamless` with an Xvfb display on the GT server, started
  by `gtvpn-xpra` in the container over its shared connection (one Duo
  prompt per host for 8 hours, as for `ssh`). xpra 6.2 comes from
  conda-forge, installed with micromamba into `~/.local/gtvpn-xpra` from a
  pinned package list (`image/rootfs/usr/local/share/gtvpn/xpra/`), each
  package checked against its MD5; no root, about 850 MB.
- **Viewer:** xpra on your computer (Windows: `winget install -e --id
  xpra-org.xpra`). It runs `ssh gtvpn` (the container's key-only, loopback
  sshd) with `gtvpn-xpra-proxy`, which relays the stream over the
  container's connection to `xpra _proxy` on the server. Nothing listens on
  the GT server or your computer, and without an open connection the proxy
  fails at once instead of signing in.
- **Display name:** like `gtvpn display`, the session's display is written
  to `~/.gtvpn-display` on the server, so VS Code terminals set `DISPLAY`
  the same way.
- **Viewer settings** (`gtvpn`, `xpra_viewer_args`): `--min-quality=70`
  keeps thin lines readable while moving, `--auto-refresh-delay=0.1` sends a
  lossless picture 0.1 s after a window stops changing, `--refresh-rate=auto`
  follows your monitor (179 Hz here). Add your own with
  `GTVPN_XPRA_VIEWER_ARGS` in `.env`, for example `--min-quality=90` for
  sharper pans on a fast connection or `--video-scaling=off` to never send
  scaled-down video.
