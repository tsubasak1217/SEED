# シェーディングアセット（L3-a・契約 v1）

ユーザーが書いた WGSL ファイル 1 枚で、**1 ライト分の光応答式**（`shade_model_N`）を差し替える仕組み。
レンダリングの 3 層モデル（`docs/rendering_flow.md` 5 章）における **第 3 層（合成）への介入点**である。

- 実装: `runtime/src/engine/core/renderer/shading_asset.rs`（ロード・検証・生成・キャッシュ）
- 契約の正典: `runtime/src/engine/core/renderer/shaders/shading_contract.wgsl`
- 既定ディスパッチ: `runtime/src/engine/core/renderer/shaders/shading_dispatch.wgsl`
- 呼び出し側: `runtime/src/engine/core/renderer/shaders/lighting_eval.wgsl`（`evaluate_lighting`）

> 表記ルール: 本ドキュメントは実装から確認した事実のみを書く。記載の関数名・定数名・既定挙動は
> すべて上記ソースが正典であり、食い違ったらソース側が正しい。

---

## 1. 概要

### できること

- マテリアルの `shading_model` が 1 / 2 / 3 のピクセルについて、**1 ライト分の反射式**を自作の
  WGSL 関数（`shade_model_1` / `shade_model_2` / `shade_model_3`）で置き換えられる。
  トゥーン・セルシェーディング・異方性ハアなど「光への応答の仕方」を差し替える用途。
- エンジンが渡すのは「面の情報（`ShadingSurface`）」と「そのライト 1 灯の実効放射輝度（`LightSample`）」の
  2 つだけで、バインディング（uniform / texture / storage）は一切見えない。
- Edit モードならファイルを保存するだけで再コンパイルされる（ホットリロード）。

### できないこと

- **compose（画面全体の組み立て）の差し替えは未対応**。ライトの走査・アンビエント・エミッシブ加算・
  影の適用・GI・反射の合成はエンジンが持つ。これは段階 **L3-b** の課題であり未実装。
- シェーディングモデル ID 0（エンジン標準 PBR）は上書きできない。
- 半透明 / WBOIT / フォワードパス / カメラプレビュー小窓には効かない（10 章）。

---

## 2. 契約 v1

| 項目 | 値 | 定義場所 |
|---|---|---|
| 契約バージョン（WGSL） | `const SHADING_CONTRACT_VERSION: u32 = 1u` | `shading_contract.wgsl:39` |
| 契約バージョン（Rust の写し） | `pub const SHADING_CONTRACT_VERSION: u32 = 1` | `shading_asset.rs:52` |
| アセット側の宣言マーカー | `@shading_contract` | `shading_asset.rs:56`（`CONTRACT_MARKER`） |

両者の一致はユニットテスト `rust_and_wgsl_contract_versions_match`（`shading_asset.rs:880`）が固定している。

### 宣言の書き方

アセットの**先頭コメント**に次の 1 行を書く。

```wgsl
// @shading_contract 1
```

パーサ（`parse_contract_version`, `shading_asset.rs:201`）は
**コメント除去前の生ソースを 1 行ずつ走査し、`@shading_contract` を含む最初の行の直後の
連続する ASCII 数字**を読む。したがって物理的にはファイルのどこに書いてもよいが、
規約として先頭コメントに書くこと。

### バージョン不一致・宣言なしの挙動

| 状況 | 挙動 | 実装 |
|---|---|---|
| 宣言があり値が 1 | 通常どおり読み込む | `shading_asset.rs:484` |
| 宣言があり値が 1 以外 | **読み込みを拒否**してエラーを返す。パイプラインは作られず組み込み標準へフォールバック | `shading_asset.rs:476-483` |
| 宣言が無い | **v1 とみなして読み込む**。ただし警告メッセージをエディタへ通知する | `shading_asset.rs:485-491` |

不一致時のメッセージ例:

