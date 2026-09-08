# UI 演出の 2D パーティクル（どこに何があり、どこを触れば直せるか）

このドキュメントは **UI（キャンバス）に載せた 2D パーティクル演出の所在地の一覧**です。
粒そのものの仕組み（キャンバス空間・`Layer`・Y 下向き・`Burst` の契約など）は
`docs/scripting_api.md` の「ParticleEmitter」節が正典で、ここでは重複させません。

## 触り方の原則

- **数値はスクリプトではなくエミッタが持つ**。寿命・初速・重力・抵抗・大きさ・色・
  ブレンド・放出間隔はすべて `.actor`（プレハブ）側の `ParticleEmitter` にある。
  勢いや量を変えたいときは、**エディタでその子アクタを選んでインスペクタを触る**。
- **スクリプトが持つのは「いつ・何個・どの色で」だけ**。個数（`Burst` の数）と
  レベル配色の反映（`Tint`）だけがスクリプト側の設定として出ている。
- **エミッタは演出パネルの子**にしてある。パネルが非表示のあいだは粒も描かれず、
  シミュレーションも進まない（＝閉じたあとに粒だけ取り残されることがない）。
- 使っているテクスチャは `assets://mainGame/textures/ui/white.png`（白い四角）だけ。
  `mainGame/textures/ui/` には他に `beat_icon.png` / `judge_*.png` / `radar_*.png` が
  あるが、いずれも UI の絵であって粒向けの星・円テクスチャは**まだ無い**。
  星形にしたくなったら小さな PNG を足して `texture_paths` を差し替える
  （`texture_paths` は複数枚を並べると粒ごとにランダムで選ばれる）。

## 一覧

| 演出 | エミッタの場所 | 出す側 | 引き金 | 主なパラメータ |
|---|---|---|---|---|
| HIT バナーの火花 | `assets://mainGame/actors/FX/HitSparkle2D.actor`（実行時に 2 体生成） | `mainGame/scripts/HitBanner.cs` | 魚が掛かった瞬間（`HitBanner.Play`） | layer 2600 / 寿命 0.35〜0.75s / 初速 180〜460 / 重力 +900 / 6〜14px / add |
| 評価バナー（Perfect） | `actors/UI/FightEvalBanner.actor` → `FightEvalBody/EvalBurstGold` | `scripts/FightEvalBanner.cs` | バナー表示開始（`Show(..., perfect: true)`） | layer 2600 / 寿命 0.8s / 初速 240〜560 / 重力 +1100 / 7〜14px / 金 / 既定 40 個 |
| 評価バナー（Good） | 同上 → `FightEvalBody/EvalBurstWhite` | 同上 | `Show(..., perfect: false)` | 上と同じ数値で色だけ白 |
| リザルトの New Record | `actors/UI/ResultPanel.actor` → `ResultBody/NewRecordSparkle` | `scripts/ResultPanel.cs` | 新記録のときパネルが開き切った瞬間から（`Play`）、閉じ始めで停止（`Stop`） | layer 3004 / ループ放出 0.035s 間隔 / 寿命 0.6〜1.1s / 初速 30〜110 / 重力 +40 / 半径 95px / 6〜12px / 金 |
| 図鑑登録の紙吹雪 | 同上 → `ResultBody/RegisteredPanel/RegisteredConfetti` | 同上 | 図鑑登録パネルが開く瞬間（`Burst`） | layer 3012 / 寿命 0.7〜1.25s / 初速 180〜480 / 重力 +950 / 7〜14px / 桃・水・黄の 3 色 / normal / 既定 48 個 |

`Sparkle2D.actor`（`actors/FX/Sparkle2D.actor`）は**汎用サンプルのまま**残してある。
新しい 2D 演出を作るときの雛形として使い、ゲームの演出には使っていない。

### 文字に合わせるエミッタは「1 フォントサイズぶん上」に置く

`box_width = 0` の Text は `vertical_align = middle` でも**アクタの座標より
約 1 フォントサイズぶん上に描かれる**（`docs/backlog.md` の既知事項）。
そのため文字の真ん中から粒を弾かせたいエミッタは、文字のアクタと同じ座標ではなく
**y をフォントサイズぶんマイナス**した位置に置いてある。

| エミッタ | 合わせている文字 | 文字の座標 / font_size | エミッタの座標 |
|---|---|---|---|
| `EvalBurstGold` / `EvalBurstWhite` | `FightEvalText` | (0, 0) / 120 | (0, **-120**) |
| `NewRecordSparkle` | `NewRecord` | (150, 130) / 34 | (150, **96**) |

