#!/usr/bin/env bash
# ============================================================
#  T8: 鳴動中にエンジン（メインプロセス）が落ちても鳴り続ける（AC-9。W1-7）
#
#    t8_engine_kill.sh [予約 ID] [出力の名前]
#
#  AC-9 の「デバッグ命令で panic」の命令はエンジンに無いので、より強い形としてメインプロセスを run-as で kill -9 する
#  （panic はプロセスの abort になり、どちらも「エンジンのプロセスが無くなる」）。手順: MainActivity を起こす（エンジンが動く）→
#  静かな予約 → 鳴り始めて KILL_AFTER_S 秒後にメインプロセスを kill -9 → CHECK_AFTER_S 秒の間 :seed_platform と USAGE_ALARM の
#  再生が続くことを確かめる → STOP_RINGING（受信機が新しいメインプロセスを起こす）→ 止まる・音量が元へ戻る。
#  前提: ロック解除中（MainActivity を前面へ出す）。音は最小（schedule_quiet）。
# ============================================================
set -u
source "$(dirname "$0")/common.sh"
ID="${1:-t8}"
OUT="$RESULTS/${2:-t8_engine_kill}"
mkdir -p "$OUT"
RES="$OUT/result.env"
: > "$RES"
# 予約までの秒
LEAD_S=15
# 鳴り始めから kill までの秒
KILL_AFTER_S=3
# kill の後に鳴り続けるのを見る秒（2 回見る）
CHECK_AFTER_S=4
# エンジンの最初のフレームを待つ上限
ENGINE_WAIT_S=30

device_present || { put "$RES" skipped device_absent; exit 3; }
user_operating && { put "$RES" skipped user_operating; exit 1; }
[ "$(keyguard_showing)" = "true" ] && { put "$RES" skipped locked; exit 1; }
record_alarm_volume_baseline
start_stream "$OUT/stream.txt"
s_start="$(dev_since)"
dsh "am start -n $MAIN_ACTIVITY" > "$OUT/am_start.txt" 2>&1
wait_mark "$s_start" '\[SEED FRAME 0\] end' "$ENGINE_WAIT_S" && put "$RES" engine_first_frame true || put "$RES" engine_first_frame false
since="$(dev_since)"
schedule_quiet "$ID" "$LEAD_S" "W1-7_T8" > "$OUT/schedule_broadcast.txt"
wait_mark "$since" '\[debug\] alarm\.schedule' 30 || log "schedule の結果が来ない"
stream_since "$since" > "$OUT/.s.txt"
quiet_request_ok "$OUT/.s.txt" || { put "$RES" aborted quiet_args_missing; cleanup_alarms > "$OUT/cleanup.txt"; stop_stream; exit 6; }
if ! wait_mark "$since" "目覚まし $ID を鳴らし始めました" $((LEAD_S + 30)); then
  put "$RES" ring_missing true; cleanup_alarms > "$OUT/cleanup.txt"; stop_stream; exit 4
fi
sleep "$KILL_AFTER_S"
main_pid="$(pid_main)"
put "$RES" main_pid_before "$main_pid"
put "$RES" platform_pid_before "$(pid_platform)"
put "$RES" alarm_playing_before "$(alarm_playing)"
[ -n "$main_pid" ] && dsh "run-as $PKG kill -9 $main_pid" > /dev/null 2>&1
put "$RES" kill_device_ms "$(dev_now_ms)"
sleep "$CHECK_AFTER_S"
put "$RES" main_pid_after "$(pid_main)"
put "$RES" platform_pid_after "$(pid_platform)"
put "$RES" alarm_playing_after_1 "$(alarm_playing)"
sleep "$CHECK_AFTER_S"
put "$RES" alarm_playing_after_2 "$(alarm_playing)"
dsh "dumpsys activity services $PKG" > "$OUT/services_after_kill.txt"
s_stop="$(dev_since)"
ctl STOP_RINGING --es id "$ID" > "$OUT/stop_broadcast.txt" 2>&1
wait_mark "$s_stop" '\[debug\] alarm\.stop_ringing' 20 || log "stop_ringing の結果が来ない"
put "$RES" stop_line "$(stream_since "$s_stop" | grep -m1 -E '\[debug\] alarm\.stop_ringing' | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//' | cut -c1-200)"
sleep 2
put "$RES" alarm_playing_at_end "$(alarm_playing)"
put "$RES" volume_at_end "$(alarm_volume_speaker)"
verify_alarm_volume > "$OUT/alarm_volume_after.txt"; put "$RES" alarm_volume_check "$(tr '\n' ';' < "$OUT/alarm_volume_after.txt")"
cleanup_alarms > "$OUT/cleanup.txt"
kill_app_processes
stop_stream
cat "$RES"