```
assets://shading/toon.wgsl:-: 契約バージョン不一致（アセット宣言 2 / エンジン 1）。
`// @shading_contract 1` に更新し、契約の変更点に追随してください
```

### バージョンを上げる条件

`ShadingSurface` / `LightSample` の**フィールドの削除・改名・意味変更**、または標準ライブラリ関数の
**シグネチャ変更**のときだけ上げる。フィールドの「末尾への追加」はアセットを壊さないので上げない
（`shading_contract.wgsl:22-24`）。

---

## 3. 契約表

### 3.1 `ShadingSurface`（面の情報）

`shading_contract.wgsl:58-106`。`Surface`（エンジン内部構造体）からの**素直な写し**であり、
写し替えは `lighting_eval.wgsl:174-189` が行う。**ライトに依存しない値だけ**を含み、
ライトループの外で 1 回だけ組み立てられる。

| フィールド | 型 | 意味 | 注意（座標系・単位） |
|---|---|---|---|
| `world_pos` | `vec3<f32>` | シェーディング点のワールド座標 | ワールド空間。位置依存の模様・距離フェードに使える |
| `normal` | `vec3<f32>` | シェーディング法線 N | ワールド空間・正規化済み・**法線マップ適用後**・裏面反転済み。BRDF はこれを使う |
| `geo_normal` | `vec3<f32>` | 幾何法線 Ng | ワールド空間・正規化済み。三角形の面そのもの（フラット）。ファセット表現・輪郭判定用 |
| `vertex_normal` | `vec3<f32>` | 補間頂点法線 Nv | ワールド空間・正規化済み・**法線マップ適用前**。三角形をまたいで滑らか |
| `view_dir` | `vec3<f32>` | 表面→カメラ方向 V | 正規化済み |
| `base_color` | `vec3<f32>` | アルベド（ベースカラー rgb） | **リニア**。頂点カラー・ベースカラー係数は畳み込み済み |
| `roughness` | `f32` | ラフネス | 0..1（clamp 済み） |
| `metallic` | `f32` | メタリック | 0＝誘電体 / 1＝金属 |
| `occlusion` | `f32` | アンビエントオクルージョン | 0..1。エンジンは環境光項にのみ乗算する（直接光には掛けない） |
| `emissive` | `vec3<f32>` | エミッシブ（自己発光） | リニア HDR。**エンジンがライト評価の最後に加算する**（アセットが返す値に足してはならない） |
| `transmission` | `f32` | 拡散透過（葉・布・紙の逆光透け） | 0..1。`Surface.diffuse_transmission` の写し |
| `user_data` | `f32` | マテリアルの自由回線 | 0..1。G-Buffer RT2.a（8bit＝1/255 刻み）由来。意味はマテリアルが決める |
| `render_tag` | `u32` | アクタ単位のセマンティックタグ | 0..15。0＝タグ無し。G-Buffer RT3.a の下位 4bit |
| `shading_model` | `u32` | シェーディングモデル ID | 0..3。0＝エンジン標準 PBR。ディスパッチの分岐キー |
| `frag_coord` | `vec2<f32>` | フラグメント座標 | `@builtin(position).xy`＝ピクセル座標。ディザ・ハッチング・ノイズの種 |

> `Surface` の内部フィールド（`shadow_mask` / `shadow_mask_valid` / `screen_gi` / `alpha`）は
> **契約に含まれない**。影は `LightSample.color` に織り込み済み、SSGI はアンビエント項専用、
> アルファは合成側の値でライティングとは無関係なため（`shading_contract.wgsl:45-55`）。

### 3.2 `LightSample`（1 ライト分の光）

`shading_contract.wgsl:118-136`。詰めるのは `lighting_eval.wgsl:358-363`。

| フィールド | 型 | 意味 | 注意 |
|---|---|---|---|
| `direction` | `vec3<f32>` | 表面→光源方向 L | 正規化済み |
| `color` | `vec3<f32>` | 実効放射輝度 | **`color × intensity × 距離減衰 × スポット円錐（rect は前面判定）× 影係数` がすべて織り込み済み。アセット側で減衰・円錐・影を再計算してはならない**（二重適用になる）。通常はこれを使う |
| `color_unshadowed` | `vec3<f32>` | 影を掛ける**前**の放射輝度 | 距離減衰・円錐までは織り込み済みで、**影のみ除いた値**。逆光透けのように「影を無視したい」表現用 |
| `distance` | `f32` | 光源までの距離 | 平行光は距離を持たないため大きな定数（`light_common.wgsl` の `RT_DIR_TMAX`）が入る。距離フェードの分母として安全に使える |
| `kind` | `u32` | ライト種別 | `light_common.wgsl` の `LIGHT_KIND_*` と同値（0=directional / 1=point / 2=spot / 3=rect） |

アセットがすべきことは「この放射輝度に対する BRDF（反射率）を掛けて返す」ことだけである。

---

## 4. 書ける関数

### シグネチャ

```wgsl
fn shade_model_1(sf: ShadingSurface, li: LightSample) -> vec3<f32> { /* ... */ }
fn shade_model_2(sf: ShadingSurface, li: LightSample) -> vec3<f32> { /* ... */ }
fn shade_model_3(sf: ShadingSurface, li: LightSample) -> vec3<f32> { /* ... */ }
```

返すのは「そのライト 1 灯ぶんの直接光の寄与（リニア HDR 放射輝度）」。
アンビエント・エミッシブ・幾何ゲート（`dot(Ng, L) <= 0` の遮断）はエンジンが外側で処理する。

### ID とマテリアルの対応

| ID | 実装 | 上書き | 備考 |
|---|---|---|---|
| 0 | `shade_model_0`（エンジン標準 PBR・Cook-Torrance） | **不可** | `shading_contract.wgsl:274`。未定義 ID・ロード失敗時のフォールバック先 |
| 1 | `shade_model_1`（アセット） | 可 | |
| 2 | `shade_model_2`（アセット） | 可 | |
| 3 | `shade_model_3`（アセット） | 可 | |

ID はマテリアルの `shading_model` フィールド（`loader/model.rs:320`）で決まり、
G-Buffer RT3.a に 2bit で焼かれる（正典 `renderer/surface_id.rs`。`SHADING_MODEL_BITS = 2` /
`SHADING_MODEL_MASK = 0b11` / `SHADING_MODEL_SHIFT = RENDER_TAG_BITS = 4`）。
**アセットに定義が無い ID・範囲外の ID は必ず `shade_model_0`（標準 PBR）へフォールバックする**
（生成される `switch` の `default:` 腕）。

### 関数の存在検出はソーステキストの走査で行う

厳密な WGSL パースはしない（`detect_shade_models`, `shading_asset.rs:181`）。手順は 2 段:

1. コメントを除去する（行コメント `//` とブロックコメント `/* */` の両方。**改行は保持**する ―
   除去後の行番号が元ソースと 1:1 対応することが、後段の行番号写像の前提）。
