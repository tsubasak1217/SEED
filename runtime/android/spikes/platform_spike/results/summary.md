# W1-0 スパイクの計測結果（results/ の自動集計）

- 生成: 2026-09-27 04:59:12（`scripts/summarize.sh`。抜粋は `scripts/extract_evidence.sh`）
- 端末: Pixel 6a（bluejay）google/bluejay/bluejay:16/CP1A.260405.005/15001963:user/release-keys（Android 16 / API 36）。2026-09-27 02:40 JST に getprop で確認
- 時刻: `wall`・`trigger_at` は端末の UTC epoch ミリ秒、logcat の先頭は epoch 秒。`*_ms` は予定時刻や鳴動開始からの差
- 生ログ（logcat・dumpsys）はリポジトリに入れていない（私物端末の他のアプリの情報を含むため）。各節の抜粋は自パッケージの行だけ
- 結果の読み方と判断の正典は docs/app_platform_roadmap.md §2.9.1

## 00 セットアップ: 権限と特別なアクセスの既定値（API 36）

| キー | 値 |
|---|---|
| installed | true |
| exact_before | true |
| fsi_before | true |
| notif_before | false |
| appop_fsi_before |  |
| appop_exact_before |  |
| use_exact_alarm_granted | 1 |
| exact_after | true |
| fsi_after | true |
| notif_after | true |
| implicit_broadcast_delivered | false |

<details><summary>証拠の抜粋（自パッケージの行）</summary>

```
# logcat（自パッケージの要の行。時刻は端末の epoch 秒）
1790448082.158  1409  1609 I am_proc_start: [0,1551,10423,com.seedengine.platformspike,broadcast,{com.seedengine.platformspike/com.seedengine.platformspike.DebugControlReceiver}]
1790448082.764  1551  1551 I SEEDPlatformSpike: MARK debug_control wall=1790448082764 rt=2915310056 pid=1551 proc=com.seedengine.platformspike action=com.seedengine.platformspike.STATUS
1790448082.795  1409  1609 I am_proc_start: [0,1582,10423,com.seedengine.platformspike:seed_platform,content provider,{com.seedengine.platformspike/com.seedengine.platformspike.PlatformProvider}]
1790448082.929  1582  1582 I SEEDPlatformSpike: MARK provider_create wall=1790448082929 rt=2915310221 pid=1582 proc=com.seedengine.platformspike:seed_platform
1790448082.956  1551  1581 I SEEDPlatformSpike: MARK status_perm wall=1790448082956 rt=2915310249 pid=1551 proc=com.seedengine.platformspike ok=true platform_pid=1582 json=can_schedule_exact=true can_use_full_screen_intent=true notifications_enabled=false sdk=36 interactive=false keyguard_l
1790448082.963  1551  1581 I SEEDPlatformSpike: MARK status_alarms wall=1790448082963 rt=2915310255 pid=1551 proc=com.seedengine.platformspike ok=true platform_pid=1582 json=[] error=null
1790448082.969  1551  1581 I SEEDPlatformSpike: MARK status_ring wall=1790448082969 rt=2915310261 pid=1551 proc=com.seedengine.platformspike ok=true platform_pid=1582 json={"service_alive":false} error=null
1790448082.975  1551  1581 I SEEDPlatformSpike: MARK status_main_process wall=1790448082975 rt=2915310268 pid=1551 proc=com.seedengine.platformspike can_schedule_exact=true can_use_full_screen_intent=true notifications_enabled=false sdk=36
1790448085.107  1551  1551 I SEEDPlatformSpike: MARK debug_control wall=1790448085107 rt=2915312399 pid=1551 proc=com.seedengine.platformspike action=com.seedengine.platformspike.STATUS
1790448085.112  1551  1677 I SEEDPlatformSpike: MARK status_perm wall=1790448085112 rt=2915312404 pid=1551 proc=com.seedengine.platformspike ok=true platform_pid=1582 json=can_schedule_exact=true can_use_full_screen_intent=true notifications_enabled=true sdk=36 interactive=false keyguard_lo
1790448085.113  1551  1677 I SEEDPlatformSpike: MARK status_alarms wall=1790448085113 rt=2915312405 pid=1551 proc=com.seedengine.platformspike ok=true platform_pid=1582 json=[] error=null
1790448085.114  1551  1677 I SEEDPlatformSpike: MARK status_ring wall=1790448085114 rt=2915312406 pid=1551 proc=com.seedengine.platformspike ok=true platform_pid=1582 json={"service_alive":false} error=null
```

</details>

## 01・02 ロック画面の上に出るまで / 最近のタスクから消した後

| キー | 値 |
|---|---|
| since | 1790449001.935 |
| screen_at_schedule | wakefulness=Dozing;keyguard_showing=true;top=（他のアプリ） |
| pid_main_after_kill |  |
| pid_platform_after_kill |  |
| trigger_at | 1790449096074 |
| screen_before_fire | wakefulness=Dozing;keyguard_showing=true;top=（他のアプリ） |
| ring_activity_shown | true |
| screen_while_ringing | wakefulness=Awake;keyguard_showing=true;top=com.seedengine.platformspike/.RingEntry |
| alarm_playing_before_remove | 1 |
| pid_main_before_remove | 4849 |
| pid_platform_before_remove | 4828 |
| ring_task | taskId=11767:;com.seedengine.platformspike/com.seedengine.platformspike.RingEntry |
| remove_ms | 1790449105091 |
| pid_main_after_remove_3s | 4849 |
| pid_platform_after_remove_3s | 4828 |
| alarm_playing_after_remove_3s | 1 |
| pid_main_after_remove_13s | 4849 |
| pid_platform_after_remove_13s | 4828 |
| alarm_playing_after_remove_13s | 1 |
| alarm_playing_after_stop | 0 |
| alarm_received_late_ms | 357 |
| alarm_received_interactive | false |
| alarm_received_keyguard_locked | true |
| alarm_received_device_idle | false |
| fgs_type | mediaPlayback |
| fgs_denied | 0 |
| fgs_allowed_log | Background_started_FGS:_Allowed |
| fgs_start_reason | ALARM_MANAGER_ALARM_CLOCK |
| ring_start_since_sched_ms | 733 |
| ring_start_after_receive_ms | 376 |
| activity_create_after_ring_ms | 20 |
| first_frame_after_ring_ms | 208 |
| focus_after_ring_ms | 527 |
| first_frame_after_sched_ms | 952 |
| ring_activity_trusted | true |
| ring_activity_component | ComponentInfo{com.seedengine.platformspike/com.seedengine.platformspike.RingEntry} |
| first_frame_keyguard_locked | true |
| first_frame_interactive | true |
| keyguard_occlude_transition | 2 |
| wm_activity_launch_time_ms | 518 |
| task_removed_mark | 1 |
| heartbeats_after_remove | 4 |
| heartbeat_playing_after_remove | true,true,true,true, |
| main_process_killed_log | Killing_3064:com.seedengine.platformspike/u0a423_(adj_900):_kill_background |
| ring_stop_reason | adb |
| ring_stop_rang_ms | 27885 |
| alarm_playing_note | 初回集計は uid の取り方の誤り（Android 16 は appId=）で 0 と出たため、保存した *_audio_playback.txt から数え直した |

<details><summary>証拠の抜粋（自パッケージの行）</summary>

