# Cadence performance: smooth windows from a GT server

Virtuoso runs on a GT server (for example ece-linlab19) and its windows are
shown on your computer. This page is about making that display path fast.
It changes nothing in Cadence.

**Short version:** use `gtvpn vnc <host>` instead of `ssh -Y` for
Virtuoso, and on Windows `gtvpn vnc <host> windows`. Virtuoso then draws on
the GT server itself, in a VNC session of the server's own TigerVNC
(nothing is installed there), and only compressed pictures of what changed
cross the tunnel, to a viewer on your computer that shows each Cadence
window as a native Windows window. Panning a layout-like view goes from 6
to 20-33 full redraws per second, pixel-exact, and the pointer and window
moves are drawn by Windows at your monitor's refresh rate. The session
keeps running when the tunnel or VS Code disconnects. Setup and daily use
are in the [README](../README.md#smoother-cadence-gtvpn-vnc).

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
| WSLg at the monitor's rate (`.wslgconfig`) | Over `ssh -Y`, it raises a ceiling X11 never reaches. With the viewer in its container (Smart App Control on), WSLg shows every frame, so `gtvpn vnc <host> windows` adds the monitor's rate and local window moves there; they apply after `wsl --shutdown`, which also stops Docker Desktop and the tunnel, so it says so rather than doing it |
| VcXsrv, X410 or MobaXterm instead of WSLg | The same X11 protocol over the same tunnel, so the same limits, plus an X server listening on your computer |
| **TigerVNC** (chosen): the server's own `Xvnc` | X server on the GT server, compressed updates, sessions survive disconnects. TigerVNC's viewer shows one desktop in one window, with Linux-style title bars inside |
| **gtvpn's own viewer** for that Xvnc (chosen on Windows) | Each Cadence window becomes a native Windows window: the viewer is a VNC client for the pictures and the session's window manager for the window list. Nothing new on the server; built from source with the C# compiler in Windows |
| xpra | The best fit otherwise: separate native windows on your computer, video compression while moving. But it is not on GT's servers, and a home-folder install (850 MB) is ruled out |
| FastX 4 | Installed on ece-linlab19, with native windows; but its server is not running (only port 22 answers), and it needs its own sign-in. Worth asking GT ECE about |
| X2Go / NoMachine (NX) | Good compression, but the server side must be installed by GT's admins (not on ece-linlab19) |
| TurboVNC (+ VirtualGL) | Faster encoder than TigerVNC's, but not installed there |
| VirtualGL (`vglrun`), installed on ece-linlab19 | Only speeds up OpenGL. Virtuoso's layout canvas is 2D, so it has nothing to accelerate |
| Waypipe | Wayland only; Virtuoso is X11 |

### Window frames

With `ssh -Y`, each Cadence window is a WSLg window, and WSLg's compositor
(Weston) draws its frame: square corners, Linux-style buttons. WSLg has no
setting for it (microsoft/wslg issues 530 and 1371). With TigerVNC's
viewer, the viewer is one ordinary Windows window and Cadence's windows
inside it get metacity's frames.

`gtvpn vnc <host> windows` gives every Cadence window Windows 11's own
frame: rounded corners, the dark or light title bar of your theme, snap
layouts, the taskbar and Alt+Tab. It works like xpra or FastX (remote
windows become local ones) but needs nothing on the server beyond Xvnc:
gtvpn's viewer takes the session's window-manager role, so it learns where
every window is and opens a native window for it, filled from the VNC
picture. Because Windows draws the frames, moving and resizing a window,
and the pointer, run at the monitor's refresh rate (179 Hz on the test
PC); only the window's content waits for the server. How it works is in
[DESIGN.md](DESIGN.md#gtvpns-own-viewer-gtvpn-vnc-host-windows).

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
changes on the screen too (a screen sampler, TigerVNC's F8 > "Connection
info", or the `updates/s` lines in `%TEMP%\gtvpn-viewer.log` of gtvpn's
viewer).

### On the real setup

A Windows 11 PC (2560x1440 at 179 Hz) through the container's tunnel to
ece-linlab19 (TigerVNC 1.15), `xput.py` panning a 1600x1000 hatched layout
picture, frames counted on the PC's screen (a 64x64 patch sampled 180
times a second):

| Path | Full redraws on your screen per second | Data | Server CPU |
|---|---|---|---|
| `ssh -Y` (WSLg) | 6.2 | ~40 MB/s, the link's limit | |
| `gtvpn vnc windows`, ZRLE (the default), program drawing at 60/s | 27-33 | 5-10 MB/s | Xvnc 54-94% of one core, sshd 19-33% |
| the same, program drawing at 30/s | 25 | 6-8 MB/s | Xvnc 57%, sshd 20% |
| `gtvpn vnc windows --encoding tight`, program at 60/s | 20-21 | 7-9 MB/s | Xvnc 63-92%, sshd 28-39% |
| the same, program at 30/s | 30 | 11-13 MB/s | Xvnc 88%, sshd 42% |
| either, program flooding (180/s) | 20-31 | 6-14 MB/s | Xvnc up to 94% |

Both encodings arrive pixel-exact on this picture (it has 11 colours, so
even Tight sends it as a palette); for photo-like areas Tight switches to
JPEG and ZRLE stays lossless. ZRLE needs 30-40% fewer bytes, and less of
the server's CPU per frame, so it is the default. Run-to-run noise is about
4 frames per second.

