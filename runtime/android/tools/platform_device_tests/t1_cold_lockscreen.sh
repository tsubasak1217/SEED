#!/usr/bin/env bash
# ============================================================
#  T1: ロック画面からの冷えた起動（AC-1・AC-12・X-1）。T2・T5 もこの流れで鳴らし、鳴動中の操作だけを差し替える。
#    画面オフ・ロック中に SCHEDULE で LEAD 秒後（既定 90 秒）を予約 → 両プロセスを落とす（am kill / run-as kill。
#    force-stop は予約を消すので使わない）→ 発火 → 予定時刻からの各段階（配信・startForegroundService・:seed_platform の起動・
#    startForeground・音・フルスクリーン通知の起動・MainActivity の Displayed・エンジンの最初のフレーム）を logcat の epoch で測る。
#    鳴り始めから RING_DEADLINE_S 秒（既定 15 秒。上限 20 秒の約束）までに必ず止める。音は最小（schedule_quiet。W1-7 から。
#    W1-4b の 1〜4 回目は利用者の依頼の前なので音量を変えずに鳴らした）。試験の前後で STREAM_ALARM を比べ、違えば元へ戻す。
#    kill_platform は W1-4b の T5（直す前の記録）。W1-7 の復元の試験は t5_ring_restore.sh。
#    使い方: t1_cold_lockscreen.sh <予約 ID> [LEAD 秒] [出力のフォルダ名] [鳴動中の操作: none | task_remove | kill_platform | keep_activity]
#      none          … T1。証拠を取って STOP_RINGING
#      task_remove   … T2。am stack remove → 3 秒後・13 秒後（鳴り始め +17.5 秒で打ち切り）に音とプロセス → STOP_RINGING
#      kill_platform … T5。run-as kill -9 <:seed_platform> → 2 秒後・10 秒後に音・サービス・プロセス（直さずに記録）→ 後始末
#      keep_activity … T1 と同じだが、止めた後もメインプロセス（ロック画面の上の MainActivity）を残す（T6 の前段）
# ============================================================
set -u
source "$(dirname "$0")/common.sh"
ID="${1:-t1_1}"
LEAD_S="${2:-90}"
OUT="$RESULTS/${3:-$ID}"
MODE="${4:-none}"
mkdir -p "$OUT"
RES="$OUT/result.env"
: > "$RES"
LOCK_WAIT_MAX_S=300
LOCK_WAIT_STEP_S=20
PRE_FIRE_S=15
POLL_S=5
RING_WAIT_S=75
FRAME_WAIT_MS=10000
RING_DEADLINE_MS=15000
T2_FIRST_CHECK_S=3
T2_SECOND_CHECK_MS=13000
T2_CUTOFF_MS=17500
T5_FIRST_CHECK_S=2
T5_SECOND_CHECK_S=8
put "$RES" mode "$MODE"

device_present || { put "$RES" skipped device_absent; log "端末が無い"; exit 3; }
record_alarm_volume_baseline
wait_user_idle || { put "$RES" skipped user_operating_or_absent; exit 1; }
# 前提: 画面オフかロック中（点灯・解除のままなら自動のロックを最大 5 分待つ）
waited=0
until locked_or_off; do
  [ "$waited" -ge "$LOCK_WAIT_MAX_S" ] && { put "$RES" skipped not_locked; log "ロックされないので中止"; exit 1; }
  user_operating && { put "$RES" skipped user_operating; exit 1; }
  log "画面が点いてロックが外れている（$(screen_state)）。${LOCK_WAIT_STEP_S}s 待つ"
  sleep "$LOCK_WAIT_STEP_S"; waited=$((waited + LOCK_WAIT_STEP_S))
done
# 前の回の目覚ましが点けた画面を消す（充電中は自分では消えない端末。ロック中・利用者が使っていないときだけ）
put "$RES" screen_before_sleep_key "$(screen_state | tr ' ' ';')"
if sleep_screen_if_locked; then put "$RES" sleep_key "ok_or_already_off"; else put "$RES" sleep_key "not_applied"; fi
since="$(dev_since)"
start_stream "$OUT/stream.txt"
put "$RES" since "$since"
put "$RES" screen_at_start "$(screen_state | tr ' ' ';')"
dsh "dumpsys window" | grep -E 'mCurrentFocus|isKeyguardShowing|mKeyguardOccluded=|mDreamingLockscreen|mSleeping=' > "$OUT/0_before_window.txt"
snap "$OUT" 0_before

schedule_quiet "$ID" "$LEAD_S" "W1-7_T1" > "$OUT/schedule_broadcast.txt"
wait_mark "$since" '\[debug\] alarm\.schedule' 30 || log "schedule の結果が来ない"
stream_since "$since" > "$OUT/.s.txt"
trigger_at="$(trigger_of "$OUT/.s.txt")"
put "$RES" schedule_line "$(grep -m1 -E '\[debug\] alarm\.schedule' "$OUT/.s.txt" | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//')"
put "$RES" trigger_at "$trigger_at"
[ -z "$trigger_at" ] && { log "予定時刻が取れない。中止"; cleanup_alarms > "$OUT/cleanup.txt"; stop_stream; exit 4; }
sleep 2
# 冷えた状態にする（両プロセスを落とす）
kill_app_processes
kill_done_ms="$(dev_now_ms)"
put "$RES" kill_done_ms "$kill_done_ms"
put "$RES" pid_main_after_kill "$(pid_main)"
put "$RES" pid_platform_after_kill "$(pid_platform)"
snap "$OUT" 1_scheduled
log "予約 $ID trigger_at=$trigger_at（main=$(pid_main) platform=$(pid_platform) は空のはず）"