```
# logcat（自パッケージの要の行。時刻は端末の epoch 秒）
1790449006.063  3064  3064 I SEEDPlatformSpike: MARK debug_control wall=1790449006063 rt=2916233355 pid=3064 proc=com.seedengine.platformspike action=com.seedengine.platformspike.SCHEDULE
1790449006.096  3032  4507 I SEEDPlatformSpike: MARK alarm_scheduled wall=1790449006096 rt=2916233388 pid=3032 proc=com.seedengine.platformspike:seed_platform id=t1 trigger_at=1790449096074 in_ms=89978 fgs_type=mediaPlayback max_ring_s=60
1790449006.097  3064  4506 I SEEDPlatformSpike: MARK schedule_result wall=1790449006097 rt=2916233390 pid=3064 proc=com.seedengine.platformspike ok=true trigger_at=1790449096074 error=null
1790449009.301  1409  4456 I ActivityManager: Killing 3064:com.seedengine.platformspike/u0a423 (adj 900): kill background
1790449009.302  1409  4456 I ActivityManager: Killing 3032:com.seedengine.platformspike:seed_platform/u0a423 (adj 905): kill background
1790449096.075  1409  1835 I device_idle_wake_from_idle: [0,*walarm*:com.seedengine.platformspike.ALARM_FIRE]
1790449096.129  1409  1609 I am_proc_start: [0,4828,10423,com.seedengine.platformspike:seed_platform,broadcast,{com.seedengine.platformspike/com.seedengine.platformspike.AlarmReceiver}]
1790449096.424  4828  4828 I SEEDPlatformSpike: MARK provider_create wall=1790449096424 rt=2916323716 pid=4828 proc=com.seedengine.platformspike:seed_platform
1790449096.442  4828  4828 I SEEDPlatformSpike: MARK alarm_received wall=1790449096442 rt=2916323734 pid=4828 proc=com.seedengine.platformspike:seed_platform id=t1 sched=1790449096074 late_ms=357 interactive=false keyguard_locked=true device_locked=true device_idle=false light_idle=false po
1790449096.466  1409  2318 I ActivityManager: Background started FGS: Allowed [callingPackage: com.seedengine.platformspike; callingUid: 10423; uidState: RCVR; uidBFSL: n/a; intent: Intent { act=com.seedengine.platformspike.RING_START xflg=0x4 cmp=com.seedengine.platformspike/.RingService (
1790449096.468  4828  4828 I SEEDPlatformSpike: MARK fgs_start_requested wall=1790449096468 rt=2916323761 pid=4828 proc=com.seedengine.platformspike:seed_platform origin=alarm id=t1
1790449096.472  4828  4828 I SEEDPlatformSpike: MARK service_create wall=1790449096472 rt=2916323765 pid=4828 proc=com.seedengine.platformspike:seed_platform interactive=false keyguard_locked=true device_locked=true device_idle=false light_idle=false power_save=false user_unlocked=true
1790449096.477  1409  4452 I am_foreground_service_start: [0,com.seedengine.platformspike/.RingService,0,ALARM_MANAGER_ALARM_CLOCK,36,36,0,0,0,1,UNKNOWN,2]
1790449096.478  4828  4828 I SEEDPlatformSpike: MARK fgs_started wall=1790449096478 rt=2916323770 pid=4828 proc=com.seedengine.platformspike:seed_platform id=t1 type=mediaPlayback type_flag=2 can_schedule_exact=true can_use_full_screen_intent=true notifications_enabled=true sdk=36
1790449096.501 31127 31127 I sysui_fullscreen_notification: 0|com.seedengine.platformspike|4101|null|10423
1790449096.542  1409  1609 I am_proc_start: [0,4849,10423,com.seedengine.platformspike,top-activity,{com.seedengine.platformspike/com.seedengine.platformspike.RingEntry}]
1790449096.816 31127 31141 V WindowManagerShell: Transition requested (#12500): android.os.BinderProxy@90e5612 TransitionRequestInfo { type = KEYGUARD_OCCLUDE, triggerTask = TaskInfo{userId=0 taskId=11767 effectiveUid=10423 displayId=0 isRunning=true baseIntent=Intent { flg=0x10040000 cmp=c
1790449096.818  4828  4828 I SEEDPlatformSpike: MARK ring_start wall=1790449096818 rt=2916324110 pid=4828 proc=com.seedengine.platformspike:seed_platform id=t1 sched=1790449096074 since_sched_ms=733 usage=alarm fgs_type=mediaPlayback interactive=true keyguard_locked=true device_locked=true 
1790449096.838  4849  4849 I SEEDPlatformSpike: MARK ring_activity_create wall=1790449096838 rt=2916324130 pid=4849 proc=com.seedengine.platformspike trusted=true component=ComponentInfo{com.seedengine.platformspike/com.seedengine.platformspike.RingEntry} id=t1 since_ring_ms=357 interactive
1790449096.920  4849  4849 I SEEDPlatformSpike: MARK ring_activity_resume wall=1790449096920 rt=2916324212 pid=4849 proc=com.seedengine.platformspike id=t1 interactive=true keyguard_locked=true device_locked=true device_idle=false light_idle=false power_save=false user_unlocked=true
1790449096.947  4849  4849 I SEEDPlatformSpike: MARK ring_activity_resume wall=1790449096947 rt=2916324240 pid=4849 proc=com.seedengine.platformspike id=t1 interactive=true keyguard_locked=true device_locked=true device_idle=false light_idle=false power_save=false user_unlocked=true
1790449096.985  1409  1592 V WindowManager: Sent Transition (#12500) createdAt=09-27 03:58:16.813 via request=TransitionRequestInfo { type = KEYGUARD_OCCLUDE, triggerTask = TaskInfo{userId=0 taskId=11767 effectiveUid=10423 displayId=0 isRunning=true baseIntent=Intent { flg=0x10040000 cmp=co
1790449097.026  4849  4849 I SEEDPlatformSpike: MARK ring_activity_first_frame wall=1790449097026 rt=2916324318 pid=4849 proc=com.seedengine.platformspike id=t1 since_ring_ms=552 interactive=true keyguard_locked=true device_locked=true device_idle=false light_idle=false power_save=false use
1790449097.031  1409  1592 I wm_activity_launch_time: [0,174053882,com.seedengine.platformspike/.RingEntry,518]
1790449097.345  4849  4849 I SEEDPlatformSpike: MARK ring_activity_focus wall=1790449097345 rt=2916324638 pid=4849 proc=com.seedengine.platformspike id=t1 since_ring_ms=872 interactive=true keyguard_locked=true device_locked=true device_idle=false light_idle=false power_save=false user_unlo
1790449101.820  4828  4828 I SEEDPlatformSpike: MARK ring_heartbeat wall=1790449101819 rt=2916329112 pid=4828 proc=com.seedengine.platformspike:seed_platform id=t1 n=1 playing=true elapsed_ms=5346
1790449105.223  4828  4828 I SEEDPlatformSpike: MARK task_removed wall=1790449105223 rt=2916332515 pid=4828 proc=com.seedengine.platformspike:seed_platform root=ComponentInfo{com.seedengine.platformspike/com.seedengine.platformspike.RingEntry} playing=true id=t1
1790449105.244  4849  4849 I SEEDPlatformSpike: MARK ring_activity_destroy wall=1790449105244 rt=2916332536 pid=4849 proc=com.seedengine.platformspike id=t1 finishing=true
1790449105.421  1409  2588 I wm_task_removed: [11767,11767,0,removeChild, last child = ActivityRecord{174053882 u0 com.seedengine.platformspike/.RingEntry t-1 f}} in Task{feedda1 #11767 type=standard A=10423:com.seedengine.platformspike}]
1790449106.823  4828  4828 I SEEDPlatformSpike: MARK ring_heartbeat wall=1790449106822 rt=2916334115 pid=4828 proc=com.seedengine.platformspike:seed_platform id=t1 n=2 playing=true elapsed_ms=10349
1790449110.333  1409  2318 I AS.AudioService: AudioHardening background playback would be muted for com.seedengine.platformspike (10423), level: full
1790449111.826  4828  4828 I SEEDPlatformSpike: MARK ring_heartbeat wall=1790449111825 rt=2916339118 pid=4828 proc=com.seedengine.platformspike:seed_platform id=t1 n=3 playing=true elapsed_ms=15352
1790449116.828  4828  4828 I SEEDPlatformSpike: MARK ring_heartbeat wall=1790449116828 rt=2916344120 pid=4828 proc=com.seedengine.platformspike:seed_platform id=t1 n=4 playing=true elapsed_ms=20355
1790449121.832  4828  4828 I SEEDPlatformSpike: MARK ring_heartbeat wall=1790449121831 rt=2916349124 pid=4828 proc=com.seedengine.platformspike:seed_platform id=t1 n=5 playing=true elapsed_ms=25358
1790449124.353  4849  4849 I SEEDPlatformSpike: MARK debug_control wall=1790449124353 rt=2916351646 pid=4849 proc=com.seedengine.platformspike action=com.seedengine.platformspike.STOP
1790449124.358  4849  5263 I SEEDPlatformSpike: MARK stop_result wall=1790449124358 rt=2916351650 pid=4849 proc=com.seedengine.platformspike ok=true platform_pid=4828 json={"was_ringing":true} error=null
1790449124.376  1409  2589 I am_foreground_service_stop: [0,com.seedengine.platformspike/.RingService,0,ALARM_MANAGER_ALARM_CLOCK,36,36,0,0,27899,1,STOP_FOREGROUND,2]
1790449124.377  4828  4828 I SEEDPlatformSpike: MARK ring_stop wall=1790449124377 rt=2916351669 pid=4828 proc=com.seedengine.platformspike:seed_platform reason=adb id=t1 rang_ms=27885
1790449124.384  4828  4828 I SEEDPlatformSpike: MARK service_destroy wall=1790449124384 rt=2916351676 pid=4828 proc=com.seedengine.platformspike:seed_platform
# 再生の設定（dumpsys audio。uid 10423 の行）
0_before: （再生なし）
1_scheduled: （再生なし）
2_ringing: u/pid:10423/4828 state:started attr:AudioAttributes: usage=USAGE_ALARM
3_after_remove_3s: u/pid:10423/4828 state:started attr:AudioAttributes: usage=USAGE_ALARM
4_after_remove_13s: u/pid:10423/4828 state:started attr:AudioAttributes: usage=USAGE_ALARM
5_after_stop: （再生なし）
# プロセスと Doze の状態（snap の記録）
0_before: pid_main=3064 pid_platform=3032 mForceIdle=false mState=ACTIVE mLightState=ACTIVE 
1_scheduled: pid_main= pid_platform= mForceIdle=false mState=ACTIVE mLightState=ACTIVE 
2_ringing: pid_main=4849 pid_platform=4828 mForceIdle=false mState=ACTIVE mLightState=ACTIVE 
3_after_remove_3s: pid_main=4849 pid_platform=4828 mForceIdle=false mState=ACTIVE mLightState=ACTIVE 
4_after_remove_13s: pid_main=4849 pid_platform=4828 mForceIdle=false mState=ACTIVE mLightState=ACTIVE 
5_after_stop: pid_main=4849 pid_platform=4828 mForceIdle=false mState=ACTIVE mLightState=ACTIVE 
# 予約の中身（dumpsys alarm）
    RTC_WAKEUP #8: Alarm{f44517a type 0 origWhen 1790449096074 whenElapsed 2916323366 com.seedengine.platformspike}
      tag=*walarm*:com.seedengine.platformspike.ALARM_FIRE
      type=RTC_WAKEUP origWhen=2026-09-27 03:58:16.074 window=0 exactAllowReason=policy_permission repeatInterval=0 count=0 flags=0x3
      policyWhenElapsed: requester=+1m22s941ms app_standby=-7s37ms device_idle=-- battery_saver=--
      whenElapsed=+1m22s941ms maxWhenElapsed=+1m22s941ms
      Alarm clock:
        triggerTime=2026-09-27 03:58:16.074
        showIntent=PendingIntent{f460d2b: PendingIntentRecord{318e588 com.seedengine.platformspike startActivity}}
      operation=PendingIntent{6b95b21: PendingIntentRecord{3360b46 com.seedengine.platformspike broadcastIntent}}
      idle-options=Bundle[{android.pendingIntent.backgroundActivityAllowed=2, android:broadcast.temporaryAppAllowlistReasonCode=301, android:broadcast.temporaryAppAllowlistDuration=10000, android:broadcast.temporaryAppAllowlistReason=, android:broadcast.temporaryAppAllowlistType=0, android:broadcast.flags=8}]
```