2. 「**行頭**（空白のみ挟んでよい）→ `fn` → 空白 → 関数名が完全一致 → 空白* → `(`」に一致する行を探す
   （`line_defines_fn`, `shading_asset.rs:162`）。

この規約から導かれる挙動:

| 書き方 | 検出 | 理由 |
|---|---|---|
| `fn shade_model_1(...)` | される | 規約どおり |
| `    fn shade_model_3(...)`（インデント） | される | 行頭の空白は許容 |
| `fn shade_model_2 (...)`（名前と `(` の間に空白） | される | 空白を挟んでよい |
| `// fn shade_model_1(...)` | **されない** | コメント除去済み |
| `/* fn shade_model_2(...) */` | **されない** | 同上 |
| `fn my_shade_model_1(...)` | されない | 名前の完全一致を要求 |
| `fn shade_model_31(...)` | されない | 直後が空白か `(` であることを要求 |
| `return shade_model_2(sf, li);`（呼び出し） | されない | 行頭 `fn` でない |

定義が 1 つも見つからない場合は**エラーにはならない**が、警告が通知され、全マテリアルが
標準 PBR で描かれる（`shading_asset.rs:496-501`）。

---

## 5. 標準ライブラリ

`shading_contract.wgsl` が提供する定数・関数の全リスト。バインディングを一切持たない純関数なので、
フォワード／デファードのどのパスへ連結しても副作用が無い。

### 定数

| 名前 | 型 | 値 | 用途 |
|---|---|---|---|
| `SHADING_CONTRACT_VERSION` | `u32` | `1u` | 契約バージョン |
| `SHADING_RADIANCE_MAX` | `f32` | `65504.0` | Rgba16Float（IEEE half）が保持できる有限値の上限。`shading_nan_guard` の上限クランプ値 |
| `SHADING_LUMA_REC709` | `vec3<f32>` | `(0.2126, 0.7152, 0.0722)` | Rec.709 輝度係数 |
| `SHADING_SRGB_LINEAR_SCALE` | `f32` | `12.92` | sRGB 伝達関数の線形区間の傾き |
| `SHADING_SRGB_GAMMA_INV` | `f32` | `1.0 / 2.4` | ガンマ区間の指数 |
| `SHADING_SRGB_GAMMA_SCALE` | `f32` | `1.055` | ガンマ区間のスケール |
| `SHADING_SRGB_GAMMA_OFFSET` | `f32` | `0.055` | ガンマ区間のオフセット |
| `SHADING_SRGB_LINEAR_THRESHOLD` | `f32` | `0.0031308` | リニア→sRGB の区間切り替え（リニア側） |
| `SHADING_SRGB_ENCODED_THRESHOLD` | `f32` | `0.04045` | sRGB→リニアの区間切り替え（sRGB 側） |
| `SHADING_DIELECTRIC_F0` | `f32` | `0.04` | 誘電体の垂直入射反射率（IOR 1.5 相当） |