What limits it now is the GT server's Xvnc: it encodes on one thread, the
same thread that runs the X server for Virtuoso, and its congestion
control (which counts a frame as delivered when the viewer answers for it)
also paces frames by round trips, which is why Xvnc is not always busy. A program
that floods it (xput at 180/s) takes encoder time away and gets fewer
frames on screen than one drawing at 60/s. The viewer itself never used
more than a third of one core of the PC. Before the quick ACKs below,
`--compress 0` moved the limit to the server's sshd (92% CPU at 19 MB/s)
without more frames. With them, on the test PC at 1200x800 and 180 frames
a second, zlib level 1 pinned Xvnc at 94-98% of a core for 27-32 changes
a second on the screen, and level 0 gave 46 at half of Xvnc's CPU, sending
twice the bytes (23 MB/s through the tunnel). On a slower link, or for a
larger area changing, level 1 gets more through. So the viewer picks the
level as it goes (`--compress auto`, the default), by what is being sent:

- **A burst after a pause** (a window resized, a menu, a page of text) goes
  at level 1. After 100 ms without sending, Xvnc's congestion control starts
  again from a 16 KB window that doubles only every third round trip, so the
  fewer bytes of level 1 get through sooner. On the PC, resizing the CIW at
  level 0, every grow began with a 117-526 ms wait before its contents
  caught up with the edge (p90 152-526 ms over 7 films). At level 1 they
  caught up in 23-35 ms at the median and 82-140 ms at the 90th percentile.
- **Pictures that stream for 2 s** (a long pan, a program drawing at full
  rate) go at level 0. Counting pixels alone chose wrong here: level 1 got
  more pixels through (62 Mpx/s against 44), yet Xvnc, busy compressing,
  ran xput at 51 frames a second instead of 151, and the screen changed 35
  times a second instead of 46. Xvnc's CPU is also what draws Cadence.
- **Unless the link holds pictures back.** The viewer pings Xvnc four times
  a second (Fence). Only when streaming pings take at least 25 ms longer
  than on the idle link (pictures queued on the way) does it try level 1 for
  2 s. It keeps level 1 if 15% more pixels arrive a second while the link
  still carries 80% of level 0's bytes (still the limit, rather than Xvnc),
  or 2.5 times the pixels. It looks again 20 s later, then twice as long
  each time the level stays, up to 160 s.

In the lab with Tight (this Xvnc 1.13 ignores the level for ZRLE), 1200x800
xput at 180/s over a 10 ms link:

- At 1 Gbit/s, streaming stayed at 0 without a try (pings 16 ms against 11
  idle).
- At 80 Mbit/s, it took 1 (33-36 Mpx/s against 9, pings 61-128 ms).
- At 200 Mbit/s, it stays at 0 (25 Mpx/s, 26 updates a second, xput at
  124/s). Level 1 there got 43 Mpx/s at 11 MB/s, with xput at 42/s.

Part of that pacing was the link's own: ssh's local forward to Xvnc's Unix
socket leaves Nagle on for its end of the connection to the viewer, so the
last small piece of each update, and Xvnc's one-byte round-trip ping after
it, waited for an ACK that Linux delays up to 40 ms while the viewer has
nothing to send. Xvnc's congestion control took that for a slow link and
sent fewer frames (on the PC, 400x300 at 180/s flipped between about 40 and
about 180 updates a second from run to run). Through a real OpenSSH
forward in the lab it was 42 a second; the viewer now ACKs at once
(TCP_QUICKACK after every read, under Linux) and gets 163-176. The table
above predates that.

## How `gtvpn vnc` is built

- **Server:** `Xvnc` (TigerVNC, from the server's own packages) on a
  private Unix socket, no TCP port, no VNC password (the 0700 folder is the
  boundary), `-FrameRate 180` (Xvnc sends at most that many updates per
  second; the default is 60), a 1920x1080 desktop that follows the
  viewer's window size (`-AcceptSetDesktopSize`), and the server's
  metacity for title bars and moving windows. Started by `gtvpn-vnc` in
  the container over its shared connection (one Duo prompt per host for 8
  hours, as for `ssh`).
- **Viewer, `windows` (Windows):** gtvpn's own (`windows/viewer`), built on
  first use with the .NET Framework 4 C# compiler that is part of Windows.
  It asks for ZRLE with zlib's level chosen as it goes (1 for bursts, 0 for streams, above), streams frames (continuous
  updates), draws the cursor locally, and becomes the session's window
  manager over a second forward to the display (`127.0.0.1:Q`, Q = 16300 +
  N). Options via `GTVPN_VIEWER_ARGS` in `.env`: `--encoding tight`,
  `--compress 0-9` (a fixed level), `--quality 0-9`. Its log is `%TEMP%\gtvpn-viewer.log`.
  To compare, `GTVPN_VIEWER_CU=0` (environment or `.env`) turns continuous
  updates off, so it asks for each update, and `nofence` also drops Fence;
  `GTVPN_VIEWER_IMAGE` runs a given viewer image under mono as it is.
  Xvnc before 1.14 ignored the client's level for ZRLE (zlib's default
  halves the frame rate), so `gtvpn vnc` starts those with `-ZlibLevel=1`.
  A session also lets `vncconfig` change `FrameRate`, `CompareFB`, `Log`
  (and `ZlibLevel`) while it runs (`-AllowOverride`), so it can be tuned
  and traced without ending it, e.g. `DISPLAY=:N vncconfig -set FrameRate=120`.
- **Viewer, desktop (`attach`):** TigerVNC's viewer on your computer, which `gtvpn vnc` starts
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
- **Display name:** `gtvpn vnc` prints it (`:N`, 100 + your uid mod 200
  unless taken), and it is in `/tmp/gtvpn-<uid>/display` on the server
  while the session runs. Nothing is written in your GT home except the
  display's cookie in `~/.Xauthority`.
