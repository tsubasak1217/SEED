# SEED スクリプト API リファレンス

このファイルは **SEED のスクリプト（C#）から使える API の唯一の正典** です。

- 人間向けのリファレンスであると同時に、**スクリプトエディタの AI インライン補完へ注入される情報源** でもあります（`editor/src/Panels/ScriptEditor/InlineCompletion/ScriptApiReference.cs` が本ファイルを読み込み、補完のシステムプロンプトへ要約を渡します）。
- したがって **API を追加・変更したら、必ず本ファイルを更新** してください。ここに書かれていない API は AI が知りません。
- 検索機能付きのブラウザ閲覧用 HTML 版が [`docs/scripting_api.html`](scripting_api.html) にあります（スクリプトエディタの「📖 API ガイド」ボタンから開けます）。**API を追加・変更したら HTML 版も同時に更新** してください。

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

### 物理イベントコールバック

自分のアクターのコライダー（ColliderComponent / Collider2dComponent）が他のコライダーと衝突・接触すると、以下が呼ばれます（3D / 2D 共通）。`other` は相手アクターの GameObject です（特定できない場合は `IsValid == false`）。

| 関数 | 呼ばれるタイミング |
|------|--------------------|
| `OnCollisionEnter(SEED.GameObject other)` | 衝突が始まったフレーム |
| `OnCollisionStay(SEED.GameObject other)`  | 衝突継続中（毎物理ステップ） |
| `OnCollisionExit(SEED.GameObject other)`  | 衝突が終わったフレーム |
| `OnTriggerEnter(SEED.GameObject other)`   | トリガーへの進入時（トリガー側・相手側の両方に通知） |
| `OnTriggerExit(SEED.GameObject other)`    | トリガーからの退出時（同上） |

```csharp
public class Coin : SEEDScript
{
    public override void OnTriggerEnter(SEED.GameObject other)
    {
        SEED.Debug.Log($"取得: {other.IsValid}");
        gameObject.Destroy();   // 自分を消す
    }
}
```

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

## 6.5 Input（キーボード・マウス入力）

エンジンの入力状態を参照する静的クラス。判定は 3 種類（押している間 / 押した瞬間 / 離した瞬間）。

```csharp
// キーボード
Input.GetKey(KeyCode.Space)        // bool: 押されている間 true
Input.GetKeyDown(KeyCode.Space)    // bool: 押された瞬間のフレームだけ true
Input.GetKeyUp(KeyCode.Space)      // bool: 離された瞬間のフレームだけ true

// マウスボタン（MouseButton.Left / Right / Middle）
Input.GetMouseButton(MouseButton.Left)
Input.GetMouseButtonDown(MouseButton.Left)
Input.GetMouseButtonUp(MouseButton.Left)

// マウス状態
Input.MousePos        // Vector2: スクリーン座標（ピクセル・左上原点）
Input.MouseMove       // Vector2: 今フレームの相対移動量
Input.MouseScroll     // float:   今フレームのホイール量（上=正）

// 簡易軸入力（WASD/矢印 → [-1,1]。斜めは正規化しない）
Input.MoveAxis()      // Vector2

// 例: WASD 移動 + スペースでジャンプ判定
var move = SEED.Input.MoveAxis();
transform.Position += new SEED.Vector3(move.x, 0f, move.y) * speed * SEED.Time.DeltaTime;
if (SEED.Input.GetKeyDown(SEED.KeyCode.Space)) { /* ジャンプ */ }
```

`KeyCode` の定義: `A`〜`Z` / `Alpha0`〜`Alpha9`（メイン数字キー）/ `F1`〜`F12` / `UpArrow` `DownArrow` `LeftArrow` `RightArrow` / `Space` `Enter` `Escape` `Tab` `Backspace` `Delete` / `LeftShift` `RightShift` `LeftControl` `RightControl` `LeftAlt` `RightAlt`

---

## 6.6 Physics（レイキャスト・キャラクターコントローラー）

