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
jumps() { ssh -G -F "$HOME/.ssh/config" "$1" 2>/dev/null </dev/null | grep -qx 'proxyjump gtvpn'; }
check "a GT host typed by name goes through the container (VS Code Remote-SSH)" jumps ece-linlab19.ece.gatech.edu
check "a GT address goes through the container" jumps 143.215.155.65
no_jumps() { ! jumps example.com && ! jumps 10.1.2.3 && ! jumps gtvpn; }
check "non-GT hosts, 10.x addresses and gtvpn itself are left alone" no_jumps
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

# Interactive shells say when the tunnel is off (else: "Could not resolve hostname").
SH=$(docker exec -u dev gtvpn bash -ic '__gtvpn_prompt; echo "chroot=$debian_chroot"; ssh -o BatchMode=yes -o ConnectTimeout=3 x.gatech.edu true' 2>&1)
check "shell prompt shows (GT:off) while the tunnel is down" grep -q 'chroot=GT:off' <<<"$SH"
check "shell and ssh print the 'connect first' hint while the tunnel is down" \
  bash -c "[[ \$(grep -c 'GT tunnel is off' <<<\"\$1\") -ge 2 ]]" _ "$SH"
check "non-interactive ssh commands get no hint (VS Code, scripts)" \
  bash -c "! ssh -F '$HOME/.ssh/config' -o BatchMode=yes gtvpn 'true' 2>&1 | grep -q 'GT tunnel'"

# VS Code Dev Containers attach (gtvpn code): through docker exec, as dev.
LBL=$(docker inspect -f '{{index .Config.Labels "devcontainer.metadata"}}' gtvpn)
check "image tells Dev Containers to log in as dev" \
  python3 -c 'import json,sys; assert json.loads(sys.argv[1])[0]["remoteUser"]=="dev"' "$LBL"
VSC=$(mktemp -d); mkdir -p "$VSC/bin" "$VSC/cfg/Code/User"
printf '#!/bin/sh\nprintf "%%s\\n" "$@" > "%s/args"\n' "$VSC" > "$VSC/bin/code"; chmod +x "$VSC/bin/code"
check "gtvpn code runs VS Code with a Dev Containers attach URI" \
  env PATH="$VSC/bin:$PATH" XDG_CONFIG_HOME="$VSC/cfg" ./gtvpn code
URI=$(sed -n 2p "$VSC/args" 2>/dev/null)
check "the URI names the gtvpn container and opens ~/projects" python3 - "$URI" <<'PY'
import sys, json
u = sys.argv[1]; pre = "vscode-remote://attached-container+"
assert u.startswith(pre), u
hexpart, path = u[len(pre):].split("/", 1)
assert json.loads(bytes.fromhex(hexpart)) == {"containerName": "/gtvpn"}
assert "/" + path == "/home/dev/projects", path
PY
check "gtvpn code installed the attach config (remoteUser dev)" python3 -c 'import json,sys; assert json.load(open(sys.argv[1]))["remoteUser"]=="dev"' \
  "$VSC/cfg/Code/User/globalStorage/ms-vscode-remote.remote-containers/imageConfigs/gtvpn%3alocal.json"
check "gtvpn code refuses a path that is not a plain absolute path" \
  bash -c "! env PATH='$VSC/bin:$PATH' XDG_CONFIG_HOME='$VSC/cfg' ./gtvpn code '/x;y' 2>/dev/null"
rm -rf "$VSC"
# What a Dev Containers terminal is: docker exec -u dev, login shell env probe.
ENVP=$(docker exec -u dev gtvpn bash -lic 'env' 2>/dev/null)
check "attached VS Code gets the session env (DISPLAY, GTVPN, Claude Code proxy)" \
  bash -c "grep -q '^DISPLAY=' <<<\"\$1\" && grep -qx 'GTVPN=1' <<<\"\$1\" && grep -q '^HTTPS_PROXY=http://127.0.0.1:3128' <<<\"\$1\"" _ "$ENVP"
