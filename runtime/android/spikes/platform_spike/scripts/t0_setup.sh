#!/usr/bin/env bash
# ============================================================
#  t0: インストール直後の権限・特別なアクセスの既定値を記録し、試験に要るものだけ adb で付ける
#   ・POST_NOTIFICATIONS … 実行時の権限（pm grant）
#   ・USE_FULL_SCREEN_INTENT … Android 14+ の特別なアクセス（appops）。既定で拒否なら allow にする
#   ・USE_EXACT_ALARM … インストール時に許可されるはず（canScheduleExactAlarms で確かめるだけ）
#  前提: APK はインストール済み（adb install は Play Protect の確認で止まることがあるので別に行う）
# ============================================================
set -u
source "$(dirname "$0")/common.sh"
OUT="$RESULTS/00_setup"
mkdir -p "$OUT"
RES="$OUT/result.env"
: > "$RES"

if ! dsh "pm list packages $PKG" | grep -q "package:$PKG\$"; then
  log "$PKG がインストールされていません"
  put "$RES" installed false
  exit 1
fi
put "$RES" installed true

since="$(dev_since)"
# 既定値（付ける前）
dsh "dumpsys package $PKG" > "$OUT/dumpsys_package_before.txt"
dsh "appops get $PKG" > "$OUT/appops_before.txt"
grep -E "android.permission.(USE_EXACT_ALARM|SCHEDULE_EXACT_ALARM|USE_FULL_SCREEN_INTENT|POST_NOTIFICATIONS|FOREGROUND_SERVICE[A-Z_]*|RECEIVE_BOOT_COMPLETED|WAKE_LOCK): granted" \
  "$OUT/dumpsys_package_before.txt" > "$OUT/permissions_before.txt" || true
ctl STATUS > "$OUT/status_before_broadcast.txt"
wait_mark "$since" "MARK status_main_process" 20 || log "STATUS の応答が無い"
sleep 1
adbs logcat -d -v epoch -s "$TAG" -T "$since" | tr -d '\r' > "$OUT/status_before_logcat.txt"
put "$RES" exact_before "$(mark_field "$OUT/status_before_logcat.txt" status_main_process can_schedule_exact)"
put "$RES" fsi_before "$(mark_field "$OUT/status_before_logcat.txt" status_main_process can_use_full_screen_intent)"
put "$RES" notif_before "$(mark_field "$OUT/status_before_logcat.txt" status_main_process notifications_enabled)"
put "$RES" appop_fsi_before "$(grep -E '^USE_FULL_SCREEN_INTENT' "$OUT/appops_before.txt" | head -1 | tr ' ' '_')"
put "$RES" appop_exact_before "$(grep -E '^SCHEDULE_EXACT_ALARM' "$OUT/appops_before.txt" | head -1 | tr ' ' '_')"
put "$RES" use_exact_alarm_granted "$(grep -cE 'android.permission.USE_EXACT_ALARM: granted=true' "$OUT/dumpsys_package_before.txt")"

# 付ける（試験に要るものだけ。実運用の UI は対象外）
{
  echo "\$ pm grant $PKG android.permission.POST_NOTIFICATIONS"
  dsh "pm grant $PKG android.permission.POST_NOTIFICATIONS" 2>&1
  if [ "$(mark_field "$OUT/status_before_logcat.txt" status_main_process can_use_full_screen_intent)" != "true" ]; then
    echo "\$ appops set $PKG USE_FULL_SCREEN_INTENT allow"
    dsh "appops set $PKG USE_FULL_SCREEN_INTENT allow" 2>&1
  fi
} > "$OUT/grant_commands.txt"

since2="$(dev_since)"
ctl STATUS > "$OUT/status_after_broadcast.txt"
wait_mark "$since2" "MARK status_main_process" 20 || log "STATUS の応答が無い"
sleep 1
adbs logcat -d -v epoch -s "$TAG" -T "$since2" | tr -d '\r' > "$OUT/status_after_logcat.txt"
dsh "appops get $PKG" > "$OUT/appops_after.txt"
dsh "dumpsys package $PKG" > "$OUT/dumpsys_package_after.txt"
put "$RES" exact_after "$(mark_field "$OUT/status_after_logcat.txt" status_main_process can_schedule_exact)"
put "$RES" fsi_after "$(mark_field "$OUT/status_after_logcat.txt" status_main_process can_use_full_screen_intent)"
put "$RES" notif_after "$(mark_field "$OUT/status_after_logcat.txt" status_main_process notifications_enabled)"

# 素の am broadcast（-n / -p なしの暗黙の放送）がマニフェストの受信機に届くかも確かめる（タスク文のコマンドの形）
since3="$(dev_since)"
adbs shell am broadcast -a "$PKG.STATUS" | tr -d '\r' > "$OUT/implicit_broadcast.txt"
if wait_mark "$since3" "MARK debug_control action=$PKG.STATUS" 8; then
  put "$RES" implicit_broadcast_delivered true
else
  put "$RES" implicit_broadcast_delivered false
fi
save_logcat "$OUT" setup "$since"
cat "$RES"
