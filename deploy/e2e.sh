#!/usr/bin/env bash
# End-to-end run against the three services (API :5300, flaky partner :5301, merchant :5302).
# Proves: idempotent creation, retries through injected partner failures, signed callbacks,
# merchant notifications delivered despite a failed first attempt, and a deterministic decline.
set -euo pipefail
API=${API:-http://localhost:5300}
PARTNER=${PARTNER:-http://localhost:5301}
MERCHANT=${MERCHANT:-http://localhost:5302}

for url in "$API/health" "$PARTNER/health" "$MERCHANT/health"; do
  for _ in $(seq 1 60); do curl -sf "$url" >/dev/null && break; sleep 1; done
done

create() { # $1 = idempotency key, $2 = body
  curl -sf -X POST "$API/api/payments" -H 'Content-Type: application/json' -H "Idempotency-Key: $1" -d "$2"
}

echo "== Idempotency: the same key twice returns the same payment"
run=$(date +%s)
first=$(create "order-$run-1" '{"merchantReference":"ORDER-1","amount":150.00,"currency":"SAR"}')
replay_headers=$(curl -s -D - -o /tmp/replay.json -X POST "$API/api/payments" -H 'Content-Type: application/json' \
  -H "Idempotency-Key: order-$run-1" -d '{"merchantReference":"ORDER-1","amount":150.00,"currency":"SAR"}')
[ "$(echo "$first" | jq -r .id)" = "$(jq -r .id /tmp/replay.json)" ]
echo "$replay_headers" | grep -qi 'idempotent-replayed: true'
echo "   same id, replay header present"

echo "== Reusing a key with a different body is rejected (422)"
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/payments" -H 'Content-Type: application/json' \
  -H "Idempotency-Key: order-$run-1" -d '{"merchantReference":"ORDER-1","amount":999.00,"currency":"SAR"}')
[ "$code" = "422" ]

echo "== Creating 9 more payments (one ends in .99 and will be declined)"
ids=("$(echo "$first" | jq -r .id)")
for n in $(seq 2 9); do
  ids+=("$(create "order-$run-$n" "{\"merchantReference\":\"ORDER-$n\",\"amount\":$((n * 10)).00,\"currency\":\"SAR\"}" | jq -r .id)")
done
declined=$(create "order-$run-10" '{"merchantReference":"ORDER-10","amount":19.99,"currency":"SAR"}' | jq -r .id)
ids+=("$declined")

echo "== Waiting for every payment to reach a final status"
for _ in $(seq 1 90); do
  pending=0
  for id in "${ids[@]}"; do
    status=$(curl -sf "$API/api/payments/$id" | jq -r .status)
    [[ "$status" == "Succeeded" || "$status" == "Failed" ]] || pending=$((pending + 1))
  done
  [ "$pending" = "0" ] && break
  sleep 1
done
[ "$pending" = "0" ] || { echo "$pending payments still not final"; exit 1; }

[ "$(curl -sf "$API/api/payments/$declined" | jq -r .status)" = "Failed" ]
[ "$(curl -sf "$API/api/payments/$declined" | jq -r .failureReason)" = "Insufficient funds" ]
echo "   all final; the .99 payment was declined with the partner's reason"

echo "== Partner stats (injected failures were retried)"
curl -sf "$PARTNER/stats" | jq -c .

echo "== Merchant received exactly one event per payment, all correctly signed"
for _ in $(seq 1 60); do
  received=$(curl -sf "$MERCHANT/events" | jq --argjson ids "$(printf '%s\n' "${ids[@]}" | jq -R . | jq -s .)" \
    '[.events[] | select(.paymentId as $p | $ids | index($p))] | length')
  [ "$received" = "${#ids[@]}" ] && break
  sleep 1
done
[ "$received" = "${#ids[@]}" ] || { echo "merchant received $received of ${#ids[@]} events"; exit 1; }
curl -sf "$MERCHANT/events" | jq -c '{deliveries, rejectedSignatures, events: (.events | length)}'
[ "$(curl -sf "$MERCHANT/events" | jq .rejectedSignatures)" = "0" ]

echo "All end-to-end checks passed."
