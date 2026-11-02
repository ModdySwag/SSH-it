# Jump-host lab

A throwaway set of sshds inside WSL Ubuntu that makes the awkward parts of a jump chain testable from
Windows. It exists because the transport questions are "does our argv survive a hop", "does an
interactive second factor reach the pane", and "does the chain really go through the hops" — none of
which a single local sshd can answer.

    tools/jump-lab/setup-lab.sh <path-to-your-public-key>

then point sessions at the addresses below.

## What it builds

| Listen | Role | Auth |
| --- | --- | --- |
| `127.0.0.1:2224` | jump host with a second factor | `publickey,keyboard-interactive` + TOTP (PAM) |
| `127.0.0.1:2226` | plain jump host | `publickey` |
| `10.99.0.3:2227` | second hop, WSL-only | `publickey` |
| `10.99.0.2:2225` | target, WSL-only | `publickey`; `AllowTcpForwarding no`, `AllowAgentForwarding yes` |

All four run as root from `/tmp/sshit-lab/` with their own host keys, configs, pid files and logs; the
script is idempotent (it kills and restarts them). Nothing touches the system sshd.

Two addresses are added to `lo` inside WSL: `10.99.0.2` and `10.99.0.3`. Windows cannot route to them,
so a connection that succeeds on `10.99.0.2:2225` **can only** have gone through a hop. That is the
whole point of the lab.

The second factor is real TOTP: `libpam-google-authenticator` with `PAMServiceName sshd-jump` and a
`/etc/pam.d/sshd-jump` that has no password fallback. The secret is written straight into
`~/.google_authenticator` (the CLI insists on a tty for its "enter code from app" step). Codes come
from `totp.py`, which is the same RFC 6238 arithmetic the token app does.

## Sessions worth having

    lab-jump-key     10.99.0.2:2225, key file, jump demo@127.0.0.1:2226
    lab-chain2       10.99.0.2:2225, key file, jump demo@127.0.0.1:2226,demo@10.99.0.3:2227
    lab-mfa-via-jump 10.99.0.2:2225, key file, jump demo@127.0.0.1:2224
    lab-mfa-direct   127.0.0.1:2224, key file (a second factor with no hop)
    lab-agent-jump   10.99.0.2:2225, no key file, jump demo@127.0.0.1:2226, ForwardAgent on

Set `StrictHostKeyChecking` off (accept-new) for a headless run: strict checking has nowhere to ask.

## What it proved (2026-11-02)

    --exec lab-jump-key "echo JUMP-KEY-OK; hostname"     -> JUMP-KEY-OK / build-host
    --exec lab-chain2   "echo CHAIN2-OK"                 -> CHAIN2-OK
    --tmux-test ssh lab-jump-key                         -> 6/6 PASS
    GUI: prompt `demo@127.0.0.1) Verification code:`, code typed in the pane, shell opened

## Traps this lab exposed, so keep them in mind

- `-J` does not pass `-i` to the hop. OpenSSH builds the hop's own command line with `-l -p -v -W`
  only (`ssh -vvv` prints it), so a key given as a file never reaches the hop. Sessions with a key
  file must spell the chain as nested ProxyCommands; the app does that now.
- ssh expands `%h:%p` across the whole ProxyCommand string, nested strings included. Only the
  outermost stage may carry the token; inner stages must name the next hop literally.
- A hop written `user@host:port` is not a legal ssh destination; split it into `-p` and `-l`.
- A stale `known_hosts` entry from an earlier, mis-nested chain makes the next run fail with
  "REMOTE HOST IDENTIFICATION HAS CHANGED" — that error can be the proof that the old chain was
  skipping a hop, not a real key change.
- `pkill -f google-authenticator` matches the shell running the command and kills it. Use
  `pkill -x google-authent`.
- The Windows OpenSSH agent service must be running for `-A` to forward anything, and a user-space
  named-pipe agent is not enough: `ssh-add -l` does not even connect to one.

## Tear-down

    wsl -d Ubuntu -u root -- bash -c 'pkill -f "/tmp/sshit-lab/"; rm -rf /tmp/sshit-lab'
    wsl -d Ubuntu -u root -- bash -c 'rm -f /etc/pam.d/sshd-jump /home/demo/.google_authenticator'
    wsl -d Ubuntu -u root -- bash -c 'ip addr del 10.99.0.2/32 dev lo; ip addr del 10.99.0.3/32 dev lo'

and remove the lab keys from `~/.ssh/authorized_keys` and the four host entries from the Windows
`known_hosts`.
