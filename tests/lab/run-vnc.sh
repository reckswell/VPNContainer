#!/usr/bin/env bash
# VNC lab: `gtvpn vnc` end to end, with a real tunnel.
#   GT server (ocserv stand-in): its own TigerVNC Xvnc and metacity, as on
#     ece-linlab19 (nothing is installed by the test or by gtvpn-vnc)
#   gtvpn container: gtvpn-vnc start/display/status/stop over the shared
#     connection, and the forward of 127.0.0.1:P to the session's socket
#   this computer: the TigerVNC viewer on an Xvfb "desktop", through ssh gtvpn,
#     then the real host CLI (./gtvpn vnc, its Linux path) with a stand-in viewer
# Needs Xvfb, xauth, an ssh client and script(1) on the host.
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1
BASE=${LAB_BASE_IMAGE:-ubuntu:24.04}; BNET=${LAB_BUILD_NETWORK:-default}
FILES=(-f compose.yaml); [[ ${LAB_NO_IPV6:-0} == 1 ]] && FILES+=(-f tests/lab/compose.nov6.yaml)
FILES+=(-f tests/lab/compose.lab.yaml)
DC() { docker compose -p gtvpn-lab "${FILES[@]}" "$@"; }
FAILED=0; ok(){ printf '\033[32mPASS\033[0m %s\n' "$*"; }; bad(){ printf '\033[31mFAIL\033[0m %s\n' "$*"; FAILED=1; }
check(){ local d=$1; shift; if "$@"; then ok "$d"; else bad "$d"; fi; }
D=:79; XA=$(mktemp); KEYDIR=$(mktemp -d); XPID=
cleanup(){ [[ ${KEEP:-0} == 1 ]] && return; pkill -f -- '-L 127.0.0.1:1[56][0-9][0-9][0-9]:127.0.0.1:.* [g]tvpn$' 2>/dev/null; docker rm -f gtvpn-lab-viewer gtvpn-lab-fwd >/dev/null 2>&1; DC down -v --remove-orphans >/dev/null 2>&1
  [[ -n $XPID ]] && kill "$XPID" 2>/dev/null; rm -rf "$XA" "$KEYDIR" runtime/in/xauth runtime/in/hosts.conf; }
trap cleanup EXIT

docker build -q --network "$BNET" --build-arg BASE_IMAGE="$BASE" -t gtvpn:local image >/dev/null || exit 1
docker build -q --network "$BNET" --build-arg BASE_IMAGE="$BASE" -t lab/ocserv tests/lab/ocserv >/dev/null || exit 1
docker build -q --network "$BNET" -t lab/gtvpn-client -f tests/lab/Dockerfile.client tests/lab >/dev/null || exit 1

# The "desktop" of this computer, and the ssh key/config for `ssh gtvpn`.
# Its X and ssh clients come from the lab image, so this computer needs only
# Xvfb and xauth (host network: the cookie is stored under this hostname).
xauth -f "$XA" add "$D" . "$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
desk(){ docker run --rm --network host -v /tmp/.X11-unix:/tmp/.X11-unix -v "$XA:/xauth:ro" -e XAUTHORITY=/xauth -e DISPLAY="$D" --entrypoint "$1" lab/ocserv "${@:2}"; }
pkill -f "Xvfb $D" 2>/dev/null; Xvfb "$D" -screen 0 1920x1200x24 -auth "$XA" -nolisten tcp >/dev/null 2>&1 & XPID=$!
docker run --rm -v "$KEYDIR:/k" --entrypoint ssh-keygen gtvpn:local -q -t ed25519 -N '' -f /k/id >/dev/null
mkdir -p runtime/in runtime/out; cp "$KEYDIR/id.pub" runtime/in/authorized_keys
rm -f runtime/in/xauth   # (the container needs no desktop for this test)

DC down -v --remove-orphans >/dev/null 2>&1
LAB_MODE=split DC up -d ocserv fakeapi >/dev/null 2>&1
for _ in $(seq 20); do docker logs gtvpn-lab-ocserv 2>&1 | grep -q '^LABPIN' && break; sleep 0.5; done
export LAB_PIN; LAB_PIN=$(docker logs gtvpn-lab-ocserv 2>&1 | awk '/^LABPIN/{print $2}')
GTVPN_SSH_HOSTS="cadence=10.66.0.1" GTVPN_GT_USER=gtuser DC up -d gtvpn >/dev/null 2>&1
for _ in $(seq 60); do docker logs gtvpn 2>&1 | grep -q 'ready\.' && break; sleep 1; done
docker exec gtvpn lab-connect >/dev/null || { bad "tunnel"; exit 1; }
PORT=$(docker port gtvpn 2222/tcp | head -1 | sed 's/.*://')
printf 'Host gtvpn\n  HostName 127.0.0.1\n  Port %s\n  User dev\n  IdentityFile /k/id\n  IdentitiesOnly yes\n  StrictHostKeyChecking no\n  UserKnownHostsFile /dev/null\n  LogLevel error\n' "$PORT" > "$KEYDIR/config"