### 関数

| シグネチャ | 内容 |
|---|---|
| `shading_saturate(x: f32) -> f32` | 0..1 クランプ（スカラー） |
| `shading_saturate3(v: vec3<f32>) -> vec3<f32>` | 0..1 クランプ（成分ごと） |
| `shading_nan_guard(c: vec3<f32>) -> vec3<f32>` | NaN → 0 / ±Inf → 0 / 負値 → 0 / `SHADING_RADIANCE_MAX` 超過 → 上限クランプ。**有限かつ非負かつ上限以下の入力には恒等写像** |
| `shading_luminance(c: vec3<f32>) -> f32` | Rec.709 の相対輝度 |
| `shading_linear_to_srgb(c: vec3<f32>) -> vec3<f32>` | リニア → sRGB。出力先はリニア HDR なので、変換した色はそのまま返さず必ず戻すこと |
| `shading_srgb_to_linear(c: vec3<f32>) -> vec3<f32>` | 上の逆変換 |
| `shading_posterize(x: f32, steps: f32) -> f32` | 階調量子化 `floor(x*steps)/steps`。`steps <= 0` は素通し（ゼロ除算・負段数の防御） |
| `shade_light(N, V, L, albedo, F0, metallic, roughness, radiance) -> vec3<f32>` | 1 ライト分の Cook-Torrance BRDF。標準 PBR を土台に足したいときの基礎ブロック |
| `shade_model_0(sf: ShadingSurface, li: LightSample) -> vec3<f32>` | エンジン標準 PBR（**上書き不可**）。アセットからは**呼べる**ので、標準 PBR に加算・混合するのが推奨形 |

### `shading_nan_guard` はユーザー実装にだけ自動で掛かる

- **ID 1..3（ユーザー実装）の返り値には、エンジンが自動で `shading_nan_guard` を掛ける**。
  生成コードが `return shading_nan_guard(shade_model_N(sf, li));` を出すため、アセット側で
  自分で呼ぶ必要はない（呼んでも恒等写像なので害は無い）。
  理由: アセットは任意の式を書けるため 0 除算・`log(0)`・`pow(負, 小数)` で NaN/±Inf が出うる。
  NaN が 1 ピクセルでもブルームのダウンサンプル平均に混ざると画面全体が壊れる。
- **ID 0 には掛からない**。既定ディスパッチ（`shading_dispatch.wgsl:39`）も生成ディスパッチの
  `default:` 腕（`shading_asset.rs:241`）も `shade_model_0(sf, li)` を素通しで返す。
  `shading_nan_guard` は上限クランプも行うため、極端な HDR スパイクでは値が変わり得る。
  「ID 0 の経路はアセット導入前と完全に同値」という L3-a の設計要件に疑いを残さないための措置であり、
  ユニットテスト `dispatch_default_is_model_zero_without_guard`（`shading_asset.rs:966`）が固定している。

> **契約外だが実際には呼べるもの**: 連結順の都合で `pbr_common.wgsl`（`PI` / `distribution_ggx` /
> `geometry_smith` / `fresnel_schlick`）はアセットより前に連結されるため、アセットから呼べてしまう。
> ただしこれらは**契約 v1 の一部ではない**（`shading_contract.wgsl` が提供する関数ではない）ため、
> 将来の変更で壊れうる。依存するなら自己責任で。

---

## 6. 設定方法

### 6.1 カメラごとの指定（インスペクタ）

`CameraComponent` の `shading_asset: Option<String>` フィールド（`camera_component.rs:151`）。
既定は `None`（未設定）。パスは `assets://` 仮想パスまたは絶対パス（`engine/asset_fs.rs` の規約）。

エディタでは Camera コンポーネントのインスペクタに **「Shading」** というファイル参照行がある
（`InspectorPanel.xaml.cs:1922-1955`）。`.wgsl` の D&D／ファイル選択に対応し、
**行を右クリックすると指定を解除**できる（空パスを送る＝`None` に戻る）。

### 6.2 シーン既定

`Scene::shading_asset: Option<String>`（`scene.rs:145`）。`.scene` ファイルのルート直下に
`"shading_asset"` キーとして保存される（`scene.rs:119-123`。`None` のときはキー自体が出力されない）。

```json
{
  "name": "Main",
  "shading_asset": "assets://shading/toon.wgsl",
  "actors": [ ... ]
}
```

> **シーン既定にはエディタ UI が無い**。現状の設定手段は `.scene` の手編集か、
> 後述の IPC コマンド `SET_SCENE_SHADING_ASSET` の直送のみ。

