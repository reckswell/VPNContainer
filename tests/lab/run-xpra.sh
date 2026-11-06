#!/usr/bin/env bash
# xpra lab: `gtvpn xpra` end to end, with a real tunnel.
#   GT server (ocserv stand-in): xpra installed by remote.sh, the session
#   gtvpn container: gtvpn-xpra (start/stop), gtvpn-xpra-proxy
#   this computer: the xpra viewer on an Xvfb "desktop", through ssh gtvpn
# The first run installs xpra from conda-forge into a Docker volume
# (gtvpn-lab-xpra), which needs internet here; later runs reuse it.
#   LAB_XPRA_REINSTALL=1  reinstall into the volume
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1
BASE=${LAB_BASE_IMAGE:-ubuntu:24.04}; BNET=${LAB_BUILD_NETWORK:-default}
FILES=(-f compose.yaml); [[ ${LAB_NO_IPV6:-0} == 1 ]] && FILES+=(-f tests/lab/compose.nov6.yaml)
FILES+=(-f tests/lab/compose.lab.yaml -f tests/lab/compose.xpra.yaml)
DC() { docker compose -p gtvpn-lab "${FILES[@]}" "$@"; }
FAILED=0; ok(){ printf '\033[32mPASS\033[0m %s\n' "$*"; }; bad(){ printf '\033[31mFAIL\033[0m %s\n' "$*"; FAILED=1; }
check(){ local d=$1; shift; if "$@"; then ok "$d"; else bad "$d"; fi; }
VOL=gtvpn-lab-xpra; D=:78; XA=$(mktemp); KEYDIR=$(mktemp -d)
cleanup(){ [[ ${KEEP:-0} == 1 ]] && return; docker rm -f gtvpn-lab-viewer >/dev/null 2>&1; DC down -v --remove-orphans >/dev/null 2>&1
  kill "$XPID" 2>/dev/null; rm -rf "$XA" "$KEYDIR" runtime/in/xauth runtime/in/hosts.conf; }
trap cleanup EXIT

docker build -q --network "$BNET" --build-arg BASE_IMAGE="$BASE" -t gtvpn:local image >/dev/null || exit 1
docker build -q --network "$BNET" --build-arg BASE_IMAGE="$BASE" -t lab/ocserv tests/lab/ocserv >/dev/null || exit 1
docker build -q --network "$BNET" -t lab/gtvpn-client -f tests/lab/Dockerfile.client tests/lab >/dev/null || exit 1

# GT server user ids are fixed by the ocserv image's start.sh (gtuser is the
# first user it adds); the volume's files must belong to it.
GTUID=$(docker run --rm --entrypoint sh lab/ocserv -c 'useradd -m gtuser >/dev/null 2>&1; id -u gtuser')
docker volume create "$VOL" >/dev/null
if [[ ${LAB_XPRA_REINSTALL:-0} == 1 ]] || ! docker run --rm -v "$VOL:/x" --entrypoint test lab/ocserv -x /x/env/bin/xpra; then
  # remote.sh install as the GT user, exactly as gtvpn-xpra runs it on GT.
  CA=(); for v in SSL_CERT_FILE CURL_CA_BUNDLE; do [[ -n ${!v:-} && -f ${!v} ]] && { CA=(-v "${!v}:/ca.crt:ro" -e SSL_CERT_FILE=/ca.crt -e CURL_CA_BUNDLE=/ca.crt -e MAMBA_SSL_VERIFY=/ca.crt); break; }; done
  docker run --rm --network host -e HTTPS_PROXY="${HTTPS_PROXY:-}" -e https_proxy="${https_proxy:-}" "${CA[@]}" \
    -v "$VOL:/home/gtuser/.local/gtvpn-xpra" -v "$PWD/image/rootfs/usr/local/share/gtvpn/xpra:/src:ro" --entrypoint sh lab/ocserv -c "
      useradd -m -u $GTUID gtuser >/dev/null 2>&1; chown $GTUID /home/gtuser/.local/gtvpn-xpra
      su gtuser -c 'cp /src/xpra-linux-64.lock ~/.local/gtvpn-xpra/xpra.lock && sh -s -- install < /src/remote.sh'" > "$KEYDIR/install.log" 2>&1
  check "remote.sh installs xpra on a fresh GT account (no root)" grep -q '^installed v6\.2' "$KEYDIR/install.log"
  grep -q '^installed v6\.2' "$KEYDIR/install.log" || { tail -20 "$KEYDIR/install.log"; exit 1; }
else echo "INFO xpra already installed in volume $VOL (LAB_XPRA_REINSTALL=1 to redo the install test)"; fi

# The "desktop" of this computer, and the ssh key/config the viewer uses
xauth -f "$XA" add "$D" . "$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
pkill -f "Xvfb $D" 2>/dev/null; Xvfb "$D" -auth "$XA" -nolisten tcp >/dev/null 2>&1 & XPID=$!
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
docker exec gtvpn-lab-ocserv sh -c "chown gtuser: /home/gtuser; [ \$(id -u gtuser) = $GTUID ]" || { bad "gtuser uid"; exit 1; }
PORT=$(docker port gtvpn 2222/tcp | head -1 | sed 's/.*://')
printf 'Host gtvpn\n  HostName 127.0.0.1\n  Port %s\n  User dev\n  IdentityFile /k/id\n  IdentitiesOnly yes\n  StrictHostKeyChecking no\n  UserKnownHostsFile /dev/null\n  LogLevel error\n' "$PORT" > "$KEYDIR/config"

