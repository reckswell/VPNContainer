# gtvpn vnc, the GT server's side. gtvpn-vnc in the container sends this
# file over its shared connection and runs it there as you, with /bin/sh
# (your login shell may be tcsh):   sh -s -- ACTION [ARG]
#
#   check            "ok Xvnc <version> wm <name>" or what is missing
#   start            start your session, or find the running one; print
#                    "display=:N" and "socket=PATH" (N is also in
#                    /tmp/gtvpn-<uid>/display)
#   display          the same two lines for the running session (exit 1: none)
#   status           the session and the programs showing windows in it
#   stop [--force]   end it (refuses while programs other than the window
#                    manager have windows open)
#
# It installs nothing and writes nothing in your home folder but the
# display's cookie in ~/.Xauthority (as ssh -Y does): it runs the server's own
# TigerVNC X server (Xvnc) and window manager, as you. Programs you start with DISPLAY=:N draw on the GT
# server itself, without a round trip to your computer, and Xvnc sends
# compressed pictures of what changed to the viewer on your computer. It keeps
# running when the viewer, the tunnel or VS Code disconnect, and so does
# Virtuoso.
#
# Nothing listens on the network. Xvnc listens only on a Unix socket in a
# folder only you can open (/tmp/gtvpn-<uid>, mode 700, socket mode 600),
# which the container reaches through its shared ssh connection; the X
# display itself needs the cookie in your ~/.Xauthority, like ssh -Y's.
set -u
U=$(id -u)
D=/tmp/gtvpn-$U
SOCK=$D/vnc.sock

say() { printf '%s\n' "$*"; }
die() { printf 'gtvpn vnc on %s: %s\n' "$(hostname -s 2>/dev/null)" "$*" >&2; exit 1; }

# Window managers this server may have, best first. They draw the title
# bars and let you move and resize Virtuoso's windows inside the viewer.
WMS="metacity mwm mutter"
wm() {
  for w in $WMS; do command -v "$w" >/dev/null 2>&1 && { say "$w"; return 0; }; done
  return 1
}
start_wm() { # display
  for w in $WMS; do
    command -v "$w" >/dev/null 2>&1 || continue
    case $w in mwm) a= ;; *) a=--replace ;; esac
    # Its settings in memory and its caches in our folder: nothing in your
    # home folder (metacity would otherwise create ~/.cache/dconf).
    DISPLAY=":$1" GSETTINGS_BACKEND=memory XDG_CACHE_HOME="$D/cache" XDG_CONFIG_HOME="$D/config" \
      XDG_DATA_HOME="$D/data" setsid nohup "$w" $a >"$D/wm.log" 2>&1 </dev/null &
    p=$!; sleep 1
    kill -0 "$p" 2>/dev/null && return 0
  done
  return 1
}
# Display number of the running session (its Xvnc serves our socket).
live() {
  [ -S "$SOCK" ] && [ -s "$D/display" ] || return 1
  n=$(cat "$D/display") || return 1
  pgrep -u "$U" -f "^Xvnc :$n .*-rfbunixpath $SOCK" >/dev/null 2>&1 || return 1
  say "$n"
}
report() { say "display=:$1"; say "socket=$SOCK"; }

case ${1:-} in
check)
  command -v Xvnc >/dev/null 2>&1 || { say "missing Xvnc (TigerVNC server)"; exit 1; }
  v=$(Xvnc -version 2>&1 | sed -n 's/.*TigerVNC \(server \)*\([0-9][0-9.]*\).*/\2/p' | head -1)
  w=$(wm) || w=none
  say "ok Xvnc ${v:-?} wm $w" ;;