# 発火の 15 秒前まで待つ（利用者が使い始めたら取り消して中止・端末が外れたら何もしないで終える）
while [ $(( trigger_at - $(dev_now_ms) )) -gt $(( PRE_FIRE_S * 1000 )) ]; do
  device_present || { put "$RES" aborted device_absent_before_fire; log "端末が外れた"; stop_stream; exit 3; }
  if user_operating; then
    log "利用者が操作中（$(current_focus)）→ 予約を取り消して中止"
    cleanup_alarms > "$OUT/cleanup.txt"
    put "$RES" aborted "user_operating_before_fire"
    stop_stream; exit 2
  fi
  sleep "$POLL_S"
done
put "$RES" screen_before_fire "$(screen_state | tr ' ' ';')"
dsh "dumpsys window" | grep -E 'mCurrentFocus|isKeyguardShowing|mKeyguardOccluded=|mDreamingLockscreen|mSleeping=' > "$OUT/1_before_fire_window.txt"
log "発火前: $(screen_state)"

# 鳴り始めを待つ
if ! wait_mark "$since" "目覚まし $ID を鳴らし始めました" "$RING_WAIT_S"; then
  log "鳴り始めが来ない → 止めて後始末"
  put "$RES" ring_start_missing true
  ctl STOP_RINGING > /dev/null; cleanup_alarms > "$OUT/cleanup.txt"; stop_stream
  bash "$(dirname "$0")/analyze_ring.sh" "$OUT/stream.txt" "$trigger_at" "$ID" "$kill_done_ms" >> "$RES"
  exit 5
fi
ring_ms="$(stream_since "$since" | grep -m1 -E "目覚まし $ID を鳴らし始めました" | awk '{ printf "%d\n", $1 * 1000 + 0.5 }')"
deadline_ms=$(( ring_ms + RING_DEADLINE_MS ))
put "$RES" ring_start_device_ms "$ring_ms"
log "鳴り始めた（端末 $ring_ms）"
# 最初のフレームを待つ（鳴り始め +10 秒まで）
while [ $(( $(dev_now_ms) - ring_ms )) -lt "$FRAME_WAIT_MS" ]; do
  stream_since "$since" | grep -qE "\\[SEED FRAME 0\\] end" && break
  sleep 0.5
done
stream_since "$since" | grep -qE "\\[SEED FRAME 0\\] end" && log "エンジンの最初のフレームが出た" || log "最初のフレームがまだ"

# 鳴動中の証拠（軽いものだけ。止める期限を超えない）
put "$RES" screen_while_ringing "$(screen_state | tr ' ' ';')"
put "$RES" alarm_playing_while_ringing "$(alarm_playing)"
put "$RES" pid_main_while_ringing "$(pid_main)"
put "$RES" pid_platform_while_ringing "$(pid_platform)"
dsh "dumpsys window" | grep -E 'mCurrentFocus|isKeyguardShowing|mKeyguardOccluded=|mDreamingLockscreen|mSleeping=' > "$OUT/2_ringing_window.txt"
dsh "am stack list" | grep -E "taskId=[0-9]+: $PKG/" > "$OUT/2_ringing_stack.txt" || true
if [ "$(dev_now_ms)" -lt "$deadline_ms" ]; then
  dsh "dumpsys activity services $PKG" > "$OUT/2_ringing_services.txt"
  { dsh "dumpsys audio" | grep -E 'AudioPlaybackConfiguration' || true; } > "$OUT/2_ringing_audio_playback.txt"
fi
if [ "$(dev_now_ms)" -lt "$deadline_ms" ]; then
  dsh "dumpsys notification --noredact" | grep -E -A40 "pkg=$PKG" | head -120 > "$OUT/2_ringing_notification.txt" || true
fi

