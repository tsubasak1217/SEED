#!/usr/bin/env bash
# ============================================================
#  T6: 別名 PlatformEntry 経由の onNewIntent（MainActivity が既に居るとき）
#    前提: MainActivity がロック画面の上に居る（t1_cold_lockscreen.sh の keep_activity の直後）。
#    (a) 本物の経路: SCHEDULE で LEAD 秒後（既定 12 秒）→ 発火 → フルスクリーン通知 → PlatformEntry → singleTask の既存の
#        MainActivity へ onNewIntent（「起動後に届いた Intent の理由: alarm」）。am stack list・dumpsys activity で、タスクも
#        ActivityRecord も増えない・pid が変わらないことを見る。鳴り始めから 5 秒で STOP_RINGING。
#    (b) adb の am start -n <app>/…PlatformEntry に起動理由の extra を付けて送る（exported=false なので拒否されるはず）。
# ============================================================
set -u
source "$(dirname "$0")/common.sh"
ID="${1:-t6}"
LEAD_S="${2:-12}"
OUT="$RESULTS/${3:-t6_platform_entry}"
mkdir -p "$OUT"
RES="$OUT/result.env"
: > "$RES"
RING_WAIT_S=45
STOP_AFTER_RING_MS=5000
EXTRA_LAUNCH="com.seedengine.runtime.platform.extra.LAUNCH"

device_present || { put "$RES" skipped device_absent; exit 3; }
wait_user_idle || { put "$RES" skipped user_operating_or_absent; exit 1; }
since="$(dev_since)"
start_stream "$OUT/stream.txt"
put "$RES" screen_at_start "$(screen_state | tr ' ' ';')"
put "$RES" pid_main_before "$(pid_main)"
dsh "am stack list" | grep -E "taskId=[0-9]+: $PKG/" > "$OUT/0_before_stack.txt" || true
dsh "dumpsys activity activities" | grep -E "ActivityRecord\{[^}]*$PKG" > "$OUT/0_before_activity_records.txt" || true
put "$RES" task_before "$(head -1 "$OUT/0_before_stack.txt" | sed -E 's/^ +//; s/ bounds=.*//' | tr ' ' ';')"
put "$RES" activity_records_before "$(grep -oE "ActivityRecord\{[0-9a-f]+ u0 $PKG/[^ ]+ t[0-9]+" "$OUT/0_before_activity_records.txt" | sort -u | tr '\n' ';')"
[ -z "$(pid_main)" ] && { put "$RES" skipped no_main_activity; log "MainActivity が居ない"; stop_stream; exit 4; }

# (a) 本物の経路
ctl SCHEDULE --ei seconds "$LEAD_S" --es id "$ID" --ei max_ring_minutes 1 --es title "W1-4b_T6" --es body "$ID" > "$OUT/schedule_broadcast.txt"
wait_mark "$since" '\[debug\] alarm\.schedule' 30 || log "schedule の結果が来ない"
stream_since "$since" > "$OUT/.s.txt"
trigger_at="$(trigger_of "$OUT/.s.txt")"
put "$RES" trigger_at "$trigger_at"
if wait_mark "$since" "目覚まし $ID を鳴らし始めました" "$RING_WAIT_S"; then
  ring_ms="$(stream_since "$since" | grep -m1 -E "目覚まし $ID を鳴らし始めました" | awk '{ printf "%d\n", $1 * 1000 + 0.5 }')"
  wait_mark "$since" '起動後に届いた Intent の理由' 4 || log "onNewIntent の印がまだ"
  put "$RES" screen_while_ringing "$(screen_state | tr ' ' ';')"
  put "$RES" pid_main_while_ringing "$(pid_main)"
  dsh "am stack list" | grep -E "taskId=[0-9]+: $PKG/" > "$OUT/1_ringing_stack.txt" || true
  dsh "dumpsys activity activities" | grep -E "ActivityRecord\{[^}]*$PKG" > "$OUT/1_ringing_activity_records.txt" || true
  while [ $(( $(dev_now_ms) - ring_ms )) -lt "$STOP_AFTER_RING_MS" ]; do sleep 0.5; done
else
  log "鳴り始めが来ない"
  put "$RES" ring_start_missing true
fi
s_stop="$(dev_since)"
ctl STOP_RINGING --es id "$ID" > "$OUT/stop_broadcast.txt"
wait_mark "$s_stop" '\[debug\] alarm\.stop_ringing' 20 || log "stop_ringing の結果が来ない"
[ -n "${ring_ms:-}" ] && put "$RES" stop_after_ring_ms "$(( $(dev_now_ms) - ring_ms ))"
put "$RES" task_while_ringing "$(head -1 "$OUT/1_ringing_stack.txt" 2>/dev/null | sed -E 's/^ +//; s/ bounds=.*//' | tr ' ' ';')"
put "$RES" tasks_while_ringing_count "$(grep -cE 'taskId=' "$OUT/1_ringing_stack.txt" 2>/dev/null)"
put "$RES" activity_records_while_ringing "$(grep -oE "ActivityRecord\{[0-9a-f]+ u0 $PKG/[^ ]+ t[0-9]+" "$OUT/1_ringing_activity_records.txt" 2>/dev/null | sort -u | tr '\n' ';')"

# (b) adb から別名を直接開けるか（exported=false）
json='{"kind":"alarm","id":"t6_adb","action_id":"","scheduled_at_utc_ms":1,"fired_at_utc_ms":1,"payload_json":""}'
dsh "am start -W -n $ENTRY_ALIAS --es $EXTRA_LAUNCH '$json'" > "$OUT/am_start_entry.txt" 2>&1
put "$RES" am_start_entry "$(tr '\n' ' ' < "$OUT/am_start_entry.txt" | cut -c1-400)"
sleep 2
cleanup_alarms > "$OUT/cleanup.txt"
dsh "am stack list" | grep -E "taskId=[0-9]+: $PKG/" > "$OUT/2_after_stack.txt" || true
put "$RES" pid_main_after "$(pid_main)"
stop_stream
F="$OUT/stream.txt"
put "$RES" new_intent_line "$(tr -d '\r' < "$F" | grep -E '起動後に届いた Intent の理由' | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//' | tr '\n' '|')"
put "$RES" launch_reason_line "$(tr -d '\r' < "$F" | grep -E 'SEEDPlatform: 起動理由: ' | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//' | tr '\n' '|')"
put "$RES" start_entry_lines "$(tr -d '\r' < "$F" | grep -E "START u0 .*PlatformEntry" | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//' | cut -c1-300 | tr '\n' '|')"
put "$RES" platform_launch_event "$(tr -d '\r' < "$F" | grep -E 'platform\.launch' | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//' | cut -c1-300 | tr '\n' '|')"
put "$RES" main_proc_start "$(tr -d '\r' < "$F" | grep -cE "Start proc [0-9]+:$PKG/u")"
put "$RES" displayed_lines "$(tr -d '\r' < "$F" | grep -E "Displayed $PKG/" | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//' | tr '\n' '|')"
put "$RES" security_lines "$(tr -d '\r' < "$F" | grep -E 'Permission Denial|not exported' | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//' | cut -c1-300 | tr '\n' '|')"
cat "$RES"
