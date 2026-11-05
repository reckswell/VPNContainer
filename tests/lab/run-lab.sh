#!/usr/bin/env bash
# End-to-end lab for the gtvpn kill-switch, using a real tunnel.
#
# A local ocserv container stands in for the GT gateway (it serves "internal"
# hosts 10.66.0.1:2200 and 198.18.0.10:22 plus DNS only through the tunnel),
# and a fake HTTPS API stands in for an allow-listed external service such
# as api.anthropic.com. The gtvpn image is used unchanged except that the
# openconnect CLI is added to talk to ocserv.
#
#   tests/lab/run-lab.sh            run everything (split + full tunnel)
#   LAB_BASE_IMAGE=... run-lab.sh   base image override (e.g. behind a proxy)
#   LAB_BUILD_NETWORK=host          docker build --network value
#   LAB_NO_IPV6=1                   for kernels without IPv6
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1
BASE=${LAB_BASE_IMAGE:-ubuntu:24.04}
BNET=${LAB_BUILD_NETWORK:-default}
FILES=(-f compose.yaml)
[[ ${LAB_NO_IPV6:-0} == 1 ]] && FILES+=(-f tests/lab/compose.nov6.yaml)
FILES+=(-f tests/lab/compose.lab.yaml)
DC() { docker compose -p gtvpn-lab "${FILES[@]}" "$@"; }
KEYDIR=$(mktemp -d)
RESULTS=()
FAILED=0

hdr()  { printf '\n\033[1;36m### %s\033[0m\n' "$*"; }
ok()   { printf '\033[32mPASS\033[0m %s\n' "$*"; RESULTS+=("PASS $*"); }
bad()  { printf '\033[31mFAIL\033[0m %s\n' "$*"; RESULTS+=("FAIL $*"); FAILED=1; }
check(){ local d=$1; shift; if "$@"; then ok "$d"; else bad "$d"; fi; }
cx()   { docker exec gtvpn "$@"; }
asdev(){ docker exec gtvpn setpriv --reuid=dev --regid=dev --init-groups -- "$@"; }

cleanup() { DC down -v --remove-orphans >/dev/null 2>&1; rm -rf "$KEYDIR"; }
trap cleanup EXIT

hdr "build"
docker build -q --network "$BNET" --build-arg BASE_IMAGE="$BASE" -t gtvpn:local image >/dev/null || exit 1
docker build -q --network "$BNET" --build-arg BASE_IMAGE="$BASE" -t lab/ocserv tests/lab/ocserv >/dev/null || exit 1
docker build -q --network "$BNET" -t lab/gtvpn-client -f tests/lab/Dockerfile.client tests/lab >/dev/null || exit 1
[[ -s tests/lab/fakeapi/c.pem ]] || openssl req -x509 -newkey rsa:2048 -nodes -keyout tests/lab/fakeapi/k.pem \
  -out tests/lab/fakeapi/c.pem -days 7 -subj /CN=api.fake-anthropic.test >/dev/null 2>&1
