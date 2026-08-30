# 2D メッシュ変形スキニング（Spine 風スプライト）— Phase A1

`SkinnedSpriteComponent` と `.sprite_mesh` アセットによる「メッシュを変形させる 2D スプライト」の仕様書。
Phase A1 ではランタイム（描画・変形・ピッキング・スクリプト API）が通っている。
メッシュ編集ツールとボーン対応表の編集 UI は Phase A2 の担当。

---

## 1. 設計の要（なぜこの形なのか）

### ボーン＝普通の 2D 子アクター

独立した Skeleton アセットは**作らない**。ボーンは `SkinnedSpriteComponent` を持つアクターの
**子孫アクター**（`CanvasTransform` を持つ Actor2D）そのものである。

この選択の見返り:

| 欲しいもの | 追加実装 |
|---|---|
| ボーンのギズモ操作 | 不要（既存のアクターギズモがそのまま効く） |
| ボーンの Undo/Redo | 不要（既存のアクター Undo 機構） |
| ボーンのキーフレームアニメ | 不要（既存 `.anim` の汎用プロパティトラック） |
| ボーンの階層・親子付け | 不要（既存のヒエラルキー） |

ボーンを動かすアニメーションは、`.anim` に
`{ actor_path: "root/elbow", component: "CanvasTransform", property: "rotation" }`
のトラックを打ち、`AnimatorComponent` で再生するだけでよい。

### 描画は既存のスプライトパイプラインを「任意メッシュ対応」へ拡張した

別パイプラインは作っていない。スキニングは**コンピュートシェーダ**が担当し、
その出力を「既存のスプライト頂点レイアウトそのもの」で書き出すことで、
描画側は頂点バッファを差し替えるだけで済むようにしてある（詳細は §4）。

---

## 2. `.sprite_mesh` アセット形式（JSON）

```json
{
  "version": 1,
  "name": "quad_one_bone",
  "comment": "制作者向けメモ（実行時挙動に影響しない）",

  "vertices": [[0.0, 0.0], [100.0, 0.0], [100.0, 80.0], [0.0, 80.0]],
  "uvs":      [[0.0, 0.0], [1.0, 0.0],   [1.0, 1.0],    [0.0, 1.0]],
  "triangles": [0, 1, 2, 0, 2, 3],

  "bones": [
    { "name": "root",  "parent": "",     "position": [0.0, 0.0],   "rotation": 0.0, "scale": [1.0, 1.0] },
    { "name": "elbow", "parent": "root", "position": [100.0, 0.0], "rotation": 0.0, "scale": [1.0, 1.0] }
  ],

  "weights": [
    [{ "bone": 0, "weight": 1.0 }],
    [{ "bone": 0, "weight": 0.5 }, { "bone": 1, "weight": 0.5 }],
    [{ "bone": 1, "weight": 1.0 }],
    [{ "bone": 1, "weight": 1.0 }]
  ]
}
```

| フィールド | 意味 |
|---|---|
| `version` | スキーマバージョン。現行 `1`（省略＝`0` も受け入れる） |
| `vertices` | 頂点位置。**スプライトローカルのキャンバスピクセル座標**（§3） |
| `uvs` | 頂点 UV。`[0,1]×[0,1]`・左上原点。`vertices` と同数 |
| `triangles` | 三角形インデックス。3 の倍数個・全て頂点範囲内 |
| `bones` | ボーンのバインドポーズ宣言。`parent` は**ボーン名**（空 = ルート） |
| `weights` | 頂点ごとの影響。**1 頂点あたり最大 4 本**。`bone` は `bones` の添字 |

### バリデーション（読込時に必ず走る）

以下はすべて**読み込みエラー**になる（描画されず、警告を 1 度だけ出す）:

- `vertices` が空 / `uvs`・`weights` の数が `vertices` と不一致
- `triangles` が空、3 の倍数でない、範囲外インデックスを含む
- `bones` が空、ボーン数が 128 本超、ボーン名が空または重複
- `parent` が存在しないボーン名、自分自身を親にしている、親子関係が循環している
- ある頂点の影響が 0 本または 5 本以上
- ウェイトが負・非有限、あるいは 1 頂点のウェイト合計が 0

**ウェイトは読込時に合計 1.0 へ正規化される**（GPU 側では正規化しない）。

