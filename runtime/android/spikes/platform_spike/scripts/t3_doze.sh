#!/usr/bin/env bash
# ============================================================
#  t3: Doze（dumpsys deviceidle force-idle）の下で N 秒後（既定 300 秒）の予約 → 実際に鳴った時刻との差
#   公式の手順（developer.android.com「Test with Doze」）どおり battery unplug → force-idle。
#   終わったら（失敗しても trap で）deviceidle unforce と battery reset で必ず戻す。
#   鳴動は安全弁（max_ring_s=20）で止まるのを待つ（安全弁の確認と、:seed_platform → 鳴動画面への「止まった」の放送の遅延の標本）。
# ============================================================
set -u
source "$(dirname "$0")/common.sh"
LEAD_S="${1:-300}"
OUT="$RESULTS/03_doze"
mkdir -p "$OUT"
RES="$OUT/result.env"
: > "$RES"
POLL_S=30
MAX_RING_S=20

restore() {
  dsh "dumpsys deviceidle unforce" > "$OUT/restore_unforce.txt" 2>&1
  dsh "dumpsys battery reset" > "$OUT/restore_battery.txt" 2>&1
}
trap restore EXIT

if ! wait_user_idle; then put "$RES" skipped user_operating; exit 1; fi
since="$(dev_since)"
start_stream "$OUT/.stream_raw.txt"
put "$RES" screen_at_start "$(screen_state | tr ' ' ';')"
snap "$OUT" 0_before
dsh "dumpsys battery unplug" > "$OUT/battery_unplug.txt" 2>&1
dsh "dumpsys deviceidle force-idle" > "$OUT/force_idle_output.txt" 2>&1
put "$RES" force_idle_output "$(tr '\n' ' ' < "$OUT/force_idle_output.txt" | tr ' ' '_')"
dsh "dumpsys deviceidle" | grep -E 'mState=|mLightState=|mForceIdle=' > "$OUT/idle_state_after_force.txt"
put "$RES" idle_state_after_force "$(tr '\n' ' ' < "$OUT/idle_state_after_force.txt" | tr -s ' ' | tr ' ' ';')"

ctl SCHEDULE --ei seconds "$LEAD_S" --es id t3 --ei max_ring_s "$MAX_RING_S" > "$OUT/schedule_broadcast.txt"
wait_mark "$since" "MARK schedule_result" 30 || log "schedule_result が来ない"
recent_marks "$since" "$OUT/.marks.txt"
trigger_at="$(mark_field "$OUT/.marks.txt" schedule_result trigger_at)"
put "$RES" trigger_at "$trigger_at"
log "予約 trigger_at=$trigger_at"
dsh "dumpsys deviceidle" | grep -E 'mState=|mLightState=|mForceIdle=' > "$OUT/idle_state_after_schedule.txt"
put "$RES" idle_state_after_schedule "$(tr '\n' ' ' < "$OUT/idle_state_after_schedule.txt" | tr -s ' ' | tr ' ' ';')"
snap "$OUT" 1_scheduled
dsh "am kill $PKG"

# 発火の 40 秒前まで 30 秒ごとに Doze の状態を記録
: > "$OUT/idle_timeline.txt"
while :; do
  now_ms="$(dev_now_ms)"
  echo "$(date '+%T') device_ms=$now_ms to_fire_s=$(( (trigger_at - now_ms) / 1000 )) $(dsh "dumpsys deviceidle" | grep -E 'mState=|mLightState=|mForceIdle=' | tr -s ' \n' ' ') wakefulness=$(wakefulness)" >> "$OUT/idle_timeline.txt"
  [ $(( trigger_at - now_ms )) -le $(( (POLL_S + 10) * 1000 )) ] && break
  # 利用者が端末を使い始めたら（Doze の前提が崩れ、鳴動が邪魔になる）予約を取り消して中止する
  if user_operating; then
    log "利用者が操作中（$(top_activity)）→ 予約を取り消して中止"
    s="$(dev_since)"; ctl CANCEL --es id t3 > /dev/null; wait_mark "$s" "MARK cancel_result" 20 || true
    put "$RES" aborted "user_operating_before_fire:$(top_activity)"
    save_logcat "$OUT" doze_aborted "$since"
    exit 2
  fi
  ensure_stream
  sleep "$POLL_S"
