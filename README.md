# gtvpn: Georgia Tech VPN in a sealed container

Connect to GT's GlobalProtect VPN **inside a small container**, not on your
computer. SSH, VS Code and Cadence/Vivado windows work as usual. Your
computer's own traffic never touches GT, and GT has no way back in.

```
  your computer ──(normal internet, untouched)──► the world
       │
       └─ gtvpn container ══ GlobalProtect tunnel ══► GT servers (ssh, Cadence, …)
            • kill-switch: nothing leaves except through the tunnel
            • VS Code attaches through Docker (Dev Containers), as user dev
            • ssh in on 127.0.0.1:2222 (key-only)
            • X11 → your desktop (Ubuntu: Xwayland, Windows: WSLg)
            • Claude Code, with its own allow-listed direct path (never via GT)
```

Idle cost is ~5 MB of RAM and ~0% CPU. Set CPU and memory limits in `.env`.
Design, threat model and the alternatives considered are in
[docs/DESIGN.md](docs/DESIGN.md).

---

## 1. Install (once per OS)

### Ubuntu 24.04

```bash
sudo apt install docker.io docker-compose-v2 git xauth
sudo usermod -aG docker $USER   # then log out and back in
git clone https://github.com/reckswell/VPNContainer ~/VPNContainer && cd ~/VPNContainer
./gtvpn setup
```

### Windows 10/11

1. In PowerShell: `wsl --install -d Ubuntu-24.04` (reboot if asked; an
   existing `Ubuntu` distro works too), then `wsl --update` to get WSLg, the
   built-in X server.
2. Docker, either way:
   - **Docker Desktop:** Settings → Resources → WSL integration → turn on
     your Ubuntu distro.
   - **Docker Engine inside WSL:** in the Ubuntu terminal, run the `apt`
     and `usermod` lines from the Ubuntu section above.
