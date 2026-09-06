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

### インスペクタ公開の属性（`SEEDEditor.Scripting` 名前空間）

フィールドやクラスに付ける属性。すべて `using SEEDEditor.Scripting;`（テンプレートに含まれる）だけで使えます。

| 属性 | 付ける先 | 効果 |
|------|----------|------|
| `[SerializeField]` | フィールド | インスペクタに公開する。`Label = "表示名"` / `Tooltip = "説明"` を名前付き引数で指定できる（省略時のラベルはフィールド名を整形したもの） |
| `[Header("見出し")]` | フィールド | そのフィールドの直前に見出し行を挿入する |
| `[Tooltip("説明")]` | フィールド | マウスオーバー時の説明。`[SerializeField(Tooltip = ...)]` より**独立した `[Tooltip]` が優先**される |
| `[Range(min, max)]` | フィールド | 数値フィールドをスライダー表示にする（float / int） |
| `[ResetButton]` | フィールド | 行の右端に「デフォルトに戻す」ボタン（⟲）を出す。押すと**宣言の初期化子の値**（無ければ 0 / false / 空文字 / 参照は未設定）へ戻る。Ctrl+Z で取り消せる |
| `[Bindable]` | フィールド | このフィールドを**シェーダパラメータのバインド元**として公開する（`[SerializeField]` との併用が必須）。対応型は `float` と `Vector3` のみ |
| `[RequireComponent(typeof(OtherScript))]` / `[RequireComponent("Camera")]` | クラス | アタッチ時に不足コンポーネントを**自動追加**する。型指定＝他スクリプト（型名から `.cs` を探す）、文字列指定＝ネイティブコンポーネント名 |
| `[DisallowMultipleComponent]` | クラス | 同一アクターに同じスクリプトを 2 つ以上付けられなくする（追加操作が警告で中止される） |

```csharp
[RequireComponent("Camera")]
[DisallowMultipleComponent]
public class CameraShake : SEEDScript
{
    [Header("揺れ")]
    [SerializeField(Label = "強さ")]
    [Range(0f, 5f)]
    private float amplitude = 1.0f;

    [SerializeField]
    [Tooltip("1 秒あたりの振動回数")]
    private float frequency = 8.0f;

    // 行末の ⟲ を押すと 1.5 に戻る（初期化子が無い場合は 0 に戻る）
    [SerializeField, ResetButton]
    private float damping = 1.5f;
}
```

**ホットリロード時の挙動**

- スクリプトエディタで `.cs` を保存すると再コンパイルが走り、成功すればインスペクタの `[SerializeField]` 行は**スクリプトを付け直さなくても自動で作り直されます**。設定済みの値は「フィールド名＋型が一致するもの」だけが引き継がれ、新しく増えたフィールドは宣言時の初期値、型を変えたフィールドは初期値に戻り、消したフィールドの値は破棄されます（コンパイル失敗時は何も変わりません）。

**`[ResetButton]` の詳細**

- 戻り先はフィールド**宣言の初期化子**の値です。初期化子が無ければ言語既定値（数値 `0` / `bool false` / `string` 空文字）になります。
- 参照フィールド（`GameObject` / `Transform` など）に付けた場合は「未設定」へ戻ります（✕ ボタンと同じ結果）。
- リセットは通常の値編集と同じ経路を通るため **Ctrl+Z で取り消せます**（リセット前の値に戻る）。
- 対応型は数値（float / double / int / long / short）・bool・string・参照フィールドです。**列挙型など、インスペクタが読み取り専用表示にする型ではボタンは出ません**。既定値の文字列に改行が含まれる場合もボタンは出ません（1 行 1 コマンドの通信経路に載せられないため）。
- `[Serializable]` ネストクラスの**フィールドそのもの**に付けてもボタンは出ません（子をまとめて戻すと Ctrl+Z が 1 手にまとまらないため）。**ネストの中の個々のフィールド**には付けられます。その場合の戻り先は**そのネストクラス側の初期化子**であり、外側での `new Nested { inner = 99f }` のような初期化は反映されません。

**`[Bindable]` の詳細**

- 水面シェーディングアセット（`.wgsl`）が `@ref` を付けて宣言したパラメータは、
  インスペクタで「アクタ → コンポーネント → 変数」を選ぶだけで、このフィールドの
  **実行中の値**が毎フレームシェーダへ流し込まれます（`docs/water_shading_asset.md` の
  「3.6 `@ref` — シーンから値を流し込む」を参照）。
- `[SerializeField]` との**併用が必須**です。`[SerializeField]` の無いフィールドに
  付けてもバインド候補には現れません（インスペクタに出ない値をバインド候補に出すと、
  何がどこから流れているのか追跡できなくなるため）。
- 対応する型は WGSL 側と厳密に対応する 2 つだけです。
  - `float` … WGSL の `f32` パラメータへ繋がる
  - `Vector3` … WGSL の `vec3<f32>`（色）パラメータへ繋がる

  **成分の部分取り出しは行いません。** `Vector3` を `f32` のパラメータへ繋ぐことは
  できません（X 成分だけ欲しいなら `float` のフィールドを別に用意してください）。
  上記以外の型に付けても候補には現れません。
- 値は**毎フレーム、描画の直前に実行中のインスタンスから直接**読まれます（Edit /
  Play の両方）。したがって `Update` などで書き換えた値がそのままシェーダへ届きます。
- `[Bindable]` が付いているかどうかの検証は、**値を読み取るたびにランタイム側で
  毎回行われます**（設定時に一度だけ検証してキャッシュする方式ではありません）。
  そのため、バインドを張った後にこの属性を外す・フィールドを消す・型を変える、
  といった変更をすると、**その瞬間からバインドは静かに切れます**。
  シェーダのパラメータは保存値（無ければアセットの既定値）へフォールバックし、
  インスペクタのその行に ⚠ が出ます。
- スクリプトのコンパイルに失敗している間は、そのスクリプトのフィールドはバインド
  候補に一切現れません（まずコンパイルエラーを直してください）。

```csharp
[SerializeField, Bindable]
private float glowPower = 1.0f;
```

- `[Serializable]` を付けたクラス／構造体型のフィールドに `[SerializeField]` を付けると、インスペクタで**子フィールドが再帰的に展開**されます（入れ子の上限は 8 段）。
- `GameObject` やコンポーネントハンドル型（`Transform` / `Camera` など）のフィールドに `[SerializeField]` を付けると、**他アクターへの参照フィールド**になります（Hierarchy から D&D で設定）。詳細は第 7 節の「参照フィールド」を参照してください。

### 配列・リストのフィールド

`T[]` と `List<T>` のフィールドは、インスペクタで**要素を追加・削除できる折りたたみ行**になります。

```csharp
[SerializeField(Label = "巡回速度")]   private float[] patrolSpeeds = { 1.0f, 2.0f };
[SerializeField(Label = "出現プレハブ")] private List<string> spawnPrefabs = new();
[SerializeField(Label = "追従対象")]   private SEED.Transform[] followTargets;
```

- 要素型は `float` / `double` / `int` / `long` / `short` / `bool` / `string`、参照フィールドに使える型
  （`GameObject` / `Transform` / `Camera` などのハンドル型）、および
  `[System.Serializable]` を付けた**自作の構造体／クラス**（後述）に対応します。
  それ以外の要素型（列挙型など）は従来どおり読み取り専用表示になります。
- 見出しは「フィールド名 (件数)」で、`[＋]` が末尾への追加、行ごとの `[×]` がその要素の削除です。
  追加された要素の初期値は数値なら 0、真偽値なら `false`、文字列・参照は未設定です。
- 参照要素は単体の参照フィールドと同じく Hierarchy から D&D で設定でき、`OnStart` の直前に解決されます。
- `string` 要素の行には Project パネルから `.actor` ファイルをドロップでき、
  `assets://` 仮想パスが入ります（`GameObject.Instantiate(path)` へそのまま渡せます）。
- 多次元配列・ジャグ配列（`float[,]` / `float[][]`）は対象外です。
- 保存形式は 1 フィールド = JSON 配列文字列（例 `[1.0,2.5]` / `["a","b"]`）です。
  再コンパイル時の値引き継ぎは、**要素型まで一致するときだけ**行われます
  （`float[]` → `string[]` のような変更では宣言時の初期値に戻ります）。

### 構造体のリスト（`[System.Serializable]` 構造体の配列）

`[System.Serializable]` を付けた構造体／クラスを要素にすると、
**「1 件ぶんのまとまり」を単位に増やしていけるデータ表**をインスペクタで組めます。

```csharp
[System.Serializable]
public struct FishLevelEntry
{
    [SerializeField(Label = "出現距離")]        public float spawnDistance;
    [SerializeField(Label = "魚prefab(.actor)")] public List<string> fishPrefabs;
}

public class FishManager : SEEDScript
{
    [SerializeField(Label = "レベル定義")]
    private List<FishLevelEntry> levels = new();
}
```

- インスペクタでは配列の折りたたみの中に、要素ごとの折りたたみ（`[0]` `[1]` …）が並び、
  その中に構造体メンバの行が出ます。`[＋]` で 1 件追加、要素右端の `[×]` でその 1 件を削除します。
- メンバに使えるのは `[SerializeField]` を付けた public / private フィールドで、型は
  **スカラ型（`float` / `double` / `int` / `long` / `short` / `bool` / `string`）・参照型・
  それらの `List<>` / 配列**です。メンバの `List<>` は要素の追加・削除まで編集できます。
- **入れ子は 1 段まで**です。構造体の中に構造体（またはそのリスト）を置くと、
  そのフィールド全体が対象外になり読み取り専用表示へ落ちます。
- 追加した要素の初期値は、クラス要素なら宣言時の初期化子、構造体要素なら言語既定値（0 / `false` / 空）です。
- 保存形式は 1 フィールド = JSON オブジェクト配列文字列
  （例 `[{"spawnDistance":10.0,"fishPrefabs":["assets://fish.actor"]}]`）です。
- 再コンパイル時の値引き継ぎは**メンバ名で照合**します。
  メンバを増やしても既存の値は残り（新メンバは既定値）、削除したメンバの値は無視されます。
  同名メンバの**型を変えた場合だけ**、そのフィールドが宣言時の初期値へ戻ります。

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

### 生成・破棄コールバック（OnStart / OnDestroy）

フレームごとの更新とは別に、インスタンスの一生に 1 回ずつ呼ばれるコールバックがあります。**引数は取りません**。

| 関数 | 呼ばれるタイミング |
|------|--------------------|
| `OnStart()`   | 有効化後、そのスクリプト自身の**最初の `BeginFrame` の直前**に 1 回だけ |
| `OnDestroy()` | スクリプトインスタンスが破棄される直前に 1 回だけ |

```csharp
public class Enemy : SEEDScript
{
    private float _initialY;

    public override void OnStart()
    {
        // 初期化はここ。gameObject / transform は利用可能
        _initialY = transform.Position.y;
        SEED.Debug.Log("Enemy 出現");
    }

    public override void OnDestroy()
    {
        // 後片付けはここ（シーンへのアクセスは行わないこと）
        SEED.Debug.Log("Enemy 消滅");
    }
}
```

**`OnStart` の呼び出し規約**

- 呼ばれるのは **Play モードのみ**（編集モードではスクリプトのライフサイクルは走りません）。
- タイミングは「全スクリプトの `BeginFrame` 群より前にまとめて」ではなく、**スクリプトごとに、そのスクリプトの初回 `BeginFrame` の直前**です。したがって「A の OnStart → A の BeginFrame → B の OnStart → B の BeginFrame」の順になります。他スクリプトの `OnStart` 完了を前提にした処理は `BeginFrame` 以降で行ってください。
- 対象は次の 2 つで、どちらも同じ規約です。
  1. **Play 開始時**にシーンへ存在した全スクリプト → Play 開始後の最初のフレームの `BeginFrame` の直前。
  2. **`GameObject.Instantiate` で動的生成**されたアクターのスクリプト → 生成が実際にシーンへ適用されるのは発行フレームのゲームロジック後なので、`OnStart` は**次のフレーム**の `BeginFrame` 直前になります（Unity の `Start` と同じ考え方）。
- 一時的に無効化（アクター/スロットの非アクティブ化）していたスクリプトを再度有効化しても、`OnStart` が二度呼ばれることはありません（インスタンスにつき 1 回）。
- `ctx`（`DeltaTime` 等）は渡されません。フレーム時間が必要な初期化は `BeginFrame` 側で行ってください。

**`OnDestroy` の呼び出し規約**

- 発火する破棄経路は次のすべてです。
  1. アクターの破棄（`gameObject.Destroy()` / `GameObject.Destroy(...)`。実際の破棄はそのフレームのゲームロジック後に遅延適用され、その時点で呼ばれます）
  2. シーン遷移・シーンリロード（旧シーンの全スクリプト）
  3. Play の終了（シーン上の全スクリプト）
  4. スクリプトのホットリロード（再コンパイルで旧インスタンスが捨てられるとき）