</details>

## 01a 1 回目の鳴動（安全弁で停止。ログから再構成）

| キー | 値 |
|---|---|
| note | 1回目の鳴動（スクリプトの待ち時間の不具合で途中から放置）。main バッファは流れて MARK は失われたため system/events バッファから再構成 |
| condition | 画面オフ(Dozing/AOD)・ロック中・予約後に両プロセスを am kill |
| trigger_at | 1790448301101 |
| wake_from_idle_offset_ms | 0 |
| platform_proc_start_offset_ms | 68 |
| fgs_start_offset_ms | 219 |
| fgs_start_reason | ALARM_MANAGER_ALARM_CLOCK |
| fullscreen_intent_offset_ms | 268 |
| main_proc_start_offset_ms | 398 |
| keyguard_occlude_request_offset_ms | 704 |
| ring_activity_oncreate_offset_ms | 767 |
| wm_activity_launch_time_ms | 787 |
| input_focus_offset_ms | 1222 |
| safety_valve_fgs_duration_ms | 60452 |
| activity_finish_after_fgs_stop_ms | 13 |

<details><summary>証拠の抜粋（自パッケージの行）</summary>

```
# logcat（自パッケージの要の行。時刻は端末の epoch 秒）
1790448214.410  1409  2589 I ActivityManager: Killing 1979:com.seedengine.platformspike/u0a423 (adj 905): kill background
1790448214.411  1409  2589 I am_kill : [0,1979,com.seedengine.platformspike,905,kill background,79208]
1790448214.412  1409  2589 I ActivityManager: Killing 2002:com.seedengine.platformspike:seed_platform/u0a423 (adj 905): kill background
1790448301.101  1409  1835 I device_idle_wake_from_idle: [0,*walarm*:com.seedengine.platformspike.ALARM_FIRE]
1790448301.169  1409  1609 I am_proc_start: [0,3032,10423,com.seedengine.platformspike:seed_platform,broadcast,{com.seedengine.platformspike/com.seedengine.platformspike.AlarmReceiver}]
1790448301.312  1409  2314 I ActivityManager: Background started FGS: Allowed [callingPackage: com.seedengine.platformspike; callingUid: 10423; uidState: RCVR; uidBFSL: n/a; intent: Intent { act=com.seedengine.platformspike.RING_START xflg=0x4 cmp=com.seedengine.platformspike/.RingService (
1790448301.320  1409  4456 I am_foreground_service_start: [0,com.seedengine.platformspike/.RingService,0,ALARM_MANAGER_ALARM_CLOCK,36,36,0,0,0,1,UNKNOWN,2]
1790448301.369 31127 31127 I sysui_fullscreen_notification: 0|com.seedengine.platformspike|4101|null|10423
1790448301.499  1409  1609 I am_proc_start: [0,3064,10423,com.seedengine.platformspike,top-activity,{com.seedengine.platformspike/com.seedengine.platformspike.RingEntry}]
1790448301.805 31127 31141 V WindowManagerShell: Transition requested (#12497): android.os.BinderProxy@809e549 TransitionRequestInfo { type = KEYGUARD_OCCLUDE, triggerTask = TaskInfo{userId=0 taskId=11766 effectiveUid=10423 displayId=0 isRunning=true baseIntent=Intent { flg=0x10040000 cmp=c
1790448301.947  1409  1592 V WindowManager: Sent Transition (#12497) createdAt=09-27 03:45:01.804 via request=TransitionRequestInfo { type = KEYGUARD_OCCLUDE, triggerTask = TaskInfo{userId=0 taskId=11766 effectiveUid=10423 displayId=0 isRunning=true baseIntent=Intent { flg=0x10040000 cmp=co
1790448302.172  1409  1592 I wm_activity_launch_time: [0,188170541,com.seedengine.platformspike/.RingEntry,787]
1790448361.772  1409  4267 I am_foreground_service_stop: [0,com.seedengine.platformspike/.RingService,0,ALARM_MANAGER_ALARM_CLOCK,36,36,0,0,60452,1,STOP_FOREGROUND,2]
1790448361.978  1409  1564 I wm_task_removed: [11766,11766,0,removeChild, last child = ActivityRecord{188170541 u0 com.seedengine.platformspike/.RingEntry t-1 f}} in Task{76c6ae #11766 type=standard A=10423:com.seedengine.platformspike}]
```

</details>

## 03（1 回目）Doze の下の時刻精度 — 利用者の操作で無効

| キー | 値 |
|---|---|
| note | Doze の1回目。force-idle で IDLE に入った後、04:04:26 に利用者が指紋でロックを解除してゲームを始めたため、発火時は IDLE ではなく INACTIVE（Doze の測定としては無効）。代わりに「使用中の端末」の標本になった |
| force_idle_output | Now_forced_in_to_deep_idle_mode |
| idle_state_after_schedule | mForceIdle=true;mState=IDLE;mLightState=OVERRIDE |
| idle_state_at_fire | mForceIdle=true;mState=INACTIVE;mLightState=INACTIVE（利用者の操作で IDLE を抜けた） |
| trigger_at | 1790449703492 |
| wake_from_idle_offset_ms | 1 |
| platform_proc_start_offset_ms | 47 |
| fgs_start_offset_ms | 1788 |
| fgs_start_reason | ALARM_MANAGER_ALARM_CLOCK |
| presentation | heads-up 通知（sysui_heads_up_status=1）。端末使用中のためフルスクリーンの画面は起動されない |
| audio_hardening_log | AudioHardening background playback would be muted ... level: full（ログのみ・消音はされていない） |
| ring_stop_reason | timeout |
| ring_stop_rang_ms | 20238 |
| usb_disconnect | 04:05:19 に USB が切れ logcat の流しが止まった（main バッファの MARK は失われた） |
| restore | deviceidle unforce と battery reset は実行済み（mForceIdle=false・mState=ACTIVE・USB powered: true） |

