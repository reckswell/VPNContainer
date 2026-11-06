# gtvpn design

## Goal

Run Georgia Tech's GlobalProtect VPN so that:

1. GT-bound work (SSH, VS Code remote, Cadence Virtuoso / Vivado over X11)
   reaches GT **only** through the tunnel.
2. Nothing else on the computer is routed to GT, and GT has no path back
   into the computer.
3. It feels native: one command to sign in, VS Code attaches, GUI tools pop
   up as normal windows on Ubuntu 24.04 and on Windows.
4. Overhead stays tiny when idle, with CPU/memory limits you set.
5. Claude Code (and other dev tools) work inside it for future projects,
   without that traffic going through GT.

## Threat model

| Concern | Mitigation |
|---|---|
| Host traffic (browser, games, personal apps) ends up on GT's network | The tunnel exists only in the container's network namespace. The host's routing table, DNS and interfaces are never touched. Checked by `host/verify-host.sh` (H7–H15). |
| GT (or a compromised GT host) reaches into the computer | The container accepts no inbound connections from the tunnel (INPUT policy DROP; only sshd on the Docker-side interface, published to `127.0.0.1`). The proprietary GlobalProtect agent (HIP scans, enforced policies) is not installed; the open-source client sends a HIP report only if you enable it. |
| GT-bound traffic leaks outside the tunnel (tunnel drops, DNS falls back, client crashes) | Fail-closed kill-switch: only the VPN client's own sockets may use the WAN interface, and only on the VPN control ports. The interactive user can use the tunnel and nothing else. Tunnel-down, crash and tampering cases are tested in the lab. |
| DNS leaks | Docker's DNS (`127.0.0.11`, which forwards to your home resolver) is reserved for the VPN client and the egress proxy. Your tools can only resolve through GT's DNS over the tunnel. GT never sees the lookups for the direct-egress hostnames, because those are pinned from Docker DNS. |
| Container reaches your LAN / Docker host / other containers | Private, link-local and multicast ranges are rejected on the WAN interface. Inter-container traffic is disabled on the gtvpn network. |
| IPv6 side channel | IPv6 is disabled by sysctl, and ip6tables drops everything. |
| Someone (or something) opens the firewall at runtime | A watchdog fingerprints the ruleset every 15 s. On drift it tears the tunnel down and re-applies the kill-switch. |
| `dev` becomes root inside the container and drops the kill-switch | Every script root runs, and every directory above one, is root-owned and not writable by `dev`. The image build normalises modes (a checkout on a Windows drive shows every file as 0777) and refuses to finish otherwise. Checked again at every start (F4). Root writes into the shared `runtime/out` folder only through `gtvpn-outwrite` (temp file + rename), so a symlink `dev` plants there cannot redirect the write. |
| The container gets the host to run something | The host treats `runtime/out` as untrusted: `gtvpn connect` opens only an `https://` sign-in address or the container's own regular `login.html`, never a program, a path or another URL scheme. |
| X11 exposure | Remote GT apps can see other **X11** windows on your display. On Ubuntu's Wayland session and on WSLg, your normal apps are native Wayland/Windows apps and are not visible to X clients. Untrusted forwarding (`ssh -X`) can be enabled per host, but Virtuoso normally requires trusted (`-Y`). |

Out of scope: a malicious *root* process inside the container (root holds
`NET_ADMIN` in that namespace and could rewrite the rules; the watchdog
catches that afterwards, not before). Everything you run as `dev` cannot
change the firewall.

## Approaches considered

| Option | Isolation | Linux | Windows | X11 | VS Code | Overhead | Verdict |
|---|---|---|---|---|---|---|---|
| **Container (Docker/Podman) + open-source GP client** | Own network namespace; kill-switch inside | Native | WSL2 + WSLg | Host Xwayland / WSLg socket | Dev Containers or Remote-SSH | ~15 MB RAM idle | **Chosen** |
| Official GlobalProtect app on the host | None: captures host traffic (HIP agent too) | Yes | Yes | n/a | n/a | Agent always on | Rejected; this is what we are avoiding |
| Plain `ip netns` + openconnect | Good | Yes | No (no netns on Windows) | Manual | Hard (no attach target) | Lowest | Linux-only, fiddly |
| Dedicated WSL2 distro running the VPN | **Weak**: all WSL2 distros share one network namespace/VM, so the VPN would affect every distro | No | Yes | WSLg | Remote-WSL | Medium | Rejected (not isolated) |
| Full VM (VirtualBox/Hyper-V) | Strong | Yes | Yes | Inside VM | Remote-SSH | High (you reported it is too slow) | Rejected |
| Firejail / systemd-nspawn | Good | Yes | No | Possible | Awkward | Low | Linux-only |