```csharp
// レイキャスト: 最初にヒットしたコライダーの情報を得る
if (SEED.Physics.Raycast(transform.Position, SEED.Vector3.Down, 10f, out var hit))
{
    SEED.Debug.Log($"接地: {hit.Point} 距離={hit.Distance}");
    hit.GameObject   // GameObject: ヒットしたアクター（IsValid で有効判定）
    hit.Point        // Vector3: ヒット点のワールド座標
    hit.Normal       // Vector3: ヒット点の法線
    hit.Distance     // float:   始点からの距離
}

// ヒット情報が不要な場合の簡易版
bool blocked = SEED.Physics.Raycast(origin, dir, maxDistance);
```

- 3D 物理（ColliderComponent）に対するレイキャストです。物理スレッドへの同期問い合わせのため、毎フレーム大量に呼ぶとフレーム時間を消費します。
- 衝突・トリガーの**イベント通知**は第 2 節「物理イベントコールバック」（`OnCollisionEnter` 等）を参照してください。

### キャラクターコントローラー（Transform を書くだけで地形に押し戻される）

Collider インスペクタの「**キャラクターコントローラー**」を ON にしたアクターは、**専用 API を呼ばず**、`transform.Position` を書き換えるだけで地形・静的コライダーに衝突解決され、めり込んだぶんが自動で押し戻されます。押し戻しは移動のたびではなく、**物理ステップ同期（60Hz）と同じタイミングで定期的に** Transform を確認し、前回解決済み位置との差分を moveVector として解決します。壁ずり・段差の乗り越え・スロープ登坂は内部の KCC（KinematicCharacterController）が処理します。

```csharp
// キャラクター移動: Transform.Position を希望位置へ書くだけ。地形にめり込めば押し戻される。
float velocityY = 0f; // フィールドとして保持する

void Update()
{
    // 重力はエンジンが自動適用しないので自前で積分する
    velocityY += -9.81f * SEED.Time.DeltaTime;
    if (SEED.Physics.IsGrounded(gameObject) && velocityY < 0f) velocityY = 0f; // 接地で落下停止

    var move = horizontal + new SEED.Vector3(0f, velocityY, 0f) * SEED.Time.DeltaTime;
    transform.Position += move; // ← これだけ。押し戻しは物理ステップ同期で自動適用される
}
```

- 対象アクターは **Collider コンポーネント（カプセル推奨）** を持ち、インスペクタで「キャラクターコントローラー」を ON にしてください。カプセル形状が KCC のシェイプに使われます。
- 補正後の位置は集約経路で反映されるため、**子アクタ（カメラ等）も追従**します。
- 1 フレーム内で `Position` を何度書いても、物理ステップで **1 回だけまとめて** 解決されます。押し戻しの反映には物理同期の都合上 1 フレームの遅延があります。
- **`Physics.IsGrounded(gameObject)`**: キャラクターが接地しているかを返します（物理ステップ同期で自動更新）。
- **`transform.Teleport(pos)`**: 衝突を無視して瞬間移動します（下記 Transform 参照）。ワープ・リスポーン・初期配置に使います。
- 重力・複数コライダーの合成移動はスクリプト側の責務です（KCC 自体は重力を持ちません）。

---

## 6.7 Audio（BGM・効果音）

ファイルは `assets://` 仮想パスで指定します（対応形式: wav / ogg / mp3 / flac）。

```csharp
// 効果音（多重再生可）
SEED.Audio.Play("assets://sounds/shoot.wav");           // 音量 1.0
SEED.Audio.Play("assets://sounds/hit.ogg", 0.5f);       // 音量指定

// BGM（既存 BGM は停止して置き換え。既定でループ）
SEED.Audio.PlayBgm("assets://sounds/stage1.ogg");
SEED.Audio.PlayBgm("assets://sounds/jingle.ogg", 0.8f, loop: false);

SEED.Audio.SetBgmVolume(0.3f);   // BGM 音量を変更
SEED.Audio.StopBgm();            // BGM を停止
```

