#!/usr/bin/env bash
# Drives the real host CLI (./gtvpn) end to end against the lab:
# setup -> up -> connect (SSO hand-off via the URL handler path) -> verify.
# The fake portal only implements prelogin, so the run ends when gpclient
# tries to use the (fake) cookie; everything before that is real.
# Run as a NORMAL user with docker access (not root).
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1
[[ $(id -u) != 0 ]] || { echo "run as a non-root user with docker access"; exit 1; }
export COMPOSE_PROJECT_NAME=gtvpn-lab
CF="compose.yaml"; [[ ${LAB_NO_IPV6:-0} == 1 ]] && CF+=":tests/lab/compose.nov6.yaml"
export COMPOSE_FILE="$CF:tests/lab/compose.lab.yaml:tests/lab/compose.sso.yaml"
FAILED=0; ok(){ printf '\033[32mPASS\033[0m %s\n' "$*"; }; bad(){ printf '\033[31mFAIL\033[0m %s\n' "$*"; FAILED=1; }
check(){ local d=$1; shift; if "$@"; then ok "$d"; else bad "$d"; fi; }
LOG=$(mktemp)
trap 'docker compose down -v --remove-orphans >/dev/null 2>&1; rm -f "$LOG"' EXIT

[[ -s tests/lab/fakeportal/c.pem ]] || openssl req -x509 -newkey rsa:2048 -nodes -keyout tests/lab/fakeportal/k.pem \
  -out tests/lab/fakeportal/c.pem -days 7 -subj /CN=fakeportal.lab.test >/dev/null 2>&1
chmod 644 tests/lab/fakeportal/*.pem
[[ -s tests/lab/fakeapi/c.pem ]] || openssl req -x509 -newkey rsa:2048 -nodes -keyout tests/lab/fakeapi/k.pem \
  -out tests/lab/fakeapi/c.pem -days 7 -subj /CN=api.fake-anthropic.test >/dev/null 2>&1
chmod 644 tests/lab/fakeapi/*.pem

check "gtvpn setup" ./gtvpn setup
check ".env has this user's UID" grep -qx "HOST_UID=$(id -u)" .env
check "ssh config includes the gtvpn block" grep -q "Include $HOME/.ssh/gtvpn/config" "$HOME/.ssh/config"
docker compose up -d ocserv fakeapi fakeportal >/dev/null 2>&1; sleep 3
export LAB_PIN; LAB_PIN=$(docker logs gtvpn-lab-ocserv 2>&1 | awk '/^LABPIN/{print $2}')
check "gtvpn up (container sealed, self-test passed)" ./gtvpn up --no-build
check "host key pinned for ssh" test -s "$HOME/.ssh/gtvpn/known_hosts"
check "ssh gtvpn works with the generated config (strict host key)" ssh -F "$HOME/.ssh/config" -o BatchMode=yes gtvpn 'test "$GTVPN" = 1'
check "gtvpn status" ./gtvpn status
check "gtvpn verify (pre: container + host)" ./gtvpn verify

# Root inside the container writes into runtime/out, which dev can also
# write. A planted symlink must not redirect root's write.
rm -f runtime/out/verify.txt
docker exec -u dev gtvpn sh -c 'ln -s /etc/gtvpn/sshd_config /run/gtvpn/out/verify.txt'
SUM=$(docker exec gtvpn sha256sum /etc/gtvpn/sshd_config)
./gtvpn verify >/dev/null 2>&1
check "a symlink planted in runtime/out does not redirect root's writes" \
  bash -c "[[ -n '$SUM' && \"\$(docker exec gtvpn sha256sum /etc/gtvpn/sshd_config)\" == '$SUM' ]] && [[ -f runtime/out/verify.txt && ! -L runtime/out/verify.txt ]]"

# The host opens only safe sign-in addresses that the container publishes.
opens() { # login.url content -> exit 0 if the host would open it
  printf '%s\n' "$1" > runtime/out/login.url
  bash -c 'source ./gtvpn; open_url() { echo "OPEN $1"; }; open_signin' 2>/dev/null | grep -q '^OPEN '
}
check "host opens an https sign-in address" opens 'https://sso.gatech.edu/idp/profile/SAML2/Redirect/SSO?SAMLRequest=fZ%2B&RelayState=x'
refuses() { ! opens "$1"; }
check "host refuses a Windows program path" refuses 'C:\Windows\System32\calc.exe'
check "host refuses a file: URL" refuses 'file:///etc/passwd'
check "host refuses a non-https scheme" refuses 'ms-settings:privacy'
check "host refuses an address with quotes" refuses "https://x.example/a'\"b"
ln -sf /etc/hostname runtime/out/login.html
check "host refuses a symlinked login.html" refuses 'file:login.html'
rm -f runtime/out/login.url runtime/out/login.html

# connect: needs a TTY; the browser part is simulated by the URL handler call
script -qefc "./gtvpn connect" "$LOG" >/dev/null 2>&1 &
CPID=$!
for _ in $(seq 60); do [[ -s runtime/out/login.url ]] && break; sleep 0.5; done
check "connect published the SSO URL for the host browser" grep -q '^https://idp.example.edu/sso' runtime/out/login.url
B64=$(printf '<html><!-- <saml-auth-status>1</saml-auth-status><prelogin-cookie>PRECOOKIE</prelogin-cookie><saml-username>gtuser</saml-username><saml-slo>no</saml-slo> --></html>' | base64 -w0)
check "URL handler rejects a non-callback link" bash -c "! ./gtvpn callback 'https://evil.example/x' 2>/dev/null"
check "URL handler delivers the globalprotectcallback link" ./gtvpn callback "globalprotectcallback:$B64"
wait $CPID 2>/dev/null
OUT=$(sed 's/\x1b\[[0-9;]*m//g' "$LOG")
check "container received the sign-in from the handler" grep -q "received sign-in from the browser handler" <<<"$OUT"
check "gpauth accepted the SAML result and gpclient was started" grep -q "signed in; starting tunnel" <<<"$OUT"
check "sign-in temp files and login URL were cleaned up" bash -c "! ls runtime/out/login.* >/dev/null 2>&1 && ! docker exec gtvpn sh -c 'ls -d /run/gtvpn/auth.* 2>/dev/null' | grep -q ."
check "after the failed (fake) tunnel the container is still sealed" ./gtvpn verify --pre
exit $FAILED