<details><summary>証拠の抜粋（自パッケージの行）</summary>

```
# logcat（自パッケージの要の行。時刻は端末の epoch 秒）
1790449391.659  1409  4456 I ActivityManager: Force stopping com.seedengine.platformspike appid=10423 user=0: from pid 6213
1790449391.660  1409  4456 I ActivityManager: Killing 5547:com.seedengine.platformspike/u0a423 (adj 0): stop com.seedengine.platformspike due to from pid 6213
1790449391.663  1409  4456 I wm_task_removed: [11768,11768,0,removeChild, last child = ActivityRecord{25207826 u0 com.seedengine.platformspike/.MainActivity t-1 f}} in Task{daf87e3 #11768 type=standard A=10423:com.seedengine.platformspike}]
1790449391.665  1409  4456 I ActivityManager: Killing 5855:com.seedengine.platformspike:seed_platform/u0a423 (adj 905): stop com.seedengine.platformspike due to from pid 6213
1790449403.214  1409  1609 I am_proc_start: [0,6387,10423,com.seedengine.platformspike,broadcast,{com.seedengine.platformspike/com.seedengine.platformspike.DebugControlReceiver}]
1790449403.373  6387  6387 I SEEDPlatformSpike: MARK debug_control wall=1790449403373 rt=2916630665 pid=6387 proc=com.seedengine.platformspike action=com.seedengine.platformspike.SCHEDULE
1790449403.378  1409  1609 I am_proc_start: [0,6403,10423,com.seedengine.platformspike:seed_platform,broadcast,{com.seedengine.platformspike/com.seedengine.platformspike.BootReceiver}]
1790449403.486  6403  6403 I SEEDPlatformSpike: MARK provider_create wall=1790449403486 rt=2916630778 pid=6403 proc=com.seedengine.platformspike:seed_platform
1790449403.513  6403  6403 I SEEDPlatformSpike: MARK boot_received wall=1790449403513 rt=2916630805 pid=6403 proc=com.seedengine.platformspike:seed_platform action=android.intent.action.LOCKED_BOOT_COMPLETED interactive=true keyguard_locked=true device_locked=true device_idle=true light_idl
1790449403.517  6403  6413 I SEEDPlatformSpike: MARK alarm_scheduled wall=1790449403517 rt=2916630809 pid=6403 proc=com.seedengine.platformspike:seed_platform id=t3 trigger_at=1790449703492 in_ms=299976 fgs_type=mediaPlayback max_ring_s=20
1790449403.522  6387  6401 I SEEDPlatformSpike: MARK schedule_result wall=1790449403522 rt=2916630814 pid=6387 proc=com.seedengine.platformspike ok=true trigger_at=1790449703492 error=null
1790449403.544  6403  6403 I SEEDPlatformSpike: MARK alarm_scheduled wall=1790449403543 rt=2916630836 pid=6403 proc=com.seedengine.platformspike:seed_platform id=t3 trigger_at=1790449703492 in_ms=299949 fgs_type=mediaPlayback max_ring_s=20
1790449403.560  6403  6403 I SEEDPlatformSpike: MARK rearm_all wall=1790449403560 rt=2916630852 pid=6403 proc=com.seedengine.platformspike:seed_platform reason=boot:android.intent.action.LOCKED_BOOT_COMPLETED stored=1 pi_alive_before=1 armed=1 missed=0 next_alarm_clock_before=1790449703492 
1790449403.574  6403  6403 I SEEDPlatformSpike: MARK boot_received wall=1790449403574 rt=2916630866 pid=6403 proc=com.seedengine.platformspike:seed_platform action=android.intent.action.BOOT_COMPLETED interactive=true keyguard_locked=true device_locked=true device_idle=true light_idle=false
1790449403.602  6403  6403 I SEEDPlatformSpike: MARK alarm_scheduled wall=1790449403602 rt=2916630895 pid=6403 proc=com.seedengine.platformspike:seed_platform id=t3 trigger_at=1790449703492 in_ms=299890 fgs_type=mediaPlayback max_ring_s=20
1790449403.627  6403  6403 I SEEDPlatformSpike: MARK rearm_all wall=1790449403627 rt=2916630919 pid=6403 proc=com.seedengine.platformspike:seed_platform reason=boot:android.intent.action.BOOT_COMPLETED stored=1 pi_alive_before=1 armed=1 missed=0 next_alarm_clock_before=1790449703492 next_al
1790449403.642  6403  6403 I SEEDPlatformSpike: MARK boot_received wall=1790449403642 rt=2916630935 pid=6403 proc=com.seedengine.platformspike:seed_platform action=android.intent.action.LOCKED_BOOT_COMPLETED interactive=true keyguard_locked=true device_locked=true device_idle=true light_idl
1790449403.662  6403  6403 I SEEDPlatformSpike: MARK alarm_scheduled wall=1790449403662 rt=2916630954 pid=6403 proc=com.seedengine.platformspike:seed_platform id=t3 trigger_at=1790449703492 in_ms=299830 fgs_type=mediaPlayback max_ring_s=20
1790449403.674  6403  6403 I SEEDPlatformSpike: MARK rearm_all wall=1790449403674 rt=2916630966 pid=6403 proc=com.seedengine.platformspike:seed_platform reason=boot:android.intent.action.LOCKED_BOOT_COMPLETED stored=1 pi_alive_before=1 armed=1 missed=0 next_alarm_clock_before=1790449703492 
1790449403.705  6403  6403 I SEEDPlatformSpike: MARK boot_received wall=1790449403705 rt=2916630997 pid=6403 proc=com.seedengine.platformspike:seed_platform action=android.intent.action.BOOT_COMPLETED interactive=true keyguard_locked=true device_locked=true device_idle=true light_idle=false
1790449403.730  6403  6403 I SEEDPlatformSpike: MARK alarm_scheduled wall=1790449403730 rt=2916631022 pid=6403 proc=com.seedengine.platformspike:seed_platform id=t3 trigger_at=1790449703492 in_ms=299762 fgs_type=mediaPlayback max_ring_s=20
1790449403.741  6403  6403 I SEEDPlatformSpike: MARK rearm_all wall=1790449403740 rt=2916631032 pid=6403 proc=com.seedengine.platformspike:seed_platform reason=boot:android.intent.action.BOOT_COMPLETED stored=1 pi_alive_before=1 armed=1 missed=0 next_alarm_clock_before=1790449703492 next_al
1790449406.174  1409  4456 I ActivityManager: Killing 6387:com.seedengine.platformspike/u0a423 (adj 905): kill background
1790449406.175  1409  4456 I ActivityManager: Killing 6403:com.seedengine.platformspike:seed_platform/u0a423 (adj 905): kill background
1790449406.176  1409  4456 I am_kill : [0,6403,com.seedengine.platformspike:seed_platform,905,kill background,92364]
1790449703.493  1409  1835 I device_idle_wake_from_idle: [0,*walarm*:com.seedengine.platformspike.ALARM_FIRE]
1790449703.539  1409  1609 I am_proc_start: [0,8618,10423,com.seedengine.platformspike:seed_platform,broadcast,{com.seedengine.platformspike/com.seedengine.platformspike.AlarmReceiver}]
1790449705.266  1409  4166 I ActivityManager: Background started FGS: Allowed [callingPackage: com.seedengine.platformspike; callingUid: 10423; uidState: RCVR; uidBFSL: n/a; intent: Intent { act=com.seedengine.platformspike.RING_START xflg=0x4 cmp=com.seedengine.platformspike/.RingService (
1790449705.280  1409  1721 I am_foreground_service_start: [0,com.seedengine.platformspike/.RingService,0,ALARM_MANAGER_ALARM_CLOCK,36,36,0,0,0,1,UNKNOWN,2]
1790449705.506 31127 31127 I sysui_heads_up_status: [0|com.seedengine.platformspike|4101|null|10423,1]
1790449705.936  1409  4289 I AS.AudioService: AudioHardening background playback would be muted for com.seedengine.platformspike (10423), level: full
1790449725.542  1409  4289 I am_foreground_service_stop: [0,com.seedengine.platformspike/.RingService,0,ALARM_MANAGER_ALARM_CLOCK,36,36,0,0,20262,1,STOP_FOREGROUND,2]
1790449725.551  8618  8618 I SEEDPlatformSpike: MARK ring_stop wall=1790449725550 rt=2916952843 pid=8618 proc=com.seedengine.platformspike:seed_platform reason=timeout id=t3 rang_ms=20238
1790449725.562 31127 31127 I sysui_heads_up_status: [0|com.seedengine.platformspike|4101|null|10423,0]
1790449725.563  8618  8618 I SEEDPlatformSpike: MARK service_destroy wall=1790449725563 rt=2916952855 pid=8618 proc=com.seedengine.platformspike:seed_platform
# 再生の設定（dumpsys audio。uid 10423 の行）
0_before: （再生なし）
1_scheduled: （再生なし）
2_ringing: （再生なし）
3_after_restore: （再生なし）
# プロセスと Doze の状態（snap の記録）
0_before: pid_main= pid_platform= mForceIdle=false mState=ACTIVE mLightState=ACTIVE 
1_scheduled: pid_main=6387 pid_platform=6403 mForceIdle=true mState=IDLE mLightState=OVERRIDE 
2_ringing: pid_main= pid_platform=8618 mForceIdle=true mState=SENSING mLightState=IDLE 
3_after_restore: pid_main=10284 pid_platform=8618 mForceIdle=false mState=ACTIVE mLightState=ACTIVE 
# 予約の中身（dumpsys alarm）
    RTC_WAKEUP #12: Alarm{8062415 type 0 origWhen 1790449703492 whenElapsed 2916930785 com.seedengine.platformspike}
      tag=*walarm*:com.seedengine.platformspike.ALARM_FIRE
      type=RTC_WAKEUP origWhen=2026-09-27 04:08:23.492 window=0 exactAllowReason=policy_permission repeatInterval=0 count=0 flags=0x3
      policyWhenElapsed: requester=+4m57s990ms app_standby=-1s776ms device_idle=-1s776ms battery_saver=--
      whenElapsed=+4m57s990ms maxWhenElapsed=+4m57s990ms
      Alarm clock:
        triggerTime=2026-09-27 04:08:23.492
        showIntent=PendingIntent{6d9f32a: PendingIntentRecord{f5f851b com.seedengine.platformspike startActivity}}
      operation=PendingIntent{41c16b8: PendingIntentRecord{931ec91 com.seedengine.platformspike broadcastIntent}}
      idle-options=Bundle[{android.pendingIntent.backgroundActivityAllowed=2, android:broadcast.temporaryAppAllowlistReasonCode=301, android:broadcast.temporaryAppAllowlistDuration=10000, android:broadcast.temporaryAppAllowlistReason=, android:broadcast.temporaryAppAllowlistType=0, android:broadcast.flags=8}]
```