check "attached VS Code processes are sealed too (no internet without the tunnel)" \
  bash -c "! docker exec -u dev gtvpn curl -sS -m 5 --noproxy '*' -o /dev/null https://1.1.1.1 2>/dev/null"
check "container terminals drop VS Code's forwarded ssh-agent" bash -c \
  "[[ \$(docker exec -u dev -e SSH_AUTH_SOCK=/tmp/vscode-ssh-auth-x.sock gtvpn bash -ic 'echo \"s=\${SSH_AUTH_SOCK:-none}\"' 2>/dev/null | tail -1) == s=none ]]"
check "...but keep an agent you started in the container" bash -c \
  "[[ \$(docker exec -u dev -e SSH_AUTH_SOCK=/tmp/ssh-x/agent.1 gtvpn bash -ic 'echo \"s=\${SSH_AUTH_SOCK:-none}\"' 2>/dev/null | tail -1) == s=/tmp/ssh-x/agent.1 ]]"

# hosts.conf: one standard ssh config for the container and for VS Code.
sed -i 's/^GTVPN_GT_USER=.*/GTVPN_GT_USER=gtdefault/' .env
cat > hosts.conf <<'HOSTS'
Host labbox
    HostName labhost.gatech.edu
    User someone
    Port 2200
Host labip
    HostName 10.66.0.1
Host *.example
    User nobody
HOSTS
check "gtvpn hosts accepts a standard ssh config" ./gtvpn hosts
G=$HOME/.ssh/gtvpn/config
# (capture first: `cmd | grep -q` can SIGPIPE cmd, and pipefail calls that a failure)
sshG() { local o; o=$(ssh -G -F "$G" "$1" 2>/dev/null); grep -qx "$2" <<<"$o"; }
check "VS Code entry gt-labbox: HostName" sshG gt-labbox 'hostname labhost.gatech.edu'
check "VS Code entry gt-labbox: User" sshG gt-labbox 'user someone'
check "VS Code entry gt-labbox: Port" sshG gt-labbox 'port 2200'
check "VS Code entry gt-labbox: jumps through the container" sshG gt-labbox 'proxyjump gtvpn'
check "VS Code entry gt-labip gets the GT username from .env" sshG gt-labip 'user gtdefault'
check "wildcard Host lines don't become VS Code entries" bash -c "! grep -q 'gt-\*' '$G'"
inG() { local o; o=$(docker exec -u dev gtvpn ssh -G "$1" 2>/dev/null); grep -qx "$2" <<<"$o"; }
check "container: ssh labbox goes to the right host" inG labbox 'hostname labhost.gatech.edu'
check "container: ssh labbox has X11 on" inG labbox 'forwardx11 yes'
check "container: ssh labbox shares one connection (one Duo prompt)" inG labbox 'controlmaster auto'
check "container: an alias with an IP HostName gets X11 too" inG labip 'forwardx11 yes'
check "container: alias without User gets the GT username" inG labip 'user gtdefault'
check "container's copy of hosts.conf is root-owned, not writable by dev" \
  bash -c "[[ \$(docker exec gtvpn stat -c '%U %a' /etc/gtvpn/user-hosts.conf) == 'root 644' ]]"
cp hosts.conf hosts.conf.good
printf 'Host broken\n    NotAnOption yes\n' >> hosts.conf
check "gtvpn hosts refuses a hosts.conf with errors" bash -c "! ./gtvpn hosts >/dev/null 2>&1"
check "after the refusal the VS Code entries are unchanged" sshG gt-labbox 'hostname labhost.gatech.edu'
printf 'Host broken\n    NotAnOption yes\n' > runtime/in/hosts.conf
check "the container also refuses a broken hosts.conf by itself" bash -c "! docker exec gtvpn gtvpn-hosts >/dev/null 2>&1"
check "after the refusal the container keeps the previous hosts" inG labbox 'hostname labhost.gatech.edu'
mv hosts.conf.good hosts.conf; ./gtvpn hosts >/dev/null

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