文字の位置やフォントサイズを変えたら、この対応も直すこと。

## それぞれの事情

### HIT バナーだけプレハブを実行時に生成している

HIT バナーの演出アイテム（帯 2 本・文字 2 つ）は**プレハブではなくシーン上のアクタ**
（`MainGame.scene` の `FishingUI/HitBannerItems` 配下）なので、他の演出のように
「プレハブへ子を足して作り込む」ことができない。そのため

1. エミッタだけを持つ 1 体のプレハブ `actors/FX/HitSparkle2D.actor` を用意し、
2. `HitBanner.OnStart` が位置基準の数（既定 2 個）だけ `Instantiate` して
   演出ルート `HitBannerItems` の子にぶら下げ、
3. `HitBanner.Play` が毎回**位置基準アクタ（`HitTextLevel` / `HitTextHit`）の
   アンカーと座標をそのまま写して**から `Burst` する

という形にしている。帯や文字の配置を動かしても、火花は写して追従するので追加作業は要らない。
位置基準を増やしたい／別の場所で弾きたいときは、インスペクタの
**「火花の位置基準アクタ名」**にアクタ名を足す（1 名につき 1 体生成される）。

火花の色は**レベル配色**（Lv 文字と同じ低・中・高の 3 色グラデーション）を
`ParticleEmitter.Tint` で粒へ乗せている。したがって配色を変えたいときは
`HitBanner` のインスペクタの「低／中／高レベルの色(16進)」を触れば火花も一緒に変わる。

### 評価バナーは色ちがいのエミッタを 2 本置いている

Perfect と Good で色を変えるために `EvalBurstGold` / `EvalBurstWhite` の 2 本を置き、
`FightEvalBanner.Show` の第 4 引数（完璧かどうか）で**片方だけ**弾く。
`Tint` で 1 本を塗り替える手もあるが、2 本にしておくと
「Perfect のときだけ粒を大きく・長生きに」のような**色以外の差**も
エディタだけで付けられる（＝スクリプトを触らずに演出を変えられる）。

### 図鑑登録の紙吹雪はエミッタ 1 本で多色

`ParticleEmitter` の色カーブ（`color_curves`）は**複数本持て、粒ごとにランダムで
1 本が選ばれる**。そこで桃・水・黄の 3 本を 1 つのエミッタに持たせている
（3 体のエミッタを置く必要はない）。色を増やしたいときはインスペクタで色カーブを足す。

### 【要対応】リザルトの 2 つは MainGame ではまだ出ない

`MainGame.scene` に置いてある `ResultPanel` は、**プレハブへのリンクを持たない古い複製**
（`prefab_source` が無く、プレハブ側にある `Prompt` の子すら欠けている）。
シーン側は AI が触らない約束なので、プレハブへ足した `NewRecordSparkle` /
`RegisteredConfetti` は**シーン上のインスタンスには届かない**（実測で確認済み）。

直し方は次のどちらか。**利用者の判断が要る**（どちらもシーン編集）。

1. シーンの `ResultPanel` アクタを**削除する**。`ResultPanel.Show` は
   配置済みが無ければプレハブを生成するので、常に最新のプレハブが使われるようになる
   （`FightEvalBanner` と同じ運用になり、以後この手のズレが起きない）。
2. シーンの `ResultPanel` を**プレハブのインスタンスで置き直す**。

このドキュメントの確認手順（ヘッドレス）では、1 と同じ状態を作った一時シーン
（`MainGame.scene` から `ResultPanel` アクタだけ抜いたもの）で撮っている。

## 動作確認（ヘッドレス）

`docs/editor_mcp.md` 10 章のデバッグコマンドで、それぞれの場面を 1 手で出せる。

| 出したいもの | コマンド |
|---|---|
| HIT バナー（火花） | `seed_script_debug(name:"hit_test", arg:"5")`（`arg` はレベル数値／魚の表示名／空＝一番近い魚） |
| 評価バナー → リザルト → 図鑑登録 | `seed_script_debug(name:"catch_test")` → `seed_script_debug(name:"result_confirm")` |

New Record と図鑑登録の両方を出したいときは、`SEED_SAVE_DIR` に釣果記録の無い
`save.json`（`{"tutorial_done": 1}` だけ）を置いて起動する（初捕獲かつ新記録になる）。