</details>

## 04 強制停止の後

| キー | 値 |
|---|---|
| alarm_lines_before | 2 |
| alarm_lines_after_force_stop | 0 |
| package_stopped_flag | stopped=true |
| broadcast_delivered_while_stopped | true |
| alarm_lines_after_broadcast | 2 |
| rearm_on_launch | wall=1790449243878;rt=2916471171;pid=5561;proc=com.seedengine.platformspike:seed_platform;reason=app_launch;stored=1;pi_alive_before=1;armed=1;missed=0;next_alarm_clock_before=1790449834370;next_alarm_clock_creator=com.seedengine.platformspike |
| alarm_lines_after_relaunch | 2 |
| alarm_lines_after_cancel | 0 |

<details><summary>証拠の抜粋（自パッケージの行）</summary>

```
# logcat（自パッケージの要の行。時刻は端末の epoch 秒）
1790449234.365  4849  4849 I SEEDPlatformSpike: MARK debug_control wall=1790449234364 rt=2916461657 pid=4849 proc=com.seedengine.platformspike action=com.seedengine.platformspike.SCHEDULE
1790449234.381  4828  4840 I SEEDPlatformSpike: MARK alarm_scheduled wall=1790449234381 rt=2916461673 pid=4828 proc=com.seedengine.platformspike:seed_platform id=t4 trigger_at=1790449834370 in_ms=599990 fgs_type=mediaPlayback max_ring_s=60
1790449234.384  4849  5410 I SEEDPlatformSpike: MARK schedule_result wall=1790449234384 rt=2916461676 pid=4849 proc=com.seedengine.platformspike ok=true trigger_at=1790449834370 error=null
1790449237.828  1409  2284 I ActivityManager: Force stopping com.seedengine.platformspike appid=10423 user=0: from pid 5472
1790449237.830  1409  2284 I ActivityManager: Killing 4849:com.seedengine.platformspike/u0a423 (adj 905): stop com.seedengine.platformspike due to from pid 5472
1790449237.832  1409  2284 I ActivityManager: Killing 4828:com.seedengine.platformspike:seed_platform/u0a423 (adj 905): stop com.seedengine.platformspike due to from pid 5472
1790449243.000  1409  1609 I am_proc_start: [0,5547,10423,com.seedengine.platformspike,broadcast,{com.seedengine.platformspike/com.seedengine.platformspike.DebugControlReceiver}]
1790449243.150  5547  5547 I SEEDPlatformSpike: MARK debug_control wall=1790449243150 rt=2916470442 pid=5547 proc=com.seedengine.platformspike action=com.seedengine.platformspike.STATUS
1790449243.154  1409  1609 I am_proc_start: [0,5561,10423,com.seedengine.platformspike:seed_platform,broadcast,{com.seedengine.platformspike/com.seedengine.platformspike.BootReceiver}]
1790449243.260  5561  5561 I SEEDPlatformSpike: MARK provider_create wall=1790449243260 rt=2916470552 pid=5561 proc=com.seedengine.platformspike:seed_platform
1790449243.297  5561  5561 I SEEDPlatformSpike: MARK boot_received wall=1790449243296 rt=2916470589 pid=5561 proc=com.seedengine.platformspike:seed_platform action=android.intent.action.LOCKED_BOOT_COMPLETED interactive=false keyguard_locked=true device_locked=true device_idle=false light_i
1790449243.305  5547  5560 I SEEDPlatformSpike: MARK status_perm wall=1790449243305 rt=2916470597 pid=5547 proc=com.seedengine.platformspike ok=true platform_pid=5561 json=can_schedule_exact=true can_use_full_screen_intent=true notifications_enabled=true sdk=36 interactive=false keyguard_lo
1790449243.316  5547  5560 I SEEDPlatformSpike: MARK status_alarms wall=1790449243316 rt=2916470608 pid=5547 proc=com.seedengine.platformspike ok=true platform_pid=5561 json=[{"id":"t4","trigger_at":1790449834370,"fgs_type":"mediaPlayback","max_ring_s":60,"armed":true}] error=null
1790449243.324  5547  5560 I SEEDPlatformSpike: MARK status_ring wall=1790449243324 rt=2916470616 pid=5547 proc=com.seedengine.platformspike ok=true platform_pid=5561 json={"service_alive":false} error=null
1790449243.330  5561  5561 I SEEDPlatformSpike: MARK rearm_all wall=1790449243330 rt=2916470622 pid=5561 proc=com.seedengine.platformspike:seed_platform reason=boot:android.intent.action.LOCKED_BOOT_COMPLETED stored=1 pi_alive_before=0 armed=1 missed=0 next_alarm_clock_before=-1 next_alarm_
1790449243.333  5547  5560 I SEEDPlatformSpike: MARK status_main_process wall=1790449243333 rt=2916470625 pid=5547 proc=com.seedengine.platformspike can_schedule_exact=true can_use_full_screen_intent=true notifications_enabled=true sdk=36
1790449243.345  5561  5561 I SEEDPlatformSpike: MARK boot_received wall=1790449243342 rt=2916470634 pid=5561 proc=com.seedengine.platformspike:seed_platform action=android.intent.action.BOOT_COMPLETED interactive=false keyguard_locked=true device_locked=true device_idle=false light_idle=fal
1790449243.359  5561  5561 I SEEDPlatformSpike: MARK alarm_scheduled wall=1790449243359 rt=2916470651 pid=5561 proc=com.seedengine.platformspike:seed_platform id=t4 trigger_at=1790449834370 in_ms=591012 fgs_type=mediaPlayback max_ring_s=60
1790449243.375  5561  5561 I SEEDPlatformSpike: MARK rearm_all wall=1790449243373 rt=2916470666 pid=5561 proc=com.seedengine.platformspike:seed_platform reason=boot:android.intent.action.BOOT_COMPLETED stored=1 pi_alive_before=1 armed=1 missed=0 next_alarm_clock_before=1790449834370 next_al
1790449243.387  5561  5561 I SEEDPlatformSpike: MARK boot_received wall=1790449243387 rt=2916470679 pid=5561 proc=com.seedengine.platformspike:seed_platform action=android.intent.action.LOCKED_BOOT_COMPLETED interactive=false keyguard_locked=true device_locked=true device_idle=false light_i
1790449243.398  5561  5561 I SEEDPlatformSpike: MARK alarm_scheduled wall=1790449243398 rt=2916470690 pid=5561 proc=com.seedengine.platformspike:seed_platform id=t4 trigger_at=1790449834370 in_ms=590972 fgs_type=mediaPlayback max_ring_s=60
1790449243.407  5561  5561 I SEEDPlatformSpike: MARK rearm_all wall=1790449243406 rt=2916470699 pid=5561 proc=com.seedengine.platformspike:seed_platform reason=boot:android.intent.action.LOCKED_BOOT_COMPLETED stored=1 pi_alive_before=1 armed=1 missed=0 next_alarm_clock_before=1790449834370 
1790449243.416  5561  5561 I SEEDPlatformSpike: MARK boot_received wall=1790449243415 rt=2916470708 pid=5561 proc=com.seedengine.platformspike:seed_platform action=android.intent.action.BOOT_COMPLETED interactive=false keyguard_locked=true device_locked=true device_idle=false light_idle=fal
1790449243.434  5561  5561 I SEEDPlatformSpike: MARK alarm_scheduled wall=1790449243434 rt=2916470726 pid=5561 proc=com.seedengine.platformspike:seed_platform id=t4 trigger_at=1790449834370 in_ms=590936 fgs_type=mediaPlayback max_ring_s=60
1790449243.443  5561  5561 I SEEDPlatformSpike: MARK rearm_all wall=1790449243443 rt=2916470735 pid=5561 proc=com.seedengine.platformspike:seed_platform reason=boot:android.intent.action.BOOT_COMPLETED stored=1 pi_alive_before=1 armed=1 missed=0 next_alarm_clock_before=1790449834370 next_al
1790449243.868  5547  5547 I SEEDPlatformSpike: MARK main_activity_create wall=1790449243868 rt=2916471160 pid=5547 proc=com.seedengine.platformspike interactive=false keyguard_locked=true device_locked=true device_idle=false light_idle=false power_save=false user_unlocked=true
1790449243.875  5561  5570 I SEEDPlatformSpike: MARK alarm_scheduled wall=1790449243875 rt=2916471167 pid=5561 proc=com.seedengine.platformspike:seed_platform id=t4 trigger_at=1790449834370 in_ms=590495 fgs_type=mediaPlayback max_ring_s=60
1790449243.878  5561  5570 I SEEDPlatformSpike: MARK rearm_all wall=1790449243878 rt=2916471171 pid=5561 proc=com.seedengine.platformspike:seed_platform reason=app_launch stored=1 pi_alive_before=1 armed=1 missed=0 next_alarm_clock_before=1790449834370 next_alarm_clock_creator=com.seedengin
1790449243.886  5547  5605 I SEEDPlatformSpike: MARK main_activity_rearm wall=1790449243886 rt=2916471178 pid=5547 proc=com.seedengine.platformspike ok=true json=reason=app_launch stored=1 pi_alive_before=1 armed=1 missed=0 next_alarm_clock_before=1790449834370 next_alarm_clock_creator=com.
1790449243.938  1409  1592 I wm_activity_launch_time: [0,25207826,com.seedengine.platformspike/.MainActivity,106]
1790449247.636  5547  5547 I SEEDPlatformSpike: MARK debug_control wall=1790449247636 rt=2916474929 pid=5547 proc=com.seedengine.platformspike action=com.seedengine.platformspike.CANCEL_ALL
1790449247.641  5561  5570 I SEEDPlatformSpike: MARK alarm_cancelled wall=1790449247641 rt=2916474933 pid=5561 proc=com.seedengine.platformspike:seed_platform id=t4 was_armed=true
1790449247.651  5547  5752 I SEEDPlatformSpike: MARK cancel_all_result wall=1790449247651 rt=2916474944 pid=5547 proc=com.seedengine.platformspike ok=true platform_pid=5561 json=null error=null
# 再生の設定（dumpsys audio。uid 10423 の行）
0_scheduled: （再生なし）
1_after_force_stop: （再生なし）
2_after_relaunch: （再生なし）
3_after_cancel: （再生なし）
# プロセスと Doze の状態（snap の記録）
0_scheduled: pid_main=4849 pid_platform=4828 mForceIdle=false mState=ACTIVE mLightState=ACTIVE 
1_after_force_stop: pid_main= pid_platform= mForceIdle=false mState=ACTIVE mLightState=ACTIVE 
2_after_relaunch: pid_main=5547 pid_platform=5561 mForceIdle=false mState=ACTIVE mLightState=ACTIVE 
3_after_cancel: pid_main=5547 pid_platform=5561 mForceIdle=false mState=ACTIVE mLightState=ACTIVE 
# 予約の中身（dumpsys alarm）
    RTC_WAKEUP #14: Alarm{979294d type 0 origWhen 1790449834370 whenElapsed 2917061662 com.seedengine.platformspike}
      tag=*walarm*:com.seedengine.platformspike.ALARM_FIRE
      type=RTC_WAKEUP origWhen=2026-09-27 04:10:34.370 window=0 exactAllowReason=policy_permission repeatInterval=0 count=0 flags=0x3
      policyWhenElapsed: requester=+9m57s135ms app_standby=-2s855ms device_idle=-- battery_saver=--
      whenElapsed=+9m57s135ms maxWhenElapsed=+9m57s135ms
      Alarm clock:
        triggerTime=2026-09-27 04:10:34.370
        showIntent=PendingIntent{1ba3c02: PendingIntentRecord{318e588 com.seedengine.platformspike startActivity}}
      operation=PendingIntent{6205913: PendingIntentRecord{e9be750 com.seedengine.platformspike broadcastIntent}}
      idle-options=Bundle[{android.pendingIntent.backgroundActivityAllowed=2, android:broadcast.temporaryAppAllowlistReasonCode=301, android:broadcast.temporaryAppAllowlistDuration=10000, android:broadcast.temporaryAppAllowlistReason=, android:broadcast.temporaryAppAllowlistType=0, android:broadcast.flags=8}]
```

