#!/usr/bin/env bash
# ============================================================
#  AC-10: SaveData.Save() の直後・途中にメインプロセスを kill -9 する試験を N 回（既定 100）繰り返し、save.json が消えず
#  壊れないことを確かめる。最後に save.json を壊して起動し、1 つ前の世代（save.json.bak）から読むことを確かめる（W1-S の実機分）。
#
#    ac10_save_kill_loop.sh [回数] [出力の名前]
#
#  前提: ロック解除中（セーブは資格情報で暗号化された files/ にあり、MainActivity は directBootAware でない）・デバッグ版 APK。
#  使うシーン: プロジェクトの scenes/SaveSmoke.scene（起動時に 2 MB の文書を含めて 4 回 Save する。W1-S の確かめ用）。
#  pak に入っていなければ、アプリ専用フォルダ（files/assets/scenes/）へ run-as で置く（エンジンは pak → APK → アプリ専用
#  フォルダの順に探す）。置いたシーンと、この試験で作ったセーブは最後に消す（試験の前にセーブが無かったときだけ）。
#
#  1 回の流れ: am start（seed.scene）→「[SaveSmoke] 起動 N 回目」を待つ →
#    奇数回: 「[SaveSmoke] 全体」（最後の Save の後）を待って直ちに kill -9（Save の直後）
#    偶数回: 0〜KILL_JITTER_MS のでたらめな待ちの後に kill -9（4 回の Save〈1 回約 230 ms〉の途中に当たりやすい）
#    → save.json を取り出して JSON として読めるか・smoke_run_count を確かめる。
#  合格の条件: 全回で save.json が読める（.bak があっても本体が読める）・次の起動の RecoveredFrom が None・
#  起動の回数（smoke_run_count）が減らない。
#  環境変数: SEED_DEVICE_TEST_RESULTS（結果の置き場。common.sh）・SAVE_SMOKE_SCENE（既定は下の SCENE_SRC）。
# ============================================================
set -u
source "$(dirname "$0")/common.sh"
COUNT="${1:-100}"
OUT="$RESULTS/${2:-ac10_save_kill}"
mkdir -p "$OUT"
RES="$OUT/result.env"
: > "$RES"
TABLE="$OUT/runs.tsv"
SCENE_REL="scenes/SaveSmoke.scene"
SCENE_SRC="${SAVE_SMOKE_SCENE:-D:/SEED_projects/WakeOrPay/assets/scenes/SaveSmoke.scene}"
APP_SCENE_DIR="files/assets/scenes"
SAVE_DIR="files/save"
# 起動から「起動 N 回目」までの上限（冷えた起動のエンジンの初期化を含む）
START_TIMEOUT_S=40
# 「全体」までの上限（4 回の Save）
FINISH_TIMEOUT_S=20
# 偶数回の kill までのでたらめな待ちの上限（ミリ秒。環境変数で変えられる。小さくすると 4 回の Save の途中に当たりやすい）
KILL_JITTER_MS="${KILL_JITTER_MS:-1200}"
# 目印を探す間隔（秒）
POLL_S=0.1
# kill の後、プロセスが消えるのを待つ（秒）
AFTER_KILL_S=1

# 目印を細かく待つ（wait_mark より細かい間隔。見つかれば 0）
wait_fast() {
  local since="$1" pattern="$2" timeout_s="$3" limit
  limit=$(( $(date +%s) + timeout_s ))
  while [ "$(date +%s)" -lt "$limit" ]; do
    stream_since "$since" | grep -a -qE "$pattern" && return 0
    sleep "$POLL_S"
  done
  return 1
}

# save.json を取り出し、JSON として読めれば smoke_run_count を、読めなければ「broken」を返す（無ければ「missing」）
disk_run_count() {
  local file="$OUT/.save.json"
  if ! adbs exec-out run-as "$PKG" cat "$SAVE_DIR/save.json" > "$file" 2>/dev/null || [ ! -s "$file" ]; then
    echo "missing"
    return
  fi
  python - "$(cygpath -w "$file")" <<'PY'
import json, sys
try:
    with open(sys.argv[1], encoding="utf-8") as f:
        doc = json.load(f)
except Exception:
    print("broken")
    sys.exit(0)
# 新しい形（値の型つき）でも古い形（そのままの値）でも数を拾う
def find(node):
    if isinstance(node, dict):
        if "smoke_run_count" in node:
            v = node["smoke_run_count"]
            if isinstance(v, dict):
                for k in ("Int", "int", "value", "v"):
                    if k in v:
                        return v[k]
                return next(iter(v.values()), None)
            return v
        for child in node.values():
            r = find(child)
            if r is not None:
                return r
    return None
print(find(doc))
PY
}

device_present || { put "$RES" skipped device_absent; exit 3; }
[ "$(keyguard_showing)" = "true" ] && { put "$RES" skipped locked; log "ロック中なので行えない（セーブは解除後にしか読めない）"; exit 1; }
had_save_before="$(dsh "run-as $PKG ls $SAVE_DIR/save.json" 2>/dev/null | grep -c 'save.json$' || true)"
put "$RES" had_save_before "$had_save_before"
put "$RES" kill_jitter_ms "$KILL_JITTER_MS"
# シーンを置く（pak に無いとき用。置いた印を残し、最後に消す）
dsh "run-as $PKG ls $APP_SCENE_DIR/SaveSmoke.scene" 2>/dev/null | grep -q 'SaveSmoke.scene$' && scene_was_there=1 || scene_was_there=0
if [ "$scene_was_there" = "0" ]; then
  adbs shell "run-as $PKG sh -c 'mkdir -p $APP_SCENE_DIR && cat > $APP_SCENE_DIR/SaveSmoke.scene'" < "$SCENE_SRC"
