#!/usr/bin/env bash
# ============================================================
#  鳴動 1 回ぶんの logcat（-v epoch）から、予定時刻（trigger_at_utc_ms）からの各段階の時刻（ms）を出す。
#    analyze_ring.sh <logcat のファイル> <予定時刻 ms> <予約 ID> [<この時刻 ms より後の行だけを見る>]
#  出力は key=value（値が無い段階は空）。端末の時計（logcat の epoch と予定時刻）だけで計算する。
# ============================================================
set -u
F="$1"; T="$2"; ID="$3"; AFTER="${4:-0}"
PKG="com.wakeorpay.seed"
TMP="$(mktemp)"
tr -d '\r' < "$F" | awk -v a="$AFTER" '($1 * 1000 + 0) >= (a + 0)' > "$TMP"

# 最初に一致した行の時刻（ms・予定時刻からの差）
rel() { local line; line="$(grep -m1 -E "$1" "$TMP")"; [ -n "$line" ] && echo "$line" | awk -v t="$T" '{ printf "%d\n", $1 * 1000 - t + 0.5 }'; }
# 最初に一致した行そのもの（行頭の時刻・pid などを除く）
text() { grep -m1 -E "$1" "$TMP" | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//'; }

recv_line="$(text "AlarmReceiver: $ID の発火を処理しました")"
echo "receiver_line=$recv_line"
recv_late="$(echo "$recv_line" | grep -oE '予定から -?[0-9]+ ms' | grep -oE -- '-?[0-9]+')"
fgs_req="$(echo "$recv_line" | grep -oE '前景サービスの要求まで [0-9]+ ms' | grep -oE '[0-9]+')"
echo "delivery_ms=$recv_late"
[ -n "$recv_late" ] && [ -n "$fgs_req" ] && echo "start_fgs_requested_ms=$((recv_late + fgs_req))"
echo "receiver_done_ms=$(rel "AlarmReceiver: $ID の発火を処理しました")"
echo "platform_proc_start_ms=$(rel "(am_proc_start: \[[0-9]+,[0-9]+,[0-9]+,$PKG:seed_platform,)|(Start proc [0-9]+:$PKG:seed_platform/)")"
echo "platform_proc_start_line=$(text "(am_proc_start: \[[0-9]+,[0-9]+,[0-9]+,$PKG:seed_platform,)|(Start proc [0-9]+:$PKG:seed_platform/)")"
echo "provider_created_ms=$(rel 'PlatformProvider を作りました')"
echo "ring_service_created_ms=$(rel 'RingService: 起こされました')"
echo "fgs_allowed_line=$(text "Background started FGS: [A-Za-z]+ .*$PKG" | cut -c1-300)"
echo "start_foreground_done_ms=$(rel "RingService: 目覚まし $ID を鳴らしています")"
echo "am_fgs_start_ms=$(rel "am_foreground_service_start: \[[0-9]+,$PKG/")"
echo "sound_start_ms=$(rel "目覚まし $ID を鳴らし始めました")"
echo "sound_line=$(text "目覚まし $ID を鳴らし始めました")"
echo "fsi_start_ms=$(rel "START u0 .*$PKG/com.seedengine.runtime.platform.PlatformEntry")"
echo "fsi_start_line=$(text "START u0 .*$PKG/com.seedengine.runtime.platform.PlatformEntry" | cut -c1-400)"
echo "main_proc_start_ms=$(rel "(am_proc_start: \[[0-9]+,[0-9]+,[0-9]+,$PKG,)|(Start proc [0-9]+:$PKG/u)")"
echo "main_proc_start_line=$(text "(am_proc_start: \[[0-9]+,[0-9]+,[0-9]+,$PKG,)|(Start proc [0-9]+:$PKG/u)")"
echo "launch_reason_ms=$(rel '起動理由: ')"
echo "launch_reason_line=$(text '起動理由: ' | cut -c1-300)"
echo "new_intent_line=$(text '起動後に届いた Intent の理由' | cut -c1-300)"
echo "keyguard_occlude_count=$(grep -cE 'KEYGUARD_OCCLUDE' "$TMP")"
echo "keyguard_occlude_first_ms=$(rel 'KEYGUARD_OCCLUDE')"
echo "surface_created_ms=$(rel '\[SEED SURFACE\] created')"
echo "engine_first_frame_start_ms=$(rel '\[SEED FRAME 0\] start')"
echo "engine_first_frame_end_ms=$(rel '\[SEED FRAME 0\] end')"
echo "displayed_ms=$(rel "Displayed $PKG/")"
echo "displayed_line=$(text "Displayed $PKG/")"
echo "wm_activity_launch_time_line=$(text "wm_activity_launch_time: \[[^]]*$PKG")"
echo "dotnet_lines=$(grep -E '\[SEED DOTNET\]' "$TMP" | sed -E 's/^ *[0-9.]+ +[0-9]+ +[0-9]+ +[A-Z] +//' | cut -c1-200 | tr '\n' '|')"
echo "dotnet_first_ms=$(rel '\[SEED DOTNET\]')"
echo "ring_stopped_ms=$(rel "目覚まし $ID の鳴動が終わりました")"
echo "ring_stopped_line=$(text "目覚まし $ID の鳴動が終わりました")"
echo "task_removed_line=$(text 'RingService: タスクが消されましたが鳴らし続けます' | cut -c1-300)"
echo "fsi_denied_line=$(text 'フルスクリーン通知が許可されていない|通知が許可されていない' | cut -c1-200)"
echo "anr_or_crash=$(grep -cE "ANR in $PKG|FATAL EXCEPTION|Process: $PKG|RustPanic|SEED PANIC" "$TMP")"
rm -f "$TMP"