case "$MODE" in
  task_remove)
    # T2: 最近のタスクから消す（removeTask）→ 3 秒後・13 秒後（鳴り始め +17.5 秒で打ち切り）
    task_id="$(grep -m1 -oE 'taskId=[0-9]+' "$OUT/2_ringing_stack.txt" | sed 's/taskId=//')"
    put "$RES" ring_task "$(head -1 "$OUT/2_ringing_stack.txt" | sed -E 's/^ +//; s/ bounds=.*//' | tr ' ' ';')"
    if [ -n "$task_id" ]; then
      remove_ms="$(dev_now_ms)"
      put "$RES" remove_ms "$remove_ms"
      put "$RES" remove_after_ring_ms "$(( remove_ms - ring_ms ))"
      dsh "am stack remove $task_id" > "$OUT/stack_remove_output.txt" 2>&1
      log "タスク $task_id を消した（removeTask）"
      sleep "$T2_FIRST_CHECK_S"
      put "$RES" check1_after_remove_ms "$(( $(dev_now_ms) - remove_ms ))"
      put "$RES" pid_main_after_remove_1 "$(pid_main)"
      put "$RES" pid_platform_after_remove_1 "$(pid_platform)"
      put "$RES" alarm_playing_after_remove_1 "$(alarm_playing)"
      dsh "dumpsys media.audio_flinger" > "$OUT/3_after_remove_1_audio_flinger.txt"
      # 2 回目: remove +13 秒か鳴り始め +17.5 秒の早いほう
      target=$(( remove_ms + T2_SECOND_CHECK_MS ))
      cutoff=$(( ring_ms + T2_CUTOFF_MS ))
      [ "$target" -gt "$cutoff" ] && target="$cutoff"
      while [ "$(dev_now_ms)" -lt "$target" ]; do sleep 0.5; done
      put "$RES" check2_after_remove_ms "$(( $(dev_now_ms) - remove_ms ))"
      put "$RES" pid_main_after_remove_2 "$(pid_main)"
      put "$RES" pid_platform_after_remove_2 "$(pid_platform)"
      put "$RES" alarm_playing_after_remove_2 "$(alarm_playing)"
    else
      log "鳴動画面のタスクが見つからない。T2 は行わない"
      put "$RES" t2_skipped no_task
    fi
    ;;
  kill_platform)
    # T5: 鳴動中に :seed_platform を kill -9（直さずに振る舞いを記録する）
    pp="$(pid_platform)"
    put "$RES" platform_pid_before_kill "$pp"
    kill9_ms="$(dev_now_ms)"
    put "$RES" kill9_ms "$kill9_ms"
    put "$RES" kill9_after_ring_ms "$(( kill9_ms - ring_ms ))"
    dsh "run-as $PKG kill -9 $pp" > "$OUT/kill9_output.txt" 2>&1
    log ":seed_platform（pid $pp）を kill -9"
    sleep "$T5_FIRST_CHECK_S"
    put "$RES" pid_main_after_kill9_1 "$(pid_main)"
    put "$RES" pid_platform_after_kill9_1 "$(pid_platform)"
    put "$RES" alarm_playing_after_kill9_1 "$(alarm_playing)"
    dsh "dumpsys activity services $PKG" > "$OUT/3_after_kill9_1_services.txt"
    dsh "dumpsys notification --noredact" | grep -E -A30 "pkg=$PKG" | head -80 > "$OUT/3_after_kill9_1_notification.txt" || true
    sleep "$T5_SECOND_CHECK_S"
    put "$RES" pid_main_after_kill9_2 "$(pid_main)"
    put "$RES" pid_platform_after_kill9_2 "$(pid_platform)"
    put "$RES" alarm_playing_after_kill9_2 "$(alarm_playing)"
    dsh "dumpsys activity services $PKG" > "$OUT/4_after_kill9_2_services.txt"
    dsh "dumpsys media.audio_flinger" > "$OUT/4_after_kill9_2_audio_flinger.txt"
    ;;
esac

# 止める（T5 は止めるものが無いはず。命令は :seed_platform を起こし直す）
s_stop="$(dev_since)"
ctl STOP_RINGING --es id "$ID" > "$OUT/stop_broadcast.txt"
wait_mark "$s_stop" '\[debug\] alarm\.stop_ringing' 20 || log "stop_ringing の結果が来ない"
put "$RES" stop_after_ring_ms "$(( $(dev_now_ms) - ring_ms ))"
sleep 2
put "$RES" stop_line "$(stream_since "$s_stop" | grep -m1 -E '\[debug\] alarm\.stop_ringing' | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//')"
put "$RES" alarm_playing_after_stop "$(alarm_playing)"
cleanup_alarms > "$OUT/cleanup.txt"
snap "$OUT" 5_after_stop
put "$RES" screen_after_stop "$(screen_state | tr ' ' ';')"
dsh "am stack list" | grep -E "taskId=[0-9]+: $PKG/" > "$OUT/5_after_stop_stack.txt" || true
if [ "$MODE" != "keep_activity" ] && [ "$MODE" != "kill_platform" ]; then
  # 次の回を冷えた状態から始めるため、ロック画面の上に残った MainActivity ごとメインプロセスを落とす
  # （このシーン〈Main〉にはスクリプトが無く SetShowWhenLocked(false) を呼ばないので、下ろす者がいない）
  kill_app_processes
fi
sleep 1
verify_alarm_volume > "$OUT/alarm_volume_after.txt"; put "$RES" alarm_volume_after "$(tr '\n' ';' < "$OUT/alarm_volume_after.txt")"
stop_stream

# 集計（予定時刻からの ms）
bash "$(dirname "$0")/analyze_ring.sh" "$OUT/stream.txt" "$trigger_at" "$ID" "$kill_done_ms" >> "$RES"
cat "$RES"
