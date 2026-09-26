#!/usr/bin/env bash
# ============================================================
#  t4: am force-stop の後に予約が残るか（残らないはず）と、次の起動（MainActivity）で張り直せるか
# ============================================================
set -u
source "$(dirname "$0")/common.sh"
OUT="$RESULTS/04_force_stop"
mkdir -p "$OUT"
RES="$OUT/result.env"
: > "$RES"

alarm_count() { pending_alarms; }

if ! wait_user_idle; then put "$RES" skipped user_operating; exit 1; fi
since="$(dev_since)"
start_stream "$OUT/.stream_raw.txt"
ctl SCHEDULE --ei seconds 600 --es id t4 > "$OUT/schedule_broadcast.txt"
wait_mark "$since" "MARK schedule_result" 20 || log "schedule_result が来ない"
sleep 1
put "$RES" alarm_lines_before "$(alarm_count)"
snap "$OUT" 0_scheduled

dsh "am force-stop $PKG"
sleep 2
put "$RES" alarm_lines_after_force_stop "$(alarm_count)"
put "$RES" package_stopped_flag "$(dsh "dumpsys package $PKG" | grep -oE 'stopped=(true|false)' | head -1)"
snap "$OUT" 1_after_force_stop

# 強制停止のあと、放送（FLAG_INCLUDE_STOPPED_PACKAGES なし）が届くか＝アプリが自分で復旧できる経路があるか
since_b="$(dev_since)"
adbs shell am broadcast -n "$PKG/.DebugControlReceiver" -a "$PKG.STATUS" | tr -d '\r' > "$OUT/broadcast_while_stopped.txt"
if wait_mark "$since_b" "MARK debug_control" 6; then
  put "$RES" broadcast_delivered_while_stopped true
else
  put "$RES" broadcast_delivered_while_stopped false
fi
put "$RES" alarm_lines_after_broadcast "$(alarm_count)"

# 次の起動（ランチャーからの起動の代役）。MainActivity が onCreate で全予約を張り直す
since2="$(dev_since)"
adbs shell am start -n "$PKG/.MainActivity" | tr -d '\r' > "$OUT/am_start.txt"
wait_mark "$since2" "MARK main_activity_rearm" 30 || log "main_activity_rearm が来ない"
sleep 1
recent_marks "$since2" "$OUT/.marks.txt"
put "$RES" rearm_on_launch "$(mark_line "$OUT/.marks.txt" rearm_all | sed -E 's/.*MARK rearm_all //; s/ interactive=.*//' | tr ' ' ';')"
put "$RES" alarm_lines_after_relaunch "$(alarm_count)"
snap "$OUT" 2_after_relaunch

since3="$(dev_since)"
ctl CANCEL_ALL > /dev/null
wait_mark "$since3" "MARK cancel_all_result" 20 || log "cancel_all_result が来ない"
sleep 1
put "$RES" alarm_lines_after_cancel "$(alarm_count)"
snap "$OUT" 3_after_cancel
save_logcat "$OUT" force_stop "$since"
cat "$RES"
