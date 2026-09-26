#!/usr/bin/env bash
# ============================================================
#  t8（再起動の代わりに今できる範囲）: adb install -r（アプリの更新）で予約が残るか、
#   exported=false の BootReceiver にシステムの MY_PACKAGE_REPLACED が届いて張り直せるか。
#   ※ adb install は Play Protect の確認で止まることがある（t0 の記録を参照）。止まれば 300 秒で諦める。
# ============================================================
set -u
source "$(dirname "$0")/common.sh"
OUT="$RESULTS/08_package_replaced"
mkdir -p "$OUT"
RES="$OUT/result.env"
: > "$RES"
INSTALL_TIMEOUT_S=300

alarm_count() { pending_alarms; }

since="$(dev_since)"
start_stream "$OUT/.stream_raw.txt"
ctl SCHEDULE --ei seconds 900 --es id t8 > "$OUT/schedule_broadcast.txt"
wait_mark "$since" "MARK schedule_result" 20 || log "schedule_result が来ない"
sleep 1
put "$RES" alarm_lines_before "$(alarm_count)"
snap "$OUT" 0_before

timeout "$INSTALL_TIMEOUT_S" "$ADB" -s "$SERIAL" install -r "$APK" > "$OUT/install_r.txt" 2>&1
put "$RES" install_exit "$?"
put "$RES" install_output "$(tr '\n' ' ' < "$OUT/install_r.txt" | tr ' ' '_')"
if wait_mark "$since" "MARK rearm_all reason=boot:android.intent.action.MY_PACKAGE_REPLACED" 60; then
  put "$RES" my_package_replaced_received true
else
  put "$RES" my_package_replaced_received false
fi
sleep 1
recent_marks "$since" "$OUT/.marks.txt"
put "$RES" rearm "$(grep -E 'MARK rearm_all reason=boot' "$OUT/.marks.txt" | head -1 | sed -E 's/.*MARK rearm_all //; s/ interactive=.*//' | tr ' ' ';')"
put "$RES" alarm_lines_after "$(alarm_count)"
snap "$OUT" 1_after_install

s="$(dev_since)"; ctl CANCEL_ALL > /dev/null; wait_mark "$s" "MARK cancel_all_result" 20 || true
put "$RES" alarm_lines_after_cancel "$(alarm_count)"
save_logcat "$OUT" package_replaced "$since"
cat "$RES"