- 同じファイルはキャッシュされ、2 回目以降の再生でディスク読み込みは発生しません。
- オーディオデバイスが無い環境では全操作が無音で無視されます（エラーになりません）。
- アクターに紐づく音源（3D 距離減衰・パン対応）は **AudioComponent**（第 7 節の `gameObject.AudioSource`）を使ってください。こちらの静的 API はアクターに紐づかない BGM / 単発 SE 向けです。

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
gameObject.AudioSource        // オーディオソース（AudioComponent）
gameObject.Animator           // アニメーター（キーフレームアニメーション再生。AnimatorComponent）
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
transform.WorldPosition    // Vector3（get のみ。ワールド絶対座標 = Position と同値）
transform.Rotation         // Vector3（get/set。YXZ オイラー角・度）
transform.Scale            // Vector3（get/set）
transform.Teleport(pos)    // void: 衝突を無視して pos へ瞬間移動（キャラクターコントローラー用）

// 例: 回しながら上げる（エンジン API は SEED. で修飾）
transform.Rotation += new SEED.Vector3(0f, 90f * SEED.Time.DeltaTime, 0f);
transform.Position += SEED.Vector3.Up * SEED.Time.DeltaTime;
```

> **`Teleport(pos)`**: キャラクターコントローラー（Collider の「キャラクターコントローラー」ON）を、
> 地形との衝突解決（自動押し戻し）を発生させずに `pos` へ瞬間移動します。物理側の「前回位置」も
> 同時にリセットされるため、瞬間移動先で押し戻されません。ワープ・リスポーン・シーン開始時の
> 初期配置に使います。`Position` への代入（＝押し戻しあり）との使い分けに注意してください。

> **親子の追従**: `Transform` の各値は**ワールド絶対座標**です。スクリプトから `Position` /
> `Rotation` / `Scale` のいずれかを書き込むと、その差分が**自身のメッシュと全子孫アクター
> （Transform とメッシュの両方）へ即座に伝播**します。モデルを持たない子アクター
> （カメラなど）も追従します。代入した直後に子の `Transform` を読めば、更新後の値が返ります。
> なおアニメーション・物理による Transform 更新はこの伝播経路を通りません（既知の制限）。

### CanvasTransform（2D キャンバス上の位置・回転・スケール）

```csharp
var ct = gameObject.CanvasTransform;
ct.Position                // Vector2（get/set。親 Canvas 基準の相対座標）
ct.Rotation                // float（get/set。Z 軸周りの度）
ct.Scale                   // Vector2（get/set）
ct.Pivot                   // Vector2（get/set。回転・スケール基準点。正規化 [0,1]、(0.5,0.5)=中央）
ct.Anchor                  // Vector2（get/set。親 Canvas 内の position 基準点。(0,0)=左上 (1,1)=右下）
ct.ScreenPosition          // Vector2（get のみ。ウィンドウ左上原点のスクリーン座標・ピクセル）
```

> `Position` は**親 Canvas 相対**の座標ですが、`ScreenPosition` はアンカー・スケールモード・親チェーンをすべて反映した**画面上の絶対位置**（ピボット点）を返します。SEED の 3D `Transform.Position` は元々ワールド絶対座標で、書き込み時に子孫へ差分が伝播します（上記「親子の追従」参照）。

### Sprite（2D スプライト表示）

```csharp
var sprite = gameObject.Sprite;
sprite.TexturePath         // string（get/set。assets:// 仮想パス。空文字=単色表示）
sprite.Color               // Color（get/set。RGBA。テクスチャに乗算）
sprite.Width               // float（get/set。キャンバスユニット）
sprite.Height              // float（get/set）
sprite.Size                // Vector2（get/set。Width/Height をまとめて）
sprite.Layer               // int（get/set。描画優先度。大きいほど手前。既定 0。
                           //     同値はヒエラルキー順。同一描画ゾーン内で比較される）

