# SEED スクリプト API リファレンス

このファイルは **SEED のスクリプト（C#）から使える API の唯一の正典** です。

- 人間向けのリファレンスであると同時に、**スクリプトエディタの AI インライン補完へ注入される情報源** でもあります（`editor/src/Panels/ScriptEditor/InlineCompletion/ScriptApiReference.cs` が本ファイルを読み込み、補完のシステムプロンプトへ要約を渡します）。
- したがって **API を追加・変更したら、必ず本ファイルを更新** してください。ここに書かれていない API は AI が知りません。

> **重要（AI 向け）**: SEED は **Unity ではありません**。`UnityEngine` 名前空間・`MonoBehaviour`・`GetComponent` の Unity 実装などは存在しません。使えるのは以下に列挙した SEED 独自 API と .NET 標準ライブラリ（`System.*`）だけです。
>
> **名前空間の必須修飾（重要）**: ゲーム向けエンジン API（`Mathf` / `Vector3` / `Vector2` / `Quaternion` / `Time` / `Random` / `Debug` / `GameObject` など）は **`SEED` 名前空間**にあります。エンジンはテンプレートに `using SEED;` を入れません（`System.Random` などとの型名衝突を防ぐため）。したがって **コードでは必ず `SEED.` を付けて呼び出してください**（例: `SEED.Random.Range(0, 10)`、`SEED.Vector3.Up`、`SEED.Mathf.Sin(x)`、`SEED.Time.DeltaTime`）。無修飾で書きたい場合のみ、ユーザー自身が各スクリプトの先頭に `using SEED;` を書きます（その際の名前衝突は自己責任）。基底クラス `SEEDScript` と属性・`NativeFrameContext` は `SEEDEditor.Scripting` 名前空間にあり、こちらは衝突しないためテンプレートで `using` 済みです。
>
> 本リファレンスの **API 一覧（列挙）部分は簡潔さのため `SEED.` を省略**しています。実際のコードでは上記のとおり `SEED.` を付けてください（`using SEED;` した場合を除く）。

---

## 1. スクリプトの基本形

スクリプトは C# で書き、`SEEDScript`（`SEEDEditor.Scripting` 名前空間）を継承します。ゲーム向け API は `SEED` 名前空間にあり、**`SEED.` で修飾して呼び出します**（エンジンは `using SEED;` を自動では入れません）。

```csharp
using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

public class Mover : SEEDScript
{
    // インスペクタに公開するフィールドは [SerializeField] を付ける
    [SerializeField(Label = "速度")]
    private float speed = 2.0f;

    public override void Update(ref NativeFrameContext ctx)
    {
        // 毎フレーム、自分の GameObject を右へ動かす（エンジン API は SEED. で修飾）
        transform.Position += SEED.Vector3.Right * speed * SEED.Time.DeltaTime;
    }
}
```

`ref NativeFrameContext ctx` には `ctx.DeltaTime`（前フレームからの経過秒）と `ctx.AnimTime`（累計時間）が入っています。同じ値は `SEED.Time.DeltaTime` / `SEED.Time.ElapsedTime` でも取れます。

> 無修飾で書きたい場合は、そのスクリプトの先頭に自分で `using SEED;` を足してください。ただし `System.Random` など標準ライブラリと型名が衝突することがあり、その解決（`SEED.Random` と明示するなど）は利用者側の責任になります。`using` はファイル単位で閉じるため、他のスクリプトには影響しません。

---

## 2. ライフサイクル関数

`SEEDScript` を継承して override します。1 フレーム内で以下の順に呼ばれます（すべて `ref NativeFrameContext ctx` を取る）。

| 関数 | 呼ばれるタイミング／用途 |
|------|--------------------------|
| `BeginFrame`     | フレーム開始時。入力取得や状態リセット向け |
| `EarlyUpdate`    | Update より前。他スクリプトへ渡す事前計算向け |
| `Update`         | 毎フレームの主更新。ゲームロジックの中心 |
| `ConstantUpdate` | 固定タイムステップの更新。物理など時間刻みを一定にしたい処理向け |
| `LateUpdate`     | Update 後。追従カメラなど他更新の結果を使う処理向け |
| `Render`         | 描画フェーズ。描画に関わる処理向け |
| `EndFrame`       | フレーム終了時。後片付けや状態確定向け |

