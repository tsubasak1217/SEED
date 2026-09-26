#!/usr/bin/env bash
# ============================================================
#  results/<試験>/ の生ログ（logcat・dumpsys）から、自パッケージの要の行だけを抜いて evidence.txt を作る。
#  summary.md（summarize.sh）はこの抜粋を埋め込む。生ログはリポジトリに入れない（私物端末の他のアプリの情報を含むため）。
#  端末には触らない（results/ だけを読む）。
# ============================================================
set -u
source "$(dirname "$0")/common.sh"
# 残す行（自パッケージ・自タグの行のうち、計測の根拠になるもの）
KEY='MARK |BENCH |device_idle_wake_from_idle|am_proc_start|Background started FGS|am_foreground_service_(start|stop)|sysui_fullscreen_notification|sysui_heads_up_status|type = KEYGUARD_OCCLUDE|Displayed |wm_activity_launch_time|AudioHardening|Force stopping|wm_task_removed|am_kill|Killing [0-9]+:com.seedengine'
MAX_COLS=300

# 自パッケージの行だけ残し、長い行を切る
own_lines() { grep -E "$KEY" "$@" 2>/dev/null | grep -E "platformspike|$TAG" | cut -c1-"$MAX_COLS"; }

for dir in "$RESULTS"/*/; do
  [ -d "$dir" ] || continue
  dir="${dir%/}"
  name="$(basename "$dir")"
  out="$dir/evidence.txt"
  : > "$out"
  logs=()
  while IFS= read -r f; do logs+=("$f"); done < <(ls "$dir"/*_logcat_all.txt "$dir"/recovered/*.txt 2>/dev/null)
  if [ "${#logs[@]}" -gt 0 ]; then
    # window.txt（「開始 終了」の epoch 秒）があれば、その間の行だけにする（端末から丸ごと取り直したログのとき）
    from=0
    to=9999999999
    if [ -f "$dir/window.txt" ]; then
      read -r from to < "$dir/window.txt"
    fi
    echo "# logcat（自パッケージの要の行。時刻は端末の epoch 秒）" >> "$out"
    for f in "${logs[@]}"; do own_lines "$f"; done | sed -E 's/^ +//' \
      | awk -v a="$from" -v b="$to" '($1 + 0) >= (a + 0) && ($1 + 0) <= (b + 0)' | sort -u -k1,1n -s >> "$out"
  fi
  # 再生の設定（dumpsys audio の AudioPlaybackConfiguration のうち自アプリの uid の行）
  uid="$(grep -m1 -hoE 'appId=[0-9]+' "$RESULTS"/00_setup/dumpsys_package_before.txt 2>/dev/null | sed 's/appId=//')"
  if [ -n "$uid" ] && ls "$dir"/*_audio_playback.txt > /dev/null 2>&1; then
    echo "# 再生の設定（dumpsys audio。uid $uid の行）" >> "$out"
    for f in "$dir"/*_audio_playback.txt; do
      line="$(grep -E "u/pid:${uid}/" "$f" | grep -oE 'u/pid:[0-9/]+ state:[a-z]+ attr:AudioAttributes: usage=[A-Z_]+' | head -1)"
      echo "$(basename "$f" _audio_playback.txt): ${line:-（再生なし）}" >> "$out"
    done
  fi
  # プロセス（pidof）と Doze の状態
  if ls "$dir"/*_state.txt > /dev/null 2>&1; then
    echo "# プロセスと Doze の状態（snap の記録）" >> "$out"
    for f in "$dir"/*_state.txt; do
      echo "$(basename "$f" _state.txt): $(grep -hE '^pid_main=|mState=|mForceIdle=' "$f" | tr -s ' \n' ' ')" >> "$out"
    done
  fi
  # 予約の中身（dumpsys alarm の自パッケージの予約。最初の 1 件）
  first_alarm="$(ls "$dir"/*_dumpsys_alarm.txt 2>/dev/null | head -1)"
  if [ -n "$first_alarm" ] && grep -qE "Alarm\{[^}]* $PKG\}" "$dir"/*_dumpsys_alarm.txt 2>/dev/null; then
    echo "# 予約の中身（dumpsys alarm）" >> "$out"
    grep -h -A9 -E "(RTC_WAKEUP|ELAPSED_WAKEUP) #[0-9]+: Alarm\{[^}]* $PKG\}" "$dir"/*_dumpsys_alarm.txt | head -10 | sed -E 's/^[0-9]+[:-]//' >> "$out"
  fi
  # 計測の行（IPC）
  if ls "$dir"/run*_bench.txt > /dev/null 2>&1; then
    echo "# 計測（各 10 回。値はマイクロ秒）" >> "$out"
    for f in "$dir"/run*_bench.txt; do sed "s/^/$(basename "$f" _bench.txt): /" "$f"; done >> "$out"
  fi
  # 手で写した観察（試験の最中に画面で見たもの）
  for f in "$dir"/observed_*.txt; do
    [ -f "$f" ] || continue
    echo "# $(basename "$f" .txt)（試験の最中に dumpsys で見たものを写した）" >> "$out"
    cat "$f" >> "$out"
  done
  [ -s "$out" ] || rm -f "$out"
  [ -f "$out" ] && echo "$name: $(wc -l < "$out") 行"
done
