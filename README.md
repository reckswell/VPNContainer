# vpncontainer: your school or work VPN in a sealed container

Connect to a school or work VPN **inside a small container**, not on your
computer. So far that is Georgia Tech's GlobalProtect VPN (`[vpn] provider`
in `vpncontainer.toml` is where others will go). SSH, VS Code and
Cadence/Vivado windows work as usual. Your computer's own traffic never
touches GT, and GT has no way back in.

```
  your computer ──(normal internet, untouched)──► the world
       │
       └─ vpncontainer container ══ GlobalProtect tunnel ══► GT servers (ssh, Cadence, …)
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
sudo apt install docker.io docker-compose-v2 git xauth python3
sudo usermod -aG docker $USER   # then log out and back in
git clone https://github.com/reckswell/VPNContainer ~/VPNContainer && cd ~/VPNContainer
./vpncontainer setup
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
3. Clone or download the repo wherever you keep projects, for example
   `Documents\Projects` (Git for Windows is fine; `.gitattributes` keeps the
   scripts' Linux line endings). Then either double-click
   **`Start-VpnContainer.cmd`** in it, which runs the setup below the first time and
   signs you in (see [One click](#one-click-on-windows)), or run the setup
   script yourself:

   ```powershell
   cd $HOME\Documents\Projects
   git clone https://github.com/reckswell/VPNContainer
   cd VPNContainer
   powershell -ExecutionPolicy Bypass -File .\windows\Setup-VpnContainer.ps1 -Memory 6GB -Processors 4
   ```

   It finds the repo and your Ubuntu distro on its own (`-Distro` and
   `-RepoPath` override that), runs `./vpncontainer setup` in WSL, sets up SSH for
   VS Code on Windows, registers the sign-in link handler, caps WSL's
   resources (`-Memory`/`-Processors` are optional) and adds a `vpncontainer`
   command to PowerShell/cmd.

> On Linux, Docker Engine is preferred over Docker Desktop, because Docker
> Desktop's VM cannot use your X11 socket.

## 2. Configure

Everything shared lives in **[vpncontainer.toml](vpncontainer.toml)**, in the repo, so the
copy on each of your computers matches GitHub. It says:

- **which VPN** the container runs (`[vpn] provider = "globalprotect"`, the
  only kind so far) and how it signs in (portal, gateway, Duo);
- **which SSH connections go through the container**: your servers, one
  `[[hosts]]` block each, plus name and address patterns (`[ssh] private`).
  **Anything not listed connects directly**, as if vpncontainer weren't
  installed: VS Code Remote-SSH to your home desktop, GitHub, and so on;
- the leak-check targets and the direct-egress allow-list.

```toml
[ssh]
private = ["*.gatech.edu", "128.61.*", "130.207.*", "143.215.*"]   # [] = only the hosts below
direct = []                                    # exceptions that connect directly

[[hosts]]
name = "linlab19"
hostname = "ece-linlab19.ece.gatech.edu"

[[hosts]]
name = "pace"
hostname = "login-ice.pace.gatech.edu"
user = "gburdell3"      # optional: also port, x11, options = { ... }
```

Then run `./vpncontainer hosts`. Each host now works as `ssh linlab19` inside the
container, with X11, compression and one Duo prompt per 8 hours already on,
and shows up in VS Code's Remote-SSH list as `gt-linlab19`. Its own name
(`ece-linlab19.ece.gatech.edu`) goes through the container too. Run
`vpncontainer hosts` again after every edit, and `vpncontainer up` after changing `[vpn]`.
A file with mistakes is refused, and the previous settings stay in place.
To see which way a connection goes:

```bash
./vpncontainer route ece-linlab19.ece.gatech.edu my-desktop.local
  ece-linlab19.ece.gatech.edu    through the container  (to ece-linlab19.ece.gatech.edu)
  my-desktop.local               direct, not through the container  (to my-desktop.local)
