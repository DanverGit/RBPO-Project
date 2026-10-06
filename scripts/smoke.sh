#!/usr/bin/env bash
# Проверка работоспособности запущенного стенда (docker compose up) с настоящими токенами Keycloak.
# Использование: ./scripts/smoke.sh   (нужны bash и curl)
set -u

API=${API:-http://localhost:5080}
KC=${KC:-http://localhost:8080/realms/cinema}
failures=0

token() { # $1 — пользователь (пароль совпадает с именем), $2 — клиент
  curl -s -X POST "$KC/protocol/openid-connect/token" \
    -d grant_type=password -d client_id="${2:-cinema-client}" -d username="$1" -d password="$1" \
    | sed -n 's/.*"access_token":"\([^"]*\)".*/\1/p'
}

check() { # $1 — описание, $2 — ожидаемый код, $3 — фактический код
  if [ "$2" = "$3" ]; then echo "OK   $1 ($3)"; else echo "FAIL $1: ожидался $2, получен $3"; failures=$((failures + 1)); fi
}

code() { curl -s -o /dev/null -w '%{http_code}' "$@"; }

for _ in $(seq 1 30); do [ "$(code "$API/health")" = 200 ] && break; sleep 2; done
check "GET /health" 200 "$(code "$API/health")"

VIEWER1=$(token viewer1)
VIEWER2=$(token viewer2)
STAFF=$(token staff1)
OTHER=$(token viewer1 other-client)
[ -n "$VIEWER1" ] && [ -n "$VIEWER2" ] && [ -n "$STAFF" ] || { echo "FAIL не удалось получить токены Keycloak"; exit 1; }

SESSION=$(curl -s "$API/sessions" | sed -n 's/^\[{"id":\([0-9]*\).*/\1/p')
SEAT=$(curl -s "$API/sessions/$SESSION/seats" | grep -o '"seatId":[0-9]*,"row":[0-9]*,"number":[0-9]*,"state":"free"' | head -1 | sed 's/"seatId":\([0-9]*\).*/\1/')
echo "     сеанс $SESSION, место $SEAT"

RESPONSE=$(curl -s -w '\n%{http_code}' -X POST "$API/bookings" -H "Authorization: Bearer $VIEWER1" \
  -H 'Content-Type: application/json' -d "{\"sessionId\":$SESSION,\"seatIds\":[$SEAT]}")
check "viewer1 бронирует место" 201 "$(echo "$RESPONSE" | tail -1)"
BOOKING=$(echo "$RESPONSE" | head -1 | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')

check "viewer2 не видит чужое бронирование (D-01)" 404 "$(code "$API/bookings/$BOOKING" -H "Authorization: Bearer $VIEWER2")"
check "повторное бронирование того же места (D-02)" 409 "$(code -X POST "$API/bookings" -H "Authorization: Bearer $VIEWER2" \
  -H 'Content-Type: application/json' -d "{\"sessionId\":$SESSION,\"seatIds\":[$SEAT]}")"
check "зритель вызывает /staff (D-01)" 403 "$(code -X PATCH "$API/staff/sessions/$SESSION" -H "Authorization: Bearer $VIEWER2" \
  -H 'Content-Type: application/json' -d '{"cancelled":true}')"
check "токен другого клиента Keycloak (aud)" 401 "$(code "$API/bookings/my" -H "Authorization: Bearer $OTHER")"
check "запрос без токена" 401 "$(code "$API/bookings/my")"
check "сотрудник отменяет бронирование с причиной" 200 "$(code -X DELETE "$API/staff/bookings/$BOOKING" -H "Authorization: Bearer $STAFF" \
  -H 'Content-Type: application/json' -d '{"reason":"smoke check"}')"

if [ "$failures" -eq 0 ]; then echo "Все проверки пройдены"; else echo "Провалено проверок: $failures"; exit 1; fi