// 例: 点滅させる
sprite.Color = SEED.Color.White.WithAlpha(SEED.Mathf.PingPong(SEED.Time.ElapsedTime, 1f));
```

### Camera（3D カメラ設定）

カメラの位置・向きは同じ GameObject の `transform` で動かします。

```csharp
var cam = gameObject.Camera;
cam.FieldOfView            // float（get/set。垂直視野角・度。透視投影時に使用）
cam.Near / cam.Far         // float（get/set。クリップ距離）
cam.IsMain                 // bool（get/set。Play モードのメインカメラか）
cam.ClearColor             // Color（get/set。背景クリアカラー）
cam.TargetWidth / cam.TargetHeight  // int（get/set。スケーリングのベース解像度）
cam.BarColor               // Color（get/set。レターボックス帯の色）
cam.Projection             // string（get/set。"perspective" / "orthographic"）
cam.OrthoHeight            // float（get/set。正射投影時の縦の描画範囲・ワールド単位）
```

- `Projection = "orthographic"` で平行投影（遠近感なし）。縦 `OrthoHeight`・横 `OrthoHeight × アスペクト比` の範囲を写します。透視投影時は `FieldOfView` を使用します。

### AudioSource（アクター紐づけの音源。3D 距離減衰・パン対応）

エディタの「コンポーネント追加 → サウンド → Audio Source」で追加し、インスペクタで設定します。

```csharp
var audio = gameObject.AudioSource;
audio.Play();              // 設定された音源を再生（再生中なら鳴らし直し）
audio.Stop();              // 停止
audio.IsPlaying            // bool: 再生中か

audio.Path                 // string（get/set。assets:// 仮想パス）
audio.Volume               // float（get/set。1.0=等倍。再生中も即反映）
audio.Loop                 // bool（get/set。次回 Play 時に反映）
audio.PlayOnStart          // bool（get/set。Play 開始時に自動再生）
audio.Spatial              // bool（get/set。3D 空間再生 = メインカメラとの距離減衰 + 方向パン）
audio.MinDistance          // float（get/set。減衰開始距離。これ以内は音量 100%）
audio.MaxDistance          // float（get/set。無音距離。これ以遠は聞こえない）
audio.Pan                  // float（get/set。-1=左 〜 1=右。Spatial=false 時のみ有効）
```

- 距離減衰は線形（MinDistance 以内 100% → MaxDistance で 0%）。リスナーは `is_main` のメインカメラ。
- `Spatial = true` では音源方向に応じて左右パンが自動で振られます（手動 `Pan` は無効）。

### Animator（キーフレームアニメーション再生）

エディタの「コンポーネント追加 → アニメーター」で追加し、インスペクタで再生対象クリップ（`clips`）を登録します。実際のトラック評価・書き込みはエンジン側の AnimationSystem が毎フレーム自動で行うため、スクリプトからは再生の開始・停止・状態参照のみ行います。

```csharp
var anim = gameObject.Animator;
anim.Play("Walk");         // 指定クリップを先頭（time=0）から再生（速度は変更しない）
anim.Play("Walk", 1.5f);   // 再生速度も同時に指定して再生
anim.Stop();                // 停止して time=0 に戻す
anim.Pause();                // 再生位置を保持したまま一時停止
anim.Resume();               // 一時停止を再開（再生対象クリップが無ければ無視）

