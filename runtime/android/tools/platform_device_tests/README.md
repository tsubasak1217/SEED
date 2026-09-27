# SEED.Platform の実機試験スクリプト（W1-4b で使ったもの。2026-09-27）

Pixel 6a（Android 16）で `docs/app_platform_roadmap.md` §2.9.2 の試験（T1 ロック画面からの冷えた起動、T3 Doze、
T4 再起動、T6 PlatformEntry 経由の onNewIntent）を行った bash スクリプトの写し。結果の正典は roadmap §2.9.2 と
`docs/android.md` §25.12.8〜10。W1-7（通しの確認）で整えて使い回す前提で、そのまま置いてある（パスは
`C:\Users\k023g\.claude\jobs\434062fd\tmp\wop_w1_4b\` を前提にした所が残る。使う前に `common.sh` の置き場の変数を直す）。

- `common.sh` … logcat をファイルへ流し続ける・目印を待つ・dumpsys を記録する・利用者が操作中なら待つ・デバッグ受信機へ命令する
- `t1_cold_lockscreen.sh` … 予約 → 両プロセスを kill → 画面オフ・ロック中の発火から最初のフレームまでを計測
- `t3_doze.sh` … `deviceidle force-idle` の下の時刻精度（利用者が使い始めたら取り消して中止）
- `t4_reboot.sh` … `adb reboot` 後の張り直し（利用者の許可が要る）
- `t6_platform_entry_new_intent.sh` … PlatformEntry 経由の onNewIntent
- `analyze_ring.sh` … logcat から段階ごとの時刻を抜き出して表にする
- `kill_stream.ps1` … 流しっぱなしの logcat を止める

前提: デバッグ版 APK（`app/src/debug/` の `DebugPlatformReceiver` が SCHEDULE / CANCEL_ALL / STOP_RINGING / GET_RINGING / LIST を受ける）。
予約は必ず `max_ring_minutes` 1 で入れ、鳴動は 20 秒以内に止める。音を小さくするには `force_volume` 0・`vibrate` false・
`fade_in_seconds` 60。STREAM_ALARM の元の値を試験の前後で確かめる。