dev(){ docker exec -u dev gtvpn bash -lc "$1"; }
srv(){ docker exec -u "${2:-gtuser}" -e HOME="/home/${2:-gtuser}" gtvpn-lab-ocserv sh -c "$1"; }
on_srv(){ docker exec gtvpn-lab-ocserv sh -c "$1"; }
dev 'test -s ~/.ssh/id_ed25519 || ssh-keygen -q -t ed25519 -N "" -f ~/.ssh/id_ed25519'
for u in gtuser gtcsh; do
  docker exec gtvpn cat /home/dev/.ssh/id_ed25519.pub | docker exec -i gtvpn-lab-ocserv sh -c "cat > /etc/ssh/lab_keys/$u; chmod 644 /etc/ssh/lab_keys/$u"
done
dev 'ssh -o StrictHostKeyChecking=accept-new -o BatchMode=yes cadence true' || { bad "ssh to the GT server"; exit 1; }
export -f dev srv on_srv desk
GTUID=$(srv 'id -u'); SD=/tmp/gtvpn-$GTUID
export GTUID SD

check "the server's own Xvnc and window manager are found" bash -c "[[ \$(dev 'ssh -o BatchMode=yes cadence \"exec /bin/sh -s -- check\" < /usr/local/share/gtvpn/vnc/remote.sh') == 'ok Xvnc '*' wm metacity' ]]"
HOME0=$(srv 'cd ~ && find . -mindepth 1 ! -name .Xauthority | sort')
OUT=$(dev 'gtvpn-vnc cadence start' 2>&1)
N=$(sed -n 's/^display=:\([0-9]*\)$/\1/p' <<<"$OUT"); P=$(sed -n 's/^port=\([0-9]*\)$/\1/p' <<<"$OUT")
check "gtvpn-vnc starts a session over the existing connection (display :$N, port $P)" \
  bash -c "[[ -n '$N' && '$P' == \$((15900 + ${N:-0})) ]]"
[[ -n $N && -n $P ]] || { echo "$OUT"; exit 1; }
export N P
check "the session's folder names it, and nothing new is in the home folder but the cookie" \
  bash -c "[[ \$(srv 'cat $SD/display') == $N && \$(srv 'cd ~ && find . -mindepth 1 ! -name .Xauthority | sort') == \"\$1\" ]] && srv 'test -s ~/.Xauthority'" _ "$HOME0"
check "starting again reuses the same session" bash -c "dev 'gtvpn-vnc cadence start' | grep -qx 'reusing your session :$N'"
check "another session on the server (like VS Code's terminal) draws on it" srv "DISPLAY=:$N xdpyinfo >/dev/null"
check "the window manager runs in it (metacity, as on ece-linlab19)" bash -c "on_srv \"pgrep -u gtuser -x metacity\" >/dev/null"
check "Xvnc listens on no TCP port of the server" \
  bash -c "! on_srv 'ss -Hltnp' | grep -q -i -E 'vnc|:(59|60)[0-9][0-9] '"
check "its socket is in a folder only you can open (0700, socket 0600)" \
  bash -c "[[ \$(on_srv 'stat -c \"%U %a\" $SD $SD/vnc.sock') == \$'gtuser 700\ngtuser 600' ]]"
check "another user on the server cannot reach the session" \
  bash -c "! srv 'nc -U -w 2 $SD/vnc.sock </dev/null | head -c 3 | grep -q RFB' gtcsh"
check "the X display itself needs the cookie" \
  bash -c "! srv 'XAUTHORITY=/dev/null xdpyinfo -display :$N >/dev/null 2>&1'"
check "the container's 127.0.0.1:$P is forwarded to it (VNC greeting)" \
  bash -c "dev 'exec 3<>/dev/tcp/127.0.0.1/$P; head -c 12 <&3' | grep -q '^RFB 003\.00'"