- `OnStart` が一度も呼ばれていないインスタンスでは呼ばれません（`OnStart` と 1 対 1 で対応します）。
- **同フレームの他コールバックとの関係**: `Destroy` は即時ではなく、`Render` フェーズまで走り終えた後の「シーン操作コマンド適用」で実行されます。したがって破棄を要求したフレームでは `BeginFrame`〜`Render` は**通常どおり最後まで実行**され、その直後に `OnDestroy` が呼ばれます。**そのフレームの `EndFrame` は呼ばれません**（`EndFrame` はフレーム末尾で走るため）。`EndFrame` の中で `Destroy` を呼んだ場合は `EndFrame` 実行直後（同フレーム末尾）に `OnDestroy` が呼ばれます。いずれの場合も `OnDestroy` の後にそのインスタンスへコールバック（ライフサイクル・物理イベントとも）が来ることはありません。
- **破棄処理中に呼ばれるため、シーンへのアクセスは保証されません**。`OnDestroy` 内での `transform` の読み書き・`GameObject.Find` は既定値／無効な結果になります。位置などを使いたい場合は破棄を要求する前に控えておいてください。
- **再入（OnDestroy 内での生成・破棄）は無視されます**。`GameObject.Instantiate` / `Destroy` は `OnDestroy` の実行中に限り受理されず、何も起きません（遅延実行もされません）。破棄の連鎖でシーンが不定状態になるのを防ぐための仕様です。

### 物理イベントコールバック

自分のアクターのコライダー（ColliderComponent / Collider2dComponent）が他のコライダーと衝突・接触すると、以下が呼ばれます（3D / 2D 共通）。`other` は相手アクターの GameObject です（特定できない場合は `IsValid == false`）。

| 関数 | 呼ばれるタイミング |
|------|--------------------|
| `OnCollisionEnter(SEED.GameObject other)` | 衝突が始まったフレーム |
| `OnCollisionStay(SEED.GameObject other)`  | 衝突継続中（毎物理ステップ） |
| `OnCollisionExit(SEED.GameObject other)`  | 衝突が終わったフレーム |
| `OnTriggerEnter(SEED.GameObject other)`   | トリガーへの進入時（トリガー側・相手側の両方に通知） |
| `OnTriggerStay(SEED.GameObject other)`    | トリガーに重なり続けている間（毎物理ステップ・同上） |
| `OnTriggerExit(SEED.GameObject other)`    | トリガーからの退出時（同上） |

`OnTriggerStay` は `OnCollisionStay` と同じ頻度規約で、重なりが続いている限り**毎物理ステップ**呼ばれます（フレームあたり複数回呼ばれうる点に注意）。`OnTriggerEnter` が発火したステップでは `Enter` と `Stay` の両方が届きます。

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

### ポインタイベントコールバック（キャンバス UI のクリック）

自分のアクターが持つ `Sprite` / `SkinnedSprite` の **`raycast_target`（インスペクタの「ポインタ判定」チェック）を ON** にすると、Play 中のマウス操作が以下のコールバックで届きます。既定は OFF なので、ボタンにしたいスプライトだけを明示的に ON にします。

| 関数 | 呼ばれるタイミング |
|------|--------------------|
| `OnPointerEnter()` | カーソルがこのアクターへ乗った最初のフレーム |
| `OnPointerExit()`  | カーソルがこのアクターから外れた最初のフレーム |
| `OnPointerDown()`  | このアクターの上で左ボタンが押された瞬間 |
| `OnPointerUp()`    | このアクターの上で左ボタンが離された瞬間 |
| `OnPointerClick()` | 押下と解放が同一アクターで完結したとき（`OnPointerUp` の直後） |

```csharp
public class TitleButton : SEEDScript
{
    public override void OnPointerEnter() { /* ハイライト */ }
    public override void OnPointerExit()  { /* 戻す */ }
    public override void OnPointerClick() { SEED.Scene.Load("assets://scenes/game.scene"); }
}
```

> **重要**: 判定は毎フレーム 1 回だけ行われ、**最前面の 1 アクターにだけ**イベントが届きます（重なり順は「描画ゾーン → `Layer` が大きい方 → ヒエラルキー順で後」）。判定形状は Sprite が表示矩形、SkinnedSprite が変形後メッシュの三角形で、どちらも見た目と一致します。

> **重要**: 対応するのは**スクリーンスペースキャンバス**（Actor2D + Canvas）だけです。3D ワールド内に置いたキャンバス（Actor3D + Canvas）にはポインタイベントは届きません。また非アクティブなアクター・無効化したスロットのスプライトは判定対象外です（＝見えていないものはクリックできません）。

```csharp
// ボタンの作り方（レシピ）
// 1. Canvas 配下に Sprite を持つ子アクターを作る
// 2. インスペクタで Sprite の「ポインタ判定」を ON にする（= raycast_target）
// 3. 同じアクターへスクリプトを追加して OnPointer* を実装する
public class Button : SEEDScript
{
    // 色はスクリプトから変えるだけで「押した感じ」が作れる（エンジンに Button 型は無い）
    private static readonly SEED.Color Normal = SEED.Color.White;
    private static readonly SEED.Color Hover  = new SEED.Color(0.85f, 0.95f, 1f, 1f);
    private static readonly SEED.Color Press  = new SEED.Color(0.6f, 0.7f, 0.9f, 1f);

    private void Tint(SEED.Color c)
    {
        if (gameObject.GetComponent<SEED.Sprite>() is { } s) s.Color = c;
    }

    public override void OnStart()        => Tint(Normal);
    public override void OnPointerEnter() => Tint(Hover);
    public override void OnPointerExit()  => Tint(Normal);
    public override void OnPointerDown()  => Tint(Press);
    public override void OnPointerUp()    => Tint(Hover);
    public override void OnPointerClick() => SEED.Debug.Log("押された");
}
```

### スクリプト例外の扱い

ライフサイクル関数・物理イベントコールバックの中で**未処理の例外**（`Nullable` の `.Value`、`NullReferenceException`、`IndexOutOfRangeException` など）が発生しても、**ランタイムプロセスは落ちません**。エンジン側がすべてのコールバック境界で例外を捕捉します。

| 項目 | 挙動 |
|------|------|
| プロセス | 継続する（強制終了しない） |
| ゲーム進行 | 継続する。中断されるのは**例外を出したスクリプトの、その 1 回の呼び出しだけ** |
| ログ | エディタのログパネルに `[SCRIPT ERROR] {型名}.{関数名}: {メッセージ}` の 1 行と、続けてスタックトレースが出力される |
| 繰り返し発生時 | 全文スタックは**初回のみ**。以降は 300 回に 1 回、累計回数付きの 1 行サマリのみ（毎フレーム全文を吐くとログが溢れるため） |

**例外を放置しないこと。** 落ちないのはあくまで安全網であり、正常動作ではありません。例外が出続けているスクリプトはその関数が毎フレーム途中で打ち切られているため、状態更新が飛んで挙動が壊れます。`[SCRIPT ERROR]` を見つけたら必ず原因を直してください。

```csharp
// NG: hitInfo が null のフレームで例外 → その BeginFrame は以降が実行されない
var target = hitInfo.Value.Position;

// OK: null を明示的に扱う
if (hitInfo is { } hit) { var target = hit.Position; }
```

デバッガをアタッチしている場合でも**ブレークポイントは通常どおり機能します**。ただしエンジンが例外を捕捉するため「ユーザー未処理例外」での自動停止は発生しません。例外の発生箇所で止めたい場合は、デバッガ側の**例外ブレークポイント（first-chance）**を有効にしてください。

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
Mathf.PI, Mathf.Deg2Rad, Mathf.Rad2Deg, Mathf.Epsilon, Mathf.Infinity, Mathf.NegativeInfinity

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

- `Abs` / `Min` / `Max` / `Clamp` には **int 版のオーバーロード**もあります（`Mathf.Clamp(i, 0, 9)` は int を返す）。
- `Mathf.Epsilon` は `1e-6f`（.NET の `float.Epsilon` とは別物）。`Approximately` はこれを基準に相対誤差で比較します（許容差 = `max(Epsilon × max(|a|,|b|), Epsilon × 8)`）。

---

## 5. Vector2 / Vector3 / Quaternion（不変値型）

### Vector3（位置・方向・スケール）

```csharp
new Vector3(x, y, z)
new Vector3(x, y)      // z = 0
Vector3.Zero Vector3.One Vector3.Up Vector3.Down Vector3.Left Vector3.Right Vector3.Forward Vector3.Back

v.x v.y v.z
v.Magnitude v.SqrMagnitude v.Normalized

a + b, a - b, -a, a * 2f, 2f * a, a / 2f, a == b, a != b

Vector3.Dot(a, b) Vector3.Cross(a, b) Vector3.Distance(a, b) Vector3.Scale(a, b)
Vector3.Lerp(a, b, t) Vector3.MoveTowards(cur, target, maxDelta) Vector3.Angle(a, b)
Vector3.Min(a, b) Vector3.Max(a, b)
```

`Vector2` も同様（`x, y` と `Zero/One/Up/Down/Left/Right`、`Magnitude/SqrMagnitude/Normalized`、`Dot/Distance/Scale/Lerp/Min/Max`）。ただし `Cross` / `MoveTowards` / `Angle` は **Vector3 のみ**です。

### Quaternion（回転）

```csharp
Quaternion.Identity
Quaternion.Euler(xDeg, yDeg, zDeg)  // オイラー角（度）から。適用順は YXZ
Quaternion.Euler(vector3Degrees)
Quaternion.AngleAxis(angleDeg, axis)
Quaternion.LookRotation(forward)              // forward を +Z へ向ける回転（up はワールド上基準）
Quaternion.LookRotation(forward, rollDeg)     // 上に加え、視線軸まわりに rollDeg 回転

q1 * q2          // 回転の合成
q * vector3      // ベクトルを回す
q.EulerAngles    // Vector3（度）へ変換（Transform.Rotation へ書き戻す用）
q.Normalized
```

> Transform の回転は **YXZ オイラー角（度）の Vector3** で表します。合成・補間したいときだけ Quaternion を使い、`q.EulerAngles` で Vector3 に戻します。
> `LookRotation` は forward がゼロ長なら Identity、forward が上下方向とほぼ平行なときは代替の上方向で基底を作り直します（真上/真下を向いても破綻しません）。

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
Input.MousePosition   // Vector2: MousePos の別名
Input.MouseMove       // Vector2: 今フレームの相対移動量（OS の Raw Input 由来。
                      //          エディタ埋め込み Play では届かないことがある）
Input.MouseDelta      // Vector2: 今フレームのカーソル座標差分（埋め込み Play でも必ず取れる。
                      //          ジェスチャ判定はこちらを積む。画面端で止まると 0）
Input.MouseScroll     // float:   今フレームのホイール量（上=正）
Input.MousePositionCanvas // Vector2: キャンバス座標（画面中央が原点・Y 下向き・1 単位=1px）
                          //          UI のポインタ判定と同じ座標系。CanvasTransform.Position と直接比較できる
                          //          キャンバス世界線でない・Play 外では (0,0)

// カーソルロック（相対マウスモード）
Input.CursorLocked    // bool（get/set）: ロック中はカーソルを隠して毎フレーム画面中央へ戻す。
                      //   MouseDelta が画面端でクランプされず取れるようになる
Input.SetCursorLock(true) // CursorLocked = true の別名

// 例: マウスジェスチャ（引いてから前へ振る）の判定
private SEED.Vector2 _swing;
public override void Update(ref NativeFrameContext ctx)
{
    _swing = _swing * 0.8f + SEED.Input.MouseDelta;   // 直近の振りを指数移動平均で蓄える
    if (_swing.y < -40f) { /* 上方向へ強く振った = キャスト */ }
}

// 簡易軸入力（WASD/矢印 → [-1,1]。斜めは正規化しない）
Input.MoveAxis()      // Vector2

// 例: WASD 移動 + スペースでジャンプ判定
var move = SEED.Input.MoveAxis();
transform.Position += new SEED.Vector3(move.x, 0f, move.y) * speed * SEED.Time.DeltaTime;
if (SEED.Input.GetKeyDown(SEED.KeyCode.Space)) { /* ジャンプ */ }
```

> **重要**: `Input.CursorLocked = true` の間、カーソルは**非表示**になり毎フレーム
> ビューポート中央へ戻される。そのため `MouseDelta` は画面端で 0 に潰れず動き続ける
> （エディタ埋め込み Play では ClipCursor でカーソルが閉じ込められるため、ボタンを
> 押さないマウスジェスチャの判定にはロックがほぼ必須）。
> 反面、ロック中は `MousePos` / `MousePositionCanvas` が中央に張り付き**意味を持たない**
> ので、UI のヒット判定と併用しないこと。
> Play を停止すると自動的に解除されるため、解除し忘れでカーソルが消えたままにはならない。

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
public override void Update(ref NativeFrameContext ctx)
{
    // 水平移動だけ書けばよい（落下は「重力を適用」チェックがエンジン側で処理する）
    transform.Position += horizontal * moveSpeed * ctx.DeltaTime;
}
```