### バインドポーズ逆行列

`bones` のローカル TRS からグローバル行列（ルート→自分の合成）を組み、
その逆行列を**読込時に 1 度だけ計算してキャッシュ**する。実行時は毎フレーム
`bone_matrix = current_relative × inverse_bind` を作るだけである。

---

## 3. 座標系（ここを外すと全部ずれる）

- `.sprite_mesh` の `vertices` は **スプライトローカルのキャンバスピクセル座標**。
  原点は `SkinnedSpriteComponent` を持つアクターの `CanvasTransform` 原点、
  **+X が右・+Y が下**（既存のキャンバス座標系と同じ）。
- ボーンアクターの `CanvasTransform.position` も**まったく同じ空間**なので、
  頂点座標とボーン座標を無変換で突き合わせられる。
- `uvs` は `[0,1]×[0,1]`・左上原点（既存スプライトのユニットクワッドと同じ）。

### 従来スプライトとの等価性

`SpriteComponent { width: w, height: h }`（pivot 0）と等価なメッシュは:

```
vertices  = [[0,0], [w,0], [w,h], [0,h]]
uvs       = [[0,0], [1,0], [1,1], [0,1]]
triangles = [0,1,2, 0,2,3]
bones     = [ root（無変形） ]
weights   = すべて root へ 1.0
```

これが実際に一致することは
`runtime/src/engine/core/loader/sprite_mesh.rs` の
`quad_one_bone_matches_plain_sprite` テストが検証している
（フィクスチャ: `runtime/tests/fixtures/quad_one_bone.sprite_mesh`）。

### `CanvasTransform` の解釈の違い

| | `SpriteComponent` | `SkinnedSpriteComponent` |
|---|---|---|
| 使う行列 | `to_sprite_mat4(width, height)` | `to_mesh_mat4(scale_x, scale_y)` |
| 引数の意味 | スプライトの**実寸** | 親キャンバス由来の**追加スケール**（既定 1.0） |
| `pivot` の意味 | `pivot × サイズ`（正規化 0〜1） | `pivot × 追加スケール`（**メッシュローカルのピクセル値**） |

頂点が既に実寸を持っているため、スキンスプライトには `width`/`height` フィールドが無い。
サイズを変えたいときはメッシュ側を作り直すか、`CanvasTransform.scale` を使う。

---

## 4. スキニングの実装方式

### GPU（コンピュート）で変形 → 既存パイプラインで描画

1. **CPU**（`sprite_skin.rs::build_bone_palette`）
   ボーンアクターの現在姿勢を集め、`bone_matrix = current_relative × inverse_bind` を計算する。
   `current_relative` は「スプライトルートアクターからボーンアクターまでの
   `CanvasTransform::to_mat4()` の合成」。
2. **GPU パレット**
   2D アフィンは 6 成分しか要らないので、`mat4x4` ではなく **1 ボーン = `vec4` × 2**
   （`r0 = (a, b, tx, 0)` / `r1 = (c, d, ty, 0)`）で持つ。
   これは行優先 `[[f32;4];4]` の 0/1 行目そのままである。
3. **コンピュート**（`shaders/sprite_skin.wgsl`、1 スレッド = 1 頂点）
   バインドポーズ頂点にパレットを適用し、**変形後の頂点を
   `sprite_vertex` レイアウト（`pos.xy` + `uv.xy` = 16 bytes）で書き出す**。
4. **描画**
   出力バッファは既存の `sprite.wgsl` / `canvas_id.wgsl` の slot0 頂点バッファと
   **同一形式**なので、ユニットクワッドの代わりに差し替えて `draw_indexed` するだけ。
   **新しい描画シェーダは 1 本も足していない。**

### リソースの持ち方

| 資源 | キャッシュ単位 | 中身 |
|---|---|---|
| `GpuSpriteMesh` | `.sprite_mesh` のパス | バインドポーズ頂点（storage）＋インデックス |
| `SpriteSkinInstance` | **スロット `Entity`** | ボーンパレット・パラメータ・変形後頂点・BindGroup |

インスタンスがスロット単位なので、**同じメッシュを複数体が使っても各体が自分のパレットを引く**。

### ボーン解決規則

`.sprite_mesh` の各ボーン名について、次の順で解決する:

1. `bone_overrides` に明示エントリがあれば、その**アクター相対パス**（`"/"` 区切り）で解決
2. 無ければボーン名を直下パスとして解決
3. それでも見つからなければ、子孫を DFS 探索して**同名のアクター**を探す（自動解決）

どれでも見つからないボーンは**バインドポーズ（無変形）**として扱い、
「どのボーンが見つからなかったか」を**アクター × メッシュにつき 1 度だけ**警告する。

---

## 5. 描画規約（`SpriteComponent` と完全に同じ）

- **レイヤー / 描画ゾーン**: 矩形スプライトとスキンスプライトは**同じリストへ積まれ、
  同じ規則（ゾーン → レイヤー昇順の安定ソート）で並べ替えられる**。
  したがって両者の前後関係はレイヤー値だけで正しく決まる。
- **color**: テクスチャへの乗算（RGBA）。
- **キャンバス Transform**: アンカー・`scale_transform` / `scale_size`・アスペクト比維持まで
  スプライトと同一の変換連鎖を通る。
- **ID パス（GPU ピッキング）**: 変形後の頂点でメッシュ形状のまま ID を描くので、
  **クリック判定の形が見た目と一致する**。テクスチャのアルファ 0.1 未満は従来どおり discard。

---

## 6. スクリプト API

`docs/scripting_api.md` の「SkinnedSprite」節が正典。

```csharp
if (gameObject.GetComponent<SkinnedSprite>() is { } ss)
{
    ss.MeshPath;     // string（get/set）
    ss.TexturePath;  // string（get/set）
    ss.Color;        // Color（get/set）
    ss.Layer;        // int（get/set）
}
```

ボーンを動かす API は**持たない**。ボーンは普通の子アクターなので、
そのアクターの `CanvasTransform` を操作する（あるいは `.anim` で再生する）。

---

## 7. Phase A1 の制約（既知の未対応）

| 項目 | 状況 |
|---|---|
| メッシュ編集ツール（頂点・ウェイトのペイント） | Phase A2。現状は `.sprite_mesh` を手書き／外部生成する |
| ボーン対応表（`bone_overrides`）の編集 UI | Phase A2。インスペクタは件数の読み取り専用表示のみ |
| 3D ワールドキャンバス配下のスキンスプライトの**ピッキング** | 未対応（**描画はされる**）。2D キャンバス（SS）のピックのみ対応 |
| アクター編集 2D タブの CPU ピッキング | 未対応（矩形スプライトのみ）。GPU ID パス経由のピックは動く |
| `postfx`（テクスチャ単位ポストエフェクト） | 未対応。`SpriteComponent` のみ |
| 選択アウトライン | 矩形スプライト用のクワッドアウトラインのみ（メッシュ輪郭は未対応） |
| compute のディスパッチ | 1 体につき 1 submit。体数が増えたら 1 フレーム 1 エンコーダへまとめる（TODO） |
| インスタンスキャッシュの寿命 | スロット `Entity` キーで、シーン切替時に明示破棄していない。<br>メッシュパス変更は検出して作り直すため**描画結果は常に正しい**が、<br>長時間セッションではエントリが積み残る（メモリのみの問題） |

---

## 8. 関連ファイル

| ファイル | 役割 |
|---|---|
| `runtime/src/engine/core/loader/sprite_mesh.rs` | `.sprite_mesh` のパース・検証・逆バインド計算・CPU スキニング |
| `runtime/src/engine/components/skinned_sprite_component.rs` | ECS コンポーネント（データのみ） |
| `runtime/src/engine/core/renderer/sprite_skin.rs` | GPU 資源・ボーン解決・compute ディスパッチ |
| `runtime/src/engine/core/renderer/shaders/sprite_skin.wgsl` | スキニング コンピュートシェーダ |
| `runtime/src/engine/core/renderer/batch2d.rs` | `SpriteDrawItem` とバッチ描画（メッシュ／クワッド共通） |
| `runtime/src/engine/core/app_base/app/canvas_collect.rs` | 描画・ID アイテムの収集（スプライトと同じ変換連鎖） |
| `runtime/src/engine/core/app_base/app/skinned_sprite_ops.rs` | インスペクタからのフィールド編集ハンドラ |
| `runtime/tests/fixtures/*.sprite_mesh` | テスト用フィクスチャ（最小矩形・2 ボーンの帯） |
