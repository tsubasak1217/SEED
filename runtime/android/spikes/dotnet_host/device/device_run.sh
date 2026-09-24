#!/usr/bin/env bash
# =============================================================================
# device_run.sh - 実機（ユーザー私物の Pixel 6a）で spike_host を 1 シナリオ実行する。
#
# 既存の tools/run_on_device.sh（変更しない）との違い:
#   - logcat 全体は保存しない。端末側で次の行だけに絞ってから持ち出す（私物端末の他アプリのログを持ち出さないため）
#       1) spike_host 自身の PID の行（logcat --pid）
#       2) 本スパイク由来と判断できる avc 行（コマンド名・パス・.NET のスレッド名で絞る）
#       3) クラッシュバッファのうち spike_host / 本スパイクのパスを含む行
#   - PID を得るため、端末側ではバックグラウンド起動して wait する
#   - ログ名は logs/device_<scenario>.txt / logs/device_<scenario>.logcat.txt
#
# 使い方: device_run.sh <scenario名> [環境変数 KEY=VAL ...] -- <spike_host の引数...>
#   引数・環境変数中の @D@ は端末上の ABI ディレクトリに置換される。
# =============================================================================
set -u
export MSYS_NO_PATHCONV=1

ADB=/c/Users/k023g/AppData/Local/Android/Sdk/platform-tools/adb.exe
SPIKE=/c/Users/k023g/.claude/jobs/434062fd/tmp/dotnet_spike
SERIAL=2B011JEGR02535
DIR=/data/local/tmp/seed_dotnet_spike/arm64-v8a

scenario="$1"; shift
envs=()
while [ "$#" -gt 0 ] && [ "$1" != "--" ]; do envs+=("${1//@D@/$DIR}"); shift; done
[ "$#" -gt 0 ] && shift
args=()
for a in "$@"; do args+=("${a//@D@/$DIR}"); done

log="$SPIKE/logs/device_${scenario}.txt"
logcat_log="$SPIKE/logs/device_${scenario}.logcat.txt"
# adb shell は引数を空白で連結し直すので、date の書式は 1 つの文字列として渡す
since=$("$ADB" -s "$SERIAL" shell "date +'%m-%d %H:%M:%S.000'" | tr -d '\r')

# 端末側コマンド: 開始/終了時刻（ns）・PID・終了コードを出力する（mksh の算術は 32bit なので差はホスト側で計算）
cmd="cd $DIR && s=\$(date +%s%N); ${envs[*]:-} ./spike_host ${args[*]} 2>&1 & p=\$!; echo \"PID \$p\"; wait \$p; rc=\$?; e=\$(date +%s%N); echo \"WALLNS \$s \$e\"; echo \"EXIT \$rc\""
{
  echo "# scenario=$scenario serial=$SERIAL"
  echo "# device-cmd: $cmd"
  "$ADB" -s "$SERIAL" shell "$cmd"
} > "$log" 2>&1

pid=$(grep -E '^PID ' "$log" | head -1 | awk '{print $2}' | tr -d '\r')
read -r _ s_ns e_ns < <(grep -E '^WALLNS ' "$log" | tr -d '\r')
if [ -n "${s_ns:-}" ] && [ -n "${e_ns:-}" ]; then
  printf 'WALL\tprocess.wall_ms\t%d\n' $(( (e_ns - s_ns) / 1000000 )) >> "$log"
fi

# logcat は端末側で絞り込んでから取得する
# （adbd は受け取ったシェルコマンド＝このフィルタ文字列自体をログに残すので、その行は除外する）
filter="logcat -d -T '$since' --pid=${pid:-0}; echo '--- avc(related)'; logcat -d -T '$since' | grep 'avc:' | grep -v 'adbd service requested' | grep -E 'spike_host|seed_dotnet_spike|clr-debug|dotnet-diagnostic|[.]NET|SGen|Finalizer|mono'; echo '--- crash(related)'; logcat -d -b crash -T '$since' | grep -E 'spike_host|seed_dotnet_spike'"
"$ADB" -s "$SERIAL" shell "$filter" > "$logcat_log" 2>&1

avc=$(sed -n '/^--- avc(related)/,/^--- crash(related)/p' "$logcat_log" | grep -c 'avc:')
echo "== $scenario: $(grep -E '^(EXIT|WALL)' "$log" | tr -d '\r' | tr '\n' ' ') pid=${pid:-?} avc=$avc logcat_lines=$(wc -l < "$logcat_log")"