### 6.3 フォールバック連鎖

解決は deferred ライティングパスの直前で毎フレーム行われる（`frame_renderer.rs:4697-4719`）。

| モード | 連鎖 |
|---|---|
| **Play**（非ポーズ） | メインカメラ（`is_main=true`）の `shading_asset` → シーン既定 `Scene.shading_asset` → 組み込み標準 |
| **Edit メインビュー**（デバッグカメラ含む）／Play ポーズ中 | シーン既定 `Scene.shading_asset` → 組み込み標準 |

Edit / ポーズ中はデバッグカメラで描くため `main_camera_shading_asset` が `None` のままになり、
自動的にシーン既定へ落ちる（`frame_renderer.rs:655-661`）。
どこにも指定が無ければ、以降は従来どおり `DrawContext::pipelines.deferred.*` を使い
新経路には一切入らない。

### 6.4 IPC コマンド

| コマンド | 書式 | 動作 |
|---|---|---|
| `SET_CAMERA_SHADING_ASSET` | `SET_CAMERA_SHADING_ASSET:{actor_dfs_id},{slot_idx},{path}` | 指定アクタの Camera スロットの `shading_asset` を設定。`path` が空／空白のみなら `None` へ戻す |
| `SET_SCENE_SHADING_ASSET` | `SET_SCENE_SHADING_ASSET:{path}` | シーン既定を設定。空／空白のみなら `None` |

- 定義: `ipc.rs:480-487`、パース: `ipc.rs:1792-1806`
- 処理: `camera_component_ops.rs:194`（カメラ）／`ipc_handler.rs:1197-1208`（シーン）
- どちらも成功時に `SCENE_MODIFIED` を返す。`path` は `assets://` 仮想パスまたは絶対パス。

---

## 7. トゥーンの実装例（フルコード）

以下は `shading_asset.rs` のユニットテストが naga 検証にかけている実サンプル
（`TOON_ASSET`, `shading_asset.rs:822-871`）そのものである。
3 変種すべて（rt_off / rt_on / rt_bindless）で検証に通ることをテスト
`toon_asset_passes_naga_validation_for_all_variants` が保証している。

```wgsl
// @shading_contract 1
// ============================================================
// toon.wgsl — 3 階調セルシェーディング + リムライト（シェーディングモデル 1）
//
// マテリアルの「シェーディングモデル」を 1 に設定したアクタだけがこの実装で描かれる。
// 未設定（0）のアクタはエンジン標準 PBR のまま。
// ============================================================

/// 拡散光の階調数（3 階調＝影・中間・ハイライト）。
const TOON_DIFFUSE_STEPS: f32 = 3.0;
/// 最も暗い階調でも完全な黒にしないための下限（環境光的な底上げ）。
const TOON_SHADOW_FLOOR: f32 = 0.15;
/// スペキュラを「乗るか乗らないか」の 2 値にするしきい値。
const TOON_SPECULAR_THRESHOLD: f32 = 0.6;
/// 2 値スペキュラの強さ。
const TOON_SPECULAR_STRENGTH: f32 = 0.7;
/// スペキュラの鋭さ（大きいほど小さく硬いハイライト）。
const TOON_SPECULAR_POWER: f32 = 48.0;
/// リムライトの立ち上がり（大きいほど輪郭が細くなる）。
const TOON_RIM_POWER: f32 = 3.0;
/// リムライトの強さ。
const TOON_RIM_STRENGTH: f32 = 0.35;

/// シェーディングモデル 1: トゥーン。
fn shade_model_1(sf: ShadingSurface, li: LightSample) -> vec3<f32> {
    let N = sf.normal;
    let V = sf.view_dir;
    let L = li.direction;

    // ── 拡散: N·L を階調化する ─────────────────────────────
    let ndl  = shading_saturate(dot(N, L));
    // floor(x * steps) / steps は「段の下端」を返すため、段の中心へ寄せて明るさの目減りを防ぐ。
    let band = shading_posterize(ndl, TOON_DIFFUSE_STEPS) + 0.5 / TOON_DIFFUSE_STEPS;
    let diffuse_term = max(shading_saturate(band), TOON_SHADOW_FLOOR);

    // ── スペキュラ: Blinn-Phong を 2 値化する ───────────────
    let H    = normalize(V + L);
    let ndh  = shading_saturate(dot(N, H));
    let spec = pow(ndh, TOON_SPECULAR_POWER);
    let spec_term = select(0.0, TOON_SPECULAR_STRENGTH, spec > TOON_SPECULAR_THRESHOLD);

    // ── リムライト: 視線に対して立っている面ほど明るく ──────
    // 影の付いていない側に出ると不自然なので、ライト方向の寄与で抑える。
    let rim  = pow(1.0 - shading_saturate(dot(N, V)), TOON_RIM_POWER);
    let rim_term = rim * TOON_RIM_STRENGTH * ndl;

    // li.color は減衰・スポット円錐・影まで織り込み済み（再計算してはならない）。
    return (sf.base_color * diffuse_term + vec3<f32>(spec_term) + vec3<f32>(rim_term)) * li.color;
}
```

