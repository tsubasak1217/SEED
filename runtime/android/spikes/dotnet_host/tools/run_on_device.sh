#!/usr/bin/env bash
# =============================================================================
# run_on_device.sh - 端末上で spike_host を 1 シナリオ実行し、出力と logcat を保存する。
#
# 使い方: run_on_device.sh <serial> <abi> <scenario名> [環境変数 KEY=VAL ...] -- <spike_host の引数...>
#   引数中の @D@ は端末上の ABI ディレクトリ（/data/local/tmp/seed_dotnet_spike/<abi>）に置換される。
# 出力: logs/<abi>_<scenario>.txt（標準出力＋標準エラー＋所要時間）と logs/<abi>_<scenario>.logcat.txt
# 注意: logcat -c（全消去）は他エージェントと共用のため使わず、実行前の端末時刻以降だけを読む。
# =============================================================================
set -u
export MSYS_NO_PATHCONV=1

ADB=/c/Users/k023g/AppData/Local/Android/Sdk/platform-tools/adb.exe
SPIKE=/c/Users/k023g/.claude/jobs/434062fd/tmp/dotnet_spike
DEVICE_ROOT=/data/local/tmp/seed_dotnet_spike

serial="$1"; abi="$2"; scenario="$3"; shift 3
envs=()
while [ "$#" -gt 0 ] && [ "$1" != "--" ]; do envs+=("$1"); shift; done
[ "$#" -gt 0 ] && shift
dir="$DEVICE_ROOT/$abi"
args=()
for a in "$@"; do args+=("${a//@D@/$dir}"); done

log="$SPIKE/logs/${abi}_${scenario}.txt"
# adb shell は引数を空白で連結し直すので、date の書式は 1 つの文字列として渡す
since=$("$ADB" -s "$serial" shell "date +'%m-%d %H:%M:%S.000'" | tr -d '\r')

# 端末側コマンド: 開始/終了時刻（ns）を出力し、終了コードも残す。
# mksh の算術は 32bit なので差はここ（64bit の bash）で計算する。
cmd="cd $dir && s=\$(date +%s%N) && ${envs[*]:-} ./spike_host ${args[*]} 2>&1; rc=\$?; e=\$(date +%s%N); echo \"WALLNS \$s \$e\"; echo \"EXIT \$rc\""
{
  echo "# scenario=$scenario abi=$abi serial=$serial"
  echo "# device-cmd: $cmd"
  "$ADB" -s "$serial" shell "$cmd"
} > "$log" 2>&1

read -r _ s_ns e_ns < <(grep -E '^WALLNS ' "$log" | tr -d '\r')
if [ -n "${s_ns:-}" ] && [ -n "${e_ns:-}" ]; then
  printf 'WALL\tprocess.wall_ms\t%d\n' $(( (e_ns - s_ns) / 1000000 )) >> "$log"
fi

"$ADB" -s "$serial" logcat -d -T "$since" > "$SPIKE/logs/${abi}_${scenario}.logcat.txt" 2>&1
echo "== $scenario: $(grep -E '^(EXIT|WALL)' "$log" | tr -d '\r' | tr '\n' ' ') avc=$(grep -c 'avc:' "$SPIKE/logs/${abi}_${scenario}.logcat.txt") logcat_lines=$(wc -l < "$SPIKE/logs/${abi}_${scenario}.logcat.txt")"
