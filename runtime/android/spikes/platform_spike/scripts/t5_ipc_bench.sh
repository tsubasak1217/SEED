#!/usr/bin/env bash
# ============================================================
#  t5: メインプロセス ↔ :seed_platform の呼び出しの往復時間（各 10 回の中央値）
#   1 回目は両プロセスを落としてから（最初の ContentResolver.call が :seed_platform の起動を含む＝冷えた呼び出し）。
#   2・3 回目は温まった状態で繰り返し、ばらつきを見る。画面を使わないので利用者の操作は待たない。
# ============================================================
set -u
source "$(dirname "$0")/common.sh"
OUT="$RESULTS/05_ipc"
mkdir -p "$OUT"
RES="$OUT/result.env"
: > "$RES"

for run in 1 2 3; do
  if [ "$run" = 1 ]; then
    dsh "am kill $PKG"
    sleep 2
    log "run1 前: main=$(pid_main) platform=$(pid_platform)（両方空なら冷えた状態）"
    put "$RES" run1_pid_main_before "$(pid_main)"
    put "$RES" run1_pid_platform_before "$(pid_platform)"
  fi
  since="$(dev_since)"
  ctl IPC_BENCH > /dev/null
  if ! wait_mark "$since" "MARK bench_end" 60; then
    log "run$run: bench_end が来ない"
  fi
  sleep 1
  adbs logcat -d -v epoch -s "$TAG" -T "$since" | tr -d '\r' > "$OUT/run${run}_logcat.txt"
  grep -E "BENCH " "$OUT/run${run}_logcat.txt" | sed -E 's/.*BENCH /BENCH /' > "$OUT/run${run}_bench.txt"
  log "run$run:"; cat "$OUT/run${run}_bench.txt"
  for name in resolver_call_ping resolver_call_invoke256 provider_client_ping provider_client_invoke256 aidl_invoke256 messenger_roundtrip broadcast_back_oneway broadcast_back_roundtrip; do
    put "$RES" "run${run}_${name}_median_us" "$(grep -E "^BENCH $name " "$OUT/run${run}_bench.txt" | grep -oE 'median_us=[0-9.]+' | sed 's/median_us=//')"
  done
  put "$RES" "run${run}_cold_first_call_us" "$(grep -E '^BENCH cold_resolver_call ' "$OUT/run${run}_bench.txt" | grep -oE 'first_us=[0-9]+' | sed 's/first_us=//')"
  put "$RES" "run${run}_aidl_bind_us" "$(grep -E '^BENCH aidl_bind ' "$OUT/run${run}_bench.txt" | grep -oE 'bind_us=[0-9]+' | sed 's/bind_us=//')"
  put "$RES" "run${run}_messenger_bind_us" "$(grep -E '^BENCH messenger_bind ' "$OUT/run${run}_bench.txt" | grep -oE 'bind_us=[0-9]+' | sed 's/bind_us=//')"
  sleep 2
done
cat "$RES"