chmod 644 tests/lab/fakeapi/*.pem

# SSH key for the "host" side
docker run --rm -v "$KEYDIR:/k" --entrypoint ssh-keygen gtvpn:local -q -t ed25519 -N '' -f /k/id >/dev/null
mkdir -p runtime/in runtime/out; cp "$KEYDIR/id.pub" runtime/in/authorized_keys

start_lab() { # mode
  DC down -v --remove-orphans >/dev/null 2>&1
  LAB_MODE=$1 DC up -d ocserv fakeapi >/dev/null 2>&1
  for _ in $(seq 20); do docker logs gtvpn-lab-ocserv 2>&1 | grep -q '^LABPIN' && break; sleep 0.5; done
  export LAB_PIN; LAB_PIN=$(docker logs gtvpn-lab-ocserv 2>&1 | awk '/^LABPIN/{print $2}')
  LAB_MODE=$1 DC up -d gtvpn >/dev/null 2>&1
  for _ in $(seq 60); do docker logs gtvpn 2>&1 | grep -q 'ready\.\|REFUS\|FAILED' && break; sleep 1; done
}

command -v ip >/dev/null || { echo "the lab needs iproute2 on the host"; exit 1; }
host_cannot_reach() { # ip port   (from the Docker host's own network namespace)
  ! timeout 5 bash -c "exec 3<>/dev/tcp/$1/$2" 2>/dev/null
}
host_route_not_tun() { local r; r=$(ip -4 route get "$1" 2>/dev/null) || return 1; ! grep -qE 'dev (tun|gpd|vpn)' <<<"$r"; }
export GTVPN_PROBE_TARGETS="10.66.0.1:2200 198.18.0.10:22"

for MODE in split full; do
  hdr "scenario: $MODE tunnel"
  start_lab "$MODE"
  check "[$MODE] container started and passed its pre-connect self-test" \
    bash -c 'docker logs gtvpn 2>&1 | grep -q "pre-connect leak self-test passed"'
  check "[$MODE] host cannot reach GT-internal 10.66.0.1:2200 before connect" host_cannot_reach 10.66.0.1 2200

  if [[ $MODE == split ]]; then
    # The real sign-in path (gtvpn-connect, OpenConnect: authenticate, then
    # the background tunnel from the cookie), against ocserv's protocol.
    check "[$MODE] tunnel comes up via gtvpn-connect (OpenConnect password path)" bash -c \
      "docker exec -i -u root -e GTVPN_AUTH=password -e GTVPN_CLIENT=openconnect -e GTVPN_PORTAL=ocserv \
         -e GTVPN_OC_PROTOCOL=anyconnect -e GTVPN_OC_ARGS=--no-dtls \
         -e 'GTVPN_OC_AUTH_ARGS=-u gtuser --passwd-on-stdin --servercert $LAB_PIN' \
         gtvpn gtvpn-connect <<<student >/tmp/gtvpn-lab-connect.log 2>&1 || { cat /tmp/gtvpn-lab-connect.log; false; }"
    check "[$MODE] the session cookie is not on any command line" bash -c \
      "! docker exec gtvpn sh -c 'for f in /proc/[0-9]*/cmdline; do tr \"\\\\000\" \" \" <\$f; echo; done' 2>/dev/null | grep -qi 'webvpn=\\|authcookie='"
    check "[$MODE] OpenConnect runs as root:gpvpn with no terminal (closing the window cannot stop it)" bash -c \
      "[[ \$(docker exec gtvpn ps -o user=,group=,tty= -C openconnect | head -1 | tr -s ' ') == 'root gpvpn ?' ]]"
  else
    check "[$MODE] tunnel comes up" cx lab-connect
  fi
  check "[$MODE] container verification suite (full) passes" cx gtvpn-verify --full --quiet
  check "[$MODE] dev reaches GT-internal 10.66.0.1:2200 via tunnel" asdev nc -z -n -w 3 10.66.0.1 2200
  check "[$MODE] dev resolves internal.gt.test via tunnel DNS" asdev getent ahostsv4 internal.gt.test
  check "[$MODE] dev reaches allow-listed API via direct proxy" \
    asdev curl -fsS -k -m 10 -x http://127.0.0.1:3128 https://api.fake-anthropic.test/
  check "[$MODE] proxy egress leaves via eth0 even with VPN up" \
    bash -c "docker exec gtvpn ip -4 route get 172.30.66.20 mark 0x1a6 | grep -q 'dev eth0'"

  # Host side, with the tunnel up
  check "[$MODE] host still cannot reach 10.66.0.1:2200 (tunnel is container-only)" host_cannot_reach 10.66.0.1 2200
  check "[$MODE] host cannot reach 198.18.0.10:22 (GT-internal)" host_cannot_reach 198.18.0.10 22
  check "[$MODE] host routing table has no VPN route for 10.66.0.1" host_route_not_tun 10.66.0.1
  check "[$MODE] host-side verification suite passes" bash host/verify-host.sh --quiet

  # Host -> container SSH -> ProxyJump into a GT-internal host
  SSHOPTS=(-i /k/id -o IdentitiesOnly=yes -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o BatchMode=yes -o LogLevel=ERROR)
  check "[$MODE] host SSH into container (127.0.0.1:2222, key only)" \
    docker run --rm --network host -v "$KEYDIR:/k:ro" --entrypoint ssh gtvpn:local "${SSHOPTS[@]}" -p 2222 dev@127.0.0.1 'echo $GTVPN'
  check "[$MODE] host SSH password login is refused" \
    bash -c "! docker run --rm --network host --entrypoint ssh gtvpn:local -o PreferredAuthentications=password -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o BatchMode=yes -p 2222 dev@127.0.0.1 true 2>/dev/null"
  check "[$MODE] root SSH login is refused" \
    bash -c "! docker run --rm --network host -v $KEYDIR:/k:ro --entrypoint ssh gtvpn:local ${SSHOPTS[*]} -p 2222 root@127.0.0.1 true 2>/dev/null"
  check "[$MODE] host -> container -> GT-internal 10.66.0.1:2200 via ssh -W (ProxyJump path)" \
    bash -c "docker run --rm --network host -v $KEYDIR:/k:ro --entrypoint ssh gtvpn:local ${SSHOPTS[*]} -p 2222 -W 10.66.0.1:2200 dev@127.0.0.1 </dev/null 2>/dev/null | grep -q SSH-2.0-LabFake"
  check "[$MODE] ProxyJump cannot be abused to reach the internet outside the tunnel" \
    bash -c "! docker run --rm --network host -v $KEYDIR:/k:ro --entrypoint ssh gtvpn:local ${SSHOPTS[*]} -p 2222 -W 172.30.66.10:8080 dev@127.0.0.1 </dev/null 2>/dev/null | grep -q public"

  # Kill-switch under failure: tunnel dies
  cx pkill -9 openconnect; sleep 2
  check "[$MODE] after tunnel death: dev cannot reach 10.66.0.1:2200" bash -c '! docker exec gtvpn setpriv --reuid=dev --regid=dev --init-groups -- nc -z -n -w 3 10.66.0.1 2200'
  check "[$MODE] after tunnel death: dev cannot reach the gateway's public service" bash -c '! docker exec gtvpn setpriv --reuid=dev --regid=dev --init-groups -- nc -z -n -w 3 172.30.66.10 8080'
  check "[$MODE] after tunnel death: verification suite (pre) passes" cx gtvpn-verify --pre --quiet

  # Kill-switch under tampering: someone opens the firewall
  cx lab-connect >/dev/null 2>&1
  cx iptables -P OUTPUT ACCEPT; cx iptables -I OUTPUT 1 -j ACCEPT
  sleep 6
  check "[$MODE] watchdog detected tampering and re-locked the firewall" cx /usr/local/sbin/gtvpn-firewall check
  check "[$MODE] watchdog tore the tunnel down after tampering" bash -c '! docker exec gtvpn ip link show tun0 >/dev/null 2>&1'
  check "[$MODE] after re-lock: verification suite (pre) passes" cx gtvpn-verify --pre --quiet
done

hdr "summary"
printf '%s\n' "${RESULTS[@]}" | sort | uniq -c | sed 's/^ *//' >/dev/null
P=$(printf '%s\n' "${RESULTS[@]}" | grep -c '^PASS'); F=$(printf '%s\n' "${RESULTS[@]}" | grep -c '^FAIL')
echo "$P passed, $F failed"
printf '%s\n' "${RESULTS[@]}" > tests/lab/last-run.txt
exit $FAILED
