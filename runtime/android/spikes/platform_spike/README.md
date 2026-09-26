# platform_spike（W1-0 スパイク: 確実に鳴る目覚ましの基盤を Java だけで確かめる）

アプリ基盤 W1（Android サービス層 `SEED.Platform`）の構成（docs/app_platform_roadmap.md §2.2）を、
**SEED 本体を 1 行も変えずに**実機で確かめるための使い捨てアプリ（applicationId `com.seedengine.platformspike`）。
**結果と判断の正典は docs/app_platform_roadmap.md §2.9.1**（決めた未決事項は同 §5 の E-01〜E-03・E-10）。
ここは「もう一度同じ検証を回す」ためと、W1 の実装の下敷きとして置いている。2026-09-27 に Pixel 6a（Android 16）で実施。

## 何を確かめるか

- 別プロセス `:seed_platform` の前景サービスが、`setAlarmClock` の発火から（両プロセスが無い状態でも）鳴り、
  フルスクリーン通知でロック画面の上に鳴動画面が出るまでの時間
- 鳴動中に最近のタスクから消しても（`am stack remove`＝removeTask）音が続くか
- Doze（`dumpsys deviceidle force-idle`）の下の時刻精度、強制停止の後に予約が残るか、再起動の後の張り直し（手順だけ）
- メインプロセス ↔ `:seed_platform` の呼び出し（`ContentResolver.call`・`ContentProviderClient`・AIDL・Messenger・放送）の往復時間
- 前景サービスの種類（`mediaPlayback`・`systemExempted`）と、背面から起こせる条件

## 構成

| パス | 役割 |
|---|---|
| `app/src/main/AndroidManifest.xml` | プロセスの割り振り・exported・directBootAware・権限（冒頭のコメントに設計の要点） |
| `app/src/main/java/…/SpikeContract.java` | アクション名・extra 名・既定値の置き場（scripts/ と一致させる） |
| `…/AlarmScheduler.java`・`AlarmStore.java`・`AlarmEntry.java` | `setAlarmClock` の予約・取り消し・張り直しと、予約の控え（端末保護ストレージ・一時ファイル → fsync → rename） |
| `…/AlarmReceiver.java`・`BootReceiver.java` | 発火 → 前景サービスの起動／再起動・更新・停止状態からの復帰で張り直す（exported=false） |
| `…/RingService.java` | 鳴動の前景サービス（種類は extra で選ぶ・`USAGE_ALARM` のループ・WakeLock・安全弁・`onTaskRemoved` で止めない） |
| `…/RingActivity.java` | 鳴動画面（SEED の GameActivity の代役）。別名 `RingEntry`（exported=false）経由の起動だけ `showWhenLocked` を上げる |
| `…/PlatformProvider.java`・`PlatformClient.java` | 同期の命令の窓口（`ContentResolver.call`。失敗は `{ok:false, error}`） |
| `…/IpcBench.java`・`PlatformAidlService.java`・`PlatformMessengerService.java`・`app/src/main/aidl/` | 往復時間の計測と比較用の IPC |
| `…/DebugControlReceiver.java`・`DebugPlatformReceiver.java` | adb から叩くデバッグの入口（exported=true。スパイク専用） |
| `…/SpikeLog.java`・`DeviceState.java` | 計測の目印（`MARK <名前> wall=… rt=…`）と端末の状態の 1 行 |
| `app/src/main/res/raw/quiet_tone.wav` | 鳴動音（-60 dBFS の短いビープ。`scripts/gen_quiet_tone.py` で作った）。端末の音量は変えない約束なので音源を小さくした |
| `scripts/common.sh` | 共通の関数（adb・logcat を試験の間ずっとファイルへ流す・目印を待つ・dumpsys の記録・利用者が操作中なら待つ） |
| `scripts/t0_setup.sh` 〜 `t8_package_replaced.sh`・`run_all.sh` | 試験ごとのスクリプト（下の表） |
| `scripts/extract_evidence.sh`・`summarize.sh` | 生ログから自パッケージの行を抜き、`results/summary.md` を作る（端末には触らない） |
| `scripts/cleanup.sh` | 後片付け（下の節） |
| `results/summary.md`・`results/lint-results-debug.txt` | 2026-09-27 の結果（lint のパスはこのフォルダからの相対に直した）。生ログはリポジトリに入れない（`.gitignore`） |

## ビルド

Gradle の wrapper は `runtime/android/` のものを使う（Gradle 9.3.1・AGP 9.1.0。このフォルダには wrapper を置かない）。
AndroidX は使わない（素の `android.app.*` だけ）。

```bash
# Git Bash。JAVA_HOME は Android Studio 同梱の JBR、ANDROID_HOME は SDK
export JAVA_HOME="C:/Program Files/Android/Android Studio/jbr"
export ANDROID_HOME="$LOCALAPPDATA/Android/Sdk"
cd runtime/android
./gradlew.bat -p spikes/platform_spike assembleDebug   # → spikes/platform_spike/app/build/outputs/apk/debug/app-debug.apk
./gradlew.bat -p spikes/platform_spike lintDebug       # 任意
./gradlew.bat --stop
```

