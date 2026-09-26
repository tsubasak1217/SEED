#!/usr/bin/env bash
# ============================================================
#  後片付け: 予約の取り消し → Doze・電池の模擬を戻す → アプリの停止 → アンインストール → 最終状態の記録
# ============================================================
set -u
source "$(dirname "$0")/common.sh"
OUT="$RESULTS/99_final_state"
mkdir -p "$OUT"
RES="$OUT/result.env"
: > "$RES"

if dsh "pm list packages $PKG" | grep -q "package:$PKG\$"; then
  s="$(dev_since)"
  ctl STOP > /dev/null
  ctl CANCEL_ALL > /dev/null
  wait_mark "$s" "MARK cancel_all_result" 20 || log "cancel_all_result が来ない"
  put "$RES" pending_alarms_after_cancel "$(pending_alarms)"
fi
dsh "dumpsys deviceidle unforce" > "$OUT/unforce.txt" 2>&1
dsh "dumpsys battery reset" > "$OUT/battery_reset.txt" 2>&1
dsh "am force-stop $PKG" > /dev/null 2>&1
adbs uninstall "$PKG" > "$OUT/uninstall.txt" 2>&1
put "$RES" uninstall_output "$(tr '\n' ' ' < "$OUT/uninstall.txt" | tr ' ' '_')"
put "$RES" package_present "$(dsh "pm list packages $PKG" | grep -c "package:$PKG\$")"
put "$RES" pending_alarms_final "$(pending_alarms)"
put "$RES" processes "$(dsh 'ps -A -o NAME' | grep -cF "$PKG")"
put "$RES" deviceidle "$(dsh 'dumpsys deviceidle' | grep -E 'mForceIdle=|mState=|mLightState=' | tr -s ' \n' ';')"
put "$RES" battery "$(dsh 'dumpsys battery' | grep -E 'AC powered|USB powered|status:' | tr -s ' \n' ';')"
put "$RES" screen "$(screen_state | tr ' ' ';')"
cat "$RES"