不要な関数は override しなくて構いません。

---

## 3. Time（フレーム時間） / Debug（ログ）

```csharp
Time.DeltaTime      // float: 前フレームからの経過秒
Time.ElapsedTime    // float: ゲーム内の累計時間（秒）

Debug.Log("メッセージ");        // 情報ログ
Debug.LogWarning("注意");       // 警告
Debug.LogError("失敗");         // エラー
```

---

## 4. Mathf（数学ユーティリティ・float 中心）

```csharp
Mathf.PI, Mathf.Deg2Rad, Mathf.Rad2Deg, Mathf.Epsilon, Mathf.Infinity

Mathf.Sin(x) Mathf.Cos(x) Mathf.Tan(x) Mathf.Asin(x) Mathf.Acos(x) Mathf.Atan(x) Mathf.Atan2(y, x)
Mathf.Sqrt(x) Mathf.Pow(x, p) Mathf.Exp(x) Mathf.Log(x) Mathf.Log(x, b) Mathf.Log10(x)
Mathf.Abs(x) Mathf.Sign(x) Mathf.Floor(x) Mathf.Ceil(x) Mathf.Round(x)
Mathf.FloorToInt(x) Mathf.CeilToInt(x) Mathf.RoundToInt(x)
Mathf.Min(a, b) Mathf.Max(a, b)
Mathf.Clamp(v, min, max) Mathf.Clamp01(v)
Mathf.Lerp(a, b, t) Mathf.LerpUnclamped(a, b, t) Mathf.InverseLerp(a, b, v)
Mathf.MoveTowards(current, target, maxDelta)
Mathf.Repeat(t, length) Mathf.PingPong(t, length) Mathf.SmoothStep(from, to, t)
Mathf.Approximately(a, b)   // 浮動小数の等価比較（== の代わりに使う）
```

---

## 5. Vector2 / Vector3 / Quaternion（不変値型）

### Vector3（位置・方向・スケール）

```csharp
new Vector3(x, y, z)
Vector3.Zero Vector3.One Vector3.Up Vector3.Down Vector3.Left Vector3.Right Vector3.Forward Vector3.Back

v.x v.y v.z
v.Magnitude v.SqrMagnitude v.Normalized

a + b, a - b, -a, a * 2f, 2f * a, a / 2f, a == b, a != b

Vector3.Dot(a, b) Vector3.Cross(a, b) Vector3.Distance(a, b) Vector3.Scale(a, b)
Vector3.Lerp(a, b, t) Vector3.MoveTowards(cur, target, maxDelta) Vector3.Angle(a, b)
Vector3.Min(a, b) Vector3.Max(a, b)
```

`Vector2` も同様（`x, y` と `Zero/One/Up/Down/Left/Right`、`Dot/Distance/Scale/Lerp/Min/Max`）。

### Quaternion（回転）

```csharp
Quaternion.Identity
Quaternion.Euler(xDeg, yDeg, zDeg)  // オイラー角（度）から。適用順は YXZ
Quaternion.Euler(vector3Degrees)
Quaternion.AngleAxis(angleDeg, axis)

q1 * q2          // 回転の合成
q * vector3      // ベクトルを回す
q.EulerAngles    // Vector3（度）へ変換（Transform.Rotation へ書き戻す用）
q.Normalized
```

> Transform の回転は **YXZ オイラー角（度）の Vector3** で表します。合成・補間したいときだけ Quaternion を使い、`q.EulerAngles` で Vector3 に戻します。

### Color（RGBA カラー・不変値型）

```csharp
new Color(r, g, b)            // アルファ省略時は 1.0（不透明）
new Color(r, g, b, a)         // 各成分 0.0〜1.0 の正規化 float
Color.White Color.Black Color.Red Color.Green Color.Blue
Color.Yellow Color.Cyan Color.Magenta Color.Gray Color.Transparent

c.r c.g c.b c.a
c.WithAlpha(0.5f)             // アルファだけ差し替えた新しい色
Color.Lerp(a, b, t)           // 線形補間（t は 0..1 にクランプ）
a * b                         // 成分ごとの乗算（ティント合成）
c * 2f                        // スカラー倍
```