`app/build/`・`.gradle/` は `runtime/android/.gitignore` で追跡しない。

## 実行

```bash
cd runtime/android/spikes/platform_spike
adb install app/build/outputs/apk/debug/app-debug.apk   # 初回は Play Protect の確認で長く止まることがある（§2.9.1）
bash scripts/run_all.sh        # t0 → t5 → t1・t2 → t4 → t6 → t3 → 抜粋と集計（results/summary.md）
```

- 端末とツールは環境変数で変えられる: `ADB`（adb.exe のパス）・`SERIAL`（端末のシリアル）。既定は開発機の値（`scripts/common.sh`）。
- 約束: 端末の設定・音量は変えない（`dumpsys battery unplug`・`deviceidle force-idle` は試験の後に必ず戻す）、
  `input keyevent` などの端末操作をしない、`adb reboot` は利用者の許可が要る、利用者が端末を操作中（画面が点き・ロック画面でなく・
  前面がランチャーでも自アプリでもない）なら 2 分おきに最大 20 分待つ。音は控えめな音源を短く（安全弁 20〜60 秒）。

| スクリプト | 内容 |
|---|---|
| `t0_setup.sh` | インストール直後の権限・特別なアクセスの既定値を記録し、試験に要るもの（通知の権限）だけ `pm grant` / `appops` で付ける |
| `t1_t2_lockscreen_task_remove.sh [秒] [id]` | 画面オフ・ロック中に N 秒後の予約 → 両プロセスを落とす → 鳴動から鳴動画面の最初のフレームまで → 鳴動中にタスクを消して音が続くか |
| `t3_doze.sh [秒]` | `battery unplug` → `force-idle` の下で N 秒後（既定 300 秒）の予約。利用者が使い始めたら予約を取り消して中止する |
| `t4_force_stop.sh` | `am force-stop` の後の予約と、次の起動（MainActivity）での張り直し |
| `t5_ipc_bench.sh` | プロセス間の往復時間（各 10 回の中央値を 3 回） |
| `t6_fgs.sh` | 予約を経ない背面からの前景サービスの起動（拒否されるはず）と、`systemExempted` での鳴動 |
| `t7_reboot_procedure.sh` | 再起動と Direct Boot の手順を表示するだけ。実行は `--reboot-permitted`（利用者の許可の後） |
| `t8_package_replaced.sh` | `adb install -r` で `MY_PACKAGE_REPLACED` が届いて張り直せるか（Play Protect の確認で止まる恐れ。未実施） |

## 後片付け（`scripts/cleanup.sh`）

```bash
bash scripts/cleanup.sh
```

鳴動の停止 → 全予約の取り消し → `dumpsys deviceidle unforce` → `dumpsys battery reset` → `am force-stop` → アンインストール →
最終状態の記録（`results/99_final_state/result.env`: パッケージの有無・保留中の予約の数・プロセス・Doze と電池の状態）。
何度流してもよい。試験を途中で止めたとき・端末を返す前に必ず流す。
**2026-09-27 の実施では、終盤に端末が USB から外れたためアンインストールがまだ（端末に `com.seedengine.platformspike` が残っている）。**

## W1 での再利用の指針

- **そのまま下敷きにしてよいもの**: `setAlarmClock` と予約 ID ごとの data URI の PendingIntent（`AlarmScheduler`）、端末保護ストレージの控えと
  原子的な書き込み（`AlarmStore`）、前景サービスの起動の順序と `onTaskRemoved` で止めない作り（`RingService`）、別名（activity-alias）経由の
  起動だけを信用する判定（`RingActivity`）、`{ok, error}` を返す `call` の流儀（`PlatformProvider`）、`scripts/common.sh` の関数（W1-7 の
  実機の確認で SEED の APK に向け直して使える）。
- **本番で変えるもの**（docs/app_platform_roadmap.md §2.2・§2.10）: Java の置き場は `com.seedengine.runtime.platform`（メインプロセス）と
  `.platform.service`（`:seed_platform`）。デバッグの受信機は `app/src/debug/` のマニフェストだけへ。前景サービスの種類は `mediaPlayback` に
  固定し、音は `USAGE_ALARM` に固定（Android 17 の背面の音の制限）。`AlarmReceiver` は真っ先に `startForegroundService` し、控えの fsync は
  その後。音の準備を前倒しする（冷えたプロセスでは `startForeground` から音まで約 340 ms）。エンジンへの知らせは放送ではなく Binder の
  コールバック（放送は受け手が別の放送を処理中だと 5 秒待たされた）。バイブ・音量の指定・音声フォーカス・重なりの順番待ちはスパイクに無い。
- **スパイクの手抜き**: `START_NOT_STICKY`、`MediaPlayer` の同期の `prepare`、通知のアイコンは端末の既定、exported=true のデバッグの受信機、
  種類を切り替えるために前景サービスに 3 種類（`mediaPlayback|systemExempted|specialUse`）を宣言、`RingActivity` を exported=true
  （SEED の MainActivity の代役のため）。