#### 重力を適用（ノーコードで落下させる）

Collider インスペクタの「キャラクターコントローラー」を ON にすると、その直下に「**重力を適用**」チェックボックスが現れます。ここを ON にするだけで、**スクリプトを 1 行も書かずに**キャラクターが落下し、地面で止まります。

- 重力加速度はエンジンの物理設定（`-9.81 m/s²`）を使います。落下は**終端速度 55 m/s** で頭打ちになります（床のすり抜け防止）。
- 落下量は**スクリプトが同じフレームに書いた移動へ加算合成**されます。水平移動を上書きしないので、上の移動スクリプトとそのまま併用できます。適用順は「スクリプトの Update 群 → 重力 → KCC 衝突解決」です。
- 接地中は落下速度が 0 にリセットされ、空中で加速、着地でまた 0 に戻ります。接地状態は `SEED.Physics.IsGrounded(gameObject)` で読めます。
- 段差の乗り越え・斜面の登坂／滑り落ちは KCC の既存設定（段差 0.3m・斜面 45 度）に従います。
- **ジャンプ**は今までどおりスクリプトで上方向に `transform.Position` を足してください。上向きの移動量が重力ぶんを上回っていれば上昇し、離れたあとは重力が自動で引き戻します。
- OFF（既定）のときの挙動は従来どおりで、落下させたい場合は自分で速度を積分します。既存シーンの動作は変わりません。

```csharp
// 「重力を適用」ON のキャラに、ジャンプだけスクリプトで足す例
if (SEED.Physics.IsGrounded(gameObject) && jumpPressed) { jumpVelocity = 5f; }
jumpVelocity = SEED.Mathf.Max(0f, jumpVelocity - 9.81f * ctx.DeltaTime); // 上昇ぶんだけ自前で減衰
transform.Position += new SEED.Vector3(0f, jumpVelocity * ctx.DeltaTime, 0f);
```

- 対象アクターは **Collider コンポーネント（カプセル推奨）** を持ち、インスペクタで「キャラクターコントローラー」を ON にしてください。カプセル形状が KCC のシェイプに使われます。
- 補正後の位置は集約経路で反映されるため、**子アクタ（カメラ等）も追従**します。
- 1 フレーム内で `Position` を何度書いても、物理ステップで **1 回だけまとめて** 解決されます。押し戻しの反映には物理同期の都合上 1 フレームの遅延があります。
- **`Physics.IsGrounded(gameObject)`**: キャラクターが接地しているかを返します（物理ステップ同期で自動更新）。
- **`transform.Teleport(pos)`**: 衝突を無視して瞬間移動します（下記 Transform 参照）。ワープ・リスポーン・初期配置に使います。
- 重力はインスペクタの「重力を適用」チェックで自動化できます（上記）。OFF のままなら従来どおりスクリプト側の責務です。複数コライダーの合成移動は引き続きスクリプト側で行います。

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
SEED.Audio.SetBgmSpeed(1.25f);   // BGM 再生速度を変更（1.0 = 等倍）
SEED.Audio.StopBgm();            // BGM を停止
```

- `SetBgmSpeed` は早送り／スロー再生なので、**速度に比例してピッチも変わります**（テンポだけを変える機能ではありません）。値は 0.25〜4.0 にクランプされ、BGM を差し替えても保持されます（`PlayBgm` の前後どちらで指定しても同じ結果）。等倍へ戻すときは明示的に `1.0` を渡してください。素材の BPM が分かっていれば `SetBgmSpeed(目標BPM / 素材BPM)` で任意のテンポに合わせられます。
- 同じファイルはキャッシュされ、2 回目以降の再生でディスク読み込みは発生しません。
- オーディオデバイスが無い環境では全操作が無音で無視されます（エラーになりません）。
- アクターに紐づく音源（3D 距離減衰・パン対応）は **AudioComponent**（第 7 節の `gameObject.GetComponent<AudioSource>()`）を使ってください。こちらの静的 API はアクターに紐づかない BGM / 単発 SE 向けです。

---

## 7. GameObject とコンポーネント（GetComponent<T>）

スクリプトは自分がアタッチされた GameObject を `gameObject`、その Transform を `transform`（短縮）で参照できます。コンポーネントは **`gameObject.GetComponent<T>()`** で型引数指定して取得します。戻り値は `T?`（`Nullable<T>`）で、**未アタッチ・該当なしは `null`** です。取得したハンドルは薄く、プロパティへの代入は即座にゲーム世界へ反映されます。

SEED のアクターは**同種コンポーネントを複数スロット**持て、スロットには**名前**があります。`GetComponent` は 3 とおりの索引に対応します。

```csharp
gameObject                            // GameObject: このスクリプトが乗るオブジェクト
gameObject.IsValid                    // bool: 実体が有効か
gameObject.HasComponent("Sprite")     // bool: 指定名のコンポーネントを持つか
transform                             // Transform: gameObject.GetComponent<Transform>() の短縮

gameObject.GetComponent<T>()          // T?: 0 番目のスロット（未アタッチは null）
gameObject.GetComponent<T>(1)         // T?: index 番目のスロット
gameObject.GetComponent<T>("Weapon")  // T?: スロット名一致

// null 合体・パターンで安全に使う（Unity の GetComponent とは戻り値が Nullable な点が異なる）
if (gameObject.GetComponent<Sprite>() is { } sprite)
{
    sprite.Color = SEED.Color.Red;
}

// T に指定できる型: Transform / CanvasTransform / Sprite / Camera /
//                   AudioSource / Animator / ParticleEmitter / InputMap
```

> **重要**: `GetComponent<T>()` は未アタッチ時に `null` を返します。`is { } x` パターンか `?.` / `??` で受けてください（Unity と違い戻り値は `Nullable<T>` です）。`Transform` / `CanvasTransform` はアクターのルートに 1 つだけ存在し、`index` / `name` は無視されます。

> **`HasComponent(name)` の名前**: 受け付ける文字列は `Transform` / `CanvasTransform` / `Sprite` / `Camera` / **`Audio`**（AudioSource ではなく `Audio`）/ `Animator` / `ParticleEmitter` / `InputMap` の 8 つで、それ以外は常に false です。型で判定できる場面では `GetComponent<T>() is { }` のほうが安全です。

### 生成・破棄・検索（Instantiate / Destroy / Find）

```csharp
// .actor ファイル（プレハブ）からアクターを生成する（assets:// 仮想パス）
var bullet = SEED.GameObject.Instantiate("assets://actors/Bullet.actor");
if (bullet.GetComponent<Transform>() is { } bt)   // 生成直後に位置設定できる
    bt.Position = transform.Position;
if (!bullet.IsValid) { /* 読み込み失敗 */ }

// アクターを破棄する（実際の破棄はフレーム末尾。Unity の Destroy と同じ遅延モデル）
bullet.Destroy();                       // インスタンス版
SEED.GameObject.Destroy(bullet);        // 静的版（同じ動作）

// アクターを名前で検索する（ヒエラルキーの DFS 順で最初の一致）
var player = SEED.GameObject.Find("Player");
if (player.GetComponent<Transform>() is { } pt) { pt.Position = SEED.Vector3.Zero; }
```

- `Instantiate` の戻り値には**同フレーム中に** `Transform.Position` 等を設定でき、その値が優先されます（アクター本体の構築はフレーム末尾に行われます）。
- **2D アクター（Actor2D）の注意**: 構築時に Transform が CanvasTransform へ差し替わるため、生成直後の 3D Position 設定は反映されません。位置は翌フレーム以降に `CanvasTransform.Position` で設定してください。
- 破棄済み GameObject への読み取りは既定値、書き込みは無視されます（クラッシュしません）。
- 現時点の制限: Play 開始後に生成・破棄したアクターの**物理コライダーは物理スレッドに反映されません**。コライダーの収集は Play 開始時（およびシーン遷移時）の一括処理で、`Instantiate` / `Destroy` は物理側の追加・除去を行わないためです。衝突・トリガーの**イベント通知**自体は実装済みですが（第 2 節）、実行時に生成したアクターはその対象になりません。

### Transform（3D 位置・回転・スケール）

```csharp
transform.Position         // Vector3（get/set）
transform.WorldPosition    // Vector3（get のみ。ワールド絶対座標 = Position と同値）
transform.Rotation         // Vector3（get/set。YXZ オイラー角・度）
transform.Scale            // Vector3（get/set）
transform.Teleport(pos)    // void: 衝突を無視して pos へ瞬間移動（キャラクターコントローラー用）

// 方向ベクトル（すべて get のみ・ワールド空間・正規化済み）
transform.Forward          // Vector3（回転 0 のとき (0,0,1)）
transform.Back             // Vector3（-Forward）
transform.Right            // Vector3（回転 0 のとき (1,0,0)）
transform.Left             // Vector3（-Right）
transform.Up               // Vector3（回転 0 のとき (0,1,0)）
transform.Down             // Vector3（-Up）

// 例: 回しながら上げる（エンジン API は SEED. で修飾）
transform.Rotation += new SEED.Vector3(0f, 90f * SEED.Time.DeltaTime, 0f);
transform.Position += SEED.Vector3.Up * SEED.Time.DeltaTime;

// 例: 自分の向いている方向へ前進する
transform.Position += transform.Forward * 5f * SEED.Time.DeltaTime;
```

> **重要 — エンジンの前方向は +Z**（左手系）です。`Transform.Rotation` が 0 のとき
> `Forward == (0,0,1)` / `Right == (1,0,0)` / `Up == (0,1,0)` になります。方向ベクトルは
> スケールの影響を受けず常に正規化済み・ワールド空間で、`Back` / `Left` / `Down` は
> それぞれ `Forward` / `Right` / `Up` の符号反転です（すべて get のみ）。

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
if (gameObject.GetComponent<CanvasTransform>() is { } ct)   // CanvasTransform?（未アタッチは null）
{
    ct.Position            // Vector2（get/set。親 Canvas 基準の相対座標）
    ct.Rotation            // float（get/set。Z 軸周りの度）
    ct.Scale               // Vector2（get/set）
    ct.Pivot               // Vector2（get/set。回転・スケール基準点。正規化 [0,1]、(0.5,0.5)=中央）
    ct.Anchor              // Vector2（get/set。親 Canvas 内の position 基準点。(0,0)=左上 (1,1)=右下）
    ct.ScreenPosition      // Vector2（get のみ。ウィンドウ左上原点のスクリーン座標・ピクセル）
}
```

> `Position` は**親 Canvas 相対**の座標ですが、`ScreenPosition` はアンカー・スケールモード・親チェーンをすべて反映した**画面上の絶対位置**（ピボット点）を返します。SEED の 3D `Transform.Position` は元々ワールド絶対座標で、書き込み時に子孫へ差分が伝播します（上記「親子の追従」参照）。

### Model（3D モデルの表示切替・描画オフセット）

アクターの `Transform` は動かさずに、**そのモデルの描画だけ**をローカルにずらす／回す／拡縮する補正値です。
モデルの原点ズレ補正や、手に持たせた道具（釣り竿など）のグリップ位置合わせに使います。

```csharp
if (gameObject.GetComponent<Model>() is { } model)   // Model?（未アタッチは null）
{
    model.OffsetPosition   // Vector3（get/set。アクターのローカル空間・既定 (0,0,0)）
    model.OffsetRotation   // Vector3（get/set。YXZ オイラー角・度・既定 (0,0,0)）
    model.OffsetScale      // Vector3（get/set。既定 (1,1,1)）
    model.Visible          // bool（get/set。既定 true。false でこのモデルだけ描かれなくなる）

    model.LocalBoundsMin   // Vector3（get のみ。モデルローカル AABB の最小側）
    model.LocalBoundsMax   // Vector3（get のみ。モデルローカル AABB の最大側）
    model.LocalBoundsSize  // Vector3（get のみ。Max − Min ＝ 各軸の長さ）

    // 例: 座標は追従させたまま見た目だけ隠す（画面外へ退避させる必要はない）
    model.Visible = false;

    // 例: 釣り竿の持ち手を手の位置へ合わせる
    model.OffsetPosition = new Vector3(0f, -0.15f, 0.4f);
    model.OffsetRotation = new Vector3(0f, 0f, 25f);
}
```

