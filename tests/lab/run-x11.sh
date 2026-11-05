#!/usr/bin/env bash
# X11 lab: proves the Cadence path end to end on a Linux host:
#   remote X client (GT server) --ssh -X over the tunnel--> container --> host X server
# Uses Xvfb as the "host display". Requires Xvfb, xauth, iproute2 on the host.
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1
BASE=${LAB_BASE_IMAGE:-ubuntu:24.04}; BNET=${LAB_BUILD_NETWORK:-default}
FILES=(-f compose.yaml); [[ ${LAB_NO_IPV6:-0} == 1 ]] && FILES+=(-f tests/lab/compose.nov6.yaml); FILES+=(-f tests/lab/compose.lab.yaml)
DC() { docker compose -p gtvpn-lab "${FILES[@]}" "$@"; }
FAILED=0; ok(){ printf '\033[32mPASS\033[0m %s\n' "$*"; }; bad(){ printf '\033[31mFAIL\033[0m %s\n' "$*"; FAILED=1; }
check(){ local d=$1; shift; if "$@"; then ok "$d"; else bad "$d"; fi; }
D=:77; XA=$(mktemp)
cleanup(){ [[ ${KEEP:-0} == 1 ]] && return; DC down -v --remove-orphans >/dev/null 2>&1; kill "$XPID" 2>/dev/null; rm -f "$XA" runtime/in/xauth; }
trap cleanup EXIT

docker build -q --network "$BNET" --build-arg BASE_IMAGE="$BASE" -t lab/ocserv tests/lab/ocserv >/dev/null || exit 1
# Host X server with a real auth cookie (like Xwayland on Ubuntu)
xauth -f "$XA" add "$D" . "$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
pkill -f "Xvfb $D" 2>/dev/null; Xvfb "$D" -auth "$XA" -nolisten tcp >/dev/null 2>&1 & XPID=$!
sleep 1
# What `gtvpn up` does: wildcard-family copy of the cookie for the container
mkdir -p runtime/in; rm -f runtime/in/xauth
XAUTHORITY=$XA xauth nlist "$D" | sed -e 's/^..../ffff/' | xauth -f runtime/in/xauth nmerge -
chmod 644 runtime/in/xauth   # lab: container 'dev' uid differs from root here
check "host X server rejects clients without the cookie" bash -c "! XAUTHORITY=/dev/null xdpyinfo -display $D >/dev/null 2>&1"

DC down -v --remove-orphans >/dev/null 2>&1
LAB_MODE=split DC up -d ocserv fakeapi >/dev/null 2>&1
for _ in $(seq 20); do docker logs gtvpn-lab-ocserv 2>&1 | grep -q '^LABPIN' && break; sleep 0.5; done
export LAB_PIN; LAB_PIN=$(docker logs gtvpn-lab-ocserv 2>&1 | awk '/^LABPIN/{print $2}')
GTVPN_DISPLAY=$D GTVPN_X11_DIR=/tmp/.X11-unix GTVPN_SSH_HOSTS="cadence=10.66.0.1" GTVPN_GT_USER=gtuser DC up -d gtvpn >/dev/null 2>&1
for _ in $(seq 60); do docker logs gtvpn 2>&1 | grep -q 'ready\.' && break; sleep 1; done
docker exec gtvpn lab-connect >/dev/null || { bad "tunnel"; exit 1; }

dev(){ docker exec -u dev gtvpn bash -lc "$1"; }
check "container sees DISPLAY=$D and the cookie" bash -c "docker exec -u dev gtvpn bash -lc 'echo \$DISPLAY \$XAUTHORITY' | grep -q '$D /home/dev/.Xauthority'"
check "container app draws on the host display (xdpyinfo)" dev "xdpyinfo >/dev/null"
dev "test -s ~/.ssh/id_ed25519 || ssh-keygen -q -t ed25519 -N '' -f ~/.ssh/id_ed25519"
docker exec gtvpn cat /home/dev/.ssh/id_ed25519.pub | docker exec -i gtvpn-lab-ocserv sh -c 'cat > /etc/ssh/lab_keys/gtuser; chmod 644 /etc/ssh/lab_keys/gtuser'
check "ssh alias 'cadence' (from GTVPN_SSH_HOSTS) reaches the GT server through the tunnel" \
  dev "ssh -o StrictHostKeyChecking=accept-new -o BatchMode=yes cadence true"
check "remote X app on the GT server displays on the host (ssh -Y, like Virtuoso)" \
  dev "ssh -o BatchMode=yes cadence 'xdpyinfo >/dev/null'"
# Untrusted (-X) forwarding depends on the X server's SECURITY extension and
# breaks many EDA tools anyway, so it is informational only.
if dev "ssh -o BatchMode=yes -o ForwardX11Trusted=no -o ControlPath=none cadence 'xdpyinfo >/dev/null'" 2>/dev/null; then
  echo "INFO untrusted forwarding (ssh -X) also works on this X server"
else echo "INFO untrusted forwarding (ssh -X) does not work on this X server; use the default trusted mode"; fi
check "ssh connection multiplexing is active (one Duo prompt per host)" dev "ssh -O check cadence 2>&1 | grep -qi running"
docker exec gtvpn pkill -9 openconnect; sleep 2
check "after tunnel death the GT server is unreachable" bash -c "! docker exec -u dev gtvpn bash -lc 'ssh -o BatchMode=yes -o ControlPath=none -o ConnectTimeout=4 cadence true' 2>/dev/null"
exit $FAILED
