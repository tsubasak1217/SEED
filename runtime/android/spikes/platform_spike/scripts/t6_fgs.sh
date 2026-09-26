#!/usr/bin/env bash
# ============================================================
#  t6: 前景サービスの起動条件と種類（E-03）
#   A: 予約を経ずに、背面の :seed_platform の受信機から startForegroundService（mediaPlayback）
#      → 背面からの起動の制限に当たるはず（AlarmReceiver から通るのは正確なアラームの例外のおかげ、の切り分け）
#   B: 予約（setAlarmClock）経由で systemExempted の種類で鳴らす（USE_EXACT_ALARM を持つアプリだけが使える種類）。
#      安全弁（20 秒）で止まるのを待ち、「止まった」の放送が鳴動画面へ届くまでの遅延も取る。
#   mediaPlayback を予約経由で鳴らす場合は t1・t3 が兼ねる。
# ============================================================
set -u
source "$(dirname "$0")/common.sh"
OUT="$RESULTS/06_fgs"
mkdir -p "$OUT"
RES="$OUT/result.env"
: > "$RES"
FGS_MARKS="MARK (fgs_start_denied|fgs_started|fgs_start_error|fgs_start_failed)"
MAX_RING_S=20

# ---- A ----
dsh "am kill $PKG"
sleep 2
put "$RES" a_pids_before "main=$(pid_main);platform=$(pid_platform)"
sinceA="$(dev_since)"
start_stream "$OUT/.stream_a.txt"
adbs shell am broadcast -f "$FLAG_INCLUDE_STOPPED" -n "$PKG/.DebugPlatformReceiver" -a "$PKG.RING_NOW" \
  --es fgs_type mediaPlayback --ei max_ring_s 15 | tr -d '\r' > "$OUT/a_broadcast.txt"
wait_mark "$sinceA" "$FGS_MARKS" 15 || log "A: 前景サービスの目印が来ない"
sleep 2
recent_marks "$sinceA" "$OUT/a_marks.txt"
a_line="$(grep -E "$FGS_MARKS" "$OUT/a_marks.txt" | head -1 | sed -E 's/.*MARK //')"
put "$RES" a_result "$(echo "$a_line" | tr ' ' ';')"
if grep -qE 'MARK fgs_started' "$OUT/a_marks.txt"; then
  log "A: 起動できてしまった（鳴っている）→ 止める"
  s="$(dev_since)"; ctl STOP > /dev/null; wait_mark "$s" "MARK ring_stop" 20 || true
fi
save_logcat "$OUT" a "$sinceA"
put "$RES" a_system_log "$(grep -m1 -oE 'Background started FGS: [A-Za-z]+[^]]*\]' "$OUT/a_logcat_all.txt" | cut -c1-160 | tr ' ' '_')"

# ---- B ----
if ! wait_user_idle; then put "$RES" b_skipped user_operating; cat "$RES"; exit 1; fi
sinceB="$(dev_since)"
start_stream "$OUT/.stream_b.txt"
put "$RES" b_screen_at_schedule "$(screen_state | tr ' ' ';')"
ctl SCHEDULE --ei seconds 40 --es id t6sys --es fgs_type systemExempted --ei max_ring_s "$MAX_RING_S" > "$OUT/b_schedule.txt"
wait_mark "$sinceB" "MARK schedule_result" 20 || log "B: schedule_result が来ない"
dsh "am kill $PKG"
wait_mark "$sinceB" "$FGS_MARKS" 90 || log "B: 前景サービスの目印が来ない"
wait_mark "$sinceB" "MARK ring_activity_first_frame" 20 || log "B: first_frame が来ない"
sleep 3
snap "$OUT" b_ringing
put "$RES" b_service_types "$(grep -oE 'types=0x[0-9a-f]+|foregroundServiceType=0x[0-9a-f]+|foregroundId=[0-9]+' "$OUT/b_ringing_dumpsys_services.txt" | head -3 | tr '\n' ';')"
put "$RES" b_alarm_playing "$(alarm_playing)"
if wait_mark "$sinceB" "MARK ring_stop" $((MAX_RING_S + 15)); then
  log "B: 安全弁で停止"
else
  s="$(dev_since)"; ctl STOP > /dev/null; wait_mark "$s" "MARK ring_stop" 20 || log "B: ring_stop が来ない"
fi
sleep 2
save_logcat "$OUT" b "$sinceB"
F="$OUT/b_logcat_spike.txt"
b_line="$(grep -E "$FGS_MARKS" "$F" | head -1 | sed -E 's/.*MARK //; s/ can_schedule_exact.*//')"
put "$RES" b_result "$(echo "$b_line" | tr ' ' ';')"
put "$RES" b_fgs_start_reason "$(grep -m1 -oE 'am_foreground_service_start: \[[^]]*' "$F" | cut -d, -f4)"
put "$RES" b_ring_start_since_sched_ms "$(mark_field "$F" ring_start since_sched_ms)"
put "$RES" b_first_frame_keyguard_locked "$(mark_field "$F" ring_activity_first_frame keyguard_locked)"
put "$RES" b_ring_stop_reason "$(mark_field "$F" ring_stop reason)"
put "$RES" b_ring_stop_rang_ms "$(mark_field "$F" ring_stop rang_ms)"
stop_wall="$(mark_field "$F" ring_stop wall)"
got_wall="$(mark_field "$F" ring_activity_stopped_broadcast wall)"
[ -n "$stop_wall" ] && [ -n "$got_wall" ] && put "$RES" b_stopped_broadcast_latency_ms "$((got_wall - stop_wall))"
cat "$RES"
