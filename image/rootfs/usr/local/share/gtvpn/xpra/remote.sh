# gtvpn xpra, the GT server's side. gtvpn-xpra in the container sends this
# file over its shared connection and runs it there as you, with /bin/sh
# (your login shell may be tcsh):   sh -s -- ACTION [ARG]
#
#   check            "installed <version>" or "missing"
#   install          xpra into ~/.local/gtvpn-xpra (no root): micromamba plus
#                    the packages in xpra.lock (sent beforehand), from
#                    conda-forge, over this server's own internet connection
#   start            start your session, or find the running one; print
#                    "display=:N" and write ":N" to ~/.gtvpn-display
#   display          print "display=:N" of the running session (exit 1: none)
#   status           the session and the windows in it
#   stop [--force]   end the session (refuses while windows are open)
#
# The session is an X server (Xvfb) that only xpra watches: programs you
# start on it with DISPLAY=:N draw locally on this server, without a round
# trip to your computer, and xpra sends compressed pictures of their
# windows to the viewer on your computer. It keeps running when the viewer,
# the tunnel or VS Code disconnect, so Virtuoso does too.
set -u
P=$HOME/.local/gtvpn-xpra
X=$P/env/bin/xpra
MM_URL=https://conda.anaconda.org/conda-forge/linux-64/micromamba-2.9.0-0.tar.bz2
MM_SHA256=8761c382127e6363bd9e0a2451aa3ef90d071a79133f736e2f759a3bf13040dd
DF=$HOME/.gtvpn-display
# Sockets in ~/.xpra (xpra names them after this host), not only in
# /run/user/<uid>, which is deleted when your last login on the server ends:
# the session must stay reachable after the tunnel drops. gtvpn-xpra-proxy
# looks there too.
SD=$HOME/.xpra

say() { printf '%s\n' "$*"; }
die() { printf 'gtvpn xpra on %s: %s\n' "$(hostname -s 2>/dev/null)" "$*" >&2; exit 1; }

# A start that failed can leave its X server behind (xpra keeps it so a
# crashed session can be rescued); remove it, then report.
failed() {
  pkill -u "$(id -u)" -f "^Xvfb-for-Xpra-$n " 2>/dev/null
  tail -n 15 "$P/log/start.log" "$P/log/server.log" 2>/dev/null >&2
  die "$1 (logs: $P/log)"
}

# Display number of this host's live session, if any.
live() {
  [ -x "$X" ] || return 1
  "$X" list --socket-dirs="$SD" 2>/dev/null | sed -n 's/.*LIVE session at :\([0-9][0-9]*\).*/\1/p' | head -1 | grep .
}

case ${1:-} in
check)
  if [ -x "$X" ] && v=$("$X" --version 2>/dev/null); then say "installed ${v#xpra }"; else say missing; fi ;;

install)
  [ "$(uname -m)" = x86_64 ] || die "only x86_64 servers are supported (this one is $(uname -m))"
  command -v curl >/dev/null 2>&1 || die "curl is missing"
  command -v bzip2 >/dev/null 2>&1 || die "bzip2 is missing"
  command -v Xvfb >/dev/null 2>&1 || die "Xvfb is missing (ask your admins for xorg-x11-server-Xvfb)"
  [ -s "$P/xpra.lock" ] || die "no package list ($P/xpra.lock)"
  avail=$(df -Pk "$HOME" 2>/dev/null | awk 'NR==2{print $4}')
  [ -z "$avail" ] || [ "$avail" -gt 1500000 ] || die "needs about 1.5 GB free in your home (has $((avail / 1024)) MB)"
  umask 077; mkdir -p "$P/mm" || exit 1
  say "Downloading micromamba (the package installer, 7 MB)"
  curl -fsSL --retry 3 -o "$P/mm.tar.bz2" "$MM_URL" || die "download failed: $MM_URL"
  echo "$MM_SHA256  $P/mm.tar.bz2" | sha256sum -c - >/dev/null 2>&1 || { rm -f "$P/mm.tar.bz2"; die "micromamba checksum mismatch"; }
  tar -xjf "$P/mm.tar.bz2" -C "$P/mm" bin/micromamba && rm -f "$P/mm.tar.bz2" || die "could not unpack micromamba"
  live >/dev/null && die "a session is running; stop it first (gtvpn xpra <host> stop)"
  rm -rf "$P/env"
  say "Installing xpra 6.2 and its libraries (about 300 MB download, 850 MB on disk; 1 to 3 minutes)"
  MAMBA_ROOT_PREFIX=$P/mamba "$P/mm/bin/micromamba" create -y -q -p "$P/env" --file "$P/xpra.lock" \
    || die "the package installation failed (see above)"
  MAMBA_ROOT_PREFIX=$P/mamba "$P/mm/bin/micromamba" clean -a -y -q >/dev/null 2>&1; rm -rf "$P/mamba/pkgs"
  v=$("$X" --version 2>/dev/null) || die "xpra was installed but does not start"
  say "installed ${v#xpra }" ;;