fi
put "$RES" scene_was_there "$scene_was_there"
printf 'run\tmode\tstart_line_count\trecovered_from\tkill_after_ms\tdisk_run_count\tsave_files\n' > "$TABLE"
start_stream "$OUT/stream.txt"
ng=0
prev_disk=0
for i in $(seq 1 "$COUNT"); do
  wait_user_idle || { put "$RES" aborted_at "$i"; break; }
  # 前の回で kill したはずだが、残っていれば落とす（:seed_platform はこの試験に関わらないので触らない）
  left="$(pid_main)"
  [ -n "$left" ] && { dsh "run-as $PKG kill -9 $left" > /dev/null 2>&1; sleep "$AFTER_KILL_S"; }
  s="$(dev_since)"
  dsh "am start -n $MAIN_ACTIVITY --es seed.scene $SCENE_REL" > /dev/null
  if ! wait_fast "$s" '\[SaveSmoke\] 起動 [0-9]+ 回目' "$START_TIMEOUT_S"; then
    log "$i 回目: 起動の目印が来ない"
    printf '%s\t-\t-\t-\t-\t-\t-\n' "$i" >> "$TABLE"
    ng=$((ng + 1))
    continue
  fi
  lines="$(stream_since "$s")"
  started="$(echo "$lines" | grep -a -m1 -oE '起動 [0-9]+ 回目' | grep -oE '[0-9]+')"
  recovered="$(echo "$lines" | grep -a -m1 -oE 'RecoveredFrom = [A-Za-z]+' | sed 's/.*= //')"
  t0="$(date +%s%3N)"
  if [ $((i % 2)) -eq 1 ]; then
    mode="after_final_save"
    wait_fast "$s" '\[SaveSmoke\] 全体' "$FINISH_TIMEOUT_S" || log "$i 回目: 全体の目印が来ない"
  else
    mode="random"
    jitter=$(( RANDOM % KILL_JITTER_MS ))
    sleep "$(awk -v ms="$jitter" 'BEGIN { printf "%.3f", ms / 1000 }')"
  fi
  pid="$(pid_main)"
  [ -n "$pid" ] && dsh "run-as $PKG kill -9 $pid" > /dev/null 2>&1
  kill_after=$(( $(date +%s%3N) - t0 ))
  sleep "$AFTER_KILL_S"
  disk="$(disk_run_count)"
  files="$(dsh "run-as $PKG ls $SAVE_DIR" 2>/dev/null | tr '\n' ',' )"
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$i" "$mode" "$started" "$recovered" "$kill_after" "$disk" "$files" >> "$TABLE"
  # 合否: 読める・復旧していない・数が減らない
  case "$disk" in
    missing|broken|None) ng=$((ng + 1)); log "$i 回目: save.json が $disk" ;;
    *) if [ "$disk" -lt "$prev_disk" ] 2>/dev/null; then ng=$((ng + 1)); log "$i 回目: 数が減った（$prev_disk → $disk）"; fi
       prev_disk="$disk" ;;
  esac
  [ "$recovered" != "None" ] && { ng=$((ng + 1)); log "$i 回目: RecoveredFrom=$recovered"; }
  log "$i/$COUNT $mode kill_after=${kill_after}ms start=$started disk=$disk recovered=$recovered"
done
put "$RES" runs "$(($(wc -l < "$TABLE") - 1))"
put "$RES" ng "$ng"
put "$RES" last_disk_run_count "$prev_disk"

# 壊した save.json から前の世代で起動する
kill_app_processes
dsh "run-as $PKG sh -c 'printf \"{broken\" > $SAVE_DIR/save.json'"
s="$(dev_since)"
dsh "am start -n $MAIN_ACTIVITY --es seed.scene $SCENE_REL" > /dev/null
wait_fast "$s" '\[SaveSmoke\] 全体' $((START_TIMEOUT_S + FINISH_TIMEOUT_S)) || log "壊した後の起動で全体の目印が来ない"
stream_since "$s" | grep -a -E '\[SEED SAVE\]|\[SaveSmoke\] (RecoveredFrom|起動|全体)' | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//' > "$OUT/corrupt_restart.txt"
put "$RES" corrupt_recovered_from "$(grep -a -m1 -oE 'RecoveredFrom = [A-Za-z]+' "$OUT/corrupt_restart.txt" | sed 's/.*= //')"
put "$RES" corrupt_files_after "$(dsh "run-as $PKG ls $SAVE_DIR" 2>/dev/null | tr '\n' ',')"
kill_app_processes
stop_stream

# 後始末: 置いたシーンと、試験の前に無かったセーブを消す
if [ "$scene_was_there" = "0" ]; then
  # 置いたシーンを消し、空になったフォルダ（files/assets/scenes・files/assets）も消す（空でなければ rmdir は何もしない）
  dsh "run-as $PKG sh -c 'rm -f $APP_SCENE_DIR/SaveSmoke.scene; rmdir $APP_SCENE_DIR files/assets 2>/dev/null; true'"
fi
if [ "$had_save_before" = "0" ]; then dsh "run-as $PKG rm -rf $SAVE_DIR"; fi
put "$RES" cleanup_save_dir "$(dsh "run-as $PKG ls files" 2>/dev/null | tr '\n' ',')"
cat "$RES"