done
tail -1 "$OUT/idle_timeline.txt"
wait_mark "$since" "MARK ring_start" 120 || log "ring_start が来ない"
wait_mark "$since" "MARK ring_activity_first_frame" 30 || log "first_frame が来ない"
sleep 3
snap "$OUT" 2_ringing
put "$RES" alarm_playing_while_ringing "$(alarm_playing)"
# 安全弁で止まるのを待つ（止まらなければ命令で止める）
if wait_mark "$since" "MARK ring_stop" $((MAX_RING_S + 15)); then
  log "安全弁で停止"
else
  log "安全弁で止まらない → STOP"
  s="$(dev_since)"; ctl STOP > /dev/null; wait_mark "$s" "MARK ring_stop" 20 || log "ring_stop が来ない"
fi
sleep 2

restore
trap - EXIT
sleep 2
snap "$OUT" 3_after_restore
save_logcat "$OUT" doze "$since"

F="$OUT/doze_logcat_spike.txt"
put "$RES" wake_from_idle_log "$(grep -m1 -E 'device_idle_wake_from_idle: \[0,\*walarm\*:com.seedengine.platformspike' "$F" | awk '{print $1}')"
put "$RES" alarm_received_late_ms "$(mark_field "$F" alarm_received late_ms)"
put "$RES" alarm_received_device_idle "$(mark_field "$F" alarm_received device_idle)"
put "$RES" alarm_received_light_idle "$(mark_field "$F" alarm_received light_idle)"
put "$RES" alarm_received_interactive "$(mark_field "$F" alarm_received interactive)"
put "$RES" fgs_start_reason "$(grep -m1 -oE 'am_foreground_service_start: \[[^]]*' "$F" | cut -d, -f4)"
put "$RES" ring_start_since_sched_ms "$(mark_field "$F" ring_start since_sched_ms)"
ring="$(mark_field "$F" ring_start wall)"
frame="$(mark_field "$F" ring_activity_first_frame wall)"
[ -n "$ring" ] && [ -n "$frame" ] && put "$RES" first_frame_after_ring_ms "$((frame - ring))"
[ -n "$trigger_at" ] && [ -n "$frame" ] && put "$RES" first_frame_after_sched_ms "$((frame - trigger_at))"
put "$RES" first_frame_keyguard_locked "$(mark_field "$F" ring_activity_first_frame keyguard_locked)"
put "$RES" wm_activity_launch_time_ms "$(grep -m1 -oE "wm_activity_launch_time: \[[^]]*$PKG[^]]*" "$F" | awk -F, '{print $NF}')"
put "$RES" ring_stop_reason "$(mark_field "$F" ring_stop reason)"
put "$RES" ring_stop_rang_ms "$(mark_field "$F" ring_stop rang_ms)"
stop_wall="$(mark_field "$F" ring_stop wall)"
got_wall="$(mark_field "$F" ring_activity_stopped_broadcast wall)"
[ -n "$stop_wall" ] && [ -n "$got_wall" ] && put "$RES" stopped_broadcast_latency_ms "$((got_wall - stop_wall))"
put "$RES" idle_state_after_restore "$(grep -E 'mForceIdle=|mState=' "$OUT/3_after_restore_state.txt" | tr -s ' \n' ';')"
put "$RES" battery_after_restore "$(grep -E 'AC powered|USB powered' "$OUT/3_after_restore_state.txt" | tr -s ' \n' ';')"
grep -iE 'deviceidle|DeviceIdle|device_idle' "$OUT/doze_logcat_all.txt" | head -80 > "$OUT/deviceidle_log_excerpt.txt" || true
cat "$RES"