The container wins on the only combination that matters here: real
network-namespace isolation on **both** OSes, a single artifact, an easy VS
Code target, and idle cost near zero. On Windows it runs inside WSL2 (a
lightweight utility VM that is already there), with the VPN still confined
to the container's own namespace *inside* that VM.

## Architecture

```
 Host (Ubuntu 24.04 or Windows+WSL2)                  ┌──────── gtvpn container (own netns) ──────────────────────────┐
                                                      │                                                               │
  VS Code ──docker exec (Dev Containers, as dev) ────►│ VS Code server, terminals                                     │
  ssh ──► 127.0.0.1:2222 ────────────────────────────►│ sshd (key-only, user dev, -L/-W forwarding only)              │
  terminal: GT password + Duo code ──────────────────►│ gtvpn-connect ─► openconnect ─► gateway ─► tun0 ═════════════╪══► GT
  (SSO portals: browser ◄─ login.url, callback link ─►│  gpauth (SAML), gtvpn-callback)                               │  (all GT traffic)
  X server (Xwayland / WSLg) ◄── /tmp/.X11-unix ──────│ ssh -Y to GT host  ◄═══ X11 over the tunnel ═══ Virtuoso      │
                                                      │                                                               │
                                                      │ tinyproxy 127.0.0.1:3128 ─(allow-list, 443 only)─► eth0 ──────┼──► api.anthropic.com,
                                                      │   (policy-routed, never via tun0)                             │    github.com, ...
                                                      │                                                               │
                                                      │ kill-switch: everything else → REJECT (counted)               │
                                                      └───────────────────────────────────────────────────────────────┘
```

### Identities and what each may do

| Identity | Who | tun0 | WAN (eth0) | Docker DNS |
|---|---|---|---|---|
| `root:gpvpn` | gpclient, gpauth | yes | TCP 443, UDP 4501 to public IPs only | yes |
| `tinyproxy:gtegress` | direct-egress proxy | (policy-routed away) | TCP 443 to public IPs only, for allow-listed names | yes |
| `dev` | you, VS Code server, ssh, Claude Code | yes | **no** | **no** |
| anything else (incl. plain root) | | yes | **no** | **no** |

These are enforced with iptables `owner` matches. The policy is `DROP`, and
the catch-all is `REJECT` through a counting chain (`GTVPN_BLOCKED`). The
verifier uses that counter to prove each blocked attempt was stopped by the
kill-switch itself, rather than failing by accident.

## GlobalProtect client

