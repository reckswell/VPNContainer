#!/usr/bin/env bash
# Updating a setup from before the rename (gtvpn) in place, the way a
# `git pull` does, while its container has a real tunnel up:
#   1. the old version (OLD_REF, default: the last commit with ./gtvpn) in a
#      scratch folder: setup, up, a tunnel, a file in the container's home,
#      a saved sign-in file
#   2. that folder updated to this checkout's tracked files, keeping the
#      untracked ones (.env, runtime/, the sign-in file)
#   3. ./vpncontainer works with the old container as it is, and `up`
#      leaves it and its tunnel alone
#   4. after `disconnect`: `up` replaces it, copying the home and the ssh
#      host key, and keeps the old volumes
# Run as a NORMAL user with docker access (not root). Uses a scratch HOME.
# It uses the real names (gtvpn, vpncontainer), so it refuses to run where
# a container or volume by those names already exists.
#   tests/lab/run-rename.sh [OLD_REF]
#   LAB_BASE_IMAGE, LAB_BUILD_NETWORK, LAB_NO_IPV6: as for run-lab.sh
#   LAB_BUILD_ARGS: more docker build arguments (e.g. a GP_IMAGE mirror)
set -uo pipefail
SRC=$(cd "$(dirname "$0")/../.." && pwd); cd "$SRC" || exit 1
[[ $(id -u) != 0 ]] || { echo "run as a non-root user with docker access"; exit 1; }
OLD_REF=${1:-$(git log -1 --format=%H --diff-filter=D -- gtvpn)^}
BASE=${LAB_BASE_IMAGE:-ubuntu:24.04}; BNET=${LAB_BUILD_NETWORK:-default}
read -ra XARGS <<<"${LAB_BUILD_ARGS:-}"
FAILED=0; ok(){ printf '\033[32mPASS\033[0m %s\n' "$*"; }; bad(){ printf '\033[31mFAIL\033[0m %s\n' "$*"; FAILED=1; }
check(){ local d=$1; shift; if "$@"; then ok "$d"; else bad "$d"; fi; }

for x in gtvpn vpncontainer; do
  if docker container inspect "$x" >/dev/null 2>&1 || docker volume inspect "${x}_home" >/dev/null 2>&1 || docker volume inspect "${x}_state" >/dev/null 2>&1; then
    echo "a '$x' container or volume exists here. This test uses those names, so it does not run where it could touch a real setup."
    exit 1
  fi
done
T=$(mktemp -d); W=$T/repo; mkdir -p "$W" "$T/home"
LABC=(ocserv fakeapi fakegw)
cleanup() {
  docker rm -f gtvpn vpncontainer "${LABC[@]/#/gtvpn-lab-}" "${LABC[@]/#/vpncontainer-lab-}" >/dev/null 2>&1
  docker network rm gtvpn_gtvpn_net vpncontainer_vpncontainer_net >/dev/null 2>&1
  docker volume rm gtvpn_home gtvpn_state vpncontainer_home vpncontainer_state >/dev/null 2>&1
  rm -rf "$T"
}
trap cleanup EXIT

git archive "$OLD_REF" | tar -x -C "$W" || exit 1
[[ -f $W/gtvpn ]] || { echo "$OLD_REF has no ./gtvpn"; exit 1; }
# For the update: the old version's files, and this checkout's
git ls-tree -r -z --name-only "$OLD_REF" > "$T/old.list" && git ls-files -z | xargs -0 tar -c --no-recursion -f "$T/new.tar" || exit 1
echo "building the images of $OLD_REF and of this checkout"
B() { docker build -q --network "$BNET" "${XARGS[@]}" "$@" >/dev/null; }
B --build-arg BASE_IMAGE="$BASE" --build-arg INSTALL_CLAUDE_CODE=0 -t gtvpn:local "$W/image" \
  && B -t lab/gtvpn-client -f "$W/tests/lab/Dockerfile.client" "$W/tests/lab" \
  && B --build-arg BASE_IMAGE="$BASE" --build-arg INSTALL_CLAUDE_CODE=0 -t vpncontainer:local image \
  && B -t lab/vpncontainer-client -f tests/lab/Dockerfile.client tests/lab \
  && B --build-arg BASE_IMAGE="$BASE" -t lab/ocserv tests/lab/ocserv || { echo "build failed"; exit 1; }

