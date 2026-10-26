#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
"$root/scripts/test.sh"
relay="$root/publish/edgestpan-relay"
(cd "$root/relay" && go build -o "$relay" ./cmd/relay)
"$relay" -http 127.0.0.1:8787 -udp 127.0.0.1:8788 >/tmp/edgestpan-relay.log 2>&1 &
pid=$!
trap 'kill "$pid" >/dev/null 2>&1 || true' EXIT
for _ in 1 2 3 4 5 6 7 8 9 10; do
  if curl -fsS http://127.0.0.1:8787/health >/dev/null; then
    break
  fi
  sleep 0.2
done
curl -fsS http://127.0.0.1:8787/health
echo
echo "Relay status: http://127.0.0.1:8787/"