### 試し方

1. 上のコードを `assets/shading/toon.wgsl` として保存する（拡張子は `.wgsl`）。
2. アセットを割り当てる。どちらか一方でよい。
   - **カメラに設定**: シーンの Camera アクタを選択 → インスペクタの Camera コンポーネント →
     「Shading」行にファイルを D&D（または参照ボタンで選択）。Play したときに効く。
   - **シーン既定に設定**: `.scene` のルートに `"shading_asset": "assets://shading/toon.wgsl"` を
     追記して読み込み直す（または `SET_SCENE_SHADING_ASSET` を直送）。Edit のメインビューでも効く。
3. **対象マテリアルの `shading_model` を 1 にする**。`.mat` / `.smdl` の `shading_model` フィールドを
   1 に設定する（**現状エディタ UI は無い** ― 10 章参照）。
4. 見た目を確認する。`shading_model = 1` のマテリアルだけが 3 階調＋リムライトになり、
   `shading_model = 0` のままのマテリアルは標準 PBR で描かれる。
5. **ホットリロードの確認**（Edit モードのみ）: エンジンを動かしたまま `toon.wgsl` の
   `TOON_DIFFUSE_STEPS` を `2.0` などへ書き換えて保存する。約 1 秒以内に階調数が変わる。
   わざと構文エラーを入れて保存すれば、画面が壊れずに標準 PBR へ戻り、
   エディタへエラーが通知されることも確認できる。

---

## 8. エラー時の挙動

### 検証は naga で事前に行う

生成した連結ソースは `device.create_shader_module` へ渡す**前**に必ず naga の parse + validate を
通す（`validate_wgsl`, `shading_asset.rs:376`）。不正な WGSL をデバイスへ渡さないことで
デバイスロストを回避する設計上の不変条件（`shading_asset.rs:20-21`）。

| 失敗箇所 | 結果 |
|---|---|
| rt_off 変種（必須）の検証失敗・契約バージョン不一致 | **アセット全体が失敗**。パイプラインを一切作らず、そのフレームから組み込み標準へフォールバック |
| rt_on 変種のみ失敗 | `rt` フィールドが `None` になり、その変種を使う条件のときだけ組み込み標準へ落ちる。警告を通知 |
| rt_bindless 変種のみ失敗 | 同上（`rt_bindless` が `None`） |
| ファイルが読めない | エラー通知＋フォールバック。`mtime` が変わるまで再試行しない（`shading_asset.rs:764-775`） |

**いずれの場合も画面は壊れない**（設計上の不変条件 2, `shading_asset.rs:17-19`）。

### エラーの通知経路

キャッシュが人間可読メッセージをキューへ積み、deferred ライティングパスの直後に
`drain_errors()` して IPC `LOAD_ERROR:` プレフィクスでエディタへ送る
（`frame_renderer.rs:4800-4810`）。同時に stderr へも `[SEED shading_asset] ...` として出る。
同一メッセージの連投はキャッシュ側で抑止される（`push_message`, `shading_asset.rs:683`）。

### 行番号はアセット内の行番号へ写像される

naga が返すのは**連結ソース基準**の行番号なので、そのまま出すと 100 行以上ずれる。
`map_reported_line`（`shading_asset.rs:358`）が連結時に記録したアセット開始行を使って写す。

| 報告位置 | 表示形式 |
|---|---|
| アセット本体の中 | `{asset_path}:{アセット内行番号}: {naga のメッセージ}` |
| アセット範囲外（エンジン標準ライブラリ側） | `{asset_path}:-: エンジン標準ライブラリ側（連結 {変種名} の {連結ソース上の行} 行目）でエラー: {メッセージ}` |
| 行番号が取れない | `{asset_path}:-: {メッセージ}` |

書式の実装は `format_error`（`shading_asset.rs:395`）。

### 失敗したアセットは内容が変わるまで再試行しない