</details>

## 05 プロセス間の往復時間

| キー | 値 |
|---|---|
| run1_pid_main_before |  |
| run1_pid_platform_before |  |
| run1_resolver_call_ping_median_us | 2775.5 |
| run1_resolver_call_invoke256_median_us | 3337.0 |
| run1_provider_client_ping_median_us | 1370.5 |
| run1_provider_client_invoke256_median_us | 1815.5 |
| run1_aidl_invoke256_median_us | 507.5 |
| run1_messenger_roundtrip_median_us | 1013.5 |
| run1_broadcast_back_oneway_median_us |  |
| run1_broadcast_back_roundtrip_median_us |  |
| run1_cold_first_call_us | 122955 |
| run1_aidl_bind_us | 18765 |
| run1_messenger_bind_us | 11793 |
| run2_resolver_call_ping_median_us | 786.5 |
| run2_resolver_call_invoke256_median_us | 873.0 |
| run2_provider_client_ping_median_us | 384.5 |
| run2_provider_client_invoke256_median_us | 508.5 |
| run2_aidl_invoke256_median_us | 242.0 |
| run2_messenger_roundtrip_median_us | 649.0 |
| run2_broadcast_back_oneway_median_us |  |
| run2_broadcast_back_roundtrip_median_us |  |
| run2_cold_first_call_us | 2486 |
| run2_aidl_bind_us | 7286 |
| run2_messenger_bind_us | 5386 |
| run3_resolver_call_ping_median_us | 641.5 |
| run3_resolver_call_invoke256_median_us | 698.0 |
| run3_provider_client_ping_median_us | 359.0 |
| run3_provider_client_invoke256_median_us | 501.5 |
| run3_aidl_invoke256_median_us | 256.0 |
| run3_messenger_roundtrip_median_us | 353.0 |
| run3_broadcast_back_oneway_median_us |  |
| run3_broadcast_back_roundtrip_median_us |  |
| run3_cold_first_call_us | 3947 |
| run3_aidl_bind_us | 3486 |
| run3_messenger_bind_us | 3247 |

<details><summary>証拠の抜粋（自パッケージの行）</summary>