> **重要**: `Visible = false` は**描画だけ**を止めます。Transform・親子の追従・JointAttach のソケット追従・コライダー・スクリプトは通常どおり更新され続けるので、子アクタをカメラの注視点にしているような「見えないが位置は正しくいてほしい」オブジェクトを安全に隠せます（非表示中は影も落とさず、選択アウトラインも出ません）。

> **重要**: オフセットは**描画にだけ**効きます（通常描画・スキン・LOD・影・レイトレース・クリック判定・選択枠まで一貫）。物理コライダー・レイキャスト・`Transform` の値は一切変わりません。当たり判定をずらしたい場合はコライダー側のオフセットを使ってください。

> **`LocalBounds*` はモデル空間の「素材寸法」です**（読み取り専用）。`OffsetPosition/Rotation/Scale` も
> アクターの `Transform`（位置・回転・スケール）も**掛かっていない**、読み込んだメッシュの生の頂点範囲を返します。
> 実際の見た目の大きさが要るときは呼び出し側で合成してください
> （例: `Vector3.Scale(Vector3.Scale(model.LocalBoundsSize, model.OffsetScale), transform.Scale)`。
> `Vector3` 同士の乗算演算子は無いので成分ごとの積は `Vector3.Scale` を使います）。
> スキンメッシュは**バインドポーズ**の寸法で、アニメーション変形は反映しません。
> モデルが未ロード（またはこのアクターに Model が無い）なら `Vector3.Zero` を返すので、
> 0 除算を避けたい計算では必ずゼロ判定を挟んでください。
> 全頂点を走査する計算なので、毎フレーム使う場合は**結果を控えて**使い回してください。

```csharp
// 例: モデルの実寸（高さ）を求めて、頭上に置く高さを自動で決める
if (gameObject.GetComponent<Model>() is { } m)
{
    var scaled = Vector3.Scale(m.LocalBoundsSize, m.OffsetScale);     // 成分ごとの積
    var size = Vector3.Scale(scaled, transform.Scale);
    float height = size.y;                                            // 見た目の高さ（m）
}
```

### Sprite（2D スプライト表示）

```csharp
if (gameObject.GetComponent<Sprite>() is { } sprite)   // Sprite?（未アタッチは null）
{
    sprite.TexturePath     // string（get/set。assets:// 仮想パス。空文字=単色表示）
    sprite.Color           // Color（get/set。RGBA。テクスチャに乗算）
    sprite.Width           // float（get/set。キャンバスユニット）
    sprite.Height          // float（get/set）
    sprite.Size            // Vector2（get/set。Width/Height をまとめて）
    sprite.Layer           // int（get/set。描画優先度。大きいほど手前。既定 0。
                           //     同値はヒエラルキー順。同一描画ゾーン内で比較される）
    sprite.RaycastTarget   // bool（get/set。ポインタイベント OnPointerEnter/Down/Up/Click/Exit の
                           //      判定対象にするか。既定 false のオプトイン）

    // 例: 点滅させる
    sprite.Color = SEED.Color.White.WithAlpha(SEED.Mathf.PingPong(SEED.Time.ElapsedTime, 1f));
}
```

### SkinnedSprite（メッシュ変形 2D スプライト）

`.sprite_mesh` のメッシュを、子アクター（＝ボーン）の `CanvasTransform` で変形しながら描画します。
レイヤー・色・描画ゾーンの規約は `Sprite` と完全に同じです。
**ボーンを動かす API はありません**——ボーンは普通の 2D 子アクターなので、そのアクターの
`CanvasTransform` を操作するか、`.anim` のプロパティトラックで再生してください。

```csharp
if (gameObject.GetComponent<SkinnedSprite>() is { } skin)   // SkinnedSprite?（未アタッチは null）
{
    skin.MeshPath          // string（get/set。.sprite_mesh の assets:// 仮想パス。空文字=非表示）
    skin.TexturePath       // string（get/set。assets:// 仮想パス。空文字=単色表示）
    skin.Color             // Color（get/set。RGBA。テクスチャに乗算）
    skin.Layer             // int（get/set。描画優先度。Sprite と同じ土俵で比較される）
    skin.RaycastTarget     // bool（get/set。ポインタイベントの判定対象にするか。既定 false。
                           //      判定形状は変形後メッシュの三角形）

    // 例: ボーン（アクター名 "elbow"）を回して腕を振る
    var elbow = SEED.GameObject.Find("elbow");
    if (elbow.GetComponent<CanvasTransform>() is { } ct)
        ct.Rotation = SEED.Mathf.Sin(SEED.Time.ElapsedTime) * 30f;
}
```

### Camera（3D カメラ設定）

カメラの位置・向きは同じ GameObject の `transform` で動かします。

```csharp
if (gameObject.GetComponent<Camera>() is { } cam)   // Camera?（未アタッチは null）
{
    cam.FieldOfView        // float（get/set。垂直視野角・度。透視投影時に使用）
    cam.Near / cam.Far     // float（get/set。クリップ距離）
    cam.IsMain             // bool（get/set。Play モードのメインカメラか）
    cam.ClearColor         // Color（get/set。背景クリアカラー）
    cam.TargetWidth / cam.TargetHeight  // int（get/set。スケーリングのベース解像度）
    cam.BarColor           // Color（get/set。レターボックス帯の色）
    cam.Projection         // string（get/set。"perspective" / "orthographic"）
    cam.OrthoHeight        // float（get/set。正射投影時の縦の描画範囲・ワールド単位）
}
```

- `Projection = "orthographic"` で平行投影（遠近感なし）。縦 `OrthoHeight`・横 `OrthoHeight × アスペクト比` の範囲を写します。透視投影時は `FieldOfView` を使用します。

### AudioSource（アクター紐づけの音源。3D 距離減衰・パン対応）

エディタの「コンポーネント追加 → サウンド → Audio Source」で追加し、インスペクタで設定します。

```csharp
if (gameObject.GetComponent<AudioSource>() is { } audio)   // AudioSource?（未アタッチは null）
{
    audio.Play();          // 設定された音源を再生（再生中なら鳴らし直し）
    audio.Stop();          // 停止
    audio.IsPlaying        // bool: 再生中か

    audio.Path             // string（get/set。assets:// 仮想パス）
    audio.Volume           // float（get/set。1.0=等倍。再生中も即反映）
    audio.Loop             // bool（get/set。次回 Play 時に反映）
    audio.PlayOnStart      // bool（get/set。Play 開始時に自動再生）
    audio.Spatial          // bool（get/set。3D 空間再生 = メインカメラとの距離減衰 + 方向パン）
    audio.MinDistance      // float（get/set。減衰開始距離。これ以内は音量 100%）
    audio.MaxDistance      // float（get/set。無音距離。これ以遠は聞こえない）
    audio.Pan              // float（get/set。-1=左 〜 1=右。Spatial=false 時のみ有効）
}
```

- 距離減衰は線形（MinDistance 以内 100% → MaxDistance で 0%）。リスナーは `is_main` のメインカメラ。
- `Spatial = true` では音源方向に応じて左右パンが自動で振られます（手動 `Pan` は無効）。

### Animator（キーフレーム / モデル内蔵アニメ再生・クロスフェード）

エディタの「コンポーネント追加 → アニメーター」で追加し、インスペクタで再生対象クリップ（`clips`）を登録します。クリップは 2 種類あり、`.anim` キーフレームクリップと、glTF モデル内蔵アニメ（インスペクタの「モデル内蔵アニメを追加」で登録。**モデル内のどのアニメでも選べます**）です。実際の評価・書き込みはエンジン側の AnimationSystem が毎フレーム自動で行うため、スクリプトからは再生の開始・停止・状態参照のみ行います。

> **エディタ操作**: モデルクリップの「アニメ名 (モデル)」はインスペクタのドロップダウンで、同アクターの Model スロットが持つ glTF アニメ一覧から選びます（`(未設定=先頭アニメ)` を選ぶと index 0 を再生。モデル差し替えなどで一覧に無くなった名前は ⚠ 付きの警告色で残るので選び直してください。モデル未設定・未ロードのときは一覧が空でドロップダウンは無効になります）。

```csharp
if (gameObject.GetComponent<Animator>() is { } anim)   // Animator?（未アタッチは null）
{
    anim.Play("Walk");            // 指定クリップを先頭（time=0）から再生（速度は変更しない）
    anim.Play("Walk", 1.5f);      // 再生速度も同時に指定して再生
    anim.Play("Walk", 1.5f, 0.2f);// 速度とクロスフェード時間（秒）を同時に指定
    anim.CrossFade("Walk", 0.25f);// 0.25 秒かけて現在のクリップから滑らかに切り替える
    anim.Stop();                  // 停止して time=0 に戻す（フェード状態も破棄）
    anim.Pause();                 // 再生位置とフェード状態を保持したまま一時停止
    anim.Resume();                // 一時停止を再開（再生対象クリップが無ければ無視）

    anim.IsPlaying                // bool（get のみ。再生中か）
    anim.CurrentClip              // string（get のみ。再生中のクリップ名。未再生は空文字）
    anim.Time                     // float（get/set。再生位置・秒。書き込みでシーク可能）
    anim.Speed                    // float（get/set。再生速度倍率。1.0=等倍、負値で逆再生）
    anim.DefaultFadeSeconds       // float（get/set。Play がフェード時間未指定のとき使う既定値。0=即時切替）
    anim.FadeWeight               // float（get のみ。フェード進行度。0=フェード元のみ / 1=フェードなし）
}
```

> **重要**: クロスフェードで補間されるのは **glTF モデル内蔵アニメ（モデルクリップ）同士の切替だけ**です。`.anim` キーフレームクリップが絡む切替（Keyframe↔Model / Keyframe↔Keyframe）は、フェード時間を指定しても常に即時切替になります（警告は出ません）。

- フェード中にさらに切替を呼ぶと、そのときの「現在クリップ」が新しいフェード元になり、ブレンド率は 0 から再開します（3 本以上を同時に混ぜることはありません）。
- `Play` で指定するクリップ名は、そのアクターの Animator に登録済み（`clips` 一覧に存在し、キーフレームクリップなら既にロード済み）である必要があります。未登録・未ロードの名前を指定すると警告ログを出して無視されます（例外は発生しません）。
- クリップは Play モード開始時（初回フレーム、スクリプトの `Update` 等より前）に自動ロードされるため、通常のスクリプトライフサイクル関数から呼ぶ限り「まだロードされていない」状況は発生しません。

**`.anim` キーフレームクリップでアニメーションできるプロパティ**（トラックの `component` / `property` の組。正典は Rust 側 `engine/animation/registry.rs` の `resolve_binding`、エディタのトラック追加 UI は `AnimPropertyRegistry.cs`）:

| component | property | value_type | 書き込み先 |
|---|---|---|---|
| `actor_transform` | `position` / `rotation` / `scale` | `vec3` | アクタールートの `Transform`（`rotation` は YXZ オイラー角・度） |
| `canvas_transform` | `position` / `scale` | `vec2` | アクタールートの `CanvasTransform` |
| `canvas_transform` | `rotation` | `float` | 同上（Z 回転・度） |
| `sprite` | `color` | `color` | 最初の Sprite スロットの `color`（RGBA 0〜1） |
| `text` | `color` | `color` | 最初の Text スロットの `color`（RGBA 0〜1） |
| `text` | `font_size` | `float` | 最初の Text スロットの `font_size`（キャンバスピクセル） |

トラックの `target.actor_path` は **Animator を持つアクターからの相対パス**（`/` 区切りの子アクタ名。空文字＝Animator 自身）です。親や兄弟へは遡れないため、複数の子をまとめて動かすクリップは**共通の親アクターに Animator を置いて**各子を名前で指します。

### ParticleEmitter（GPU パーティクル放出源）

エディタの「コンポーネント追加 → パーティクルエミッタ」で追加し、インスペクタで放出パラメータ（レート・寿命・色・ブレンドなど）を設定します。放出位置・向きは同じ GameObject の `transform` が決めます。

```csharp
if (gameObject.GetComponent<ParticleEmitter>() is { } ps)   // ParticleEmitter?（未アタッチは null）
{
    ps.Play();             // 放出を開始（playing = true）
    ps.Stop();             // 放出を停止（既存パーティクルは寿命で消える）
    ps.Burst(50);          // 50 個を即時一括放出（継続放出とは独立）
    ps.IsPlaying           // bool（get のみ。放出中か。Playing の別名）

    ps.Playing             // bool（get/set。放出中フラグ。Play()/Stop() と同じ切り替え）
    ps.EmitRate            // float（get/set。1 秒あたりの放出個数。負値は 0 にクランプ）
    ps.LoopEmit            // bool（get/set。寿命ループ放出するか）
    ps.Drag                // float（get/set。空気抵抗係数。負値は 0 にクランプ）
    ps.SpreadAngle         // float（get/set。放出円錐の半頂角・度。0〜180 にクランプ）
}
```