anim.IsPlaying              // bool（get のみ。再生中か）
anim.CurrentClip            // string（get のみ。再生中のクリップ名。未再生は空文字）
anim.Time                   // float（get/set。再生位置・秒。書き込みでシーク可能）
anim.Speed                  // float（get/set。再生速度倍率。1.0=等倍、負値で逆再生）
```

- `Play` で指定するクリップ名は、そのアクターの Animator に登録済み（`clips` 一覧に存在し、既にロード済み）である必要があります。未登録・未ロードの名前を指定すると警告ログを出して無視されます（例外は発生しません）。
- クリップは Play モード開始時（初回フレーム、スクリプトの `Update` 等より前）に自動ロードされるため、通常のスクリプトライフサイクル関数から呼ぶ限り「まだロードされていない」状況は発生しません。

### ParticleEmitter（GPU パーティクル放出源）

エディタの「コンポーネント追加 → パーティクルエミッタ」で追加し、インスペクタで放出パラメータ（レート・寿命・色・ブレンドなど）を設定します。放出位置・向きは同じ GameObject の `transform` が決めます。

```csharp
var ps = gameObject.ParticleEmitter;
ps.Play();                 // 放出を開始（playing = true）
ps.Stop();                 // 放出を停止（既存パーティクルは寿命で消える）
ps.Burst(50);              // 50 個を即時一括放出（継続放出とは独立）
ps.IsPlaying               // bool（get のみ。放出中か。Playing の別名）

ps.Playing                 // bool（get/set。放出中フラグ。Play()/Stop() と同じ切り替え）
ps.EmitRate                // float（get/set。1 秒あたりの放出個数。負値は 0 にクランプ）
ps.LoopEmit                // bool（get/set。寿命ループ放出するか）
ps.Drag                    // float（get/set。空気抵抗係数。負値は 0 にクランプ）
ps.SpreadAngle             // float（get/set。放出円錐の半頂角・度。0〜180 にクランプ）
```

- `Burst(n)` の放出リクエストは蓄積され、次フレームで GPU パーティクルシステムが消費します（`emit_rate` による継続放出とは別枠）。`n` が 0 以下なら何もしません。

### 利用可能なコンポーネント一覧

| コンポーネント名 | アクセサ | 内容 |
|---|---|---|
| `Transform` | `gameObject.Transform` / `transform` | 3D 位置・回転・スケール |
| `CanvasTransform` | `gameObject.CanvasTransform` | 2D キャンバス上の位置・回転・スケール・ピボット・アンカー |
| `Sprite` | `gameObject.Sprite` | テクスチャパス・色・サイズ・レイヤー |
| `Camera` | `gameObject.Camera` | FOV・クリップ距離・メインカメラ・クリアカラー・ベース解像度 |
| `Audio` | `gameObject.AudioSource` | 音源パス・音量・ループ・3D 減衰・パン + Play/Stop |
| `Animator` | `gameObject.Animator` | 再生中クリップ・再生位置・速度 + Play/Stop/Pause/Resume |
| `ParticleEmitter` | `gameObject.ParticleEmitter` | 放出レート・ループ・抵抗・拡散角 + Play/Stop/Burst |

他のコンポーネント（Collider / Rigidbody など物理系）は物理 API として順次対応予定で、対応済みのものは本節に追記されます。

---

## 7.5 Scene（シーン遷移）

シーンは**エディタの「プロジェクト設定 → シーンマネージャ」で登録した名前**で参照します（`assets://` パス直接指定も可能）。

```csharp
// 推奨フロー: 事前読み込み → 遷移（遷移フレームのロード時間がなくなる）
SEED.Scene.Load("game");         // 事前読み込み（遷移はしない。フェード中などに呼ぶ）
// ... フェードアウト演出など ...
SEED.Scene.Transition("game");   // 即座に切り替わる

// Load を省略しても OK（Transition が内部で自動的に読み込む。その分遷移が重い）
SEED.Scene.Transition("result");
```

- `Load` = 事前読み込みのみ（現在のシーンはそのまま）。保持できる事前読み込みは 1 つで、直後の `Transition`（同じシーン）で消費されます。
- `Transition` = シーン切り替え。**フレーム末尾**に行われ、現在のシーンの全アクター・スクリプトは破棄されます。
- 読み込みに失敗した場合は現在のシーンが維持されます（戻り値 true は「受理」であり成功保証ではない）。
- **注意**: `Transition` を呼んだフレームで発行した `Instantiate` / `Destroy` は破棄されます。シーン遷移を決めたら、そのフレームではそれ以上シーン操作をしないでください。

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
