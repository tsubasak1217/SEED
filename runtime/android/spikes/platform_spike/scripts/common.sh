#!/usr/bin/env bash
# ============================================================
#  W1-0 スパイクの計測スクリプトの共通部（Git Bash で source して使う）
#
#  端末: Pixel 6a（2B011JEGR02535・Android 16）。約束:
#    ・端末の設定・音量は変えない（dumpsys battery unplug / deviceidle force-idle は試験の後で必ず戻す）
#    ・adb reboot はしない（t7 は手順だけ。許可の印が無ければ実行しない）
#    ・input keyevent などの端末操作はしない
#    ・利用者が端末を操作中（画面が点き・ロック画面でなく・前面がランチャーでも自アプリでもない）なら 2 分おきに最大 20 分待つ
# ============================================================
export MSYS_NO_PATHCONV=1
ADB="${ADB:-C:/Users/k023g/AppData/Local/Android/Sdk/platform-tools/adb.exe}"
SERIAL="${SERIAL:-2B011JEGR02535}"
PKG="com.seedengine.platformspike"
PLATFORM_PROC="$PKG:seed_platform"
SPIKE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RESULTS="$SPIKE_ROOT/results"
APK="$SPIKE_ROOT/app/build/outputs/apk/debug/app-debug.apk"
LAUNCHER_PKG="com.google.android.apps.nexuslauncher"
TAG="SEEDPlatformSpike"
WAIT_STEP_S=120
WAIT_MAX_S=1200
# 停止済み（強制停止の後）のパッケージにも放送を届ける（FLAG_INCLUDE_STOPPED_PACKAGES）
FLAG_INCLUDE_STOPPED=0x20

adbs() { "$ADB" -s "$SERIAL" "$@"; }
# 端末のシェル（CR を落とす）
dsh() { adbs shell "$@" | tr -d '\r'; }
log() { echo "[$(date '+%H:%M:%S')] $*"; }

# 端末の時刻（UTC epoch ミリ秒）と、logcat -T に渡す形（epoch 秒.ミリ秒。-v epoch と同じ）
# 端末の mksh の算術は 32 ビットなので、ナノ秒の文字列を取ってホスト側（64 ビット）で割る
dev_now_ms() { local ns; ns="$(dsh 'date +%s%N')"; echo $(( ns / 1000000 )); }
dev_since() { dsh 'date +%s.%3N'; }

# エンジンの代役（メインプロセスの DebugControlReceiver）へ命令する
ctl() {
  local action="$1"; shift
  adbs shell am broadcast -f "$FLAG_INCLUDE_STOPPED" -n "$PKG/.DebugControlReceiver" -a "$PKG.$action" "$@" | tr -d '\r'
}

pid_main() { dsh "pidof $PKG" || true; }
pid_platform() { dsh "pidof $PLATFORM_PROC" || true; }

wakefulness() { dsh "dumpsys power" | grep -E '^  mWakefulness=' | head -1 | sed 's/.*=//'; }
keyguard_showing() { dsh "dumpsys window" | grep -E 'isKeyguardShowing=' | head -1 | sed 's/.*=//'; }
top_activity() {
  dsh "dumpsys activity activities" | grep -E 'topResumedActivity=|ResumedActivity:' | head -1 \
    | sed -E 's/.*ActivityRecord\{[^ ]+ [^ ]+ ([^ ]+).*/\1/'
}
screen_state() { echo "wakefulness=$(wakefulness) keyguard_showing=$(keyguard_showing) top=$(top_activity)"; }

# 利用者が端末を操作中か
user_operating() {
  [ "$(wakefulness)" = "Awake" ] || return 1
  [ "$(keyguard_showing)" = "true" ] && return 1
  local top; top="$(top_activity)"
  case "$top" in *"$LAUNCHER_PKG"*|*"$PKG"*|"") return 1 ;; esac
  return 0
}

# 操作中なら 2 分おきに最大 20 分待つ。待ち切れなければ 1
wait_user_idle() {
  local waited=0
  while user_operating; do
    if [ "$waited" -ge "$WAIT_MAX_S" ]; then
      log "20 分待っても操作中のため中止"
      return 1
    fi
    log "利用者が操作中（前面: $(top_activity)）。${WAIT_STEP_S}s 待つ（累計 ${waited}s）"
    sleep "$WAIT_STEP_S"
    waited=$((waited + WAIT_STEP_S))
  done
  return 0
}

# logcat を試験の間ずっとファイルへ流す（端末の main バッファは 256 KiB しかなく、数分で古い行が消えるため）
STREAM_FILE=""
STREAM_PID=""
start_stream() {
  STREAM_FILE="$1"
  : > "$STREAM_FILE"
  adbs logcat -v epoch -b main,system,crash,events -T "$(dev_since)" > "$STREAM_FILE" 2>/dev/null &
  STREAM_PID=$!
  sleep 1
}
stop_stream() {
  if [ -n "$STREAM_PID" ]; then
    kill "$STREAM_PID" 2>/dev/null
    wait "$STREAM_PID" 2>/dev/null
  fi
  STREAM_PID=""
}

