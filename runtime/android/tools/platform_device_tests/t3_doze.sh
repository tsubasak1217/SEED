#!/usr/bin/env bash
# ============================================================
#  T3: Doze（dumpsys deviceidle force-idle）の下での発火の遅れと音の開始（AC-1 の後半・W1-0 の残り）
#    公式の手順（「Test with Doze」）どおり battery unplug → force-idle（IDLE を確かめる）→ LEAD 秒後（既定 300 秒）を予約 →
#    両プロセスを落とす → 30 秒ごとに Doze の状態を記録（利用者が使い始めたら取り消して中止）→ 発火 → 予定時刻との差・音の開始 →
#    鳴り始めから 6 秒で STOP_RINGING → deviceidle unforce と battery reset（失敗しても trap で必ず戻す）。
# ============================================================
set -u
source "$(dirname "$0")/common.sh"
ID="${1:-t3}"
LEAD_S="${2:-300}"
OUT="$RESULTS/${3:-t3_doze}"
mkdir -p "$OUT"
RES="$OUT/result.env"
: > "$RES"
POLL_S=30
PRE_FIRE_S=40
RING_WAIT_S=150
STOP_AFTER_RING_MS=6000

restore() {
  dsh "dumpsys deviceidle unforce" > "$OUT/restore_unforce.txt" 2>&1
  dsh "dumpsys battery reset" > "$OUT/restore_battery.txt" 2>&1
}
idle_state() { dsh "dumpsys deviceidle" | grep -E 'mState=|mLightState=|mForceIdle=' | tr -s ' \n' ' '; }

device_present || { put "$RES" skipped device_absent; exit 3; }
wait_user_idle || { put "$RES" skipped user_operating_or_absent; exit 1; }
locked_or_off || { put "$RES" skipped not_locked; log "画面が点いてロックが外れているので中止"; exit 1; }
# 前の試験の目覚ましが点けた画面を消す（充電中は自分では消えない端末。ロック中・利用者が使っていないときだけ）
kill_app_processes
put "$RES" screen_before_sleep_key "$(screen_state | tr ' ' ';')"
if sleep_screen_if_locked; then put "$RES" sleep_key "ok_or_already_off"; else put "$RES" sleep_key "not_applied"; fi
trap restore EXIT
since="$(dev_since)"
start_stream "$OUT/stream.txt"
put "$RES" screen_at_start "$(screen_state | tr ' ' ';')"
snap "$OUT" 0_before
dsh "dumpsys battery unplug" > "$OUT/battery_unplug.txt" 2>&1
dsh "dumpsys deviceidle force-idle" > "$OUT/force_idle_output.txt" 2>&1
put "$RES" force_idle_output "$(tr '\n' ' ' < "$OUT/force_idle_output.txt")"
put "$RES" idle_after_force "$(idle_state)"

schedule_quiet "$ID" "$LEAD_S" "W1-7_T3" > "$OUT/schedule_broadcast.txt"
wait_mark "$since" '\[debug\] alarm\.schedule' 30 || log "schedule の結果が来ない"
stream_since "$since" > "$OUT/.s.txt"
trigger_at="$(trigger_of "$OUT/.s.txt")"
if ! quiet_request_ok "$OUT/.s.txt"; then log "静かな引数が予約に入っていない → 取り消して中止"; put "$RES" aborted quiet_args_missing; cleanup_alarms > "$OUT/cleanup.txt"; stop_stream; exit 6; fi
put "$RES" schedule_line "$(grep -m1 -E '\[debug\] alarm\.schedule' "$OUT/.s.txt" | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//')"
put "$RES" trigger_at "$trigger_at"
[ -z "$trigger_at" ] && { log "予定時刻が取れない。中止"; cleanup_alarms > "$OUT/cleanup.txt"; stop_stream; exit 4; }
sleep 2
kill_app_processes
kill_done_ms="$(dev_now_ms)"
put "$RES" kill_done_ms "$kill_done_ms"
put "$RES" idle_after_schedule "$(idle_state)"
snap "$OUT" 1_scheduled
log "予約 $ID trigger_at=$trigger_at・$(idle_state)"

