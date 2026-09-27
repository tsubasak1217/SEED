#!/usr/bin/env bash
# ============================================================
#  T5: 鳴動中に :seed_platform が殺されたときの復元（W1-7。AC-3 の残り・W1-4b の T5 と G-6）
#
#    t5_ring_restore.sh <kill|forcestop> [予約 ID] [出力の名前] [鳴り始めから殺すまでの秒]
#
#    kill      … 鳴り始めて KILL_AFTER_S 秒後に :seed_platform を run-as で kill -9 → 見張りの予約（RingWatchdog。最長 20 秒後）が
#                発火して鳴動が戻る（音・前景サービス・force_volume のかけ直し）ことを確かめ → STOP_RINGING → 音量が元へ戻る・
#                ringing.json が消える・見張りの予約が消えることを確かめる
#    forcestop … 鳴り始めて KILL_AFTER_S 秒後に am force-stop（見張りも消える＝戻せない）→ 下げたままの音量を確かめ →
#                デバッグ受信機の LIST で :seed_platform を起こす → 起動時の後始末（RingRecovery.onStartup）で ring_stopped(error)
#                と元の音量への戻りを確かめる
#  音は最小（common.sh の QUIET_ARGS。force_volume 0＝STREAM_ALARM の最小の段階・振動なし・漸増 60 秒）・安全弁 1 分。
#  鳴っている時間は殺すまでの数秒＋戻ってからの数秒（間に最長 20 秒の無音がある）。
#  結果: $SEED_DEVICE_TEST_RESULTS/<出力の名前>/result.env と stream.txt（logcat）・dumpsys の写し。
# ============================================================
set -u
source "$(dirname "$0")/common.sh"
MODE="${1:?kill か forcestop を指定してください}"
ID="${2:-t5}"
OUT="$RESULTS/${3:-t5_${MODE}}"
KILL_AFTER_S="${4:-3}"
mkdir -p "$OUT"
RES="$OUT/result.env"
: > "$RES"
# 予約までの秒（予約の結果を読んでから鳴るまでの余裕）
LEAD_S=15
# 鳴り始めを待つ上限（予定からの秒）
RING_WAIT_S=30
# 見張りで戻るのを待つ上限（見張りの 20 秒＋起こし直し）
RESTORE_WAIT_S=40
# 戻ってから止めるまでに音を確かめる秒
AFTER_RESTORE_S=3
# LIST の結果を待つ上限
LIST_WAIT_S=20
DE_DIR="/data/user_de/0/$PKG/files/seed_platform"

device_present || { put "$RES" skipped device_absent; exit 3; }
user_operating && { put "$RES" skipped user_operating; exit 1; }
record_alarm_volume_baseline
put "$RES" alarm_volume_baseline "$(sed -n '3p' "$ALARM_VOLUME_BASELINE_FILE")"
put "$RES" screen_at_start "$(screen_state | tr ' ' ';')"
start_stream "$OUT/stream.txt"
since="$(dev_since)"
schedule_quiet "$ID" "$LEAD_S" "W1-7_T5" > "$OUT/schedule_broadcast.txt"
wait_mark "$since" '\[debug\] alarm\.schedule' 30 || log "schedule の結果が来ない"
stream_since "$since" > "$OUT/.s.txt"
quiet_request_ok "$OUT/.s.txt" || { log "静かな引数が予約に入っていない → 取り消して中止"; put "$RES" aborted quiet_args_missing; cleanup_alarms > "$OUT/cleanup.txt"; stop_stream; exit 6; }
trigger_at="$(trigger_of "$OUT/.s.txt")"
put "$RES" trigger_at "$trigger_at"

# 1. 鳴り始めを待つ
if ! wait_mark "$since" "目覚まし $ID を鳴らし始めました" $((LEAD_S + RING_WAIT_S)); then
  log "鳴り始めが見えない"; put "$RES" ring_missing true; cleanup_alarms > "$OUT/cleanup.txt"; stop_stream; exit 4
fi
sleep "$KILL_AFTER_S"
put "$RES" alarm_playing_before_kill "$(alarm_playing)"
put "$RES" volume_before_kill "$(alarm_volume_speaker)"
dsh "run-as $PKG cat $DE_DIR/ringing.json" > "$OUT/1_ringing_json_before_kill.txt" 2>&1
dsh "dumpsys alarm" | grep -B2 -A12 'RING_WATCHDOG' > "$OUT/1_watchdog_alarm_before_kill.txt" || true
put "$RES" watchdog_armed_before_kill "$(watchdog_pending)"
pid_before="$(pid_platform)"
put "$RES" platform_pid_before "$pid_before"

# 2. 殺す
s_kill="$(dev_since)"
put "$RES" kill_device_ms "$(dev_now_ms)"
case "$MODE" in
  kill) dsh "run-as $PKG kill -9 $pid_before" > /dev/null 2>&1 ;;
  forcestop) dsh "am force-stop $PKG" > /dev/null 2>&1 ;;
  *) log "知らないモード $MODE"; exit 2 ;;
esac
sleep 2
put "$RES" alarm_playing_after_kill "$(alarm_playing)"
put "$RES" volume_after_kill "$(alarm_volume_speaker)"
put "$RES" platform_pid_after_kill "$(pid_platform)"
dsh "run-as $PKG cat $DE_DIR/ringing.json" > "$OUT/2_ringing_json_after_kill.txt" 2>&1

