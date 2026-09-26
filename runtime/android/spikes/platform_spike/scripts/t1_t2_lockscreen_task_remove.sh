#!/usr/bin/env bash
# ============================================================
#  t1: 画面オフ・ロック中に N 秒後（既定 90 秒）の予約 → 鳴動開始からロック画面の上に RingActivity が出るまでの時間
#      予約の後にアプリの両プロセスを落とす（am kill）＝「アプリのプロセスが無いとき」の冷えた状態で鳴らす。
#  t2: 鳴動中に RingActivity のタスクを am stack remove で消し（最近のタスクから消すのと同じ removeTask）、
#      メインのプロセスが消えても :seed_platform が生きて音が続くか（pidof・dumpsys audio・心拍のログ）。
#  画面は端末の自然な消灯・ロックのまま使う（KEYCODE_SLEEP などの操作はしない）。点灯していればその旨を記録する。
#  logcat は試験の間ずっとファイルへ流す（端末の main バッファは数分で古い行が消える）。
# ============================================================
set -u
source "$(dirname "$0")/common.sh"
LEAD_S="${1:-90}"
ID="${2:-t1}"
OUT="$RESULTS/${3:-01_lockscreen_02_task_remove}"
mkdir -p "$OUT"
RES="$OUT/result.env"
: > "$RES"
MAX_RING_S=60

if ! wait_user_idle; then put "$RES" skipped user_operating; exit 1; fi
since="$(dev_since)"
start_stream "$OUT/.stream_raw.txt"
put "$RES" since "$since"
put "$RES" screen_at_schedule "$(screen_state | tr ' ' ';')"
snap "$OUT" 0_before
ctl SCHEDULE --ei seconds "$LEAD_S" --es id "$ID" --ei max_ring_s "$MAX_RING_S" > "$OUT/schedule_broadcast.txt"
wait_mark "$since" "MARK schedule_result" 20 || log "schedule_result が来ない"
sleep 3
# アプリのプロセスが無い状態にする（両方とも背面なので am kill で落ちる）
dsh "am kill $PKG"
sleep 2
put "$RES" pid_main_after_kill "$(pid_main)"
put "$RES" pid_platform_after_kill "$(pid_platform)"
snap "$OUT" 1_scheduled
recent_marks "$since" "$OUT/.marks.txt"
trigger_at="$(mark_field "$OUT/.marks.txt" schedule_result trigger_at)"
put "$RES" trigger_at "$trigger_at"
log "予約 trigger_at=$trigger_at（main=$(pid_main) platform=$(pid_platform) は空のはず）"

# 発火の 15 秒前の画面の状態
now_ms="$(dev_now_ms)"
wait_s=$(( (trigger_at - now_ms) / 1000 - 15 ))
log "発火の 15 秒前まで ${wait_s}s 待つ"
[ "$wait_s" -gt 0 ] && [ "$wait_s" -lt 600 ] && sleep "$wait_s"
put "$RES" screen_before_fire "$(screen_state | tr ' ' ';')"
log "発火前: $(screen_state)"

# 鳴動と鳴動画面を待つ
if wait_mark "$since" "MARK ring_start" 60; then log "鳴動開始"; else log "ring_start が来ない"; fi
shown=false
if wait_mark "$since" "MARK ring_activity_first_frame" 30; then shown=true; fi
put "$RES" ring_activity_shown "$shown"
sleep 3
put "$RES" screen_while_ringing "$(screen_state | tr ' ' ';')"
put "$RES" alarm_playing_before_remove "$(alarm_playing)"
put "$RES" pid_main_before_remove "$(pid_main)"
put "$RES" pid_platform_before_remove "$(pid_platform)"
snap "$OUT" 2_ringing
dsh "dumpsys notification --noredact" | grep -A30 -E "pkg=$PKG" | head -80 > "$OUT/2_ringing_notification.txt" || true

# t2: 鳴動画面のタスクを消す（最近のタスクから消すのと同じ removeTask）
task_line="$(dsh "am stack list" | grep -E "taskId=[0-9]+: $PKG/" | head -1)"
task_id="$(echo "$task_line" | grep -oE 'taskId=[0-9]+' | head -1 | sed 's/taskId=//')"
put "$RES" ring_task "$(echo "$task_line" | sed -E 's/^ +//; s/ bounds=.*//' | tr ' ' ';')"
remove_ms=""
if [ -n "$task_id" ]; then
  remove_ms="$(dev_now_ms)"
  put "$RES" remove_ms "$remove_ms"
  dsh "am stack remove $task_id" > "$OUT/stack_remove_output.txt" 2>&1
  log "タスク $task_id を削除（removeTask）"
  sleep 3
  put "$RES" pid_main_after_remove_3s "$(pid_main)"
  put "$RES" pid_platform_after_remove_3s "$(pid_platform)"
  put "$RES" alarm_playing_after_remove_3s "$(alarm_playing)"
  snap "$OUT" 3_after_remove_3s
  sleep 10
  put "$RES" pid_main_after_remove_13s "$(pid_main)"
  put "$RES" pid_platform_after_remove_13s "$(pid_platform)"
  put "$RES" alarm_playing_after_remove_13s "$(alarm_playing)"
  snap "$OUT" 4_after_remove_13s