- `Burst(n)` の放出リクエストは蓄積され、次フレームで GPU パーティクルシステムが消費します（`emit_rate` による継続放出とは別枠）。`n` が 0 以下なら何もしません。

### InputMap（入力アクションマップ）

エディタの「コンポーネント追加 → 入力 → Input Map」で追加し、`.inputmap`（アクション名 → 物理入力のマッピング）を割り当てます。アクション名でアクション状態や軸値を取得します。**キーボード（Key）に加えてゲームパッド（GamepadButton / GamepadAxis）** を評価します（PC プラットフォーム）。

```csharp
if (gameObject.GetComponent<InputMap>() is { } input)   // InputMap?（未アタッチは null）
{
    input.GetAction("Jump")        // bool: 条件（Trigger/Press/Release）適用後の状態
    input.GetActionStart("Jump")   // bool: アクション成立の瞬間のフレームだけ
    input.GetActionEnd("Jump")     // bool: アクション終了の瞬間のフレームだけ
    input.GetAxis("Steer")         // float: Axis1D（[-1,1]。正/負バインドの合成）
    input.GetVector2("Move")       // Vector2: Axis2D（各 [-1,1]。x/y の正負合成）

    // 例: 入力マップで移動＋ジャンプ
    var move = input.GetVector2("Move");
    transform.Position += new SEED.Vector3(move.x, 0f, move.y) * 5f * SEED.Time.DeltaTime;
    if (input.GetActionStart("Jump")) { /* ジャンプ */ }
}
```

- **アクション条件（Bool）**: `.inputmap` の condition で `Trigger`（成立した瞬間）/`Press`（押下中・既定）/`Release`（離した瞬間）を選びます。`GetAction` はこの条件適用後の状態を返します。`GetActionStart`/`GetActionEnd` は条件適用後の値の立ち上がり/立ち下がりです。
- **軸（Axis1D / Axis2D）**: `正バインド − 負バインド` を合成して `[-1,1]` にクランプします。デジタル（Key/GamepadButton）は押下で 1.0、アナログ（GamepadAxis スティック）はデッドゾーン適用後の符号付き生値です。スティックは各軸の正バインドに `LeftStickX` 等を 1 件置けば両方向をカバーします。Axis2D は `normalize` を有効にすると長さ>1 のとき正規化され、斜めキーボードが 0.707 になります。
- **ゲームパッド**: GamepadButton は `South`/`East`/`West`/`North`・`DPadUp/Down/Left/Right`・`LeftShoulder`(LB)/`RightShoulder`(RB)・`LeftStickPress`(L3)/`RightStickPress`(R3)・`Start`/`Select`。GamepadAxis は `LeftStickX`/`LeftStickY`/`RightStickX`/`RightStickY`（-1..1）・`LeftTrigger`/`RightTrigger`（0..1）。GamepadAxis のみ `dead_zone`（既定 0.2）が有効です。接続パッドは最初の 1 台のみ対応します。
- **キー名**はエディタの選択肢（`Space` / `LeftShift` / `Q` / `Alpha0` / `Keypad0` / `UpArrow` …）に対応します。マッピング不能な名前は無反応（ロード時に警告 1 回）。
- `.inputmap` は初回アクセス時に読み込み・キャッシュされ、以降は **1 秒間隔の mtime 監視で自動再読込**されます（毎フレームの再読込はしません）。ファイルを編集・保存すれば約 1 秒以内に反映され、ランタイムの再起動は不要です。再読込時はアクションのエッジ検出履歴（Start/End・Trigger/Release）もリセットされます。
- **後方互換**: 旧 v1 形式（version 欠落・WASD バインディング）も読み込め、内部で自動的に v2 へ移行します（エディタの保存は常に v2）。
- **複数コンポーネントの索引例**: 同種を複数持つ場合は `gameObject.GetComponent<InputMap>(1)`（index）や `gameObject.GetComponent<InputMap>("Vehicle")`（スロット名）で選べます。

### WaterVolume / WaterLink（水位グラフ＝浸水・バルブ制御）

海・池・川を表す `WaterVolume` と、2 つの水域をつなぐ開口（扉・窓・穴・バルブ）を表す `WaterLink` です。エディタで水域に「水位シミュレーション」を有効にし、その間に `WaterLink` を置くと、Play 中に**水位差 × 開口面積 × 係数**で水が行き来します（連通ボリューム方式）。**バルブ開閉は `Openness` を書くだけ**です。

```csharp
// バルブを閉じる／開ける（0 = 全閉で水は 1 滴も通らない、1 = 全開）
if (gameObject.GetComponent<WaterLink>() is { } valve)
{
    valve.Openness         // float（get/set。0..1。0 = バルブ全閉）
    valve.OpeningWidth     // float（get/set。開口の幅 m）
    valve.OpeningHeight    // float（get/set。開口の高さ m）
    valve.OpeningBottom    // float（get/set。開口下端 Y。アクタ原点からの相対 m）
    valve.FlowCoefficient  // float（get/set。流量係数 1/s。大きいほど速く釣り合う）

    // 例: レバーを引いたらバルブ全閉
    if (input.GetActionStart("Interact")) valve.Openness = 0f;
}

// 水位を読んで判定する
if (gameObject.GetComponent<WaterVolume>() is { } water)
{
    water.WaterLevel     // float（get のみ。現在の水面 Y。ワールド絶対値）
    water.SurfaceHeight  // float（get/set。設定水位。Ocean=ワールド絶対 / Region=アクタ相対）
    water.SimulateLevel  // bool（get/set。水位グラフの対象にするか。Region のみ有効）

    // 例: 水位が 3m を超えたら脱出フラグ
    if (water.WaterLevel > 3f) { /* 脱出イベント */ }
}
```

水面シェーディングアセット（`.wgsl`）が `override` で宣言したパラメータは、名前を指定して読み書きできます（ゲーム内変数を見た目へ流し込む用途。例: ボス HP で毒沼の蛍光を変える）。

```csharp
if (gameObject.GetComponent<WaterVolume>() is { } water)
{
    water.SetShaderParam("glow_boost", 2.5f);                       // f32 パラメータへ書く
    water.SetShaderParam("glow_color", new Vector3(0.2f, 0.6f, 0.1f)); // vec3<f32>（色）へ書く
    water.GetShaderParamFloat("glow_boost");                        // float（未設定ならアセット既定値、宣言が無ければ 0）
    water.GetShaderParamVector3("glow_color");                      // Vector3（同上。宣言が無ければ (0,0,0)）

    // 例: ボス HP が減るほど毒沼が明るく光る（毎フレーム流し込む）
    float t = 1f - bossHp / bossHpMax;
    water.SetShaderParam("glow_boost", 0.5f + 3f * t);
}
```

> **重要**: `SetShaderParam` の名前はアセットの `override` 宣言の識別子です（インスペクタの行と同じもの）。Play 中の書き込みは**シーンへ焼き付きません**（Play 終了で Play 開始時点の値に戻ります）。恒久的な既定値はアセット側の初期値かインスペクタで設定してください。

> **重要**: `WaterVolume.WaterLevel` は**読み取り専用**です。直接代入できると体積保存が破れて水位グラフの前提が壊れるため、水を足す／抜く演出は `WaterLink.Openness` の開閉で表現します。`WaterLink` の接続先（volume_a / volume_b）も実行中は変更できません（インスペクタで設定します）。

### LineRenderer（3D の線を描く：釣り糸・ロープ・軌跡）

点列を結ぶ 1 本の線をワールド空間に描きます。太さは**ワールド単位（m）**で、線は常にカメラを向くリボンとして描かれます（遠いほど細く見えます）。点列は毎フレーム `SetPoints` で丸ごと差し替える使い方が基本です。

```csharp
if (gameObject.GetComponent<LineRenderer>() is { } line)
{
    line.Width       // float（get/set。線の太さ。ワールド単位 m。0 で非表示）
    line.Color       // Color（get/set。RGBA。アルファ < 1 で半透明）
    line.Visible     // bool（get/set。false でスロットを消さずに線だけ隠す）
    line.LocalSpace  // bool（get/set。true=点列はアクター Transform 基準 / false=ワールド座標）
    line.DepthTest   // bool（get/set。true=手前の物体に隠れる / false=常に最前面）
    line.PointCount  // int（get のみ。現在の点数）

    line.SetPoints(points);   // Vector3[] / ReadOnlySpan<Vector3> を丸ごと差し替え
    line.Clear();             // 点列を空にして線を消す
}
```

> **重要**: `SetPoints` に渡せる点数の上限は `LineRenderer.MaxPoints`（512）です。超えると `false` を返して何も変わりません。点列そのものは読み出せません（点数だけ `PointCount` で取れます）。

### Text（キャンバスに文字を表示：HUD の数値・ラベル）

キャンバス（Actor2D、または CanvasComponent を持つ Actor3D）の配下に置いたアクターへ文字列を表示します。位置・回転・スケール・アンカーは `CanvasTransform` が決め、Sprite とまったく同じ変換で描かれます。`Content` の代入は文字列を差し替えるだけなので、毎フレーム呼んで構いません。

```csharp
if (gameObject.GetComponent<Text>() is { } label)
{
    label.Content        // string（get/set。表示文字列。"\n" で改行）
    label.FontSize       // float（get/set。キャンバスピクセル）
    label.Color          // Color（get/set。RGBA。アルファ 0 で非表示）
    label.LineSpacing    // float（get/set。行送り = フォントサイズ × この倍率）
    label.Layer          // int（get/set。大きいほど手前。Sprite と共通の順序）
    label.Align          // string（get/set。"left" / "center" / "right"）
    label.VerticalAlign  // string（get/set。"top" / "middle" / "bottom"）
    label.FontPath       // string（get/set。assets:// 仮想パス。空文字=組み込みフォント）
    label.OutlineWidth   // float（get/set。縁取りの太さ px。0=縁取りなし）
    label.OutlineColor   // Color（get/set。縁取りの色。既定=不透明な黒）
}
```

```csharp
// 例: 所持金の表示を毎フレーム更新する
public void Update()
{
    if (gameObject.GetComponent<Text>() is { } label)
        label.Content = $"所持金: {SaveData.GetInt("money")} 円";
}
```

> **重要**: `Align` / `VerticalAlign` に未知の文字列を代入しても無視され、既存の値が保たれます（typo で表示が崩れません）。1 つの Text が描ける文字数の上限は 4096 文字で、超えた分は切り捨てられます。縁取りの太さはフォントサイズの約 1/8 が上限で、それを超える指定は上限で頭打ちになります（SDF のスプレッド幅による）。

### Skybox（天球の色調整：時間帯・天候の演出）

equirectangular 画像 1 枚を天球として描く `Skybox` コンポーネントを、実行時に読み書きします。
**色調整（色相シフト／彩度／明度／コントラスト）は、背景の空だけでなく、鏡面反射・水面反射に映る空へも同時に効きます**。

```csharp
if (gameObject.GetComponent<Skybox>() is { } sky)
{
    sky.TexturePath = "assets://sky/dusk.hdr"; // equirectangular 画像（assets:// 仮想パス）
    sky.Intensity   = 1.5f;                    // 強度（1 = 素の色。1 超で Bloom と連動）
    sky.Tint        = new Vector3(1f, 0.9f, 0.8f); // 色味（リニア RGB 乗算）

    sky.HueShift    = -30f;  // 色相シフト（度。-180〜180。0 = 無変換）
    sky.Saturation  = 1.3f;  // 彩度（0〜2。0 = グレースケール / 1 = 無変換）
    sky.Brightness  = 0.8f;  // 明度（0〜2。色への乗算。1 = 無変換）
    sky.Contrast    = 1.2f;  // コントラスト（0〜2。中間グレー基準。1 = 無変換）
}
```

#### レシピ: 時間帯に応じて夕焼けへ寄せる

```csharp
public override void Update(ref NativeFrameContext ctx)
{
    if (gameObject.GetComponent<SEED.Skybox>() is not { } sky) return;

    // 0（昼）→ 1（夕方）へ進む係数
    float t = SEED.Mathf.Clamp01(dayProgress);
    sky.HueShift   = SEED.Mathf.Lerp(0f, -35f, t);  // 青 → 橙へ色相をずらす
    sky.Saturation = SEED.Mathf.Lerp(1f, 1.4f, t);  // 夕焼けは彩度を上げる
    sky.Brightness = SEED.Mathf.Lerp(1f, 0.7f, t);  // 全体を落とす
}
```

> **重要**: 色調整の値域はエンジン側でクランプされます（色相 -180〜180 度、彩度／明度／コントラスト 0〜2）。既定値（0 / 1 / 1 / 1）では計算そのものが飛ばされ、調整なしと**完全に同じ**出力になります。

### ControlPointPath（コントロールポイント経路：巡回・レール移動）