check "port checks (connections that end at the greeting) do not shut the viewer out" \
  bash -c "for i in 1 2 3 4 5 6 7 8; do dev 'exec 3<>/dev/tcp/127.0.0.1/$P; head -c 12 <&3' >/dev/null; done; dev 'exec 3<>/dev/tcp/127.0.0.1/$P; head -c 12 <&3' | grep -q '^RFB 003\.008'"
check "the forward lives in the shared connection (no ssh left running for it)" \
  bash -c "! docker exec gtvpn pgrep -u dev -f -- '-L 127\.0\.0\.1:$P:' >/dev/null"

# This computer: 127.0.0.1:P -> the container's 127.0.0.1:P through ssh
# gtvpn (as `gtvpn vnc` does), and the TigerVNC viewer on the desktop.
docker run -d --name gtvpn-lab-fwd --network host -v "$KEYDIR:/k:ro" --entrypoint ssh lab/ocserv \
  -F /k/config -N -o ExitOnForwardFailure=yes -L "127.0.0.1:$P:127.0.0.1:$P" gtvpn >/dev/null
docker run -d --name gtvpn-lab-viewer --network host -v /tmp/.X11-unix:/tmp/.X11-unix -v "$XA:/xauth:ro" \
  -e DISPLAY="$D" -e XAUTHORITY=/xauth --entrypoint xtigervncviewer lab/ocserv \
  -SecurityTypes=None -AutoSelect=1 -RemoteResize=1 -geometry 1400x900+10+10 "127.0.0.1::$P" >/dev/null
srv "DISPLAY=:$N nohup xlogo -geometry 320x240 -title gtvpn-lab-xlogo >/dev/null 2>&1 &"
viewer(){ for _ in $(seq 30); do desk xwininfo -root -tree 2>/dev/null | grep -q 'TigerVNC' && return 0; sleep 0.5; done; docker logs gtvpn-lab-viewer 2>&1 | tail -5; return 1; }
check "the viewer on this computer shows the session through ssh gtvpn" viewer
resized(){ for _ in $(seq 20); do srv "xdpyinfo -display :$N | grep -q 'dimensions: *1400x900 '" && return 0; sleep 0.5; done; srv "xdpyinfo -display :$N | grep dimensions"; return 1; }
check "the session's desktop follows the viewer window's size" resized
check "status lists the programs in the session" bash -c "dev 'gtvpn-vnc cadence status' | grep -q 'program: xlogo'"
docker rm -f gtvpn-lab-viewer gtvpn-lab-fwd >/dev/null
dev 'ssh -O exit cadence' >/dev/null 2>&1; sleep 1
check "without an open connection, display reports nothing (and does not sign in)" bash -c "! dev 'gtvpn-vnc cadence display </dev/null' >/dev/null 2>&1"
check "the session (and its window) survived the viewer and the connection closing" \
  bash -c "dev 'ssh -o BatchMode=yes cadence true' && dev 'gtvpn-vnc cadence display' | grep -qx 'display=:$N' && srv 'DISPLAY=:$N xlsclients | grep -q xlogo'"
check "after reconnecting, the port is forwarded again" \
  bash -c "dev 'exec 3<>/dev/tcp/127.0.0.1/$P; head -c 12 <&3' | grep -q '^RFB 003\.00'"
check "stop refuses while a program has windows open" \
  bash -c "dev 'gtvpn-vnc cadence stop' >/dev/null; [[ \$? == 3 ]] && srv 'xdpyinfo -display :$N >/dev/null 2>&1'"
check "stop --force ends it and removes its folder and its cookie" \
  bash -c "dev 'gtvpn-vnc cadence stop --force' >/dev/null && srv '! test -e $SD && ! xauth list | grep -q \":$N \"'"
gone(){ for _ in $(seq 10); do ! on_srv "pgrep -u gtuser -f \"^Xvnc \" || pgrep -u gtuser -x metacity || pgrep -u gtuser -x xlogo" >/dev/null && return 0; sleep 0.5; done; on_srv 'pgrep -u gtuser -af "Xvnc|metacity|xlogo"'; return 1; }
check "nothing of the session is left running on the server" gone
check "after a whole session, the home folder holds nothing new (but ~/.Xauthority)" \
  bash -c "[[ \$(srv 'cd ~ && find . -mindepth 1 ! -name .Xauthority | sort') == \"\$1\" ]]" _ "$HOME0"
