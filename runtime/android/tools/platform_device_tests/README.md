# SEED.Platform の実機試験スクリプト（W1-4b で作り、W1-7 で整えた。2026-09-27）

Pixel 6a（W1-4b は Android 16。W1-7 の T4 の再起動で保留中の更新が当たり Android 17 になった）で `docs/app_platform_roadmap.md` §2.9.2 の試験を行う bash スクリプト（Git Bash）。結果の正典は roadmap §2.9.2 と
`docs/android.md` §25.12.8〜11。生ログ（logcat・dumpsys）は私物の端末の他のアプリの情報を含むので、**結果はリポジトリの外**へ書く。

```bash
export SEED_DEVICE_TEST_RESULTS=/c/Users/<you>/…/results   # 必須（common.sh が作る）。SERIAL・ADB・PKG も環境変数で変えられる
cd runtime/android/tools/platform_device_tests
bash t5_ring_restore.sh kill t5a t5_kill_run1 3             # 例: :seed_platform を殺して見張りで鳴動が戻るか
```

| スクリプト | 試験 | 端末の状態 |
|---|---|---|
| `common.sh` | 共通部（source する）。logcat をファイルへ流し続ける・目印を待つ・dumpsys を記録する・利用者が操作中なら待つ・**デバッグ受信機の命令 `ctl`**・静かな予約 `schedule_quiet`・保留中の予約と見張りの数・STREAM_ALARM の基準の記録と確かめ・戻し | — |
| `t1_cold_lockscreen.sh` | T1 ロック画面からの冷えた起動（AC-1・AC-12）。モード `task_remove`（T2・AC-2）・`kill_platform`（W1-4b の T5。直す前の記録）・`keep_activity` | 画面オフ・ロック中 |
| `t3_doze.sh` | T3 `deviceidle force-idle` の下の時刻精度（`battery unplug` / `unforce` / `reset` を必ず戻す） | ロック中・利用者が使っていない |
| `t4_reboot.sh --reboot-permitted` | T4 `adb reboot` の後の張り直しと Direct Boot（AC-4）。**`adb reboot` は利用者の許可が要る**。再起動の後はロック解除まで adb が `unauthorized` になりうる（W1-7 の実機） | ロック中 |
| `t5_ring_restore.sh <kill\|forcestop>` | T5 鳴動中に `:seed_platform` を `kill -9` → 見張りで鳴動と音量が戻る（W1-7）／`am force-stop` → 起動時の後始末（`ring_stopped(error)`・音量。`FOREGROUND_RETRY=1` で MainActivity を開いて戻るのを見る・AC-6 の偽装も兼ねる） | どちらでも（`FOREGROUND_RETRY` は解除中） |
| `t6_platform_entry_new_intent.sh` | T6 `PlatformEntry` 経由の `onNewIntent`（「開く」のタップは利用者の手） | 画面点灯・ロック中 |
| `t7_loop_seam.sh` | T7 既定の音のループの継ぎ目（`dumpsys media.audio_flinger` のトラックの記録。W1-7） | どちらでも |
| `t8_engine_kill.sh` | T8 鳴動中にエンジン（メインプロセス）を `kill -9` しても鳴り続ける（AC-9） | ロック解除中 |
| `ac10_save_kill_loop.sh [回数]` | AC-10 `SaveData.Save()` の直後・途中にメインプロセスを `kill -9` × 100 → `save.json` が常にある・壊れない・起動の回数が減らない、最後に壊した `save.json` から `.bak` で起動（`SaveSmoke.scene` を使う。pak に無ければ `files/assets/scenes/` へ置き、最後に消す） | ロック解除中 |
| `analyze_ring.sh` | logcat から段階ごとの時刻（予定時刻からの ms）を抜き出す | — |
| `kill_stream.ps1` | 流しっぱなしの logcat（adb.exe）を `-T` の値で探して止める | — |

約束（私物の端末。W1-4b・W1-7 の指示）:

- 予約は必ず `schedule_quiet`（`force_volume` 0＝STREAM_ALARM の最小の段階・振動なし・漸増 60 秒・安全弁 1 分）。鳴動は 20 秒以内に `STOP_RINGING`。
  試験の前に STREAM_ALARM の基準を記録し（`record_alarm_volume_baseline`）、後で比べて違えば元の値へ戻す（`verify_alarm_volume`。Android 17 では
  `cmd media_session volume --set` が効かなかったので `cmd audio set-volume` を使う）。
- `adb kill-server`・`logcat -c`・端末の設定の変更・`pm grant/revoke`・`appops set`・`input keyevent` はしない（`sleep_screen_if_locked` は
  `ALLOW_KEYEVENT_SLEEP=1` のときだけキーを送る）。利用者が端末を使い始めたら予約を取り消して待つ（2 分おき・最大 20 分）。
- デバッグ受信機（`DebugPlatformReceiver`）は送り手に `android.permission.DUMP` を求める（W1-7。adb の shell は持つ）。directBootAware でないので、
  再起動の後の最初のロック解除の前は届かない。受信機の命令でメインプロセスが起きると呼び鈴を登録し、`:seed_platform` の未読の記録を取り出して捨てるので、
  記録（`journal.json`）を確かめたいときは命令より先に `run-as … cat` で読む。