---

## 6. Random（乱数）

エンジン全体で 1 つの系列を共有します。既定では `SEED.Random.Range(...)` のように `SEED.` を付けて呼び出します（`System.Random` との衝突を避けるため、エンジンは `using SEED;` を入れません）。自分で `using SEED;` を足した場合、`using System;` も併用していると `Random` が曖昧になるので、その場合は引き続き `SEED.Random` と明示してください。

```csharp
Random.Value                 // float: 0.0 以上 1.0 未満
Random.Range(min, max)       // float: min 以上 max 未満
Random.Range(minInt, maxInt) // int:   min 以上 max 未満（max は含まない）
Random.Bool                  // bool
Random.InsideUnitCircle      // Vector2: 半径 1 の円内
Random.OnUnitSphere          // Vector3: 長さ 1 のランダム方向
Random.InitState(seed)       // シード固定（再現用）
```

---

## 7. GameObject とコンポーネント（Transform / CanvasTransform / Sprite / Camera）

スクリプトは自分がアタッチされた GameObject を `gameObject`、その Transform を `transform` で参照できます。各コンポーネントアクセサは薄いハンドルで、プロパティへの代入は即座にゲーム世界へ反映されます。対象コンポーネントを持たないエンティティに対する読み取りは既定値、書き込みは無視されます（`HasComponent` で保持判定）。

```csharp
gameObject                    // GameObject: このスクリプトが乗るオブジェクト
gameObject.IsValid            // bool: 実体が有効か
gameObject.HasComponent("Sprite")   // bool: 指定名のコンポーネントを持つか
transform                     // Transform: gameObject.Transform の短縮

gameObject.Transform          // 3D トランスフォーム
gameObject.CanvasTransform    // 2D キャンバストランスフォーム
gameObject.Sprite             // 2D スプライト
gameObject.Camera             // 3D カメラ
```

### 生成・破棄・検索（Instantiate / Destroy / Find）

```csharp
// .actor ファイル（プレハブ）からアクターを生成する（assets:// 仮想パス）
var bullet = SEED.GameObject.Instantiate("assets://actors/Bullet.actor");
bullet.Transform.Position = transform.Position;   // 生成直後に位置設定できる
if (!bullet.IsValid) { /* 読み込み失敗 */ }

// アクターを破棄する（実際の破棄はフレーム末尾。Unity の Destroy と同じ遅延モデル）
bullet.Destroy();                       // インスタンス版
SEED.GameObject.Destroy(bullet);        // 静的版（同じ動作）

// アクターを名前で検索する（ヒエラルキーの DFS 順で最初の一致）
var player = SEED.GameObject.Find("Player");
if (player.IsValid) { player.Transform.Position = SEED.Vector3.Zero; }
```

- `Instantiate` の戻り値には**同フレーム中に** `Transform.Position` 等を設定でき、その値が優先されます（アクター本体の構築はフレーム末尾に行われます）。
- **2D アクター（Actor2D）の注意**: 構築時に Transform が CanvasTransform へ差し替わるため、生成直後の 3D Position 設定は反映されません。位置は翌フレーム以降に `CanvasTransform.Position` で設定してください。
- 破棄済み GameObject への読み取りは既定値、書き込みは無視されます（クラッシュしません）。
- 現時点の制限: Play 開始後に生成・破棄したアクターの**物理コライダーは物理スレッドに反映されません**（物理イベント API 実装時に対応予定）。

### Transform（3D 位置・回転・スケール）

```csharp
transform.Position         // Vector3（get/set）
transform.Rotation         // Vector3（get/set。YXZ オイラー角・度）
transform.Scale            // Vector3（get/set）

// 例: 回しながら上げる（エンジン API は SEED. で修飾）
transform.Rotation += new SEED.Vector3(0f, 90f * SEED.Time.DeltaTime, 0f);
transform.Position += SEED.Vector3.Up * SEED.Time.DeltaTime;
```

### CanvasTransform（2D キャンバス上の位置・回転・スケール）