check "stop also closed the container's forward" bash -c "! dev '(exec 3<>/dev/tcp/127.0.0.1/$P)' 2>/dev/null"
check "stop leaves the shared connection open (no new Duo prompt)" dev "ssh -O check cadence 2>&1 | grep -qi running"
# A tcsh login shell (most GT accounts), at the same time as another user's
# session: its own display number, socket and port.
dev 'gtvpn-vnc cadence start' >/dev/null 2>&1; dev 'ssh -o BatchMode=yes gtcsh@10.66.0.1 true'; N1=:$(srv "cat $SD/display")
OUT2=$(dev 'gtvpn-vnc gtcsh@10.66.0.1 start' 2>&1); N2=$(sed -n 's/^display=\(:[0-9]*\)$/\1/p' <<<"$OUT2")
check "with a tcsh login shell, beside another user's session, it gets its own display ($N1, $N2)" \
  bash -c "[[ -n '$N2' && '$N2' != '$N1' ]] && srv 'DISPLAY=$N2 xdpyinfo >/dev/null' gtcsh"
[[ -n $N2 ]] || echo "$OUT2"
dev 'gtvpn-vnc gtcsh@10.66.0.1 stop' >/dev/null 2>&1; dev 'gtvpn-vnc cadence stop' >/dev/null 2>&1
# gtvpn's own viewer (windows/viewer; run here with mono): it takes over from
# metacity as the session's window manager and shows every window of the
# session as a window of its own on this computer's desktop, at the same
# place, with input going back through VNC.
if command -v mono >/dev/null && command -v mcs >/dev/null; then
  OUT=$(dev 'gtvpn-vnc cadence windows' 2>&1)
  NW=$(sed -n 's/^display=:\([0-9]*\)$/\1/p' <<<"$OUT"); PW=$((15900 + ${NW:-0})); QW=$((16300 + ${NW:-0}))
  check "windows: a session without metacity, and its X display forwarded too (port $QW)" \
    bash -c "[[ -n '$NW' ]] && grep -qx 'xport=$QW' <<<\"\$1\" && ! on_srv 'pgrep -u gtuser -x metacity' >/dev/null" _ "$OUT"
  [[ -n $NW ]] || echo "$OUT"
  CK=$(dev 'gtvpn-vnc cadence cookie' | sed -n 's/^cookie=//p')
  check "cookie gives the session's X cookie" bash -c "[[ '$CK' =~ ^[0-9a-f]{32}$ ]] && srv 'xauth list :$NW' | grep -q '$CK'"
  docker run -d --name gtvpn-lab-fwd --network host -v "$KEYDIR:/k:ro" --entrypoint ssh lab/ocserv \
    -F /k/config -N -o ExitOnForwardFailure=yes -L "127.0.0.1:$PW:127.0.0.1:$PW" -L "127.0.0.1:$QW:127.0.0.1:$QW" gtvpn >/dev/null
  mcs -langversion:5 -optimize+ -target:winexe -out:"$KEYDIR/viewer.exe" -r:System.Windows.Forms.dll -r:System.Drawing.dll windows/viewer/*.cs >/dev/null \
    || bad "the viewer builds"
  sleep 1
  GTVPN_X_COOKIE=$CK DISPLAY=$D XAUTHORITY=$XA TMPDIR=$KEYDIR setsid -f mono "$KEYDIR/viewer.exe" \
    --vnc "127.0.0.1:$PW" --x11 "127.0.0.1:$QW" --label lab >/dev/null 2>&1 </dev/null
  srv "DISPLAY=:$NW nohup xlogo -geometry 400x300 -title gtvpn-lab-xlogo >/dev/null 2>&1 &"
  srv "DISPLAY=:$NW nohup xev -geometry 300x200 -name gtvpn-lab-xev >/tmp/gtvpn-lab-xev.log 2>&1 &"
  shown(){ for _ in $(seq 30); do desk xwininfo -root -tree 2>/dev/null | grep -q "\"$1\"" && return 0; sleep 0.5; done; tail -5 "$KEYDIR/gtvpn-viewer.log"; return 1; }
  export -f shown; export KEYDIR D XA
  check "each window of the session appears as a window of its own here" bash -c "shown gtvpn-lab-xlogo && shown gtvpn-lab-xev"
  check "the viewer is the session's window manager now" \
    bash -c "id=\$(srv \"xprop -display :$NW -root _NET_SUPPORTING_WM_CHECK\" | sed -n 's/.*# //p'); srv \"xprop -display :$NW -id \$id _NET_WM_NAME\" | grep -q gtvpn"
  # Same pixels: sample the xlogo window in the session and on this desktop.
  geom(){ srv "xwininfo -display :$NW -name $1" | awk '/Absolute upper-left X/{x=$NF} /Absolute upper-left Y/{y=$NF} /Width/{w=$NF} /Height/{h=$NF} END{print x, y, w, h}'; }
  read -r LX LY LW LH <<<"$(geom gtvpn-lab-xlogo)"
  sleep 2
  srv "xwd -display :$NW -root -silent" > "$KEYDIR/s.xwd"; desk xwd -root -silent > "$KEYDIR/d.xwd"
  check "the native window shows the session's picture (same pixels at the same place)" \
    python3 - "$KEYDIR/s.xwd" "$KEYDIR/d.xwd" "$LX" "$LY" "$LW" "$LH" <<'PY'
import struct, sys
def load(p):
    b = open(p, 'rb').read(); h = struct.unpack('>25I', b[:100])
    off = h[0] + h[19] * 12; bpl = h[12]; little = h[7] == 0
    def px(x, y):
        v = b[off + y * bpl + x * 4: off + y * bpl + x * 4 + 4]
        v = struct.unpack('<I' if little else '>I', v)[0]
        return (v >> 16) & 255, (v >> 8) & 255, v & 255
    return px
s, d = load(sys.argv[1]), load(sys.argv[2]); x, y, w, h = map(int, sys.argv[3:7])
pts = [(x + 2 + (w - 4) * i // 9, y + 2 + (h - 4) * j // 9) for i in range(10) for j in range(10)]
same = sum(all(abs(a - c) <= 24 for a, c in zip(s(*p), d(*p))) for p in pts)
print(same, 'of', len(pts), 'sample points match'); sys.exit(0 if same >= 90 else 1)
PY
  read -r EX EY EW EH <<<"$(geom gtvpn-lab-xev)"
  desk xdotool mousemove $((EX + EW / 2)) $((EY + EH / 2)) sleep 0.3 click 1 sleep 0.5 type --delay 50 gtvpn >/dev/null 2>&1
  # The letters of the KeyPress events that xev in the session saw.
  keys(){ srv 'cat /tmp/gtvpn-lab-xev.log' | awk '/^KeyPress/{k=1} /^KeyRelease/{k=0} k && /keysym/{match($0, /keysym 0x[0-9a-f]+, [^)]*/); t=substr($0, RSTART, RLENGTH); sub(/.*, /, "", t); printf "%s", t; k=0}'; }
  typed(){ for _ in $(seq 10); do [[ $(keys) == *gtvpn* ]] && return 0; sleep 0.5; done; echo "keys seen: $(keys); clicks: $(srv 'grep -c ButtonPress /tmp/gtvpn-lab-xev.log')"; return 1; }
  check "clicks and typing reach the program in the session" typed
  pkill -f -- "$KEYDIR/viewer.exe"; sleep 1
  check "after the viewer, wm (as attach does, for the TigerVNC viewer) brings metacity back" \
    bash -c "dev 'gtvpn-vnc cadence wm' | grep -q 'window manager restarted' && on_srv 'pgrep -u gtuser -x metacity' >/dev/null"
  check "and wm leaves a running window manager alone" \
    bash -c "[[ -z \$(dev 'gtvpn-vnc cadence wm') && \$(on_srv 'pgrep -u gtuser -xc metacity') == 1 ]]"
  docker rm -f gtvpn-lab-fwd >/dev/null
  check "stop also closes the X display's forward" \
    bash -c "dev 'gtvpn-vnc cadence stop --force' >/dev/null && ! dev '(exec 3<>/dev/tcp/127.0.0.1/$QW)' 2>/dev/null"