キャッシュの値は `Result<Arc<ShadingAssetPipelines>, String>` であり、`Err` エントリは
「この内容ではビルドに失敗した」ことの記録として残る。同一内容ハッシュに対しては再ビルドしない
（毎フレーム naga 検証が走るのを防ぐ）。失敗メッセージを保持しているのは、一度直してから
また同じ壊し方に戻したときにも同じエラーを再通知できるようにするため（`shading_asset.rs:643-655`）。

---

## 9. ホットリロード

| 項目 | 内容 |
|---|---|
| 対象モード | **Edit モードのみ**（`allow_hot_reload = self.mode == RuntimeMode::Edit`, `frame_renderer.rs:4708`） |
| ポーリング間隔 | `SHADING_ASSET_POLL_INTERVAL_SECS: f64 = 1.0`（秒。`shading_asset.rs:77`） |
| 判定 | 前回ポーリングから間隔が経過し、かつ `asset_fs::mtime` が前回と変化しているときだけ読み直す |
| Play 中 | **一切リロードしない**。開始時点のパイプラインを使い続ける（フレーム中のシェーダ再コンパイルによるスパイクを避けるため） |

初回（そのパスを一度も見ていないとき）は必ず読む。これは Play 中でも同じで、
Play 開始後に初めて解決されるパスは 1 回だけ読み込み＋ビルドされる。

---

## 10. 既知の制限

| 制限 | 詳細 |
|---|---|
| **半透明 / WBOIT / フォワードパスには効かない** | アセットの差し替えは deferred ライティングパスにしか適用されない（`frame_renderer.rs:4696` のブロック内でのみ `resolve` が呼ばれる）。フォワード・半透明・WBOIT のパイプライン（`mesh*.toml` / `transparent_*.toml` / `transparency.rs:316-330,517-530`）は常に `shading_dispatch.wgsl`（＝モデル 0 固定）を連結する。よって半透明マテリアルの `shading_model` を 1 にしても標準 PBR で描かれる |
| **カメラプレビュー小窓は常に組み込み標準** | プレビューのライティングパスは `draw_ctx.pipelines.deferred.pipeline` を直に使う（`frame_renderer.rs:2287`）。アセットは経由しない |
| **deferred が無効なフレームには効かない** | `deferred_active = false`（Edit のワイヤーフレーム表示・2D シーンビュー・`post_fx.deferred` オフ）のときは deferred ライティングパス自体が走らないため、アセットは効かない |
| **compose（画面全体の組み立て）は未対応** | ライト走査・アンビエント・影の適用・GI・反射の合成はエンジンが持つ。段階 **L3-b** の課題であり未実装 |
| **ユーザー定義可能な ID は 1..3 の 3 枠のみ** | G-Buffer RT3.a に詰められるビット幅が 2bit（`SHADING_MODEL_BITS = 2`）だから。正典は `renderer/surface_id.rs`。枠数は `USER_MODEL_SLOTS = 3`（`shading_asset.rs:65`）で、ユニットテスト `user_model_id_range_is_derived_from_surface_id` が 1..3 を固定している |
| **マテリアルの `shading_model` にエディタ UI が無い** | 実装確認: エディタ側に `shading_model` を編集する UI は存在しない（`editor/` 内に該当文字列なし）。`MaterialOverrideKind::Inline` にも `shading_model` フィールドは無い（`material_override.rs:46-80`）。現状の設定手段は `.mat` アセットまたはモデルキャッシュ（`.smdl`）に載る `Material::shading_model`（`loader/model.rs:320`）のみで、glTF / OBJ ローダは常に既定値 0 を入れる |
| **`user_data` / `render_tag` にも同様の制約** | `render_tag` は `ModelComponent::render_tag` として `.scene` に保存されるが、`user_data` はマテリアル側の値であり同じくエディタ UI を持たない |

---

## 11. 内部実装メモ

### 11.1 連結順（実際の配列）

標準リストの `"shading_dispatch.wgsl"` の位置を、**アセット本体＋生成ディスパッチの 2 要素**へ
その場で置換する（`substitute_dispatch`, `shading_asset.rs:302`）。標準リストは
TOML / 定数から機械的に読み、**この配列をハードコードしない**（連結順の二重管理を避ける）。

