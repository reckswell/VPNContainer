#!/usr/bin/env bash
# X11 lab: proves the Cadence path end to end on a Linux host:
#   remote X client (GT server) --ssh -X over the tunnel--> container --> host X server
# Uses Xvfb as the "host display". Requires Xvfb, xauth, iproute2 on the host.
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1
BASE=${LAB_BASE_IMAGE:-ubuntu:24.04}; BNET=${LAB_BUILD_NETWORK:-default}
FILES=(-f compose.yaml); [[ ${LAB_NO_IPV6:-0} == 1 ]] && FILES+=(-f tests/lab/compose.nov6.yaml); FILES+=(-f tests/lab/compose.lab.yaml)
DC() { docker compose -p vpncontainer-lab "${FILES[@]}" "$@"; }
FAILED=0; ok(){ printf '\033[32mPASS\033[0m %s\n' "$*"; }; bad(){ printf '\033[31mFAIL\033[0m %s\n' "$*"; FAILED=1; }
check(){ local d=$1; shift; if "$@"; then ok "$d"; else bad "$d"; fi; }
D=:77; XA=$(mktemp)
cleanup(){ [[ ${KEEP:-0} == 1 ]] && return; DC down -v --remove-orphans >/dev/null 2>&1; kill "$XPID" 2>/dev/null; rm -f "$XA" runtime/in/xauth runtime/in/hosts.conf; }
trap cleanup EXIT

docker build -q --network "$BNET" --build-arg BASE_IMAGE="$BASE" -t lab/ocserv tests/lab/ocserv >/dev/null || exit 1
# Host X server with a real auth cookie (like Xwayland on Ubuntu)
xauth -f "$XA" add "$D" . "$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
pkill -f "Xvfb $D" 2>/dev/null; Xvfb "$D" -auth "$XA" -nolisten tcp >/dev/null 2>&1 & XPID=$!
sleep 1
# What `vpncontainer up` does: wildcard-family copy of the cookie for the container
mkdir -p runtime/in; rm -f runtime/in/xauth
XAUTHORITY=$XA xauth nlist "$D" | sed -e 's/^..../ffff/' | xauth -f runtime/in/xauth nmerge -
chmod 644 runtime/in/xauth   # lab: container 'dev' uid differs from root here
check "host X server rejects clients without the cookie" bash -c "! XAUTHORITY=/dev/null xdpyinfo -display $D >/dev/null 2>&1"

DC down -v --remove-orphans >/dev/null 2>&1
LAB_MODE=split DC up -d ocserv fakeapi >/dev/null 2>&1
for _ in $(seq 20); do docker logs vpncontainer-lab-ocserv 2>&1 | grep -q '^LABPIN' && break; sleep 0.5; done
export LAB_PIN; LAB_PIN=$(docker logs vpncontainer-lab-ocserv 2>&1 | awk '/^LABPIN/{print $2}')
VPNCONTAINER_DISPLAY=$D VPNCONTAINER_X11_DIR=/tmp/.X11-unix VPNCONTAINER_SSH_HOSTS="cadence=10.66.0.1" VPNCONTAINER_GT_USER=gtuser DC up -d vpncontainer >/dev/null 2>&1
for _ in $(seq 60); do docker logs vpncontainer 2>&1 | grep -q 'ready\.' && break; sleep 1; done
docker exec vpncontainer lab-connect >/dev/null || { bad "tunnel"; exit 1; }

dev(){ docker exec -u dev vpncontainer bash -lc "$1"; }
check "container sees DISPLAY=$D and the cookie" bash -c "docker exec -u dev vpncontainer bash -lc 'echo \$DISPLAY \$XAUTHORITY' | grep -q '$D /home/dev/.Xauthority'"
check "container app draws on the host display (xdpyinfo)" dev "xdpyinfo >/dev/null"
dev "test -s ~/.ssh/id_ed25519 || ssh-keygen -q -t ed25519 -N '' -f ~/.ssh/id_ed25519"
docker exec vpncontainer cat /home/dev/.ssh/id_ed25519.pub | docker exec -i vpncontainer-lab-ocserv sh -c 'cat > /etc/ssh/lab_keys/gtuser; chmod 644 /etc/ssh/lab_keys/gtuser'
check "ssh alias 'cadence' (from VPNCONTAINER_SSH_HOSTS) reaches the GT server through the tunnel" \
  dev "ssh -o StrictHostKeyChecking=accept-new -o BatchMode=yes cadence true"
check "remote X app on the GT server displays on the host (ssh -Y, like Virtuoso)" \
  dev "ssh -o BatchMode=yes cadence 'xdpyinfo >/dev/null'"
