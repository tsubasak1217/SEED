#!/usr/bin/env bash
# ============================================================
#  SEED.Platform の実機の試験の共通部（Git Bash で source して使う。W1-4b で作り、W1-7 で整えた）。
#  W1-0 のスパイクの scripts/common.sh を SEED の APK（com.wakeorpay.seed・DebugPlatformReceiver・PlatformEntry）へ向け直したもの。
#
#  使い方: 結果の置き場（リポジトリの外）を環境変数で渡してから source する。
#    export SEED_DEVICE_TEST_RESULTS=/c/…/results   # 生ログは私物の端末の他のアプリの情報を含むのでリポジトリに入れない
#    SERIAL・ADB・PKG は環境変数で変えられる（既定は Pixel 6a・SDK の adb・Wake or Pay のデバッグ版）
#
#  端末: Pixel 6a（2B011JEGR02535。W1-4b は Android 16、W1-7 の T4 の再起動で Android 17 に更新された）。約束:
#    ・端末の設定・音量は変えない（Doze の試験の battery unplug / deviceidle force-idle は必ず戻す）
#    ・adb kill-server・logcat -c・pm grant/revoke・appops set はしない
#    ・予約は必ず max_ring_minutes 1（端末が外れても 1 分で止まる）。音は最小（schedule_quiet）
#    ・利用者が使い始めたら（画面が点き・ロックが外れ・前面がランチャーでも自アプリでもない）予約を取り消して中止し、
#      2 分おきに最大 20 分待つ
#    ・端末が adb devices に無ければ、待ち続けずにその試験を「未実施（端末が外れた）」にする
#  デバッグ受信機の命令（SCHEDULE / CANCEL_ALL / STOP_RINGING / GET_RINGING / LIST / PING / EMIT_TEST_EVENT）は ctl に集約する。
#  受信機は送り手に android.permission.DUMP を求める（W1-7）。adb の shell は持つので、この ctl からは届く。
# ============================================================
export MSYS_NO_PATHCONV=1
ADB="${ADB:-C:/Users/k023g/AppData/Local/Android/Sdk/platform-tools/adb.exe}"
SERIAL="${SERIAL:-2B011JEGR02535}"
PKG="${PKG:-com.wakeorpay.seed}"
PLATFORM_PROC="$PKG:seed_platform"
# 結果の置き場（必須）。生ログは私物端末の他のアプリの情報を含むので、リポジトリの外を指す（W1-7 で固定のパスをやめた）
RESULTS="${SEED_DEVICE_TEST_RESULTS:?SEED_DEVICE_TEST_RESULTS に結果の置き場（リポジトリの外のディレクトリ）を指定してください}"
mkdir -p "$RESULTS"
LAUNCHER_PKG="com.google.android.apps.nexuslauncher"
TAG="SEEDPlatform"
RECEIVER="$PKG/com.seedengine.runtime.platform.DebugPlatformReceiver"
ACTION_PREFIX="com.seedengine.runtime.platform"
MAIN_ACTIVITY="$PKG/com.seedengine.runtime.MainActivity"
ENTRY_ALIAS="$PKG/com.seedengine.runtime.platform.PlatformEntry"
WAIT_STEP_S=120
WAIT_MAX_S=1200
# 停止済み（強制停止の後）のパッケージにも放送を届ける（FLAG_INCLUDE_STOPPED_PACKAGES）
FLAG_INCLUDE_STOPPED=0x20

# adb の 1 回の命令（端末が外れても待ち続けないよう上限時間を付ける。logcat の流しは adb_stream を使う）
adbs() { timeout "${ADB_TIMEOUT_S:-60}" "$ADB" -s "$SERIAL" "$@"; }
adb_stream() { "$ADB" -s "$SERIAL" "$@"; }
# 端末のシェル（CR を落とす）
dsh() { adbs shell "$@" | tr -d '\r'; }
log() { echo "[$(date '+%H:%M:%S')] $*"; }

# 端末が adb devices に「device」で見えるか
device_present() { "$ADB" devices | tr -d '\r' | grep -qE "^$SERIAL[[:space:]]+device$"; }

