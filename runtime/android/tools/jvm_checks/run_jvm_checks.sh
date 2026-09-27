#!/usr/bin/env bash
# ============================================================
#  run_jvm_checks.sh — Android の Java のうち、Android の API を使わない純粋な部分を JVM で確かめる（端末なし）
#
#  使い方（Git Bash）: runtime/android/tools/jvm_checks/run_jvm_checks.sh [出力フォルダ]
#    出力フォルダを省くと一時フォルダへ書く。javac / java は PATH のもの（Microsoft JDK 25 で確かめた。--release 17 で
#    Android のアプリと同じ言語の版にそろえる）。どれかの検査が失敗したら終了コード 1。
#
#  検査の置き場: src/（本物のクラスと同じパッケージに置く。package-private のクラスを触るため）。
#  検査するクラスは、アプリの src/main/java から「Android の API を import しない」ものだけを渡す（android.jar は要らない）。
#    PermissionChangeTrackerCheck … PermissionChangeTracker（権限の前回の状態との比較。M7）
# ============================================================
set -euo pipefail

# このファイルの場所から、アプリの Java のソースを探す
HERE="$(cd "$(dirname "$0")" && pwd)"
APP_JAVA="$HERE/../../app/src/main/java"
CHECK_SRC="$HERE/src"

# 出力フォルダ（省略時は一時フォルダ）
OUT="${1:-$(mktemp -d)}"
mkdir -p "$OUT"

# 検査の対象（アプリの純粋なクラス）と検査の本体
PERMISSION_DIR="com/seedengine/runtime/platform/permission"
SOURCES=(
  "$APP_JAVA/$PERMISSION_DIR/PermissionChangeTracker.java"
  "$CHECK_SRC/$PERMISSION_DIR/PermissionChangeTrackerCheck.java"
)
MAIN_CLASSES=(
  "com.seedengine.runtime.platform.permission.PermissionChangeTrackerCheck"
)

javac -J-Duser.language=en -J-Duser.country=US -Xlint:all --release 17 -encoding UTF-8 -d "$OUT" "${SOURCES[@]}"

status=0
for main_class in "${MAIN_CLASSES[@]}"; do
  echo "== $main_class"
  if ! java -Dstdout.encoding=UTF-8 -cp "$OUT" "$main_class"; then
    status=1
  fi
done
exit "$status"