# Untrusted (-X) forwarding depends on the X server's SECURITY extension and
# breaks many EDA tools anyway, so it is informational only.
if dev "ssh -o BatchMode=yes -o ForwardX11Trusted=no -o ControlPath=none cadence 'xdpyinfo >/dev/null'" 2>/dev/null; then
  echo "INFO untrusted forwarding (ssh -X) also works on this X server"
else echo "INFO untrusted forwarding (ssh -X) does not work on this X server; use the default trusted mode"; fi
check "ssh connection multiplexing is active (one Duo prompt per host)" dev "ssh -O check cadence 2>&1 | grep -qi running"
# hosts.conf (standard ssh config): an alias with only HostName/User still
# gets the GT defaults, so Virtuoso-style X11 works over a fresh connection.
printf 'Host labx\n    HostName 10.66.0.1\n    User gtuser\n' > runtime/in/hosts.conf
check "hosts.conf alias reaches the GT server and X11 works on a fresh connection" \
  bash -c "docker exec vpncontainer vpncontainer-hosts >/dev/null && docker exec -u dev vpncontainer bash -lc \"ssh -o BatchMode=yes -o ControlPath=none labx 'xdpyinfo >/dev/null'\""
# Display bridge (vpncontainer display): a terminal on the GT server that ssh -Y did
# not open, like VS Code Remote-SSH from the computer, still reaches the
# desktop. "On the server" here is docker exec into the server container.
srv(){ docker exec -u "$1" -e HOME="/home/$1" vpncontainer-lab-ocserv sh -c "$2"; }
# Within 5 s: no ~/.vpncontainer-display (or its old name) and no waiting sleep left for user $1
gone(){ for _ in $(seq 10); do srv "$1" '! test -e ~/.vpncontainer-display && ! test -e ~/.gtvpn-display' && ! docker exec vpncontainer-lab-ocserv pgrep -u "$1" -x sleep >/dev/null && return 0; sleep 0.5; done; return 1; }
export -f dev srv gone
docker exec vpncontainer cat /home/dev/.ssh/id_ed25519.pub | docker exec -i vpncontainer-lab-ocserv sh -c 'cat > /etc/ssh/lab_keys/gtcsh; chmod 644 /etc/ssh/lab_keys/gtcsh'
check "vpncontainer-display opens a display on the GT server, over the existing connection" \
  bash -c "dev 'vpncontainer-display cadence' | grep -q '^X display open on cadence: localhost:'"
check "a separate session on the server draws on the desktop with ~/.vpncontainer-display" \
  srv gtuser 'DISPLAY=$(cat ~/.vpncontainer-display) xdpyinfo >/dev/null'
check "...and with ~/.gtvpn-display, its name before the rename" \
  srv gtuser 'test "$(cat ~/.gtvpn-display)" = "$(cat ~/.vpncontainer-display)" && DISPLAY=$(cat ~/.gtvpn-display) xdpyinfo >/dev/null'
check "status reports the display, and a second start keeps the same one" \
  bash -c "a=\$(dev 'vpncontainer-display cadence status') && b=\$(dev 'vpncontainer-display cadence') && [[ \$a == 'open on cadence: DISPLAY=localhost:'* && \$a == \"\$b\" ]]"
check "while open, it waits on the server as one idle process" \
  bash -c "[[ \$(docker exec vpncontainer-lab-ocserv pgrep -u gtuser -x sleep | wc -l) == 1 ]]"
check "stop removes ~/.vpncontainer-display (and the old name) and leaves nothing running on the server" \
  bash -c "dev 'vpncontainer-display cadence stop' >/dev/null && gone gtuser"
check "stop leaves the shared connection open (no new Duo prompt)" dev "ssh -O check cadence 2>&1 | grep -qi running"
check "with a tcsh login shell and no open connection, it signs in and opens a display" \
  bash -c "dev 'vpncontainer-display gtcsh@10.66.0.1' | grep -q '^X display open on gtcsh@10.66.0.1: localhost:' && srv gtcsh 'DISPLAY=\$(cat ~/.vpncontainer-display) xdpyinfo >/dev/null'"
check "when the connection itself ends, the server side cleans up too" \
  bash -c "dev 'ssh -O exit gtcsh@10.66.0.1' 2>/dev/null; gone gtcsh && ! dev 'vpncontainer-display gtcsh@10.66.0.1 status' >/dev/null"
docker exec vpncontainer pkill -9 openconnect; sleep 2
check "after tunnel death the GT server is unreachable" bash -c "! docker exec -u dev vpncontainer bash -lc 'ssh -o BatchMode=yes -o ControlPath=none -o ConnectTimeout=4 cadence true' 2>/dev/null"
exit $FAILED
