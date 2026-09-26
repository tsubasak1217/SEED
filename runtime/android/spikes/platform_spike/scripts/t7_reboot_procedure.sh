#!/usr/bin/env bash
# ============================================================
#  t7: 再起動の後の張り直し（AC-4）と Direct Boot（E-10。ロック解除前に鳴るか）の手順
#
#  ※ adb reboot は利用者の許可が要る。許可の印（--reboot-permitted）が無ければ手順を表示するだけで何もしない。
#
#  手順（許可のあと）:
#   1. 240 秒後に予約（id=t7）し、dumpsys alarm に載っていることを記録する
#   2. adb reboot → 起動完了（sys.boot_completed=1）まで待つ
#   3. 【ロックを解除しないで】待つ。見るもの:
#        ・LOCKED_BOOT_COMPLETED で boot_received → rearm_all（user_unlocked=false）→ dumpsys alarm に再び載る
#        ・予定時刻に alarm_received → fgs_started → ring_start（user_unlocked=false のまま）
#        ・RingActivity（directBootAware）がロック解除前のロック画面の上に出るか（ring_activity_first_frame）
#   4. 鳴動は安全弁（60 秒）で止まる。止める命令はメインプロセスの受信機（directBootAware でない）なので解除前は届かない。
#      急ぐなら am force-stop。
#   5. ロックを解除した後: BOOT_COMPLETED で 2 回目の rearm_all（控えは空のはず）を確認する
#   6. 後片付け（cleanup.sh）
#  代わりに今できる範囲: t8（adb install -r で MY_PACKAGE_REPLACED を起こす）
# ============================================================
set -u
source "$(dirname "$0")/common.sh"
OUT="$RESULTS/07_reboot"
LEAD_S=240

if [ "${1:-}" != "--reboot-permitted" ]; then
  grep -E '^#' "$0" | sed -n '2,30p'
  echo
  echo "（未実施: 利用者の許可が無いので何もしません）"
  exit 0
fi

mkdir -p "$OUT"
RES="$OUT/result.env"
: > "$RES"
since="$(dev_since)"
ctl SCHEDULE --ei seconds "$LEAD_S" --es id t7 --ei max_ring_s 60 > "$OUT/schedule_broadcast.txt"
wait_mark "$since" "MARK schedule_result" 20 || log "schedule_result が来ない"
put "$RES" pending_before_reboot "$(pending_alarms)"
snap "$OUT" 0_before_reboot
adbs logcat -d -v epoch -s "$TAG" -T "$since" | tr -d '\r' > "$OUT/.marks_before.txt"
put "$RES" trigger_at "$(mark_field "$OUT/.marks_before.txt" schedule_result trigger_at)"

adbs reboot
adbs wait-for-device
until [ "$(dsh 'getprop sys.boot_completed')" = "1" ]; do sleep 2; done
log "起動完了。ロックを解除しないでください"
put "$RES" boot_completed_ms "$(dev_now_ms)"
# 再起動で logcat は消えるので、起動の頭から取る
until adbs logcat -d -v epoch -s "$TAG" | tr -d '\r' | grep -qE "MARK ring_start|MARK alarm_missed"; do sleep 2; done
sleep 15
snap "$OUT" 1_after_ring_before_unlock
adbs logcat -d -v epoch -b main,system,crash,events | tr -d '\r' > "$OUT/after_reboot_logcat_all.txt"
grep -E "$TAG|Displayed $PKG" "$OUT/after_reboot_logcat_all.txt" > "$OUT/after_reboot_logcat_spike.txt" || true
F="$OUT/after_reboot_logcat_spike.txt"
put "$RES" locked_boot_rearm "$(grep -E 'MARK rearm_all reason=boot:android.intent.action.LOCKED_BOOT_COMPLETED' "$F" | head -1 | sed -E 's/.*MARK rearm_all //' | tr ' ' ';')"
put "$RES" alarm_received_late_ms "$(mark_field "$F" alarm_received late_ms)"
put "$RES" alarm_received_user_unlocked "$(mark_field "$F" alarm_received user_unlocked)"
put "$RES" ring_start_since_sched_ms "$(mark_field "$F" ring_start since_sched_ms)"
put "$RES" ring_activity_shown_before_unlock "$(mark_field "$F" ring_activity_first_frame user_unlocked)"
cat "$RES"
