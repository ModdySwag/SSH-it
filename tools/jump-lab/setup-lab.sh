#!/bin/bash
# SSH-it jump-host lab: two jump sshds (one with TOTP MFA, one key-only) and a target sshd that is
# only reachable from inside WSL. Run as root:  wsl -d Ubuntu -u root -- bash <this file>
set -uo pipefail

LAB=/tmp/sshit-lab
SCRATCH=/mnt/c/Users/Moddy/AppData/Local/hermes/profiles/deepseek/cache/scratch/sshit-jump-lab
# The Windows public key that may log in to every hop and the target.
PUB="${1:-$SCRATCH/sshit-lab_ed25519.pub}"
mkdir -p "$LAB"; chmod 755 "$LAB"

echo "== client key =="
if [ ! -f "$PUB" ]; then echo "usage: setup-lab.sh <path-to-public-key>   (MISSING $PUB)"; exit 1; fi
install -d -o moddy -g moddy -m 700 /home/demo/.ssh
grep -qF "$(cut -d' ' -f2 "$PUB")" /home/demo/.ssh/authorized_keys 2>/dev/null || cat "$PUB" >> /home/demo/.ssh/authorized_keys
chown moddy:moddy /home/demo/.ssh/authorized_keys; chmod 600 /home/demo/.ssh/authorized_keys
echo "authorized_keys lines: $(wc -l < /home/demo/.ssh/authorized_keys)"

echo "== host keys =="
for n in jumpmfa jumpplain jumpb target; do
  [ -f "$LAB/${n}_hostkey" ] || ssh-keygen -q -t ed25519 -N '' -f "$LAB/${n}_hostkey"
done
ls -1 "$LAB" | grep hostkey

echo "== WSL-only addresses on lo =="
ip addr show dev lo | grep -q '10\.99\.0\.2' || ip addr add 10.99.0.2/32 dev lo
ip addr show dev lo | grep -q '10\.99\.0\.3' || ip addr add 10.99.0.3/32 dev lo
ip -4 addr show dev lo | grep 10.99

echo "== pam_google_authenticator =="
if ! dpkg -s libpam-google-authenticator >/dev/null 2>&1; then
  DEBIAN_FRONTEND=noninteractive apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq libpam-google-authenticator
fi
dpkg -s libpam-google-authenticator | grep -E '^(Status|Version)'

cat > /etc/pam.d/sshd-jump <<'EOF'
# SSH-it jump-lab only: TOTP as the keyboard-interactive factor, no password fallback.
auth       required   pam_google_authenticator.so
account    include    common-account
session    include    common-session
password   include    common-password
EOF

echo "== totp secret for moddy =="
# The CLI insists on a tty ("Enter code from app"), so write the documented minimal file directly:
# line 1 is the base32 secret, the quoted option line turns TOTP on.
if [ ! -s /home/demo/.google_authenticator ]; then
  { printf '%s\n' "J2CK5XZMQMCTO5BNRRVJV4EYXRY44IWK"; printf '" TOTP_AUTH\n'; } > /home/demo/.google_authenticator
fi
chown moddy:moddy /home/demo/.google_authenticator 2>/dev/null
chmod 600 /home/demo/.google_authenticator 2>/dev/null
head -c 40 /home/demo/.google_authenticator | head -1
echo
stat -c '%U %a %n' /home/demo/.google_authenticator 2>/dev/null

echo "== configs =="
cat > "$LAB/jumpmfa.conf" <<EOF
Port 2224
ListenAddress 127.0.0.1
HostKey $LAB/jumpmfa_hostkey
PidFile $LAB/jumpmfa.pid
LogLevel VERBOSE
StrictModes no
AuthorizedKeysFile /home/demo/.ssh/authorized_keys
AllowUsers moddy
UseDNS no
PermitRootLogin no
UsePAM yes
PAMServiceName sshd-jump
KbdInteractiveAuthentication yes
PasswordAuthentication no
PubkeyAuthentication yes
AuthenticationMethods publickey,keyboard-interactive
AllowTcpForwarding yes
EOF

cat > "$LAB/jumpplain.conf" <<EOF
Port 2226
ListenAddress 127.0.0.1
HostKey $LAB/jumpplain_hostkey
PidFile $LAB/jumpplain.pid
LogLevel VERBOSE
StrictModes no
AuthorizedKeysFile /home/demo/.ssh/authorized_keys
AllowUsers moddy
UseDNS no
PermitRootLogin no
UsePAM no
PubkeyAuthentication yes
AuthenticationMethods publickey
AllowTcpForwarding yes
EOF

cat > "$LAB/jumpb.conf" <<EOF
Port 2227
ListenAddress 10.99.0.3
HostKey $LAB/jumpb_hostkey
PidFile $LAB/jumpb.pid
LogLevel VERBOSE
StrictModes no
AuthorizedKeysFile /home/demo/.ssh/authorized_keys
AllowUsers moddy
UseDNS no
PermitRootLogin no
UsePAM no
PubkeyAuthentication yes
AuthenticationMethods publickey
AllowTcpForwarding yes
EOF

cat > "$LAB/target.conf" <<EOF
Port 2225
ListenAddress 10.99.0.2
HostKey $LAB/target_hostkey
PidFile $LAB/target.pid
LogLevel VERBOSE
StrictModes no
AuthorizedKeysFile /home/demo/.ssh/authorized_keys
AllowUsers moddy
UseDNS no
PermitRootLogin no
UsePAM no
PubkeyAuthentication yes
AuthenticationMethods publickey
AllowTcpForwarding no
AllowAgentForwarding yes
EOF

echo "== start =="
for c in jumpmfa jumpplain jumpb target; do
  if [ -f "$LAB/$c.pid" ] && kill -0 "$(cat "$LAB/$c.pid")" 2>/dev/null; then
    kill "$(cat "$LAB/$c.pid")"; sleep 0.4
  fi
  /usr/sbin/sshd -f "$LAB/$c.conf" -E "$LAB/$c.log" && echo "started $c"
done
sleep 0.8
ss -ltnp 2>/dev/null | grep -E '2224|2225|2226' || echo "NOT LISTENING"