export HOME=$T/home   # the old and the new ~/.ssh live here
cd "$W" || exit 1
openssl req -x509 -newkey rsa:2048 -nodes -keyout tests/lab/fakeapi/k.pem -out tests/lab/fakeapi/c.pem \
  -days 2 -subj /CN=api.fake-anthropic.test >/dev/null 2>&1; chmod 644 tests/lab/fakeapi/*.pem
CF=compose.yaml; [[ ${LAB_NO_IPV6:-0} == 1 ]] && CF+=:tests/lab/compose.nov6.yaml
export COMPOSE_FILE=$CF:tests/lab/compose.lab.yaml   # the same names before and after
unset COMPOSE_PROJECT_NAME
pin() { for _ in $(seq 20); do docker logs "$1" 2>&1 | grep -q '^LABPIN' && break; sleep 0.5; done
  docker logs "$1" 2>&1 | awk '/^LABPIN/{print $2}'; }
tun() { docker exec "$1" ip link show tun0 >/dev/null 2>&1; }
cid() { docker container inspect -f '{{.Id}}' "$1" 2>/dev/null; }
S=$HOME/.ssh/config

# ---- 1. Before the rename ---------------------------------------------------------
check "old: gtvpn setup" bash -c './gtvpn setup >/dev/null 2>&1'
docker compose up -d ocserv fakeapi >/dev/null 2>&1
export LAB_PIN; LAB_PIN=$(pin gtvpn-lab-ocserv)
check "old: gtvpn up" bash -c './gtvpn up --no-build >/dev/null 2>&1'
check "old: a tunnel is up" docker exec gtvpn lab-connect
docker exec -u dev gtvpn sh -c 'mkdir -p ~/projects && echo kept > ~/projects/marker'
check "old: ssh gtvpn works" ssh -F "$S" -o BatchMode=yes gtvpn true
printf 'username=gtuser\n' > gtvpn-login.conf; touch -d '2 days ago' gtvpn-login.conf; touch -d '1 day ago' gtvpn-login.conf.refused
REF_T=$(stat -c %Y gtvpn-login.conf.refused)
KEY=$(cut -d' ' -f2 runtime/out/ssh_host_ed25519_key.pub)
OLD_ID=$(cid gtvpn)

# ---- 2. The update (git pull) ----------------------------------------------------------
xargs -0 rm -f < "$T/old.list"; tar -x -f "$T/new.tar" -C "$W"
[[ -f vpncontainer && ! -e gtvpn ]] || { bad "the update"; exit 1; }

# ---- 3. The old container, still connected -----------------------------------------------
OUT=$(./vpncontainer status 2>&1)
check "vpncontainer status works with the old container, and shows its tunnel" grep -q '^  up: ' <<<"$OUT"
check ".env: the GTVPN_ settings are now VPNCONTAINER_ ones (the old file kept)" \
  bash -c 'grep -q "^VPNCONTAINER_SSH_PORT=" .env && ! grep -q "^GTVPN_" .env && grep -q "^GTVPN_SSH_PORT=" .env.before-rename'
check "gtvpn-login.conf is now vpncontainer-login.conf" bash -c '[[ -f vpncontainer-login.conf && ! -e gtvpn-login.conf ]] && grep -qx username=gtuser vpncontainer-login.conf'
check "...and its .refused mark keeps its time (still newer than the file)" \
  bash -c "[[ \$(stat -c %Y vpncontainer-login.conf.refused) == $REF_T && vpncontainer-login.conf.refused -nt vpncontainer-login.conf ]]"
# shellcheck disable=SC2088  # a description
check "~/.ssh/gtvpn is now ~/.ssh/vpncontainer, and ~/.ssh/config includes it" \
  bash -c "[[ -d $HOME/.ssh/vpncontainer && ! -e $HOME/.ssh/gtvpn ]] && grep -qx 'Include $HOME/.ssh/vpncontainer/config' '$S' && ! grep -q '/.ssh/gtvpn' '$S'"
check "ssh vpncontainer reaches the old container (key and pinned host key moved)" \
  ssh -F "$S" -o BatchMode=yes vpncontainer 'test "$GTVPN" = 1'
check "vpncontainer verify passes against the old container (container + host side)" bash -c './vpncontainer verify >/dev/null 2>&1'
check "vpncontainer hosts hands the hosts to the old container" \
  bash -c './vpncontainer hosts >/dev/null 2>&1 && docker exec -u dev gtvpn ssh -G linlab19 | grep -qx "hostname ece-linlab19.ece.gatech.edu"'
check "vpncontainer connect says it is already connected" \
  bash -c "script -qec './vpncontainer connect' /dev/null </dev/null 2>&1 | grep -q 'already connected'"
OUT=$(./vpncontainer up --no-build 2>&1); RC=$?
check "vpncontainer up refuses while the old tunnel is up" bash -c "(( $RC != 0 )) && grep -q 'has its VPN tunnel up' <<<\"\$1\"" _ "$OUT"
check "...and leaves the old container and its tunnel as they were" bash -c "[[ \$(docker container inspect -f '{{.Id}}' gtvpn) == $OLD_ID ]] && docker exec gtvpn ip link show tun0 >/dev/null 2>&1"
check "no vpncontainer container was made" bash -c '! docker container inspect vpncontainer >/dev/null 2>&1'

# ---- 4. Disconnected: the switch ------------------------------------------------------------
check "vpncontainer disconnect stops the old tunnel" bash -c './vpncontainer disconnect >/dev/null 2>&1 && ! docker exec gtvpn ip link show tun0 >/dev/null 2>&1'
# (the old lab gateway holds the old network and the lab's addresses)
docker rm -f "${LABC[@]/#/gtvpn-lab-}" >/dev/null 2>&1
unset LAB_PIN
OUT=$(./vpncontainer up --no-build 2>&1); RC=$?
check "vpncontainer up now replaces the old container" bash -c "(( $RC == 0 )) && grep -q 'replacing the gtvpn container' <<<\"\$1\"" _ "$OUT"
(( RC == 0 )) || printf '%s\n' "$OUT" | tail -20
check "...the old container and network are gone, its volumes kept" \
  bash -c '! docker container inspect gtvpn >/dev/null 2>&1 && ! docker network inspect gtvpn_gtvpn_net >/dev/null 2>&1 && docker volume inspect gtvpn_home gtvpn_state >/dev/null'
check "the new container has the files from the old home" bash -c '[[ $(docker exec -u dev vpncontainer cat /home/dev/projects/marker) == kept ]]'
check "...and the same ssh host key" bash -c "[[ \$(cut -d' ' -f2 runtime/out/ssh_host_ed25519_key.pub) == '$KEY' ]] && docker exec vpncontainer grep -qF '$KEY' /var/lib/vpncontainer/ssh/ssh_host_ed25519_key.pub"
check "the new volumes are docker compose's own (no warning on up)" \
  bash -c "[[ \$(docker volume inspect -f '{{index .Labels \"com.docker.compose.project\"}}' vpncontainer_home) == vpncontainer ]] && ! grep -qi 'already exists' <<<\"\$1\"" _ "$OUT"
check "ssh vpncontainer reaches the new container, strict host key as before" \
  ssh -F "$S" -o BatchMode=yes vpncontainer 'test "$VPNCONTAINER" = 1'
check "a tunnel comes up in the new container" docker exec -e LAB_PIN="$(pin vpncontainer-lab-ocserv)" vpncontainer lab-connect
check "vpncontainer status shows it" bash -c "./vpncontainer status 2>&1 | grep -q '^  up: '"
NEW_ID=$(cid vpncontainer)
OUT=$(./vpncontainer up --no-build 2>&1)
check "a second up copies nothing and keeps the container and tunnel" \
  bash -c "! grep -q copying <<<\"\$1\" && [[ \$(docker container inspect -f '{{.Id}}' vpncontainer) == $NEW_ID ]] && docker exec vpncontainer ip link show tun0 >/dev/null 2>&1" _ "$OUT"
check "nothing renamed twice: the .env copy is still the old one" grep -q '^GTVPN_SSH_PORT=' .env.before-rename
exit $FAILED