# 端末の時刻（UTC epoch ミリ秒）と、logcat -T に渡す形（epoch 秒.ミリ秒。-v epoch と同じ）
# 端末の mksh の算術は 32 ビットなので、ナノ秒の文字列を取ってホスト側（64 ビット）で割る
dev_now_ms() { local ns; ns="$(dsh 'date +%s%N')"; echo $(( ns / 1000000 )); }
dev_since() { dsh 'date +%s.%3N'; }

# DebugPlatformReceiver へ命令する（-n で部品を指定。停止状態でも届く）
ctl() {
  local action="$1"; shift
  adbs shell am broadcast -f "$FLAG_INCLUDE_STOPPED" -n "$RECEIVER" -a "$ACTION_PREFIX.$action" "$@" | tr -d '\r'
}

pid_main() { dsh "pidof $PKG" || true; }
pid_platform() { dsh "pidof $PLATFORM_PROC" || true; }

wakefulness() { dsh "dumpsys power" | grep -E '^  mWakefulness=' | head -1 | sed 's/.*=//'; }
keyguard_showing() { dsh "dumpsys window" | grep -E 'isKeyguardShowing=' | head -1 | sed 's/.*=//'; }
current_focus() { dsh "dumpsys window" | grep -E '^  mCurrentFocus=' | head -1 | sed 's/^  mCurrentFocus=//'; }
screen_state() {
  local w; w="$(dsh 'dumpsys window')"
  echo "wakefulness=$(wakefulness) keyguard_showing=$(echo "$w" | grep -m1 -oE 'isKeyguardShowing=[a-z]+' | sed 's/.*=//') keyguard_occluded=$(echo "$w" | grep -m1 -oE 'mKeyguardOccluded=[a-z]+' | sed 's/.*=//') focus=$(echo "$w" | grep -m1 -E '^  mCurrentFocus=' | sed 's/^  mCurrentFocus=//; s/ /_/g')"
}

# 利用者が端末を使っているか（点灯・ロック解除・前面がランチャーでも自アプリでもない）
user_operating() {
  [ "$(wakefulness)" = "Awake" ] || return 1
  [ "$(keyguard_showing)" = "true" ] && return 1
  local focus; focus="$(current_focus)"
  case "$focus" in *"$LAUNCHER_PKG"*|*"$PKG/"*|*"NotificationShade"*|"null"|"") return 1 ;; esac
  return 0
}

# 画面オフかロック中か（冷えたロック画面の試験の前提）
locked_or_off() {
  [ "$(wakefulness)" != "Awake" ] && return 0
  [ "$(keyguard_showing)" = "true" ] && return 0
  return 1
}

# 操作中なら 2 分おきに最大 20 分待つ。待ち切れない・端末が外れたら 1
wait_user_idle() {
  local waited=0
  while :; do
    device_present || { log "端末が見えない"; return 1; }
    user_operating || return 0
    if [ "$waited" -ge "$WAIT_MAX_S" ]; then
      log "20 分待っても操作中のため中止"
      return 1
    fi
    log "利用者が操作中（前面: $(current_focus)）。${WAIT_STEP_S}s 待つ（累計 ${waited}s）"
    sleep "$WAIT_STEP_S"
    waited=$((waited + WAIT_STEP_S))
  done
}

# logcat を試験の間ずっとファイルへ流す（端末の main バッファは 256 KiB で約 5 分で古い行が消えるため。F-5）
STREAM_FILE=""
STREAM_PID=""
STREAM_SINCE_LIST=""
KILL_STREAM_PS1="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -W 2>/dev/null)/kill_stream.ps1"
# 流しは adb.exe を直接バックグラウンドで起こす（関数を & で起こすと、kill してもサブシェルだけが死に adb.exe が残った。
# 11:43〜11:50 の 3 本が漏れて後の回の行を書き足した）。止めるときは -T の値で adb.exe を探して確実に止める（kill_stream.ps1）
start_stream() {
  STREAM_FILE="$1"
  : > "$STREAM_FILE"
  local since; since="$(dev_since)"
  "$ADB" -s "$SERIAL" logcat -v epoch -b main,system,crash,events -T "$since" > "$STREAM_FILE" 2>/dev/null &
  STREAM_PID=$!
  STREAM_SINCE_LIST="$STREAM_SINCE_LIST $since"
  sleep 1
}
stop_stream() {
  if [ -n "$STREAM_PID" ]; then
    kill "$STREAM_PID" 2>/dev/null
    wait "$STREAM_PID" 2>/dev/null
  fi
  local s
  for s in $STREAM_SINCE_LIST; do
    pwsh -NoProfile -File "$KILL_STREAM_PS1" -Since "$s" > /dev/null 2>&1
  done
  STREAM_PID=""
  STREAM_SINCE_LIST=""
}

