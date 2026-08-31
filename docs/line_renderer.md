# LineRenderer（3D ポリライン描画）

点列を結ぶ 1 本の線をワールド空間に描く ECS コンポーネント。
釣り糸・ロープ・鎖・軌跡（トレイル）・照準線など、**「点列で表せる細長い描画物」の共通の土台**として使う。

点列は毎フレームスクリプトから丸ごと差し替える運用が前提で、
エディタのインスペクタは見た目のパラメータ（太さ・色・フラグ）だけを持つ。

---

## 1. 使い方（最短手順）

1. ヒエラルキーで 3D アクタを選ぶ
2. インスペクタの「コンポーネントを追加」→ カテゴリ **「描画」** → **Line Renderer**
3. 点 0 個・幅 0.02m・白の線が付く（この時点では点が無いので何も描かれない）
4. スクリプトから `SetPoints` で点列を流し込む（[スクリプト API](#4-スクリプト-api)）

**Edit モードでも描画される**。ギズモやワイヤーのようなエディタ専用表示ではなく、
ゲーム内オブジェクトとして Play / Edit を問わず同じように見える。

---

## 2. フィールド

`runtime/src/engine/components/line_renderer_component.rs` が正典。

| serde 名 | 型 | 既定値 | 意味 |
|---|---|---|---|
| `points` | `Vec<[f32;3]>` | 空 | 線を構成する点列。2 点未満なら描画されない。上限 512 点（`MAX_LINE_POINTS`） |
| `width` | `f32` | `0.02` | 線の太さ（**ワールド単位＝メートル**）。0 以下で非表示 |
| `color` | `[f32;4]` | 白・不透明 | 線色（RGBA・リニア）。アルファ < 1 で半透明合成される |
| `local_space` | `bool` | `true` | `points` をアクターの Transform 基準で解釈するか（false = ワールド座標） |
| `depth_test` | `bool` | `true` | 手前の不透明物に隠れるか（false = 常に最前面） |
| `visible` | `bool` | `true` | 描画するか。スロットを消さずに一時的に隠せる |

### 座標系について

SEED の `Transform` は**ワールド空間で保持され、親子行列の合成が存在しない**
（`runtime/src/engine/core/transform_sync.rs` 参照）。
そのため `local_space = true` の点列は、そのアクター自身の `Transform::to_mat4()` を
掛けるだけでワールド座標になる（親をさかのぼる必要はない）。

竿先とウキのように**別々のアクターの位置を結ぶ**用途では、
`local_space = false` にしてワールド座標をそのまま渡すのが素直。

---

## 3. 描画方式

### 3.1 カメラ向きリボン（CPU 展開）

各セグメント（`p[i] → p[i+1]`）を 2 三角形のクワッドへ展開する。
端点 `p` でのオフセット方向は

```
side = normalize(cross(seg_dir, view_dir)) * (width / 2)
```

（`seg_dir` = セグメント方向、`view_dir` = カメラ → `p` の向き）。
セグメント方向・視線方向の両方に直交するので、リボン面は常にカメラを向き、
線はどの角度から見ても `width` の幅を保つ（＝遠いほど細く見える、正しい遠近感）。

- 実装: `runtime/src/engine/methods/drawer/line_ribbon.rs::expand_polyline_ribbon`（**純関数**。ユニットテストあり）
- ジョイント（折れ点）は**マイター処理をしない**。セグメントごとに独立して展開するため
  隣接クワッドが端点で軽く重なるが、細い糸・ロープでは折れ角が緩く視認できない。
  マイターは頂点数と分岐を増やすだけなので採らない。
- 視線とセグメントが平行な縮退時は、`seg_dir` に直交する任意軸へフォールバックする
  （その向きから見ると線は点に潰れるので、どちらへ広げても見た目は変わらない）。

> **なぜギズモの太線（`gizmo_line.wgsl`）を使わないか**
> あちらは太さを **px** で指定するスクリーン空間展開で、「遠くの線も同じ太さに見える」
> デバッグ表示向けの挙動になっている。ゲーム内の釣り糸・ロープには
> **ワールド単位の太さ**が要るため、別方式が必要になる。

### 3.2 パイプライン

CPU で展開済みなので、シェーダは既存の `unlit.wgsl`（位置 + 頂点色）を流用する。
**新規 WGSL は追加していない**（展開ロジックが CPU 側の純関数に閉じているため）。

| パイプライン | TOML | 深度比較 | 用途 |
|---|---|---|---|
| `ribbon_depth_pipeline` | `pipelines/line_ribbon_depth.toml` | `LessEqual` | `depth_test = true`（不透明物に隠れる） |
| `ribbon_nodepth_pipeline` | `pipelines/line_ribbon_nodepth.toml` | `Always` | `depth_test = false`（常に最前面） |

どちらも `TriangleList` / `cull_mode = None` / `AlphaBlending` / `depth_write = false`。
定義は `runtime/src/engine/core/renderer/pipeline.rs` の `UnlitPipeline` が保持する。

### 3.3 描画順（どのパスで描くか）

**WBOIT 合成後のオーバーレイパス**で描く（`frame_renderer.rs`）。

- メインパス内に描くと、WBOIT 透明合成のフルスクリーン `no_depth` クアッドに上書きされ、
  半透明被覆のある画面座標で線が消える。合成の後に描くこのパスなら 3D 前後関係を保ったまま残る。
- 深度アタッチメントは共有深度を `Load`（テストのみ・書き込みなし）なので、
  `LessEqual` の線は不透明物に正しく隠れる。
- **Play のレターボックス時はゲーム領域へ `set_viewport` する**（これはエディタ表示ではなく
  ゲーム内オブジェクトなので、他 9 箇所と同一条件）。描画後は全面へ戻し、
  後続のエディタオーバーレイの挙動を変えない。
- 深度あり／なしで頂点段階からバッファを 2 本に仕分けているため、
  **線が何本あってもドローコールは最大 2 回**（`line_renderer_ops::LineRibbonVertices`）。

### 3.4 対象外

影（シャドウマップ）・レイトレーシング（BLAS）・G-Buffer への寄与は**行わない**。
細い線が影を落とす必要はなく、コストに見合わないため。

---

## 4. スクリプト API

正典は `docs/scripting_api.md` の第 7 節。要点のみ：

```csharp
if (gameObject.GetComponent<SEED.LineRenderer>() is { } line)
{
    line.Width      = 0.015f;                     // ワールド単位の太さ
    line.Color      = new SEED.Color(1, 1, 1, 1);
    line.Visible    = true;
    line.LocalSpace = false;                      // ワールド座標で点を渡す
    line.DepthTest  = true;
    line.PointCount;                              // get のみ

    line.SetPoints(points);                       // Vector3[] を丸ごと差し替え（上限 512 点）
    line.Clear();                                 // 線を消す
}

// たわんだ糸の点列を作る補助（純 C#）
SEED.Vector3[] pts = SEED.LineHelper.Catenary(start, end, slack, segments);
```

### FFI（点列をどう渡しているか）

点列は**既存の汎用 float 配列書き込み**（`ffi_set_floats`）にそのまま乗せている。
新しい FFI 関数も `ScriptHostApi` の変更も無い。

読み取り側（`ffi_get_floats`）は固定長スタックバッファ 1 本で受けるため上限 4 要素のままだが、
書き込み側は C# のメモリを直接スライスで見るだけなので長い配列を 1 回で渡せる。
そこで**書き込み専用の上限**を別定数として導入した：

- Rust: `host_api::MAX_FLOAT_WRITE_LEN = MAX_LINE_POINTS * 3`（= 1536）
- C#: `ScriptHost.MaxFloatWriteLen`（同値。**必ず一致させること**）

`points` フィールドは要素数が 3 の倍数でない／点数が上限超過なら**失敗させる**
（黙って丸めると C# 側の詰め忘れを検出できなくなるため）。
`SetPoints(空)` は `point_count` フィールドへ 0 を書く経路で表現している
（FFI の float 書き込みは 1 要素以上が必要なため）。

---

## 5. インスペクタ

- 編集できるのは **太さ・色（RGBA）・表示・ローカル座標・深度テスト**の 5 項目。
- 点列は**件数の表示のみ**。数百点になり得るうえ毎フレーム差し替わるので、
  インスペクタに全点を出す意味が無く、IPC 文字列も肥大するため。
- IPC は `SET_LINE_RENDERER_FIELD:{actor},{slot},{key},{value}`
  （key: `width` / `color` / `local_space` / `depth_test` / `visible`。color は `"r,g,b,a"`）。
- Undo は `field_edit.rs::field_edit_target` へ分類を書いてあるので、共通機構がそのまま効く。

---

## 6. 関連ファイル

| 役割 | ファイル |
|---|---|
| コンポーネント（データ） | `runtime/src/engine/components/line_renderer_component.rs` |
| リボン頂点展開（純関数） | `runtime/src/engine/methods/drawer/line_ribbon.rs` |
| 収集・インスペクタ編集 | `runtime/src/engine/core/app_base/app/line_renderer_ops.rs` |
| 描画関数 | `runtime/src/engine/methods/drawer/primitive_drawer.rs::draw_line_ribbon_batch` |
| パイプライン定義 | `runtime/src/engine/core/renderer/pipelines/line_ribbon_{depth,nodepth}.toml` |
| 描画の差し込み | `runtime/src/engine/core/app_base/app/frame_renderer.rs`（オーバーレイパス冒頭） |
| スクリプト公開 | `runtime/src/engine/core/scripting/host_api.rs` / `scripting/src/Api/LineRenderer.cs` |
| 点列ヘルパー（純 C#） | `scripting/src/Api/LineHelper.cs` |
| インスペクタ UI | `editor/src/Panels/InspectorPanel.xaml.cs::BuildLineRendererSlotContent` |