シーンに置いた**コントロールポイント列**（順序付きの点列）を、時刻を与えて評価します。補間・ワールド変換・閉ループの周回はすべてエンジンが行うため、**ビューポートに見えている線とまったく同じ経路**を辿ります。読み取り専用（点列の編集はエディタで行う）です。

```csharp
if (gameObject.GetComponent<ControlPointPath>() is { } path)
{
    path.PointCount              // int（get。制御点の数）
    path.Closed                  // bool（get。閉ループか。点が 2 個未満なら false）
    path.Duration                // float（get。1 周ぶんの所要時間・秒）
    path.StartTime               // float（get。先頭の制御点の time＝時刻の原点）

    path.SamplePosition(t)       // Vector3（ワールド位置。閉ループは周回／開経路は両端クランプ）
    path.SampleTangent(t)        // Vector3（進行方向の単位ベクトル・ワールド。定まらなければ Zero）
}
```

> **重要**: 座標はすべて**ワールド空間**です（制御点はアクタ相対で保存されますが、取得時にそのアクタの Transform が合成済み）。時刻の単位・原点は制御点の `time` に従います（既定は「1 点 = 1 秒」）。閉ループでは時刻が `Duration` を周期に**周回**するので、時刻を増やし続けるだけでぐるぐる回れます。開いた経路は両端でクランプされ、経路の外へは出ません。

#### レシピ: 閉ループ経路を進行方向を向きながら移動する

別アクタに置いた経路を `[SerializeField]` で参照し、入力で経路上の時刻を進める／戻すだけで「レールに沿った移動」になります。向きは接線から求めた目標ヨーへ、最短回りで緩やかに補間します。

```csharp
using SEEDEditor.Scripting;

public class RailMove : SEEDScript
{
    [SerializeField(Label = "経路")]      private SEED.ControlPointPath? path = null;
    [SerializeField(Label = "移動速度")]  private float pathSpeed = 1.0f;
    [SerializeField(Label = "回転補間率")] private float turnLerpRate = 10.0f;

    /// <summary>経路上の現在時刻（秒）。閉ループならエンジン側で周回する。</summary>
    private float pathTime = 0f;

    public override void Update(ref NativeFrameContext ctx)
    {
        if (path is not { } p || !p.IsValid) return;
        if (gameObject.GetComponent<SEED.InputMap>() is not { } im) return;

        float axis = im.GetVector2("Move").y;              // 前後入力で経路上を進む／戻る
        pathTime += axis * pathSpeed * ctx.DeltaTime;
        transform.Position = p.SamplePosition(pathTime);

        // 進行方向（逆走時は符号反転）から目標ヨーを作り、最短回りで補間する
        var tangent = p.SampleTangent(pathTime);
        if (tangent.SqrMagnitude > 1e-6f && SEED.Mathf.Abs(axis) > 1e-3f)
        {
            var dir = axis < 0f ? tangent * -1f : tangent;
            float targetYaw = SEED.Mathf.Atan2(dir.x, dir.z) * SEED.Mathf.Rad2Deg;
            float yaw = transform.Rotation.y;
            float delta = SEED.Mathf.Repeat(targetYaw - yaw + 180f, 360f) - 180f;   // 最短回り
            yaw += delta * SEED.Mathf.Clamped01(turnLerpRate * ctx.DeltaTime);
            transform.Rotation = new SEED.Vector3(transform.Rotation.x, yaw, transform.Rotation.z);
        }
    }
}
```

### LineHelper（線の点列を作る補助・純 C#）

`LineRenderer.SetPoints` に渡す点列を組み立てる静的ヘルパーです。エンジンへのアクセスを伴わないので、どこからでも呼べます。

```csharp
// 始点→終点を結ぶ、下向きにたわんだカテナリー（懸垂線）状の点列
Vector3[] pts = LineHelper.Catenary(start, end, slack, segments);
//   start    : 始点（ワールド座標。例: 竿先）
//   end      : 終点（ワールド座標。例: ウキ）
//   slack    : 中央でのたわみ量（m）。0 で直線、大きいほど深く垂れる
//   segments : 分割数。返る点数は segments + 1（端点は start / end と厳密に一致）
```

#### レシピ: 竿先 → ウキの釣り糸を描く

竿先アクターに `LineRenderer` を付け、`LocalSpace = false`（ワールド座標）にしてから、毎フレーム竿先とウキのワールド位置を結びます。糸の張り具合を距離から決めると、引き寄せたときに自然にたわみます。

```csharp
using SEEDEditor.Scripting;

public class FishingLine : SEEDScript
{
    // 竿先とウキを参照フィールドで差す（Hierarchy からドロップ）
    [SerializeField(Label = "竿先")] private SEED.Transform rodTip;
    [SerializeField(Label = "ウキ")] private SEED.Transform bobber;

    /// <summary>糸の全長（m）。これより両端が近ければ余った長さがたわみになる。</summary>
    private const float LineLength = 6f;
    /// <summary>糸の分割数（多いほど滑らか）。</summary>
    private const int Segments = 24;
    /// <summary>余り長さのうちどれだけをたわみ深さにするかの係数。</summary>
    private const float SlackRatio = 0.5f;

    public override void OnStart()
    {
        if (gameObject.GetComponent<SEED.LineRenderer>() is { } line)
        {
            line.LocalSpace = false;                                  // 点列をワールド座標で渡す
            line.Width      = 0.015f;                                 // 釣り糸の太さ（m）
            line.Color      = new SEED.Color(0.9f, 0.9f, 0.85f, 0.8f);
        }
    }

    public override void Update(ref NativeFrameContext ctx)
    {
        if (gameObject.GetComponent<SEED.LineRenderer>() is not { } line) return;
        // 竿先かウキが未設定・破棄済みなら糸を消す
        if (!rodTip.IsValid || !bobber.IsValid) { line.Clear(); return; }

        var a = rodTip.Position;
        var b = bobber.Position;

        // 余った糸の長さぶんだけたわませる（張りきったら直線になる）
        float dist  = SEED.Vector3.Distance(a, b);
        float slack = SEED.Mathf.Max(0f, LineLength - dist) * SlackRatio;

        line.SetPoints(SEED.LineHelper.Catenary(a, b, slack, Segments));
    }
}
```

### 利用可能なコンポーネント一覧

| コンポーネント名 | 取得 | 内容 |
|---|---|---|
| `Transform` | `gameObject.GetComponent<Transform>()` / `transform` | 3D 位置・回転・スケール |
| `CanvasTransform` | `gameObject.GetComponent<CanvasTransform>()` | 2D キャンバス上の位置・回転・スケール・ピボット・アンカー |
| `Model` | `gameObject.GetComponent<Model>()` | 3D モデルの表示切替（`Visible`）と描画オフセット（位置・回転・スケール）。描画のみで物理・追従には影響しない |
| `Sprite` | `gameObject.GetComponent<Sprite>()` | テクスチャパス・色・サイズ・レイヤー・ポインタ判定対象（RaycastTarget） |
| `SkinnedSprite` | `gameObject.GetComponent<SkinnedSprite>()` | メッシュパス（.sprite_mesh）・テクスチャパス・色・レイヤー・ポインタ判定対象。ボーンは子アクターの CanvasTransform で動かす |
| `Camera` | `gameObject.GetComponent<Camera>()` | FOV・クリップ距離・メインカメラ・クリアカラー・ベース解像度 |
| `AudioSource` | `gameObject.GetComponent<AudioSource>()` | 音源パス・音量・ループ・3D 減衰・パン + Play/Stop |
| `Animator` | `gameObject.GetComponent<Animator>()` | 再生中クリップ・再生位置・速度・フェード + Play/CrossFade/Stop/Pause/Resume |
| `ParticleEmitter` | `gameObject.GetComponent<ParticleEmitter>()` | 放出レート・ループ・抵抗・拡散角 + Play/Stop/Burst |
| `InputMap` | `gameObject.GetComponent<InputMap>()` | 入力アクション評価（Bool / Axis1D / Axis2D。Key / GamepadButton / GamepadAxis） |
| `WaterVolume` | `gameObject.GetComponent<WaterVolume>()` | 現在水位（読み取り専用）・設定水位・水位シミュレーションの有効／無効・水面シェーダのパラメータ（SetShaderParam / GetShaderParamFloat / GetShaderParamVector3） |
| `WaterLink` | `gameObject.GetComponent<WaterLink>()` | 水位グラフの開口。**開閉率（バルブ）**・開口寸法・流量係数 |
| `LineRenderer` | `gameObject.GetComponent<LineRenderer>()` | 3D の線（釣り糸・ロープ・軌跡）。点列（SetPoints）・太さ・色・表示・座標系・深度テスト |
| `Text` | `gameObject.GetComponent<Text>()` | キャンバス上の文字表示（HUD の数値・ラベル）。内容・フォント（assets:// の .otf/.ttf）・サイズ・色・**縁取り**（太さ・色）・整列・行送り・レイヤー |
| `Skybox` | `gameObject.GetComponent<Skybox>()` | 天球（equirectangular）のテクスチャパス・強度・色味と、**色調整**（色相シフト・彩度・明度・コントラスト）。調整は背景・反射・水面反射の空すべてに効く |
| `ControlPointPath` | `gameObject.GetComponent<ControlPointPath>()` | コントロールポイント経路（巡回・レール移動）。点数・閉ループ・1 周時間と、開始時刻と、時刻指定のワールド位置／進行方向サンプル（読み取り専用） |

> **重要**: `GetComponent<T>()` は `T?` を返し、同種コンポーネントを複数スロット持てます。`GetComponent<T>()`＝0 番目、`GetComponent<T>(index)`＝index 番目、`GetComponent<T>("Name")`＝スロット名一致。

他のコンポーネント（Collider / Rigidbody など物理系）は物理 API として順次対応予定で、対応済みのものは本節に追記されます。

### 参照フィールド（インスペクタで他アクターを差し込む）

`[SerializeField]` を **`GameObject` やコンポーネントハンドル型**のフィールドに付けると、インスペクタ上で**他のアクターへの参照**として編集できます。Unity の「オブジェクト参照フィールド」に相当します。

```csharp
using SEEDEditor.Scripting;

public class FollowCamera : SEEDScript
{
    // 追従したい対象。Hierarchy からアクタ行をドロップして設定する
    [SerializeField(Label = "追従対象")]
    private SEED.Transform target;

    // 未設定を許したい参照は Nullable で宣言する（null = 未設定）
    [SerializeField(Label = "注視カメラ")]
    private SEED.Camera? lookCamera;

    [SerializeField] private SEED.Vector3 offset = new(0f, 3f, -8f);

    public override void LateUpdate()
    {
        // 非 Nullable 宣言は「未設定でも null にはならない」ので IsValid で確かめる
        if (!target.IsValid) return;

        transform.Position = target.Position + offset;

        // Nullable 宣言は null チェック → さらに IsValid で生存確認
        if (lookCamera is { } cam && cam.IsValid)
            cam.FieldOfView = 60f;
    }
}
```

**指定できる型**

| 宣言 | 意味 |
|---|---|
| `SEED.GameObject` / `SEED.GameObject?` | アクター本体への参照 |
| `SEED.Transform` / `SEED.CanvasTransform`（＋ `?`） | アクターのルートに直付けされた Transform 系への参照 |
| `SEED.Sprite` / `SEED.Camera` / `SEED.AudioSource` / `SEED.Animator` / `SEED.ParticleEmitter` / `SEED.InputMap` / `SEED.LineRenderer`（＋ `?`） | アクター内の**コンポーネントスロット**への参照 |
| `MyScript` / `MyScript?`（任意の自作スクリプト） | 参照先アクターに付いている**そのスクリプトの実インスタンス**への参照（フィールド・メソッドを直接呼べる） |

**自作スクリプトへの参照**

```csharp
using SEEDEditor.Scripting;

public class CameraMove : SEEDScript
{
    // 参照先アクターに付いている PlayerMove の「生きているインスタンス」が注入される。
    // 解決できないときは（? の有無にかかわらず）null なので必ず null チェックする。
    [SerializeField(Label = "プレイヤー")]
    private PlayerMove? player;

    public override void Update()
    {
        if (player is null) return;      // 未設定・参照先破棄・解決失敗
        var state = player.State;        // public フィールド／プロパティ／メソッドを直接使える
    }
}
```

> **重要**: 自作スクリプトへの参照はハンドル構造体ではなく**実インスタンス（class）**なので、`IsValid` は**ありません**。解決できないとき（未設定・アクター不在・そのスクリプトが付いていない・破棄済み）は `T` 宣言でも `T?` 宣言でも **必ず `null`** になるため、**毎回 null チェックが必須**です。参照先スクリプトの `OnStart` が自分より先に走っている保証は**ありません**（初期化済みの値を読むのは `Update` 以降にしてください）。インスタンスは**ホットリロードのたびに作り直されて再注入**されるので、**別のフィールドへキャッシュしてはいけません**（古いインスタンスを掴み続けます）。同じスクリプトが 1 アクターに複数付いている場合はインスペクタのスロット選択ダイアログで指定できます（未指定なら先頭のスロット）。