if [ "$MODE" = "kill" ]; then
  # 3a. 見張りで戻るのを待つ
  if wait_mark "$s_kill" "目覚まし $ID の鳴動を戻しました" "$RESTORE_WAIT_S"; then
    wait_mark "$s_kill" "目覚まし $ID を鳴らし始めました" 10 || true
    sleep "$AFTER_RESTORE_S"
    put "$RES" restored true
    put "$RES" alarm_playing_after_restore "$(alarm_playing)"
    put "$RES" volume_after_restore "$(alarm_volume_speaker)"
    put "$RES" platform_pid_after_restore "$(pid_platform)"
    dsh "run-as $PKG cat $DE_DIR/ringing.json" > "$OUT/3_ringing_json_after_restore.txt" 2>&1
    dsh "dumpsys alarm" | grep -B2 -A12 'RING_WATCHDOG' > "$OUT/3_watchdog_alarm_after_restore.txt" || true
    dsh "dumpsys activity services $PKG" > "$OUT/3_services_after_restore.txt"
  else
    put "$RES" restored false
  fi
  s_stop="$(dev_since)"
  ctl STOP_RINGING --es id "$ID" > "$OUT/stop_broadcast.txt" 2>&1
  wait_mark "$s_stop" '\[debug\] alarm\.stop_ringing' 20 || log "stop_ringing の結果が来ない"
  put "$RES" stop_line "$(stream_since "$s_stop" | grep -m1 -E '\[debug\] alarm\.stop_ringing' | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//' | cut -c1-300)"
else
  # 3b. 強制停止では見張りも消える。:seed_platform を起こして起動時の後始末を確かめる
  put "$RES" watchdog_after_forcestop "$(watchdog_pending)"
  s_list="$(dev_since)"
  ctl LIST > "$OUT/list_broadcast.txt" 2>&1
  wait_mark "$s_list" "目覚まし $ID の鳴動が終わりました（理由 error" "$LIST_WAIT_S" && put "$RES" startup_recorded_error true || put "$RES" startup_recorded_error false
  wait_mark "$s_list" "音量を元の" 10 || true
  sleep 1
  put "$RES" volume_after_background_start "$(alarm_volume_speaker)"
  dsh "run-as $PKG cat $DE_DIR/ringing.json" > "$OUT/4_ringing_json_after_background_start.txt" 2>&1
  # Android 17 は背面のプロセスからの音量の変更を無視するので、戻せなかった控えはアプリが前面に出たときに戻る（W1-7）。
  # FOREGROUND_RETRY=1 のとき、MainActivity を「目覚ましの起動」の偽の extra 付きで起こし（AC-6 の偽装の確かめを兼ねる）、
  # エンジンの接続（PlatformProvider.call）で戻ることを確かめる
  if [ "${FOREGROUND_RETRY:-0}" = "1" ]; then
    s_fg="$(dev_since)"
    dsh "am start -n $MAIN_ACTIVITY --es com.seedengine.runtime.platform.extra.LAUNCH '{\"kind\":\"alarm\",\"alarm_id\":\"spoof\"}'" > "$OUT/foreground_start.txt" 2>&1
    wait_mark "$s_fg" "起動理由: " 30 || true
    put "$RES" spoofed_launch_reason "$(stream_since "$s_fg" | grep -m1 -oE '起動理由: [^（(]*' | head -1)"
    wait_mark "$s_fg" "前のプロセスが変えたアラームの音量を元の [0-9]+ へ戻しました" 40 && put "$RES" foreground_retry_restored true || put "$RES" foreground_retry_restored false
    sleep 1
    put "$RES" volume_after_foreground "$(alarm_volume_speaker)"
    kill_app_processes
  fi
fi
sleep 2
put "$RES" alarm_playing_at_end "$(alarm_playing)"
put "$RES" volume_at_end "$(alarm_volume_speaker)"
put "$RES" ringing_json_at_end "$(dsh "run-as $PKG ls $DE_DIR" | tr '\n' ',')"
put "$RES" watchdog_at_end "$(watchdog_pending)"
verify_alarm_volume > "$OUT/alarm_volume_after.txt"; put "$RES" alarm_volume_check "$(tr '\n' ';' < "$OUT/alarm_volume_after.txt")"
cleanup_alarms > "$OUT/cleanup.txt"
put "$RES" cleanup "$(tr '\n' ';' < "$OUT/cleanup.txt" | cut -c1-400)"
stop_stream
# 流れ（予定時刻からの ms）
F="$OUT/stream.txt"
{
  echo "timeline（予定時刻 $trigger_at からの ms）:"
  tr -d '\r' < "$F" | grep -a -E "SEEDPlatform|am_proc_start: .*$PKG|am_proc_died: .*$PKG|am_kill: .*$PKG|Background started FGS: .*$PKG|am_foreground_service_(start|stop): .*$PKG|volume_changed: .*$PKG|notification_(enqueue|canceled): .*$PKG" \
    | awk -v t="$trigger_at" '{ printf "%+7d ", $1 * 1000 - t; $1=""; $2=""; $3=""; print }' | cut -c1-260
} > "$OUT/timeline.txt"
cat "$RES"
