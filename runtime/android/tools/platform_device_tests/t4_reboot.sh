#!/usr/bin/env bash
# ============================================================
#  T4: 再起動の後の張り直しと Direct Boot（AC-4・E-10・W1-0 の残り。adb reboot は利用者の許可で 1 回だけ）
#    LEAD 秒後（既定 240 秒・max_ring_minutes 1）を予約 → 控え（端末保護ストレージの alarms.json）と dumpsys alarm を記録 →
#    adb reboot → wait-for-device → 起動直後から logcat をファイルへ流す → ロックは解除しない（Direct Boot のまま）→
#    BootReceiver が LOCKED_BOOT_COMPLETED で張り直すか（dumpsys alarm）・発火で RingService（directBootAware）が鳴るか・
#    MainActivity（directBootAware でない）は出ないはず、を記録 → STOP_RINGING（受信機はメインプロセスなので解除前は届かない見込み。
#    届かなければ am force-stop で止め、理由を記録）。
#    --reboot-permitted が無ければ何もしない。
# ============================================================
set -u
source "$(dirname "$0")/common.sh"
[ "${1:-}" = "--reboot-permitted" ] || { echo "（--reboot-permitted が無いので何もしません）"; exit 0; }
ID="${2:-t4}"
LEAD_S="${3:-240}"
OUT="$RESULTS/${4:-t4_reboot}"
mkdir -p "$OUT"
RES="$OUT/result.env"
: > "$RES"
DEVICE_WAIT_S=300
BOOT_WAIT_S=300
REARM_WAIT_S=180
RING_WAIT_S=120
STOP_CHECK_S=10
DE_DIR="/data/user_de/0/$PKG/files/seed_platform"
user_state() { dsh "am get-started-user-state 0" 2>&1 | head -1; }

device_present || { put "$RES" skipped device_absent; exit 3; }
user_operating && { put "$RES" skipped user_operating; exit 1; }
locked_or_off || { put "$RES" skipped not_locked; exit 1; }
since="$(dev_since)"
start_stream "$OUT/stream_before_reboot.txt"
put "$RES" screen_at_start "$(screen_state | tr ' ' ';')"
ctl SCHEDULE --ei seconds "$LEAD_S" --es id "$ID" --ei max_ring_minutes 1 --es title "W1-4b_T4" --es body "$ID" $QUIET_ARGS > "$OUT/schedule_broadcast.txt"
wait_mark "$since" '\[debug\] alarm\.schedule' 30 || log "schedule の結果が来ない"
stream_since "$since" > "$OUT/.s.txt"
trigger_at="$(trigger_of "$OUT/.s.txt")"
if ! quiet_request_ok "$OUT/.s.txt"; then log "静かな引数が予約に入っていない → 取り消して中止"; put "$RES" aborted quiet_args_missing; cleanup_alarms > "$OUT/cleanup.txt"; stop_stream; exit 6; fi
put "$RES" schedule_line "$(grep -m1 -E '\[debug\] alarm\.schedule' "$OUT/.s.txt" | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//')"
put "$RES" trigger_at "$trigger_at"
[ -z "$trigger_at" ] && { log "予定時刻が取れない。中止"; cleanup_alarms > "$OUT/cleanup.txt"; stop_stream; exit 4; }
dsh "run-as $PKG cat $DE_DIR/alarms.json" > "$OUT/0_alarms_json_before_reboot.txt" 2>&1
put "$RES" pending_before_reboot "$(pending_alarms)"
snap "$OUT" 0_before_reboot
stop_stream

# 再起動
reboot_ms="$(dev_now_ms)"
put "$RES" reboot_cmd_device_ms "$reboot_ms"
log "adb reboot（予定まで $(( (trigger_at - reboot_ms) / 1000 )) 秒）"
adbs reboot > "$OUT/reboot_output.txt" 2>&1
sleep 5
timeout "$DEVICE_WAIT_S" "$ADB" -s "$SERIAL" wait-for-device || { put "$RES" aborted device_not_back; log "端末が戻らない"; exit 3; }
waited=0
until [ "$(dsh 'getprop sys.boot_completed')" = "1" ]; do
  [ "$waited" -ge "$BOOT_WAIT_S" ] && { put "$RES" aborted boot_not_completed; exit 3; }
  sleep 2; waited=$((waited + 2))