```csharp
var ct = gameObject.CanvasTransform;
ct.Position                // Vector2（get/set。親 Canvas 基準の相対座標）
ct.Rotation                // float（get/set。Z 軸周りの度）
ct.Scale                   // Vector2（get/set）
ct.Pivot                   // Vector2（get/set。回転・スケール基準点。正規化 [0,1]、(0.5,0.5)=中央）
ct.Anchor                  // Vector2（get/set。親 Canvas 内の position 基準点。(0,0)=左上 (1,1)=右下）
```

### Sprite（2D スプライト表示）

```csharp
var sprite = gameObject.Sprite;
sprite.TexturePath         // string（get/set。assets:// 仮想パス。空文字=単色表示）
sprite.Color               // Color（get/set。RGBA。テクスチャに乗算）
sprite.Width               // float（get/set。キャンバスユニット）
sprite.Height              // float（get/set）
sprite.Size                // Vector2（get/set。Width/Height をまとめて）

// 例: 点滅させる
sprite.Color = SEED.Color.White.WithAlpha(SEED.Mathf.PingPong(SEED.Time.ElapsedTime, 1f));
```

### Camera（3D カメラ設定）

カメラの位置・向きは同じ GameObject の `transform` で動かします。

```csharp
var cam = gameObject.Camera;
cam.FieldOfView            // float（get/set。垂直視野角・度）
cam.Near / cam.Far         // float（get/set。クリップ距離）
cam.IsMain                 // bool（get/set。Play モードのメインカメラか）
cam.ClearColor             // Color（get/set。背景クリアカラー）
cam.TargetWidth / cam.TargetHeight  // int（get/set。スケーリングのベース解像度）
cam.BarColor               // Color（get/set。レターボックス帯の色）
```

### 利用可能なコンポーネント一覧

| コンポーネント名 | アクセサ | 内容 |
|---|---|---|
| `Transform` | `gameObject.Transform` / `transform` | 3D 位置・回転・スケール |
| `CanvasTransform` | `gameObject.CanvasTransform` | 2D キャンバス上の位置・回転・スケール・ピボット・アンカー |
| `Sprite` | `gameObject.Sprite` | テクスチャパス・色・サイズ |
| `Camera` | `gameObject.Camera` | FOV・クリップ距離・メインカメラ・クリアカラー・ベース解像度 |

他のコンポーネント（Collider / Rigidbody など物理系）は物理 API として順次対応予定で、対応済みのものは本節に追記されます。

---

## 8. （メンテナ向け）新しいコンポーネントをスクリプトへ公開する手順

コンポーネントを増やしたら、以下を行うことで **自動的にスクリプト・AI 補完から使える** ようになります。

1. **Rust 側レジストリへ登録**: `runtime/src/engine/core/scripting/host_api.rs` の `read_floats` / `write_floats`（文字列フィールドがあれば `read_string` / `write_string` も）と `has_component` に、コンポーネント名の分岐を 1 つずつ追加する（`Sprite` の例に倣う）。数値は float 配列（f32=1 要素 / Vector2=2 / Vector3=3 / RGBA=4、bool は 0/1、整数は f32 変換）で受け渡す。
2. **C# 側ラッパー（任意）**: 型付きで扱いたい場合は `scripting/src/Api/` に薄いラッパー（`Sprite.cs` に倣う）を足し、`GameObject.cs` にアクセサプロパティを追加する。汎用アクセス（`ScriptHost.TryGetFloats` などの名前指定）だけで良ければ不要。
3. **本ファイル（`docs/scripting_api.md`）の第 7 節に追記**: これを忘れると AI 補完がその API を知りません。

この 3 点は `.claude/CLAUDE.md` にも運用ルールとして明記されています。

---

## 9. 使用可能なライブラリ

- 上記の SEED API（`SEED` / `SEEDEditor.Scripting` 名前空間）
- .NET 標準ライブラリ（`System`, `System.Collections.Generic`, `System.Linq`, `System.Math` など）

**使えないもの**: `UnityEngine.*`、`MonoBehaviour`、Unity のコルーチン（`IEnumerator` ベースの `StartCoroutine` 等）。これらは SEED には存在しません。