> **重要**: 参照フィールドは**常に参照（ハンドル）**です（「値としての Transform」は作れません。値で持つなら `SEED.Vector3`）。**`null` は「未設定」のみ**を意味し Nullable（`T?`）宣言でしか起きません。**`IsValid` は「参照先が生きているか」**で、未解決・破棄済みのどちらでも `false` です。非 Nullable（`T`）宣言は未設定でも null にならず `IsValid == false` の無効ハンドルになります。参照は**アクタ名（＋スロット名）**で保存されますが、エディタでアクタをリネームすると**旧名一致の参照は自動で新名に追従**します（同名アクタが複数ある場合は旧名一致の参照がすべて書き換わる点に注意。コンパイルエラー中のスクリプトの参照は型判定できないため追従しません）。解決は Play 開始時／Instantiate 時に **`OnStart` より前の一度きり**です。

**`null` と `IsValid` の使い分け（重要）**

- **`null` は「未設定」だけを意味します**。Nullable（`T?`）で宣言したフィールドのみ null になり得ます。
- **`IsValid` は「参照先が今も生きているか」**を意味します。未解決（アクタが見つからない）・破棄済みのどちらでも `false` になります。
- 非 Nullable（`T`）で宣言した参照は未設定でも null にならず、**`IsValid == false` の無効ハンドル**になります。
- `IsValid` はライフサイクル関数の中でのみ意味のある値を返します（コンストラクタ等、エンジンの実行フェーズ外では常に `false`）。

**インスペクタでの設定方法**

1. スクリプトのフィールド行にある参照ボックスへ、**Hierarchy パネルからアクタ行をドラッグ＆ドロップ**します。
2. ドロップしたアクターがその種別のスロットを**複数持つ**場合は、スロット選択ダイアログが出ます。
3. `✕` ボタンで参照を解除します（未設定に戻ります）。
4. 参照ボックスを**ダブルクリック**すると、Hierarchy の参照先アクタへジャンプします。
5. 参照先がその種別を持っていない場合は警告が出て設定されません。

**保存形式と制約**

- 参照は**アクタ名**（コンポーネント参照は加えて**スロット名**）で保存されます（`Player` / `Player|MainCamera`）。未設定は空文字列です。
- したがって **アクタ名やスロット名を変更すると参照は切れます**（再設定が必要）。アクタ名に `|` は使えません。
- 同名アクタが複数ある場合は、ヒエラルキーの DFS 順で**最初に見つかったもの**が使われます。
- **解決は一度きり**です。Play 開始時（および `Instantiate` されたアクターの生成時）に、そのスクリプトの **`OnStart` より前**に解決・注入されます。実行中のアクタ名変更やアクタ生成には追従しません。実行中に相手を探し直したい場合は `SEED.GameObject.Find(name)` を使ってください。
- 解決に失敗した場合、Nullable 宣言なら `null`、非 Nullable 宣言なら `IsValid == false` の無効ハンドルになります（例外にはなりません）。

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

## 7.6 Profiler（任意区間の時間計測）

エディタの「プロファイラ」パネルへ、自分のスクリプトの任意区間を計測項目として出せます。
計測結果は**そのとき実行中のセクションの子**として階層に現れます（例: `スクリプト > Update > 敵の索敵`）。

```csharp
public void Update()
{
    // 推奨: using で自動終了（例外で抜けても確実に閉じる）
    using (SEED.Profiler.Scope("敵の索敵"))
    {
        SearchEnemies();
    }

    // 明示的に書く場合は Begin と End を必ず対にする
    SEED.Profiler.Begin("経路探索");
    SolvePath();
    SEED.Profiler.End();
}
```

- **計測されるのはエディタの「プロファイラ」パネルを開いている間だけ**です。閉じている間は
  `Begin` / `End` ともにほぼゼロコストで `false` を返します（文字列変換すら行いません）。
- 戻り値は「計測されたか」であり、処理の成否ではありません。
- **名前は固定文字列にしてください。** 名前の種類には上限（256）があり、`Begin($"敵{i}")` のように
  ループ変数を埋め込むと上限に達し、それ以降の名前が計測されなくなります。
- `Begin` と `End` の対応が崩れてもエンジン側の計測は壊れません（`End` は自分が開いた区間しか閉じず、
  閉じ忘れは親区間の終了時にまとめて閉じられます）。
- 詳細（パネルの見方・計測方式・オーバーヘッド）は **docs/profiler.md** を参照してください。

---

## 7.7 SaveData（セーブデータ：ゲーム進行の永続化）

資金・強化レベル・図鑑・ハイスコアのように「シーンを切り替えても、ゲームを終了して起動し直しても残したい値」をキー・バリューで保存します。実体は Rust ランタイムが持つ JSON 1 ファイルで、スクリプト側はファイルパスもファイル IO も意識しません。

```csharp
// 書き込み（メモリ上のストアへ。毎フレーム呼んでも安い）
SaveData.SetInt("money", 1200);
SaveData.SetLong("total_score", 12345678901);   // int に収まらない値用
SaveData.SetFloat("best_size_bass", 41.5f);
SaveData.SetString("player_name", "kani");
SaveData.SetBool("tutorial_done", true);

// 読み取り（キーが無い・型が合わない場合は既定値）
int    money = SaveData.GetInt("money", 0);
long   score = SaveData.GetLong("total_score", 0);
float  best  = SaveData.GetFloat("best_size_bass", 0f);
string name  = SaveData.GetString("player_name", "no name");
bool   done  = SaveData.GetBool("tutorial_done", false);

// 問い合わせ・削除
SaveData.Has("money");        // bool（型は問わずキーの有無）
SaveData.DeleteKey("money");  // bool（削除した=true / 元から無かった=false）
SaveData.DeleteAll();         // ニューゲーム用（全キー削除）

// ディスクへ書き出す（明示保存）
SaveData.Save();              // bool（成功=true）
```

```csharp
// 例: 魚を釣った瞬間に図鑑と資金を更新して保存する
void OnCatch(string fishId, float sizeCm, int price)
{
    // その魚の最大サイズを更新（初回は 0 と比較される）
    string key = $"best_{fishId}";
    if (sizeCm > SaveData.GetFloat(key, 0f)) SaveData.SetFloat(key, sizeCm);

    SaveData.SetInt("money", SaveData.GetInt("money", 0) + price);
    SaveData.Save();   // 区切りの良いところで明示保存する
}
```

> **重要**: `Set*` はメモリ上のストアを書き換えるだけです。ディスクへ書き出すのは `Save()` を呼んだときと、Play 終了時・アプリ終了時の自動保存だけなので、進行の区切り（魚を釣った直後・購入した直後）で `Save()` を呼んでください。

> **重要**: 整数と実数は相互に読み替えられます（実数 → 整数は 0 方向へ切り捨て）。文字列と数値は**相互変換しません** — 型を間違えた読み取りは既定値を返すので、書いたときと同じ型で読んでください。

| 保存先 | パス |
|---|---|
| パッケージ実行（配布ビルド） | 実行ファイルと同じ階層の `save/save.json` |
| エディタから Play | `runtime/save/save.json`（Git 追跡外） |
| 環境変数 `SEED_SAVE_DIR` 指定時 | そのディレクトリの `save.json`（最優先） |

> **重要**: セーブデータは Play を終了して Edit へ戻しても**巻き戻りません**（ゲームの進行であってシーンの編集データではないため）。テストで初期状態へ戻したいときは `SaveData.DeleteAll()` + `SaveData.Save()` を呼ぶか、保存先の `save.json` を削除してください。

---

## 7.8 Draw（2D プリミティブ描画：ゲージ・レーダー・図形 UI）

`SEED.Draw` は**イミディエイトモード**の 2D 図形描画 API です。Update などから**毎フレーム呼ぶ**と、そのフレームだけ図形が描かれます（オブジェクトは作られず、フレーム終了時にコマンドは破棄されます）。Unity の Gizmos / Debug.DrawLine に似ていますが、こちらはデバッグ用ではなく**ゲーム本編の UI として使える描画物**です。

```csharp
// スクリーンスペース（左上原点・1 単位 = 1px・Y 下向き）へ直接描く
Draw.Line(new Vector2(100, 100), new Vector2(300, 180), new Color(1, 1, 1, 1), thickness: 2f);
Draw.Circle(new Vector2(200, 200), 40f, new Color(0.2f, 0.8f, 1f, 0.6f));
Draw.Rect(new Vector2(200, 400), new Vector2(160, 24), new Color(0, 0, 0, 0.5f));
```

### 座標空間（`space` 引数）

```csharp
// space を省略（null）= スクリーンスペース。左上が (0,0)、Y は下向き、単位は px。
Draw.Circle(new Vector2(64, 64), 20f, color);

// space に CanvasTransform を渡す = そのアクターのローカル空間。
// アンカー・ピボット・親子スケール・自動解像度が「そのアクターの子スプライト」と
// まったく同じ規則で効く（＝その位置に子として置いたスプライトと同じ座標系）。
if (gameObject.GetComponent<CanvasTransform>() is { } ct)
{
    Draw.Circle(new Vector2(0, 0), 20f, color, space: ct);   // このアクターの原点に円
}

// 3D ワールドキャンバス（3D 空間に置いた Canvas）配下のノードを渡すと、
// そのキャンバス平面上へワールド空間で描かれる（3D シーンに正しく隠れる）。
Draw.Ring(center, 30f, 40f, color, space: worldCanvasNode);
```

> **重要**: `space` に渡したアクターがそのフレームに描画されない（非アクティブ・別世界線・CanvasTransform を持たない）場合、その図形は**黙って描画されません**。

### レイヤーと重なり順

```csharp
Draw.Rect(center, size, bgColor, layer: 10);      // 奥
Draw.Ring(center, 30, 40, fgColor, layer: 20);    // 手前
```

`layer` はスプライト（`Sprite.Layer`）・テキスト（`Text.Layer`）と**同じソート軸**で、大きいほど手前です。同じレイヤー値のときの前後は「**スプライト → プリミティブ → テキスト**」の順になります（テキストは常にプリミティブより手前）。描画ゾーン（Canvas の背景／前面）は `space` に渡したキャンバスのゾーンを継承し、スクリーンスペース（`space: null`）は前面ゾーン扱いです。

### 図形一覧

```csharp
// 四角形（4 点指定 / 中心+サイズの簡易版）
Draw.Rect(p0, p1, p2, p3, Transform2D.Identity, color, DrawMode.Fill, thickness: 1f, layer: 0, space: null);
Draw.Rect(center, size, color, DrawMode.Outline, thickness: 2f);

// 三角形
Draw.Triangle(a, b, c, Transform2D.Identity, color, DrawMode.Fill);

// 直線
Draw.Line(a, b, color, thickness: 2f, layer: 0, space: null);

// 円・楕円（scale で楕円になる）
Draw.Circle(center, radius, color, scale: null, DrawMode.Fill, thickness: 1f, layer: 0, space: null);
Draw.Circle(center, 30f, color, scale: new Vector2(2f, 1f));   // 横長の楕円

// 正多角形（rotationDegrees は時計回り）
Draw.RegularPolygon(center, radius, vertices: 6, color, rotationDegrees: 0f, scale: null, DrawMode.Fill);

// 円弧（Fill = 太さ thickness のリング / Outline = 太さ thickness の線）
Draw.Arc(center, radius, startDegrees, endDegrees, color, DrawMode.Fill, thickness: 8f);

// リング（内半径・外半径で指定。innerRadius = 0 なら扇形）
Draw.Ring(center, innerRadius, outerRadius, color, startDegrees: 0f, endDegrees: 360f, DrawMode.Fill);

// 角丸四角形
Draw.RoundedRect(center, size, cornerRadius, Transform2D.Identity, color, DrawMode.Fill);
Draw.RoundedRect(p0, p1, p2, p3, cornerRadius, Transform2D.Identity, color, DrawMode.Fill);

// 折れ線（closed = true で閉じる）
Draw.Polyline(points, closed: false, color, thickness: 2f, layer: 0, space: null);

// 多角形（塗りは耳刈りで三角形分割。凹多角形も可）
Draw.Polygon(points, Transform2D.Identity, color, DrawMode.Fill, thickness: 1f, layer: 0, space: null);

// 3 次ベジエ曲線（線のみ）
Draw.Bezier(p0, p1, p2, p3, color, segments: 32, thickness: 2f);
```