done
put "$RES" boot_completed_device_ms "$(dev_now_ms)"
put "$RES" user_state_after_boot "$(user_state)"
log "起動完了（$(user_state)）。ロックは解除しない"
# 再起動で logcat は消えるので、起動の頭から全部を流す（-T を付けない）
STREAM_FILE="$OUT/stream_after_reboot.txt"; : > "$STREAM_FILE"
"$ADB" -s "$SERIAL" logcat -v epoch -b main,system,crash,events -T 1.000 > "$STREAM_FILE" 2>/dev/null &
STREAM_SINCE_LIST="1.000"
STREAM_PID=$!
sleep 2
wait_mark 0 "BootReceiver: android.intent.action.LOCKED_BOOT_COMPLETED" "$REARM_WAIT_S" && log "LOCKED_BOOT_COMPLETED で張り直した" || log "LOCKED_BOOT_COMPLETED の張り直しが見えない"
sleep 2
snap "$OUT" 1_after_boot
dsh "run-as $PKG cat $DE_DIR/alarms.json" > "$OUT/1_alarms_json_after_boot.txt" 2>&1
put "$RES" pending_after_boot "$(pending_alarms)"
put "$RES" user_state_before_fire "$(user_state)"
put "$RES" screen_before_fire "$(screen_state | tr ' ' ';')"
# 発火を待つ
if wait_mark 0 "AlarmReceiver: $ID の発火を処理しました|目覚まし $ID を鳴らし始めました" $(( (trigger_at - $(dev_now_ms)) / 1000 + RING_WAIT_S )); then
  wait_mark 0 "目覚まし $ID を鳴らし始めました" 10 || true
  sleep 2
  put "$RES" user_state_while_ringing "$(user_state)"
  put "$RES" screen_while_ringing "$(screen_state | tr ' ' ';')"
  put "$RES" alarm_playing_while_ringing "$(alarm_playing)"
  put "$RES" pid_main_while_ringing "$(pid_main)"
  put "$RES" pid_platform_while_ringing "$(pid_platform)"
  dsh "dumpsys activity services $PKG" > "$OUT/2_ringing_services.txt"
  dsh "dumpsys notification --noredact" | grep -E -A40 "pkg=$PKG" | head -120 > "$OUT/2_ringing_notification.txt" || true
else
  log "発火が見えない"
  put "$RES" fire_missing true
fi
# 止める: 受信機（メインプロセス・directBootAware でない）で止められるか
s_stop="$(dev_since)"
ctl STOP_RINGING --es id "$ID" > "$OUT/stop_broadcast.txt" 2>&1
put "$RES" stop_broadcast "$(tr '\n' ' ' < "$OUT/stop_broadcast.txt" | cut -c1-300)"
if wait_mark "$s_stop" '\[debug\] alarm\.stop_ringing' "$STOP_CHECK_S"; then
  put "$RES" stopped_by "debug_receiver"
else
  put "$RES" stopped_by "force_stop（受信機の結果が $STOP_CHECK_S 秒で来ない）"
  dsh "am force-stop $PKG" > "$OUT/force_stop_output.txt" 2>&1
fi
sleep 2
put "$RES" alarm_playing_after_stop "$(alarm_playing)"
put "$RES" pending_after_stop "$(pending_alarms)"
verify_alarm_volume > "$OUT/alarm_volume_after.txt"; put "$RES" alarm_volume_after "$(tr '\n' ';' < "$OUT/alarm_volume_after.txt")"
put "$RES" user_state_at_end "$(user_state)"
snap "$OUT" 3_after_stop
stop_stream
F="$OUT/stream_after_reboot.txt"
put "$RES" boot_receiver_lines "$(tr -d '\r' < "$F" | grep -E 'BootReceiver' | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//' | tr '\n' '|')"
put "$RES" provider_lines "$(tr -d '\r' < "$F" | grep -E 'PlatformProvider を作りました|起動時の照合' | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//' | tr '\n' '|')"
put "$RES" fsi_lines "$(tr -d '\r' < "$F" | grep -E "START u0 .*$PKG|Displayed $PKG/|PlatformEntry" | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//' | cut -c1-300 | head -10 | tr '\n' '|')"
put "$RES" direct_boot_errors "$(tr -d '\r' < "$F" | grep -E "$PKG" | grep -iE 'locked|unlock|directBoot|not yet unlocked|Unable to start|Failed to' | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//' | cut -c1-300 | head -10 | tr '\n' '|')"
bash "$(dirname "$0")/analyze_ring.sh" "$F" "$trigger_at" "$ID" 0 >> "$RES"
cat "$RES"