else
  log "鳴動画面のタスクが見つからない（ヘッドアップ通知だけ等）。t2 は行わない"
  put "$RES" t2_skipped no_task
fi

# 止める（エンジンの代役 → ContentResolver.call("ring.stop")）
since_stop="$(dev_since)"
ctl STOP > "$OUT/stop_broadcast.txt"
wait_mark "$since_stop" "MARK ring_stop" 20 || log "ring_stop が来ない"
sleep 2
put "$RES" alarm_playing_after_stop "$(alarm_playing)"
snap "$OUT" 5_after_stop
save_logcat "$OUT" t1t2 "$since"

# ---- 集計（端末の時計の UTC epoch ミリ秒） ----
F="$OUT/t1t2_logcat_spike.txt"
recv="$(mark_field "$F" alarm_received wall)"
ring="$(mark_field "$F" ring_start wall)"
create="$(mark_field "$F" ring_activity_create wall)"
frame="$(mark_field "$F" ring_activity_first_frame wall)"
focus="$(mark_field "$F" ring_activity_focus wall)"
put "$RES" alarm_received_late_ms "$(mark_field "$F" alarm_received late_ms)"
put "$RES" alarm_received_interactive "$(mark_field "$F" alarm_received interactive)"
put "$RES" alarm_received_keyguard_locked "$(mark_field "$F" alarm_received keyguard_locked)"
put "$RES" alarm_received_device_idle "$(mark_field "$F" alarm_received device_idle)"
put "$RES" fgs_type "$(mark_field "$F" fgs_started type)"
put "$RES" fgs_denied "$(grep -cE 'MARK fgs_start_(denied|failed|error)' "$F")"
put "$RES" fgs_allowed_log "$(grep -m1 -oE 'Background started FGS: [A-Za-z]+' "$F" | tr ' ' '_')"
put "$RES" fgs_start_reason "$(grep -m1 -oE 'am_foreground_service_start: \[[^]]*' "$F" | cut -d, -f4)"
put "$RES" ring_start_since_sched_ms "$(mark_field "$F" ring_start since_sched_ms)"
[ -n "$recv" ] && [ -n "$ring" ] && put "$RES" ring_start_after_receive_ms "$((ring - recv))"
[ -n "$ring" ] && [ -n "$create" ] && put "$RES" activity_create_after_ring_ms "$((create - ring))"
[ -n "$ring" ] && [ -n "$frame" ] && put "$RES" first_frame_after_ring_ms "$((frame - ring))"
[ -n "$ring" ] && [ -n "$focus" ] && put "$RES" focus_after_ring_ms "$((focus - ring))"
[ -n "$trigger_at" ] && [ -n "$frame" ] && put "$RES" first_frame_after_sched_ms "$((frame - trigger_at))"
put "$RES" ring_activity_trusted "$(mark_field "$F" ring_activity_create trusted)"
put "$RES" ring_activity_component "$(mark_field "$F" ring_activity_create component)"
put "$RES" first_frame_keyguard_locked "$(mark_field "$F" ring_activity_first_frame keyguard_locked)"
put "$RES" first_frame_interactive "$(mark_field "$F" ring_activity_first_frame interactive)"
put "$RES" keyguard_occlude_transition "$(grep -cE 'type = KEYGUARD_OCCLUDE' "$F")"
put "$RES" wm_activity_launch_time_ms "$(grep -m1 -oE "wm_activity_launch_time: \[[^]]*$PKG[^]]*" "$F" | awk -F, '{print $NF}')"
put "$RES" task_removed_mark "$(grep -cE 'MARK task_removed' "$F")"
if [ -n "$remove_ms" ]; then
  put "$RES" heartbeats_after_remove "$(grep -E 'MARK ring_heartbeat' "$F" | awk -v t="$remove_ms" '{ for (i = 1; i <= NF; i++) if ($i ~ /^wall=/) { split($i, a, "="); if (a[2] + 0 > t + 0) n++ } } END { print n + 0 }')"
  put "$RES" heartbeat_playing_after_remove "$(grep -E 'MARK ring_heartbeat' "$F" | awk -v t="$remove_ms" '{ w = 0; p = ""; for (i = 1; i <= NF; i++) { if ($i ~ /^wall=/) { split($i, a, "="); w = a[2] } if ($i ~ /^playing=/) { split($i, b, "="); p = b[2] } } if (w + 0 > t + 0) s = s p "," } END { print s }')"
  put "$RES" main_process_killed_log "$(grep -m1 -oE "Killing [0-9]+:$PKG/[^ ]+ \(adj [0-9]+\): [a-z ]+" "$F" | tr ' ' '_')"
fi
put "$RES" ring_stop_reason "$(mark_field "$F" ring_stop reason)"
put "$RES" ring_stop_rang_ms "$(mark_field "$F" ring_stop rang_ms)"
cat "$RES"