We use [GlobalProtect-openconnect](https://github.com/yuezk/GlobalProtect-openconnect)
3.2.0 (`gpclient` + `gpauth`), taken from its official image pinned by
digest. Those are musl binaries; the Dockerfile copies them, their exact
shared libraries and the musl loader onto Ubuntu 24.04, because the VS Code
server and Claude Code need glibc. `gpclient` embeds libopenconnect, so no
separate openconnect build is needed.

### How GT's VPN works (learned from the official app)

To match the official client, we read the official GlobalProtect app's
settings and logs on a Windows PC where it was installed, and surveyed its
install files (file list, driver INF files, text found in the binaries).
Nothing was run, patched or connected. What GT does:

- **Portal `vpn.gatech.edu` is only a portal.** After sign-in its config
  lists the real gateways, both priority 1: `ni-ext-gw.vpn.gatech.edu` and
  `dc-ext-gw.vpn.gatech.edu`. Its `/ssl-vpn/prelogin.esp` answers "gateway
  does not exist". The official app picks the faster gateway.
- **The portal answers the password with a Duo challenge.** The official
  app logs "Handle Challenge request" one second after the password. The
  first client we used (gpclient 3.2.0) handles challenges only at a
  gateway: at the portal it read the challenge as a config with no gateways
  ("No gateways found", then "gateway does not exist"). Signing in at the
  gateway directly (`--as-gateway`) was refused after 25 s with no reason
  given (most likely the Duo push it had been told to send, which never
  arrives on this account). So password sign-ins now use **OpenConnect**
  (Ubuntu's 9.12), the reference open-source GlobalProtect client, which
  handles Duo challenges at the portal and at a gateway.
- **Sign-in is username + password at the portal, then Duo** as a second
  prompt ("Choose a secondary factor from ('push', 'phone') or enter
  passcode"). At GT the answer is the 6-digit passcode shown in Duo Mobile
  (no push). No SSO (`use-sso=no`, no SAML in prelogin). For the official
  app the gateway then accepts the portal's sign-in ("Using portal's user
  credential to login to gateway"), so there is one Duo prompt per connect.
- **What the gateways did with OpenConnect.** Reporting itself as the
  official Windows app (`GTVPN_OS=Windows`, `GTVPN_CLIENT_VERSION=6.2.8-243`),
  OpenConnect passed the portal and Duo every time, but the NI gateway
  answered HTTP 512 (invalid credentials) both to the portal's sign-in and
  to the GT password typed again. A published OpenConnect sign-in at GT
  (June 2025) used OpenConnect's own Linux identity and the DC gateway: the
  gateway took the portal's sign-in with no further prompt. With
  OpenConnect's own Linux identity (empty `GTVPN_OS` and
  `GTVPN_CLIENT_VERSION`, now the default) the portal handed over no pass
  for the gateway, and the DC gateway ran its own full sign-in: it accepted
  the GT password, sent its own Duo challenge, and refused the Duo code
  that was typed 11 s after the portal's (Duo accepts each code once). So
  gtvpn now signs in at the gateway directly (`GTVPN_SKIP_PORTAL=1`,
  `GTVPN_GATEWAY=dc-ext-gw.vpn.gatech.edu`): one password, one Duo code.
- **Protocol:** portal `/global-protect/prelogin.esp` → `getconfig.esp`;
  gateway `/ssl-vpn/prelogin.esp` → `login.esp` → `getconfig.esp`, then the
  tunnel: IPsec ESP over UDP 4501 (aes-256-gcm/sha1), with a TLS fallback
  (`/ssl-tunnel-connect.sslvpn`). Full tunnel, GT DNS
  (130.207.244.251/.244), 3 h idle and session timeouts. OpenConnect (and
  gpclient, which embeds libopenconnect) speaks exactly this; the
  kill-switch lets only the `gpvpn` identity out, to TCP 443 and UDP 4501.
- **Tunnel interface:** on Windows a Palo Alto NDIS miniport ("PANGP Virtual
  Ethernet Adapter"); here a plain `tun0`, inside the container only.
- **HIP:** the gateway asks for a Host Information Profile report
  (`hip-report-needed=yes`), hourly. The official app collects it with the
  OPSWAT SDK: host info, anti-malware, firewall, disk encryption, patches,
  backup, DLP. GT's gateway answered every check with success, no
  quarantine and no remediation. The open-source clients' HIP reports
  (gpclient `--hip`, OpenConnect's `hipreport.sh`) describe an invented
  Windows PC (antivirus products that are not there), so gtvpn does not
  send one by default (`GTVPN_HIP=0`).

### Sign-in

**Password + Duo (GT):** `gtvpn connect` runs `gtvpn-connect` in your
terminal, which uses OpenConnect in two steps:

1. `openconnect --protocol=gp --usergroup=gateway --authenticate
   dc-ext-gw.vpn.gatech.edu` signs in interactively: username, password,
   then the Duo challenge (the Duo passcode). (With `GTVPN_SKIP_PORTAL=0`
   it signs in at the portal first, picks the gateway by
   `GTVPN_GATEWAY_NAME`, and the gateway asks for both again.) It
   prints only the session details (cookie, gateway URL, certificate pin)
   on stdout, which the script reads; the cookie never appears on a command
   line or in a file. Its prompts (stderr) pass through `gtvpn-prompts`,
   which labels each one `[PASSWORD]` or `[DUO CODE]` with what to type, and
   ends the sign-in if the gateway refuses it rather than letting
   OpenConnect ask again: repeated failed logins can lock a GT account.
   `--timestamp` puts the time on each step in the transcript.
2. `openconnect --cookie-on-stdin --background` starts the tunnel from that
   cookie, as root with primary group `gpvpn` (the identity the kill-switch
   lets out), in its own session with no terminal, so closing the window
   cannot stop it. Its log is `/var/log/gtvpn/gpclient.log` (root-only).

The password prompt does not echo, so the terminal transcript
(`/var/log/gtvpn/connect.log`, root-only, kept so an error survives a
closed window) never contains it. `GTVPN_CLIENT=gpclient` switches back to
gpclient for password portals that it handles.

**Saved password** (`gtvpn-login.conf`, optional, never committed):

- **Where it lives.** On Windows, `Start-GtVpn.cmd` turns a plain-text
  password into `password_dpapi` (DPAPI, current user), and `gtvpn` in WSL
  decrypts it with `powershell.exe` when it signs in. On Linux the file is
  kept at 0600.
- **How it gets in.** Before the sign-in, `gtvpn connect` pipes the username
  and password to `gtvpn-connect --stash`, which writes them to
  `/run/gtvpn/secret/signin`. That is a tmpfs that compose mounts root-only
  (0700), so it never reaches disk, and `dev` cannot read it. The sign-in
  takes it only if it is under two minutes old, and deletes it before
  anything else runs.
- **How OpenConnect gets it.** OpenConnect runs with `--passwd-on-stdin`.
  Its stdin is a FIFO in the same tmpfs, fed by a relay that writes the
  password and then `exec`s `cat` on the terminal, so the Duo code typed
  next reaches OpenConnect the same way. Echo is off for that time, so the
  code is not in the transcript. Neither value is ever an argument or an
  environment variable.
- **Refusals.** `gtvpn-prompts --saved-password` tells the two refusals
  apart. A 512 before any Duo challenge is the password, so it stops at
  once, and `gtvpn-connect` exits 3. The host CLI then writes
  `gtvpn-login.conf.refused` and stops using the saved password until the
  file is newer than that marker. A Duo-code refusal ends the sign-in as
  usual, and the next connect uses the saved password again, so each
  connect makes at most one try with it.
- **Older containers.** A container from before this change would read a
  stash as typed input and start a real sign-in with it. So `gtvpn connect`
  first checks that the container has `gtvpn-connect --stash` and the
  tmpfs. If it doesn't, connect recreates it on the current image when no
  tunnel is up, and otherwise sends nothing.

**SSO portals (SAML):** `gpauth --browser manual` normally prints a URL on
a random local port and waits for a pasted `globalprotectcallback:` link.
We automate both ends:

1. `gtvpn-connect` reads that URL and resolves it **inside** the container.
   It is a one-shot redirect (or an auto-posting form) to the login page.
   The result goes to `runtime/out/login.url`, so the host browser never
   needs network access into the container.
2. The host-side `gtvpn connect` watches that file and opens it in your
   default browser (or `GTVPN_BROWSER`, for example a private window).
3. After login, the browser opens `globalprotectcallback:…`. A per-user URL
   handler (an XDG `.desktop` file on Linux, HKCU registry on Windows) runs
   `gtvpn callback`, which passes it into the container on stdin
   (`gtvpn-callback`). Pasting into the terminal works too.
4. `gpauth` prints the auth cookie, and `gpclient connect --cookie-on-stdin
   --background` brings up `tun0`.

The SSO flow is exercised in the lab against a fake portal, up to the point
where the portal would issue a real cookie.

## X11

- **Ubuntu 24.04 (GNOME on Wayland):** the container mounts `/tmp/.X11-unix`
  read-only and gets a wildcard-family copy of your Xwayland cookie,
  refreshed on every `gtvpn up`. `dev` runs with your UID, so the
  `SI:localuser` fallback also works. Inside, `~/.Xauthority` holds the
  cookie under both the wildcard and `gtvpn/unix:N` names; without that,
  ssh's X11 forwarding fails with `Invalid MIT-MAGIC-COOKIE-1`, which the lab
  caught.
- **Windows:** WSLg is a built-in X server. The container mounts
  `/mnt/wslg/.X11-unix` from the WSL distro, with Docker Engine in WSL and
  with Docker Desktop alike (tested on Docker Desktop 4.98: its
  `/run/desktop/mnt/host/wslg` path is an empty folder in the daemon's
  view). No VcXsrv/X410 install, no cookie. `gtvpn up` warns if the
  container cannot see the socket.
- Inside the container, `ssh <alias>` uses `ForwardX11Trusted yes`,
  compression and connection multiplexing, so there is one Duo prompt per
  host for 8 hours.
- **VS Code Remote-SSH windows** run ssh on the computer (Windows OpenSSH
  through `ProxyJump gtvpn`), and VS Code only passes the computer's own
  `DISPLAY` along. The computer has no X server for GT, so those terminals
  get none. Rather than add an X server on Windows (another listener on
  the computer, plus restarting VS Code's server on the GT host),
  `gtvpn display <host>` (`gtvpn-display` in the container) keeps a
  background `ssh -Y -tt` session from the container on that host. sshd
  gives it a display (`localhost:N`, cookie in the shared
  `~/.Xauthority`), and the session writes the name to `~/.gtvpn-display`,
  so the VS Code terminal sets `DISPLAY` from it. The windows then take the
  same path as any container X11: tunnel, container, WSLg or Xwayland.
  - It is a client of the container's shared connection (`ControlMaster
    no`, same `ControlPath`), so it needs no extra Duo prompt when one is
    open and `stop` never closes that connection. With none open it signs
    in once with `ssh true` and `ControlPersist`, so the connection it
    leaves behind is the usual shared one, not owned by the bridge.
  - The server side runs `/bin/sh` explicitly (GT login shells may be
    tcsh) and waits in `sleep & wait` under a hang-up trap. Its pty is what
    makes cleanup reliable: when the session ends for any reason (`stop`,
    `ssh -O exit`, the master dying), sshd closes the pty, the shell gets
    `SIGHUP`, removes `~/.gtvpn-display` if it still names its display, and
    kills its sleep. Without a pty a `sleep` would outlive the session on
    GT's server.
  - Anyone else on the GT host can reach `localhost:N`, but sshd's fake
    cookie in your `~/.Xauthority` is required, the same exposure as any
    `ssh -Y` session.

### VNC sessions (`gtvpn vnc`)

X11 over ssh is slow for Virtuoso (Qt5 sends full-window pictures, every
request crosses the tunnel; numbers in [PERFORMANCE.md](PERFORMANCE.md)).
`gtvpn vnc <host>` moves the X server to the GT host instead, using only
what GT's servers already have: nothing is installed or stored there beyond
runtime files in `/tmp` and one cookie in `~/.Xauthority`, as `ssh -Y` adds.

- **Server side** (`share/gtvpn/vnc/remote.sh`, sent over the shared
  connection and run with `/bin/sh -s`, since login shells may be tcsh):
  the server's own TigerVNC `Xvnc` on a display picked per user (100 + uid
  mod 200, skipping used ones), with the server's window manager (metacity,
  else mwm or mutter). Xvnc has no TCP port at all (`-rfbport -1`,
  `-nolisten tcp`); it listens only on a Unix socket in `/tmp/gtvpn-<uid>`,
  which must be a real directory owned by the user with mode 0700 (the
  socket is 0600), so no other user of the shared server can reach it. VNC
  authentication is off (`-SecurityTypes None`) because that socket is the
  boundary; the X display itself needs the cookie, like any X server.
- **Transport:** `gtvpn-vnc` in the container, as a client of the shared
  connection (`ControlMaster no`, same `ControlPath`), asks the master to
  forward the container's `127.0.0.1:P` (P = 15900 + N) to that socket.
  The forward lives in the master, so nothing extra keeps running, and it
  ends with the connection. On the computer, a background `ssh -N -L
  127.0.0.1:P:127.0.0.1:P gtvpn` (the container's key-only loopback sshd,
  the same one VS Code uses) brings it to the viewer. The computer still
  never talks to GT, and nothing new listens on any network.
- **Sign-in:** `gtvpn vnc` runs `gtvpn-vnc start` with a terminal, so a
  missing connection is opened the usual way (`ssh true` with
  `ControlPersist`, one Duo prompt), exactly like `gtvpn display`. `attach`
  and `display` never sign in.
- **Lifetime:** the session is independent of every connection; `stop`
  refuses while programs other than the window manager have windows open
  (Virtuoso would lose unsaved work) unless `--force`, then removes the
  cookie and the folder, and cancels the forward. It never touches
  `~/.gtvpn-display`, which belongs to `gtvpn display`.
- **Not taken:** xpra (separate native windows, but it would have to be
  installed in the GT home), X2Go and NX (root packages on the server),
  FastX (installed on GT's servers, but its service is not running).
  Details in [PERFORMANCE.md](PERFORMANCE.md).

#### gtvpn's own viewer (`gtvpn vnc <host> windows`)

TigerVNC's viewer shows the session as one desktop. To give every Cadence
window a native Windows window instead, without anything new on the GT
server, `windows/viewer` (C# 5, about 2000 lines, no dependencies) talks to
the same session twice:

- **Pictures:** an RFB client of Xvnc. It asks for ZRLE first (lossless;
  on CAD pictures, with few colours and fine hatching, its 1-, 2- and
  4-bit packed palettes give Xvnc about half the zlib work of Tight's 8-bit
  indices, and the link 30% fewer bytes), at compression level 1, with
  Tight (and JPEG) as the fallback. Continuous updates with Fence answers
  let Xvnc stream frames without a request per frame, paced by its own
  congestion control. The cursor shape comes along, so the pointer is drawn
  by Windows at the monitor's refresh rate. zlib is .NET's native
  `DeflateStream`. The desktop is resized to the size of Windows' virtual
  screen (ExtendedDesktopSize), so X coordinates are Windows coordinates.
- **Windows:** an X11 client of the session's display, as its window
  manager. `gtvpn-vnc windows` starts the session without metacity (or the
  viewer takes the `WM_S0` selection and metacity steps aside), and asks
  the master for a second forward, `127.0.0.1:Q` (Q = 16300 + N) to the
  display's socket `/tmp/.X11-unix/XN`. The viewer authenticates with the
  display's cookie, which `gtvpn-vnc cookie` reads from `~/.Xauthority` and
  which reaches the viewer only in an environment variable. It redirects
  the root's map and configure requests, does not reparent (the frame is
  Windows'), and keeps the EWMH basics (`_NET_SUPPORTING_WM_CHECK`,
  `_NET_ACTIVE_WINDOW`, `_NET_WM_STATE` maximize, `_NET_CLOSE_WINDOW`,
  `_NET_CLIENT_LIST`).
- **One native window per X window:** a Windows Forms window whose client
  area is the X window's rectangle and which shows that part of the
  framebuffer (a DIB section and `BitBlt`). Title, moves, resizes,
  activation, minimize, maximize and close go both ways (moving a Windows
  window moves the X window; Cadence opening a dialog opens a Windows
  window, owned by its parent). Override-redirect windows (menus,
  tooltips, combo-box lists) become borderless, topmost windows that never
  take focus. Windows 11's dark title bar follows the system theme, also
  when you switch it while windows are open. Dialogs (transient or
  `_NET_WM_WINDOW_TYPE_DIALOG`) get no maximize button, as on Windows;
  `_MOTIF_WM_HINTS` can take away minimize, maximize or the whole frame,
  and splash screens have none. A window that opens while another program
  is in front flashes its taskbar button (Windows does not let it take the
  foreground). Windows are grouped on the taskbar by their program: each
  gets the AppUserModelID `GtVpn.X.<WM_CLASS>`, named after its WM_CLASS
  (the relaunch display name; no pinning, since a pinned button could not
  start it), and its `_NET_WM_ICON` as its icon.
- **Dragging:** while you drag a window by its title bar, Windows moves its
  picture with the frame and the X window stays put; at the drop the
  viewer moves the X window once. Moving it at every step would paint the
  window from framebuffer pixels a network round trip behind its frame,
  so the content trailed the frame. Until Xvnc's copy of the moved window
  arrives (35-57 ms on the real setup, at most 300 ms), the viewer shows
  its own copy there and keeps the framebuffer's older updates for that
  rectangle out of the picture. Snaps, keyboard moves and a program moving
  its own window go the same way. A resize by a border is sent at most
  every 15 ms.
- **Input:** mouse and keys go to Xvnc as RFB events at root coordinates
  (Windows virtual keys become X keysyms, with Ctrl and Alt kept as
  modifiers); focus goes through the window manager. Text copied on
  either side is offered on the other (RFB cut text). Xvnc runs with
  `-SendPrimary=0`, so only the CLIPBOARD selection comes to Windows: a
  mouse selection (PRIMARY) does not replace the Windows clipboard. A
  session started before that option is changed with `vncconfig -set` when
  it is reused, where the server has vncconfig.
- **With Smart App Control on:** Windows 11's Smart App Control blocks
  programs that are not signed and that it has not seen before; every
  build of the viewer on the PC is one. Signing would take a paid
  certificate (or a free open-source one, which needs a release history
  first), and running it in memory through PowerShell would get around a
  protection the user chose to keep. So `windows` then runs the same
  viewer under mono in a container of its own (`gtvpn-viewer`, image from
  `windows/viewer/Dockerfile`), in the gtvpn container's network namespace
  (the forwards are its loopback ports, and nothing listens on the PC),
  with the X socket the gtvpn container gets: WSLg shows each of its X
  windows as a window on the Windows desktop (a RemoteApp of Microsoft's
  signed msrdc). The cookie reaches it through stdin. Under mono the
  viewer's message loop runs without a main form, since Mono's WinForms
  timers wait for the main form to load, and keys come from WM_CHAR.
  WSLg's window manager places a new window by the size hints Mono writes
  when it creates it, frame first: the viewer places windows before they
  exist, learns the frame's size from the first one that lands and keeps
  it on the session's root (`_GTVPN_FRAME`), so client areas land on the
  session's coordinates and a restarted viewer moves nothing. Popups are
  `WS_POPUP` without a caption, which Mono makes override-redirect, so
  menus and tooltips stay where Cadence puts them.
  The frames are WSLg's (Weston's) rather than Windows 11's.
  `GTVPN_VIEWER` overrides the choice; the state comes from
  `HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy`
  `VerifiedAndReputablePolicyState` (1 = on).
- **Build and lifetime:** `gtvpn vnc <host> windows` compiles it on first
  use (and again when its sources change) with the `csc.exe` of .NET
  Framework 4, which is part of Windows 10 and 11, into
  `%LOCALAPPDATA%\gtvpn`; nothing is downloaded. When it closes, Xvnc and
  every program keep running; `attach` starts metacity again for the
  TigerVNC viewer (`gtvpn-vnc wm`). The lab runs it under mono and checks
  that the native windows show the session's exact pixels, that typing and
  clicks arrive, and that it is the session's window manager.

## SSH hosts (`hosts.conf`)

One file in standard OpenSSH syntax lists your GT servers, and both sides
read it:

- **Container:** `gtvpn hosts` (and `gtvpn up`) checks it with `ssh -G`
  and copies it to `runtime/in`. `gtvpn-hosts` then installs a root-owned
  0644 copy as `/etc/gtvpn/user-hosts.conf`, included from
  `ssh_config.d/40-gtvpn-user.conf`. A plain include of the mounted file
  would not work: ssh refuses config files that others can write, and a
  Windows-drive checkout shows every file as 0777. A file with errors is
  refused (validated as `dev`, so a `Match exec` never runs as root), and
  the previous copy stays. The GT defaults (X11, compression,
  multiplexing) come after your settings, so yours win. They apply to every
  plain alias in the file and, through `Match host`, to any target whose
  real name is under `gatech.edu` or in GT's address ranges.
- **Host / VS Code:** each plain alias becomes `gt-<alias>` with only its
  resolved HostName, User and Port plus `ProxyJump gtvpn`. Other options
  are not copied, so container-only settings such as ControlMaster never
  reach Windows OpenSSH, which lacks them. On WSL the Windows copy
  (`%USERPROFILE%\.ssh\gtvpn\config`) is rewritten too.

## VS Code

- **Primary: Dev Containers "Attach to Running Container"** (`gtvpn code`).
  VS Code enters through `docker exec`, so there is no network path at all.
  The image label `devcontainer.metadata` and the attach config that
  `gtvpn setup` installs (`vscode/attached-container-config.json`, in VS
  Code's globalStorage, on WSL the Windows one) both set `remoteUser: dev`.
  Root is never used, because root holds NET_ADMIN and could rewrite the
  kill-switch. Dev Containers downloads the server on the host and copies
  it in. Processes it starts are ordinary `dev` processes in the container's
  namespace, so the kill-switch applies to them exactly as to ssh sessions
  (the lab checks this through `docker exec -u dev`).
- **What Dev Containers forwards from the host**, and how it is limited:
  git credentials and Docker credentials are turned off by the settings
  snippet (`dev.containers.gitCredentialHelperConfigLocation: none`,
  `dev.containers.dockerCredentialHelper: false`). Its ssh-agent socket
  (`/tmp/vscode-ssh-auth-*.sock`) has no setting, so interactive shells in
  the container unset `SSH_AUTH_SOCK` when it points there. Nothing from GT
  can reach these: INPUT is DROP on the tunnel, so only processes you start
  in the container could.
- **GT hosts directly:** `gt-<alias>` entries use `ProxyJump gtvpn`, and
  so does a `Match host *.gatech.edu,128.61.*,130.207.*,143.215.*` block
  for any other GT server typed by name or address (VS Code Remote-SSH's
  *Connect to Host*; `GTVPN_SSH_VIA_CONTAINER=0` turns it off; `10.*` is
  left out because home networks use it too). The host's ssh/VS Code
  reaches the GT host *through the container's tunnel*. VS Code runs
  Remote-SSH on the host even in a container window, so this jump is how
  it gets to GT from inside the container.
  Container sshd allows only local (`-W`/`-L`) forwarding, and those
  connections are opened as `dev`, so they are tunnel-only. The lab checks
  that a jump cannot reach the internet or LAN.
- **Also possible: Remote-SSH to `gtvpn`** (`127.0.0.1:2222`, key-only, host
  key pinned by `gtvpn setup`). With `remote.SSH.localServerDownload:
  auto`, the container's own download of VS Code's server is rejected at
  once by the firewall, and VS Code falls back to downloading it on the
  host and copying it in (over SFTP, which the container's sshd has). Not
  `always`: that also copies the server into GT servers, and on those
  with SFTP turned off (ece-linlab19) the install then waits forever.

## Direct egress for Claude Code

A sealed container would make Claude Code unusable in split-tunnel mode, and
in full-tunnel mode it would send that traffic through GT. Instead:

- tinyproxy on `127.0.0.1:3128` with a default-deny, exact-hostname
  allow-list (`GTVPN_DIRECT_ALLOW`), CONNECT to port 443 only. It refuses IP
  literals and always refuses `*.gatech.edu`.
- Its sockets are marked and policy-routed (`ip rule fwmark → table 166`)
  out of `eth0` with MASQUERADE, so they bypass GT even when GT pushes a
  default route.
- Allow-listed names are resolved through Docker DNS and pinned in
  `/etc/hosts`, refreshed every 5 minutes, so GT's DNS never sees them.
- `HTTPS_PROXY`/`NO_PROXY` are set for `dev` sessions. `NO_PROXY` covers GT
  names and ranges so they still go straight into the tunnel.
- Claude Code itself is the official native binary from the npm platform
  package, checked against the registry's sha512 integrity hash at build.
  The auto-updater is disabled; `gtvpn rebuild` updates it.

Set `GTVPN_DIRECT_EGRESS=0` for a container that can reach GT and nothing
else.

## Verification ("recursive checks")

Three layers, run continuously by `gtvpn watch`:

1. **Container** (`gtvpn-verify`): firewall integrity and policy, IPv6 off,
   root-run files not writable by `dev`, and 13 blocked-path probes as `dev`, as plain root and as the VPN client
   (internet, DNS servers, Docker DNS, Docker host, LAN, wrong ports, IPv6,
   name resolution while down). Each probe must fail **and** increment the
   kill-switch counter. A positive control checks that the VPN client can
   still reach the portal. With the tunnel up: VPN DNS servers routed via
   tun0, GT names resolve, probe targets reachable via tun0, and the
   gateway is unreachable for anyone but the client. Direct-egress checks:
   loopback-only proxy, allow-listed host works, non-listed host/GT/raw IP
   and other ports refused, pins come from Docker DNS, policy routing goes
   via eth0, and the pinned IP is unreachable without the proxy.
   It runs automatically at container start, and the container refuses to
   start if it fails.
2. **Host** (`host/verify-host.sh`): container is not host-networked or
   privileged, capabilities and `no-new-privileges` are as expected, ports
   are on loopback only, sshd is unreachable from the LAN IP, the VPN
   address/routes/DNS are absent on the host, GT-internal probe targets are
   unreachable from the host, no GP client runs on the host (and on Windows
   the PANGP adapter is down), and host vs container egress IPs differ.
3. **Lab** (`tests/lab/run-lab.sh`, `run-x11.sh`): a real tunnel to an
   ocserv stand-in gateway, in split and full-tunnel modes, plus a fake
   allow-listed API, a fake GT ssh server and an Xvfb "host display". It
   runs all of the above plus host→container→GT ProxyJump, SSH auth
   hardening, kill -9 of the client, firewall tampering (watchdog), and the
   Cadence path (remote X app → ssh over tunnel → container → host X),
   including `gtvpn-display` for bash and tcsh accounts: another session on
   the server draws through it, and stop or the connection ending leaves
   nothing on the server.
   `run-cli.sh` drives the real host CLI, including a planted-symlink
   attack on `runtime/out` and hostile sign-in addresses. `run-perms.sh`
   builds the image from an all-0777 copy, as from a Windows drive.

## Resource use

Idle: sshd, tinyproxy and a few shell loops, about 5 MB RSS (measured) and ~0%
CPU. Limits come from `.env` (`GTVPN_CPUS`, `GTVPN_MEMORY`, `GTVPN_PIDS`),
and on Windows also from `.wslconfig` (`Setup-GtVpn.ps1 -Memory 6GB
-Processors 4`). The image is ~260 MB compressed with Claude Code (~100 MB
without).

## Extending later

- Other company VPNs: `gpclient` handles any GlobalProtect portal
  (`GTVPN_PORTAL`). For AnyConnect/Pulse/Fortinet, add `openconnect` to the
  image and switch `gtvpn-connect`; the kill-switch, verifier, X11 and VS
  Code layers are protocol-agnostic. For simultaneous VPNs, run one
  container per VPN (`COMPOSE_PROJECT_NAME`, different `GTVPN_SSH_PORT`).
- Heavier GUI use: if X11 over the VPN is too slow for Virtuoso, GT's
  remote desktop offerings can be reached the same way (for example VNC
  through `ssh -L` inside the container).