```csharp
// DrawMode: 塗り or 輪郭
DrawMode.Fill      // 内側を塗る（既定）
DrawMode.Outline   // 輪郭を太さ thickness の線で描く

// Transform2D: 点列へ「スケール → 回転 → 平行移動」の順で適用する SRT
Transform2D.Identity                       // 何もしない
new Transform2D(position)                  // 平行移動のみ
new Transform2D(position, rotationDegrees)             // + 回転（時計回りが正）
new Transform2D(position, rotationDegrees, scale)      // + スケール
```

### 例: レーダーに点を打つ

```csharp
// 画面右上のレーダー（半径 60px）に、周囲の敵を点で表示する
void DrawRadar(Vector2 radarCenter, Vector2 playerPos, Vector2[] enemies)
{
    var frame = new Color(0.2f, 1f, 0.6f, 0.8f);
    Draw.Circle(radarCenter, 60f, new Color(0f, 0f, 0f, 0.4f), layer: 10);
    Draw.Circle(radarCenter, 60f, frame, mode: DrawMode.Outline, thickness: 2f, layer: 11);

    const float WorldPerPixel = 4f;    // レーダー 1px が表すワールド距離
    foreach (var e in enemies)
    {
        var d = new Vector2((e.x - playerPos.x) / WorldPerPixel,
                            (e.y - playerPos.y) / WorldPerPixel);
        if (Mathf.Sqrt(d.x * d.x + d.y * d.y) > 60f) continue;   // 範囲外は描かない
        Draw.Circle(new Vector2(radarCenter.x + d.x, radarCenter.y + d.y), 3f,
            new Color(1f, 0.3f, 0.3f, 1f), layer: 12);
    }
}
```

### 例: 円形のゲージ（テンションゲージ・クールダウン）

```csharp
// value（0..1）に応じてリングを伸ばす。12 時方向から時計回りに満ちる。
void DrawRingGauge(Vector2 center, float value)
{
    const float StartDegrees = -90f;      // 12 時方向（Y 下向きなので -90）
    const float FullSweep    = 360f;
    var bg   = new Color(0f, 0f, 0f, 0.5f);
    var fill = new Color(1f, 0.8f, 0.2f, 1f);

    Draw.Ring(center, 34f, 42f, bg, 0f, FullSweep, layer: 20);
    Draw.Ring(center, 34f, 42f, fill,
        StartDegrees, StartDegrees + FullSweep * Mathf.Clamped01(value), layer: 21);
}
```

### 制限

| 項目 | 上限・制限 |
|---|---|
| 1 フレームの図形数 | 4096（`Draw.MaxPrimitivesPerFrame`）。超過分は描かれず警告ログが出る |
| 1 図形の点数 | 1024（`Draw.MaxPointsPerPrimitive`）。超過分は切り捨て |
| `Polygon` の形状 | 自己交差しない単純多角形のみ（凹は可）。穴あき・自己交差は結果が保証されない |
| 半透明の太い折れ線 | 角（ジョイント）がわずかに濃くなる（帯が重なるため）。不透明色では見えない |
| アンチエイリアス | 輪郭の外側 1px のフェザー帯による近似（スクリーンスペースでは 1 画面 px） |

> **重要**: `Draw.*` は「呼んだフレームだけ描く」API です。図形を出し続けたいなら毎フレーム呼んでください。Play していないフレームに積まれたコマンドは破棄されます。

---

## 7.9 Draw3D（3D プリミティブ描画：ワールド空間の線・図形）

`SEED.Draw3D` は**ワールド空間**のイミディエイトモード 3D 図形描画 API です。`SEED.Draw`（2D）と同じく Update などから**毎フレーム呼ぶ**と、そのフレームだけ図形が描かれます（オブジェクトは作られません）。釣り糸のたるみ・水面の距離リング・索敵範囲のワイヤ球など、デバッグ表示にも**ゲーム本編の表現にも**使えます。

```csharp
// ワールド空間に線を 1 本引く（太さは画面 px なので距離で細くならない）
Draw3D.Line(rodTip, hookPos, new Color(1f, 1f, 1f, 0.8f), thicknessPx: 2f);

// 索敵範囲のワイヤ球（3D シーンに隠れないデバッグ表示）
Draw3D.WireSphere(transform.Position, 12f, Color.Green, thicknessPx: 1f, depthTest: false);
```

### 2D 版（`SEED.Draw`）との違い

| | `SEED.Draw`（2D） | `SEED.Draw3D` |
|---|---|---|
| 座標 | スクリーン px / キャンバスローカル | **ワールド空間（Vector3）** |
| 太さ | 描画空間の単位（px） | **画面 px。カメラ距離に依らず一定** |
| 前後関係 | `layer`（スプライト・テキストと同じ軸） | `depthTest` と**呼び出し順**（レイヤーなし） |
| 描画位置 | UI（2D キャンバス／スクリーン） | 半透明・3D キャンバススプライトの後、2D UI の前 |

### 深度テスト（`depthTest` 引数）

```csharp
Draw3D.Line(a, b, color);                        // 既定 true = 3D シーンに正しく隠れる
Draw3D.Line(a, b, color, depthTest: false);      // 常に手前（デバッグ表示向け）
```

同じ `depthTest` 内の重なりは**呼び出した順**（後に呼んだものが上）です。深度テストありの図形はすべて、深度テストなしの図形より先に描かれます。

### 平面の指定（`normal` 引数）

`Circle` / `Ring` / `Arc` は「どの平面に乗せるか」を法線で指定します。角度 0 度は法線から自動で決まる基準軸の方向で、角度は右ねじ方向へ増加します。水面へ寝かせたいときは法線に `Vector3.Up` を渡します。

```csharp
Draw3D.Circle(center, Vector3.Up, radius: 5f, color);          // 水平（水面）の円
Draw3D.Circle(center, Vector3.Forward, radius: 5f, color);     // 画面奥向きの平面の円
```

### 図形一覧

```csharp
// 線
Draw3D.Line(a, b, color, thicknessPx: 1f, depthTest: true);
Draw3D.Polyline(points, closed: false, color, thicknessPx: 1f, depthTest: true);

// 円・弧・リング（normal でどの平面に乗せるか決める）
Draw3D.Circle(center, normal, radius, color, DrawMode.Outline, thicknessPx: 1f, segments: 48, depthTest: true);
Draw3D.Arc(center, normal, radius, startDegrees, endDegrees, color, thicknessPx: 1f, segments: 48, depthTest: true);
Draw3D.Ring(center, normal, innerRadius, outerRadius, color, startDegrees: 0f, endDegrees: 360f, segments: 48, depthTest: true);

// ワイヤフレーム
Draw3D.WireSphere(center, radius, color, thicknessPx: 1f, segments: 48, depthTest: true);
Draw3D.WireBox(center, size, rotationEulerDegrees, color, thicknessPx: 1f, depthTest: true);
Draw3D.WireCapsule(p0, p1, radius, color, thicknessPx: 1f, segments: 16, depthTest: true);

// 面（塗りは両面・アンリット）
Draw3D.Triangle(a, b, c, color, DrawMode.Fill, thicknessPx: 1f, depthTest: true);
Draw3D.Quad(a, b, c, d, color, DrawMode.Fill, thicknessPx: 1f, depthTest: true);

// 矢印（軸は線・矢尻は塗りの円錐）・点（常に画面を向く正方形）
Draw3D.Arrow(from, to, color, headLength, headRadius, thicknessPx: 1f, segments: 16, depthTest: true);
Draw3D.Point(p, sizePx: 6f, color, depthTest: true);
```

```csharp
// DrawMode は 2D 版と共通
DrawMode.Fill      // 内側を塗る（Circle / Triangle / Quad）
DrawMode.Outline   // 輪郭を太さ thicknessPx の線で描く
```

### 例: 釣り糸のたるみを折れ線で描く

```csharp
// 竿先から浮きまでを、たるみ（重力方向のサグ）付きの折れ線で描く。
const int LinePoints = 16;          // 分割数（多いほど滑らか）
const float SagMeters = 0.6f;       // 最大たるみ量（張力で 0 に近づける）

void DrawFishingLine(Vector3 rodTip, Vector3 hook, float tension01)
{
    var pts = new Vector3[LinePoints];
    float sag = SagMeters * (1f - Mathf.Clamped01(tension01));
    for (int i = 0; i < LinePoints; i++)
    {
        float t = (float)i / (LinePoints - 1);
        var straight = Vector3.Lerp(rodTip, hook, t);
        // 4t(1-t) は両端 0・中央 1 の放物線＝糸のたるみ形状
        straight = straight + Vector3.Down * (sag * 4f * t * (1f - t));
        pts[i] = straight;
    }
    Draw3D.Polyline(pts, closed: false, new Color(1f, 1f, 1f, 0.9f), thicknessPx: 2f);
}
```

### 例: 水面に「ヒットまでの距離リング」を出す

```csharp
// 水面（y = waterY）へ寝かせたリングで、魚までの残り距離を示す。
void DrawHookDistanceRing(Vector3 hook, float waterY, float distance)
{
    const float RingWidth = 0.25f;      // リングの帯幅（ワールド単位）
    var center = new Vector3(hook.x, waterY, hook.z);
    var color  = new Color(0.2f, 0.9f, 1f, 0.5f);
    // 法線を真上にすると水面へ寝る。内外半径の差が帯幅になる。
    Draw3D.Ring(center, Vector3.Up, distance - RingWidth, distance, color);
}
```

### 例: 索敵範囲をワイヤ球でデバッグ表示

```csharp
// depthTest: false なら地形に隠れず必ず見えるので、範囲確認に向く。
void DrawSenseRange(Vector3 center, float range, bool found)
{
    Draw3D.WireSphere(center, range, found ? Color.Red : Color.Green,
        thicknessPx: 1f, segments: 32, depthTest: false);
}
```

### 制限

| 項目 | 上限・制限 |
|---|---|
| 1 フレームの図形数 | 4096（`Draw3D.MaxPrimitivesPerFrame`）。超過分は描かれず警告ログが出る |
| 1 図形の点数 | 1024（`Draw3D.MaxPointsPerPrimitive`）。超過分は切り捨て |
| 線の太さ | 0.5〜256 px にクランプされる |
| 分割数（`segments`） | 3〜256 にクランプされる |
| `Quad` / `Polyline` の形状 | 塗りは**凸形状のみ**（頂点 0 を要とする扇分割）。凹形状は自分で三角形へ分けること |
| カメラ近平面 | 線はカメラ背後側を自動で切り詰めるが、**塗りの三角形は 1 頂点でも背後にあると丸ごと消える** |
| アンチエイリアス | なし（2D 版のフェザー帯は使わない。細い線は距離によりジャギーが出る） |
| 角（ジョイント） | 折れ線の角は継ぎ目処理をしていないため、太い線では外側にわずかな欠けが出る |

> **重要**: `Draw3D.*` は「呼んだフレームだけ描く」API です。図形を出し続けたいなら毎フレーム呼んでください。Play していないフレームに積まれたコマンドは破棄されます。

---

## 8. （メンテナ向け）新しいコンポーネントをスクリプトへ公開する手順

コンポーネントを増やしたら、以下を行うことで **自動的にスクリプト・AI 補完から使える** ようになります。

1. **Rust 側レジストリへ登録**: `runtime/src/engine/core/scripting/host_api.rs` の `read_floats` / `write_floats`（文字列フィールドがあれば `read_string` / `write_string` も）と `has_component` に、コンポーネント名の分岐を 1 つずつ追加する（`Sprite` の例に倣う）。数値は float 配列（f32=1 要素 / Vector2=2 / Vector3=3 / RGBA=4、bool は 0/1、整数は f32 変換）で受け渡す。
2. **C# 側ラッパー（任意）**: 型付きで扱いたい場合は `scripting/src/Api/` に薄いラッパー（`Sprite.cs` に倣う）を足す。`readonly struct` として `IComponentHandle<T>` を実装し、`ComponentKindName`（Rust 側の分岐キーと完全一致させる）と `FromEntity(slotEntity)` を明示実装すれば、**`GameObject.cs` への追記は不要**（`GetComponent<T>()` が汎用に解決する。名前ごとのアクセサプロパティ方式は廃止済み）。汎用アクセス（`ScriptHost.TryGetFloats` などの名前指定）だけで良ければラッパー自体が不要。
3. **本ファイル（`docs/scripting_api.md`）の第 7 節に追記**: これを忘れると AI 補完がその API を知りません。

この 3 点は `.claude/CLAUDE.md` にも運用ルールとして明記されています。

---

## 9. 使用可能なライブラリ

- 上記の SEED API（`SEED` / `SEEDEditor.Scripting` 名前空間）
- .NET 標準ライブラリ（`System`, `System.Collections.Generic`, `System.Linq`, `System.Math` など）

**使えないもの**: `UnityEngine.*`、`MonoBehaviour`、Unity のコルーチン（`IEnumerator` ベースの `StartCoroutine` 等）。これらは SEED には存在しません。
