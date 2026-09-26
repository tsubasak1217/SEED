#!/usr/bin/env bash
# ============================================================
#  インストール済みの前提で、t0 → t5 → t1+t2 → t4 → t6 → t3 → 集計 を順に流す（後片付けは cleanup.sh を別に）。
#  ・adb install は Play Protect の「アプリをスキャンに送信しますか」の確認で止まることがある（端末で利用者が答える）
#  ・t7（再起動）は利用者の許可が要るので含めない。t8（install -r）も Play Protect の確認で止まる恐れがあるので含めない
# ============================================================
set -u
DIR="$(cd "$(dirname "$0")" && pwd)"
source "$DIR/common.sh"
for step in t0_setup.sh t5_ipc_bench.sh t1_t2_lockscreen_task_remove.sh t4_force_stop.sh t6_fgs.sh t3_doze.sh; do
  log "==== $step ===="
  bash "$DIR/$step" || log "$step は失敗・中止（続行）"
done
bash "$DIR/extract_evidence.sh" > /dev/null
bash "$DIR/summarize.sh" > /dev/null
log "集計: $RESULTS/summary.md"