dev(){ docker exec -u dev gtvpn bash -lc "$1"; }
srv(){ docker exec -u gtuser -e HOME=/home/gtuser -e XDG_RUNTIME_DIR=/tmp gtvpn-lab-ocserv sh -c "$1"; }
dev 'test -s ~/.ssh/id_ed25519 || ssh-keygen -q -t ed25519 -N "" -f ~/.ssh/id_ed25519'
docker exec gtvpn cat /home/dev/.ssh/id_ed25519.pub | docker exec -i gtvpn-lab-ocserv sh -c 'cat > /etc/ssh/lab_keys/gtuser; chmod 644 /etc/ssh/lab_keys/gtuser'
dev 'ssh -o StrictHostKeyChecking=accept-new -o BatchMode=yes cadence true' || { bad "ssh to the GT server"; exit 1; }
export -f dev srv

OUT=$(dev 'gtvpn-xpra cadence start' 2>&1); N=$(sed -n 's/^display=:\([0-9]*\)$/\1/p' <<<"$OUT")
check "gtvpn-xpra starts a session over the existing connection (display :$N)" test -n "$N"
[[ -n $N ]] || { echo "$OUT"; exit 1; }
check "~/.gtvpn-display on the server names it" bash -c "[[ \$(srv 'cat ~/.gtvpn-display') == :$N ]]"
check "starting again reuses the same session" bash -c "dev 'gtvpn-xpra cadence start' | grep -qx 'display=:$N'"
check "another session on the server (like VS Code's terminal) draws on it" srv 'DISPLAY=$(cat ~/.gtvpn-display) xdpyinfo >/dev/null'

# The viewer: xpra on "this computer", reaching the session through ssh gtvpn
# and the proxy, drawing on the Xvfb desktop.
docker run -d --name gtvpn-lab-viewer --network host -v "$VOL:/home/gtuser/.local/gtvpn-xpra:ro" \
  -v /tmp/.X11-unix:/tmp/.X11-unix -v "$XA:/xauth:ro" -v "$KEYDIR:/k:ro" -e DISPLAY="$D" -e XAUTHORITY=/xauth \
  --entrypoint /home/gtuser/.local/gtvpn-xpra/env/bin/xpra lab/ocserv \
  attach "ssh://gtvpn/$N" --ssh="ssh -F /k/config" --remote-xpra=/home/dev/.gtvpn/xpra/cadence \
  --splash=no --tray=no --notifications=no --speaker=off --microphone=off --webcam=no --opengl=no >/dev/null
srv "DISPLAY=:$N nohup xlogo -geometry 320x240 -title gtvpn-lab-xlogo >/dev/null 2>&1 &"
seen(){ for _ in $(seq 40); do XAUTHORITY=$XA xwininfo -display "$D" -root -tree 2>/dev/null | grep -q gtvpn-lab-xlogo && return 0; sleep 0.5; done; return 1; }
check "a window on the server appears on this computer's desktop through the viewer" seen
check "the viewer's traffic is a channel of the container's connection to the server" \
  bash -c "docker exec gtvpn-lab-ocserv pgrep -u gtuser -f 'xpra _proxy :$N' >/dev/null"
check "the proxy refuses to run without a host link" \
  bash -c "! dev '/usr/local/bin/gtvpn-xpra-proxy _proxy :$N' 2>/dev/null"
check "the proxy refuses anything but '_proxy :N'" \
  bash -c "! dev '~/.gtvpn/xpra/cadence _proxy \"; touch /tmp/x\"' 2>/dev/null && ! srv 'test -e /tmp/x'"
docker rm -f gtvpn-lab-viewer >/dev/null
dev 'ssh -O exit cadence' >/dev/null 2>&1; sleep 1
check "without an open connection the proxy fails at once instead of signing in" \
  bash -c "timeout 10 ssh -F '$KEYDIR/config' gtvpn '~/.gtvpn/xpra/cadence _proxy :$N' 2>&1 | grep -q 'no open connection'"
check "the session (and its window) survived the viewer and the connection closing" \
  bash -c "dev 'ssh -o BatchMode=yes cadence true' && dev 'gtvpn-xpra cadence display' | grep -qx 'display=:$N' && srv 'xwininfo -display :$N -root -tree | grep -q gtvpn-lab-xlogo'"
check "status lists the open window" bash -c "dev 'gtvpn-xpra cadence status' | grep -q 'window: gtvpn-lab-xlogo'"
check "stop refuses while a window is open" bash -c "dev 'gtvpn-xpra cadence stop'; [[ \$? == 3 ]] && srv 'xwininfo -display :$N -root >/dev/null 2>&1'"
check "stop --force ends it and removes ~/.gtvpn-display" bash -c "dev 'gtvpn-xpra cadence stop --force' >/dev/null && ! srv 'test -e ~/.gtvpn-display'"
gone(){ for _ in $(seq 10); do ! docker exec gtvpn-lab-ocserv pgrep -u gtuser -f 'Xvfb|xpra|xlogo' >/dev/null && return 0; sleep 0.5; done; docker exec gtvpn-lab-ocserv pgrep -u gtuser -af 'Xvfb|xpra|xlogo'; return 1; }
check "nothing of the session is left running on the server" gone
docker exec gtvpn pkill -9 openconnect; sleep 2
check "after tunnel death the server is unreachable for the proxy too" \
  bash -c "! dev 'timeout 8 ssh -o BatchMode=yes -o ControlPath=none -o ConnectTimeout=4 cadence true' 2>/dev/null"
exit $FAILED