# 流している logcat が途切れていたら（USB の抜き差し等）、端末が見えるときだけ最後の行の時刻から流し直す
ensure_stream() {
  if [ -n "$STREAM_FILE" ] && [ -n "$STREAM_PID" ] && ! kill -0 "$STREAM_PID" 2>/dev/null; then
    device_present || return 1
    local last
    last="$(tail -1 "$STREAM_FILE" | awk '{print $1}')"
    echo "# ---- logcat の流しを再開（途切れ: 最後の行 ${last:-不明}） ----" >> "$STREAM_FILE"
    local since="${last:-$(dev_since)}"
    "$ADB" -s "$SERIAL" logcat -v epoch -b main,system,crash,events -T "$since" >> "$STREAM_FILE" 2>/dev/null &
    STREAM_PID=$!
    STREAM_SINCE_LIST="$STREAM_SINCE_LIST $since"
    log "logcat の流しを再開した（最後の行 ${last:-不明}）"
  fi
  return 0
}

# 流しているファイルのうち since（epoch 秒.ミリ秒）以降の行
stream_since() { tr -d '\r' < "$STREAM_FILE" | awk -v s="$1" '($1 + 0) >= (s + 0)'; }

# logcat の目印を待つ: wait_mark <since> <正規表現> <秒>。見つかれば 0。端末が外れたら 2
wait_mark() {
  local since="$1" pattern="$2" timeout="$3" t=0
  while [ "$t" -lt "$timeout" ]; do
    ensure_stream || return 2
    if stream_since "$since" | grep -qE "$pattern"; then
      return 0
    fi
    sleep 1
    t=$((t + 1))
  done
  return 1
}

# 最初に一致した行の epoch 秒（小数）→ ミリ秒（整数）。無ければ空
first_ms() { local line; line="$(grep -m1 -E "$2" "$1")"; [ -n "$line" ] && echo "$line" | awk '{ printf "%d\n", $1 * 1000 + 0.5 }'; }

# 予約の返答から予定時刻（trigger_at_utc_ms）を取る
trigger_of() { grep -E "\[debug\] alarm\.schedule" "$1" | grep -m1 -oE '"trigger_at_utc_ms":[0-9]+' | tail -1 | sed 's/.*://'; }

# 証拠の保存: 状態・alarm・services・audio・audio_flinger・通知
snap() {
  local dir="$1" label="$2"
  mkdir -p "$dir"
  {
    echo "# $label device_ms=$(dev_now_ms) host=$(date '+%F %T')"
    echo "pid_main=$(pid_main) pid_platform=$(pid_platform)"
    screen_state
    dsh "dumpsys deviceidle" | grep -E 'mState=|mLightState=|mForceIdle=|mScreenOn=|mCharging='
    dsh "dumpsys battery" | grep -E 'AC powered|USB powered|level:|status:'
    dsh "ps -A -o PID,USER,NAME" | grep -F "$PKG" || true
  } > "$dir/${label}_state.txt" 2>&1
  dsh "dumpsys alarm" > "$dir/.alarm_full.tmp"
  {
    grep -E 'Next alarm clock information|Next wakeup alarm|Next non-wakeup alarm' "$dir/.alarm_full.tmp"
    grep -n -B1 -A10 -E "Alarm\{[^}]* $PKG\}" "$dir/.alarm_full.tmp" || echo "($PKG の保留中の予約は dumpsys alarm に無い)"
  } > "$dir/${label}_dumpsys_alarm.txt"
  rm -f "$dir/.alarm_full.tmp"
  dsh "dumpsys activity services $PKG" > "$dir/${label}_dumpsys_services.txt"
  { dsh "dumpsys audio" | grep -E 'AudioPlaybackConfiguration' || echo "(再生中の設定なし)"; } > "$dir/${label}_audio_playback.txt"
  dsh "dumpsys media.audio_flinger" > "$dir/${label}_audio_flinger.txt"
}