| 変種 | 標準リストの正典 | 連結順 |
|---|---|---|
| `RtOff` | `pipelines/deferred_lighting.toml:9` | `cluster_common` → `pbr_common` → `ddgi_common` → `light_common` → `shadow` → `rt_shadow_off` → `surface` → `shading_contract` → **`shading_dispatch`** → `lighting_eval` → `deferred_lighting` |
| `RtOn` | `pipelines/deferred_lighting_rt.toml:4` | `cluster_common` → `pbr_common` → `ddgi_common` → `light_common` → `shadow` → `rt_shadow_on` → `rt_shadow_tint_avg` → `surface` → `shading_contract` → **`shading_dispatch`** → `lighting_eval` → `deferred_lighting` |
| `RtBindless` | `deferred.rs:37-42`（`RT_BINDLESS_SHADER_SOURCES`） | `cluster_common` → `pbr_common` → `ddgi_common` → `light_common` → `shadow` → `rt_shadow_on` → `bindless_common` → `rt_shadow_tint_bindless` → `surface` → `shading_contract` → **`shading_dispatch`** → `lighting_eval` → `deferred_lighting` |

置換後は太字の 1 要素が `[<アセットのパス>, "<generated shade_surface>"]` の 2 要素になる。
`shade_surface` の定義は連結全体で常にちょうど 1 本（既定版と生成版は排他）。
置換位置の正しさはテスト `dispatch_element_is_substituted_in_place`（`shading_asset.rs:1075`）が固定。

naga 検証のケイパビリティは変種ごとに異なる（`Variant::capabilities`, `shading_asset.rs:286`）:
`RtOff` = なし / `RtOn` = `RAY_QUERY` / `RtBindless` = `RAY_QUERY` + 非一様インデックス。

### 11.2 キャッシュキー

- `ShadingAssetCache.built` のキーは **アセット内容のハッシュ**（`content_hash`, `shading_asset.rs:84`。
  `std::collections::hash_map::DefaultHasher` ＝ SipHash-1-3）。暗号学的強度は不要で、
  「同一内容 → 同一キー」「異なる内容 → 実質衝突しない」だけを求める。
  内容ハッシュなので、**別パスに同じ内容を置いた場合はパイプラインを共有する**。
- `ShadingAssetCache.paths` はパス → `PathState { last_poll, mtime, hash }`
  （`shading_asset.rs:634-641`）。mtime が変わっていなければファイル読み込み自体をスキップする。
- キャッシュ本体は `DrawContext::shading_asset_cache`（`methods/drawer/mod.rs:143`）に置かれ、
  `&self` 共有のフレーム内から更新できるよう `RefCell` の内部可変で持つ。

### 11.3 生成される `shade_surface`

`generate_dispatch`（`shading_asset.rs:227`）が出す WGSL。`shade_model_1` だけを定義した
アセット（前掲のトゥーン）の場合、生成されるのは次のコードである。

```wgsl
// ── 自動生成（shading_asset.rs）。このコードは編集できません ──
fn shade_surface(sf: ShadingSurface, li: LightSample) -> vec3<f32> {
    switch sf.shading_model {
        case 1u: { return shading_nan_guard(shade_model_1(sf, li)); }
        default: { return shade_model_0(sf, li); }
    }
}
```

3 モデルすべてを定義した場合は `case 1u:` / `case 2u:` / `case 3u:` の 3 腕が並ぶ。
**定義されていないモデルの `case` は出力されない**（＝`default:` へ落ちて標準 PBR になる）。

呼び出し側は `lighting_eval.wgsl:364` の 1 行:

```wgsl
Lo += geo_gate * shade_surface(sf, li_sample);
```

`geo_gate`（`dot(Ng, L) > 0` の幾何ゲート）はエンジン側で掛かるため、アセットは
「面がライトに背を向けている場合」を自分で処理する必要はない。

### 11.4 追跡用ファイル一覧

| ファイル | 役割 |
|---|---|
| `runtime/src/engine/core/renderer/shaders/shading_contract.wgsl` | 契約 v1 の正典（型・標準ライブラリ・`shade_model_0`） |
| `runtime/src/engine/core/renderer/shaders/shading_dispatch.wgsl` | 既定ディスパッチ（アセット未指定時） |
| `runtime/src/engine/core/renderer/shading_asset.rs` | ロード・検証・生成・キャッシュ・エラー写像 |
| `runtime/src/engine/core/renderer/shaders/lighting_eval.wgsl` | `ShadingSurface` / `LightSample` の組み立てと `shade_surface` 呼び出し |
| `runtime/src/engine/core/renderer/surface_id.rs` | `shading_model` ID のビット規約（2bit） |
| `runtime/src/engine/components/camera_component.rs` | `CameraComponent.shading_asset` |
| `runtime/src/engine/core/app_base/scene.rs` | `Scene.shading_asset`（シーン既定） |
| `runtime/src/engine/core/app_base/app/frame_renderer.rs` | フォールバック連鎖・解決・エラー通知 |
| `editor/src/Panels/InspectorPanel.xaml.cs` | カメラの「Shading」ファイル参照行 |
