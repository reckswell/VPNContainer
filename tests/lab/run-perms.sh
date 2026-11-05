#!/usr/bin/env bash
# A checkout on a Windows drive (WSL /mnt/c) shows every file and folder as
# 0777. Build the image from such a copy and prove nothing root runs ends up
# writable by dev (the build itself also refuses, see image/Dockerfile).
#   LAB_BASE_IMAGE=... LAB_BUILD_NETWORK=host tests/lab/run-perms.sh
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1
BASE=${LAB_BASE_IMAGE:-ubuntu:24.04}; BNET=${LAB_BUILD_NETWORK:-default}
T=$(mktemp -d); trap 'rm -rf "$T"; docker rmi -f gtvpn:permtest >/dev/null 2>&1' EXIT
cp -r image "$T/"; chmod -R 0777 "$T/image"
FAILED=0
if docker build -q --network "$BNET" --build-arg BASE_IMAGE="$BASE" --build-arg INSTALL_CLAUDE_CODE=0 \
     -t gtvpn:permtest "$T/image" >/dev/null; then
  echo "PASS image builds from a 0777 checkout"
else echo "FAIL image does not build from a 0777 checkout"; exit 1; fi
out=$(docker run --rm --entrypoint sh gtvpn:permtest -c '
  stat -c "%a %U %n" / /etc /usr /usr/local /usr/local/sbin /usr/local/bin /etc/gtvpn /etc/ssh/ssh_config.d \
    /usr/local/sbin/gtvpn-firewall /usr/local/bin/gtvpn-verify /etc/gtvpn/sshd_config /tmp')
echo "$out" | sed 's/^/     /'
bad=$(echo "$out" | awk '$3!="/tmp" && ($2!="root" || substr($1,length($1)-1,1) ~ /[2367]/ || substr($1,length($1),1) ~ /[2367]/)')
tmp=$(echo "$out" | awk '$3=="/tmp"{print $1}')
if [[ -z $bad ]]; then echo "PASS no root-run path is group/world-writable"; else echo "FAIL writable: $bad"; FAILED=1; fi
if [[ $tmp == 1777 ]]; then echo "PASS /tmp kept its sticky 1777 mode"; else echo "FAIL /tmp is $tmp"; FAILED=1; fi
x=$(docker run --rm --entrypoint sh gtvpn:permtest -c 'test -x /usr/local/sbin/gtvpn-outwrite && test ! -x /etc/gtvpn/sshd_config && echo ok')
if [[ $x == ok ]]; then echo "PASS scripts executable, config files not"; else echo "FAIL exec bits wrong"; FAILED=1; fi
exit $FAILED