```
# 計測（各 10 回。値はマイクロ秒）
run1: BENCH cold_resolver_call first_us=122955 ok=true platform_pid=2002 error=null
run1: BENCH resolver_call_ping n=10 median_us=2775.5 min_us=2286 max_us=4099 values_us=4099,3027,2684,2627,2950,2779,2669,2786,2772,2286
run1: BENCH resolver_call_invoke256 n=10 median_us=3337.0 min_us=3251 max_us=5590 values_us=5590,3411,3337,3322,3283,3251,3357,3337,3389,3299
run1: BENCH provider_client_ping n=10 median_us=1370.5 min_us=1291 max_us=2186 values_us=2186,1399,1450,1291,1308,1576,1342,1330,1476,1301
run1: BENCH provider_client_invoke256 n=10 median_us=1815.5 min_us=1651 max_us=2250 values_us=2250,1716,1827,1651,1873,2189,1804,1665,1868,1758
run1: BENCH aidl_bind bind_us=18765
run1: BENCH aidl_invoke256 n=10 median_us=507.5 min_us=447 max_us=1050 values_us=1050,492,458,458,451,447,778,523,557,524
run1: BENCH messenger_bind bind_us=11793
run1: BENCH messenger_roundtrip n=10 median_us=1013.5 min_us=976 max_us=2516 values_us=2516,1083,1017,1010,1105,994,989,1009,1048,976
run1: BENCH broadcast_back timeout i=0
run2: BENCH cold_resolver_call first_us=2486 ok=true platform_pid=2002 error=null
run2: BENCH resolver_call_ping n=10 median_us=786.5 min_us=719 max_us=2426 values_us=2426,871,843,823,779,719,740,720,744,794
run2: BENCH resolver_call_invoke256 n=10 median_us=873.0 min_us=834 max_us=1270 values_us=1270,846,936,834,857,849,842,942,903,889
run2: BENCH provider_client_ping n=10 median_us=384.5 min_us=357 max_us=1750 values_us=1750,465,377,424,392,361,357,359,360,432
run2: BENCH provider_client_invoke256 n=10 median_us=508.5 min_us=460 max_us=640 values_us=640,511,495,490,506,619,565,522,468,460
run2: BENCH aidl_bind bind_us=7286
run2: BENCH aidl_invoke256 n=10 median_us=242.0 min_us=231 max_us=959 values_us=959,407,235,280,241,241,231,243,234,272
run2: BENCH messenger_bind bind_us=5386
run2: BENCH messenger_roundtrip n=10 median_us=649.0 min_us=292 max_us=1200 values_us=1200,812,770,761,799,537,305,292,304,409
run2: BENCH broadcast_back timeout i=0
run3: BENCH cold_resolver_call first_us=3947 ok=true platform_pid=2002 error=null
run3: BENCH resolver_call_ping n=10 median_us=641.5 min_us=597 max_us=750 values_us=653,613,642,612,603,641,746,750,662,597
run3: BENCH resolver_call_invoke256 n=10 median_us=698.0 min_us=683 max_us=843 values_us=796,697,688,735,699,688,686,683,736,843
run3: BENCH provider_client_ping n=10 median_us=359.0 min_us=347 max_us=533 values_us=533,347,352,355,363,350,354,458,481,456
run3: BENCH provider_client_invoke256 n=10 median_us=501.5 min_us=427 max_us=693 values_us=693,536,504,499,531,532,452,433,427,437
run3: BENCH aidl_bind bind_us=3486
run3: BENCH aidl_invoke256 n=10 median_us=256.0 min_us=243 max_us=590 values_us=364,273,255,590,249,303,254,245,243,257
run3: BENCH messenger_bind bind_us=3247
run3: BENCH messenger_roundtrip n=10 median_us=353.0 min_us=344 max_us=672 values_us=672,526,552,363,350,350,356,346,350,344
run3: BENCH broadcast_back timeout i=0
# observed_broadcast_history（試験の最中に dumpsys で見たものを写した）
run1 の直後に adb shell dumpsys activity broadcasts history で見た BENCH_PONG（:seed_platform → メインの実行時の受信機）の配信記録:
    Historical Broadcast #183:
      BroadcastRecord{d60ec07 com.seedengine.platformspike.BENCH_PONG/u0/0x1} to user 0
      Intent { act=com.seedengine.platformspike.BENCH_PONG flg=0x10 xflg=0x4 pkg=com.seedengine.platformspike (has extras) }
      caller=com.seedengine.platformspike 2002:com.seedengine.platformspike:seed_platform/u0a423 pid=2002 uid=10423 realCallingUid=10423
      enqueueClockTime=2026-09-27 03:42:09.394 dispatchClockTime=2026-09-27 03:42:14.399
      dispatchTime=-56s609ms (+5s5ms since enq) finishTime=-56s608ms (+1ms since disp)
    #183: act=com.seedengine.platformspike.BENCH_PONG ... +5s5ms dispatch +1ms finish
    #184: act=com.seedengine.platformspike.IPC_BENCH flg=0x400030 cmp=com.seedengine.platformspike/.DebugControlReceiver
      +1ms dispatch +5s62ms finish
      enq=2026-09-27 03:42:09.336 disp=2026-09-27 03:42:09.337 fin=2026-09-27 03:42:14.399
読み方: 受け手（メインプロセス）が goAsync で処理中の IPC_BENCH が 03:42:14.399 に終わるまで、BENCH_PONG の配信は待たされた（+5s5ms）。
同じ待ちは 03:41:59.966 の BENCH_PONG でも起きた（+5s8ms）。受け手に処理中の放送が無いとき（06 の B）は RING_STOPPED が 5 ms で届いた。
```

</details>

## 06 前景サービスの起動条件と種類