# 鳴動中の USAGE_ALARM の再生がこのアプリにあるか（dumpsys audio の AudioPlaybackConfiguration の state:started の数）
APP_UID=""
alarm_playing() {
  if [ -z "$APP_UID" ]; then
    APP_UID="$(dsh "dumpsys package $PKG" | grep -m1 -oE '(userId|appId)=[0-9]+' | sed -E 's/(userId|appId)=//')"
  fi
  dsh "dumpsys audio" | grep -E 'AudioPlaybackConfiguration' | grep -E "u/pid:${APP_UID}/" | grep -E 'USAGE_ALARM' | grep -cE 'state:started' || true
}

# AudioFlinger の Active なトラック（このアプリの uid の行を数える。表の形は版で変わりうるので行ごと残す）
flinger_active_lines() { dsh "dumpsys media.audio_flinger" | grep -E "^\s+(Yes|No)?\s*[0-9]+\s+.*\s${APP_UID}\s" || true; }

# 結果の key=value を書き足す
put() { echo "$2=$3" >> "$1"; }

# 保留中の（まだ配信されていない）予約の数（Alarm{<id> … <pkg>} の id の種類を数える。同じ予約が「Next wake from idle」にも
# 出るので行の数ではない〈W1-7 の T4 で 1 件を 2 と数えた〉。統計の欄は数えない）。見張りの予約（RING_WATCHDOG）も数に入る
pending_alarms() { dsh "dumpsys alarm" | grep -oE "Alarm\{[0-9a-f]+ [^}]* $PKG\}" | awk '{print $1}' | sort -u | wc -l | tr -d ' '; }

# 保留中の鳴動の見張りの予約（W1-7。RingWatchdog）の数（tag の行の前の行が Alarm{…} のものだけ。統計の欄の tag は数えない）
watchdog_pending() { dsh "dumpsys alarm" | grep -B1 -E 'tag=\*walarm\*:com\.seedengine\.runtime\.platform\.action\.RING_WATCHDOG' | grep -cE 'Alarm\{' || true; }

# 両プロセスを落とす（am kill → 残れば run-as で kill -9。force-stop は予約を消すので使わない）
kill_app_processes() {
  dsh "am kill $PKG" > /dev/null 2>&1
  sleep 2
  local p
  for p in $(pid_main) $(pid_platform); do
    dsh "run-as $PKG kill -9 $p" > /dev/null 2>&1
  done
  sleep 1
}

# 後始末: 予約を全部取り消し、鳴動が残っていないことを確かめる（結果の行を返す）
cleanup_alarms() {
  local s; s="$(dev_since)"
  ctl STOP_RINGING > /dev/null
  ctl CANCEL_ALL > /dev/null
  ctl GET_RINGING > /dev/null
  wait_mark "$s" '\[debug\] alarm\.get_ringing' 20 || true
  stream_since "$s" | grep -E '\[debug\] alarm\.(stop_ringing|cancel_all|get_ringing)' | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//'
  echo "pending_alarms=$(pending_alarms)"
}

# 画面を消す（ロック中で利用者が使っていないときだけ。KEYCODE_SLEEP は画面を消すだけで点けない）。
# この端末は「充電中は画面を消さない」（stay_on_while_plugged_in=15）なので、目覚ましが点けた画面は利用者が消すまで点いたまま。
# 利用者が置いた状態（画面オフ・ロック）へ戻し、「画面オフ・ロック中」の試験の前提を作るために使う。設定は変えない。
# キーの送信は利用者の許可が要るので、ALLOW_KEYEVENT_SLEEP=1 のときだけ送る（W1-7 は許可が無いので送らない。無ければ 1 を返す）
sleep_screen_if_locked() {
  [ "$(wakefulness)" = "Awake" ] || return 0
  [ "$(keyguard_showing)" = "true" ] || return 1
  user_operating && return 1
  [ "${ALLOW_KEYEVENT_SLEEP:-0}" = "1" ] || return 1
  dsh "input keyevent KEYCODE_SLEEP" > /dev/null 2>&1
  local t=0
  while [ "$(wakefulness)" = "Awake" ] && [ "$t" -lt 10 ]; do sleep 1; t=$((t + 1)); done
  [ "$(wakefulness)" != "Awake" ]
}

