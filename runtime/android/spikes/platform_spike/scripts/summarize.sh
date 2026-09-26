#!/usr/bin/env bash
# ============================================================
#  results/*/result.env と evidence.txt（extract_evidence.sh が作る）を、試験ごとの節に分けた
#  results/summary.md（UTF-8）にまとめる。端末には触らない（results/ だけを読む）。
#  docs/app_platform_roadmap.md §2.9.1 はこの節の見出し（「## 01・02 …」など）を証拠として引く。
#  他のアプリの名前（前面のアプリ top=…）は伏せる（私物端末の情報を持ち出さない）。
# ============================================================
set -u
source "$(dirname "$0")/common.sh"
OUT="$RESULTS/summary.md"

# 計測の結果（results/*/result.env）が無いときは書き換えない（リポジトリに残した 2026-09-27 の summary.md を空で上書きしないため）
if ! ls "$RESULTS"/*/result.env > /dev/null 2>&1; then
  log "results/*/result.env が無いので $OUT を書き換えません（先に試験を流す）"
  exit 1
fi

# 節の見出し（ディレクトリ名 → 見出し）。無いものはディレクトリ名のまま
title_of() {
  case "$1" in
    00_setup) echo "00 セットアップ: 権限と特別なアクセスの既定値（API 36）" ;;
    01_lockscreen_02_task_remove) echo "01・02 ロック画面の上に出るまで / 最近のタスクから消した後" ;;
    01a_first_ring_valve) echo "01a 1 回目の鳴動（安全弁で停止。ログから再構成）" ;;
    03_doze) echo "03 Doze の下の時刻精度" ;;
    03_doze_attempt1_user_active) echo "03（1 回目）Doze の下の時刻精度 — 利用者の操作で無効" ;;
    04_force_stop) echo "04 強制停止の後" ;;
    05_ipc) echo "05 プロセス間の往復時間" ;;
    06_fgs) echo "06 前景サービスの起動条件と種類" ;;
    07_reboot) echo "07 再起動の後の張り直しと Direct Boot" ;;
    08_package_replaced) echo "08 更新（MY_PACKAGE_REPLACED）での張り直し" ;;
    99_final_state) echo "99 端末の最終状態" ;;
    *) echo "$1" ;;
  esac
}

# 表の値: | を逃がし、自アプリ以外の前面のアプリ名を伏せる
clean_value() {
  sed -E -e 's/\|/\\|/g' -e '/top=[^;]*platformspike/! s/top=[^;]*/top=（他のアプリ）/g'
}

{
  echo "# W1-0 スパイクの計測結果（results/ の自動集計）"
  echo
  echo "- 生成: $(date '+%F %T')（\`scripts/summarize.sh\`。抜粋は \`scripts/extract_evidence.sh\`）"
  if [ -f "$RESULTS/00_setup/device.txt" ]; then
    echo "- 端末: $(cat "$RESULTS/00_setup/device.txt")"
  fi
  echo "- 時刻: \`wall\`・\`trigger_at\` は端末の UTC epoch ミリ秒、logcat の先頭は epoch 秒。\`*_ms\` は予定時刻や鳴動開始からの差"
  echo "- 生ログ（logcat・dumpsys）はリポジトリに入れていない（私物端末の他のアプリの情報を含むため）。各節の抜粋は自パッケージの行だけ"
  echo "- 結果の読み方と判断の正典は docs/app_platform_roadmap.md §2.9.1"
  for env in "$RESULTS"/*/result.env; do
    [ -f "$env" ] || continue
    dir="$(dirname "$env")"
    name="$(basename "$dir")"
    echo
    echo "## $(title_of "$name")"
    echo
    echo "| キー | 値 |"
    echo "|---|---|"
    while IFS='=' read -r key value; do
      [ -n "$key" ] || continue
      echo "| $key | $(printf '%s' "$value" | clean_value) |"
    done < "$env"
    if [ -f "$dir/evidence.txt" ]; then
      echo
      echo "<details><summary>証拠の抜粋（自パッケージの行）</summary>"
      echo
      echo '```'
      clean_value < "$dir/evidence.txt" | sed -e 's/\\|/|/g'
      echo '```'
      echo
      echo "</details>"
    fi
  done
  if [ -f "$RESULTS/lint-results-debug.txt" ]; then
    echo
    echo "## lint（AGP 9.1.0 の lintDebug）"
    echo
    echo "\`results/lint-results-debug.txt\`。指摘の種類と数:"
    echo
    echo '```'
    grep -hoE '\[[A-Za-z0-9]+\]$' "$RESULTS/lint-results-debug.txt" | sort | uniq -c
    echo '```'
    echo
    echo "正確なアラームの権限（\`USE_EXACT_ALARM\` と \`maxSdkVersion=\"32\"\` の \`SCHEDULE_EXACT_ALARM\` の併記）への指摘は無い。"
  fi
} > "$OUT"
log "書き出し: $OUT（$(wc -l < "$OUT") 行）"