else
  echo "SKIP gtvpn's own viewer (needs mono and mcs here)"
fi
# The host CLI, as on a Linux computer: ./gtvpn vnc with a terminal (script),
# its ssh gtvpn forward, and a stand-in viewer that records its options and
# reads the VNC greeting through the forwards.
B=$KEYDIR/bin; mkdir -p "$B"
printf 'Host gtvpn\n  HostName 127.0.0.1\n  Port %s\n  User dev\n  IdentityFile %s/id\n  IdentitiesOnly yes\n  StrictHostKeyChecking no\n  UserKnownHostsFile /dev/null\n  LogLevel error\n' "$PORT" "$KEYDIR" > "$KEYDIR/hostcfg"
printf '#!/bin/bash\nexec ssh -F %s/hostcfg "$@"\n' "$KEYDIR" > "$B/ssh.real"
printf '#!/bin/bash\nPATH=${PATH#*:} exec "$(dirname "$0")/ssh.real" "$@"\n' > "$B/ssh"
cat > "$B/vncviewer" <<'V'
#!/bin/bash
d=$(dirname "$0")/..; echo "$*" >> "$d/viewer-args"
p=${*: -1}; p=${p##*::}
exec 3<>"/dev/tcp/127.0.0.1/$p" && head -c 12 <&3 >> "$d/viewer-greeting"
V
chmod +x "$B/ssh" "$B/ssh.real" "$B/vncviewer"
cli(){ timeout 60 script -qec "PATH=$B:\$PATH ./gtvpn $(printf '%q ' "$@")" /dev/null 2>&1 | tr -d '\r'; }
export -f cli; export B KEYDIR
OUT=$(cli vnc cadence); NC=$(sed -n 's/^Session display on cadence: :\([0-9]*\) .*/\1/p' <<<"$OUT"); PC=$((15900 + ${NC:-0}))
check "gtvpn vnc (host CLI) starts the session, opens the viewer and prints the DISPLAY steps (:$NC)" \
  bash -c "[[ -n '$NC' ]] && grep -q 'export DISPLAY=' <<<\"\$1\"" _ "$OUT"
[[ -n $NC ]] || echo "$OUT"
sleep 2
check "the viewer gets its options and this computer's port" grep -q -- "-SecurityTypes=None -AutoSelect=1 -RemoteResize=1 .*127.0.0.1::$PC\$" "$KEYDIR/viewer-args"
check "the viewer reaches the session through ssh gtvpn and the container" grep -q '^RFB 003\.00' "$KEYDIR/viewer-greeting"
check "one background ssh gtvpn serves this computer's port" bash -c "[[ \$(pgrep -fc -- '-L 127.0.0.1:$PC:127.0.0.1:$PC [g]tvpn') == 1 ]]"
cli vnc cadence attach >/dev/null; sleep 2
check "attach reopens the viewer and reuses that forward" \
  bash -c "[[ \$(grep -c 'RFB 003' $KEYDIR/viewer-greeting) == 2 && \$(pgrep -fc -- '-L 127.0.0.1:$PC:127.0.0.1:$PC [g]tvpn') == 1 ]]"
check "gtvpn vnc status shows it" bash -c "cli vnc cadence status | grep -q 'session :$NC is running'"
if command -v mono >/dev/null && command -v mcs >/dev/null; then
  srv "DISPLAY=:$NC nohup xlogo -title gtvpn-lab-xlogo2 >/dev/null 2>&1 &"
  OUT=$(DISPLAY=$D XAUTHORITY=$XA XDG_CACHE_HOME=$KEYDIR/cache TMPDIR=$KEYDIR cli vnc cadence windows)
  check "gtvpn vnc windows (host CLI) builds the viewer and shows the session's windows here" \
    bash -c "grep -q 'appears here as a window of its own' <<<\"\$1\" && shown gtvpn-lab-xlogo2" _ "$OUT"
  grep -q 'appears here as a window of its own' <<<"$OUT" || echo "$OUT"
  pkill -f -- "$KEYDIR/cache/gtvpn/gtvpn-viewer.exe"; srv 'pkill -x xlogo'; sleep 1
fi
check "gtvpn vnc stop ends it" bash -c "cli vnc cadence stop | grep -q 'stopped session :$NC'"
check "attach without a session says so" bash -c "cli vnc cadence attach | grep -q 'no session on cadence'"
check "a bad host name is refused" bash -c "cli vnc 'a;b' | grep -q 'usage: gtvpn vnc'"
check "an unknown action is refused" bash -c "cli vnc cadence bogus | grep -q 'usage: gtvpn vnc'"
pkill -f -- "-L 127.0.0.1:$PC:127.0.0.1:$PC [g]tvpn" 2>/dev/null
docker exec gtvpn pkill -9 openconnect; sleep 2
check "after tunnel death the GT server is unreachable" \
  bash -c "! dev 'timeout 8 ssh -o BatchMode=yes -o ControlPath=none -o ConnectTimeout=4 cadence true' 2>/dev/null"
exit $FAILED
