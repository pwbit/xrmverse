# tds-certs (plan 3.3, ruling T12): copies the TLS pair of each organization's SQL (TDS) endpoint into ./tds/<org>/<host>.crt|.key,
# readable by the server (uid 1654) through group 1654. TDS_ORGS lists "<org>=<host>" pairs; TDS_PAIR is empty for Caddy's
# storage (the newest <issuer>/<host>/<host>.crt|.key), otherwise the name of a pair in /certs (<name>.crt|.key). Only regular
# files with a single link are read, and no symbolic link is ever followed (find -P); ./tds/<org> must be a folder owned by the
# owner of ./tds (the host user, who keeps owning the copies). Runs at start, then every TDS_INTERVAL seconds, or every 60 s
# while a pair is missing (an ACME certificate is issued a moment after the proxy starts).
set -u
caddy=/data/caddy/certificates
certs=/certs
out=/out
gid=1654

# Prints $1 when it is a regular file with a single link (a symbolic link is not followed).
regular() {
  find -P "$1" -maxdepth 0 -type f -links 1 2>/dev/null
}

# Prints "<crt> <key>" for the newest certificate of host $1 in Caddy's storage whose key is there too.
caddy_pair() {
  local mtime crt key
  find -P "$caddy" -mindepth 3 -maxdepth 3 -type f -links 1 -path "$caddy/*/$1/$1.crt" -printf '%T@ %p\n' 2>/dev/null | sort -rn |
    while read -r mtime crt; do
      key=${crt%.crt}.key
      if [ -n "$(regular "$key")" ]; then
        printf '%s %s\n' "$crt" "$key"
        break
      fi
    done
}

# Copies the pair of organization $1 (host $2) into $out/$1 when it changed; non-zero when it has none yet or cannot copy it.
sync_org() {
  local org=$1 host=$2 crt key dir owner ext src dest tmp pair
  if [ -n "${TDS_PAIR:-}" ]; then
    crt=$certs/$TDS_PAIR.crt
    key=$certs/$TDS_PAIR.key
    if [ -z "$(regular "$crt")" ] || [ -z "$(regular "$key")" ]; then
      echo "tds-certs: $org: $crt and $key must be regular files (not symbolic links); nothing copied"
      return 1
    fi
  else
    pair=$(caddy_pair "$host")
    if [ -z "$pair" ]; then
      echo "tds-certs: $org: Caddy holds no certificate for $host yet"
      return 1
    fi
    crt=${pair% *}
    key=${pair#* }
  fi
  dir=$out/$org
  owner=$(stat -c %u "$out")
  if [ -L "$dir" ] || [ ! -d "$dir" ] || [ "$(stat -c %u "$dir")" != "$owner" ]; then
    echo "tds-certs: $org: $dir is not a folder owned by uid $owner (xrmd server init|org add creates it); nothing copied"
    return 1
  fi
  # As secrets-init: find -P acts on the folder itself, never on what a symbolic link swapped in after the check points to.
  find -P "$dir" -maxdepth 0 -type d -user "$owner" -exec chgrp "$gid" {} + -exec chmod 0750 {} + || return 1
  for ext in crt key; do
    if [ "$ext" = crt ]; then src=$crt; else src=$key; fi
    dest=$dir/$host.$ext
    if [ -n "$(regular "$dest")" ] && cmp -s "$src" "$dest"; then continue; fi
    tmp=$dir/.$host.$ext.tmp
    if ! { rm -f "$tmp" && install -m 0640 -o "$owner" -g "$gid" "$src" "$tmp" && mv -fT "$tmp" "$dest"; }; then
      rm -f "$tmp"
      echo "tds-certs: $org: cannot write $dest"
      return 1
    fi
    echo "tds-certs: $org: copied $host.$ext"
  done
}

while :; do
  wait=${TDS_INTERVAL:-43200}
  for entry in ${TDS_ORGS:-}; do
    sync_org "${entry%%=*}" "${entry#*=}" || wait=60
  done
  if [ -n "${TDS_ONCE:-}" ]; then exit 0; fi
  sleep "$wait"
done
