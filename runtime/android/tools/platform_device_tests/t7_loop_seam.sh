#!/usr/bin/env bash
# ============================================================
#  T7: 鳴動の音のループの継ぎ目（W1-7。W1-4b の G-3）
#
#    t7_loop_seam.sh [予約 ID] [出力の名前] [鳴らす秒]
#
#  既定の音（2.22 s の WAV）を RING_S 秒鳴らしてから dumpsys media.audio_flinger を写し、AudioFlinger のトラックの記録
#  （「AT::add」「AT::remove」の行。状態 A=動いている・I=止まった〈足りなくなった〉・T=終わった）のうち、このアプリの uid の行を抜き出す。
#  setLooping のころ（W1-4b）は継ぎ目ごとに「AT::remove … I」→ 約 57〜69 ms 後に「AT::add … A」が並んだ（途切れ）。
#  継ぎ目が無ければ、鳴動の間の行は最初の add と止めたときの remove（T）だけになる。
#  音は最小（schedule_quiet）。結果: $SEED_DEVICE_TEST_RESULTS/<出力の名前>/result.env・track_log.txt・audio_flinger.txt。
# ============================================================
set -u
source "$(dirname "$0")/common.sh"
ID="${1:-t7}"
OUT="$RESULTS/${2:-t7_loop_seam}"
RING_S="${3:-12}"
mkdir -p "$OUT"
RES="$OUT/result.env"
: > "$RES"
# 予約までの秒
LEAD_S=12
# 既定の音の長さ（ミリ秒。res/raw/seed_alarm_default.wav。継ぎ目の数の見込みに使う）
DEFAULT_TONE_MS=2220

device_present || { put "$RES" skipped device_absent; exit 3; }
user_operating && { put "$RES" skipped user_operating; exit 1; }
record_alarm_volume_baseline
APP_UID="$(dsh "dumpsys package $PKG" | grep -m1 -oE '(userId|appId)=[0-9]+' | sed -E 's/(userId|appId)=//')"
put "$RES" app_uid "$APP_UID"
start_stream "$OUT/stream.txt"
since="$(dev_since)"
# この回より前のトラックの記録（dumpsys の記録は起動からのものが残る）を除くための、端末の現地時刻（記録と同じ「MM-DD HH:MM:SS.mmm」）
start_local="$(dsh 'date "+%m-%d %H:%M:%S.%3N"')"
put "$RES" start_local "$start_local"
schedule_quiet "$ID" "$LEAD_S" "W1-7_T7" > "$OUT/schedule_broadcast.txt"
wait_mark "$since" '\[debug\] alarm\.schedule' 30 || log "schedule の結果が来ない"
stream_since "$since" > "$OUT/.s.txt"
quiet_request_ok "$OUT/.s.txt" || { put "$RES" aborted quiet_args_missing; cleanup_alarms > "$OUT/cleanup.txt"; stop_stream; exit 6; }
if ! wait_mark "$since" "目覚まし $ID を鳴らし始めました" $((LEAD_S + 30)); then
  put "$RES" ring_missing true; cleanup_alarms > "$OUT/cleanup.txt"; stop_stream; exit 4
fi
put "$RES" loop_line "$(stream_since "$since" | grep -m1 -E '鳴動の音 .* のループ' | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//')"
sleep "$RING_S"
put "$RES" alarm_playing "$(alarm_playing)"
dsh "dumpsys media.audio_flinger" > "$OUT/audio_flinger.txt"
s_stop="$(dev_since)"
ctl STOP_RINGING --es id "$ID" > "$OUT/stop_broadcast.txt" 2>&1
wait_mark "$s_stop" '\[debug\] alarm\.stop_ringing' 20 || log "stop_ringing の結果が来ない"
sleep 1
dsh "dumpsys media.audio_flinger" > "$OUT/audio_flinger_after_stop.txt"
# このアプリの uid のトラックの記録（止めた後の写しに、鳴動の間の add / remove が全部残っている）
grep -a -E "AT::(add|remove)" "$OUT/audio_flinger_after_stop.txt" | grep -E "/ +$APP_UID " | sort -u   | awk -v s="$start_local" '($1 " " $2) >= s' > "$OUT/track_log.txt"
put "$RES" track_adds "$(grep -c 'AT::add' "$OUT/track_log.txt")"
put "$RES" track_removes_idle "$(grep -E 'AT::remove' "$OUT/track_log.txt" | awk '{ for (i = 1; i <= NF; i++) if ($i ~ /^[AITS]$/ && $(i-1) ~ /^[0-9]+$/) { print $i; break } }' | grep -c '^I$')"
put "$RES" expected_seams "$(( RING_S * 1000 / DEFAULT_TONE_MS ))"
put "$RES" volume_at_end "$(alarm_volume_speaker)"
verify_alarm_volume > "$OUT/alarm_volume_after.txt"; put "$RES" alarm_volume_check "$(tr '\n' ';' < "$OUT/alarm_volume_after.txt")"
cleanup_alarms > "$OUT/cleanup.txt"
stop_stream
cat "$RES"
echo "--- track_log ---"
cut -c1-120 "$OUT/track_log.txt"