start)
  command -v Xvnc >/dev/null 2>&1 || die "this server has no Xvnc (TigerVNC server)"
  if n=$(live); then
    say "reusing your session :$n"
  else
    umask 077
    # Our folder must be ours and private (it is in the shared /tmp).
    mkdir -p "$D" 2>/dev/null; chmod 700 "$D" 2>/dev/null
    [ -d "$D" ] && [ ! -L "$D" ] && [ "$(stat -c %u "$D")" = "$U" ] && [ "$(stat -c %a "$D")" = 700 ] \
      || die "$D is not a private folder of yours; remove it and try again"
    rm -f "$SOCK"
    # A free display, starting at a per-user number so classmates on the
    # same server rarely collide.
    n=$(( 100 + U % 200 )); tries=0
    while [ -e "/tmp/.X$n-lock" ] || [ -e "/tmp/.X11-unix/X$n" ]; do
      n=$((n + 1)); tries=$((tries + 1)); [ $tries -lt 300 ] || die "no free display number"
    done
    command -v mcookie >/dev/null 2>&1 && cookie=$(mcookie) || cookie=$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')
    XA=${XAUTHORITY:-$HOME/.Xauthority}
    xauth -q -f "$XA" add ":$n" . "$cookie" 2>/dev/null || die "could not add the display's cookie to $XA"
    # -rfbport -1: no TCP port at all (the default would open 5900+N on this
    # server, which other users could reach). The desktop starts at 1920x1080
    # and follows the viewer's window size (-AcceptSetDesktopSize).
    XDG_CACHE_HOME="$D/cache" XDG_CONFIG_HOME="$D/config" XDG_DATA_HOME="$D/data" \
      setsid nohup Xvnc ":$n" -auth "$XA" -nolisten tcp \
      -rfbunixpath "$SOCK" -rfbunixmode 0600 -rfbport -1 -localhost -interface 127.0.0.1 -SecurityTypes None \
      -geometry 1920x1080 -depth 24 -dpi 96 -FrameRate 180 -AlwaysShared -AcceptSetDesktopSize=1 \
      >"$D/xvnc.log" 2>&1 </dev/null &
    say "$n" > "$D/display"
    i=0; until [ -S "$SOCK" ] && DISPLAY=":$n" xdpyinfo >/dev/null 2>&1; do
      i=$((i + 1)); [ $i -lt 40 ] || { tail -n 15 "$D/xvnc.log" >&2; die "Xvnc did not start (log: $D/xvnc.log)"; }
      sleep 0.25
    done
    start_wm "$n" || say "note: no window manager started (windows have no title bars; log: $D/wm.log)"
    say "started your session :$n"
  fi
  report "$n" ;;

display)
  n=$(live) || exit 1
  report "$n" ;;

status)
  if n=$(live); then
    say "session :$n is running"
    DISPLAY=":$n" xlsclients 2>/dev/null | awk '{ $1=""; sub(/^ /, ""); print "  program: " $0 }' | head -30
  else say "no session running"; exit 1; fi ;;

stop)
  n=$(live) || { say "no session running"; exit 0; }
  p=$(DISPLAY=":$n" xlsclients 2>/dev/null | awk '{ $1=""; sub(/^ /, ""); print }' | grep -v -E '^([^ ]*/)?(metacity|mwm|mutter)( |$)')
  if [ -n "$p" ] && [ "${2:-}" != --force ]; then
    say "These programs still have windows in the session, and stopping it closes them without saving:"
    printf '%s\n' "$p" | sed 's/^/  /'
    say "Quit Virtuoso first (File > Exit in the CIW), or use: gtvpn vnc <host> stop --force"
    exit 3
  fi
  pkill -u "$U" -f "^Xvnc :$n .*-rfbunixpath $SOCK"
  i=0; while pgrep -u "$U" -f "^Xvnc :$n .*-rfbunixpath $SOCK" >/dev/null 2>&1 && [ $i -lt 20 ]; do sleep 0.25; i=$((i + 1)); done
  xauth -q -f "${XAUTHORITY:-$HOME/.Xauthority}" remove ":$n" 2>/dev/null
  rm -rf "$D"
  say "stopped session :$n" ;;

*) die "unknown action '${1:-}'" ;;
esac