```

**Personal and per-computer settings** are in `.env` (not tracked by git;
the repo is public):

```bash
VPNCONTAINER_USER=gburdell3             # your VPN sign-in name (optional)
VPNCONTAINER_GT_USER=gburdell3          # your user name on servers that don't set one
VPNCONTAINER_CPUS=2
VPNCONTAINER_MEMORY=2g
```

A setting in `vpncontainer.toml` wins over the same `VPNCONTAINER_*` line in `.env`
(`vpncontainer up` says so if they differ). Hosts you'd rather not publish can go
in `vpncontainer.local.toml`, which git ignores: same format, its settings win and
its `[[hosts]]` are added.

## 3. Daily use

```bash
./vpncontainer up          # start the container (sealed: no network yet)
./vpncontainer connect     # GT username, GT password, then the Duo passcode; tunnel comes up; checks run
./vpncontainer ssh cadence # or: ./vpncontainer shell, then `ssh cadence`
virtuoso &          # (on the GT host) windows open on your desktop
./vpncontainer disconnect  # tunnel down; container stays sealed
./vpncontainer down        # stop everything (your container home is kept)
```

**Sign-in:** `connect` asks, in your terminal, for three things, and each
prompt is labeled with what to type:

1. **GT username** (skipped if `VPNCONTAINER_USER` is set in `.env`).
2. **`[PASSWORD]`** your GT password.
3. **`[DUO CODE]`** the 6-digit passcode shown in Duo Mobile at that moment.
   It is not your password, and nothing is pushed to your phone.

Then the tunnel starts. `connect` signs in at a GT gateway directly
(`skip_portal = true`, `gateway = "dc-ext-gw.vpn.gatech.edu"` in
`vpncontainer.toml`), because
going through the vpn.gatech.edu portal first makes the gateway ask for
your password and a new Duo code a second time. If GT refuses the sign-in,
`connect` stops instead of asking again, because repeated failed logins can
lock a GT account. Each Duo code works once: to retry, wait until Duo Mobile
shows a new one.
Everything, including the sign-in, happens inside the container. (For VPN
portals that use browser SSO instead, `connect` opens the login page in your
browser and a *globalprotectcallback* link hands the result back.)

**Saved sign-in (optional):** put your GT username and password in
`vpncontainer-login.conf` in the repo folder (the first start copies it from
`vpncontainer-login.example.conf`), and `connect` asks only for the Duo code:

```
username=gburdell3
password=your GT password, as is, no quotes
```

That file never leaves your computer: `.gitignore` keeps it out of git, and
the example in the repo stays empty. On Windows the launcher encrypts the
password the first time it runs (`password_dpapi`, readable only by your
Windows account on that PC) and empties the plain line; on Linux the file
is made readable only by you. The password goes to the sign-in over a pipe
into the container's root-only memory (never a command line, a log or the
disk) and is deleted there at once. If GT refuses it, `connect` stops after
that one try and asks you to type your password until you change the file,
so a wrong saved password cannot lock your account. The Duo code is never
stored.

### One click on Windows

Double-click **`Start-VpnContainer.cmd`** in the repo folder. It:

1. starts Docker Desktop if it isn't running, and waits for it,
2. the first time, runs `windows\Setup-VpnContainer.ps1` (above),
3. encrypts a plain-text password in `vpncontainer-login.conf`,
4. runs `vpncontainer connect`: it starts the container if needed, signs in with
   your saved username and password, and asks only for the `[DUO CODE]`.

`Start-VpnContainer.cmd -SavePassword` asks for your password and saves it
encrypted, and `Start-VpnContainer.cmd -Check` shows what is set up and running
without starting anything.

### VS Code

- **Recommended:** `vpncontainer code`, or *Dev Containers: Attach to Running
  Container… → vpncontainer*. VS Code goes into the container through Docker
  itself (no network hop) and logs in as `dev`. Its terminal is inside the
  container, and `ssh cadence` with X11 just works there. Same on Windows
  and Ubuntu. `vpncontainer setup` installs the attach config, and on Windows the
  setup script adds the Dev Containers extension.
- **Open a folder on a GT server directly:** Remote-SSH to `gt-<name>` for
  any `[[hosts]]` entry in `vpncontainer.toml`, or to any GT server by its name or
  address (*Connect to Host… → ece-linlab19.ece.gatech.edu*). Every one of
  them goes through the container: your computer only talks to the
  container's sshd, and the container opens the connection to GT through
  its tunnel, so your computer never connects to GT itself. (VS Code always runs Remote-SSH on
  your computer, even from a container window; this jump is how it reaches
  GT from inside the container.) The `[ssh] private` patterns cover names
  under `*.gatech.edu` and GT's address blocks; a server outside them
  (say, an address that only exists on the VPN) needs a `[[hosts]]` entry.
  Servers that match nothing connect directly, as before. For your own
  `~/.ssh/config` aliases that point at GT, add `ProxyJump vpncontainer` or list
  the server in `vpncontainer.toml`. These
  windows' terminals have no X display of their own; see
  [Cadence](#cadence-virtuoso--vivado-x11) for the one command that gives
  them one.
- Add [vscode/settings-snippet.jsonc](vscode/settings-snippet.jsonc) to
  your user settings. The container cannot reach VS Code's download servers
  (by design), so VS Code downloads its server and extensions on your
  computer and copies them in. The snippet also stops Dev Containers from
  handing your computer's git and Docker credentials to the container, and
  container terminals ignore the ssh-agent it forwards, so your personal
  keys stay on your computer.
- Also possible: *Remote-SSH → `vpncontainer`* (or `vpncontainer code --ssh`), over the
  container's loopback-only, key-only sshd.

### Cadence Virtuoso / Vivado (X11)

Inside the container, `ssh <alias>` already uses trusted X11 forwarding,
compression and connection sharing (one Duo prompt per host for 8 h).
Test it with `./vpncontainer x11test` (shows xeyes from the container). On
Ubuntu, `vpncontainer up` refreshes the Xwayland cookie each time. On Windows,
WSLg needs nothing at all.

Two ways to start Cadence:

- **From a container terminal** (`vpncontainer code` or `vpncontainer shell`): `ssh -Y
  ece-linlab19.ece.gatech.edu` (or its `[[hosts]]` name), then start
  Cadence there.
- **From a VS Code Remote-SSH window on the GT server.** VS Code runs
  Remote-SSH from your computer, which has no X server for GT, so its
  terminals have no `DISPLAY` and Cadence fails with an X display error.
  Open a display for them once on your computer:

  ```
  vpncontainer display ece-linlab19.ece.gatech.edu
  ```

  That keeps an `ssh -Y` session from the container open in the background
  (it reuses the container's connection to that server if there is one,
  otherwise it asks for your GT password and Duo) and writes the display it
  got to `~/.vpncontainer-display` on the server. Then, in the VS Code terminal:

  ```
  export DISPLAY=$(cat ~/.vpncontainer-display)    # bash
  setenv DISPLAY `cat ~/.vpncontainer-display`     # tcsh
  ```

  and start Cadence as usual. The windows come through the container to
  your desktop, like any other X11 from GT. `vpncontainer display <host> status`
  shows it and `vpncontainer display <host> stop` closes it. It also ends when the
  tunnel drops or the container stops; run it again after `vpncontainer connect`.
  To pick it up in every new terminal, add the matching line to `~/.bashrc`
  or `~/.cshrc` on the GT server:

  ```
  [ -z "$DISPLAY" ] && [ -r ~/.vpncontainer-display ] && export DISPLAY=$(cat ~/.vpncontainer-display)   # ~/.bashrc
  if (! $?DISPLAY && -r ~/.vpncontainer-display) setenv DISPLAY "`cat ~/.vpncontainer-display`"          # ~/.cshrc
  ```

### Claude Code for future projects

`./vpncontainer claude` (or `claude` in any container terminal). Your projects can
live in `/home/dev/projects` (persistent volume), or share a host folder
through `./share` → `/home/dev/share`. Claude Code reaches Anthropic through
a filtering proxy that allows only the hostnames in `[direct_egress] allow`
in `vpncontainer.toml` (Anthropic, GitHub, npm and PyPI by default). That traffic
leaves directly and **never** goes through GT, even if GT sends you a full
tunnel. Add hosts to that list as projects need them. Set `enabled = false`
there to make the container GT-only. Update Claude Code with `./vpncontainer rebuild`.

## 4. Verify (as often as you like)

```bash
./vpncontainer verify      # ~45 checks: container side + host side, PASS/FAIL each
./vpncontainer watch 60    # re-run everything every 60 s, desktop alert on failure
./vpncontainer status      # tunnel, routes, DNS, blocked-attempt counter
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