# 発火の 40 秒前まで 30 秒ごとに Doze の状態を記録（使い始めたら取り消して中止・外れたら何もしない）
: > "$OUT/idle_timeline.txt"
while :; do
  device_present || { put "$RES" aborted device_absent_before_fire; log "端末が外れた"; stop_stream; exit 3; }
  now_ms="$(dev_now_ms)"
  echo "$(date '+%T') device_ms=$now_ms to_fire_s=$(( (trigger_at - now_ms) / 1000 )) $(idle_state) wakefulness=$(wakefulness)" >> "$OUT/idle_timeline.txt"
  [ $(( trigger_at - now_ms )) -le $(( PRE_FIRE_S * 1000 )) ] && break
  if user_operating; then
    log "利用者が操作中（$(current_focus)）→ 予約を取り消して中止"
    cleanup_alarms > "$OUT/cleanup.txt"
    put "$RES" aborted "user_operating_before_fire:$(current_focus)"
    stop_stream; exit 2
  fi
  sleep "$POLL_S"
done
tail -1 "$OUT/idle_timeline.txt"
put "$RES" idle_before_fire "$(idle_state)"
put "$RES" screen_before_fire "$(screen_state | tr ' ' ';')"

if wait_mark "$since" "目覚まし $ID を鳴らし始めました" "$RING_WAIT_S"; then
  ring_ms="$(stream_since "$since" | grep -m1 -E "目覚まし $ID を鳴らし始めました" | awk '{ printf "%d\n", $1 * 1000 + 0.5 }')"
  put "$RES" idle_while_ringing "$(idle_state)"
  put "$RES" alarm_playing_while_ringing "$(alarm_playing)"
  put "$RES" alarm_volume_while_ringing "$(alarm_volume_line)"
  put "$RES" screen_while_ringing "$(screen_state | tr ' ' ';')"
  while [ $(( $(dev_now_ms) - ring_ms )) -lt "$STOP_AFTER_RING_MS" ]; do sleep 0.5; done
else
  log "鳴り始めが来ない"
  put "$RES" ring_start_missing true
fi
s_stop="$(dev_since)"
ctl STOP_RINGING --es id "$ID" > "$OUT/stop_broadcast.txt"
wait_mark "$s_stop" '\[debug\] alarm\.stop_ringing' 20 || log "stop_ringing の結果が来ない"
[ -n "${ring_ms:-}" ] && put "$RES" stop_after_ring_ms "$(( $(dev_now_ms) - ring_ms ))"
put "$RES" alarm_playing_after_stop "$(alarm_playing)"
cleanup_alarms > "$OUT/cleanup.txt"
restore
trap - EXIT
sleep 2
snap "$OUT" 3_after_restore
put "$RES" idle_after_restore "$(idle_state)"
put "$RES" battery_after_restore "$(dsh 'dumpsys battery' | grep -E 'AC powered|USB powered|status:' | tr -s ' \n' ';')"
verify_alarm_volume > "$OUT/alarm_volume_after.txt"; put "$RES" alarm_volume_after "$(tr '\n' ';' < "$OUT/alarm_volume_after.txt")"
kill_app_processes
stop_stream
F="$OUT/stream.txt"
put "$RES" wake_from_idle_lines "$(tr -d '\r' < "$F" | grep -E "device_idle_wake_from_idle|wake_from_idle.*$PKG" | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//' | head -5 | tr '\n' '|')"
bash "$(dirname "$0")/analyze_ring.sh" "$F" "$trigger_at" "$ID" "$kill_done_ms" >> "$RES"
tr -d '\r' < "$F" | grep -iE 'deviceidle|DeviceIdle|device_idle' | head -80 > "$OUT/deviceidle_log_excerpt.txt" || true
cat "$RES"