# Sign-in prompt labels (gtvpn-prompts), fed what OpenConnect writes to stderr.
P=$(printf "Enter login credentials\nUsername: Password: Choose a secondary factor from ('push', 'phone') or enter passcode:\nChallenge: " \
    | docker exec -i -u root gtvpn gtvpn-prompts 2>&1)
check "sign-in labels the GT username prompt" grep -q 'GT username: ' <<<"$P"
check "sign-in labels the password prompt [PASSWORD]" grep -q '\[PASSWORD\].*your GT password' <<<"$P"
check "sign-in labels the Duo prompt [DUO CODE], not a password" grep -q '\[DUO CODE\].*Duo Mobile.*not your password' <<<"$P"
check "no prompt keeps OpenConnect's bare label" bash -c '! grep -Eq "(^|: )(Password|Challenge): " <<<"$1"' _ "$(sed 's/\x1b\[[0-9;]*m//g' <<<"$P")"
P=$(printf 'Please select GlobalProtect gateway.\n[2026-01-01 00:00:00] Got HTTP response: HTTP/1.1 512 status code 512\n[2026-01-01 00:00:00] Unexpected 512 result from server\nEnter login credentials\nPassword: ' \
    | docker exec -i -u root gtvpn gtvpn-prompts 2>&1)
check "a gateway refusal stops the sign-in" grep -q 'gateway refused the sign-in' <<<"$P"
check "after a gateway refusal no second password prompt is shown" bash -c '! grep -q "PASSWORD\|Password:" <<<"$1"' _ "$P"
P=$(printf 'Unexpected 512 result from server\nPassword: ' | docker exec -i -u root gtvpn gtvpn-prompts 2>&1)
check "a portal refusal explains it and asks once more" bash -c 'grep -q "GT refused that" <<<"$1" && grep -q "\[PASSWORD\]" <<<"$1"' _ "$P"

# gtvpn-login.conf (saved sign-in): read as data, never run, and handed to
# the container only on stdin. engine() is replaced here, so nothing is sent.
export LCT; LCT=$(mktemp -d)
printf '# password=not-this-one\r\n  Username = gtuser \r\npassword_dpapi=\r\npassword=p@ss w$rd`touch %s/ran` ;x=y \r\n' "$LCT" > gtvpn-login.conf
chmod 644 gtvpn-login.conf
printf 'gtuser\np@ss w$rd`touch %s/ran` ;x=y\n' "$LCT" > "$LCT/want"
stash_test() { # [rc of the container's can-stash check]
  rm -f "$LCT/sent"
  CAN=${1:-0} bash -c 'source ./gtvpn; engine() { [[ "$*" == *"gtvpn-connect --stash"* ]] && { cat >"$LCT/sent"; return 0; }
    [[ "$*" == *"sh -c"* ]] && return "$CAN"; [[ "$*" == *"ip link show"* ]] && return 1; true; }; stash_signin' 2>&1
}
O=$(stash_test)
check "login config: the saved sign-in is read as written (comments, spaces, CRLF) and never run" \
  bash -c 'cmp -s "$LCT/sent" "$LCT/want" && [[ ! -e $LCT/ran ]]'
check "login config: a file holding a password is made readable only by you" bash -c '[[ $(stat -c %a gtvpn-login.conf) == 600 ]]'
check "login config: .gitignore keeps it and its refusal marker out of git; the example has every field empty" bash -c \
  'grep -qx gtvpn-login.conf .gitignore && grep -qx gtvpn-login.conf.refused .gitignore && [[ -f gtvpn-login.example.conf ]] && ! grep -Eq "^[[:space:]]*(username|password|password_dpapi)[[:space:]]*=[[:space:]]*[^[:space:]]" gtvpn-login.example.conf'
touch -d '-1 minute' gtvpn-login.conf; touch gtvpn-login.conf.refused
O=$(stash_test)
check "login config: after GT refused the saved password it is not sent again" \
  bash -c '[[ ! -e $LCT/sent ]] && grep -q "GT refused your saved password" <<<"$1"' _ "$O"
