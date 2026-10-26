#!/usr/bin/env bash
# Two hosts behind separate NAT gateways, with the relay on the WAN.
# Proves STUN mapped addresses, a direct ESPN punch, and the ciphertext relay.
set -euo pipefail
export PATH="/usr/sbin:/sbin:/bin:$PATH"
root="$(cd "$(dirname "$0")/.." && pwd)"
relay_bin="${RELAY_BIN:-$root/publish/relay/edgestpan-relay}"
check_bin="${CHECK_BIN:-$root/src/EdgeSpan.NatCheck/bin/Release/net8.0/EdgeSpan.NatCheck}"
nss=(wan nata natb hosta hostb)

cleanup() {
  set +e
  if [[ -n "${relay_pid:-}" ]]; then
    ip netns exec wan kill "$relay_pid" 2>/dev/null || true
  fi
  for ns in "${nss[@]}"; do
    ip netns del "$ns" 2>/dev/null || true
  done
  ip link del w-a 2>/dev/null || true
  ip link del w-b 2>/dev/null || true
  ip link del n-a 2>/dev/null || true
  ip link del n-b 2>/dev/null || true
}
trap cleanup EXIT

for ns in "${nss[@]}"; do
  ip netns del "$ns" 2>/dev/null || true
  ip netns add "$ns"
done

ip link add w-a type veth peer name a-w
ip link add w-b type veth peer name b-w
ip link add n-a type veth peer name h-a
ip link add n-b type veth peer name h-b
ip link set w-a netns wan
ip link set a-w netns nata
ip link set w-b netns wan
ip link set b-w netns natb
ip link set n-a netns nata
ip link set h-a netns hosta
ip link set n-b netns natb
ip link set h-b netns hostb

ip netns exec wan ip link add br-wan type bridge
ip netns exec wan ip link set br-wan up
ip netns exec wan ip addr add 10.88.0.1/24 dev br-wan
ip netns exec wan ip link set w-a up
ip netns exec wan ip link set w-a master br-wan
ip netns exec wan ip link set w-b up
ip netns exec wan ip link set w-b master br-wan
ip netns exec wan ip link set lo up

ip netns exec nata ip addr add 10.88.0.2/24 dev a-w
ip netns exec nata ip link set a-w up
ip netns exec nata ip addr add 10.88.1.1/24 dev n-a
ip netns exec nata ip link set n-a up
ip netns exec nata ip link set lo up
ip netns exec nata ip route add default via 10.88.0.1

ip netns exec natb ip addr add 10.88.0.3/24 dev b-w
ip netns exec natb ip link set b-w up
ip netns exec natb ip addr add 10.88.2.1/24 dev n-b
ip netns exec natb ip link set n-b up
ip netns exec natb ip link set lo up
ip netns exec natb ip route add default via 10.88.0.1

ip netns exec hosta ip addr add 10.88.1.2/24 dev h-a
ip netns exec hosta ip link set h-a up
ip netns exec hosta ip link set lo up
ip netns exec hosta ip route add default via 10.88.1.1

ip netns exec hostb ip addr add 10.88.2.2/24 dev h-b
ip netns exec hostb ip link set h-b up
ip netns exec hostb ip link set lo up
ip netns exec hostb ip route add default via 10.88.2.1

for ns in wan nata natb hosta hostb; do
  ip netns exec "$ns" sysctl -w net.ipv4.ip_forward=1 >/dev/null
  ip netns exec "$ns" sysctl -w net.ipv4.conf.all.rp_filter=0 >/dev/null
  ip netns exec "$ns" sysctl -w net.ipv4.conf.default.rp_filter=0 >/dev/null
done
# 1:1 NAT. The public address is the gateway, and the external port stays the
# port STUN reports, which is what a direct punch needs.
ip netns exec nata iptables -t nat -A POSTROUTING -s 10.88.1.2/32 -j NETMAP --to 10.88.0.2
ip netns exec nata iptables -t nat -A PREROUTING -d 10.88.0.2/32 -j NETMAP --to 10.88.1.2
ip netns exec natb iptables -t nat -A POSTROUTING -s 10.88.2.2/32 -j NETMAP --to 10.88.0.3
ip netns exec natb iptables -t nat -A PREROUTING -d 10.88.0.3/32 -j NETMAP --to 10.88.2.2
ip netns exec nata iptables -A FORWARD -j ACCEPT
ip netns exec natb iptables -A FORWARD -j ACCEPT

ip netns exec wan "$relay_bin" -http 0.0.0.0:8787 -udp 0.0.0.0:8788 >/tmp/edgestpan-relay-nat.log 2>&1 &
relay_pid=$!
sleep 0.4

host_log=$(mktemp)
phone_log=$(mktemp)
timeout 25 ip netns exec hosta "$check_bin" host --relay http://10.88.0.1:8787 --stun-host 10.88.0.1 --stun-port 8788 --expect-mapped 10.88.0.2 >"$host_log" 2>&1 &
host_pid=$!
sleep 0.6
timeout 25 ip netns exec hostb "$check_bin" phone --relay http://10.88.0.1:8787 --stun-host 10.88.0.1 --stun-port 8788 --expect-mapped 10.88.0.3 >"$phone_log" 2>&1 &
phone_pid=$!

host_rc=0
phone_rc=0
wait "$host_pid" || host_rc=$?
wait "$phone_pid" || phone_rc=$?
echo "----- host ($host_rc) -----"
cat "$host_log"
echo "----- phone ($phone_rc) -----"
cat "$phone_log"

grep -q "mapped 10.88.0.2:" "$host_log"
grep -q "mapped 10.88.0.3:" "$phone_log"
grep -q "direct-ok" "$host_log"
grep -q "direct-ok" "$phone_log"
grep -q "relay-ok" "$host_log"
grep -q "relay-ok" "$phone_log"
test "$host_rc" = 0
test "$phone_rc" = 0
echo "nat-punch-ok"