| キー | 値 |
|---|---|
| a_pids_before | main=5547;platform= |
| a_result | fgs_start_denied;wall=1790449295675;rt=2916522967;pid=5855;proc=com.seedengine.platformspike:seed_platform;origin=debug_broadcast;where=startForegroundService;error=android.app.ForegroundServiceStartNotAllowedException:_startForegroundService()_not_allowed_due_to_mAllowStartForeground_false:_service_com.seedengine.platformspike/.RingService |
| a_system_log | Background_started_FGS:_Disallowed_[callingPackage:_com.seedengine.platformspike;_callingUid:_10423;_uidState:_RCVR;_uidBFSL:_n/a;_intent:_Intent_{_act=com.seed |
| b_screen_at_schedule | wakefulness=Dozing;keyguard_showing=true;top=com.seedengine.platformspike/.MainActivity |
| b_service_types | foregroundId=4101;types=0x00000400; |
| b_alarm_playing | 1 |
| b_result | fgs_started;wall=1790449341020;rt=2916568312;pid=5855;proc=com.seedengine.platformspike:seed_platform;id=t6sys;type=systemExempted;type_flag=1024 |
| b_fgs_start_reason | PROC_STATE_FGS |
| b_ring_start_since_sched_ms | 154 |
| b_first_frame_keyguard_locked | true |
| b_ring_stop_reason | timeout |
| b_ring_stop_rang_ms | 20098 |
| b_stopped_broadcast_latency_ms | 5 |

<details><summary>証拠の抜粋（自パッケージの行）</summary>

```
# logcat（自パッケージの要の行。時刻は端末の epoch 秒）
1790449295.528  1409  1609 I am_proc_start: [0,5855,10423,com.seedengine.platformspike:seed_platform,broadcast,{com.seedengine.platformspike/com.seedengine.platformspike.DebugPlatformReceiver}]
1790449295.645  5855  5855 I SEEDPlatformSpike: MARK provider_create wall=1790449295645 rt=2916522937 pid=5855 proc=com.seedengine.platformspike:seed_platform
1790449295.664  5855  5855 I SEEDPlatformSpike: MARK debug_ring_now wall=1790449295664 rt=2916522956 pid=5855 proc=com.seedengine.platformspike:seed_platform fgs_type=mediaPlayback interactive=false keyguard_locked=true device_locked=true device_idle=false light_idle=false power_save=false 
1790449295.670  1409  2588 E ActivityManager: Background started FGS: Disallowed [callingPackage: com.seedengine.platformspike; callingUid: 10423; uidState: RCVR; uidBFSL: n/a; intent: Intent { act=com.seedengine.platformspike.RING_START xflg=0x4 cmp=com.seedengine.platformspike/.RingServic
1790449295.671  1409  1607 I am_wtf  : [0,1409,system_server,-1,ActivityManager,Background started FGS: Disallowed [callingPackage: com.seedengine.platformspike; callingUid: 10423; uidState: RCVR; uidBFSL: n/a; intent: Intent { act=com.seedengine.platformspike.RING_START xflg=0x4 cmp=com.se
1790449295.675  5855  5855 I SEEDPlatformSpike: MARK fgs_start_denied wall=1790449295675 rt=2916522967 pid=5855 proc=com.seedengine.platformspike:seed_platform origin=debug_broadcast where=startForegroundService error=android.app.ForegroundServiceStartNotAllowedException:_startForegroundSer
1790449300.944  5547  5547 I SEEDPlatformSpike: MARK debug_control wall=1790449300944 rt=2916528236 pid=5547 proc=com.seedengine.platformspike action=com.seedengine.platformspike.SCHEDULE
1790449300.963  5855  5873 I SEEDPlatformSpike: MARK alarm_scheduled wall=1790449300963 rt=2916528255 pid=5855 proc=com.seedengine.platformspike:seed_platform id=t6sys trigger_at=1790449340948 in_ms=39985 fgs_type=systemExempted max_ring_s=20
1790449300.965  5547  5903 I SEEDPlatformSpike: MARK schedule_result wall=1790449300965 rt=2916528258 pid=5547 proc=com.seedengine.platformspike ok=true trigger_at=1790449340948 error=null
1790449340.949  1409  1835 I device_idle_wake_from_idle: [0,*walarm*:com.seedengine.platformspike.ALARM_FIRE]
1790449340.997  5855  5855 I SEEDPlatformSpike: MARK alarm_received wall=1790449340997 rt=2916568290 pid=5855 proc=com.seedengine.platformspike:seed_platform id=t6sys sched=1790449340948 late_ms=48 interactive=false keyguard_locked=true device_locked=true device_idle=false light_idle=false 
1790449341.001  1409  4264 I ActivityManager: Background started FGS: Allowed [callingPackage: com.seedengine.platformspike; callingUid: 10423; uidState: RCVR; uidBFSL: n/a; intent: Intent { act=com.seedengine.platformspike.RING_START xflg=0x4 cmp=com.seedengine.platformspike/.RingService (
1790449341.003  5855  5855 I SEEDPlatformSpike: MARK fgs_start_requested wall=1790449341003 rt=2916568295 pid=5855 proc=com.seedengine.platformspike:seed_platform origin=alarm id=t6sys
1790449341.006  5855  5855 I SEEDPlatformSpike: MARK service_create wall=1790449341006 rt=2916568299 pid=5855 proc=com.seedengine.platformspike:seed_platform interactive=false keyguard_locked=true device_locked=true device_idle=false light_idle=false power_save=false user_unlocked=true
1790449341.013  1409  1564 I ActivityManager: Background started FGS: Allowed [callingPackage: com.seedengine.platformspike; callingUid: 10423; uidState: FGS ; uidBFSL: [BFSL]; intent: Intent { act=com.seedengine.platformspike.RING_START cmp=com.seedengine.platformspike/.RingService }; code
1790449341.020  5855  5855 I SEEDPlatformSpike: MARK fgs_started wall=1790449341020 rt=2916568312 pid=5855 proc=com.seedengine.platformspike:seed_platform id=t6sys type=systemExempted type_flag=1024 can_schedule_exact=true can_use_full_screen_intent=true notifications_enabled=true sdk=36
1790449341.065 31127 31127 I sysui_fullscreen_notification: 0|com.seedengine.platformspike|4101|null|10423
1790449341.104  5855  5855 I SEEDPlatformSpike: MARK ring_start wall=1790449341104 rt=2916568396 pid=5855 proc=com.seedengine.platformspike:seed_platform id=t6sys sched=1790449340948 since_sched_ms=154 usage=alarm fgs_type=systemExempted interactive=true keyguard_locked=true device_locked=t
1790449341.126  5547  5547 I SEEDPlatformSpike: MARK ring_activity_create wall=1790449341126 rt=2916568418 pid=5547 proc=com.seedengine.platformspike trusted=true component=ComponentInfo{com.seedengine.platformspike/com.seedengine.platformspike.RingEntry} id=t6sys since_ring_ms=116 interact
1790449341.137  5547  5547 I SEEDPlatformSpike: MARK ring_activity_resume wall=1790449341137 rt=2916568429 pid=5547 proc=com.seedengine.platformspike id=t6sys interactive=true keyguard_locked=true device_locked=true device_idle=false light_idle=false power_save=false user_unlocked=true
1790449341.142  5547  5547 I SEEDPlatformSpike: MARK ring_activity_resume wall=1790449341142 rt=2916568435 pid=5547 proc=com.seedengine.platformspike id=t6sys interactive=true keyguard_locked=true device_locked=true device_idle=false light_idle=false power_save=false user_unlocked=true
1790449341.170  1409  1592 I wm_activity_launch_time: [0,122999691,com.seedengine.platformspike/.RingEntry,97]
1790449341.172  5547  5547 I SEEDPlatformSpike: MARK ring_activity_first_frame wall=1790449341172 rt=2916568464 pid=5547 proc=com.seedengine.platformspike id=t6sys since_ring_ms=164 interactive=true keyguard_locked=true device_locked=true device_idle=false light_idle=false power_save=false 
1790449341.615  5547  5547 I SEEDPlatformSpike: MARK ring_activity_focus wall=1790449341615 rt=2916568907 pid=5547 proc=com.seedengine.platformspike id=t6sys since_ring_ms=607 interactive=true keyguard_locked=true device_locked=true device_idle=false light_idle=false power_save=false user_u
1790449346.105  5855  5855 I SEEDPlatformSpike: MARK ring_heartbeat wall=1790449346105 rt=2916573397 pid=5855 proc=com.seedengine.platformspike:seed_platform id=t6sys n=1 playing=true elapsed_ms=5098
1790449351.109  5855  5855 I SEEDPlatformSpike: MARK ring_heartbeat wall=1790449351108 rt=2916578401 pid=5855 proc=com.seedengine.platformspike:seed_platform id=t6sys n=2 playing=true elapsed_ms=10101
1790449356.111  5855  5855 I SEEDPlatformSpike: MARK ring_heartbeat wall=1790449356111 rt=2916583403 pid=5855 proc=com.seedengine.platformspike:seed_platform id=t6sys n=3 playing=true elapsed_ms=15104
1790449361.135  1409  2299 I am_foreground_service_stop: [0,com.seedengine.platformspike/.RingService,0,PROC_STATE_FGS,36,36,0,0,20122,1,STOP_FOREGROUND,1024]
1790449361.139  5855  5855 I SEEDPlatformSpike: MARK ring_stop wall=1790449361139 rt=2916588431 pid=5855 proc=com.seedengine.platformspike:seed_platform reason=timeout id=t6sys rang_ms=20098
1790449361.144  5547  5547 I SEEDPlatformSpike: MARK ring_activity_stopped_broadcast wall=1790449361144 rt=2916588436 pid=5547 proc=com.seedengine.platformspike id=t6sys reason=timeout
1790449361.214  5547  5547 I SEEDPlatformSpike: MARK ring_activity_destroy wall=1790449361214 rt=2916588507 pid=5547 proc=com.seedengine.platformspike id=t6sys finishing=true
# 再生の設定（dumpsys audio。uid 10423 の行）
b_ringing: u/pid:10423/5855 state:started attr:AudioAttributes: usage=USAGE_ALARM
# プロセスと Doze の状態（snap の記録）
b_ringing: pid_main=5547 pid_platform=5855 mForceIdle=false mState=ACTIVE mLightState=ACTIVE 
```

</details>

## 99 端末の最終状態

| キー | 値 |
|---|---|
| status | 未完了（04:32 ごろ端末が USB から外れ adb で見えなくなったため、アンインストールを実行できていない） |
| last_snapshot | 04:12:02（results/03_doze_attempt1_user_active/3_after_restore_*） |
| spike_pending_alarms_at_last_snapshot | 0 |
| alarm_clock_at_last_snapshot | なし |
| foreground_service_at_last_snapshot | なし |
| spike_audio_at_last_snapshot | なし |
| deviceidle_at_last_snapshot | mForceIdle=false;mState=ACTIVE;mLightState=ACTIVE |
| battery_override_at_last_snapshot | なし（dumpsys battery reset 済み。UPDATES STOPPED の表示なし） |
| package_installed | はい（com.seedengine.platformspike。未アンインストール） |
| processes_at_last_snapshot | main 10284 / :seed_platform 8618（キャッシュ状態） |
| todo | 端末を再接続して scripts/cleanup.sh を実行（予約の取り消し・Doze と電池の模擬の解除・強制停止・アンインストール・最終状態の記録） |

## lint（AGP 9.1.0 の lintDebug）

`results/lint-results-debug.txt`。指摘の種類と数:

```
      1 [AndroidGradlePluginVersion]
      1 [DataExtractionRules]
      2 [ExportedReceiver]
      6 [InlinedApi]
     11 [NewApi]
      2 [SetTextI18n]
```

正確なアラームの権限（`USE_EXACT_ALARM` と `maxSdkVersion="32"` の `SCHEDULE_EXACT_ALARM` の併記）への指摘は無い。
