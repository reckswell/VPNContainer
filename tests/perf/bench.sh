#!/usr/bin/env bash
# Run the remote-display benchmarks in the lab started by lab.sh.
# Each transport shows the same two Virtuoso-like canvases on the client's
# display; x11lat measures there (key press -> pixels on your screen).
set -uo pipefail
XENV=${XENV:?path to the xpra conda env}
OUT=${OUT:-}
TRIALS=${TRIALS:-15}; FPS_SECS=${FPS_SECS:-5}; ONLY=${ONLY:-}
C() { docker exec perf-client su dev -c "export DISPLAY=:1; $1"; }
G() { docker exec perf-gthost su gtuser -c "$1"; }
NOMUX="-o ControlMaster=no -o ControlPath=none"
cleanup_apps() {
  # [x]yz patterns: pkill -f must not match the shell running it, or it
  # kills that shell and skips the rest of the line.
  G "pkill -f '[q]tcanvas.py'; pkill -x xcanvas; pkill -x Xvnc" >/dev/null 2>&1
  G "$XENV/bin/xpra stop :100" >/dev/null 2>&1
  C "pkill -f '[q]tcanvas.py'; pkill -x xcanvas; pkill -f '[x]pra attach'; pkill -x vncviewer; pkill -f 'ssh .*-[Y]'; pkill -f 'ssh .*-L [5]905'" >/dev/null 2>&1
  sleep 1
}
measure() { # label title  (window already showing)
  local label=$1 t=$2 lat fps form=""
  lat=$(C "x11lat latency '$t' Right $TRIALS" 2>&1 | tail -1)
  fps=$(C "x11lat fps '$t' Right 180 $FPS_SECS" 2>&1 | tail -1)
  [[ $t == perf-canvas* || $label == *vnc*qt* ]] && form=$(C "x11lat open '$t' d perf-form 5" 2>&1 | tail -1)
  printf '%s | %s | %s | %s\n' "$label" "$lat" "$fps" "$form"
  [[ -n $OUT ]] && printf '%s | %s | %s | %s\n' "$label" "$lat" "$fps" "$form" >> "$OUT"
}
want() { [[ -z $ONLY || $1 == *$ONLY* ]]; }

for app in qt x; do
  if [[ $app == qt ]]; then cmd="env QT_X11_NO_MITSHM=1 qtcanvas.py"; title=perf-canvas; else cmd=xcanvas; title=perf-xcanvas; fi
  cleanup_apps
  if want "local-$app"; then
    C "nohup $cmd >/dev/null 2>&1 &"; sleep 3; measure "local-$app" "$title"; cleanup_apps
  fi
  for v in "comp:-o Compression=yes" "nocomp:-o Compression=no" "nocomp-aes:-o Compression=no -c aes128-gcm@openssh.com"; do
    want "ssh-${v%%:*}-$app" || continue
    C "nohup ssh $NOMUX -Y ${v#*:} gthost '$cmd' >/dev/null 2>&1 &"; sleep 6
    measure "ssh-${v%%:*}-$app" "$title"; cleanup_apps
  done
  for v in "auto:" "tuned:--min-speed=70 --min-quality=60 --refresh-rate=180" "h264:--encoding=h264 --min-speed=80 --refresh-rate=180" "webp:--encodings=webp,png,rgb --min-quality=80 --refresh-rate=180"; do
    want "xpra-${v%%:*}-$app" || continue
    G "$XENV/bin/xpra seamless :100 --daemon=yes --systemd-run=no --mdns=no --pulseaudio=no --audio=no --notifications=no --webcam=no --printing=no --file-transfer=no --dbus=no --start-child='$cmd' --exit-with-children=no --resize-display=2560x1440 >/tmp/xpra-start.log 2>&1"
    sleep 4
    C "nohup $XENV/bin/xpra attach ssh://gthost/100 --ssh='ssh $NOMUX' --remote-xpra=$XENV/bin/xpra --splash=no --tray=no --notifications=no --audio=no --webcam=no ${v#*:} >/tmp/xpra-attach.log 2>&1 &"
    sleep 8
    measure "xpra-${v%%:*}-$app" "$title"; cleanup_apps
  done
  if want "vnc-$app"; then
    G "nohup Xvnc :5 -geometry 1600x1000 -depth 24 -SecurityTypes None -localhost yes -rfbport 5905 -AlwaysShared >/tmp/xvnc.log 2>&1 & sleep 2; DISPLAY=:5 nohup sh -c '$cmd' >/dev/null 2>&1 &"
    sleep 3
    C "nohup ssh $NOMUX -o Compression=no -N -L 5905:localhost:5905 gthost >/dev/null 2>&1 & sleep 2; nohup vncviewer -SecurityTypes None localhost:5905 >/tmp/vnc.log 2>&1 &"
    sleep 5
    measure "vnc-tiger-$app" "TigerVNC"; cleanup_apps
  fi
done