## Coming from gtvpn (the old name)

This project used to be called gtvpn. After you update, use `./vpncontainer`
(`vpncontainer` in PowerShell, `Start-VpnContainer.cmd` for one click). The
rest happens by itself:

- **A running gtvpn container keeps working** with every command, and its
  tunnel stays up. The next `up` or `connect` while no tunnel is up replaces
  it with the vpncontainer container, after copying your home in there and
  its ssh host key into the new volumes. The old volumes are kept; once all
  is well, remove them with `docker volume rm gtvpn_home gtvpn_state`.
- **The first command renames, in place:** the `GTVPN_` lines in `.env`
  (the file as it was stays as `.env.before-rename`), `gtvpn-login.conf`,
  and `~/.ssh/gtvpn` with its `Include` line in `~/.ssh/config`.
- **Windows:** the first `Start-VpnContainer.cmd` runs the setup once more.
  It moves `%USERPROFILE%\.ssh\gtvpn`, installs the `vpncontainer` command
  (`gtvpn` still works and says it was renamed) and points the sign-in link
  handler at the new script. A shortcut to `Start-GtVpn.cmd` needs to point
  to `Start-VpnContainer.cmd` now.
- **GT servers:** `vpncontainer display` writes `~/.vpncontainer-display`
  and also the old `~/.gtvpn-display`, so a line in your shell setup there
  that reads the old name keeps working.
