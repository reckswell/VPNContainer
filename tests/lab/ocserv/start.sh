#!/bin/bash
# MODE=split|full
set -eu
mkdir -p /etc/ocserv/certs; cd /etc/ocserv/certs
if [ ! -f server-key.pem ]; then
  certtool --generate-privkey --outfile server-key.pem 2>/dev/null
  printf 'cn = "lab-gw"\nexpiration_days = 30\nsigning_key\nencryption_key\ntls_www_server\n' > tmpl
  certtool --generate-self-signed --load-privkey server-key.pem --template tmpl --outfile server-cert.pem 2>/dev/null
fi
echo "student" | ocpasswd -c /etc/ocserv/passwd gtuser 2>/dev/null || printf 'student\nstudent\n' | ocpasswd -c /etc/ocserv/passwd gtuser
ROUTES="route = 10.66.0.0/255.255.255.0
route = 198.18.0.0/255.255.255.0"
[ "${MODE:-split}" = full ] && ROUTES="route = default"
cat > /etc/ocserv/ocserv.conf <<CONF
auth = "plain[passwd=/etc/ocserv/passwd]"
tcp-port = 443
run-as-user = nobody
run-as-group = nogroup
socket-file = /run/ocserv.socket
server-cert = /etc/ocserv/certs/server-cert.pem
server-key = /etc/ocserv/certs/server-key.pem
isolate-workers = false
max-clients = 4
keepalive = 32400
dpd = 90
try-mtu-discovery = false
device = vpns
ipv4-network = 10.66.0.0
ipv4-netmask = 255.255.255.0
dns = 10.66.0.1
$ROUTES
cisco-client-compat = true
CONF
# "GT internal" services, reachable only through the tunnel:
#   10.66.0.1:2200  stand-in for an ssh host
#   DNS on 10.66.0.1 answering *.gt.test and gatech.edu
ip addr add 10.66.0.1/32 dev lo
ip addr add 198.18.0.10/32 dev lo
dnsmasq --no-resolv --listen-address=10.66.0.1 --bind-interfaces --no-hosts \
  --address=/internal.gt.test/10.66.0.1 --address=/gatech.edu/198.18.0.10 --address=/#/198.18.0.10 &
( while true; do echo "SSH-2.0-LabFake" | nc -l -N 10.66.0.1 2200 >/dev/null 2>&1; done ) &
( while true; do echo "SSH-2.0-LabFake" | nc -l -N 198.18.0.10 22 >/dev/null 2>&1; done ) &
# A "GT Linux server" with sshd + X11 forwarding (stand-in for the Cadence host)
id gtuser >/dev/null 2>&1 || useradd -m -s /bin/bash gtuser
usermod -p '*' gtuser
# Many GT accounts log in to tcsh
id gtcsh >/dev/null 2>&1 || useradd -m -s /usr/bin/tcsh gtcsh
usermod -p '*' gtcsh
mkdir -p /run/sshd /etc/ssh/lab_keys; ssh-keygen -A >/dev/null
cat > /etc/ssh/sshd_lab.conf <<SSHD
ListenAddress 10.66.0.1
Port 22
HostKey /etc/ssh/ssh_host_ed25519_key
AuthorizedKeysFile /etc/ssh/lab_keys/%u
PasswordAuthentication no
UsePAM no
X11Forwarding yes
X11UseLocalhost yes
XAuthLocation /usr/bin/xauth
SSHD
/usr/sbin/sshd -f /etc/ssh/sshd_lab.conf
# A service on the gateway's WAN address that must NOT be reachable directly.
( while true; do echo "public" | nc -l -N 0.0.0.0 8080 >/dev/null 2>&1; done ) &
certtool -i --infile server-cert.pem | grep -o 'pin-sha256:[A-Za-z0-9+/=]*' | head -1 > /run/pin
echo "LABPIN $(cat /run/pin)"
exec ocserv -f -c /etc/ocserv/ocserv.conf