# 流している logcat が USB の抜き差し等で途切れていたら、最後の行の時刻から流し直す（同じファイルへ追記）
ensure_stream() {
  if [ -n "$STREAM_FILE" ] && [ -n "$STREAM_PID" ] && ! kill -0 "$STREAM_PID" 2>/dev/null; then
    local last
    last="$(tail -1 "$STREAM_FILE" | tr -d '' | awk '{print $1}')"
    adbs wait-for-device
    echo "# ---- logcat の流しを再開（途切れ: 最後の行 ${last:-不明}） ----" >> "$STREAM_FILE"
    adbs logcat -v epoch -b main,system,crash,events -T "${last:-$(dev_since)}" >> "$STREAM_FILE" 2>/dev/null &
    STREAM_PID=$!
    log "logcat の流しを再開した（最後の行 ${last:-不明}）"
  fi
}

# logcat の目印を待つ: wait_mark <since> <正規表現> <秒>。見つかれば 0（流しているファイルがあればそれを見る）
wait_mark() {
  local since="$1" pattern="$2" timeout="$3" t=0
  while [ "$t" -lt "$timeout" ]; do
    ensure_stream
    if [ -n "$STREAM_FILE" ] && [ -f "$STREAM_FILE" ]; then
      if tr -d '\r' < "$STREAM_FILE" | awk -v s="$since" '($1 + 0) >= (s + 0)' | grep -qE "$pattern"; then
        return 0
      fi
    elif adbs logcat -d -v epoch -s "$TAG" -T "$since" 2>/dev/null | tr -d '\r' | grep -qE "$pattern"; then
      return 0
    fi
    sleep 1
    t=$((t + 1))
  done
  return 1
}

# 直近の目印（since 以降の SEEDPlatformSpike の行）をファイルへ
recent_marks() {
  local since="$1" file="$2"
  if [ -n "$STREAM_FILE" ] && [ -f "$STREAM_FILE" ]; then
    tr -d '\r' < "$STREAM_FILE" | awk -v s="$since" '($1 + 0) >= (s + 0)' | grep -F "$TAG" > "$file" || true
  else
    adbs logcat -d -v epoch -s "$TAG" -T "$since" 2>/dev/null | tr -d '\r' > "$file"
  fi
}

# 目印の行（最初の 1 行）と、その中の key=value の値
mark_line() { grep -E "MARK $2( |$)" "$1" | head -1; }
mark_field() { mark_line "$1" "$2" | grep -oE "(^| )$3=[^ ]*" | head -1 | sed -E "s/^ ?$3=//"; }

# 証拠の保存: 状態・alarm・services・audio・audio_flinger
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
    grep -n -A10 -F "$PKG" "$dir/.alarm_full.tmp" || echo "($PKG は dumpsys alarm に無い)"
  } > "$dir/${label}_dumpsys_alarm.txt"
  rm -f "$dir/.alarm_full.tmp"
  dsh "dumpsys activity services $PKG" > "$dir/${label}_dumpsys_services.txt"
  { dsh "dumpsys audio" | grep -E 'AudioPlaybackConfiguration' || echo "(再生中の設定なし)"; } > "$dir/${label}_audio_playback.txt"
  dsh "dumpsys media.audio_flinger" > "$dir/${label}_audio_flinger.txt"
}

# logcat の保存（全体と、スパイク関係の抜き出し）。流していればそれを止めて使う
save_logcat() {
  local dir="$1" label="$2" since="$3"
  mkdir -p "$dir"
  if [ -n "$STREAM_FILE" ] && [ -f "$STREAM_FILE" ]; then
    stop_stream
    tr -d '\r' < "$STREAM_FILE" > "$dir/${label}_logcat_all.txt"
    rm -f "$STREAM_FILE"
    STREAM_FILE=""
  else
    adbs logcat -d -v epoch -b main,system,crash,events -T "$since" 2>/dev/null | tr -d '\r' > "$dir/${label}_logcat_all.txt"
  fi
  grep -E "$TAG|ActivityTaskManager: (Displayed|START u0)|platformspike|KEYGUARD_OCCLUDE|device_idle_wake_from_idle|Background started FGS"     "$dir/${label}_logcat_all.txt" > "$dir/${label}_logcat_spike.txt" || true
}

# 鳴動中の USAGE_ALARM の再生があるか（dumpsys audio の AudioPlaybackConfiguration）
alarm_playing() {
  # Android 16 の dumpsys package は userId= ではなく appId= で出す（両方を受ける）
  local uid; uid="$(dsh "dumpsys package $PKG" | grep -m1 -oE '(userId|appId)=[0-9]+' | sed -E 's/(userId|appId)=//')"
  dsh "dumpsys audio" | grep -E 'AudioPlaybackConfiguration' | grep -E "u/pid:${uid}/" | grep -E 'USAGE_ALARM' | grep -cE 'state:started' || true
}

# 結果の key=value を書き足す
put() { echo "$2=$3" >> "$1"; }

# 保留中の（まだ配信されていない）予約の数。dumpsys alarm の統計の欄は数えない（Alarm{... <pkg>} の行だけ）
pending_alarms() { dsh "dumpsys alarm" | grep -cE "Alarm\{[^}]* $PKG\}" || true; }