- **VS Code:** the container's ssh host is `vpncontainer` now (it was
  `gtvpn`); your `gt-<name>` hosts keep their names. If your settings have
  `"remote.SSH.remotePlatform": { "gtvpn": "linux" }`, add
  `"vpncontainer": "linux"` next to it.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Inside the container, `ssh` to a GT host says "Could not resolve hostname" | The tunnel is off (the prompt shows `(GT:off)`), and the kill-switch blocks everything until it is up. Run `vpncontainer connect` on your PC, then retry |
| Tunnel up but GT hosts refuse you | Set `hip = true` in `vpncontainer.toml` (some portals require a HIP report), then `vpncontainer up` and reconnect |
| Portal rejects the client | Check `os` / `client_version` in `vpncontainer.toml` (left out: the client's own Linux identity; the official app is `Windows` / `6.2.8-243`) |
| `unsafe legacy renegotiation` | `fix_openssl = true` in `vpncontainer.toml` |
| "gateway does not exist" or "No gateways found" | That is gpclient misreading GT's Duo challenge. Use the default `client = "auto"` (OpenConnect for password sign-in), then `vpncontainer up` |
| `vpncontainer` says a VPN provider "is not supported yet" | Only `provider = "globalprotect"` exists so far (see [Extending later](docs/DESIGN.md#extending-later)) |
| Connect asks which gateway to use | That is the portal sign-in (`skip_portal = false`). Type `DC Gateway`, or set `gateway_name` in `vpncontainer.toml` |
| Connect asks for your password and a Duo code twice | That is the portal sign-in: the gateway runs its own. Set `skip_portal = true` and `gateway = "dc-ext-gw.vpn.gatech.edu"` in `vpncontainer.toml`, then `vpncontainer up`. If you keep it, use a new Duo code the second time |
| "GT refused the sign-in" or "The gateway refused the sign-in" | Usually a Duo code that was already used (each works once) or expired, or a mistyped password. Run `vpncontainer connect` again with a new code from Duo Mobile. Don't retry many times in a row: GT can lock your account |
| "GT refused your saved password" | `username` or `password` in `vpncontainer-login.conf` is wrong (or your GT password changed). Fix it, or run `Start-VpnContainer.cmd -SavePassword`; until the file changes, `connect` asks you to type the password |
| "password_dpapi ... cannot be decrypted" | It was saved by another Windows account or PC. Run `Start-VpnContainer.cmd -SavePassword` again on this one |
| The connect window closed before you could read the error | `vpncontainer logs` shows the last sign-in transcript (passwords are never in it) |
| No windows on Ubuntu | Run `./vpncontainer up` from a desktop terminal (it needs `$DISPLAY`); `sudo apt install xauth` |
| No windows on Windows | `ls /mnt/wslg/.X11-unix` in Ubuntu should show `X0`. If not: `wsl --update`, then `wsl --shutdown`. Then `vpncontainer down && vpncontainer up` and `vpncontainer x11test` |
| Cadence in a VS Code Remote-SSH terminal: "X Window Display Initialization failure", or `echo $DISPLAY` is empty | Those terminals have no X display. Run `vpncontainer display <that server>` on your computer, then `export DISPLAY=$(cat ~/.vpncontainer-display)` in the terminal ([Cadence](#cadence-virtuoso--vivado-x11)) |
| `vpncontainer display` worked before, but now Cadence says "Can't open display" | The display closed with the tunnel or the container. Run `vpncontainer display <server>` again; the number may change, so re-run the `export` too |
| `ssh vpncontainer` says "Could not resolve hostname" in Git Bash | Git Bash's own ssh can't read the Windows config. Use PowerShell or VS Code, which use Windows' OpenSSH |
| `vpncontainer code` opens a window as root, or VS Code asks which container | Install the Dev Containers extension (`ms-vscode-remote.remote-containers`), open VS Code once, then run `vpncontainer setup` again to install the attach config |
| VS Code hangs installing the server | Add `"remote.SSH.localServerDownload": "always"` |
| A host is missing from VS Code's list | Run `vpncontainer hosts`, then reopen the Remote-SSH list. Each `[[hosts]]` entry becomes `gt-<name>`; `[ssh] private` patterns route connections but are not list entries |
| A server goes through the container but shouldn't (or the other way round) | `vpncontainer route <server>` says which way it goes. Add it to `[ssh] direct`, or as a `[[hosts]]` entry, then `vpncontainer hosts` |
| `vpncontainer` says "hosts.conf is no longer read" | Your servers moved to `vpncontainer.toml`: add a `[[hosts]]` block for each name it lists, run `vpncontainer hosts`, then delete `hosts.conf` |
| A dev tool can't download something | Add its exact hostname to `[direct_egress] allow` in `vpncontainer.toml`, then `./vpncontainer up` |
| `git@github.com` (port 22) fails | Use HTTPS remotes; the direct path is HTTPS-only |
| Virtuoso is sluggish | Expected for X11 over a VPN. Compression is on; for heavy layout work use GT's remote desktop through the container |

## Layout

```
vpncontainer                     host CLI (Linux / WSL)
vpncontainer.toml                which VPN, which SSH hosts go through it, checks (shared, in git)
compose.yaml, .env.example       container definition, per-computer settings
host/config.py                   reads and checks vpncontainer.toml
vpncontainer-login.example.conf  template for vpncontainer-login.conf, your saved sign-in (never committed)
Start-VpnContainer.cmd           Windows one-click start: setup, Docker, sign-in
image/                           Dockerfile + in-container scripts
  rootfs/usr/local/sbin/         vpncontainer-firewall (kill-switch), vpncontainer-connect, vpncontainer-egress, ...
  rootfs/usr/local/bin/          vpncontainer-verify (container-side checks)
host/verify-host.sh              host-side checks
windows/Setup-VpnContainer.ps1   Windows setup
windows/Start-VpnContainer.ps1   what Start-VpnContainer.cmd runs
vscode/                          VS Code settings + Dev Containers attach config
tests/config/                    vpncontainer.toml routing checks (no Docker needed)
tests/lab/                       end-to-end lab with a real tunnel (see below)
docs/DESIGN.md                   design, threat model, alternatives
```

## Tests

`tests/config/run-config.sh` checks the routing from `vpncontainer.toml` in a few
seconds, without Docker or a VPN: listed hosts and patterns go through the
container, unlisted ones connect directly, and mistakes are refused.

`tests/lab/` builds a stand-in VPN gateway (ocserv) with "GT-internal"
services, a fake allow-listed API, a fake portal and a fake GT ssh server,
then attacks the setup:

```bash
tests/lab/run-lab.sh    # split + full tunnel: leaks, crash, tampering, SSH jump, proxy (59 checks)
tests/lab/run-x11.sh    # remote X app → ssh over tunnel → container → host display
tests/lab/run-cli.sh    # the real ./vpncontainer flow incl. SSO hand-off via the link handler
tests/lab/run-perms.sh  # image built from a Windows-drive checkout (all files 0777) is still safe
tests/lab/run-rename.sh # updating a gtvpn setup in place, with its tunnel up, to vpncontainer
```

The one piece the lab cannot do is GT's real portal and Duo. The first
`./vpncontainer connect` against `vpn.gatech.edu` is the final check, and
`./vpncontainer verify` will tell you if anything is off.