touch -d '-1 minute' gtvpn-login.conf.refused; touch gtvpn-login.conf
O=$(stash_test)
check "login config: once you change the file it is used again" bash -c 'cmp -s "$LCT/sent" "$LCT/want"'
printf 'password=x\n' > gtvpn-login.conf
O=$(stash_test)
check "login config: a password without a username is not sent" \
  bash -c '[[ ! -e $LCT/sent ]] && grep -q "no valid username" <<<"$1"' _ "$O"
printf 'username=gtuser\npassword_dpapi=AQAAANCMnd8\n' > gtvpn-login.conf
O=$(stash_test)
check "login config: an encrypted password off Windows is not sent, and says why" \
  bash -c '[[ ! -e $LCT/sent ]] && grep -q "only be read on the Windows PC" <<<"$1"' _ "$O"
printf 'username=gtuser\npassword=x\n' > gtvpn-login.conf; rm -f gtvpn-login.conf.refused
O=$(stash_test 1)
check "login config: a container from before the saved sign-in never gets the password" \
  bash -c '[[ ! -e $LCT/sent ]] && grep -q "container is older than the saved sign-in" <<<"$1"' _ "$O"
conn_test() { # exit code of gtvpn-connect; [rc of can-stash]; [rc of the tunnel check]
  CRC=$1 CAN=${2:-0} TUN=${3:-1} bash -c 'source ./gtvpn; running() { true; }; cmd_host_verify() { :; }; cmd_up() { echo CMD_UP; }
    engine() { [[ "$*" == *"gtvpn-connect --stash"* ]] && { cat >/dev/null; return 0; }; [[ "$*" == *"sh -c"* ]] && return "$CAN"
      [[ "$*" == *"ip link show"* ]] && return "$TUN"; [[ "$*" == *gtvpn-connect* ]] && return "$CRC"; true; }
    cmd_connect' 2>&1
}
O=$(conn_test 0 1 1)
check "connect: an old container is updated first when no tunnel is up" grep -q '^CMD_UP' <<<"$O"
O=$(conn_test 0 1 0)
check "connect: an old container is left alone while the tunnel is up" bash -c '! grep -q "^CMD_UP" <<<"$1"' _ "$O"
O=$(conn_test 3)
check "connect: when GT refuses the saved password it says so and remembers it" \
  bash -c '[[ -e gtvpn-login.conf.refused ]] && grep -q "GT refused the saved password" <<<"$1"' _ "$O"
O=$(conn_test 0)
check "connect: a good sign-in clears that" bash -c '[[ ! -e gtvpn-login.conf.refused ]]'
rm -rf gtvpn-login.conf gtvpn-login.conf.refused "$LCT"

check "connect refuses GTVPN_SKIP_PORTAL=1 without a gateway" bash -c \
  "docker exec -u root -e GTVPN_SKIP_PORTAL=1 -e GTVPN_GATEWAY= gtvpn gtvpn-connect 2>&1 | grep -q 'needs GTVPN_GATEWAY'"

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
check "connect kept a root-only transcript of the sign-in terminal" bash -c \
  "[[ \$(docker exec gtvpn stat -c '%a %U' /var/log/gtvpn/connect.log) == '600 root' ]] && docker exec gtvpn grep -q 'starting tunnel' /var/log/gtvpn/connect.log"
check "gtvpn logs shows the transcript" bash -c "./gtvpn logs 2>&1 | grep -q -- '--- connect.log'"
check "gpclient accepted its log file (0600; it refuses one others can read)" bash -c \
  "! grep -q 'must not be readable' <<<\"\$1\" && [[ \$(docker exec gtvpn stat -c %a /var/log/gtvpn/gpclient.log) == 600 ]]" _ "$OUT"
check "sign-in temp files and login URL were cleaned up" bash -c "! ls runtime/out/login.* >/dev/null 2>&1 && ! docker exec gtvpn sh -c 'ls -d /run/gtvpn/auth.* 2>/dev/null' | grep -q ."
check "after the failed (fake) tunnel the container is still sealed" ./gtvpn verify --pre
exit $FAILED
