# MECHWARE: putting the shop on one server (production runbook)

For the owner. Follow the steps in order and copy the commands exactly. Each step ends with a check: do not go on until
the check passes. A one-line Bangla summary is in brackets under each heading.

Files this guide uses: `infra/docker/docker-compose.prod.yml` (the whole system), `infra/docker/prod.env.example`
(the settings to fill), `infra/docker/Caddyfile` (HTTPS), `infra/scripts/backup.sh`, `infra/scripts/restore.sh` and the
two `infra/scripts/mechware-backup.*` files (nightly backup).

Commands in `grey boxes` are typed into the server's terminal unless the step says "on your laptop".
`<IP>` means your server's IP address and `<domain>` your shop's domain (for example `mechware.com.bd`). Type them
without the `< >`.

## Before you start: what you need

- The domain, and access to its DNS settings (at the company where you bought it).
- An e-mail provider for that domain that offers SMTP (Zoho Mail, Google Workspace, Brevo or Amazon SES). You need its
  SMTP server name, a user name and an app password.
- A password manager (Bitwarden, 1Password or KeePassXC). Every secret in this guide goes there.
- An authenticator app on your phone (Google Authenticator, Microsoft Authenticator or Aegis).
- An account for off-site backups, for example Backblaze B2 (cheap; pay per GB). Needed in step 14.
- About 3 hours for the first time.

> **Domain note:** the API currently allows browser calls only from `mechware.com.bd` and `console.mechware.com.bd`.
> If your domain is different, ask the developer for that one-line change BEFORE step 11.

---

## 1. Buy the server
(সার্ভার কিনুন: ৮ জিবি র‍্যাম, উবুন্টু ২৪.০৪, সিঙ্গাপুর বা ঢাকা।)

1. At Contabo or Alpha Net, order a **KVM VPS** with **8 GB RAM**, at least **4 CPU cores** and **100 GB SSD/NVMe**.
2. Operating system: **Ubuntu 24.04 LTS**. Region: **Singapore** or **Dhaka**.
   Ask your lawyer which one: buyers' personal data stored in Singapore leaves Bangladesh (PDPO cross-border rules).
3. If the provider offers it, paste your SSH public key at order time (made in step 2) and turn on their snapshot or
   backup option too (extra safety; it does not replace step 14).
4. Write down the server's **IP address** and the root password in your password manager.

Check: the provider's panel shows the server as "running".

## 2. Make an SSH key on your laptop
(নিজের ল্যাপটপে একটি SSH চাবি তৈরি করুন।)

On your laptop (Windows: open **PowerShell**; Mac: open **Terminal**):

```
ssh-keygen -t ed25519 -C "mechware-owner"
```

Press Enter to accept the file name, then type a passphrase (save it in the password manager). This makes two files:
`id_ed25519` (private: never share it) and `id_ed25519.pub` (public: safe to give to the server).

Check: `type $env:USERPROFILE\.ssh\id_ed25519.pub` (Windows) or `cat ~/.ssh/id_ed25519.pub` (Mac) prints one line
starting with `ssh-ed25519`.

## 3. Point the domain at the server
(ডোমেইনের DNS রেকর্ড সার্ভারের IP-তে দিন।)

In your domain's DNS settings add four **A records**, each with value `<IP>`:

| Name (host) | Type | Value |
|---|---|---|
| `@` (the domain itself) | A | `<IP>` |
| `www` | A | `<IP>` |
| `console` | A | `<IP>` |
| `api` | A | `<IP>` |

- If you use Cloudflare, set these four to **DNS only** (grey cloud), not proxied.
- Also add the SPF, DKIM and DMARC records your e-mail provider shows you, or shop e-mails land in spam.

Check (on your laptop, after 5 to 60 minutes): `nslookup console.<domain>` shows `<IP>`. Do the same for the other three.

## 4. First login, updates, your own user
(সার্ভারে ঢুকে আপডেট করুন, নতুন ইউজার বানান, root লগইন বন্ধ করুন।)

On your laptop:

```
ssh root@<IP>
```

On the server (as root):