# ---- 音を小さくする（利用者の依頼。2026-09-27 11:58 以降のすべての予約）----
# force_volume 0.0（AlarmStreamVolume が STREAM_ALARM の最小の段階〈この端末は 1/7〉に丸める。止めたら元へ戻す）・振動なし・漸増 60 秒
QUIET_ARGS="--ef force_volume 0.0 --ez vibrate false --ef fade_in_seconds 60"
# 静かな予約（音は最小・振動なし・漸増 60 秒・安全弁 1 分）: schedule_quiet <ID> <今からの秒> <題（空白を入れない。G-9）>
schedule_quiet() {
  ctl SCHEDULE --ei seconds "$2" --es id "$1" --ei max_ring_minutes 1 --es title "$3" --es body "$1" $QUIET_ARGS
}
# 予約の行（[debug] alarm.schedule の request）に静かな引数が入っているか
quiet_request_ok() {
  local line; line="$(grep -m1 -E '\[debug\] alarm\.schedule' "$1")"
  echo "$line" | grep -qE '"force_volume":0(\.0)?[,}]' || return 1
  echo "$line" | grep -qE '"vibrate":false' || return 1
  echo "$line" | grep -qE '"fade_in_seconds":60(\.0)?[,}]' || return 1
  return 0
}
# アラームの音量（dumpsys audio の STREAM_ALARM の Current の行と streamVolume）
alarm_volume_line() { dsh "dumpsys audio" | grep -E -A8 '^- STREAM_ALARM:' | grep -E '^   (Current|streamVolume):' | tr -s ' ' | tr '\n' ' '; }
alarm_volume_speaker() { dsh "dumpsys audio" | grep -E -A8 '^- STREAM_ALARM:' | grep -m1 -oE '2 \(speaker\): [0-9]+' | grep -oE '[0-9]+$'; }
# 試験の前の値（結果の置き場ごとに 1 回だけ record_alarm_volume_baseline で残す。1 行目は見出し・2 行目は
# alarm_volume_line・3 行目は speaker の段階）
ALARM_VOLUME_BASELINE_FILE="$RESULTS/alarm_volume_baseline.txt"
record_alarm_volume_baseline() {
  [ -s "$ALARM_VOLUME_BASELINE_FILE" ] && return 0
  {
    echo "# STREAM_ALARM の試験の前の値 device_ms=$(dev_now_ms) host=$(date '+%F %T')"
    echo "$(alarm_volume_line)"
    echo "$(alarm_volume_speaker)"
  } > "$ALARM_VOLUME_BASELINE_FILE"
}
# 基準と今の値を比べ、違えば元の値へ戻す（利用者の依頼に基づく例外。元の値以外にはしない）。結果の行を返す。
# 比べるのはスピーカーの段階だけ（Current の行は出力の機器の一覧〈bt_a2dp など〉が再起動で変わるので、行ごとは比べない。W1-7 の T4）
verify_alarm_volume() {
  local base_line base_speaker now_line now_speaker
  base_line="$(sed -n '2p' "$ALARM_VOLUME_BASELINE_FILE")"
  base_speaker="$(sed -n '3p' "$ALARM_VOLUME_BASELINE_FILE")"
  now_line="$(alarm_volume_line)"
  now_speaker="$(alarm_volume_speaker)"
  if [ -n "$base_speaker" ] && [ "$now_speaker" = "$base_speaker" ]; then
    echo "alarm_volume=same speaker=$now_speaker"
    return 0
  fi
  echo "alarm_volume=changed now=[$now_line] base=[$base_line]"
  if [ -n "$base_speaker" ] && [ "$now_speaker" != "$base_speaker" ]; then
    # Android 17 の Pixel 6a では「cmd media_session volume --stream 4 --set」が「will set」と出すだけで変わらなかった（W1-7。
    # AudioHardening）。AudioService の shell 命令「cmd audio set-volume」（system_server の中で setStreamVolume）で戻す
    dsh "cmd audio set-volume 4 $base_speaker" > /dev/null 2>&1
    if [ "$(alarm_volume_speaker)" != "$base_speaker" ]; then
      dsh "cmd media_session volume --stream 4 --set $base_speaker" > /dev/null 2>&1
    fi
    echo "alarm_volume_restored_to=$base_speaker now=[$(alarm_volume_line)]"
  fi
}