3. Clone the repo wherever you keep projects, for example
   `Documents\Projects` (Git for Windows is fine; `.gitattributes` keeps the
   scripts' Linux line endings), and run the setup script from it:

   ```powershell
   cd $HOME\Documents\Projects
   git clone https://github.com/reckswell/VPNContainer
   cd VPNContainer
   powershell -ExecutionPolicy Bypass -File .\windows\Setup-GtVpn.ps1 -Memory 6GB -Processors 4
   ```

   It finds the repo and your Ubuntu distro on its own (`-Distro` and
   `-RepoPath` override that), runs `./gtvpn setup` in WSL, sets up SSH for
   VS Code on Windows, registers the sign-in link handler, caps WSL's
   resources (`-Memory`/`-Processors` are optional) and adds a `gtvpn`
   command to PowerShell/cmd.

> On Linux, Docker Engine is preferred over Docker Desktop, because Docker
> Desktop's VM cannot use your X11 socket.

## 2. Configure

**Your GT servers** go in `hosts.conf` (created by `setup`), in the same
format as a normal `~/.ssh/config`. Add as many as you like:

```
Host cadence
    HostName <your-ece-server>.ece.gatech.edu
    User gburdell3

Host pace
    HostName login-ice.pace.gatech.edu
```

Then run `./gtvpn hosts`. Each host now works as `ssh cadence` inside the
container, with X11, compression and one Duo prompt per 8 hours already on,
and shows up in VS Code's Remote-SSH list as `gt-cadence`. Run `gtvpn hosts`
again after every edit. A file with mistakes is refused, and the previous
hosts stay in place.

**Everything else** is in `.env`. The settings that matter:

```bash
GTVPN_GT_USER=gburdell3          # used for GT hosts that don't set User
GTVPN_PROBE_TARGETS="<your-ece-server>.ece.gatech.edu:22"   # used by the leak tests
GTVPN_CPUS=2
GTVPN_MEMORY=2g
```

## 3. Daily use

```bash
./gtvpn up          # start the container (sealed: no network yet)
./gtvpn connect     # GT username, GT password, then the Duo passcode; tunnel comes up; checks run
./gtvpn ssh cadence # or: ./gtvpn shell, then `ssh cadence`
virtuoso &          # (on the GT host) windows open on your desktop
./gtvpn disconnect  # tunnel down; container stays sealed
./gtvpn down        # stop everything (your container home is kept)
```

**Sign-in:** `connect` asks, in your terminal, for three things, and each
prompt is labeled with what to type:

1. **GT username** (skipped if `GTVPN_USER` is set in `.env`).
2. **`[PASSWORD]`** your GT password.
3. **`[DUO CODE]`** the 6-digit passcode shown in Duo Mobile at that moment.
   It is not your password, and nothing is pushed to your phone.

Then the tunnel starts. `connect` signs in at a GT gateway directly
(`GTVPN_SKIP_PORTAL=1`, `GTVPN_GATEWAY=dc-ext-gw.vpn.gatech.edu`), because
going through the vpn.gatech.edu portal first makes the gateway ask for
your password and a new Duo code a second time. If GT refuses the sign-in,
`connect` stops instead of asking again, because repeated failed logins can
lock a GT account. Each Duo code works once: to retry, wait until Duo Mobile
shows a new one.
Everything, including the sign-in, happens inside the container. (For VPN
portals that use browser SSO instead, `connect` opens the login page in your
browser and a *globalprotectcallback* link hands the result back.)

### VS Code

- **Recommended:** `gtvpn code`, or *Dev Containers: Attach to Running
  Container… → gtvpn*. VS Code goes into the container through Docker
  itself (no network hop) and logs in as `dev`. Its terminal is inside the
  container, and `ssh cadence` with X11 just works there. Same on Windows
  and Ubuntu. `gtvpn setup` installs the attach config, and on Windows the
  setup script adds the Dev Containers extension.
- **Open a folder on a GT server directly:** Remote-SSH to `gt-cadence`, or
  `gt-<name>` for any host in `hosts.conf`. It hops through the container's
  tunnel automatically. Use this to edit files on the server. To start
  Cadence, use a `gtvpn code` window and run `ssh cadence` in its terminal,
  because that is the path with X11.
- Add [vscode/settings-snippet.jsonc](vscode/settings-snippet.jsonc) to
  your user settings. The container cannot reach VS Code's download servers
  (by design), so VS Code downloads its server and extensions on your
  computer and copies them in. The snippet also stops Dev Containers from
  handing your computer's git and Docker credentials to the container, and
  container terminals ignore the ssh-agent it forwards, so your personal
  keys stay on your computer.
- Also possible: *Remote-SSH → `gtvpn`* (or `gtvpn code --ssh`), over the
  container's loopback-only, key-only sshd.

### Cadence Virtuoso / Vivado (X11)

Inside the container, `ssh <alias>` already uses trusted X11 forwarding,
compression and connection sharing (one Duo prompt per host for 8 h).
Test it with `./gtvpn x11test` (shows xeyes from the container). On
Ubuntu, `gtvpn up` refreshes the Xwayland cookie each time. On Windows,
WSLg needs nothing at all.

### Claude Code for future projects

`./gtvpn claude` (or `claude` in any container terminal). Your projects can
live in `/home/dev/projects` (persistent volume), or share a host folder
through `./share` → `/home/dev/share`. Claude Code reaches Anthropic through
a filtering proxy that allows only the hostnames in `GTVPN_DIRECT_ALLOW`
(Anthropic, GitHub, npm and PyPI by default). That traffic leaves directly
and **never** goes through GT, even if GT sends you a full tunnel. Add hosts
to that list as projects need them. Set `GTVPN_DIRECT_EGRESS=0` to make the
container GT-only. Update Claude Code with `./gtvpn rebuild`.

## 4. Verify (as often as you like)

```bash
./gtvpn verify      # ~45 checks: container side + host side, PASS/FAIL each
./gtvpn watch 60    # re-run everything every 60 s, desktop alert on failure
./gtvpn status      # tunnel, routes, DNS, blocked-attempt counter
```

What is checked (details in [docs/DESIGN.md](docs/DESIGN.md#verification-recursive-checks)):

- **Inside:** firewall intact with policy DROP and IPv6 off. As your user,
  as root and as the VPN client, every path out except the tunnel is
  rejected (internet, foreign DNS, Docker DNS, Docker host, LAN, wrong
  ports, IPv6). Each rejection must also bump the kill-switch counter. When
  connected: GT DNS and your probe targets are routed through the tunnel and
  reachable. The direct proxy refuses GT, raw IPs, other ports and
  non-allow-listed sites.
- **Host:** no VPN address, route or DNS on your computer, GT-internal
  targets unreachable from it, no GlobalProtect client running on it (on
  Windows, the official PANGP adapter is down), the container is not
  host-networked or privileged, and ports are bound to 127.0.0.1 only.
- The container refuses to start if the leak self-test fails. A watchdog
  re-checks the firewall every 15 s and drops the tunnel if anything
  changed it.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Inside the container, `ssh` to a GT host says "Could not resolve hostname" | The tunnel is off (the prompt shows `(GT:off)`), and the kill-switch blocks everything until it is up. Run `gtvpn connect` on your PC, then retry |
| Tunnel up but GT hosts refuse you | Set `GTVPN_HIP=1` (some portals require a HIP report), then reconnect |
| Portal rejects the client | Check `GTVPN_OS` / `GTVPN_CLIENT_VERSION` (empty: the client's own Linux identity; the official app is `Windows` / `6.2.8-243`) |
| `unsafe legacy renegotiation` | `GTVPN_FIX_OPENSSL=1` |
| "gateway does not exist" or "No gateways found" | That is gpclient misreading GT's Duo challenge. Use the default `GTVPN_CLIENT=auto` (OpenConnect for password sign-in), then `gtvpn up` |
| Connect asks which gateway to use | That is the portal sign-in (`GTVPN_SKIP_PORTAL=0`). Type `DC Gateway`, or set `GTVPN_GATEWAY_NAME` in `.env` |
| Connect asks for your password and a Duo code twice | That is the portal sign-in: the gateway runs its own. Set `GTVPN_SKIP_PORTAL=1` and `GTVPN_GATEWAY=dc-ext-gw.vpn.gatech.edu` in `.env`, then `gtvpn down && gtvpn up`. If you keep it, use a new Duo code the second time |
| "GT refused the sign-in" or "The gateway refused the sign-in" | Usually a Duo code that was already used (each works once) or expired, or a mistyped password. Run `gtvpn connect` again with a new code from Duo Mobile. Don't retry many times in a row: GT can lock your account |
| The connect window closed before you could read the error | `gtvpn logs` shows the last sign-in transcript (passwords are never in it) |
| No windows on Ubuntu | Run `./gtvpn up` from a desktop terminal (it needs `$DISPLAY`); `sudo apt install xauth` |
| No windows on Windows | `ls /mnt/wslg/.X11-unix` in Ubuntu should show `X0`. If not: `wsl --update`, then `wsl --shutdown`. Then `gtvpn down && gtvpn up` and `gtvpn x11test` |
| `ssh gtvpn` says "Could not resolve hostname" in Git Bash | Git Bash's own ssh can't read the Windows config. Use PowerShell or VS Code, which use Windows' OpenSSH |
| `gtvpn code` opens a window as root, or VS Code asks which container | Install the Dev Containers extension (`ms-vscode-remote.remote-containers`), open VS Code once, then run `gtvpn setup` again to install the attach config |
| VS Code hangs installing the server | Add `"remote.SSH.localServerDownload": "always"` |
| A host is missing from VS Code's list | Run `gtvpn hosts`, then reopen the Remote-SSH list. Only plain names on `Host` lines become entries (`Host *.example` is a pattern, not a host) |
| A dev tool can't download something | Add its exact hostname to `GTVPN_DIRECT_ALLOW`, then `./gtvpn down && ./gtvpn up` |
| `git@github.com` (port 22) fails | Use HTTPS remotes; the direct path is HTTPS-only |
| Virtuoso is sluggish | Expected for X11 over a VPN. Compression is on; for heavy layout work use GT's remote desktop through the container |

## Layout

```
gtvpn                         host CLI (Linux / WSL)
compose.yaml, .env.example    container definition and settings
hosts.conf.example            template for hosts.conf, your GT servers (ssh config syntax)
image/                        Dockerfile + in-container scripts
  rootfs/usr/local/sbin/      gtvpn-firewall (kill-switch), gtvpn-connect, gtvpn-egress, ...
  rootfs/usr/local/bin/       gtvpn-verify (container-side checks)
host/verify-host.sh           host-side checks
windows/Setup-GtVpn.ps1       Windows setup
vscode/                       VS Code settings + Dev Containers attach config
tests/lab/                    end-to-end lab with a real tunnel (see below)
docs/DESIGN.md                design, threat model, alternatives
```

## Tests

`tests/lab/` builds a stand-in VPN gateway (ocserv) with "GT-internal"
services, a fake allow-listed API, a fake portal and a fake GT ssh server,
then attacks the setup:

```bash
tests/lab/run-lab.sh   # split + full tunnel: leaks, crash, tampering, SSH jump, proxy (46 checks)
tests/lab/run-x11.sh   # remote X app → ssh over tunnel → container → host display
tests/lab/run-cli.sh   # the real ./gtvpn flow incl. SSO hand-off via the link handler
tests/lab/run-perms.sh # image built from a Windows-drive checkout (all files 0777) is still safe
```

The one piece the lab cannot do is GT's real portal and Duo. The first
`./gtvpn connect` against `vpn.gatech.edu` is the final check, and
`./gtvpn verify` will tell you if anything is off.