```
apt update && apt -y full-upgrade
timedatectl set-timezone Asia/Dhaka
adduser mechware                      # choose a strong password, save it
usermod -aG sudo mechware
```

Give the new user your SSH key. On your **laptop** (Windows PowerShell, one line):

```
type $env:USERPROFILE\.ssh\id_ed25519.pub | ssh mechware@<IP> "mkdir -p ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys"
```

(Mac: `ssh-copy-id mechware@<IP>`.) Now open a **new** window and test: `ssh mechware@<IP>` must log in without
the server password (only your key's passphrase).

Only when that works, switch off root and password logins. On the server:

```
sudo tee /etc/ssh/sshd_config.d/99-mechware.conf > /dev/null <<'EOF'
PermitRootLogin no
PasswordAuthentication no
KbdInteractiveAuthentication no
EOF
sudo systemctl restart ssh
```

Check: keep your current window open, open another one, `ssh mechware@<IP>` still works and `ssh root@<IP>` is refused.
From now on always log in as `mechware`.

## 5. Firewall: only 22, 80 and 443
(ফায়ারওয়াল চালু করুন: শুধু ২২, ৮০, ৪৪৩ খোলা।)

```
sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw allow 22/tcp
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw allow 443/udp
sudo ufw enable                       # answer y
```

Note: Docker opens the ports of its containers itself, past ufw. That is why the system publishes only Caddy's 80 and
443; never add a `ports:` line to another service.

Check: `sudo ufw status` shows exactly 22, 80, 443 (tcp) and 443/udp, each "ALLOW".

## 6. Block password guessers, automatic security updates, swap
(fail2ban, স্বয়ংক্রিয় নিরাপত্তা আপডেট ও swap চালু করুন।)

```
sudo apt -y install fail2ban unattended-upgrades
sudo systemctl enable --now fail2ban
sudo dpkg-reconfigure -plow unattended-upgrades        # answer Yes
sudo tee /etc/apt/apt.conf.d/52mechware-reboot > /dev/null <<'EOF'
Unattended-Upgrade::Automatic-Reboot "true";
Unattended-Upgrade::Automatic-Reboot-Time "04:30";
EOF
```

The shop restarts by itself after such a reboot (about 3 minutes; the virus scanner is last).

A 2 GB swap file protects against running out of memory:

```
sudo fallocate -l 2G /swapfile && sudo chmod 600 /swapfile && sudo mkswap /swapfile && sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
```

Check: `sudo fail2ban-client status sshd` shows the jail; `free -h` shows Swap 2.0Gi.

## 7. Install Docker
(Docker ইনস্টল করুন।)

```
sudo apt-get install -y ca-certificates curl git
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
sudo usermod -aG docker mechware
```

Log out (`exit`) and log in again so the last line takes effect. (Being in the `docker` group is as powerful as root:
keep this account's key safe.)

Check: `docker run --rm hello-world` prints "Hello from Docker!" and `docker compose version` prints v2.x.

## 8. Download the shop's code
(প্রজেক্টের কোড সার্ভারে ডাউনলোড (clone) করুন।)

The repository is private, so the server gets its own read-only key:

```
ssh-keygen -t ed25519 -C "mechware-server" -f ~/.ssh/github_deploy -N ""
cat ~/.ssh/github_deploy.pub
printf 'Host github.com\n  IdentityFile ~/.ssh/github_deploy\n' >> ~/.ssh/config
```

On GitHub: repository, **Settings, Deploy keys, Add deploy key**, paste the printed line, leave "Allow write access"
**off**. Then (the developer gives you the exact repository address):

```
sudo mkdir -p /opt/mechware && sudo chown mechware:mechware /opt/mechware
git clone git@github.com:<account>/<repository>.git /opt/mechware
cd /opt/mechware && git checkout main
```

Make a short name for the long Docker command (used in every later step):

```
echo "alias mw='docker compose -f /opt/mechware/infra/docker/docker-compose.prod.yml --env-file /opt/mechware/infra/docker/prod.env'" >> ~/.bashrc
source ~/.bashrc
```

Check: `ls /opt/mechware/infra/docker` lists `docker-compose.prod.yml`, `prod.env.example` and `Caddyfile`.

## 9. Fill in the settings (prod.env)
(সেটিংস ফাইল prod.env কপি করে পূরণ করুন; গোপন চাবিগুলো তৈরি করুন।)

Copy the example and let the server make the six secrets (random, never typed by a person):

```
cd /opt/mechware/infra/docker
cp prod.env.example prod.env && chmod 600 prod.env
for k in POSTGRES_PASSWORD REDIS_PASSWORD MEILI_MASTER_KEY; do sed -i "s|^$k=CHANGE_ME\$|$k=$(openssl rand -hex 32)|" prod.env; done
sed -i "s|^JWT_SECRET=CHANGE_ME\$|JWT_SECRET=$(openssl rand -base64 48)|" prod.env
for k in ENCRYPTION_FIELD_KEY GUEST_CART_TOKEN_KEY; do sed -i "s|^$k=CHANGE_ME\$|$k=$(openssl rand -base64 32)|" prod.env; done
openssl rand 20 | base32               # your admin's two-factor secret: copy it for BOOTSTRAP_ADMIN_TOTP_SECRET
grep -n '^[A-Z_]*=.*CHANGE_ME' prod.env   # what is left for you to type in
```

Open the file and fill the rest: `nano prod.env` (save with Ctrl+O, Enter; quit with Ctrl+X).

What **you** must fill in (each is explained in the file):

| Setting | What it is |
|---|---|
| `DOMAIN` | your domain, e.g. `mechware.com.bd` (no `https://`, no `www`) |
| `ACME_EMAIL` | a mailbox you read: certificate warnings |
| `EMAIL_HOST`, `EMAIL_USERNAME`, `EMAIL_PASSWORD` | from your e-mail provider (port 587) |
| `EMAIL_FROM_ADDRESS` | the sender buyers see, e.g. `noreply@<domain>` |
| `SALES_INBOX_EMAIL` | the mailbox that gets "RFQ received / accepted / rejected / expired" |
| `BOOTSTRAP_ADMIN_EMAIL`, `_FULL_NAME`, `_PASSWORD`, `_TOTP_SECRET` | your first admin account (step 12) |
| `INVOICING_MODE` | keep `Off` until the BIN and the facts below are known (decision O1) |
| `INVOICING_BIN`, `INVOICING_REGISTERED_ADDRESS_EN/_BN`, legal names, responsible person | for Mushak-6.3 invoices (`Live` only) |
| `BANK_*` | the bank block on invoices (from your `INVOICE.docx`) |
| `SIGNATORY_NAME`, `SIGNATORY_DESIGNATION` | who signs the invoice PDF |
| `BACKUP_RCLONE_REMOTE` | the off-site backup place (step 14) |

Rules: put names and addresses in single quotes (`INVOICING_REGISTERED_ADDRESS_EN='House 12, Road 3, Dhaka'`);
the admin password needs upper case, lower case, a digit and a symbol (use 16 or more characters).

Finally, save the **whole** `prod.env` in your password manager (as a secure note): `cat prod.env` and copy.
**`ENCRYPTION_FIELD_KEY` can never be changed or lost: without it no backup can be read.**

Check: `grep -c '^[A-Z_]*=.*CHANGE_ME' prod.env` prints `0`.

## 10. (Recommended) Encrypt the Data Protection keys
(ঐচ্ছিক: Data Protection সার্টিফিকেট তৈরি করুন।)

```
sudo mkdir -p /opt/mechware/infra/docker/secrets && sudo chown mechware:mechware /opt/mechware/infra/docker/secrets
cd /opt/mechware/infra/docker/secrets
openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -subj /CN=mechware-dp -keyout dp.key -out dp.crt
openssl pkcs12 -export -in dp.crt -inkey dp.key -out dp.pfx          # choose a password, save it
rm dp.key dp.crt
sudo chown 1654:1654 dp.pfx && sudo chmod 400 dp.pfx && chmod 711 .
```

In `prod.env` set `DATA_PROTECTION_CERT_PATH=/run/secrets/mechware/dp.pfx` and `DATA_PROTECTION_CERT_PASSWORD=` the
password. Save `dp.pfx` (download it with `scp`) and its password in the password manager.

Check (after step 11): `mw logs api | grep -i "stored unencrypted"` prints nothing.

## 11. First start
(প্রথমবার সিস্টেম চালু করুন।)

```
cd /opt/mechware
mw up -d --build
```

The first time this builds the four MECHWARE programs: 15 to 30 minutes. Then:

```
mw ps -a
```

Wait until (refresh every minute; the virus scanner needs about 3 minutes):

- `preflight` and `migrate`: **Exited (0)**
- `api`, `web`, `console`, `caddy`, `postgres`, `redis`, `meilisearch`, `clamav`: **Up ... (healthy)**
- `worker`: **Up**

If `preflight` shows Exited (1): `mw logs preflight` names the settings still on CHANGE_ME (step 9).
If `migrate` or `api` fails: `mw logs migrate` / `mw logs --tail 50 api` and send the output to the developer
(it never contains passwords).

Check: all containers as listed above.

## 12. Sign in as the first admin (two-factor)
(প্রথম অ্যাডমিন দিয়ে লগইন করুন, অথেনটিকেটর অ্যাপে কোড যোগ করুন।)

1. In the authenticator app: **Add account, Enter a setup key**. Name: `MECHWARE`. Key: the `BOOTSTRAP_ADMIN_TOTP_SECRET`
   from step 9. Type: **Time based**.
2. Open `https://console.<domain>` and sign in with `BOOTSTRAP_ADMIN_EMAIL`, the password and the 6-digit code.
3. When that works, empty the five `BOOTSTRAP_ADMIN_*` values in `prod.env` (`nano /opt/mechware/infra/docker/prod.env`)
   and apply: `mw up -d`. The account stays; only the copy of its password leaves the file.
4. Before taking orders, in the console: enter the shipping zones and tariffs (without them the shop refuses orders),
   create staff accounts with their own roles, and place one test order to see that the e-mails arrive.

Check: you are inside the console; `grep BOOTSTRAP_ADMIN_PASSWORD /opt/mechware/infra/docker/prod.env` shows an empty value.

## 13. Health checks
(ওয়েবসাইট ও হেলথ URL ঠিক আছে কিনা দেখুন।)

In a browser (each with a padlock, no warning):

- `https://<domain>`: the shop. `https://www.<domain>` jumps to it.
- `https://console.<domain>`: the staff console login.
- `https://api.<domain>/health/live`: shows `Healthy`.

On the server, the full check (database and Redis):

```
mw exec caddy wget -qO- http://api:8080/health/ready
```

shows `"status":"Healthy"`. (This address is deliberately not public.)

Tip: a free outside monitor (for example UptimeRobot) on `https://<domain>` and `https://api.<domain>/health/live`
e-mails you when the shop is down.

Check: all of the above.

## 14. Nightly backup and the off-site copy
(রাতের ব্যাকআপ টাইমার ও অফ-সাইট কপি চালু করুন।)

**a. Off-site place (do this before real orders).** A backup on the same disk dies with the server.
Example with Backblaze B2: create a bucket (private) and an application key for it, then:

```
sudo apt -y install rclone
sudo rclone config
```

Make two remotes: `b2` (type **b2**, your key id and key), then `b2crypt` (type **crypt**, remote `b2:<bucket-name>`,
choose a password and a salt password). Save `/root/.config/rclone/rclone.conf` and both passwords in the password
manager: without them the off-site copies cannot be read. In `prod.env` set `BACKUP_RCLONE_REMOTE=b2crypt:mechware`.

**b. Optional alarm:** create a free check at healthchecks.io (period 1 day) and put its ping URL in `BACKUP_PING_URL`.
It e-mails you when a night's backup did not happen.

**c. Turn on the nightly timer (02:30 every night):**

```
cd /opt/mechware
sudo cp infra/scripts/mechware-backup.service infra/scripts/mechware-backup.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now mechware-backup.timer
sudo systemctl start mechware-backup.service          # one backup right now
journalctl -u mechware-backup -n 20 --no-pager
```

Each night it saves the database and the uploaded documents into `/var/backups/mechware/daily/<date>`, keeps 14 daily
and 8 weekly copies, and uploads the new copy off-site. Without `BACKUP_RCLONE_REMOTE` it prints a
**WARNING: NO OFF-SITE COPY** every night.

Check: the journal ends with `backup: done` (and no WARNING once the remote is set); `systemctl list-timers mechware-backup.timer`
shows the next run; `sudo rclone ls b2crypt:mechware` lists files.

## 15. Restore drill (once a month)
(প্রতি মাসে একবার রিস্টোর মহড়া (drill) চালান।)

A backup is only proven when it has been restored. The drill restores the newest backup into a scratch database,
compares it with what the backup says it contains, then deletes the scratch copy. The live shop keeps running.

```
sudo bash /opt/mechware/infra/scripts/restore.sh drill
```

Check: the last lines say `DRILL PASSED` with the number of tables, user accounts and documents. Write the date and
the result in your notes. If it says FAILED, tell the developer the same day.

## 16. Updating to a new version
(নতুন ভার্সনে আপডেট করার নিয়ম।)

When the developer says a new version is ready (choose a quiet hour; the shop is offline for about a minute):

```
sudo bash /opt/mechware/infra/scripts/backup.sh          # fresh backup first
cd /opt/mechware && git pull
mw build                                            # 5 to 20 minutes; the shop keeps running meanwhile
mw up -d                                            # migrate runs first, then the new version starts
mw ps -a
```

Then repeat the checks of step 13. Once a month remove old program versions: `docker image prune -f`.

If the new version does not start: `mw logs --tail 100 api` to the developer. Going back is a developer decision
(`git checkout <previous version>`, `mw build`, `mw up -d`, and if the database was already changed, a full restore of
the backup made just before: step 17).

Check: step 13 passes on the new version.

## 17. Disaster: restore everything from a backup
(দুর্ঘটনার পর ব্যাকআপ থেকে পুরো সিস্টেম ফিরিয়ে আনা।)

Use this only when the data is lost or damaged. Everything written after the chosen backup is lost.

On a **new** server: do steps 1 to 8, then put back the **saved** `prod.env` from the password manager (same
`ENCRYPTION_FIELD_KEY`!) and, if used, `secrets/dp.pfx` and `/root/.config/rclone/rclone.conf`. On the old server,
skip this paragraph.

```
cd /opt/mechware
mw up -d postgres                                    # only the database
sudo rclone copy b2crypt:mechware/daily/<date> /var/backups/mechware/daily/<date>     # only when the copy is off-site
sudo bash infra/scripts/restore.sh list
sudo bash infra/scripts/restore.sh full /var/backups/mechware/daily/<date>
```

Type `RESTORE` when asked. The script first saves the current database to `/var/backups/mechware/pre-restore/`,
replaces the database and the documents, then starts everything. Afterwards rebuild the search index:

```
mw exec worker dotnet Mechware.Worker.dll rebuild-search
```

Check: step 13 passes, and recent orders up to the backup's date are visible in the console.

## 18. Everyday commands and problems
(প্রতিদিনের দরকারি কমান্ড ও সমস্যা সমাধান।)

| Need | Command |
|---|---|
| Is everything running? | `mw ps -a` |
| Last messages of a part | `mw logs --tail 100 api` (or `worker`, `web`, `console`, `caddy`, `postgres`) |
| Restart one part | `mw restart api` |
| Restart everything | `mw up -d` |
| Free disk space | `df -h /` (keep at least 20 % free) and `sudo du -sh /var/backups/mechware` |
| Last backup result | `journalctl -u mechware-backup -n 20 --no-pager` |
| Backups on the server | `sudo bash /opt/mechware/infra/scripts/restore.sh list` |

- **Site shows a certificate error:** DNS for that name does not point to `<IP>` yet (step 3), or ports 80/443 are
  closed (step 5). `mw logs caddy | tail -30` says which.
- **Shop e-mails do not arrive:** check the spam folder, the SPF/DKIM records (step 3) and `mw logs worker | grep -i mail`.
- **Never** run `mw down -v`: `-v` deletes the database and all documents.
- **Never** change `ENCRYPTION_FIELD_KEY`, `POSTGRES_PASSWORD` or `DOMAIN` in `prod.env` without the developer.