start)
  [ -x "$X" ] || die "xpra is not installed here yet"
  if n=$(live); then
    say "reusing your session :$n"
  else
    command -v Xvfb >/dev/null 2>&1 || die "Xvfb is missing on this server"
    # A free display, starting at a per-user number so classmates on the
    # same server rarely collide.
    n=$(( 100 + $(id -u) % 200 )); tries=0
    while [ -e "/tmp/.X$n-lock" ] || [ -e "/tmp/.X11-unix/X$n" ]; do
      n=$((n + 1)); tries=$((tries + 1)); [ $tries -lt 300 ] || die "no free display number"
    done
    mkdir -p "$P/log"
    # Xvfb from the system (the env's default, Xdummy, needs Xorg's dummy
    # driver). The screen is sized for a 5K monitor; xpra resizes it to the
    # viewer's monitor. No audio, printing or notifications: Cadence needs
    # none of them, and they would only add warnings.
    mkdir -p "$SD" && chmod 700 "$SD"
    "$X" seamless ":$n" --daemon=yes --systemd-run=no --log-dir="$P/log" \
      --socket-dir="$SD" --socket-dirs="$SD" \
      --xvfb="Xvfb +extension GLX +extension Composite +extension RANDR +extension RENDER -screen 0 5120x2880x24+32 -nolisten tcp -noreset -auth \$XAUTHORITY -dpi 96" \
      --dpi=96 --pixel-depth=24 --resize-display=yes --exit-with-children=no \
      --mdns=no --pulseaudio=no --audio=no --webcam=no --printing=no --notifications=no \
      --dbus=no --html=off --http=off --start-new-commands=no --file-transfer=no --open-files=no --open-url=no \
      >"$P/log/start.log" 2>&1 || failed "xpra did not start"
    i=0; until live >/dev/null; do
      i=$((i + 1)); [ $i -lt 40 ] || failed "the session did not come up"
      sleep 0.5
    done
    n=$(live)
    say "started your session :$n"
  fi
  umask 077; printf ':%s\n' "$n" > "$DF"
  say "display=:$n" ;;

display)
  n=$(live) || exit 1
  say "display=:$n" ;;

status)
  if n=$(live); then
    say "session :$n is running"
    "$X" info --socket-dirs="$SD" ":$n" 2>/dev/null | sed -n 's/^windows\.[0-9]*\.title=\(.*\)/  window: \1/p' | head -30
    c=$("$X" info --socket-dirs="$SD" ":$n" 2>/dev/null | sed -n 's/^clients=\([0-9]*\)$/\1/p' | head -1)
    say "  viewers connected: ${c:-0}"
  elif [ -x "$X" ]; then say "no session running"; exit 1
  else say "xpra is not installed here"; exit 1; fi ;;

stop)
  n=$(live) || { say "no session running"; exit 0; }
  w=$("$X" info --socket-dirs="$SD" ":$n" 2>/dev/null | sed -n 's/^windows\.[0-9]*\.title=\(.*\)/  \1/p')
  if [ -n "$w" ] && [ "${2:-}" != --force ]; then
    say "These windows are still open in the session, and stopping it closes them without saving:"
    say "$w"
    say "Quit Virtuoso first (File > Exit in the CIW), or use: gtvpn xpra <host> stop --force"
    exit 3
  fi
  "$X" stop --socket-dirs="$SD" ":$n" >/dev/null 2>&1
  [ "$(cat "$DF" 2>/dev/null)" = ":$n" ] && rm -f "$DF"
  say "stopped session :$n" ;;

*) die "unknown action '${1:-}'" ;;
esac
