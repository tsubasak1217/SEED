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
| `[Bindable]` | フィールド / プロパティ / 引数なしメソッド | そのメンバを**バインド元（値の供給元）**として公開する。供給先は水面シェーダの `@ref` パラメータと Text のプレースホルダ（`{num}` / `{string}`）。フィールドは `[SerializeField]` との併用が必須（プロパティ・メソッドは不要）。対応型は `float` / `int` / `string`、`Vector3` は水面シェーダ専用でフィールドのみ |
| `[TextArea]` / `[TextArea(4)]` | フィールド | `string` を**複数行のテキストボックス**にする（既定 3 行、引数で行数指定。`MinLines` / `MaxLines` も指定できる）。Enter は改行で、値の確定はフォーカスが外れたとき |
| `[AssetReference("ttf", "otf")]` | フィールド | `string` を**アセットファイルへの参照行**（パス表示＋参照ボタン＋✕）にする。指定した拡張子のファイルだけを D&D で受け付け、値は `assets://` 仮想パスで保存される |
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
- 対応型は数値（float / double / int / long / short）・bool・string・列挙型・参照フィールドです。**インスペクタが読み取り専用表示にする型（`[Flags]` 付きの列挙型など）ではボタンは出ません**。既定値の文字列に改行が含まれる場合もボタンは出ません（1 行 1 コマンドの通信経路に載せられないため）。
- `[Serializable]` ネストクラスの**フィールドそのもの**に付けてもボタンは出ません（子をまとめて戻すと Ctrl+Z が 1 手にまとまらないため）。**ネストの中の個々のフィールド**には付けられます。その場合の戻り先は**そのネストクラス側の初期化子**であり、外側での `new Nested { inner = 99f }` のような初期化は反映されません。

**`[Bindable]` の詳細**

- バインドの消費者は 2 つあります。
  - **水面シェーディングアセット（`.wgsl`）の `@ref` パラメータ** — インスペクタで
    「アクタ → コンポーネント → 変数」を選ぶだけで、そのメンバの**実行中の値**が
    毎フレームシェーダへ流し込まれます（`docs/water_shading_asset.md` の
    「3.6 `@ref` — シーンから値を流し込む」を参照）。
  - **Text のプレースホルダ記法（`{num}` / `{string}`）** — 本文の差し込み口へ
    実行中の値が毎フレーム差し込まれます（第 7 節「プレースホルダ記法」を参照）。
- **付けられる場所**はフィールド・プロパティ・引数なしメソッドの 3 つです。
  - フィールド … `[SerializeField]` との**併用が必須**です（インスペクタに出ない値を
    バインド候補に出すと、何がどこから流れているのか追跡できなくなるため）。
  - プロパティ … get アクセサを持つこと。`[SerializeField]` は**不要**です。
  - メソッド … **引数なし**で戻り値を返すこと。`[SerializeField]` は**不要**です。
- 対応する型は次のとおりです。
  - `float` … Text の `{num}` / WGSL の `f32` パラメータへ繋がる
  - `int` … Text の `{num}` へ繋がる（`float` へ変換されます）
  - `string` … Text の `{string}` へ繋がる
  - `Vector3` … WGSL の `vec3<f32>`（色）パラメータ専用。**フィールドのみ**
    （`[SerializeField]` 併用必須）

  **成分の部分取り出しは行いません。** `Vector3` を `f32` のパラメータへ繋ぐことは
  できません（X 成分だけ欲しいなら
  `[Bindable] public float PosX => transform.Position.x;` のようにプロパティを
  1 本生やしてください）。上記以外の型に付けても候補には現れません。
- **プロパティ・メソッドは毎フレーム呼び出されます。** カウンタを進める・生成する・
  ログを出すといった**副作用を持たせてはいけません**（読むだけの純粋な計算にすること）。
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
[SerializeField, Bindable]                                  // フィールド（SerializeField 併用が必須）
private float glowPower = 1.0f;

[Bindable] public float PosX => transform.Position.x;       // プロパティ（SerializeField 不要）
[Bindable] public string PlayerName => saveName;            // 文字列は Text の {string} へ
[Bindable] public int    Score() => hits * 100;             // 引数なしメソッド（副作用禁止）
```

**`[TextArea]` の詳細（改行の扱い）**

```csharp
[SerializeField(Label = "本文"), TextArea(4)]          // 4 行ぶんの高さで表示する
public string text = "";

[SerializeField, TextArea]                            // 引数を省くと 3 行
private string memo = "";

[SerializeField, TextArea(MinLines = 2, MaxLines = 8)] // 行数を範囲で挟む
private string note = "";
```

- 効果があるのは `string` フィールドだけです。他の型に付けても無視されます（値は壊れません）。
- インスペクタでは **Enter が改行**になるため、値の確定は**フォーカスが外れたとき**に行われます。
- `[System.Serializable]` 構造体リストの `string` メンバにも付けられます。
- **配列・リスト（`string[]` / `List<string>`）の要素には効きません**（属性は配列フィールド全体に付くため）。

> **重要（改行の保存形式）**: エディタ ⇔ ランタイムの通信は「1 行 = 1 コマンド」のテキストプロトコルなので、生の改行を含む値はそのまま送れません。そのため **`[TextArea]` を付けたトップレベル（および `[Serializable]` ネストクラス）の `string`** は、改行を `\n`（バックスラッシュ + n）、タブを `\t` に畳んだ表記で送受信し、**シーン JSON にもその表記のまま保存**されます。スクリプトのフィールドへ入る値は元どおりの**実際の改行を含む文字列**なので、コード側で気にする必要はありません。バックスラッシュ自体は `\\` に畳まれるため往復で失われません。バックスラッシュを含まない既存の値は無変換のままです（後方互換）。ただし、**すでに値が保存されているフィールドへ後から `[TextArea]` を付ける**場合、その値に含まれるバックスラッシュはエスケープ記号として読み直されます（1 度インスペクタで編集し直せば正しい表記へ揃います）。構造体リストの中の `string` は JSON 文字列として運ばれるので、この畳み込みは行われません（JSON 側の規則で既にエスケープされます）。

**`[AssetReference]` の詳細**

```csharp
[SerializeField(Label = "フォント"), AssetReference("ttf", "otf")]
public string fontPath = "";      // 例: "assets://prologue/fonts/MyFont.ttf"

[SerializeField, AssetReference("wav", "ogg")]
public string sePath = "";
```

- 拡張子は**ドット無し・大文字小文字どちらでも**書けます（内部で「小文字・ドット無し」へ正規化されます）。
- 行は「パス表示（読み取り専用）＋ `参照` ボタン ＋ `✕`」になります。`参照` はファイル選択ダイアログ、`✕` は未設定へ戻します。
- **Project パネルやエクスプローラーからのドラッグ＆ドロップ**で設定できます。指定した拡張子以外のファイルはドロップを拒否します。
- 保存される値は `assets://` 仮想パス（アセットルート外のファイルは絶対パスのまま）です。
- 効果があるのは `string` フィールドだけです。拡張子を 1 つも指定しない `[AssetReference()]` は通常の 1 行テキストボックスに戻ります。
- `[System.Serializable]` 構造体リストの `string` メンバにも付けられます。

- `[Serializable]` を付けたクラス／構造体型のフィールドに `[SerializeField]` を付けると、インスペクタで**子フィールドが再帰的に展開**されます（入れ子の上限は 8 段）。
- `GameObject` やコンポーネントハンドル型（`Transform` / `Camera` など）のフィールドに `[SerializeField]` を付けると、**他アクターへの参照フィールド**になります（Hierarchy から D&D で設定）。詳細は第 7 節の「参照フィールド」を参照してください。
- `ScriptEvent` 型のフィールドに `[SerializeField]` を付けると、インスペクタでメソッド呼び出し先を結線できる**イベントフィールド**（Unity の `UnityEvent` 相当）になります。詳細は次の「ScriptEvent」を参照してください。
- **列挙型（enum）**のフィールドに `[SerializeField]` を付けると、インスペクタで**メンバ名を選ぶドロップダウン**になります。詳細は次の「列挙型（enum）のフィールド」を参照してください。

### 列挙型（enum）のフィールド

列挙型のフィールドは、インスペクタで**メンバ名を選ぶドロップダウン**になります。
`[System.Serializable]` 構造体リストの要素メンバに置いた場合も同じドロップダウンです。

```csharp
public enum BlendMode { Cut, Lerp, Spring }   // スクリプト内・別ファイルどちらで宣言してもよい

[SerializeField(Label = "補間方法")]
private BlendMode blendMode = BlendMode.Lerp;   // 初期化子の値が既定の選択肢になる

[System.Serializable]
public struct StepEntry
{
    [SerializeField(Label = "遅延")] public float     delay;
    [SerializeField(Label = "補間")] public BlendMode mode;   // 構造体リストのメンバにも置ける
}
```

- 保存形式は**メンバ名の文字列**（例 `"Lerp"`）です。数値ではなく名前なので、列挙子を並べ替えても
  途中に足しても、保存済みの値の意味が入れ替わりません。シーン JSON を読んでも意味が分かります。
- 名前の照合は**大文字小文字を区別しません**（`"lerp"` でも `Lerp` として読まれます）。
- **宣言に無い名前・空文字は、スクリプト宣言時の初期値**（初期化子が無ければ数値 0 の列挙子）になります。
  警告は出ません。インスペクタでは「候補に無い値」として `⚠` 付きで選択が保たれ、**値は勝手に書き換わりません**
  （列挙子を一時的に消しただけなら、戻せば元の選択に復帰します）。
- `[ResetButton]` を付けられます。戻り先は宣言の初期化子のメンバ名です。
- **`[Flags]` 付きの列挙型は非対応**です（複数選択の意味を持つため、従来どおり読み取り専用表示になります）。
- **列挙型の配列・リスト**（`BlendMode[]` / `List<BlendMode>`）と `BlendMode?`（Nullable）は非対応です。
- 再コンパイル時の値引き継ぎは、同じ列挙型フィールドのままなら保たれます
  （型を数値などへ変えた場合は宣言時の初期値に戻ります）。

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
  それ以外の要素型（列挙型など）は従来どおり読み取り専用表示になります
  （列挙型は**単体のフィールド**としてなら編集できます。前節「列挙型（enum）のフィールド」を参照）。
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
  **スカラ型（`float` / `double` / `int` / `long` / `short` / `bool` / `string`）・列挙型・参照型・`ScriptEvent`・
  それらの `List<>` / 配列**（列挙型の配列は除く）です。メンバの `List<>` は要素の追加・削除まで編集できます。
- **入れ子は 1 段まで**です。構造体の中に構造体（またはそのリスト）を置くと、
  そのフィールド全体が対象外になり読み取り専用表示へ落ちます。ただし `ScriptEvent` は
  内部構造を晒さない**葉の型**として扱われるため、この制限に関係なく構造体メンバに置けます。
- 追加した要素の初期値は、クラス要素なら宣言時の初期化子、構造体要素なら言語既定値（0 / `false` / 空）です。
- 保存形式は 1 フィールド = JSON オブジェクト配列文字列
  （例 `[{"spawnDistance":10.0,"fishPrefabs":["assets://fish.actor"]}]`）です。
- 再コンパイル時の値引き継ぎは**メンバ名で照合**します。
  メンバを増やしても既存の値は残り（新メンバは既定値）、削除したメンバの値は無視されます。
  同名メンバの**型を変えた場合だけ**、そのフィールドが宣言時の初期値へ戻ります。

### ScriptEvent（インスペクタでメソッド呼び出しを繋ぐ）

`ScriptEvent` 型のフィールドは、Unity の `UnityEvent` に相当する「インスペクタで結線した呼び出し先を、コードから `Invoke()` 一発で呼ぶ」仕組みです。

```csharp
public class DialogueTrigger : SEEDScript
{
    [SerializeField(Label = "会話開始時")]
    public ScriptEvent onStart;   // フィールド初期化子は不要

    public override void OnStart()
    {
        onStart.Invoke();   // 結線された全メソッドを先頭から順に呼ぶ
    }
}
```

- `ScriptEvent` は初期化子を書かなくても **null にはなりません**（スクリプトのインスタンス生成直後に、結線 0 件の空実体が自動で注入されます）。null チェックは不要です。
- `Invoke()` は結線されたバインディングを先頭から順に 1 回ずつ呼びます。結線が 0 件なら何もしません。

**バインディング 1 件の構成**

インスペクタで `[＋]` すると 1 件のバインディング行が増えます。1 件は次の 4 項目で構成されます。

| 項目 | 内容 |
|---|---|
| 対象アクター | 呼び出し先のアクター名（Hierarchy から D&D で設定） |
| スクリプト型 | 対象アクターに付いているスクリプトの型（ドロップダウンで選択） |
| メソッド | 呼び出す対象メソッド（ドロップダウンで選択） |
| 引数 | メソッドが 1 引数を取る場合のみ、その値を入力する欄が出る |

ドロップダウンの候補に出る（＝呼び出せる）メソッドの条件は次のとおりです。

- `public` インスタンスメソッドであること（`static` / `private` / `protected` は対象外）。
- 非ジェネリックメソッドであること。
- 引数は **0 個**、または **`string` / `float` / `int` / `bool` / `GameObject` のいずれか 1 個**であること（`ref` / `out` 引数は不可）。
- 戻り値の型は問いません（戻り値は捨てられます）。

**実行時の解決**

- `Invoke()` が呼ばれるたびに、アクター名 → 対象スクリプトの実インスタンス → メソッドの順に**毎回解決**してから呼び出します（結果はキャッシュしません）。そのため、**実行中に `Instantiate` で生成したアクターへも結線が届きます**（生成後のアクター名がバインディングの対象アクター名と一致すればよい）。
- 同名アクターが複数存在する場合は、ヒエラルキーの **DFS で最初に見つかったもの**が呼び出し先になります（参照フィールドと同じ規則）。
- 対象アクターが未設定（空欄）のバインディングは黙って無視されます。
- アクターが見つからない・対象スクリプトが見つからない・対象メソッドが見つからないといった**解決失敗は、そのバインディングにつき最初の 1 回だけ警告ログ**を出します（同じ失敗が続いても 2 回目以降は鳴りません）。
- 呼び出し先メソッドの内部でユーザー例外が発生した場合は、呼び出しを打ち切って `SEED.Debug.LogError` へ記録します（呼び出し元やエンジン全体は落としません）。

**インスペクタでの操作**

- `[＋]` でバインディングを 1 件追加、行右端の `[×]` で 1 件削除します。
- 対象アクターは、他の参照フィールドと同じく **Hierarchy パネルからのドラッグ＆ドロップ**で設定します。
- 対象アクターを設定すると、そのアクターに付いているスクリプト型のドロップダウンが選択可能になり、型を選ぶとそのスクリプトの呼び出し可能なメソッドのドロップダウンが選択可能になります。
- メソッドを選ぶと、そのメソッドが要求する引数の種類に応じて引数入力欄が自動的に切り替わります（0 引数メソッドなら欄は出ません）。

> 上記のインスペクタ UI は Phase 3 として実装中です。保存形式・実行時解決の仕様は確定済みです。

**保存形式**

1 フィールド = JSON 配列文字列です。バインディング 1 件につき、キーは常に 5 個すべてを出力します（`actor` / `script` / `method` / `argKind` / `arg`）。未設定（結線 0 件）は `[]` です。

```json
[{"actor":"DialogueManager","script":"QuestFlow","method":"Begin","argKind":"string","arg":"intro"}]
```

**制限**

- バインディングは**アクター名**で保存されるため、対象アクターをリネームしても自動で追従しません（構造体リスト内の参照メンバと同じ既存制限。単体の参照フィールドのような自動追従はありません）。
- `List<ScriptEvent>`（`ScriptEvent` の配列・リスト）は非対応です。
- `[ResetButton]` は `ScriptEvent` フィールドには対応していません。
- 対象スクリプトがコンパイル失敗中は、そのスクリプトのメソッドは候補に出ません。
- プレハブを複数インスタンス化すると同名アクターが複数生まれるため、結線は常に**先頭に見つかった**同名アクターへ向きます（他のインスタンスには届きません）。
- `[System.Serializable]` 構造体の**要素メンバ**として置けます（内部の結線一覧を展開しない「葉の型」として扱われるため、構造体の入れ子段数制限には影響しません）。

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

> **重要**: 同じアクターに有効な **CanvasGesture**（次節のジェスチャー）も付いていると、`OnPointerDown` / `OnPointerUp` / `OnPointerClick` は届きません（押す・離す・クリックはジェスチャーの `OnGesturePressDown` / `OnGesturePressUp` / `OnGestureTap` が受け持つ。スクロールに負けた押下でクリックが起きる取り違えを防ぐため）。`OnPointerEnter` / `OnPointerExit` は届きます。CanvasGesture を付けていないアクターの `OnPointer*` は従来のままです。

### ジェスチャーコールバック（CanvasGesture：タップ・長押し・ドラッグ・フリック・押下の見た目・ピンチ。W2-2・W2-8）

自分のアクター（2D キャンバスのノード）に **CanvasGesture**（「コンポーネント追加 → UI → Canvas Gesture」。§7）を付けると、Play 中の指（PC はマウスの左ボタン・エディタ／MCP の入力の注入も 1 本の指）の操作が以下のコールバックで届きます。指ごとの**ジェスチャーアリーナ**で、押した位置の当たり判定の経路（子 → 親）のノードが受けたいジェスチャーを競い、最初に成り立った 1 つが勝ちます（他は負け）。規則の正典は `docs/input_gestures.md`。

```csharp
// SEEDScript の override（引数はすべて SEED.GestureEvent）
public override void OnGestureTap(SEED.GestureEvent e)          // タップ（押して・動かず・離した。PressUp の直後）
public override void OnGestureLongPress(SEED.GestureEvent e)    // 長押し（500ms 動かずに押し続けた。指はまだ触れている）
public override void OnGestureDragStart(SEED.GestureEvent e)    // ドラッグの始まり（8 dp を超えて動いた。e.Delta は押した位置からの移動）
public override void OnGestureDragUpdate(SEED.GestureEvent e)   // ドラッグの途中（1 フレームに 1 回まで。e.Delta は前のドラッグのイベントからの移動）
public override void OnGestureDragEnd(SEED.GestureEvent e)      // ドラッグの終わり（e.Velocity は離した時点の速度。取り消しなら e.Canceled）
public override void OnGestureFling(SEED.GestureEvent e)        // フリック（離した時点の速度が 50 dp/秒 以上。DragEnd の直後）
public override void OnGesturePressDown(SEED.GestureEvent e)    // 押下の見た目を出す
public override void OnGesturePressCancel(SEED.GestureEvent e)  // 押下の見た目を戻す（外へ出た・スクロールに負けた・複数指・取り消し）
public override void OnGesturePressUp(SEED.GestureEvent e)      // 押下の見た目を戻す（タップ・長押しとして離した）
public override void OnGesturePinchStart(SEED.GestureEvent e)   // ピンチの始まり（CanvasGesture.Pinch のノード。2 本の指の間が 8 dp 変わった。e.Scale は 1。W2-8）
public override void OnGesturePinchUpdate(SEED.GestureEvent e)  // ピンチの途中（1 フレームに 1 回まで。e.Scale は始まりからの倍率・e.Position は 2 本の指の中点・e.Delta は中点の移動）
public override void OnGesturePinchEnd(SEED.GestureEvent e)     // ピンチの終わり（どちらかの指を離した・取り消し〈e.Canceled〉）

// SEED.GestureEvent（値型）
e.Kind            // GestureKind: Tap / LongPress / DragStart / DragUpdate / DragEnd / Fling / PressDown / PressCancel / PressUp / PinchStart / PinchUpdate / PinchEnd
e.PointerId       // int: 指の番号（0 起点。ジェスチャーに参加している指の間。Touch.FingerId とは別）
e.Position        // Vector2: 今の位置（キャンバスの画素。画面の中央が原点・Y 下向き。Input.MousePositionCanvas と同じ）
e.ScreenPosition  // Vector2: 今の位置（画面の画素・左上原点。Input.MousePos・Touch.Position と同じ）
e.LocalPosition   // Vector2: ノードの見た目の矩形の左上が原点・ノードの単位（dp のキャンバスなら dp。スライダのどこを押したか）
e.StartPosition   // Vector2: 押した位置（キャンバスの画素）
e.Delta           // Vector2: 移動量（画素。横だけ・縦だけのドラッグは軸へ射影）
e.DeltaDp         // Vector2: 移動量（dp）
e.Velocity        // Vector2: 速度（画素/秒。直近 100ms の標本の最小二乗・上限 8000 dp/秒。軸のドラッグは射影）
e.VelocityDp      // Vector2: 速度（dp/秒）
e.TotalDelta      // Vector2: 押した位置からの移動（射影しない）
e.DpScale         // float: 1 dp の画素数（PC の 100% は 1、Pixel 6a は 2.625）
e.Duration        // float: 押してからの秒（入力イベントの時刻で測る。フレームの時刻に依らない）
e.Canceled        // bool: DragEnd・PinchEnd が取り消し（アプリが背面へ・一時停止・OS の取り消し）で来た（速度 0）
e.Scale           // float: ピンチの倍率（今の 2 本の指の間の距離 ÷ ピンチが始まったときの距離。ピンチ以外は 1。W2-8）
e.ScaleX / e.ScaleY // float: 横・縦の幅の比（始まったときの幅が 1 画素以下の向きは 1）

// 例: スクロールの中のボタン（押した色 → スクロールしたら戻す → 離したら押された）
public class RowButton : SEEDScript
{
    private void Tint(float v) { if (gameObject.GetComponent<SEED.Sprite>() is { } s) s.Color = new SEED.Color(v, v, v, 1f); }
    public override void OnGesturePressDown(SEED.GestureEvent e)   => Tint(0.7f);
    public override void OnGesturePressCancel(SEED.GestureEvent e) => Tint(1f);
    public override void OnGesturePressUp(SEED.GestureEvent e)     => Tint(1f);
    public override void OnGestureTap(SEED.GestureEvent e)         => SEED.Debug.Log("押された");
}
```

| 規則 | 内容 |
|---|---|
| 勝ち負け | ドラッグは軸に沿って 8 dp 動いたら・長押しは 500ms で・タップは離したときに勝ちを申し出て、最初の 1 つが勝つ。離したときに勝者がいなければ最初の（子の）タップ。同じ移動で複数のドラッグが申し出たら子が先 |
| 軸 | 横だけのドラッグは縦の動きでは始まらない（縦の一覧の中の横スクロールは最初の動きの向きで持ち主が決まる） |
| 捕捉 | 勝ったドラッグはその指を捕まえ、ノードの外へ出ても DragUpdate / DragEnd が届く |
| 押下の見た目 | 単独のボタンは押した瞬間に PressDown、ドラッグ（スクロール）の中のボタンは 100ms 待ってから（速いタップは離したときに PressDown → PressUp → Tap）。入れ子のボタンは内側だけが押される |
| タップの許容移動 | 押した位置から 18 dp を超えて動く・ノード（最小のヒット領域 + 8 dp）の外へ出るとタップ・長押しは成り立たず PressCancel |
| 複数指 | 指ごとに別の競い。押しているボタンに 2 本目の指が触れたら両方とも取り消し。1 つのノードのドラッグは 1 本の指まで（スクロール中の一覧の中は別の指で押しても反応しない） |
| ピンチ（W2-8） | `Pinch` のノードに 2 本の指が触れ、指の間が 8 dp 変わったら始まる（2 本の指のタップ・ドラッグは取り消し、以後はピンチだけが受ける）。外側のノード（縦の一覧など）のドラッグに取られている指ではピンチにならない。倍率と中点だけ（回転は無い）。規則は `docs/input_gestures.md` §2.6 |
| 最小のヒット領域 | 見た目が 48 dp より小さいノードは中心をそろえて広げる。広げた領域が重なる所は見た目に近いノード |
| 切り抜き | CanvasClip の外で押した指は参加しない |
| 閾値 | `project_settings.json` の `"gestures"`（`touch_slop_dp`・`tap_slop_dp`・`long_press_ms`・`press_delay_ms`・`tap_max_ms`・`min_fling_velocity_dp`・`max_fling_velocity_dp`・`velocity_*`）で上書きできる |

### スクロールコールバック（CanvasScroll：OnScrollStart / OnScroll / OnScrollEnd。W2-3）

自分のアクター（2D キャンバスのノード）に **CanvasScroll**（「コンポーネント追加 → UI → Canvas Scroll」。§7）を付けると、そのノードが中身（子）をずらして
見せるスクロールの窓になり、Play 中のスクロールが以下のコールバックで届きます（スクリプトフェーズより前。このフレームの `Update` から新しい位置が見える）。
指のドラッグ・離した後の慣性・端の跳ね返り・スナップ・入れ子はエンジンが動かします（C# で位置を動かす必要は無い）。規則の正典は `docs/ui_scroll_list.md`。

```csharp
// SEEDScript の override（引数はすべて SEED.ScrollEvent）。1 フレームに最大で Start → Scroll → End の順
public override void OnScrollStart(SEED.ScrollEvent e)  // スクロールが始まった（ドラッグ・慣性・ScrollTo・位置の書き込み）
public override void OnScroll(SEED.ScrollEvent e)       // 位置が変わった（1 フレームに 1 回まで）
public override void OnScrollEnd(SEED.ScrollEvent e)    // スクロールが終わった（止まった・指で触れて止めた）

// SEED.ScrollEvent（値型。値はキャンバスの単位＝dp のキャンバスなら dp）
e.Kind          // ScrollEventKind: Start / Update / End
e.Position      // Vector2: 今の位置（0 = 中身の先頭が窓の先頭。下・右へスクロールすると増える）
e.Delta         // Vector2: 前に知らせた位置からの差（Update だけ）
e.Velocity      // Vector2: 速度（単位/秒・位置の向き）
e.MaxPosition   // Vector2: 位置の最大（中身 − 窓）
e.ViewportSize  // Vector2: 窓の大きさ
e.ContentSize   // Vector2: 中身の大きさ
e.IsDragging    // bool: 指でドラッグしているか

// 例: スクロールが始まったら、開いているスワイプの行を閉じる（SEED.UI.SwipeGroup。§7.15）
public override void OnScrollStart(SEED.ScrollEvent e) => SEED.UI.SwipeGroup.For(gameObject).CloseAll();
```

> **重要**: 位置の書き込み（`CanvasScroll.Position` / `JumpTo`）でも、止まったまま位置が変わるので **Start → Scroll → End の 3 つが同じフレームに**届きます。
> 慣性で動いている窓に指で触れると止まり（End）、その指は中の行へ届きません（慣性の途中のタップで行が押されない。Flutter・Android と同じ）。

> **重要**: ジェスチャーのコールバックは **CanvasGesture を付けたアクター**にだけ届きます（付けていないアクターは参加しない）。呼ばれるのはスクリプトフェーズ（`Update` 等）より前で、同じフレームの `Update` から結果を参照できます。3D ワールド内のキャンバスには届きません。

> **重要**: 受けるジェスチャーを全部外した CanvasGesture は「遮る板」になります（後ろのノードへ指を渡さない）。ダイアログの板・覆いに付けると、後ろのボタンが押されません。ジェスチャーを受けない Sprite は遮りません。

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
Time.DeltaTime            // float: 前フレームからの経過秒（Time.Scale 適用後のゲーム時間）
Time.ElapsedTime          // float: ゲーム内の累計時間（秒。Time.Scale 適用後）
Time.UnscaledDeltaTime    // float: 前フレームからの経過秒（Time.Scale 未適用の実時間）
Time.UnscaledElapsedTime  // float: ゲーム内の累計時間（秒。Time.Scale 未適用）
Time.Scale                // float（get/set）: ゲーム時間の進む速さ。既定 1.0 / 0 で停止 / 上限 100
Time.Fps                  // float: 直近 1 秒の平均フレームレート（実測値。1 秒ごとに更新）
Time.FrameTimeMs          // float: 直近フレームの実時間（ミリ秒。フレームレート制限の待ちを含む実測周期）
```

### Time.Fps / Time.FrameTimeMs（実測フレームレート）

実測値であり、プロジェクト設定の目標フレームレート（`target_fps`）とは別物です。
`Time.Fps` は移動平均ではなく**1 秒ごとの窓**で確定するため、そのまま画面に出しても数値が跳ねません
（起動直後の 1 秒間は `0`）。`Time.FrameTimeMs` はフレーム制限の待ち時間も含む実測周期なので、
60fps に制限中はおよそ `16.7` になり、`Time.UnscaledDeltaTime * 1000` とは一致しません。

```csharp
// 画面に fps を出す（Text コンポーネントへ毎フレーム書き込む）
if (gameObject.GetComponent<SEED.Text>() is { } label)
{
    label.Content = $"{SEED.Time.Fps:F1} fps / {SEED.Time.FrameTimeMs:F2} ms";
}
```

### Time.Scale（ヒットストップ・スローモーション）

`Time.Scale` はゲーム時間の進む速さをまとめて伸縮させます。`0` で完全停止、`0.5` で半分、`2` で倍速。
負の値は `0` に丸められ、上限は `100`（`NaN` は `1` に戻ります）。

| 項目 | Time.Scale の影響 |
|---|---|
| `Time.DeltaTime` / `Time.ElapsedTime` | 止まる（スケール倍される） |
| `ConstantUpdate` の呼び出し回数 | 止まる（スケール 0 で 1 回も呼ばれない） |
| アニメーション（キーフレーム／モデル／クロスフェード） | 止まる |
| 物理（3D / 2D） | 止まる（スケール 0 でステップ停止・速度は保持） |
| パーティクル | 止まる |
| 水面・水位シミュレーション・インタラクション場 | 止まる |
| `Time.UnscaledDeltaTime` / `Time.UnscaledElapsedTime` | 止まらない |
| 入力（`Input.*`） | 止まらない |
| オーディオ（`Audio.PlayBgm` / `AudioSource` の再生） | 止まらない（`Scale = 0` でも鳴り続ける。止めたいときは `Audio.PauseBgm` / `AudioSource.Stop` を明示的に呼ぶ） |
| `Update` などフェーズの呼び出しそのもの | 止まらない（毎フレーム呼ばれ続ける） |
| エディタのカメラ操作・Edit モードのプレビュー | 影響なし（Play 中のみ効く） |

```csharp
// ヒットストップ: 0.08 秒だけゲームを 0.1 倍速にする
private float _hitStopLeft;

public override void OnHit()
{
    SEED.Time.Scale = 0.1f;   // 遅くする
    _hitStopLeft = 0.08f;
}

public override void Update(ref NativeFrameContext ctx)
{
    if (_hitStopLeft > 0f)
    {
        // 【重要】戻すためのタイマーは必ず UnscaledDeltaTime で減らす。
        // DeltaTime で減らすと Scale = 0 のとき永久に減らず、ゲームが止まったままになる。
        _hitStopLeft -= SEED.Time.UnscaledDeltaTime;
        if (_hitStopLeft <= 0f) SEED.Time.Scale = 1f;   // 戻し忘れ厳禁
    }
}
```

```csharp
// ポーズ: ゲームだけ止め、UI は動かし続ける
SEED.Time.Scale = 0f;                                   // ゲーム時間を停止
menuAlpha += SEED.Time.UnscaledDeltaTime * 4f;          // UI は実時間で動く
SEED.Time.Scale = 1f;                                   // 再開
```

> **重要**: `Time.Scale` は Play 開始時・Play 停止時・シーン遷移時に自動で `1.0` へ戻りますが、
> それ以外では戻りません。ヒットストップ・ポーズは**必ず自分で `1.0` に戻す**こと。
> 戻すためのタイマーは必ず `Time.UnscaledDeltaTime` で減らしてください
> （`DeltaTime` は `Scale = 0` のとき 0 になり、永久に復帰できなくなります）。
> UI・演出・ポーズメニューは `UnscaledDeltaTime` を使うのが原則です。

```csharp
Debug.Log("メッセージ");        // 情報ログ
Debug.LogWarning("注意");       // 警告
Debug.LogError("失敗");         // エラー
```

### Debug.OnCommand（外部からのデバッグ指示を受ける）

エディタ／MCP から送られた**デバッグコマンド**（`SCRIPT_DEBUG:{name},{arg}` IPC）を
スクリプトで受け取る仕組みです。「ゲームの奥まった場面だけを、人の操作なしで 1 手で作る」
ための開発用の入口で、**AI がゲームの流れを検証するときの標準手段**です
（送り方は `docs/editor_mcp.md` 10 章）。

```csharp
public override void OnStart()
{
    // name は大文字小文字を区別しない。同じ名前へ複数のスクリプトが登録してもよい
    SEED.Debug.OnCommand("catch_test", arg => FakeCatch(arg));
}

public override void OnDestroy()
{
    // 登録解除は必須（外さないと破棄済みスクリプトのハンドラが呼ばれ続ける）
    SEED.Debug.OffCommand("catch_test");
}
```

| API | 意味 |
|---|---|
| `Debug.OnCommand(string name, Action<string> handler)` | ハンドラを登録する。同じ名前とハンドラの組は二重登録されない |
| `Debug.OffCommand(string name, Action<string>? handler = null)` | ハンドラを外す。`handler` 省略でその名前を丸ごと外す |
| `Debug.ResetCommandHandlers()` | 登録をすべて捨てる（シーン遷移で持ち越さないため） |

- ハンドラは**ゲームスレッドのフレーム先頭（`BeginFrame` フェーズ）**で呼ばれます。
  中で ECS を触って構いません。
- `arg` はコマンドの引数（無指定なら空文字）。改行は含まれません。
- **Play 中のみ**届きます（Edit 中はランタイムが受け取り自体を拒否します）。
  Play の開始・停止で未配信の分は捨てられるので、前回 Play の指示が突然走ることはありません。
- ハンドラが例外を投げても、次のハンドラ・次のコマンドへ進みます（ログには残ります）。
- 未登録の名前が届くと `[Script:警告] [Debug] 登録されていないデバッグコマンド: xxx` が出ます。
- 静的な登録表なので、**シーン遷移では作り直されません**。必ず `OnDestroy` で外してください。

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

## 6.5 Input（キーボード・マウス・タッチ入力）

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

Android の戻るキー（ナビゲーションバーの戻る・戻るジェスチャ）は `KeyCode.Escape` として届く（Unity と同じ）。アプリは自動で終了しないので、ポーズメニューや終了確認は `Input.GetKeyDown(KeyCode.Escape)` を拾ってスクリプトで決める。

### タッチ（複数指）

Unity 風の複数指タッチ。**PC ではマウスの左ボタンが指 1 本として合成される**ので、タッチ前提のスクリプトも PC の Play でそのまま試せます。Android では**最初に触れた指（指0）がマウスも動かす**（`MousePos`・`GetMouseButton(MouseButton.Left)`・キャンバス UI の `OnPointer*` がタッチで動く）ので、マウス前提のスクリプトも実機で動きます。

```csharp
// タッチ（複数指）
Input.TouchSupported  // bool: タッチ入力を主に使う端末か（Android: true / PC: false。プラットフォーム単位）
Input.TouchCount      // int:  このフレームの指の本数（このフレームで離れた指も含む。最大 10）
Input.GetTouch(i)     // Touch: i 番目（触れ始めた順・0 起点）の指。範囲外は例外にせず Touch.None
Input.Touches         // Touch[]: 全指（触れ始めた順。呼ぶたびに配列を作るので毎フレームなら TouchCount + GetTouch が軽い）

// Touch（値型。同じフレームの間は何度取得しても同じ値）
touch.FingerId        // int:     指番号（0 起点。触れている間は不変。空いている最小の番号を割り当てる）
touch.Position        // Vector2: 位置（スクリーン座標・ピクセル・左上原点。Input.MousePos と同じ系）
touch.DeltaPosition   // Vector2: 前フレームからの移動量（右=+X / 下=+Y。触れ始めたフレームは触れ始めた位置から）
touch.Phase           // TouchPhase: このフレームの段階
touch.IsValid         // bool:    有効な指か（Touch.None は FingerId = -1・Phase = Canceled で false）

// TouchPhase（Unity と同じ並び）
TouchPhase.Began       // 触れ始めたフレームだけ
TouchPhase.Moved       // 触れたまま前フレームから位置が変わった
TouchPhase.Stationary  // 触れたまま位置が変わっていない
TouchPhase.Ended       // 離れたフレームだけ（そのフレームは一覧に残り、次フレームで消える）
TouchPhase.Canceled    // OS に取り消されたフレームだけ（着信・フォーカス喪失など。Ended と同じく 1 フレーム残る）

// 例: 1 本指ドラッグで移動・2 本指で拡大率（ピンチ）
public override void Update(ref NativeFrameContext ctx)
{
    if (SEED.Input.TouchCount == 1)
    {
        var t = SEED.Input.GetTouch(0);
        if (t.Phase == SEED.TouchPhase.Moved) { /* t.DeltaPosition だけ動かす */ }
        if (t.Phase == SEED.TouchPhase.Ended) { /* 指を離した = タップ判定など */ }
    }
    else if (SEED.Input.TouchCount >= 2)
    {
        var a = SEED.Input.GetTouch(0);
        var b = SEED.Input.GetTouch(1);
        float now  = (a.Position - b.Position).Magnitude;
        float prev = ((a.Position - a.DeltaPosition) - (b.Position - b.DeltaPosition)).Magnitude;
        float pinch = now - prev;   // 正 = 指が離れた（拡大）
    }
}
```

> **重要**: 素早いタップ（触れて離れるまでが 1 フレームに収まる）も、**触れたフレームは `Began`・次のフレームで `Ended`** として必ず 2 フレームに分けて見えます（`Began` だけ・`Ended` だけを調べるスクリプトも取りこぼさない）。Android でタッチが動かすマウスも同じで、左ボタンは「押下フレーム → 次フレームで解放」になります。

> **重要**: PC ではマウス左ボタンを押している間だけ指が 1 本（`FingerId = 0`）載り、押したまま動かすと `Moved`、止めると `Stationary`、離したフレームは `Ended` です。右・中ボタンやホイールは指になりません。Android では 2 本目以降の指はマウスに影響せず、指0 が離れた後に残った指が指0 を引き継ぐこともありません（新たに全指が離れてから触れた指が次の指0）。Android でもスクリプトは PC と同じ DLL がそのまま動く（APK に同梱した .NET 10 の CoreCLR。docs/android.md §17）。

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

SEED.Audio.PauseBgm();           // BGM を一時停止（再生位置は保持）
SEED.Audio.ResumeBgm();          // 一時停止した位置から再開
SEED.Audio.SetBgmVolume(0.3f);   // BGM 音量を変更
SEED.Audio.SetBgmSpeed(1.25f);   // BGM 再生速度を変更（1.0 = 等倍）
SEED.Audio.StopBgm();            // BGM を停止
```

音声辞書（**AudioDictionary** コンポーネント）を使うと、パスの代わりに `グループ名/用途名` のキーで鳴らせます。素材を差し替えるときは辞書の行を直すだけで、呼び出し側のコードは変えなくて済みます。

```csharp
// 辞書のキーで鳴らす（パス指定の Play / PlayBgm とは別名。混同防止）
SEED.Audio.PlayDict("Player/attack");            // 音量は辞書の既定値
SEED.Audio.PlayDict("Player/attack", 0.5f);      // 音量を明示（辞書の既定値より優先）

SEED.Audio.PlayBgmDict("Bgm/stage1");            // 音量は辞書の既定値・ループあり
SEED.Audio.PlayBgmDict("Bgm/jingle", false);     // ループなし
SEED.Audio.PlayBgmDict("Bgm/stage1", 0.8f, loop: true);
```

- キーは **シーン内の全 AudioDictionary を横断**して引きます（完全一致・DFS 順で先勝ち）。同名キーが複数の辞書にあるときは起動時に 1 度だけ警告が出ます。
- 解決できないキーは**警告を出して何も鳴らしません**（無音で握りつぶしません）。
- `PlayDict` / `PlayBgmDict` の `volume` に**負の値**を渡す（既定）と辞書の既定音量を使います。0 以上を渡すとその値が優先されます。
- 辞書の作り方・インスペクタ操作・キー解決規則は **docs/audio_dictionary.md** を参照してください。

- `PauseBgm` / `ResumeBgm` は **Sink を破棄せずに止める／続ける**ので、再生位置（＝ループの途中位置）がそのまま保たれます。`StopBgm` → `PlayBgm` は必ず先頭からの再生になるため、位相を保ったまま止めたい場面（`Time.Scale = 0` に合わせてリズムのループを凍結するなど）では `PauseBgm` を使ってください。BGM が無い／既に同じ状態のときは何も起きません（多重呼び出し安全）。**`Time.Scale` は BGM を自動では止めません**（下表のとおりオーディオは dt 駆動ではありません）。止めたいときは明示的に `PauseBgm` を呼びます。
- `SetBgmSpeed` は早送り／スロー再生なので、**速度に比例してピッチも変わります**（テンポだけを変える機能ではありません）。値は 0.25〜4.0 にクランプされ、BGM を差し替えても保持されます（`PlayBgm` の前後どちらで指定しても同じ結果）。等倍へ戻すときは明示的に `1.0` を渡してください。素材の BPM が分かっていれば `SetBgmSpeed(目標BPM / 素材BPM)` で任意のテンポに合わせられます。
- 同じファイルはキャッシュされ、2 回目以降の再生でディスク読み込みは発生しません。
- オーディオデバイスが無い環境では全操作が無音で無視されます（エラーになりません）。
- **Android** では、アプリが背面へ回ったとき・他のアプリに音声フォーカスを奪われたとき（着信・音楽アプリの再生など）に出力全体が自動で一時停止し、戻ると続きから再開します（スクリプトからの呼び出しは不要。`PauseBgm` で止めた BGM は止めたまま）。止まっている間に `Audio.Play` した効果音とループしない AudioSource の再生は鳴らさずに捨てます（再開の瞬間にまとめて鳴らないように）。通知音などが鳴っている間は全体の音量が下がります。詳細は docs/android.md §16。
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

> **`HasComponent(name)` の名前**: 受け付ける文字列は `Transform` / `CanvasTransform` / `Sprite` / `Camera` / **`Audio`**（AudioSource ではなく `Audio`）/ **`AudioDictionary`** / `Animator` / `ParticleEmitter` / `InputMap` など（正典は `host_api.rs` の `has_component`）で、登録が無い名前は常に false です。型で判定できる場面では `GetComponent<T>() is { }` のほうが安全です。

### 表示 / 非表示（GameObject.Visible）

```csharp
gameObject.Visible = false;   // このアクターと全子孫の「描画だけ」を止める
bool v = gameObject.Visible;  // bool（get/set。自分自身のフラグ）
```

| 項目 | 挙動 |
| --- | --- |
| 止まるもの | モデル・スプライト・Text・SkinnedSprite・パーティクル・ライト・スカイボックス・`SEED.Draw` の座標空間参照 |
| 止まらないもの | スクリプトの `OnUpdate`・アニメーション・物理・イベント配信（`Visible=false` でも動き続けます） |
| 子孫への波及 | 祖先が 1 つでも非表示なら子孫も非表示（実効表示 = 祖先すべて `Visible` かつ自分 `Visible`） |
| ポインタ | 非表示のアクターはポインタイベントのヒット判定に**当たりません** |
| get の意味 | **自分自身のフラグ**を返します（Unity の `activeSelf` と同じ流儀。祖先が非表示でも自分が `true` なら `true`） |
| set の反映 | 実際の反映はフレーム末尾（`Destroy` と同じ遅延モデル）。ただし同フレーム中の get は設定した値を返します |
| アクティブとの違い | 非アクティブ（`active=false`）は更新も描画も止めます。`Visible` は**描画だけ**を止めます |

> **重要**: `Visible` はシーン／`.actor` ファイルへ `"visible": false` として保存されます（`true` は省略）。エディタのヒエラルキー各行の目アイコン、およびインスペクタのアクタ名の横のトグルと同じフラグです。

### ジェスチャーの取り消し（GameObject.CancelGestures。W2-3）

```csharp
row.CancelGestures();   // この行と子孫の、ジェスチャーの押下とドラッグを取り消す（一覧の行を使い回す前）
```

押している最中の行を別のデータへ使い回しても、元の押下の `Tap` が新しい行へ届かないようにします。押していたノードへは `OnGesturePressCancel`、
ドラッグ中（スワイプ）なら取り消しの `OnGestureDragEnd`（`e.Canceled = true`）が**次のフレーム**に届きます（押下の見た目を戻す）。
取り消し自体はフレーム末尾に行われます（`Visible` と同じ遅延の流儀）。`SEED.UI.ListView` は行を使い回すときに自分で呼びます。

### アクター名（GameObject.Name）

```csharp
string n = gameObject.Name;    // アクター名（ヒエラルキーに出る名前）
spawned.Name = "BeatIcon00";   // 動的生成したアクターに一意な名前を付ける
```

| 項目 | 挙動 |
| --- | --- |
| get | アクター名。アクターが無効なら空文字を返します |
| set の反映 | 実際の反映はフレーム末尾（`Visible` と同じ遅延モデル）。ただし同フレーム中の get は設定した値を返します |
| 空文字の set | **無視されます**（名前で引けないアクターを作らないため） |
| 参照の追従 | **しません**。名前でこのアクターを指している参照文字列（`[SerializeField]` のアクタ参照など）は書き換わりません |

> **用途は「動的生成したアクターへ一意な名前を付ける」ことです。** `Instantiate` で同じプレハブを複数生成すると全て同名（`.actor` のルート名）になり、`Find` / `FindChild` では区別できません。生成直後に連番名を付けておくと、次に同じスクリプトが走ったときに「既にあるものを見つけて使い回す」ことができます（この定型は後述の `SpawnOnce` にまとまっています）。

> **シーンに元からあるアクターの改名には使わないでください。** 参照が追従しないため、名前で参照している側が切れます。既存アクターの改名はエディタのヒエラルキーで行ってください（そちらは参照も書き換えます）。

### 二重生成を防ぐ（SpawnOnce）

`assets://common/scripts/UI/SpawnOnce.cs`

```csharp
// 親を指定しない（シーン全体から "ReelSound" を探し、無ければ生成して命名する）
reelSoundActor = SpawnOnce.GetOrInstantiate("ReelSound", reelSoundActorPath);

// 親の配下から探して、無ければその親の下に作る（プールは連番で一意名にする）
var icon = SpawnOnce.GetOrInstantiate($"BeatIcon{i:00}", beatIconActorPath, parent);
```

`static GameObject GetOrInstantiate(string actorName, string prefabPath, GameObject? parent = null)`

名前で既存アクターを探し、**無いときだけ**プレハブから生成して、その名前を付けて返します。

**なぜ要るか**: エディタで `.cs` を保存するとランタイムは全スクリプトインスタンスを作り直すため、`OnStart` が改めて呼ばれます。`OnStart` の中で素の `Instantiate` をしていると、呼ばれた回数だけアクターが増えていきます（Play しながら編集していると補助アクタが二重三重になり、重くなる／古い方が前面に残る）。エディタ側は既定で「Play 中はホットリロードを保留する」ようになっていますが（`docs/editor_auto_reload.md`）、その設定を切った場合のための多重防御です。

| 引数 | 意味 |
| --- | --- |
| `actorName` | 照合キー。生成した場合はこの名前が `GameObject.Name` に設定されます。空文字なら照合せず常に新規生成します |
| `prefabPath` | 生成元の `.actor`（`assets://...`）。空なら生成せず、既存が見つかればそれを返します |
| `parent` | 探索と生成の親。指定すると**その配下だけ**を探し、生成もその子として行います。`null` ならシーン全体から探し、ルートへ生成します |

- **同一フレーム内で同じ名前を 2 回要求すると 2 つ作られます**（生成も改名もフレーム末尾に反映されるため）。プールなら添字で一意名を振ってください。
- 親を渡せる場合は必ず渡してください。探索範囲が配下に限定され、同じプレハブを複数並べても他人のアクターを掴みません。
- 見つけた既存アクターは**そのままの状態**（表示・位置・アニメの進行）で返ります。初期化が必要なら呼び出し側で行ってください。

### 生成・破棄・検索（Instantiate / Destroy / Find / FindChild）

```csharp
// .actor ファイル（プレハブ）からアクターを生成する（assets:// 仮想パス）
var bullet = SEED.GameObject.Instantiate("assets://actors/Bullet.actor");
if (bullet.GetComponent<Transform>() is { } bt)   // 生成直後に位置設定できる
    bt.Position = transform.Position;
if (!bullet.IsValid) { /* 読み込み失敗 */ }

// アクターを破棄する（実際の破棄はフレーム末尾。Unity の Destroy と同じ遅延モデル）
bullet.Destroy();                       // インスタンス版
SEED.GameObject.Destroy(bullet);        // 静的版（同じ動作）

// アクターを名前で検索する（シーン全体。ヒエラルキーの DFS 順で最初の一致）
var player = SEED.GameObject.Find("Player");
if (player.GetComponent<Transform>() is { } pt) { pt.Position = SEED.Vector3.Zero; }

// 自分の配下だけを検索する（プレハブを複数並べても自分の子を返す）
var image = gameObject.FindChild("Image");        // 直下の子（フォルダ透過）→ 無ければ子孫を DFS
var head  = gameObject.FindChild("Body/Head");    // "/" 区切りで子をたどる
var same  = gameObject.FindChild("./Image");      // 先頭の "./" は付けても同じ意味
if (!image.IsValid) { /* 見つからない */ }
```

- `Instantiate` の戻り値には**同フレーム中に** `Transform.Position` 等を設定でき、その値が優先されます（アクター本体の構築はフレーム末尾に行われます）。
- **2D アクター（Actor2D）の注意**: 構築時に Transform が CanvasTransform へ差し替わるため、生成直後の 3D Position 設定は反映されません。位置は翌フレーム以降に `CanvasTransform.Position` で設定してください。
- 破棄済み GameObject への読み取りは既定値、書き込みは無視されます（クラッシュしません）。
- `Find` は**シーン全体**が対象なので、同名アクタ（同じプレハブを複数並べた場合など）では意図しないものを引きます。自分の配下を探すときは必ず `FindChild` を使ってください。`FindChild` は**シーン全体へフォールバックしません**（見つからなければ `IsValid == false`）。パスの照合規則は参照フィールドのパス指定と同じで、2D フォルダノードは透過します（第 7 節「参照文字列のパス指定」）。
- 現時点の制限: Play 開始後に生成・破棄したアクターの**物理コライダーは物理スレッドに反映されません**。コライダーの収集は Play 開始時（およびシーン遷移時）の一括処理で、`Instantiate` / `Destroy` は物理側の追加・除去を行わないためです。衝突・トリガーの**イベント通知**自体は実装済みですが（第 2 節）、実行時に生成したアクターはその対象になりません。

### 階層操作（親を指定した生成 / SetParent / Parent）

```csharp
// 親を指定して生成する（生成されたアクターは parent の「末尾の子」になる）
var parent = SEED.GameObject.Find("Canvas");
var icon   = SEED.GameObject.Instantiate("assets://actors/Icon.actor", parent);

// 親を付け替える（フレーム末尾に遅延適用。Destroy と同じモデル）
icon.SetParent(parent);                 // parent の末尾の子へ移動
icon.SetParent(null);                   // シーンのルート（トップレベル）へ移動

// 現在の親を取得する（ルート直下なら IsValid == false）
SEED.GameObject p = icon.Parent;
if (p.IsValid) { SEED.Debug.Log("親あり"); }
```

| 項目 | 挙動 |
| --- | --- |
| `Instantiate(path, parent)` | 構築したアクターを `parent` の**末尾の子**として追加。親が無効なら**ルート直下へフォールバック**し `[Script]` 警告 |
| `SetParent(parent)` | `parent` の**末尾の子**へ移動（遅延適用。`Instantiate` / `Destroy` と発行順） |
| `SetParent(null)` | シーンのルート（トップレベル）へ移動 |
| `Parent` | 現在の親。ルート直下なら `IsValid == false` の GameObject |
| 拒否 — 循環 | 自分自身・自分の子孫を親に指定した場合は**何もしない**（`[Script]` 警告） |
| 拒否 — 不在 | 対象または親がシーンに存在しない（破棄済み）場合は**何もしない** |
| 拒否 — 種別 | 3D アクターを 2D アクターの子にはできない／2D アクターは Canvas を持たない 3D アクターの子にできない |
| 3D の変換 | `Transform` は**ワールド空間**で保持されるため、付け替えても**ワールド位置・回転・スケールは変わらない** |
| 2D の変換 | `CanvasTransform` は**親相対**。ローカル値はそのまま維持されるので、**画面上の位置は新しい親を基準に変わる** |

> **重要**: `SetParent` は遅延適用（フレーム末尾）です。呼んだ直後に `Parent` を読んでも**まだ古い親**が返ります。判定は翌フレーム以降に行ってください。拒否された場合もツリーは一切変化せず、例外も戻り値も出ません（Play 中のログに `[Script] SetParent 拒否: …` が出ます）。

```csharp
// 例: UI アイコンのプーリング（N 個をキャンバス配下へ生成して使い回す）
using SEED;
using SEEDEditor.Scripting;

public class IconPool : SEEDScript
{
    [SerializeField] string iconActor = "assets://actors/ui/Icon.actor";
    [SerializeField] int    poolSize  = 16;

    GameObject[] _pool;   // 生成済みアイコン（親 = このアクター）
    int          _used;   // 今フレームで使った個数

    public override void OnStart()
    {
        // 自分（Canvas 配下の UI アクター）の子として、まとめて作り置きする
        _pool = new GameObject[poolSize];
        for (int i = 0; i < poolSize; i++)
        {
            _pool[i] = GameObject.Instantiate(iconActor, gameObject);
        }
    }

    /// <summary>1 個借りて表示位置を設定する。足りなければ IsValid=false が返る。</summary>
    public GameObject Rent(Vector2 pos)
    {
        if (_used >= _pool.Length) return default;
        var go = _pool[_used++];
        if (go.GetComponent<CanvasTransform>() is { } ct) ct.Position = pos;   // 親相対の座標
        if (go.GetComponent<Sprite>() is { } sp) sp.Color = Color.White;       // 表示に戻す
        return go;
    }

    /// <summary>全部返却する（破棄せず隠すだけ。Destroy/Instantiate を繰り返さない）。</summary>
    public void ReturnAll()
    {
        for (int i = 0; i < _used; i++)
        {
            if (_pool[i].GetComponent<Sprite>() is { } sp) sp.Color = new Color(1f, 1f, 1f, 0f);
        }
        _used = 0;
    }
}
```

> **重要 — プーリングの要点**: 使い終わったアイコンは `Destroy` せず、透明化（`Sprite.Color` のアルファを 0 にする）や画面外への退避で「隠す」だけにします。`Instantiate` / `Destroy` はどちらもフレーム末尾の遅延適用で、毎フレーム作り直すとアクター構築（ファイル読み込み・ECS 挿入）のコストがそのまま積み上がるためです。2D アイコンの親は **Canvas を持つアクター**（または 2D アクター）である必要があります。

### Transform（3D 位置・回転・スケール）

```csharp
transform.Position         // Vector3（get/set）
transform.WorldPosition    // Vector3（get のみ。ワールド絶対座標 = Position と同値）
transform.Rotation         // Vector3（get/set。YXZ オイラー角・度）
transform.Scale            // Vector3（get/set）
transform.Teleport(pos)    // void: 衝突を無視して pos へ瞬間移動（キャラクターコントローラー用）
transform.GameObject       // GameObject（この Transform を持つアクタ。参照フィールドで受けた Transform から他のコンポーネントを辿るときに使う）

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

// 例: [SerializeField] で受けた Transform 参照から、そのアクタの別コンポーネントを辿る
if (target.GameObject.GetComponent<SEED.Camera>() is { } cam) { float fov = cam.FieldOfView; }
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
                           //   ※ Text へは Text.BoxWidth > 0（枠あり）のときだけ効く
    ct.Anchor              // Vector2（get/set。親 Canvas 内の position 基準点。(0,0)=左上 (1,1)=右下）
    ct.ScreenPosition      // Vector2（get のみ。ウィンドウ左上原点のスクリーン座標・ピクセル）
    ct.GameObject          // GameObject（この CanvasTransform を持つアクタ。参照フィールドで受けた CanvasTransform から他のコンポーネントを辿るときに使う）

    // ── レイアウトの結果（get のみ。前のフレームの描画のレイアウトの表。2026-09-29）──
    ct.HasLayout           // bool（前のフレームの描画のレイアウトの表にこのノードがあったか。false なら下の 2 つは Zero）
    ct.LayoutSize          // Vector2（レイアウトが決めたこのノードの大きさ。このノードのキャンバスの単位＝Sprite.Width/Height と同じ。dp のキャンバスなら dp）
    ct.LayoutRect          // Rect（その矩形の画面の上の外接矩形。画素・左上原点・Y 下向き＝ScreenPosition・Screen.SafeArea・Input.MousePos と同じ。dp は ÷ Screen.DpScale）
}

// 例: コンテナ（CanvasStack の Stretch・flex・親に合わせる）に伸ばされたノードの、描かれる大きさで描き直す
if (gameObject.GetComponent<CanvasTransform>() is { } node && node.HasLayout)
{
    Vector2 size = node.LayoutSize;   // Sprite.Width は元の値のまま。伸ばされた幅はこちら
    Rect onScreen = node.LayoutRect;  // 画面の画素（タップ位置 Input.MousePos と比べられる）
}
```

> `Position` は**親 Canvas 相対**の座標ですが、`ScreenPosition` はアンカー・スケールモード・親チェーンをすべて反映した**画面上の絶対位置**（ピボット点）を返します。SEED の 3D `Transform.Position` は元々ワールド絶対座標で、書き込み時に子孫へ差分が伝播します（上記「親子の追従」参照）。

> **重要**: `HasLayout` / `LayoutSize` / `LayoutRect` は **1 フレーム遅れ**です。レイアウトの表はスクリプトのフェーズの後（描画）で作るので、`Update` などで読む値は**前のフレームの描画**の値です（`CanvasScroll.ViewportSize` と同じ）。このフレームに `Position`・レイアウトの部品を書き換えても、結果が読めるのは次のフレームからです（レイアウトが大きさを決めていない軸の `LayoutSize` だけは、読んだ時点の `Sprite.Width` / `Height`・Text の枠そのもの）。`HasLayout` が false になるのは、まだ描画していないとき（Play の最初のフレーム・シーンを読み込んで最初の描画の前・このフレームに `Instantiate` したノード）と、表に無いノード（3D ワールドキャンバスや 3D アクターの下・フォルダ）です。非表示・非アクティブのノードも表にあるので true です。読むのは Play（エディタの Play・SEED.exe）のゲームの画面の表だけです。

> **重要**: `LayoutSize` の決め方（軸ごと）: CanvasComponent を持つノードは**キャンバス領域**（コンテナ・親に合わせる・安全領域・中身に合わせる・dp のルートを反映）。持たないノードは、レイアウトが大きさを決めた軸（コンテナが伸ばした・セルいっぱい・親に合わせた）ならその大きさ、それ以外は Sprite の大きさ → Text の枠 → レイアウトが割り当てた矩形 → 0 です。Sprite を持つノードでは **`LayoutSize` ＝ 描かれるスプライトの大きさ**（キャンバスの単位）で、`LayoutRect` は描かれるスプライトの 4 隅（自分の回転・Scale・pivot を含む。回転していれば外接矩形）と一致します。コンテナに伸ばされても `Sprite.Width` / `Height` は元の値のままなので、描く大きさには `LayoutSize` を使ってください。

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
    model.RayTracingExcluded // bool（get/set。既定 false。true でレイトレの BLAS/TLAS から除外）

    model.LocalBoundsMin   // Vector3（get のみ。モデルローカル AABB の最小側）
    model.LocalBoundsMax   // Vector3（get のみ。モデルローカル AABB の最大側）
    model.LocalBoundsSize  // Vector3（get のみ。Max − Min ＝ 各軸の長さ）

    // 例: 座標は追従させたまま見た目だけ隠す（画面外へ退避させる必要はない）
    model.Visible = false;

    // 例: 大量に出す小物・魚をレイトレ（影・反射）の計算対象から外して負荷を下げる
    model.RayTracingExcluded = true;

    // 例: 釣り竿の持ち手を手の位置へ合わせる
    model.OffsetPosition = new Vector3(0f, -0.15f, 0.4f);
    model.OffsetRotation = new Vector3(0f, 0f, 25f);
}
```

> **重要**: `Visible = false` は**描画だけ**を止めます。Transform・親子の追従・JointAttach のソケット追従・コライダー・スクリプトは通常どおり更新され続けるので、子アクタをカメラの注視点にしているような「見えないが位置は正しくいてほしい」オブジェクトを安全に隠せます（非表示中は影も落とさず、選択アウトラインも出ません）。

> **重要**: `RayTracingExcluded = true` はレイトレ経路（レイトレ影・反射・GI・AO・トランスルーセンシー）からだけ外します。通常のラスタ描画・ラスタのシャドウマップ・クリック選択・アウトラインは従来どおりです（画面には映るが、他の物体への映り込みには出なくなります）。スキンモデルは 1 体ごとに毎フレーム BLAS を作り直すため、魚の群れのように数が多く映り込みへの寄与が小さい対象を外すと効果が大きく、除外したインスタンスはレイトレの静止判定にも入らないので動き続けても再構築を誘発しません。エディタのインスペクタでは Model の「レイトレ対象外」チェックが同じ値です。

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

    // ── 形と塗り（W2-4。長さはキャンバスの単位＝Width と同じ。docs/ui_components.md）──
    sprite.Shape           // SpriteShapeKind（get/set。Rect=0〈既定〉/ Ellipse=1〈内接する楕円・正方形なら円〉/ Arc=2〈弧・リング〉）
    sprite.CornerRadii     // CornerRadii（get/set。四隅の角丸 new CornerRadii(左上, 右上, 右下, 左下) / CornerRadii.All(r)。Rect のとき）
    sprite.CornerRadius    // float（get/set。四隅を同じ半径に。読むと左上）
    sprite.BorderWidth     // float（get/set。縁の太さ。0 = 縁なし。形の内側に引く）
    sprite.BorderColor     // Color（get/set。縁の色。Color〈塗り〉とは独立＝塗りを透明にしても縁は見える）
    sprite.ArcStart        // float（get/set。弧の開始角〈度〉。0 = +X・時計回り。既定 -90 = 真上）
    sprite.ArcSweep        // float（get/set。弧の角度〈度〉。0〜360。進捗の輪なら 値 × 360）
    sprite.ArcThickness    // float（get/set。弧の太さ）
    sprite.ArcRoundCaps    // bool（get/set。弧の端を丸く）
    sprite.Fill            // SpriteFillKind（get/set。Solid=0〈既定・Color で塗る〉/ Linear=1 / Radial=2）
    sprite.GradientColors  // Color[]（get/set。2〜4 色。Color が掛かる。範囲外の数の代入は無視）
    sprite.GradientStops   // float[]（get/set。色の位置 0..1・昇順。空 = 等間隔）
    sprite.GradientAngle   // float（get/set。線形の角度〈度〉。0 = 左→右、90 = 上→下〈既定〉）
    sprite.RadialCenter    // Vector2（get/set。放射の中心〈矩形に対する割合〉。既定 (0.5, 0.5)）
    sprite.RadialRadius    // Vector2（get/set。放射の半径〈幅・高さに対する割合〉。既定 (0.5, 0.5) = 内接する楕円）
    sprite.SetGradient(SpriteFillKind.Linear, c0, c1, c2)   // 種類と 2〜4 色をまとめて（色の位置は等間隔へ）
    sprite.NineSlice       // bool（get/set。画像を 9 スライスで描く。テクスチャがあるときだけ効く）
    sprite.NineSliceBorder // NineSliceBorder（get/set。枠の 4 辺 new NineSliceBorder(左, 上, 右, 下)。テクスチャの画素）
    sprite.NineSliceScale  // float（get/set。描く枠の倍率〈キャンバスの単位 ÷ テクスチャの画素〉。既定 1）
    sprite.NineSliceEdgeMode   // NineSliceMode（get/set。辺: Stretch=0 / Repeat=1〈回数を丸めて端で切れない〉）
    sprite.NineSliceCenterMode // NineSliceMode（get/set。中央）
    sprite.NineSliceFillCenter // bool（get/set。中央を描くか。false = 枠だけ）
    sprite.Shadow          // bool（get/set。ぼかしの影を形の後ろに描く）
    sprite.ShadowColor     // Color（get/set。影の色。Color とは独立）
    sprite.ShadowOffset    // Vector2（get/set。影のずれ〈X 右・Y 下〉）
    sprite.ShadowBlur      // float（get/set。ぼかしの幅。0 = くっきり）

    // 例: 角丸のカード（半径 16・枠 1・影）と、上 → 下の 2 色のグラデーション
    sprite.CornerRadius = 16f; sprite.BorderWidth = 1f; sprite.Shadow = true;
    sprite.SetGradient(SpriteFillKind.Linear, new SEED.Color(0.5f, 0.3f, 1f), new SEED.Color(0f, 0.7f, 0.8f));
}
```

> **重要**: 形と塗りの欄がすべて既定（直角の矩形・縁なし・単色・9 スライスなし・影なし）のスプライトは、従来とまったく同じ描き方（同じ画素）です。角丸・楕円・弧の外は描かれず、**押せません**（ポインタイベント・ジェスチャーの当たり判定も形に合わせる。円のボタンの角は当たらない）。長さはキャンバスの単位（dp のルートなら dp）で、端末の倍率に依らず同じ形です。

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

    // ワールド座標 → 画面座標の変換
    cam.WorldToScreen(worldPos)   // Vector3: x,y = 画面左上原点のピクセル / z = カメラ前方距離
    cam.WorldToCanvas(worldPos)   // Vector2: 画面中央原点・Y 下向き・1 単位 1px（キャンバス座標）
}
```

- `Projection = "orthographic"` で平行投影（遠近感なし）。縦 `OrthoHeight`・横 `OrthoHeight × アスペクト比` の範囲を写します。透視投影時は `FieldOfView` を使用します。

#### WorldToScreen / WorldToCanvas（ワールド座標 → 画面座標）

`WorldToScreen` の `x` / `y` は**ゲーム画面左上を原点とするピクセル**（右が +X・下が +Y）で、`Input.MousePos` と同じ座標系です。レターボックス／ピラーボックスの帯も考慮した「実際に描かれている位置」を返します。
`z` は**カメラ前方距離**（ワールド単位）で、**正ならカメラの前方・負なら背後**です。背後の点は `x` / `y` が画面内に見える値になることがあるため、可視判定には必ず `z > 0` を使ってください。

`WorldToCanvas` は**画面中央が原点・Y 下向き・1 単位 1px** のキャンバス座標を返します。`Input.MousePositionCanvas` および `CanvasTransform.Position` と同じ座標系なので、返り値をそのまま 2D アクターの位置に代入できます。

メインカメラ以外のカメラでも使えます（ハンドルが指すカメラで計算します）。エディタ埋め込み Play・ウィンドウ Play のどちらでも同じ基準になります。

```csharp
// 敵の頭上に HP バー（2D アクター）を追従させる
[SerializeField] SEED.GameObject? cameraActor;   // Camera を持つアクター
[SerializeField] SEED.GameObject? hpBar;         // CanvasTransform を持つ 2D アクター

public override void LateUpdate(ref NativeFrameContext ctx)
{
    if (cameraActor?.GetComponent<SEED.Camera>() is not { } cam) return;
    if (hpBar?.GetComponent<SEED.CanvasTransform>() is not { } ct) return;

    var headWorld = transform.Position + SEED.Vector3.Up * 2f;   // 頭上 2m
    var screen    = cam.WorldToScreen(headWorld);

    // カメラ背後なら隠す（z <= 0 の x,y は意味を持たない）
    if (screen.z <= 0f) { ct.Scale = SEED.Vector2.Zero; return; }

    ct.Scale    = SEED.Vector2.One;
    ct.Position = cam.WorldToCanvas(headWorld);   // キャンバス座標へそのまま代入
}
```

### AudioSource（アクター紐づけの音源。3D 距離減衰・パン対応）

エディタの「コンポーネント追加 → サウンド → Audio Source」で追加し、インスペクタで設定します。

```csharp
if (gameObject.GetComponent<AudioSource>() is { } audio)   // AudioSource?（未アタッチは null）
{
    audio.Play();          // 設定された音源を再生（再生中なら鳴らし直し）
    audio.Stop();          // 停止
    audio.IsPlaying        // bool: 再生中か

    audio.Path             // string（get/set。assets:// 仮想パス）
    audio.DictionaryKey    // string（get/set。"グループ/用途"。空=Path を直接使う）
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
- `DictionaryKey` が空でないときは**パスも音量も音声辞書から解決**されます（`Path` と `Volume` は無視）。インスペクタでは音源欄を「辞書のキー」に切り替え、AudioDictionary を持つアクターをドロップしてキーを選びます。

### AudioDictionary（音声辞書：グループ/用途キーで音を引く）

エディタの「コンポーネント追加 → サウンド → Audio Dictionary」で追加し、インスペクタで「グループ名 → 用途名 → 音声ファイル・既定音量」を登録します。**特定の辞書だけ**を名指しで引くときにこのハンドルを使います（シーン全体から引くなら `SEED.Audio.PlayDict`）。

```csharp
if (gameObject.GetComponent<AudioDictionary>() is { } dict)   // AudioDictionary?（未アタッチは null）
{
    dict.TryGetPath("Player/attack", out var path)  // bool: この辞書にキーがあり、パスが設定済みか
    dict.DefaultVolume("Player/attack")             // float: 行の既定音量（引けなければ 1.0）
    dict.Play("Player/attack");                     // 引いて再生（音量は辞書の既定値）
    dict.Play("Player/attack", 0.5f);               // 音量を明示して再生
}

// [SerializeField] で別アクターの辞書を差し込むこともできる
[SEEDEditor.Scripting.SerializeField] SEED.AudioDictionary? bank;
void Update() { if (bank is { IsValid: true } b) b.Play("UI/click"); }
```

- キーは `グループ名/用途名`（区切りは `/`）。グループ名・用途名・音声ファイルのどれかが空の行は**引けません**（作りかけの行を誤って鳴らさないため）。
- このハンドルは**その辞書 1 つだけ**を見ます。シーン内の他の辞書は引きません。

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
    ps.Layer               // int（get/set。**2D キャンバスアクター専用**の描画優先度）
    ps.Tint                // Color（get/set。粒の色味。**アルファは無視**）
}
```

- `Burst(n)` の放出リクエストは蓄積され、次フレームで GPU パーティクルシステムが消費します（`emit_rate` による継続放出とは別枠）。`n` が 0 以下なら何もしません。
- **`Burst(n)` は `playing` / `emit_mode` に関わらず放出されます**（明示要求のため上限を適用しない）。つまり「インスペクタでは `playing = false`／`emit_mode = once` にしておき、スクリプトの `Burst` でだけ弾く」という作り方ができます。ただし**非表示（`Visible = false`）のサブツリーにあるエミッタは収集されない**ので、その間の `Burst` 要求は消費されずに溜まり、**表示に戻した最初のフレームでまとめて放出**されます（＝表示へ切り替えた直後に積んでよい）。
- `Tint` は色カーブ（HSVA）の**色相・彩度・明度を 1 色で塗り替える**もので、**アルファ（＝消え方）のカーブは残します**。取得は先頭の色カーブの寿命先頭（t=0）の色（アルファは常に 1）。設定は**保持している色カーブすべて**に効きます。レベル色やランク色のように「実行時に決まる色」で放出したいときに使ってください。色カーブそのものを書き換える処理なので、放出の直前に 1 回だけ塗る使い方を想定しています（毎フレーム代入する用途には向きません）。

#### 2D キャンバス（UI）のパーティクル

`ParticleEmitter` は **3D アクター（Transform 持ち）と 2D キャンバスアクター（CanvasTransform 持ち）の両方**に付けられます。アクターが 2D のとき、エミッタは自動的に**キャンバス空間**で動きます（コンポーネントは共通で、専用の 2D 用コンポーネントはありません）。

| 項目 | 3D アクター | 2D キャンバスアクター |
| --- | --- | --- |
| 原点 | Actor の `Transform` | Actor の `CanvasTransform`（子スプライトとまったく同じ基準） |
| 座標・サイズの単位 | ワールドユニット（m） | **px** |
| 初速 / 重力 | m/s・m/s² | **px/s・px/s²** |
| Y 軸の向き | +Y が上 | **+Y が下**（キャンバス規約。落下させたいなら `gravity = [0, +N, 0]`） |
| Z 方向 | 自由 | **常に 0 に拘束**（正射影のため画面に現れず、クリップ面で消えるだけなので落とす） |
| `sim_space` | world / local を選択 | **常に local**（インスペクタでも項目を出しません） |
| 前後関係 | 深度バッファ | **`Layer`**（スプライト／テキストと同じレイヤー空間） |

- `Layer` は `Sprite.Layer` / `Text.Layer` と**同じソート軸**です。同一 `layer` 内の種別順は `スプライト → プリミティブ → パーティクル → テキスト`（§7.8 参照）。
- 形状は `plane`（板ポリ）を推奨します。`sphere` / `box` は 3D 形状なので UI では潰れて見えます。`pixel` は常に 1px です。2D では粒子の回転は **Z 軸まわりの面内回転**に限定されます（スプライトの回転と同じ意味）。
- サイズは「`size_range` × スケールカーブ」がそのまま **px** になります（`plane` の単位メッシュが 1×1 のため）。
- 描画ゾーン（背景／前面）はスプライトと同じく**ルートキャンバスの `draw_zone` を継承**します。フォルダノードは透過なので、フォルダで括っても座標・レイヤーは変わりません。
- サンプル: `assets://mainGame/actors/FX/Sparkle2D.actor`（白い小片を 24 個バースト放出する 2D エミッタ）。

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
    label.IconSet        // string（get/set。アイコンセット .icons の assets:// パス。空文字=未使用）
    label.OutlineWidth   // float（get/set。縁取りの太さ px。0=縁取りなし）
    label.OutlineColor   // Color（get/set。縁取りの色。既定=不透明な黒）

    // 枠（テキストボックス）と自動折り返し
    label.BoxWidth       // float（get/set。枠の幅 px。0=枠なし＝従来レイアウト）
                         //   ※ エディタで Text を新規追加したときの初期値は 300（高さ 60）。
                         //     フィールドを持たない旧シーンは 0（＝枠なし・自動レイアウト）のまま。
    label.BoxHeight      // float（get/set。枠の最小高さ px。実高さ=max(この値, 内容の高さ)）
    label.Wrap           // bool（get/set。枠幅で自動折り返し。BoxWidth=0 なら無視）

    // 太さとドロップシャドウ
    label.Weight         // float（get/set。文字の太さ px。負=細く / 正=太く / 0=フォント本来）
    label.ShadowOffset   // Vector2（get/set。影のずらし量 px。X 右・Y 下。(0,0)=影なし）
    label.ShadowColor    // Color（get/set。影の色。既定=半透明の黒）
    label.ShadowSoftness // float（get/set。影のぼかし幅 px。0=シャープ）

    // プレースホルダ記法（{image} {color} {string} {num}）の差し込みスロット
    label.SlotCount               // int（get のみ。本文の記法が決めるスロット件数）
    label.SetSlotImage(0, "assets://ui/coin.png"); // {image} の画像（アイコン名でも可）
    label.GetSlotImage(0)         // string
    label.SetSlotColor(1, SEED.Color.Red);         // {color} の色
    label.GetSlotColor(1)         // Color
    label.SetSlotText(2, "勇者");                   // {string} の文字列
    label.GetSlotText(2)          // string
    label.SetSlotNumber(3, 1234.5f);               // {num} の数値
    label.GetSlotNumber(3)        // float
}
```

```csharp
// 例: 会話ウィンドウの本文（枠に収めて自動折り返し・影つき）
if (gameObject.GetComponent<Text>() is { } body)
{
    body.BoxWidth       = 640f;   // 枠を与えると折り返しとピボットが有効になる
    body.BoxHeight      = 160f;   // 最小高さ（内容が増えれば下へ伸びる）
    body.Wrap           = true;
    body.Align          = "left"; // 枠あり: 「枠の中での配置」を意味する
    body.VerticalAlign  = "top";
    body.Weight         = 0.5f;   // ほんの少し太らせて可読性を上げる
    body.ShadowOffset   = new SEED.Vector2(2f, 2f);
    body.ShadowSoftness = 1f;
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

> **重要 — `BoxWidth` の 0 か正かでレイアウトの意味が変わります**。`BoxWidth = 0`（枠なし・既定）では従来どおり `Align` / `VerticalAlign` は「アクターの位置に対してテキストブロックをどう置くか」を意味し、自動折り返しも `CanvasTransform.Pivot` も効きません。`BoxWidth > 0`（枠あり）にすると、枠のローカル矩形は Sprite と同じ「左上原点・[0,W]×[0,H]」になり、`Align` / `VerticalAlign` は「枠の中での配置」、`Pivot` は Sprite とまったく同じ意味（枠サイズに対する正規化基準点）で効きます。既存シーンは `BoxWidth` が 0 のままなので見た目は変わりません（フィールドを持たない旧 `.scene` は 0 ＝ 自動レイアウトで読み込まれます）。なお**エディタから Text コンポーネントを新規追加した場合の初期値は 300×60**（追加直後から枠が見えて選択・リサイズできるようにするため）で、`Wrap` の既定が true のため **300px で自動折り返しします**。折り返したくない場合は `Wrap` を false にするか `BoxWidth` を 0 に戻してください。

> **重要 — 折り返しの規則**: 英数字は単語単位（`'` と `-` は単語の一部として扱う）、日本語などは 1 文字単位で折ります。行末の空白は幅に数えず、次の行頭へは送りません。1 単語が枠幅より長い場合は文字単位で強制分割します。簡易禁則として、句読点・閉じ括弧・長音・小書き仮名（`、。・？！」）】ー` など）は行頭に来ないよう**最大 2 文字まで前の行末へぶら下げ**、開き括弧（`「（【` など）が行末に来た場合は次の行頭へ追い出します。

> **重要 — 太さと影**: `Weight` は SDF のしきい値をずらして太さを変えるため、縁取りと同じくフォントサイズの約 1/8 で頭打ちになります。影は本体と同じ字形を `ShadowOffset` だけずらして本体の下へ描くもので、`ShadowOffset` が (0,0) か `ShadowColor` のアルファが 0 のときは描かれません（＝コストもかかりません）。

#### インライン画像（本文に画像を 1 文字として混ぜる）

本文（`Content`）の中に次の記法を書くと、その位置に画像が **1 文字として** 並びます。折り返し・整列・枠・ピボット・影といった通常の文字と同じ規則がそのまま効きます。

| 記法 | 意味 |
| --- | --- |
| `[icon:名前]` | `IconSet` に指定した `.icons` の表で名前 → 画像パスを引く |
| `[img:assets://path/to.png]` | 画像パスを直接指定する |
| `[icon:名前 h=1.4]` / `[img:... h=0.8]` | 高さ倍率（画像の高さ = フォントサイズ × h。既定 1.0）。幅は画像のアスペクト比から決まる |
| `\[` | 「[」そのものを描くエスケープ |

```csharp
if (gameObject.GetComponent<Text>() is { } hint)
{
    hint.IconSet = "assets://ui/keys.icons";
    hint.Content = "移動 [icon:key_w][icon:key_a][icon:key_s][icon:key_d] / "
                 + "決定 [icon:mouse_l h=1.2] / 直接指定 [img:assets://ui/coin.png]";
}
```

**アイコンセット（`.icons`）の形式** — 中身は JSON です。値は「パス文字列」か「`path` と既定倍率 `h` を持つオブジェクト」のどちらでも書けます。

```json
{
  "icons": {
    "key_w":   "assets://ui/key_w.png",
    "mouse_l": { "path": "assets://ui/mouse_l.png", "h": 1.2 }
  }
}
```

高さ倍率の優先順位は **記法の `h=` > `.icons` の `h` > 既定値 1.0** です。倍率は 0.05〜16.0 に丸められます。

> **重要 — 未解決・不正な記法のときの挙動**: アイコン名が `.icons` に無い、`IconSet` が未設定、画像ファイルが読めない、といった場合は **幅 1em（フォントサイズと同じ幅）の空白**になり、本文のレイアウトは崩れません。警告はその名前／パスにつき **1 回だけ** ログへ出ます。`[` で始まっても `icon:` / `img:` のどちらでもないもの（例 `[0]`、`[note:x]`）、対応する `]` が無いもの（例 `[icon:key_w`）は **記法とみなさず、そのまま通常の文字として描かれます**。

> **重要 — 制限**: (1) 画像に文字の太さ（`Weight`）は効きません（SDF のしきい値操作なので画像には適用できません）。(2) 影は画像にも同じオフセットで落ちますが、`ShadowSoftness`（ぼかし）は画像には効きません（スプライトとして描くため）。(3) 画像は `Color` で着色されません（アイコン本来の色で出ます）が、`Color` のアルファには追従します。(4) 縦書きには対応していません。(5) 画像も 1 つにつき 1 文字として数えるため、4096 文字の上限に含まれます（上限で切れても記法の途中で壊れることはありません）。(6) 画像は本文のグリフと同じレイヤー値で、スプライトとして描かれます（同一レイヤー内ではグリフより 1 段奥になりますが、文字と画像は重ならないため見た目には現れません）。

#### プレースホルダ記法（`{image}` / `{color}` / `{string}` / `{num}`）

本文（`Content`）に「差し込み口」を置く記法です。差し込む値そのものは本文ではなく**スロット配列**が持ち、インスペクタかスクリプト（`SetSlotXxx`）、あるいは**バインド**（他のアクターのコンポーネント／スクリプトの値を毎フレーム引いてくる仕組み）で決めます。

| 記法 | 意味 |
| --- | --- |
| `{image}` | 画像を 1 文字ぶん差し込む（`[img:]` / `[icon:]` と同じ解決規則） |
| `{image h=1.4}` | 高さ倍率つき（画像の高さ = フォントサイズ × h。既定 1.0） |
| `{color}` | ここから先の文字色をスロットの色へ切り替える |
| `{/color}` | 色区間の終わり（省略時は行末＝次の改行の直前まで） |
| `{string}` | 文字列を差し込む |
| `{num}` | 数値を整数（四捨五入）で差し込む |
| `{num.3}` | 数値を小数 3 桁で差し込む |
| `{image:1}` / `{num:2}` | **番号指定**（既にあるスロット 1 番 / 2 番を参照する） |
| `{num:2.3}` | 番号 + 書式（スロット 2 番を小数 3 桁で） |
| `\{` | 「{」そのものを描くエスケープ |

**番号の決まり** — 通し番号カウンタは記法が出るたびに必ず 1 進みます（明示番号を書いても進みます）。番号を書かなければそのときのカウンタ値が添字です。スロット配列の長さは `max(記法の出現数, 明示番号の最大 + 1)` になります。

**既知キーワード以外は通常文字** — `{` で始まっても `image` / `color` / `/color` / `string` / `num` のいずれでもない場合（例 `{0}`、`{name}`）、および対応する `}` が無い場合は、**波括弧ごとそのまま描かれます**。プログラム的な `{}` を含む説明文の見た目は 1 文字も変わりません。

**スロットの保存形式** — 1 件につき「種類 / 画像パス / RGBA / バインド先 / フォールバック文字列 / フォールバック数値」を持ちます。本文を書き換えると記法に合わせて再マッピングされ、**種類が一致するスロットの値だけが引き継がれます**（種類が変わった位置は既定値へ戻ります）。

**インスペクタでの設定** — Text コンポーネントのインスペクタに、本文の記法から作られたスロットの行が並びます。各行で値（画像 / 色 / 文字列 / 数値）を直接入力するか、「バインド先」に **アクター → コンポーネント → 変数** を選びます。バインドが解決できない行には ⚠ が出ます。

**バインド候補になる条件** — バインドの供給元は次の 2 つで、**要求される型と厳密一致**したものだけが候補に出ます（`{num}` は `f32`、`{string}` は `str`）。

| 供給元 | 候補になる変数 |
| --- | --- |
| エンジン組込コンポーネント | `Transform.position` / `Transform.scale`（vec3）、`CanvasTransform.rotation`、`Light.intensity` / `Light.color`、`WaterVolume.wave_amplitude` / `surface_height` / `shallow_color` / `deep_color`、`Text.font_size`、`Sprite.layer` |
| C# スクリプト | `[Bindable]` を付けた **フィールド**（`[SerializeField]` 併用が必須）・**プロパティ**（get 必須）・**引数なしメソッド**。型は `float` / `int`（→ `{num}`）と `string`（→ `{string}`） |

> **重要 — 成分の部分取り出しはできません**。`Transform.position` は `vec3` なので `{num}` へは繋がりません。「X 座標だけ表示したい」場合は、スクリプトに 1 行足してスカラーとして公開してください。

```csharp
// Transform の X 成分を {num} へ流すための書き方（プロパティなので [SerializeField] 不要）
[Bindable] public float PosX => transform.Position.x;

// int も {num} に繋がる（float へ変換されます）
[SerializeField, Bindable] private int hp = 100;

// 文字列は {string} に繋がる
[Bindable] public string PlayerName => saveName;
```

> **重要 — `[Bindable]` のプロパティ・メソッドは毎フレーム呼ばれます**。カウンタを進める・オブジェクトを生成する・ログを出すといった**副作用を書かないでください**（読むだけの純粋な計算にすること）。

> **重要 — Edit 中（Play していないとき）はフォールバック値が出ます**。スクリプトのインスタンスは Play 中にしか存在しないため、エディタで編集しているあいだスクリプトへのバインドは解決されず、スロットに入力した値（フォールバック値）がそのまま表示されます。組込コンポーネント（`Light.intensity` など）へのバインドは Edit 中でも解決されます。

**差し込みの上限と数値の書式** — 1 スロットが差し込める文字数は **256 文字**までで、超えた分は切り捨てられます（本文全体の 4096 文字上限とは別に、先に 1 件単位で切ります）。数値は **half-away-from-zero（0.5 は絶対値が大きい側へ）** で丸めてから桁を固定します（`{num}` で 2.5 → `3`、`{num.1}` で 0.25 → `0.3`）。`NaN` は `NaN`、無限大は `∞` / `-∞`、負のゼロは `0` として表示されます。

**スクリプトからスロットを触る** — 添字は本文の登場順（0 始まり）です。範囲外の添字はゲッターが既定値を返し、セッターは無視されます。

```csharp
if (gameObject.GetComponent<Text>() is { } label)
{
    label.SlotCount              // int（get のみ。本文の記法が決めるスロット件数）
    label.SetSlotImage(0, "assets://ui/coin.png"); // {image} の画像（アイコン名でも可）
    label.GetSlotImage(0)        // string
    label.SetSlotColor(1, SEED.Color.Red);         // {color} の色
    label.GetSlotColor(1)        // Color
    label.SetSlotText(2, "勇者");                   // {string} の文字列
    label.GetSlotText(2)         // string
    label.SetSlotNumber(3, 1234.5f);               // {num} の数値
    label.GetSlotNumber(3)       // float
}
```

```csharp
// 例: "[画像]所持金 1,234 円" のような HUD（本文は 1 回だけ、値は毎フレーム）
public void OnStart()
{
    if (gameObject.GetComponent<Text>() is { } hud)
    {
        hud.Content = "{image h=1.2} 所持金 {color}{num}{/color} 円";
        hud.SetSlotImage(0, "assets://ui/coin.png");
        hud.SetSlotColor(1, new SEED.Color(1f, 0.9f, 0.2f, 1f));
    }
}

public void Update()
{
    if (gameObject.GetComponent<Text>() is { } hud)
        hud.SetSlotNumber(2, money);   // 3 番目の記法 {num} が添字 2
}
```

> **重要 — バインドが設定されているスロットでは、`SetSlotText` / `SetSlotNumber` の値はフォールバックになります**（バインドが解決できたらそちらが優先されます）。スクリプトから毎フレーム値を入れるなら、そのスロットのバインドは空のままにしてください。

### CanvasClip（子を切り抜く：スクロール領域・一覧の枠）

2D キャンバスのノードに **CanvasClip**（インスペクタの「コンポーネント追加 → UI → Canvas Clip」）を付けると、そのノードの矩形からはみ出した**子孫を描かず、押せなくします**（W2-1a）。矩形は CanvasComponent があればキャンバス領域、無ければ最初の有効な Sprite の矩形です。スクリプトからは有効・無効だけを切り替えます。

```csharp
if (gameObject.GetComponent<CanvasClip>() is { } clip)
{
    clip.Enabled        // bool（get/set。既定 true。false の間は子孫を切り抜かない。次のフレームの描画から効く）
    clip.IsValid        // bool（この参照が生きているか）
    clip.Shape          // ClipShape（get/set。Rect=0〈既定〉/ RoundedRect=1 / Ellipse=2 / SpriteShape=3〈最初の Sprite の角丸・楕円に合わせる〉。W2-4）
    clip.CornerRadii    // CornerRadii（get/set。Shape が RoundedRect のときの四隅の角丸）
}
gameObject.HasComponent("CanvasClip")   // 付いているか
```

> **重要**: 角丸・楕円の切り抜き（W2-4）は、**いちばん内側の 1 つだけ**を画素単位（シェーダーの SDF）で切り、外側の角丸・楕円の祖先は外接矩形で切ります。画素単位で切るのは**スプライト（画像・形）だけ**で、テキスト・`SEED.Draw` の図形・2D パーティクルは外接矩形で切ります。当たり判定も同じ形（丸い切り抜きの外は押せない）。丸いアイコンは「Sprite の Shape = Ellipse ＋画像」、角丸のカードは「Sprite の角丸 ＋ CanvasClip.Shape = SpriteShape」で作れます。

> **重要**: 切り抜くのは**子孫だけ**で、CanvasClip を付けたノード自身の Sprite（枠の背景の板）は切りません。スプライト・テキスト（インライン画像を含む）・2D パーティクル・`SEED.Draw` の図形（`space` に子孫の CanvasTransform を渡したもの）が切り抜かれ、ポインタイベント（`OnPointer*`）も切り抜かれて見えない所では届きません。

> **重要**: 入れ子にすると外側の枠との重なりで切ります（深さの上限なし）。回転したノードは 4 隅の外接矩形で切ります。3D ワールドキャンバス（Actor3D + Canvas）の配下と、`SEED.Draw` のスクリーンスペース（`space: null`）の図形は切り抜きません。スロットの見出しの有効・無効と `Enabled` の両方が有効のときだけ切り抜きます。

### CanvasStack・CanvasWrap・CanvasGrid・CanvasLayoutItem（レイアウトの部品：縦横の並び・折り返し・格子）

2D キャンバスのノードに**コンテナ**（インスペクタの「コンポーネント追加 → UI → Canvas Stack / Canvas Wrap / Canvas Grid」）を付けると、
子（フォルダの中の子も含む）の位置と大きさを**コンテナが決めます**（W2-1b）。子の `Anchor`・`Position` は使われません。
子の大きさは「自分の大きさ」（CanvasComponent → 最初の Sprite → Text の枠 → `CanvasLayoutItem.PreferredSize`）か、
「伸ばす」（`CanvasLayoutItem.Flex`・交差軸の `CrossAlign.Stretch`・Grid のセルいっぱい）です。伸ばした子の Sprite は矩形いっぱいに描かれます。
大きさ・間隔・余白は**キャンバスの単位**（Sprite の幅・高さと同じ。dp のルートなら dp）。変更は次のフレームのレイアウトから効きます。

```csharp
// 縦・横に 1 列に並べる
if (gameObject.GetComponent<CanvasStack>() is { } stack)
{
    stack.Enabled          // bool（get/set。既定 true。false の間は子が自分の Anchor・Position に戻る）
    stack.Direction        // LayoutDirection（get/set。Vertical（既定）/ Horizontal）
    stack.Spacing          // float（get/set。子の間隔）
    stack.Padding          // CanvasPadding（get/set。内側の余白。new CanvasPadding(左, 上, 右, 下) / CanvasPadding.All(8)）
    stack.MainAlign        // MainAlign（get/set。Start / Center / End / SpaceBetween / SpaceAround / SpaceEvenly）
    stack.CrossAlign       // CrossAlign（get/set。Start / Center / End / Stretch（交差軸いっぱい））
    stack.Reverse          // bool（get/set。逆順に並べる）
    stack.FitWidth         // bool（get/set。CanvasComponent を持つコンテナの幅を中身に合わせる）
    stack.FitHeight        // bool（get/set。同じく高さ。CanvasComponent が無いコンテナは常に中身の大きさ）
    stack.HiddenChildren   // HiddenChildren（get/set。Collapse（既定・詰める）/ KeepSpace（場所を残す））
    stack.IsValid          // bool（この参照が生きているか）
}

// 折り返して並べる（チップの 2 段など）
if (gameObject.GetComponent<CanvasWrap>() is { } wrap)
{
    wrap.Direction         // LayoutDirection（get/set。既定 Horizontal = 左から右へ並べて下へ折り返す）
    wrap.Spacing           // float（get/set。同じ行の子の間隔）
    wrap.RunSpacing        // float（get/set。行の間隔）
    wrap.Padding           // CanvasPadding（get/set）
    wrap.MainAlign         // MainAlign（get/set。行の中の揃え）
    wrap.CrossAlign        // CrossAlign（get/set。行の中の交差軸の揃え。Stretch は行の高さまで）
    wrap.RunAlign          // MainAlign（get/set。行の塊の揃え）
    wrap.Enabled / wrap.FitWidth / wrap.FitHeight / wrap.HiddenChildren   // Stack と同じ
}

// 格子に並べる（月の 3×4・庭の 8×6 など）
if (gameObject.GetComponent<CanvasGrid>() is { } grid)
{
    grid.Columns           // int（get/set。1 以上で固定、0 でセルの最小幅から自動。既定 3）
    grid.CellMinWidth      // float（get/set。自動の列数に使うセルの最小幅）
    grid.CellAspectRatio   // float（get/set。セルの幅 ÷ 高さ。1 = 正方形、0 以下 = 行の高さは中身）
    grid.Spacing           // Vector2（get/set。x = 列の間隔、y = 行の間隔）
    grid.Padding           // CanvasPadding（get/set）
    grid.CellAlign         // CrossAlign（get/set。セルの中の置き方。既定 Stretch = セルいっぱい）
    grid.Enabled / grid.FitWidth / grid.FitHeight / grid.HiddenChildren   // Stack と同じ
}

// コンテナの子の側の指定（付けなくても子は自分の大きさ・コンテナの揃えで並ぶ）
if (gameObject.GetComponent<CanvasLayoutItem>() is { } item)
{
    item.IgnoreLayout      // bool（get/set。コンテナに無視させる＝自分の Anchor・Position のまま）
    item.Flex              // float（get/set。CanvasStack の主軸の余りを分ける重み。0 = 伸ばさない）
    item.PreferredSize     // Vector2（get/set。大きさの指定。0 の軸は中身の大きさ）
    item.MinSize           // Vector2（get/set。大きさの下限。0 = なし）
    item.MaxSize           // Vector2（get/set。大きさの上限。0 = なし）
    item.AlignSelf         // ItemAlign（get/set。Auto（既定・コンテナに従う）/ Start / Center / End / Stretch）
    item.FillWidth         // bool（get/set。コンテナの外の子で使う。親の CanvasComponent の領域の幅いっぱい）
    item.FillHeight        // bool（get/set。同じく高さ）

    // 実行中だけの見た目の上書き（W2-7。保存しない・インスペクタに出ない。Play の開始・シーンの読み込みで 0 に戻る）
    item.Translate         // Vector2（get/set。置かれた後に足す平行移動。キャンバスの単位。レイアウト〈大きさ・並び・安全領域〉は変えない）
    item.TranslateFraction // Vector2（get/set。同じく自分の置かれた矩形の大きさに対する割合。(1, 0) で自分の幅だけ右＝画面の外から入る）
    item.LayerBias         // int（get/set。自分と子孫の表示〈Sprite・SkinnedSprite・Text・2D パーティクル〉のレイヤーに足す値。祖先と足し合わせる）
    item.VisualScale       // Vector2（get/set。既定 (1, 1)。W2 の手直し 3b。自分の置かれた矩形の中心の周りの倍率。子孫・当たり判定・切り抜き・LayoutRect も一緒に縮む。
                           //  レイアウト〈大きさ・並び・安全領域・LayoutSize〉は倍率の前のまま。Translate の後に掛かる。保存しない。Play の開始・シーンの読み込みで 1 に戻る）
}
```

> **重要（W2 の手直し 3b）**: `VisualScale` は保存される `CanvasTransform.Scale`（pivot の周り。入れ子のキャンバスの子は左上へ寄る）と違い、**矩形の中心の周り**に部分木ごと縮みます（入れ子のキャンバスの子も中心へ寄る）。既定 (1, 1) のノードのレイアウトの計算は一切変わりません。SEED.UI の予測型の戻るのプレビュー（§7.18）が画面の枠・ダイアログの札・シートの板に使います。ダイアログの出入りの動き（札の大きさ 0.9 ↔ 1）もこの欄です（W2 の手直し P2-1。以前は `CanvasTransform.Scale` で、文字とボタンが札の左上へ寄って縮んだ）。

> **重要（W2-7）**: `Translate`・`TranslateFraction` は親に合わせた（`FillWidth`/`FillHeight`）・コンテナが並べたノードも動かせます（`Position` は使われないため）。
> 子孫も一緒に動き、当たり判定・切り抜き・`ScreenPosition` も動いた位置になります。祖先のずらしは子孫の `CanvasSafeArea` の計算に入れません（横から入ってくる画面の箱が縮み直さない）。
> `LayerBias` は重なる画面（画面のスタック・ダイアログ・シート・トースト）の前後を中身のレイヤーに依らず決めるためのもので、描画の並び・ポインタの最前面・ジェスチャーの遮りが同じ値で比べます。`SEED.Draw` の図形にも `space` のノードの値が足されます（2026-09-28・W2-8 から。積んだ画面の中のグラフが背景の下に隠れない）。画面の組み立ての部品（§7.18）が使います。

> **重要**: 列挙の数値は固定です（`LayoutDirection` Vertical=0 / Horizontal=1、`MainAlign` Start=0〜SpaceEvenly=5、`CrossAlign` Start=0 / Center=1 / End=2 / Stretch=3、`ItemAlign` Auto=0〜Stretch=4、`HiddenChildren` Collapse=0 / KeepSpace=1）。範囲外の値の書き込みは無視されます。

> **重要**: コンテナの下の子は、自分の `CanvasTransform.Anchor`・`Position` より**コンテナの配置が優先**します（`Rotation`・`Scale` は矩形の pivot の周りに掛かる見た目の変化として残る）。コンテナから外して自由に置きたい子は `CanvasLayoutItem.IgnoreLayout = true`。CanvasTransform を持たない子（3D アクター）は並べません。Text の大きさは枠（BoxWidth/BoxHeight）か `PreferredSize` で決めます（文字の寸法の自動の計測は W2-6）。

### CanvasSafeArea（安全領域：切り欠き・ステータスバー・ジェスチャーバーを避ける）

CanvasComponent を持つ 2D キャンバスのノード（パネル）に **CanvasSafeArea**（「コンポーネント追加 → UI → Canvas Safe Area」）を付けると、
そのノードのキャンバス領域を `Screen.SafeArea` の内側へ縮めます（W2-1b）。子のアンカー・コンテナは縮めた領域を基準にします。
画面の回転とシステムバーの出し入れ（`SEED.Platform.Window.SetSystemBarsVisible`）に次のフレームから追従します。

```csharp
if (gameObject.GetComponent<CanvasSafeArea>() is { } safe)
{
    safe.Enabled           // bool（get/set。既定 true）
    safe.Left              // bool（get/set。左の辺を安全領域へ寄せる。既定 true）
    safe.Top               // bool（get/set。上の辺。ステータスバー・カメラの穴）
    safe.Right             // bool（get/set。右の辺）
    safe.Bottom            // bool（get/set。下の辺。ナビゲーションバー・ジェスチャーバー）
}
```

> **重要**: PC の Play とエディタでは安全領域は画面全体です（縮めない）。PC で確かめるときは環境変数 `SEED_SIM_SAFE_AREA=左,上,右,下`（画素）で模擬の切り欠きを出せます。Sprite の大きさは変わらないので、画面の端まで塗る背景は親のノード（`CanvasLayoutItem.FillWidth/FillHeight` で親いっぱい）に置いてください。

> **重要**: ルートキャンバスの寸法の単位（インスペクタの「寸法の単位」px / dp）を **dp** にすると、ルートの大きさは「画面 ÷ 1 dp の画素数」になり、子の位置・大きさ・余白は縦横同じ倍率で画素へ換算されます（auto_scale は使わない）。1 dp = `Screen.DPI` ÷ 基準 DPI（Android は densityDpi ÷ 160、PC は OS の表示スケール）。PC では環境変数 `SEED_SIM_SCALE_FACTOR`（例 2.625 = Pixel 6a）で端末の密度を模擬できます。

### CanvasGesture（ジェスチャーを受けるノード：ボタン・スクロール・スライダ・シートの入力）

2D キャンバスのノードに **CanvasGesture**（「コンポーネント追加 → UI → Canvas Gesture」）を付けると、そのノードが指ごとのジェスチャーアリーナに参加し、
同じアクターのスクリプトへ `OnGestureTap` などが届きます（§2「ジェスチャーコールバック」。W2-2）。当たり判定の形は CanvasComponent があればキャンバス領域、
無ければ最初の Sprite の矩形で、見た目が `MinHitSizeDp`（既定 48 dp）より小さければ中心をそろえて広げます。

```csharp
if (gameObject.GetComponent<CanvasGesture>() is { } g)
{
    g.Enabled         // bool（get/set。既定 true。false の間は当たり判定の候補にもならない＝後ろのノードへ届く）
    g.Tap             // bool（get/set。タップを受ける。既定 true）
    g.LongPress       // bool（get/set。長押しを受ける。既定 false）
    g.Drag            // bool（get/set。ドラッグを受ける。既定 false）
    g.Fling           // bool（get/set。フリックを受ける。既定 false。ドラッグを受けなくても指を取れる）
    g.DragAxis        // GestureDragAxis（get/set。Any / Horizontal / Vertical。既定 Any）
    g.Pinch           // bool（get/set。ピンチ〈2 本指の拡大縮小〉を受ける。既定 false。W2-8）
    g.PressFeedback   // bool（get/set。PressDown / PressCancel / PressUp を受ける。既定 true）
    g.MinHitSizeDp    // float（get/set。最小のヒット領域 dp。既定 48。0 = 広げない）
}

// 部品ごとの付け方（例）
// ボタン        … 既定のまま（Tap・PressFeedback）
// 縦スクロール  … Tap=false, Drag=true, Fling=true, DragAxis=Vertical（枠に CanvasClip）
// 左スワイプの行 … Tap=true, Drag=true, DragAxis=Horizontal（縦の一覧の中でも縦の動きは一覧へ譲る）
// スライダ      … Tap=true, Drag=true, DragAxis=Horizontal（e.LocalPosition.x で値を決める）
// グラフ        … Tap=true, LongPress=true, Drag=true, Fling=true, DragAxis=Horizontal, Pinch=true, PressFeedback=false（SEED.UI.LineChart / BarChart。§7.19）
// 子のつまみ    … Tap=false, Drag=true, DragAxis=Horizontal, PressFeedback=false, MinHitSizeDp=48 ＋ SEED.UI.GestureRelay
//                 （グラフの日付線のハンドル。葉なので同じ横の移動では親のパンより先に指を取る。e.LocalPosition はつまみの左上が原点。§7.19）
// 遮る板        … Tap=false（旗をすべて外す。ダイアログの板・覆い）
```

> **重要**: 変更は**次に触れた指から**効きます（触れている指は押したときの値のまま）。スクリプトから CanvasGesture を付けたプレハブを生成しても、次の指から参加します。

### CanvasScroll（スクロールの領域：一覧・横の帯・ページ送り・ホイール）

2D キャンバスのノードに **CanvasScroll**（「コンポーネント追加 → UI → Canvas Scroll」）を付けると、そのノードが中身（子）をずらして見せる窓になります（W2-3）。
窓の大きさは CanvasComponent があればキャンバスの箱、無ければ最初の Sprite の矩形。窓の外を切るには同じノードに **CanvasClip** を付けます
（付けると、窓の外の子を描画アイテムと当たり判定から外します＝1,000 行でも見えている行の分しか描かない）。指のドラッグは CanvasGesture を
付けなくても受けます（向きに合わせた軸のドラッグとフリック。中の行のボタンは W2-2 のアリーナで 100ms 待ってから押下になる）。

```csharp
if (gameObject.GetComponent<CanvasScroll>() is { } s)
{
    // 設定（get/set。インスペクタと同じ欄）
    s.Enabled          // bool（既定 true）
    s.Direction        // ScrollDirection: Vertical（既定）/ Horizontal / Both
    s.Edge             // ScrollEdge: Bounce（既定。端を越えて引っぱれ、ばねで戻る）/ Clamp（端で止まる）
    s.Inertia          // bool（既定 true。指を離した後も流れる）
    s.Snap             // ScrollSnap: None（既定）/ Page（1 回のフリックで最大 1 ページ）/ Interval（止まる位置に最も近い間隔の倍数へ）
    s.SnapInterval     // float（スナップの長さ。Page で 0 なら窓の長さ）
    s.HandOffToParent  // bool（既定 true。同じ向きの外側のスクロールへ、端に達した残りのドラッグとフリックを渡す）
    s.ContentSizeMode  // ScrollContentSize: Auto（既定。子の矩形のいちばん遠い端）/ Fixed（FixedContentSize）
    s.FixedContentSize // Vector2（Fixed のときの中身の大きさ。一覧の仮想化で全体の長さを決める）
    s.CullOutside      // bool（既定 true。CanvasClip があるとき、見える範囲の外の子を描画と当たり判定から外す）
    s.CacheExtent      // float（既定 250。見える範囲の外として飛ばす判定の余白）
    s.FlingFriction    // float（Clamp の慣性の摩擦。既定 0.015）
    s.BounceDrag       // float（Bounce の慣性の減衰。既定 0.135）

    // 状態
    s.Position         // Vector2（get/set。書くと動きを止めてすぐ移す＝範囲へ収める）
    s.Velocity         // Vector2（単位/秒）
    s.Phase            // ScrollPhase: Idle / Dragging / Ballistic（慣性・跳ね返り・スナップ）/ Animating（ScrollTo）/ Held（触れて止めた）
    s.IsScrolling      // bool（ドラッグ・慣性・ScrollTo の間）
    s.IsDragging       // bool
    s.HasMetrics       // bool（窓と中身の大きさが分かったか。Play の最初の描画の後）
    s.ViewportSize     // Vector2（窓の大きさ）
    s.ContentSize      // Vector2（中身の大きさ）
    s.MaxPosition      // Vector2（位置の最大 = 中身 − 窓）
    s.EndInset         // Vector2（get/set。中身の末尾に足す余白・0 以上・実行中だけで保存しない。W2-6b の入力欄がキーボードを避けるときに使う。
                       //   次のフレームの描画から ContentSize・MaxPosition に入る）

    // 操作
    s.ScrollTo(new Vector2(0, 1200), 0.3f);  // 時間をかけて動かす（Curves.easeInOut。次のフレームから。指で触れると止まる）
    s.JumpTo(new Vector2(0, 0));             // すぐ移す（Position への書き込みと同じ）
}

// 例: 縦の一覧（窓: Canvas + CanvasClip + CanvasScroll、中身: Stack の縦の並び）
// ListRoot（Canvas 360×640・CanvasClip・CanvasScroll 縦）
// └─ Content（Canvas・CanvasStack 縦・fit_height・CanvasLayoutItem fill_width）… 行を縦に並べる。窓の中身の大きさは Content の高さ
//    ├─ Row0 …
// 窓自身に CanvasStack を付けてもよい（スクロールの軸は箱の長さを決めずに並べる＝行は縮まない）
```

| 規則 | 内容 |
|---|---|
| 慣性 | Bounce: Flutter の BouncingScrollSimulation（1 秒で速度 0.135 倍の減衰・端を越えたらばね）。Clamp: Flutter の ClampingScrollSimulation（= Android の OverScroller。1000 dp/秒で 194 dp・0.46 秒） |
| 端 | Bounce は端の外へ摩擦つきで引っぱれ（0.52 × (1 − はみ出し/窓)²）、離すとばね（質量 0.5・硬さ 100・減衰比 1.1）で戻る。Clamp は端で止まる（端の光・伸びの表示は無い） |
| 入れ子 | 向きの違う入れ子（縦の一覧の中の横の帯）は最初の指の動きの向きで持ち主が決まる。同じ向きは内側が先に使い、端に達した残りを外側へ（`HandOffToParent`） |
| 触れて止める | 慣性・ScrollTo の途中で触れると止まり（`OnScrollEnd`・Phase は Held）、その指は中の行へ届かない。ドラッグすれば続きのスクロールになる |
| 描く理由 | 動いている間（ドラッグ・慣性・ScrollTo・位置の要求の処理待ち）はエンジンが「動いている」を申告する（`render_policy: on_demand` でも止まらず、止まったら描画も止まる。§7.14） |
| 単位 | 値はキャンバスの単位（dp のキャンバスなら dp）。物理の定数は dp で決まっているので、端末の表示倍率に依らず同じ手触り |

> **重要**: 窓と中身の大きさは**前のフレームの描画**が測った値です（Play の最初の描画までは `HasMetrics = false`・`ViewportSize` などは 0）。中身の大きさが変わったフレームは 1 フレーム遅れて範囲に効きます。

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
| （アクター自身） | `gameObject.Visible` | アクターと全子孫の**描画だけ**を止める表示フラグ。スクリプト・物理は動き続ける |
| （アクター自身） | `gameObject.Name` | アクター名（`Find` / `FindChild` の照合キー）。動的生成物へ一意な名前を付ける用途。既存アクターの改名は参照が追従しない |
| `Transform` | `gameObject.GetComponent<Transform>()` / `transform` | 3D 位置・回転・スケール |
| `CanvasTransform` | `gameObject.GetComponent<CanvasTransform>()` | 2D キャンバス上の位置・回転・スケール・ピボット・アンカー・前のフレームのレイアウトの結果（HasLayout・LayoutSize・LayoutRect。読み取り専用） |
| `Model` | `gameObject.GetComponent<Model>()` | 3D モデルの表示切替（`Visible`）・レイトレ除外（`RayTracingExcluded`）・描画オフセット（位置・回転・スケール）。描画のみで物理・追従には影響しない |
| `Sprite` | `gameObject.GetComponent<Sprite>()` | テクスチャパス・色・サイズ・レイヤー・ポインタ判定対象（RaycastTarget）・形と塗り（角丸・楕円・弧・縁・グラデーション・9 スライス・影。W2-4） |
| `SkinnedSprite` | `gameObject.GetComponent<SkinnedSprite>()` | メッシュパス（.sprite_mesh）・テクスチャパス・色・レイヤー・ポインタ判定対象。ボーンは子アクターの CanvasTransform で動かす |
| `Camera` | `gameObject.GetComponent<Camera>()` | FOV・クリップ距離・メインカメラ・クリアカラー・ベース解像度 |
| `AudioSource` | `gameObject.GetComponent<AudioSource>()` | 音源パス・**音声辞書のキー**・音量・ループ・3D 減衰・パン + Play/Stop |
| `AudioDictionary` | `gameObject.GetComponent<AudioDictionary>()` | 音声辞書（`グループ/用途` → 音声ファイル・既定音量）。TryGetPath / DefaultVolume / Play |
| `Animator` | `gameObject.GetComponent<Animator>()` | 再生中クリップ・再生位置・速度・フェード + Play/CrossFade/Stop/Pause/Resume |
| `ParticleEmitter` | `gameObject.GetComponent<ParticleEmitter>()` | 放出レート・ループ・抵抗・拡散角・色味(Tint) + Play/Stop/Burst |
| `InputMap` | `gameObject.GetComponent<InputMap>()` | 入力アクション評価（Bool / Axis1D / Axis2D。Key / GamepadButton / GamepadAxis） |
| `WaterVolume` | `gameObject.GetComponent<WaterVolume>()` | 現在水位（読み取り専用）・設定水位・水位シミュレーションの有効／無効・水面シェーダのパラメータ（SetShaderParam / GetShaderParamFloat / GetShaderParamVector3） |
| `WaterLink` | `gameObject.GetComponent<WaterLink>()` | 水位グラフの開口。**開閉率（バルブ）**・開口寸法・流量係数 |
| `LineRenderer` | `gameObject.GetComponent<LineRenderer>()` | 3D の線（釣り糸・ロープ・軌跡）。点列（SetPoints）・太さ・色・表示・座標系・深度テスト |
| `Text` | `gameObject.GetComponent<Text>()` | キャンバス上の文字表示（HUD の数値・ラベル）。内容・フォント（assets:// の .otf/.ttf）・サイズ・色・**縁取り**（太さ・色）・**枠と自動折り返し**（BoxWidth/BoxHeight/Wrap）・**太さ**（Weight）・**ドロップシャドウ**（ShadowOffset/ShadowColor/ShadowSoftness）・整列・行送り・レイヤー |
| `CanvasClip` | `gameObject.GetComponent<CanvasClip>()` | 子を切り抜く（ノードの矩形からはみ出した子孫を描かず、押せなくする）。有効・無効（Enabled）・形（Shape: 角丸・楕円。W2-4） |
| `CanvasStack` | `gameObject.GetComponent<CanvasStack>()` | 子を縦・横に 1 列に並べるコンテナ。向き・間隔・余白・主軸／交差軸の揃え・逆順・中身に合わせる・非表示の子の扱い |
| `CanvasWrap` | `gameObject.GetComponent<CanvasWrap>()` | 子を並べて折り返すコンテナ。向き・子と行の間隔・余白・行の中と行の塊の揃え |
| `CanvasGrid` | `gameObject.GetComponent<CanvasGrid>()` | 子を格子に並べるコンテナ。列数（0 = 最小幅から自動）・セルの縦横比・間隔・余白・セルの中の置き方 |
| `CanvasLayoutItem` | `gameObject.GetComponent<CanvasLayoutItem>()` | コンテナの子の側の指定。伸ばす重み・大きさの指定と上下限・揃えの上書き・無視させる・親に合わせる |
| `CanvasSafeArea` | `gameObject.GetComponent<CanvasSafeArea>()` | ノードの領域を Screen.SafeArea の内側へ縮める。有効・辺ごとの適用（Left/Top/Right/Bottom） |
| `CanvasGesture` | `gameObject.GetComponent<CanvasGesture>()` | ジェスチャーを受けるノード（W2-2）。タップ・長押し・ドラッグ・フリックの旗・ドラッグの軸・押下の見た目・最小のヒット領域（dp）。イベントは `OnGesture*` |
| `CanvasScroll` | `gameObject.GetComponent<CanvasScroll>()` | スクロールの領域（W2-3）。向き・端（跳ね返る・止める）・慣性・スナップ・入れ子・中身の大きさ・見える範囲の外を飛ばす。位置の読み書き・`ScrollTo`。イベントは `OnScroll*` |
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

> **重要**: 自作スクリプトへの参照はハンドル構造体ではなく**実インスタンス（class）**なので、`IsValid` は**ありません**。解決できないとき（未設定・アクター不在・そのスクリプトが付いていない・破棄済み）は `T` 宣言でも `T?` 宣言でも **必ず `null`** になるため、**毎回 null チェックが必須**です。参照先スクリプトの `OnStart` が自分より先に走っている保証は**ありません**（初期化済みの値を読むのは `Update` 以降にしてください）。**さらに重要**: 参照先スクリプトの `gameObject` / `transform` は、そのスクリプト自身のライフサイクル呼び出しが 1 度走るまで束縛されません。したがって自分の `OnStart` から相手のメソッドを呼ぶと、相手の中の `gameObject.GetComponent<T>()` や `gameObject.Visible` が**黙って空振り**します（図鑑のカードがこれで真っ白になりました）。相手のアクタを触るメソッドの呼び出しは `Update` 以降に回してください。インスタンスは**ホットリロードのたびに作り直されて再注入**されるので、**別のフィールドへキャッシュしてはいけません**（古いインスタンスを掴み続けます）。同じスクリプトが 1 アクターに複数付いている場合はインスペクタのスロット選択ダイアログで指定できます（未指定なら先頭のスロット）。

> **重要**: 参照フィールドは**常に参照（ハンドル）**です（「値としての Transform」は作れません。値で持つなら `SEED.Vector3`）。**`null` は「未設定」のみ**を意味し Nullable（`T?`）宣言でしか起きません。**`IsValid` は「参照先が生きているか」**で、未解決・破棄済みのどちらでも `false` です。非 Nullable（`T`）宣言は未設定でも null にならず `IsValid == false` の無効ハンドルになります。参照は**アクタ名（＋スロット名）**で保存されますが、エディタでアクタをリネームすると**旧名一致の参照は自動で新名に追従**します（同名アクタが複数ある場合は旧名一致の参照がすべて書き換わる点に注意。コンパイルエラー中のスクリプトの参照は型判定できないため追従しません）。解決は Play 開始時／Instantiate 時に **`OnStart` より前の一度きり**です。

**`null` と `IsValid` の使い分け（重要）**

- **`null` は「未設定」だけを意味します**。Nullable（`T?`）で宣言したフィールドのみ null になり得ます。
- **`IsValid` は「参照先が今も生きているか」**を意味します。未解決（アクタが見つからない）・破棄済みのどちらでも `false` になります。
- 非 Nullable（`T`）で宣言した参照は未設定でも null にならず、**`IsValid == false` の無効ハンドル**になります。
- `IsValid` はライフサイクル関数の中でのみ意味のある値を返します（コンストラクタ等、エンジンの実行フェーズ外では常に `false`）。
- **一度も値を入れていないハンドル（`default` / 未初期化の静的フィールド）は `IsValid == false`** です。`SEED.GameObject menuRoot;` のように宣言しただけの変数を「まだ生成していない」判定に使って構いません（`if (!menuRoot.IsValid) { menuRoot = SEED.GameObject.Instantiate(...); }`）。

**インスペクタでの設定方法**

1. スクリプトのフィールド行にある参照ボックスへ、**Hierarchy パネルからアクタ行をドラッグ＆ドロップ**します。
2. ドロップしたアクターがその種別のスロットを**複数持つ**場合は、スロット選択ダイアログが出ます。
3. `✕` ボタンで参照を解除します（未設定に戻ります）。
4. 参照ボックスを**ダブルクリック**すると、Hierarchy の参照先アクタへジャンプします。
5. 参照先がその種別を持っていない場合は警告が出て設定されません。

**保存形式と制約**

- 参照は**アクタ参照文字列**（コンポーネント参照は加えて**スロット名**）で保存されます（`Player` / `Player|MainCamera`）。未設定は空文字列です。
- アクタ参照文字列には**パス指定**が使えます（下の「参照文字列のパス指定」を参照）。
- **スロット名を変更すると参照は切れます**（再設定が必要）。アクタ名に `|` は使えません。
- 素のアクタ名で保存された参照は、**自分のサブツリー → シーン全体**の順に DFS で解決されます（同名が複数あるときは最初に見つかったもの）。

#### 参照文字列のパス指定

同じプレハブをシーンへ複数並べると子アクタの名前が重複します。素のアクタ名だけで参照を保存していると、シーン全体 DFS が常に 1 枚目のインスタンスの子を返すため、2 個目以降の参照がすべて 1 個目へ吸われてしまいます。これを避けるため、参照文字列は**パス形式**を受け付けます。

| 書式 | 意味 |
|---|---|
| `./Child` | **自分（このスクリプトが乗るアクタ）のサブツリー**から探す |
| `./A/B` | 自分の子 `A` の子 `B`（セグメントごとに子をたどる） |
| `.` | 自分自身 |
| `../Sibling` | 親のサブツリーから探す（`../../` と重ねて祖先へ登れる） |
| `Root/Child/Grand` | **シーンのルートから**の絶対パス |
| `Name`（`/` なし） | 従来どおりの名前指定。**自分のサブツリー優先**、無ければシーン全体 |

**解決の優先順（precedence）**

1. `./` / `../` で始まる文字列は**必ず相対**として解決します（シーン全体へは広がりません）。
2. `/` を含む文字列（`./`・`../` で始まらないもの）は**シーンのルートからの絶対パス**として解決します。
3. `/` を含まない素の名前は、**① 自分自身とその子孫を DFS → ② シーン全体を DFS** の順に探します。
   ①（自分のサブツリー優先）が後から入った規則で、既存シーンの参照は②で従来どおり解決されるため互換です。
4. `./Child` の 1 セグメント指定は「直下の子（フォルダ透過）→ 見つからなければ子孫を DFS」の順で照合します。
5. どれにも当たらなければ未解決（Nullable なら `null`、非 Nullable なら `IsValid == false`）。

2D の**フォルダノードは階層に存在しないもの**として透過します（`Items` がフォルダなら `./Image` は `./Items/Image` にも一致します）。フォルダ名を明示的に書いたパスもそのまま通ります。

**エディタが書く書式**: Hierarchy からアクタをドロップすると、エディタが位置関係を見て自動で書式を選びます。

- 落としたアクタが**参照の持ち主の子孫**なら `./Child`（プレハブ内の結線がこれになります）
- そうでなく、その名前が**シーン内で一意**なら素の名前（従来どおりの見た目）
- 同名が複数あるときはルートからの絶対パス

**制約**: パス形式で保存された参照は、**アクタのリネームに自動追従しません**（素のアクタ名で保存された参照だけが追従します）。また参照ボックスの「参照先が見つかりません」警告は、パス形式のときは判定できないため出ません。
- **解決は一度きり**です。Play 開始時（および `Instantiate` されたアクターの生成時）に、そのスクリプトの **`OnStart` より前**に解決・注入されます。実行中のアクタ名変更やアクタ生成には追従しません。実行中に相手を探し直したい場合は `SEED.GameObject.Find(name)` / `gameObject.FindChild(name)` を使ってください。
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
SaveData.Save();              // bool（成功=true。Batch の中では Batch の終わりまで待たされて true）

// 複数キーの書き換えを 1 まとまりにする（その間は自動保存も Save も書かず、終わりに 1 回だけ書く）
bool ok = SaveData.Batch(() => { /* SetLong / SetString … / Save() */ });  // bool（終わりの書き出しが不要・成功=true）

// 起動して最初に読んだとき、どこから読んだか（壊れた・無い save.json からの復旧を利用者へ知らせる）
SaveRecovery from = SaveData.RecoveredFrom;  // SaveRecovery.None / Backup / Lost

// SaveRecovery
SaveRecovery.None     // 普段どおり save.json を読めた・初めての起動
SaveRecovery.Backup   // save.json が無い・壊れていて save.json.bak（1 つ前の世代）から読んだ（直前の保存が失われた可能性）
SaveRecovery.Lost     // save.json か .bak が有ったのに読めず、空で始めた（壊れた本体は save.json.corrupt-<時刻> に残る）
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

```csharp
// 例: お金と履歴のように「片方だけ残ると困る」組は、1 つの文書（JSON）を 1 つのキーに入れる（推奨）。
//     1 キーの書き換えは常に丸ごと入れ替わるので、半端な組み合わせがディスクに残らない。長さの上限は無い（数 MB も可）。
string wallet = SaveData.GetString("wallet_doc", "{}");      // JSON 文書（中身の形はアプリが決める）
wallet = AppendPayment(wallet, amount: 100);                   // アプリ側で文書を組み立て直す
SaveData.SetString("wallet_doc", wallet);
SaveData.Save();

// 例: キーを分けたまま 1 まとまりにしたいときは Batch で包む（入れ子にしてよい）
bool written = SaveData.Batch(() =>
{
    SaveData.SetLong("money", SaveData.GetLong("money", 0) - 100);
    SaveData.SetString("history", historyJson);
    SaveData.Save();   // Batch の中では待たされ、Batch の終わりに 1 回だけ書かれる
});

// 例: 起動時に復旧を知らせる（AppendPayment・historyJson・ShowNotice はアプリ側で用意するもの。SEED の API ではない）
switch (SaveData.RecoveredFrom)
{
    case SaveRecovery.Backup: ShowNotice("前回の保存の一部が失われた可能性があります"); break;
    case SaveRecovery.Lost:   ShowNotice("保存データを読めなかったため、初期状態で始めます"); break;
}
```

> **重要**: `Set*` はメモリ上のストアを書き換えるだけです。ディスクへ書き出すのは `Save()` を呼んだときと、Play 終了時・アプリ終了時の自動保存だけなので、進行の区切り（魚を釣った直後・購入した直後）で `Save()` を呼んでください。Android ではこれに加えて、バックグラウンドへ回るとき（ホーム・アプリ切り替え・画面オフ）とアプリを閉じるときにも自動保存します（Android は背面のアプリを予告なく終了させることがあるため）。

> **重要**: 整数と実数は相互に読み替えられます（実数 → 整数は 0 方向へ切り捨て）。文字列と数値は**相互変換しません** — 型を間違えた読み取りは既定値を返すので、書いたときと同じ型で読んでください。

> **重要**（耐久性）: 書き出しは「一時ファイル `save.json.tmp` へ書いてディスクまで届ける（sync）→ 今の `save.json` の写し（Android は複製〈アプリは hard link を作れない〉、PC は hard link）を `save.json.bak.new` に作って `save.json.bak` へ置き換える（`save.json` は動かさない）→ 一時ファイルを `save.json` にする（既存を原子的に置き換える。→ Android はフォルダも sync）」の順で、**どの瞬間にも `save.json` があり**、どこで落ちても・電源が切れても `save.json` か 1 つ前の世代 `save.json.bak` のどちらかから読めます（写しを作れないとき〈容量が足りない等〉だけ `save.json` を `save.json.bak` へ rename で回す方式に戻り、その間だけ `save.json` が無い瞬間があります。そこで落ちると次の起動は `.bak` から読みます）。複製の分だけ、大きなセーブほど `Save()` は重くなります（前の世代をもう 1 回書く）。読み込みは `save.json` → 無い・壊れていれば `save.json.bak` → どちらも無ければ空。壊れた `save.json` は上書きせずに `save.json.corrupt-<UTC の時刻>` として 1 つだけ残します。復旧したときは `SaveData.RecoveredFrom` が `Backup`（1 世代前から読んだ。直前の保存が失われた可能性）か `Lost`（読めるものが無く空で始めた）になり、次の書き出しで `save.json` が作り直されます。

> **重要**（Batch）: `SaveData.Batch(action)` の間は、自動保存（Android の背面・アプリを閉じるときの別スレッドからの書き出しを含む）と `Save()` をディスクへ書かず、最も外側の Batch の終わりに 1 回だけ書きます（Batch の間に書き出しの要求が無ければ書きません）。**取り消しはしません** — `action` が例外を投げても Batch は終わりますが、それまでに書き換えたキーは戻りません。中は短く保ち、待ち（非同期・長い計算）を入れないでください。Batch の途中でプロセスが終わると、Batch より前の未書き出しの変更も書かれません（ディスクは前回の書き出しのまま）。「お金と履歴」のような組は、1 つの文書を 1 つのキーに入れる形が最も単純で確実です。

> **重要**（大きな文字列）: `SetString` の値の長さに上限はありません（数 MB の JSON も可。1 KB を超える文字列はスタックではなくヒープを経由して渡すので、スタックは溢れません）。ただし書き出しは毎回ファイル全体を書き直すので、大きな文書ほど `Save()` は重くなります（PC のデバッグビルドで 2.4 MB の save.json が約 0.1 秒）。

| 保存先 | パス |
|---|---|
| パッケージ実行（配布ビルド） | 実行ファイルと同じ階層の `saved/save.json` |
| エディタから Play | アセットルートの親の `save/save.json`（リポジトリの既定のアセットなら `runtime/save/save.json`、プロジェクトなら `<プロジェクト>/save/save.json`。Git 追跡外） |
| Android（APK 内 pak でも開発用の置き場でも同じ） | アプリの内部データ `/data/user/0/<パッケージ名>/files/save/save.json`（デバッグ版 APK なら `adb exec-out run-as <パッケージ名> cat files/save/save.json` で見られる） |
| 環境変数 `SEED_SAVE_DIR` 指定時 | そのディレクトリの `save.json`（最優先） |

| 保存先のフォルダのファイル | 中身 |
|---|---|
| `save.json` | 今の世代（本体） |
| `save.json.bak` | 1 つ前の世代（書き出しのたびに直前の `save.json` がここへ回る） |
| `save.json.bak.new` | 1 つ前の世代の作りかけ（直前の `save.json` の写し。すぐ `save.json.bak` になる。書き出しの最中に落ちた跡。読まれず、次の書き出しで消える） |
| `save.json.tmp` | 書き出しの途中の一時ファイル（書き出しの最中に落ちた跡。読まれず、次の書き出しで作り直される） |
| `save.json.corrupt-<時刻>` | 読めなかった `save.json` を上書きせずに残したもの（1 つだけ。時刻は UTC の `yyyyMMdd-HHmmss`） |

> **重要**: セーブデータは Play を終了して Edit へ戻しても**巻き戻りません**（ゲームの進行であってシーンの編集データではないため）。テストで初期状態へ戻したいときは `SaveData.DeleteAll()` + `SaveData.Save()` を呼ぶか、保存先の `save.json` と `save.json.bak` を削除してください（`save.json` だけを消すと、次の起動で `save.json.bak` から復旧します）。

---

## 7.75 Assets（アセットのテキスト読み込み：レベルデザイン用データ）

譜面表・会話台本・湧きテーブルのように「**差し替えるだけで挙動が変わる**」データを、ソースコードではなくテキストファイルへ置いて読み込むための API です。実体は Rust ランタイムの `asset_fs` なので、PAK 同梱でもエディタ Play でも同じパスで動きます（スクリプト側はファイル IO を意識しません）。

```csharp
// 本文を読む（失敗したら false。text は空文字列）
if (SEED.Assets.TryReadText("assets://mainGame/rhythm/beat_patterns.txt", out string text))
{
    foreach (string line in text.Split('\n')) { /* … */ }
}

// 読めなくても既定値で進めたいとき
string body = SEED.Assets.ReadText("assets://mainGame/data/table.txt", fallback: "");

// 更新されたかを調べる（ホットリロード用。UNIX 秒。取得できないときは 0）
long stamp = SEED.Assets.GetModifiedTime("assets://mainGame/rhythm/beat_patterns.txt");
```

| メンバー | 説明 |
| -------- | ---- |
| `bool TryReadText(string path, out string text)` | UTF-8 テキストとして読む（BOM は除去）。読めなければ false |
| `string ReadText(string path, string fallback = "")` | 同上。失敗時は `fallback` を返す簡便版 |
| `long GetModifiedTime(string path)` | 最終更新時刻（UNIX 秒）。取得できないときは `Assets.UnknownModifiedTime`（0） |

**注意**

- 呼ぶたびにディスク（または PAK）から読み直します。**毎フレーム呼ばないこと**。起動時や場面の切り替えで 1 度だけ読み、結果はスクリプト側で保持します。
- ホットリロードしたいときは `GetModifiedTime` を数秒に 1 度だけ調べ、値が変わったときだけ読み直すのが安上がりです（実例: `<project>/assets/mainGame/scripts/Rhythm/BeatPatternLibrary.cs`）。
- 書き込み API はありません。永続化したい値は `SaveData`（7.7）を使ってください。

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

`layer` はスプライト（`Sprite.Layer`）・テキスト（`Text.Layer`）と**同じソート軸**で、大きいほど手前です。

UI の重なり順は次の 3 段で**種別を跨いで**決まります（描画ゾーン → レイヤー → 種別）。

```
zone（背景 / 前面） → layer（昇順・大きいほど手前） → 同一 layer 内は スプライト → プリミティブ → パーティクル → テキスト
```

同一レイヤー・同一種別のときはヒエラルキーの並び順（DFS 順）が保たれます。

「パーティクル」は 2D キャンバスアクターに付けた `ParticleEmitter` です（`ParticleEmitter.Layer`）。同一 layer でテキストより奥に置くのは「文字は常に読めるべき」という UI の原則によるもので、手前に出したいときは `Layer` を上げてください。

> **重要（2026-09 修正）**: 以前は「全スプライト → 全プリミティブ → 全テキスト」の順に描いていたため、`layer` は同じ種別の中でしか効かず、**低い layer のテキストが高い layer のスプライトより手前に出る**不具合がありました。現在は 3 種を 1 本の描画列へマージするため、`layer 1002` のテキストは `layer 2002` のスプライトに正しく覆われます。

描画ゾーン（Canvas の背景／前面）は `space` に渡したキャンバスのゾーンを継承し、スクリーンスペース（`space: null`）は前面ゾーン扱いです。
`space` のノードとその祖先の `CanvasLayoutItem` のレイヤーの底上げ（W2-7 の画面のスタックの段）も図形のレイヤーに足されます（2026-09-28・W2-8 から。
積んだ画面の中の図形が画面の背景の下に隠れない。底上げの無いノードでは従来と同じ）。3D ワールドキャンバス（Actor3D + Canvas）配下では、レイヤーは**そのキャンバス内で完結**します（キャンバス同士はヒエラルキー順に前後）。

GPU ピッキング（クリック選択）もこの描画順と同一の規則で並ぶため、**見た目で最前面にあるものがクリックで選ばれます**。

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

### 見た目の拡張（DrawStyle）と線の下の塗り（Area。W2-8）

`DrawStyle` を受け取るメソッド（点列は `ReadOnlySpan<Vector2>`＝配列の一部でも渡せる）は、次の見た目を選べます。既存のメソッド（`DrawStyle` なし）の見た目は変わりません。

```csharp
var style = DrawStyle.Crisp;                           // アンチエイリアスの帯を画面の 1 画素にする（dp のキャンバスでも縁がにじまない）
var grad  = DrawStyle.Crisp.WithLinearGradient(
    new Vector2(0f, top), new Vector2(0f, bottom), color.WithAlpha(0f));   // 始点で図形の色 → 終点で透明（頂点の色の線形補間）

Draw.Line(a, b, color, style, thickness: 1f, layer: 0, space: ink);
Draw.Polyline(points.AsSpan(0, n), false, color, style, thickness: 2f, layer: 0, space: ink);   // 1024 点まで
Draw.Polygon(outline, color, style, layer: 0, space: ink);        // 塗り（凸は扇・凹は耳刈り）
Draw.Circle(center, 2.5f, color, style, layer: 0, space: ink);
Draw.Area(points, baselineY, color.WithAlpha(0.35f), grad, layer: 0, space: ink);   // 線の下の塗り（折れ線と基準線 y の間）
```

| 項目 | 内容 |
|---|---|
| `DrawStyle.PixelFeather`（`Crisp`） | アンチエイリアスの帯（フェザー）を画面の 1 画素にする。既定は描画空間の 1 単位（dp のキャンバスでは 1 dp = 2.625 画素の端末で 2.6 画素ににじむ）。線の太さは描画空間の単位のまま。3D ワールドキャンバスでは効かない |
| `WithLinearGradient(from, to, end)` | 始点 `from` で図形の色、終点 `to` で `end`、軸の外は端の色（点列と同じ空間） |
| `Draw.Area(points, baselineY, …)` | 折れ線（左 → 右）と基準線の間を縦の台形の帯で塗る（多角形の耳刈りを使わないので点が多くても軽い）。基準線をまたぐ線分は交点で切って両側を塗る。アンチエイリアスは上の縁だけ |
| 軽い三角形分割 | `DrawStyle` を受け取るメソッドの折れ線は 1 本の帯（線分ごとの帯と曲がる外側だけの丸いつなぎ）、凸の塗りは扇で分ける（365 点の折れ線で三角形 15,986 → 4,757）。従来のメソッドは今までと同じ分割 |

### 制限

| 項目 | 上限・制限 |
|---|---|
| 1 フレームの図形数 | 4096（`Draw.MaxPrimitivesPerFrame`）。超過分は描かれず警告ログが出る |
| 1 図形の点数 | 1024（`Draw.MaxPointsPerPrimitive`）。超過分は切り捨て |
| `Polygon` の形状 | 自己交差しない単純多角形のみ（凹は可）。穴あき・自己交差は結果が保証されない |
| 半透明の太い折れ線 | 角（ジョイント）がわずかに濃くなる（帯が重なるため）。不透明色では見えない |
| アンチエイリアス | 輪郭の外側 1px のフェザー帯による近似（スクリーンスペースでは 1 画面 px。キャンバスの中では描画空間の 1 単位。画面の 1 画素にするには `DrawStyle.Crisp`） |

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

## 7.10 Events（名前付きイベント：スクリプト間の通知）

「名前」でイベントを発火し、その名前を購読している全スクリプトへ同期で配るイベントバスです。
発火側は受け手を知らなくてよいので、`GameObject.Find` で相手を探し回らずに 1 対多の通知を飛ばせます
（例: 「魚がヒットした」を HUD・SE・カメラ演出が同時に受ける）。

### 発火（Raise）

引数は **0 個または 1 個**（`string` / `float` / `GameObject`）です。戻り値は実際に呼び出したハンドラ件数です。

```csharp
SEED.Events.Raise("Bite");                    // 引数なし
SEED.Events.Raise("ScoreTag", "combo");       // string 1 個
SEED.Events.Raise("Damage", 12.5f);           // float 1 個
SEED.Events.Raise("Caught", gameObject);      // GameObject 1 個
int called = SEED.Events.Raise("Bite");       // 戻り値 = 呼び出したハンドラ件数（0 = 誰も受けていない）
```

### 購読（推奨：`this.On` — スクリプトの寿命に自動追従）

`SEEDScript` を継承したスクリプトからは `this.On(...)` を使います。**そのスクリプトが破棄される
（アクター破棄・シーン遷移・Play 終了・ホットリロード）ときに自動で解除**されるため、解除漏れが起きません。

```csharp
public class HudFish : SEEDScript
{
    private float _hp = 100f;

    public override void OnStart()
    {
        this.On("Bite", () => SEED.Debug.Log("ヒット！"));                    // 引数なし
        this.On("ScoreTag", (string tag) => SEED.Debug.Log(tag));            // string
        this.On("Damage", (float amount) => _hp -= amount);                  // float
        this.On("Caught", (SEED.GameObject fish) => SEED.Debug.Log(fish.IsValid)); // GameObject

        SEED.EventSubscription handle = this.On("Bite", OnBite);  // 早期に解除したいときはハンドルを保持
    }

    private void OnBite() { }
}
```

### 手動購読（寿命を自分で管理する）

`SEED.Events.Subscribe` は自動解除されません。**必ず** `Dispose()` か `Events.Unsubscribe` で解除してください。

```csharp
SEED.EventSubscription sub = SEED.Events.Subscribe("Bite", OnBite);          // 引数なし
SEED.EventSubscription s2  = SEED.Events.Subscribe("Damage", (float v) => { });
SEED.EventSubscription s3  = SEED.Events.Subscribe("ScoreTag", (string v) => { });
SEED.EventSubscription s4  = SEED.Events.Subscribe("Caught", (SEED.GameObject g) => { });

sub.Dispose();                          // 解除（二重解除は無害）
SEED.Events.Unsubscribe(sub);           // 同上（null・解除済みも無害）
bool alive  = sub.IsActive;             // まだ購読中か
string name = sub.Name;                 // 購読しているイベント名

SEED.Events.Clear("Bite");              // この名前の購読をすべて解除
SEED.Events.ClearAll();                 // 全イベントの購読を破棄（シーン遷移前の掃除用）
int n = SEED.Events.SubscriberCount("Bite");  // 現在の購読件数（デバッグ用）
```

### ScriptEvent（インスペクタ結線）との使い分け

`ScriptEvent` は「どのアクターのどのメソッドを呼ぶか」をインスペクタで**結線**する仕組みで、呼び先が
決まっている 1 対 1（少数）の通知や、デザイナーが GUI 上で差し替えたい通知に向きます。一方
`SEED.Events` は呼び先を一切知らずに**名前だけ**で飛ばす 1 対多の通知で、受け手が実行中に増減する場合
（生成・破棄されるアクターが受ける、複数の HUD が同じ通知を受ける）に向きます。引数の種類は両者で
揃えてありますが（0 個 or 1 個）、`ScriptEvent` はインスペクタで入力した**固定引数**を渡すのに対し、
`SEED.Events` は発火側が**実行時の値**を渡す点が異なります。迷ったら「結線を見せたいなら ScriptEvent、
配線なしで広く配りたいなら Events」で選んでください。

### 仕様

| 項目 | 動作 |
|---|---|
| 発火 | 同期・即時。購読の登録順に呼ぶ |
| 引数 | 0 個または 1 個（`string` / `float` / `GameObject`）。`int` / `bool` は `float` か `string` で表現する |
| 型不一致 | `float` で購読している所へ `string` で発火しても**呼ばれない**（暗黙変換なし）。警告ログが 1 回だけ出る |
| 名前 | **大文字小文字を区別**する（`"Bite"` と `"bite"` は別イベント）。空文字は無視して警告 1 回 |
| ハンドラの例外 | 1 件ずつ捕捉して `Debug.LogError` へ出し、**残りのハンドラは続行**する |
| 発火中の購読変更 | 発火中の `Subscribe` / `Unsubscribe` / `Clear` は進行中の発火には反映されず、**次回の発火から**反映される |
| 再入（ハンドラ内で同名を発火） | 深さ 8 段で打ち切り、警告 1 回（無限再帰の保険） |
| 未購読イベントの発火 | 何も起きない（エラーにはならない。戻り値 0） |
| ホットリロード | スクリプト再コンパイル時に**全購読が破棄**される（購読は `OnStart` で張り直す設計にすること） |

> **重要**: `this.On(...)` で張った購読はスクリプト破棄時に自動解除されるが、`SEED.Events.Subscribe` で張った購読は**シーン遷移しても残る**。シーンをまたいで生きるハンドラを作りたくない場合は手動購読を使わず `this.On(...)` を使うか、遷移前に `SEED.Events.Clear(name)` / `SEED.Events.ClearAll()` で掃除すること。

> **重要**: イベント名は文字列なのでタイプミスをコンパイラが検出できない。名前は `public const string BiteEvent = "Bite";` のように 1 か所へ定数化し、発火側・購読側の両方から参照することを推奨する。

---

## 7.11 Application（実行環境の判定：デバッグ機能のゲート）

「今このゲームがどういう立場で動いているか」を調べる API です。
主な用途は **デバッグ機能を配布版で自動的に無効化する** こと。
デバッグ表示・当たり判定の可視化・チートコマンドを `SEED.Application.IsDebugAllowed` で囲っておけば、
配布用（release）のパッケージ（`assets.pak` 同梱）ではそれらが動かなくなります。
開発用のビルド（SeedAndroid の debug の APK・パッケージ化ウィンドウで「開発用のビルド」にチェックを入れたビルド〈既定はビルド種別 Debug〉）は pak 実行でも動きます（`IsDebugBuild`）。

```csharp
public static bool IsPackaged;      // パッケージ実行（assets.pak 同梱）なら true（開発用のビルドの pak 実行も true）
public static bool IsEditorPlay;    // エディタから Play したゲーム実行中なら true
public static bool IsDebugBuild;    // 開発用のビルド（pak に開発用のビルドの印がある）なら true。配布用（release）のビルドでは必ず false
public static bool IsDebugAllowed;  // デバッグ機能を有効にしてよいか（!IsPackaged || IsDebugBuild。配布用のビルドでは必ず false）
public static int  TargetFps;       // プロジェクト設定の目標フレームレート（0 = 無制限。描画品質プリセットの target_fps の上限を当てた値）。設定値であって実測ではない
public static bool VsyncEnabled;    // 垂直同期が実際に有効か（設定 "auto" の解決結果を含む）
```

### 各プロパティの値

| 実行のしかた | `IsPackaged` | `IsEditorPlay` | `IsDebugBuild` | `IsDebugAllowed` |
|---|---|---|---|---|
| エディタで Play | false | **true** | false | **true** |
| エディタの編集中ビュー（Edit モード） | false | false | false | **true** |
| 開発用のビルド（SeedAndroid の debug の APK・SeedPak `--debug-build`・パッケージ化ウィンドウの「開発用のビルド」〈既定は Debug〉）の pak 実行 | **true** | false | **true** | **true** |
| 配布用（release）のビルド（`assets.pak` あり）を単体起動 | **true** | false | false | false |
| 実ファイルの assets を隣に置いた単体起動・pak の無い開発用の APK（`--assets-dir`） | false | false | false | **true** |

### 開発用のビルドの印（`IsDebugBuild` の判定源）

| 項目 | 内容 |
|---|---|
| 印の正体 | pak の中の予約のエントリ `.seed/build.json`（中身 `{"format":1,"debug":true}`）。パッケージ化が**開発用のビルドのときだけ**入れる |
| 入れるビルド | SeedAndroid / エディタの Android 実行の開発用（debug）の APK（`--variant debug`。Rust の最適化 `--release` には依らない）・SeedPak `--debug-build`・パッケージ化ウィンドウの「開発用のビルド」にチェックが入ったビルド（Windows / macOS / iOS。既定はビルド種別 Debug で入る。2026-10-02 から Release でも手で入れられる＝そのパッケージは配布しない） |
| 入れないビルド | 配布用（release）の APK / AAB・パッケージ化ウィンドウの「開発用のビルド」のチェックを外したビルド（既定はビルド種別 Release）。Android の配布前の検査は、印のある配布物を不合格にする |
| 読むとき | 起動時に pak を開いた直後に 1 回（印が無い・読めない・知らない版なら false＝安全側）。pak の外のファイル・端末の上書き層からは読まない |

### 例: デバッグ表示・デバッグコマンドを配布版で無効化する

```csharp
using SEEDEditor.Scripting;

public class DebugHud : SEEDScript
{
    /// <summary>デバッグ HUD を表示中か。</summary>
    private bool visible;

    public override void Update(ref NativeFrameContext ctx)
    {
        // ゲートは必ず IsDebugAllowed で行う（判定方針を変えるときの修正箇所を 1 つに保つため）。
        // 配布版ではここで抜けるので、以降のキー判定も描画も一切走らない。
        if (!SEED.Application.IsDebugAllowed) return;

        // F1 でデバッグ HUD をトグル
        if (SEED.Input.GetKeyDown(SEED.KeyCode.F1)) visible = !visible;
        if (!visible) return;

        // 当たり判定の可視化など、開発中だけ見せたい描画
        SEED.Draw.Rect(new SEED.Vector2(8f, 8f), new SEED.Vector2(120f, 24f),
            new SEED.Color(0f, 0f, 0f, 0.5f));
    }
}
```

```csharp
// デバッグコマンド（チート）も同じゲートで囲う
public override void OnStart()
{
    if (!SEED.Application.IsDebugAllowed) return;
    SEED.Debug.OnCommand("giveitem", arg => GiveItem(arg));
}
```

```csharp
// エディタの Play でだけ効かせたい処理
// （実ファイル配布＝pak 無しの単体起動では動かしたくない場合に使う）
if (SEED.Application.IsEditorPlay)
{
    SEED.Debug.Log("エディタから Play 中");
}
```

### 制限

| 項目 | 内容 |
|---|---|
| 値の変化 | すべて**起動時に確定し、実行中は変化しない**。初回アクセスで 1 度だけランタイムへ問い合わせ、以降はキャッシュを返す |
| 呼び出しコスト | キャッシュ後はフィールド読み出しのみ。毎フレームの `if` に直接書いてよい |
| `IsPackaged` の判定源 | `assets.pak` を開けているかどうか。実ファイルの `assets/` を隣に置いた配布形態では **false** になる |
| `IsEditorPlay` の判定源 | エディタから `--mode=play` かつ IPC パイプ付きで起動されたか。Edit モードは含まない |
| `IsDebugBuild` の判定源 | pak の開発用のビルドの印（上の表）。pak を使わない実行では false（そのとき `IsDebugAllowed` は `!IsPackaged` で true） |
| ホスト API 未登録時・古いランタイム | すべて false（`IsDebugAllowed` は true）。安全側へ倒す既定（`IsDebugBuild` を知らないランタイムでも false） |

> **重要**: 「開発中だけ動かしたい処理」は `IsPackaged` / `IsEditorPlay` / `IsDebugBuild` を直接見ずに、必ず `IsDebugAllowed` で分岐してください。判定方針を変えたくなったとき（配布版でも隠しコマンドで有効化する、エディタ Play のときだけに絞る、など）に書き換える場所が 1 か所で済みます。開発用の課金の操作・サンプルデータ・デバッグの命令（`SEED.Debug.OnCommand`）の登録もこれで囲ってください。

> **重要**: 配布用（release）のビルドでは `IsDebugBuild` と `IsDebugAllowed` は**必ず false** です（印を入れない・配布前の検査が印を見張る）。開発用の APK（debug）を利用者へ配らないでください。開発用の APK では pak 実行でも開発用の機能が開きます。

> **重要**: `IsDebugAllowed` はあくまで**分岐の共通ゲート**であり、コードそのものを配布版から取り除くわけではありません。チート防止として厳密に守りたい処理は、これに頼らず別途対策してください。

---

## 7.12 Screen（画面の寸法・安全領域・向き・DPI）

画面の大きさ、カメラ穴・切り欠き・ジェスチャーバーに隠れない範囲（安全領域）、端末の向き、DPI を読む静的クラスです。
座標はすべて `Input.MousePos` と同じ「描画ターゲットの左上原点・Y 下向き・ピクセル」なので、`Input.MousePos` や `Touch.Position` とそのまま比べられます。

```csharp
// 画面（描画ターゲット）
Screen.Width          // int:   描画ターゲットの幅（ピクセル。Input.MousePos と同じ単位）
Screen.Height         // int:   描画ターゲットの高さ
Screen.SafeArea       // Rect:  安全領域（カメラ穴・切り欠き・ジェスチャーバーを避けた範囲。左上原点）
                      //        安全領域の無い環境（デスクトップ）では全画面 (0, 0, Width, Height)
Screen.Orientation    // ScreenOrientation: 画面の向き（Android は端末の回転から 4 方向、デスクトップはウィンドウの縦横比）
Screen.DPI            // float: OS が報告する論理 DPI（Android は densityDpi、Windows は 96 × 表示スケール）。取れなければ 96
Screen.DpScale        // float: 1 dp の画素数（表示倍率。Android は densityDpi ÷ 160〈Pixel 6a は 2.625〉、Windows は表示スケール）。
                      //        dp のルートキャンバスのレイアウトと同じ値。画素 → dp は「画素 ÷ DpScale」（安全領域の余白を dp にする等。W2-7）

// ScreenOrientation（Unity と同じ意味）
ScreenOrientation.Portrait            // 縦長・正立（端末の上端が上）
ScreenOrientation.PortraitUpsideDown  // 縦長・逆さ
ScreenOrientation.LandscapeLeft       // 横長。縦持ちから反時計回りに倒した向き（端末の上端が左）。デスクトップの横長ウィンドウもこれ
ScreenOrientation.LandscapeRight      // 横長。縦持ちから時計回りに倒した向き（端末の上端が右）

// Rect（不変値型。x, y が左上、width, height が大きさ）
var r = new SEED.Rect(x, y, width, height);   // new SEED.Rect(position, size) でも作れる
r.x  r.y  r.width  r.height                    // float（フィールド）
r.XMin  r.YMin  r.XMax  r.YMax                 // float: 左端・上端・右端（x + width）・下端（y + height）
r.Position  r.Size  r.Center                   // Vector2: 左上・大きさ・中心
r.Contains(point)                              // bool: 点が矩形の内側（境界を含む）か
SEED.Rect.Zero                                 // (0, 0, 0, 0)

// 例: スコア表示の下地を安全領域の左上に置く（カメラ穴・ジェスチャーバーを避ける）
public override void Update(ref NativeFrameContext ctx)
{
    var safe = SEED.Screen.SafeArea;
    const float margin = 16f;
    var size = new SEED.Vector2(200f, 48f);
    // Draw のスクリーンスペースは Screen と同じ左上原点・px。Draw.Rect は中心と大きさで描く
    var center = new SEED.Vector2(safe.XMin + margin + size.x * 0.5f, safe.YMin + margin + size.y * 0.5f);
    SEED.Draw.Rect(center, size, new SEED.Color(0f, 0f, 0f, 0.5f));
    // 右下に寄せるなら safe.XMax - margin / safe.YMax - margin を基準にする
}

// 例: 縦持ち・横持ちでレイアウトを切り替える（向きが変わったフレームだけ組み直す）
private SEED.ScreenOrientation _lastOrientation;
public override void Update(ref NativeFrameContext ctx)
{
    var orientation = SEED.Screen.Orientation;
    if (orientation == _lastOrientation) return;
    _lastOrientation = orientation;
    bool portrait = orientation is SEED.ScreenOrientation.Portrait or SEED.ScreenOrientation.PortraitUpsideDown;
    /* portrait ? 縦用の配置 : 横用の配置 */
}

// 例: 指の位置を画面の割合（0〜1）にする
var t = SEED.Input.GetTouch(0);
var ratio = new SEED.Vector2(t.Position.x / SEED.Screen.Width, t.Position.y / SEED.Screen.Height);
```

| 実行環境 | `Width` / `Height` | `SafeArea` | `Orientation` | `DPI` |
|---|---|---|---|---|
| Android（ウィンドウに合わせて描く・既定） | 画面の物理ピクセル（例 1080x2400） | カメラ穴・切り欠き・ナビゲーションバー（ジェスチャーバー）の辺だけ内側へ寄る | 端末の回転から 4 方向 | densityDpi（例 420） |
| Android（解像度を固定・レターボックス） | 内部解像度（project_settings の `window_width` x `window_height`） | 内部解像度の座標へ写した安全領域。黒帯に収まる穴・バーの分は削られない | 端末の回転から 4 方向 | densityDpi |
| デスクトップ（ウィンドウ Play・配布版） | ウィンドウのクライアント領域（解像度を固定なら内部解像度） | 全画面 | ウィンドウの縦横比（縦長 = Portrait / それ以外 = LandscapeLeft） | 96 × 表示スケール |
| エディタ埋め込みの Play | ゲームビューの大きさ | 全画面 | ゲームビューの縦横比 | 96 × 表示スケール |

> **重要**: `Screen` の値はエンジンが**フレームごとに 1 回**（スクリプトより前に）差し替えます。同じフレームの間は何度読んでも同じ値で、端末の回転や安全領域の変化は次のフレームから見えます。回転の直後の数フレームは、OS からの知らせと画面の大きさの変化が前後するため、安全領域が全画面・向きが縦横比からの値（`Portrait` / `LandscapeLeft`）になることがあります。

> **重要**: 安全領域は自動ではキャンバス UI に反映されません。ボタンや HUD を穴・ジェスチャーバーから避けたいときは、`Screen.SafeArea` を読んでスクリプトで位置を決めてください。Android の画面の向き（縦固定・横固定・両方）はプロジェクト設定の「画面の向き（モバイル）」で決まり、APK を作るときに固定されます（スクリプトからは変えられません）。Android でのスクリプトの実行（同梱 .NET・DLL の差し替え）は docs/android.md §17。

---

## 7.13 Platform（アプリのプラットフォーム機能・W1-1 の骨組み／W1-3 の目覚まし／W1-4a の鳴動・起動理由／W1-5 の通知・権限／W1-6 の画面・アプリ・触感／W1-8 のセンサー／2026-10-01 の OS の版・前面と背面・模擬の権限の操作）

目覚まし・通知・権限など **OS の機能**をスクリプトから使うための入口です（名前空間 `SEED.Platform`）。
W1-1 の橋渡しの骨組み（状態・往復の確かめ・イベントの受け口）に、W1-3 で**目覚ましの予約**（`Alarms`。この節の後半）、
W1-4a で**鳴動**（`Alarms.GetRinging` / `StopRinging`・音と通知）と**起動理由**（`App.LaunchReason`）・`Window.SetShowWhenLocked`、
W1-5 で**通知**（`Notifications`）と**権限**（`Permissions`）、W1-6 で**画面・アプリ・触感**（`Window.SetKeepScreenOn` / `SetSystemBarsVisible`・
`App.MoveTaskToBack` / `OpenUrl` / `OpenAppSettings`・`Haptics`）と**ディープリンク**（`LaunchKind.DeepLink`・`LaunchInfo.Uri`）、
W1-8 で**センサー**（`Sensors`。重力を除いた加速度）が加わりました（この節の最後。設計は docs/app_platform_roadmap.md §2.3）。
2026-10-01 に **OS の種類と版**（`App.Platform` / `App.OsVersion`）・**前面と背面の知らせ**（`platform.resumed` / `platform.paused`）・
**デスクトップの模擬の権限の操作**（環境変数・IPC `PLATFORM_SIM`・`PlatformDiagnostics.SimulatePermission` など）を足しました（「画面・アプリ・触感・ディープリンク」の後）。Android では別プロセス `:seed_platform`（Java）へ、
デスクトップ（エディタの Play・単体起動）ではエンジンの中の**模擬**へ届き、どちらも同じ形の JSON で答えます（仕組みは docs/android.md §25）。

```csharp
using SEED.Platform;   // 名前空間とクラス名が同じなので using を推奨（修飾するなら SEED.Platform.Platform.IsSupported）

// 状態
Platform.IsSupported       // bool: 使えるか（Android で Java 側の準備が済んでいる、またはデスクトップの模擬）。毎フレーム読んでよい
Platform.IsSimulated       // bool: デスクトップの模擬で動いているか
Platform.LastError         // string: 直前の呼び出しの失敗の理由（成功なら ""）
Platform.ErrorConnecting   // "connecting"           … Android の最初の呼び出し（:seed_platform へ接続中）。platform.connected を待って呼び直す
Platform.ErrorUnavailable  // "platform_unavailable" … 基盤が無い（Android で Java 側の準備が無い）
Platform.ErrorInvalidReply // "invalid_reply"        … 返答が約束の JSON でない（版の食い違い）

// 確かめ（往復とイベントの経路）
PlatformPingResult r = PlatformDiagnostics.Ping();    // 往復を 1 回計測（例外を投げない。送った合言葉が echo で返るかも確かめる）
r.Ok  r.RoundTripMs  r.ServicePid  r.ServiceUptimeMs  r.Simulated  r.Error   // r.ToString() でログ向けの 1 行
bool accepted = PlatformDiagnostics.EmitTestEvent("hello"); // 試験イベント platform.test_event を 1 つ流す（次のフレーム以降に届く）

// イベント（エンジンがフレームに 1 回、スクリプトの BeginFrame より前に配る）
this.On(PlatformEvents.TestEvent, (string json) => { });     // 推奨: SEED.Events 経由（名前 = "platform.…"・引数 = イベントの JSON 全体）
PlatformEvents.OnEvent += (string name, string json) => { }; // すべてのイベント（寿命に追従しない。OnDestroy で必ず -= する）
PlatformEvents.Connected      // "platform.connected"      … :seed_platform へつながった（Android。data.connect_ms・data.pid）
PlatformEvents.ConnectFailed  // "platform.connect_failed" … つなげなかった（Android。data.error）
PlatformEvents.Disconnected   // "platform.disconnected"   … :seed_platform のプロセスが居なくなった（次の呼び出しでつなぎ直す）
PlatformEvents.TestEvent      // "platform.test_event"     … 試験イベント（data.message・data.pid）

// 例: 起動時に往復を確かめる（Android の最初の 1 回は接続を始めるだけで失敗し、つながると platform.connected が届く）
public override void OnStart()
{
    this.On(SEED.Platform.PlatformEvents.Connected, (string json) => Measure());
    Measure();
}
private void Measure()
{
    var r = SEED.Platform.PlatformDiagnostics.Ping();
    if (!r.Ok && r.Error == SEED.Platform.Platform.ErrorConnecting) return;  // つながったら上の this.On で呼び直される
    SEED.Debug.Log($"往復 {r.RoundTripMs:0.00} ms（答えたプロセス {r.ServicePid}）");
}
```

| 項目 | Android | デスクトップ（模擬） |
|---|---|---|
| `IsSupported` / `IsSimulated` | true / false（APK の Java 側の準備が済んでいれば） | true / true |
| 最初の呼び出し | `:seed_platform` の起動（約 120 ms）を描画のスレッドで待たず、`LastError == "connecting"` で失敗。背面でつながると `platform.connected` | すぐ答える |
| 往復の時間（`Ping`） | 温まっていれば 1 ms 未満（Binder の往復 1 回） | 0.1 ms 程度（プロセスの中。最初の 1 回は JIT で遅い） |
| `ServicePid` / `ServiceUptimeMs` | `:seed_platform` の pid・起動からの ms | SEED.exe の pid・模擬を作ってからの ms |
| イベントの JSON | `{"name":"platform.…","seq":番号,"time_ms":UTC の epoch ミリ秒,"data":{…}}`（接続の知らせは seq 0） | 同じ形（seq は模擬の中の番号） |

> **重要**: SEED.Platform の呼び出しは**同期**で、スクリプトのフレームの中で動きます。Android では温まっていれば 1 ms 未満ですが、`:seed_platform` がまだ起きていない**最初の呼び出しは待たずに失敗**します（`Platform.LastError == Platform.ErrorConnecting`）。`PlatformEvents.Connected` のイベントを受けてから呼び直してください。

> **重要**: イベントはすぐには届きません。エンジンがフレームの頭で取り出し、次の BeginFrame で配ります（`EmitTestEvent` を呼んだフレームの中では見えません）。受け口は `this.On("platform.…", (string json) => …)` を推奨します（スクリプトの破棄で自動的に外れる）。`PlatformEvents.OnEvent` に足したハンドラは `OnDestroy` で必ず外してください（ホットリロードではエンジンが全部外します）。

> **重要**: 起動の直後（エディタでは Play の開始の直後）に届いたイベントは、最初のシーンのスクリプトの `OnStart` がすべて済むまでエンジンが保持し、`OnStart` の次のフレームの BeginFrame でまとめて配ります。`OnStart` で `this.On` した受け手は、起動し直した直後の `platform.permission_changed` なども受け取れます（2026-09-29 から。上限 256 件を超えた古い分は捨てるので、起動時の状態は `Permissions.Check` などでも確かめてください）。`OnStart` の中で生成したアクターのスクリプトは 1 フレーム遅れて `OnStart` するため、保持していた分を受け取れないことがあります。

### 目覚まし（`Alarms`。W1-3 は予約の基盤）

決まった時刻に確実に鳴らすための**予約**です。Android では別プロセス `:seed_platform` が予約の控え（端末保護ストレージ）を持ち、
`AlarmManager.setAlarmClock` で張ります（Doze でも時刻どおり・ステータスバーに目覚ましの印）。再起動・時刻の変更・アプリの更新・
正確なアラームの許可で控えから張り直し、電源断などで過ぎた予約は**鳴らさずに** `platform.alarm.missed` を記録します。
鳴ったことはイベント `platform.alarm.fired` で届き、同時に**鳴動**が始まります（W1-4a。後の「鳴動と起動理由」）。
Android では APK に機能 `alarm` が要ります（`project_settings.json` の `android.features` に `"alarm"`。docs/android.md §25.10）。

```csharp
using SEED.Platform;

// 予約（同期で「受け付けたか」だけを返す。false なら Platform.LastError）
var request = new AlarmRequest
{
    Id = "morning",                                          // 1〜128 文字。同じ ID は置き換え
    TriggerAtUtcMs = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(), // UTC の epoch ミリ秒（壁時計の計算はアプリ）
    SoundAsset = "assets://sounds/bell.ogg",                 // "assets://…" か端末のファイルの絶対パス。空・読めなければ同梱の既定の音
    Vibrate = true,                                          // 鳴動中に振動する（繰り返し）
    ForceVolume = -1f, KeepVolume = false,                   // アラームの音量（STREAM_ALARM）を鳴動中だけ 0..1 に（負 = 触らない）・1 秒ごとに戻す
    FadeInSeconds = 5f, MaxRingMinutes = 60,                 // 音量の漸増（秒）・安全弁（分。過ぎたら自動で止まる）
    Title = "起きる時間", Body = "…",                        // 鳴動の通知・フルスクリーン通知の題と本文
    PayloadJson = "{\"alarm\":\"morning\"}",                  // イベントにそのまま戻る任意の JSON（16384 文字まで）
};
bool ok = Alarms.Schedule(request);
bool cancelled = Alarms.Cancel("morning");                   // 無い ID でも true（冪等）
bool cleared = Alarms.CancelAll();
ScheduledAlarm[] list = Alarms.GetScheduled();               // 予定時刻の順（失敗は空の配列）
// a.Id  a.TriggerAtUtcMs  a.PayloadJson  a.Title  a.Body  a.Sound（Android は書き出した音源の絶対パス）  a.CreatedAtUtcMs
Alarms.IsSupported       // bool: 使えるか（IPC なし。APK に機能 alarm が無いと分かった後は false）
Alarms.CanScheduleExact  // bool: 正確なアラームを張れるか（:seed_platform へ問い合わせる。毎フレーム読まない）
Alarms.MaxScheduledAlarms // 64: 控えに持てる予約の数

// イベント（SEED.Events。引数はイベントの JSON 全体。型付きの値は TryParse で読む）
this.On(AlarmFiredEvent.Name, (string json) =>               // "platform.alarm.fired"
{
    if (AlarmFiredEvent.TryParse(json, out AlarmFiredEvent e))
        SEED.Debug.Log($"{e.Id} が鳴った（予定 {e.ScheduledAtUtcMs}・配信 {e.FiredAtUtcMs}・{e.PayloadJson}）");
});
this.On(AlarmMissedEvent.Name, (string json) => { });        // "platform.alarm.missed"
this.On(AlarmsRescheduledEvent.Name, (string json) => { });  // "platform.alarms.rescheduled"

// 失敗の理由（Platform.LastError）
Alarms.ErrorExactAlarmNotAllowed // "exact_alarm_not_allowed" … Android 12 系で正確なアラームの特別なアクセスが無い（黙って不正確な予約にしない）
Alarms.ErrorFeatureNotEnabled    // "feature_not_enabled"     … APK に機能 alarm が無い
Alarms.ErrorInvalidArgument      // "invalid_argument"        … ID が空・時刻が 0 以下・文字列が長すぎる等
Alarms.ErrorTooManyAlarms        // "too_many_alarms"         … 予約が 64 件（新しい ID だけ断る。置き換えは通る）
Alarms.ErrorStoreWriteFailed     // "store_write_failed"      … 控えを書けなかった（予約は張られていない）
Alarms.ErrorScheduleFailed       // "schedule_failed"         … AlarmManager が受け付けなかった
```

| イベント（SEED.Events の名前） | 型（`TryParse`） | 中身 | いつ届くか |
|---|---|---|---|
| `platform.alarm.fired` | `AlarmFiredEvent` | `Id`・`ScheduledAtUtcMs`（鳴るはずだった時刻）・`FiredAtUtcMs`（配信を受けた時刻）・`PayloadJson`・`Simulated` | 予定時刻に配信され、鳴動が始まった（か待ち行列に入った）。予約は控えから消える（一回限り）。このイベントを受けた時点で `GetRinging` はその鳴動を返す。アプリが動いていなければ、次に SEED.Platform へつないだとき |
| `platform.alarm.missed` | `AlarmMissedEvent` | `Id`・`ScheduledAtUtcMs`・`Reason`（`DeviceOff` / `PermissionRevoked` / `StartFailed` / `Unknown`）・`ReasonName`・`PayloadJson` | 電源断・強制停止・更新・許可の取り消しの間に予定時刻を過ぎていた（張り直し・起動時の照合で見つけ、鳴らさずに控えから消した）。`StartFailed` は配信は届いたが鳴動の前景サービスを起こせなかった（このときは `fired` の代わりに届く） |
| `platform.alarms.rescheduled` | `AlarmsRescheduledEvent` | `Reason`（`Boot` / `TimeChanged` / `PackageReplaced` / `PermissionChanged` / `Unknown`）・`ReasonName`・`Count`・`Missed`・`Failed` | 控えから張り直した（控えが空なら届かない）。`TimeChanged` ではアプリが次の時刻を計算し直して予約し直す |
| `platform.alarm.ring_stopped` | `AlarmRingStoppedEvent` | `Id`・`Reason`（`Stopped` / `Timeout` / `Error` / `Unknown`）・`ReasonName`・`ScheduledAtUtcMs`・`PayloadJson`・`Simulated` | 鳴動が終わった（W1-4a）。`StopRinging`・安全弁（`MaxRingMinutes`）・鳴らし続けられなかった。待ち行列に次があれば続けて鳴り始める |
| `platform.alarm.queued` | `AlarmQueuedEvent` | `Id`・`ScheduledAtUtcMs`・`WaitingFor`（今鳴っている予約の ID）・`PayloadJson`・`Simulated` | 別の予約の鳴動中に時刻が来た（W1-4a）。捨てずに待たせ、今の鳴動が止まったら続けて鳴らす（`fired` の後に届く） |
| `platform.launch` | `LaunchInfo`（`TryParseEvent`） | `Kind`・`Id`・`ActionId`・`ScheduledAtUtcMs`・`FiredAtUtcMs`・`PayloadJson`・`Uri`（W1-6） | アプリが動いている間に目覚まし・通知の操作・ディープリンク（W1-6）・ランチャーで開き直された（Android の onNewIntent。W1-4a）。起動のときの理由は `App.LaunchReason` |

| 項目 | Android | デスクトップ（模擬） |
|---|---|---|
| 予約の持ち方 | `:seed_platform` の控え（端末保護ストレージの `seed_platform/alarms.json`）＋ `setAlarmClock`。アプリを閉じても・再起動しても残る | プロセスの中の予約表。**Play を止めると消える** |
| 鳴ったとき | `platform.alarm.fired` を記録し、`:seed_platform` の前景サービスが音（`USAGE_ALARM`）・振動・フルスクリーン通知を出す（W1-4a） | 壁時計が予定時刻を過ぎた次のフレームで `platform.alarm.fired`（`Simulated == true`）と鳴動の状態。**音なし**（`[SEED PLATFORM]` のログだけ） |
| `CanScheduleExact` | Android 12 系は特別なアクセス次第。13 以降（`USE_EXACT_ALARM`）と 11 以前は true | true |
| 音源（`SoundAsset`） | `assets://…` はエンジンが `files/seed_platform/sounds/<内容のハッシュ>.<拡張子>`（端末保護ストレージ）へ書き出してから予約（読めなければ同梱の既定の音、それも駄目なら端末の既定のアラーム音） | 控えに持つだけ（`ScheduledAlarm.Sound` は渡したまま） |

> **重要**: 予約は **UTC の絶対時刻**の一回限りです。「毎朝 7:00」のような繰り返しはアプリが次の 1 回を計算して予約し、鳴った（`platform.alarm.fired`）・鳴らなかった（`platform.alarm.missed`）・時刻が変わった（`platform.alarms.rescheduled` の `TimeChanged`）ときに次を予約し直してください。Android の最初の呼び出しは他の SEED.Platform と同じく `Platform.ErrorConnecting` で失敗するので、`PlatformEvents.Connected` の後に呼び直します。

### 鳴動と起動理由（`Alarms.GetRinging` / `StopRinging`・`App.LaunchReason`・`Window.SetShowWhenLocked`。W1-4a）

予約の時刻が来ると**鳴動**が始まり、`StopRinging` か安全弁（`MaxRingMinutes`）まで鳴り続けます。Android では別プロセス `:seed_platform` の
前景サービスが鳴らすので、**エンジンが落ちても・最近のタスクからアプリを消しても・通知をスワイプしても止まりません**（止めるのはスクリプトの `StopRinging` だけ）。
鳴り始めるとフルスクリーン通知が出て、画面オフ・ロック中ならアプリ（`MainActivity`）がロック画面の上に起動し、端末の使用中ならヘッドアップ通知になります
（本文のタップでアプリへ）。どちらの起動でも `App.LaunchReason.Kind == LaunchKind.Alarm` になるので、スクリプトは鳴動画面を出します。

```csharp
using SEED.Platform;

// 鳴動の状態（呼ぶたびに :seed_platform へ問い合わせる。毎フレーム読まない）
RingingAlarm? ringing = Alarms.GetRinging();              // 鳴っていなければ null（失敗も null。Platform.LastError）
// r.Id  r.ScheduledAtUtcMs  r.StartedAtUtcMs（鳴り始め。待ち行列から繰り上がったときはその時刻）  r.PayloadJson  r.Simulated
bool ok = Alarms.StopRinging("morning");                  // 止める（解除・スヌーズ）。音・振動・鳴動の通知が止まり ring_stopped が届く
bool ok2 = Alarms.StopRinging();                          // ID を省くと今鳴っているもの。鳴っていなくても true（冪等）
// 待ち行列にいる予約の ID を渡すと、鳴らさずに外す（ring_stopped(Stopped) が届く）

// 起動理由（Android でも IPC なし。デスクトップの模擬は Launcher〈単体起動の --deep-link=<URI> があれば DeepLink〉）
LaunchInfo launch = App.LaunchReason;
// launch.Kind（LaunchKind.Launcher / Alarm / NotificationTap / NotificationAction / AlarmClockInfo / Other / DeepLink〈W1-6〉）  launch.KindName
// launch.Uri（DeepLink のときの URL。ほかは ""。W1-6。後の「画面・アプリ・触感・ディープリンク」）
// launch.Id（予約の ID）  launch.ActionId（通知の操作。鳴動の通知の「開く」は LaunchInfo.ActionOpen == "open"）
// launch.ScheduledAtUtcMs  launch.FiredAtUtcMs（配信を受けた時刻。鳴動画面が出るまでの遅れを測る起点）  launch.PayloadJson
this.On(LaunchInfo.EventName, (string json) =>             // "platform.launch": 動いている間に目覚まし・通知の操作で開き直された
{
    if (LaunchInfo.TryParseEvent(json, out LaunchInfo e) && e.Kind == LaunchKind.Alarm) { /* 鳴動画面を出す */ }
});

// 画面（Android でも IPC なし。デスクトップは受け付けてログだけ）
bool lowered = Window.SetShowWhenLocked(false);           // ロック画面の上に出す＋画面を点ける を下ろす（true で上げる）。ロックは解除しない
Window.ErrorNoActivity                                    // "no_activity" … 操作する画面（Activity）が無い（Android）

// 例: 起動時に鳴動画面を出すか決める（鳴っていなければ普通の画面。最近のタスクからの開き直しは Launcher になる）
public override void OnStart()
{
    LaunchInfo launch = App.LaunchReason;
    if (launch.Kind == LaunchKind.Alarm) ShowRingScreen(launch.Id, launch.PayloadJson);
    this.On(LaunchInfo.EventName, (string json) =>
    {
        if (LaunchInfo.TryParseEvent(json, out LaunchInfo e) && e.Kind == LaunchKind.Alarm) ShowRingScreen(e.Id, e.PayloadJson);
    });
}
private void OnDismissPressed(string id)
{
    Alarms.StopRinging(id);                               // 音を止める
    Window.SetShowWhenLocked(false);                      // ロック画面の上から降りる（電源ボタンでロック画面が出るように）
}
```

| 項目 | Android | デスクトップ（模擬） |
|---|---|---|
| 音 | `USAGE_ALARM` 固定（アラームの音量）・ループ・`FadeInSeconds` で漸増・音声フォーカスは取らない（エンジンの音が鳴っても止まらない） | 鳴らさない（ログだけ） |
| 振動・通知 | `Vibrate` なら繰り返しの振動。通知チャネル `seed_platform_alarm`（重要度 HIGH・チャネルの音なし）にフルスクリーン通知・常駐・「開く」の操作。**通知の許可（Android 13+ の POST_NOTIFICATIONS）が無いと通知は出ない**（音と振動は続く） | なし |
| 安全弁 | `MaxRingMinutes` で自動で止まり `ring_stopped(Timeout)` | 同じ（フレームの頭で見る） |
| 鳴動中に次の予約の時刻 | 捨てずに待ち行列（`queued`）。今の鳴動が止まったら続けて鳴る（通知も出し直す） | 同じ |
| 起動理由 | exported=false の入口 `PlatformEntry` 経由の起動だけを `Alarm` などとして信用（他のアプリからは偽造できない）。最近のタスクからの開き直しは `Launcher` | 常に `Launcher` |
| ロック画面の上 | `Alarm` で起動したときは自動で上がる（`setShowWhenLocked`・`setTurnScreenOn`）。**下ろすのはアプリ**（`Window.SetShowWhenLocked(false)`） | 何もしない |

> **重要**: 目覚ましで起動したアプリは、ロック画面の上に出たままになります。鳴動を片付けたら必ず `Window.SetShowWhenLocked(false)` で下ろしてください（下ろさないと、アプリを開いたまま電源ボタンを押してもロック画面が出ません）。ロックは解除しないので、鳴動画面より先（お金の操作など）へ進むときは利用者にロックを解除してもらう設計にしてください。

> **重要**: 鳴動はアプリのスクリプトが `StopRinging` を呼ぶまで続きます（通知に「止める」ボタンはありません）。通知の「開く」は起動理由 `NotificationAction`（`ActionId == "open"`）でアプリを開くだけです。

### 通知（`Notifications`。W1-5）

チャネルを作って通知を出します。Android では別プロセス `:seed_platform` が出すので、**エンジンが落ちても・アプリを閉じても通知は残ります**
（例: スヌーズ中の知らせ）。本文のタップとボタン（最大 3 つ）はどちらも**アプリを直接開き**（Android 12+ の通知のトランポリンの禁止に合わせて
受信機を挟まない）、起動理由が `LaunchKind.NotificationTap`（`Id` = 通知の ID）か `LaunchKind.NotificationAction`（`ActionId` = ボタンの ID）に
なります。デスクトップは模擬で、画面には何も出さず `[SEED PLATFORM] 通知: …` のログだけです。
Android では APK に機能 `notifications`（か `alarm`。どちらも `POST_NOTIFICATIONS` を入れる）が要り、Android 13 以降は通知の実行時の許可が要ります（次の「権限」）。

```csharp
using SEED.Platform;

// チャネル（起動のたびに呼んでよい。同じ ID は名前と説明だけ変わる。重要度は作った後は利用者だけが変えられる）
bool ok = Notifications.EnsureChannel("reminders", "リマインダー", NotificationImportance.High, "スヌーズ中の知らせ");
// NotificationImportance.Low（音なし）/ Default（音あり）/ High（音あり・画面の上に出る）。"seed_platform" で始まる ID は使えない

// 出す（同期で「受け付けたか」だけ。false なら Platform.LastError）
bool shown = Notifications.Show(new NotificationRequest
{
    Id = "snooze",                                   // 1〜128 文字。同じ ID は置き換え。Cancel もこの ID
    ChannelId = "reminders",                         // EnsureChannel で作ったもの
    Title = "スヌーズ中", Body = "7:10 にもう一度鳴ります", // Body は長文でも通知を開くと全部見える（BigTextStyle）。どちらも 4096 文字まで
    Ongoing = true,                                  // 常駐（スワイプで消えにくい・本文を押しても消えない）。false なら本文を押すと消える
    Category = NotificationRequest.CategoryReminder, // "alarm" / "reminder" / "status" / "event" / "progress"（それ以外は付けない）
    Actions = new[] { new NotificationAction("stop", "止める"), new NotificationAction("open", "開く") }, // 最大 3。ID は重ならないこと
    PayloadJson = "{\"alarm\":\"morning\"}",          // 起動理由の PayloadJson にそのまま戻る（16384 文字まで）
});
bool cancelled = Notifications.Cancel("snooze");    // 出ていない ID でも true（冪等）
bool enabled = Notifications.AreEnabled;             // アプリの通知が端末で有効か（:seed_platform へ問い合わせる。毎フレーム読まない）
Notifications.IsSupported                            // bool（IPC なし。APK に機能が無いと分かった後は false）
NotificationRequest.MaxActions                       // 3

// ボタン・本文のタップで開かれたとき（起動のときは App.LaunchReason、動いている間は platform.launch）
this.On(LaunchInfo.EventName, (string json) =>
{
    if (!LaunchInfo.TryParseEvent(json, out LaunchInfo e)) return;
    if (e.Kind == LaunchKind.NotificationAction && e.Id == "snooze" && e.ActionId == "stop")
        Notifications.Cancel("snooze");             // ボタンを押しても通知は消えないので、アプリが消す
});

// 失敗の理由（Platform.LastError）
Notifications.ErrorNotificationsDisabled // "notifications_disabled" … 通知が無効（Android 13+ の許可が無い・利用者が切った・チャネルが止められた）
Notifications.ErrorChannelNotFound       // "channel_not_found"      … チャネルが無い（先に EnsureChannel）
Notifications.ErrorFeatureNotEnabled     // "feature_not_enabled"    … APK に機能 notifications（か alarm）が無い
Notifications.ErrorInvalidArgument       // "invalid_argument"       … ID が空・ボタンが 4 つ以上・ボタンの ID の重なり・長すぎる文字列・予約済みのチャネル ID
```

| 項目 | Android | デスクトップ（模擬） |
|---|---|---|
| 出す場所 | `:seed_platform` の `NotificationManager.notify(tag = Id, …)`（アプリを閉じても残る） | プロセスの中の一覧と `[SEED PLATFORM] 通知: …` のログ。**Play を止めるとチャネルも通知も消える** |
| 本文のタップ・ボタン | アプリを直接開く（信頼できる入口 `PlatformEntry` 経由）。起動理由 `NotificationTap` / `NotificationAction`（`Id`・`ActionId`・`PayloadJson`） | 押す手段は無い（起動理由は常に `Launcher`） |
| 許可 | Android 13+ は `POST_NOTIFICATIONS`（無いと `notifications_disabled`）。12 以前は利用者が切っていなければ出る | `AreEnabled` は常に true |
| チャネル | 端末の設定の「通知」に名前が出る。作った後の重要度は利用者だけが変えられる | 名前と説明だけ持つ |

> **重要**: ボタンを押しても通知は消えません（Android の決まり）。起動理由（`NotificationAction`）を見て処理したら `Notifications.Cancel(id)` で消してください。常駐でない通知は本文を押すと消えます。`EnsureChannel` と `Cancel` は、案（roadmap §2.3）の void ではなく bool を返します（接続中・機能なしを見分けるため）。

### 権限（`Permissions`。W1-5）

通知・正確なアラーム・フルスクリーン通知の**状態**を調べ、**求め**、**設定の画面**を開きます。Android ではメインプロセスが答えるので
（IPC なし）、最初の呼び出しでも `connecting` になりません。デスクトップの模擬は既定で v1 の 3 種が `Granted` で、状態と「求めたときの答え」を
環境変数・IPC・スクリプトで変えられます（2026-10-01。後の「デスクトップの模擬の操作」）。

```csharp
using SEED.Platform;

PermissionStatus s = Permissions.Check(PermissionKind.PostNotifications); // 失敗は Unknown（Platform.LastError）。呼ぶたびに問い合わせる（軽い）
// PermissionStatus.Granted / Denied / DeniedPermanently / NeedsSettings / NotApplicable / Unknown
int requestId = Permissions.Request(PermissionKind.PostNotifications);    // すぐ要求の ID（1 以上）を返す。失敗は 0。結果はイベント
bool opened = Permissions.OpenSettings(PermissionKind.ExactAlarm);         // 設定の画面（結果のイベントは無い。戻って変わっていれば permission_changed）
// PermissionKind.PostNotifications / ExactAlarm / FullScreenIntent / RecordAudio・SendSms（v2 の予約。常に NotApplicable）

this.On(PermissionResultEvent.Name, (string json) =>           // "platform.permission_result"
{
    if (PermissionResultEvent.TryParse(json, out PermissionResultEvent e) && e.RequestId == requestId)
        SEED.Debug.Log($"{e.Kind} → {e.Status}");                // e.RequestId  e.Kind  e.KindName  e.Status  e.StatusName  e.Simulated
});
this.On(PermissionChangedEvent.Name, (string json) => { });     // "platform.permission_changed": 前面へ戻ったときに状態が変わっていた（e.Kind・e.Status）
this.On(App.ResumedEvent, (string json) => { /* 設定の画面から戻った: 変わっていなくても Check で問い直す */ }); // "platform.resumed"（2026-10-01）

// 失敗の理由（Platform.LastError）
Permissions.ErrorFeatureNotEnabled // "feature_not_enabled" … APK にその種類の機能が無い（通知は notifications か alarm、ほかは alarm）
Permissions.ErrorInvalidArgument   // "invalid_argument"    … PermissionKind.Unknown を渡した等
Permissions.ErrorNoActivity        // "no_activity"         … 画面（Activity）が無い（Request・OpenSettings）

// 例: 目覚ましを予約する前に確かめる（Android 12 系で正確なアラームの特別なアクセスが無いと Alarms.Schedule が失敗する）
if (Permissions.Check(PermissionKind.ExactAlarm) == PermissionStatus.NeedsSettings)
    Permissions.Request(PermissionKind.ExactAlarm);             // 説明を出してから。設定の画面が開き、戻ると permission_result
```

| 種類 | Android の状態（`Check`） | `Request` のとき | `OpenSettings` の画面 |
|---|---|---|---|
| `PostNotifications` | 13+: 許可なら `Granted`（通知が設定で切られていれば `NeedsSettings`）、未許可は `Denied`、二度拒否されて確認の画面が出なくなったら `DeniedPermanently`。12 以前: 通知の設定で `Granted` か `NeedsSettings` | 13+ で未許可なら実行時の確認の画面（永続の拒否なら画面は出ずにすぐ `DeniedPermanently`）。`NeedsSettings` なら通知の設定の画面 | アプリの通知の設定 |
| `ExactAlarm` | 13+: `Granted`（`USE_EXACT_ALARM`）。12 系: 特別なアクセスで `Granted` か `NeedsSettings`。11 以前: `NotApplicable` | `NeedsSettings` なら「アラームとリマインダー」の設定の画面 | 同じ（11 以前はアプリ情報） |
| `FullScreenIntent` | 14+: 特別なアクセスで `Granted` か `NeedsSettings`。13 以前: `Granted` | `NeedsSettings` なら「全画面通知」の設定の画面 | 同じ（13 以前はアプリ情報） |
| `RecordAudio` / `SendSms` | `NotApplicable`（v2 の予約） | すぐ `NotApplicable` | アプリ情報 |

- `Request` の結果: 既に `Granted`・`NotApplicable` なら画面を出さずに次のフレームで届く。確認の画面・設定の画面なら、利用者が答えて（戻って）から届く。同じ種類を重ねて求めると、出ている画面の結果がそれぞれの ID で届く。
- `PermissionChangedEvent`: 前面へ戻るたび（Android の onResume）に、APK に機能がある 3 種を前回の onResume と比べ、違えば届く（確認の画面で許可したときも `PermissionResultEvent` の後に届く）。前回の状態は端末に保存されるので、**設定で通知をオフにされて Android がアプリを止め、起動し直した最初の onResume でも届く**（`granted → denied`。W1-7 の M7 を直した）。インストール後の最初の onResume（前回の状態が無い）では届かない。起動し直した直後の分は最初のフレームで配られるので、それより後に `On` するスクリプトは受け取れないことがある（起動時は `Check` でも確かめる）。
- 模擬: `Check` は模擬の状態（既定は v1 の 3 種が `Granted`・v2 は常に `NotApplicable`）、`Request` は画面を出さずに模擬の利用者の答え（既定は許可）を当てて次のフレームで `permission_result`（状態が変われば続けて `permission_changed`）、`OpenSettings` はログだけ。模擬の状態を変えたときもすぐ `permission_changed`（2026-10-01 から。後の「デスクトップの模擬の操作」）。
- 設定の画面から戻ったときに状態が**変わっていなければ `permission_changed` は届きません**。戻ったときの問い直しは `App.ResumedEvent`（`platform.resumed`。2026-10-01）を受けて `Check` してください。
- `Check` の失敗は案（roadmap §2.3）に無い `PermissionStatus.Unknown`、`OpenSettings` は void ではなく bool を返す（失敗を見分けるため）。

> **重要**: Android 13 以降、通知の確認の画面は利用者が 2 回拒否すると二度と出ません（`DeniedPermanently`）。求める前に理由を画面で説明し、`DeniedPermanently` になったら `OpenSettings(PermissionKind.PostNotifications)` で設定の画面へ案内してください。確認の画面の外側を押して閉じた（どちらも選ばなかった）ときは `Denied` のままで、次の `Request` でもう一度出る見込みです（実機では未確認）。

### 画面・アプリ・触感・ディープリンク（`Window`・`App`・`Haptics`。W1-6）

画面の切り替え（点けたまま・システムバー）、アプリとしての操作（閉じずに背面へ・URL を開く・アプリ情報）、触感、ディープリンクで開かれたときの URL です。
Android ではどれもメインプロセスが答える（IPC なし）ので、最初の呼び出しでも `connecting` になりません。機能（`android.features`）の opt-in は要りません
（ディープリンクで開かれるには、機能 `deep_links` と `android.deep_links` の intent-filter が要ります。docs/project_system.md）。デスクトップは模擬です（下の表）。

```csharp
using SEED.Platform;

// 画面（すぐ返る。切り替えは少し後に効く。false なら Platform.LastError）
bool a = Window.SetKeepScreenOn(true);            // 画面を点けたままにする（前面に見えている間だけ。電源ボタンの消灯は止めない。既定は false）
bool b = Window.SetSystemBarsVisible(false);      // ステータスバー・ナビゲーションバーを隠す（true で出す）。起動時の既定は android.system_bars
                                                  // 安全領域 SEED.Screen.SafeArea はこれに追従する（次のフレーム以降）

// アプリ（false なら Platform.LastError）
bool back = App.MoveTaskToBack();                 // 閉じずに背面へ（戻るの最上位で。閉じるとプロセスが終わり、次の起動が冷える）
bool opened = App.OpenUrl("https://example.com"); // ブラウザ等で開く（http / https / mailto / tel / アプリ独自の scheme）
bool settings = App.OpenAppSettings();            // 端末の「アプリ情報」（権限の種類ごとの画面は Permissions.OpenSettings）
App.ErrorNoHandler          // "no_handler"         … その URL を開けるアプリが無い（Android）
App.ErrorSchemeNotAllowed   // "scheme_not_allowed" … file: / content: / javascript: は開かない
App.ErrorInvalidArgument    // "invalid_argument"   … 空・scheme が無い・制御文字・App.MaxUrlLength（8192）文字を超える
App.ErrorNoActivity         // "no_activity"        … 画面（Activity）が無い（Android）

// 触感（同期。振動子の無い端末は false・Platform.LastError == Haptics.ErrorNoVibrator〈"no_vibrator"〉）
Haptics.Tap();                                    // 軽いクリックの触感（ボタンを押したときなど）
Haptics.Vibrate(40);                              // 40 ms の振動（1〜Haptics.MaxVibrateMilliseconds〈5000〉。長い値は 5000 にそろえる。0 以下は invalid_argument）

// ディープリンク（起動のときは App.LaunchReason、動いている間は platform.launch）
LaunchInfo launch = App.LaunchReason;
if (launch.Kind == LaunchKind.DeepLink) HandleLink(launch.Uri);
this.On(LaunchInfo.EventName, (string json) =>
{
    if (LaunchInfo.TryParseEvent(json, out LaunchInfo e) && e.Kind == LaunchKind.DeepLink) HandleLink(e.Uri);
});

// 例: 戻るの最上位で閉じずに背面へ（Android の戻るキーは KeyCode.Escape で届く。§6.5）
public override void Update(ref NativeFrameContext ctx)
{
    if (SEED.Input.GetKeyDown(SEED.KeyCode.Escape) && !CloseTopDialog()) App.MoveTaskToBack();
}
```

| 項目 | Android | デスクトップ（模擬） |
|---|---|---|
| `SetKeepScreenOn` | 窓の `FLAG_KEEP_SCREEN_ON` | 状態を記録してログだけ |
| `SetSystemBarsVisible` | バーを出す・隠す（隠している間は端からのスワイプで一時的に出せ、アプリへ戻ると隠し直す）。安全領域がバーの分だけ変わる | 状態を記録してログだけ（安全領域は全画面のまま） |
| `MoveTaskToBack` | `moveTaskToBack(true)`。ランチャー・最近のタスクから戻ると起動理由 `Launcher` の `platform.launch` が届く | ログだけ |
| `OpenUrl` | `ACTION_VIEW`（ブラウザは別のタスクで開き、戻るとアプリへ）。開けるアプリが無ければ `no_handler` | 同じ規則で判定し、`http` / `https` / `mailto` だけを PC の既定のアプリで開く（ほかの scheme は判定だけで true）。環境変数 `SEED_PLATFORM_SIM_NO_OPEN=1` なら何も開かない |
| `OpenAppSettings` | 設定の「アプリ情報」 | ログだけ |
| `Haptics` | `Tap` は端末の「クリック」、`Vibrate` は決まった長さ。Android 13+ は利用者の「振動と触感」の設定（`Tap` はタッチ、`Vibrate` はメディア）が効く（切られていると振動しないが、戻り値は true のまま） | 回数を記録してログだけ（振動しない） |
| ディープリンク | intent-filter に合った URL（と、他のアプリが明示して送った URL）で `DeepLink`・`Uri`。最近のタスクからの開き直しは `Launcher` | 単体起動の `SEED.exe … --deep-link=<URI>` のときだけ `DeepLink` |

> **重要**: ディープリンクの `Uri` は**他のアプリも送れる入力**です（Android の MainActivity はランチャーのために外から起動でき、intent-filter を通らない明示の起動でも同じに見えます）。scheme・host・パスを検査し、お金やデータを動かす操作は URL だけで行わず、画面で利用者に確かめてから行ってください。

> **重要**: 目覚ましで上がった「ロック画面の上に出す」（`SetShowWhenLocked`）を下ろし忘れても、ランチャー・最近のタスクから開き直したときはエンジンが下ろします（W1-6）。ただしアプリを開いたまま電源ボタンを押したときは下ろし忘れのまま（ロック画面が出ない）なので、鳴動を片付けたら `SetShowWhenLocked(false)` を呼ぶ約束は変わりません。

- `MoveTaskToBack`・`OpenAppSettings`・`Haptics.Tap` / `Vibrate`・`SetKeepScreenOn`・`SetSystemBarsVisible` は、案（roadmap §2.3）の void ではなく bool を返します（失敗を見分けるため）。`LaunchKind.DeepLink` は列挙の末尾に足しました（既存の値の番号は変えない）。
- 仕組み（命令・URL の規則・パッケージの可視性・振動の種類）は docs/android.md §25.15。

### OS の種類と版・前面と背面（`App.Platform` / `App.OsVersion`・`platform.resumed` / `platform.paused`。2026-10-01）

OS の版による出し分け（通知の実行時の許可は Android 13 以上など）と、**前面へ戻った・前面を離れた**ことの知らせです（Wake or Pay の W3-5 で見つかった不足）。
Android はメインプロセスが答える・流す（IPC なし）ので、最初の呼び出しでも `connecting` になりません。デスクトップは模擬です（下の表）。

```csharp
using SEED.Platform;

// OS の種類と版（最初に成功した値を控えるので毎フレーム読んでよい。取れなければ Unknown / 0 で Platform.LastError に理由）
PlatformKind os = App.Platform;   // PlatformKind.Android / Windows / MacOS / Linux / Unknown（デスクトップはホストの OS）
int version = App.OsVersion;      // Android は Build.VERSION.SDK_INT（API レベル。13 = 33・14 = 34）。デスクトップの模擬は 0（環境変数 SEED_PLATFORM_SIM_OS_VERSION で差し替え）

// 例: 通知の実行時の許可の段は Android 13（API 33）以上だけ出す（PC で試すなら SEED_PLATFORM_SIM_OS_VERSION=33）
bool showNotificationStep = App.OsVersion >= 33;

// 前面・背面の知らせ（SEED.Events。2 つとも AppLifecycleEvent.TryParse で読める）
App.ResumedEvent   // "platform.resumed" … 前面へ戻った（Android の onResume。プロセスの起動の最初の onResume では届かない＝必ず paused の後）
App.PausedEvent    // "platform.paused"  … 前面を離れた（Android の onPause）
this.On(App.ResumedEvent, (string json) =>
{
    if (AppLifecycleEvent.TryParse(json, out AppLifecycleEvent e))
    {
        AppLifecyclePhase phase = e.Phase;  // Resumed / Paused（イベントの名前から）
        long count = e.Count;               // 何回目か（前面・背面それぞれ 1 から。Android はプロセスの中、模擬は Play の回の中で数える）
        long away = e.BackgroundMs;         // 背面にいた時間（ミリ秒。resumed だけ。paused は 0）
        bool simulated = e.Simulated;       // デスクトップの模擬が作ったか
    }
    RefreshPermissions();                   // 例: 設定の画面から戻ったら、変化のイベントが無くても Permissions.Check で問い直す
});

// デスクトップの模擬だけ: 前面・背面の出入りを起こす（窓のフォーカスの出入りと同じ扱い。Android は false・"unknown_method"）
PlatformDiagnostics.SimulateLifecycle(AppLifecyclePhase.Paused);
PlatformDiagnostics.SimulateLifecycle(AppLifecyclePhase.Resumed);
```

| 項目 | Android | デスクトップ（模擬） |
|---|---|---|
| `App.Platform` | `Android` | ホストの OS（Windows なら `Windows`） |
| `App.OsVersion` | `Build.VERSION.SDK_INT`（API レベル） | 0。環境変数 `SEED_PLATFORM_SIM_OS_VERSION` に 0 以上の整数を書くとその値（読めない値はログに出して 0） |
| `platform.resumed` | `MainActivity.onResume` の最後（権限の結果・変化〈`permission_result` / `permission_changed`〉の後）。**プロセスの起動の最初の onResume では届かない** | 窓がフォーカスを得たとき（Play の間だけ）・`SimulateLifecycle(Resumed)`・IPC `PLATFORM_SIM:lifecycle,resumed`。Play の始まりは「前面」で、始まりの前面は知らせない |
| `platform.paused` | `MainActivity.onPause` の最初 | 窓がフォーカスを失ったとき（**エディタに埋め込んだ Play では、エディタの別のパネルを押しただけでも届く**）・`SimulateLifecycle(Paused)`・IPC `PLATFORM_SIM:lifecycle,paused` |
| 同じ状態への移り | 起きない（onResume と onPause は交互） | 知らせない（前面で前面へ・背面で背面へは何もしない） |
| スクリプトの準備の前 | 起動の直後に届いた分はエンジンが保持し、最初のシーンの `OnStart` の後に配る（ほかのプラットフォームのイベントと同じ） | 同じ |

> **重要**: `platform.paused` は背面にいる間に届くとは限りません。Android では背面にいる間は描画の面が無くフレームが回らないことが多く、戻ったときに `platform.resumed` の直前にまとめて届くことがあります（推論。実機で確かめる）。セーブの書き出しなど「背面へ回る前に済ませたい処理」を `paused` に頼らないでください（セーブはエンジンが背面へ回るときに書き出します）。

> **重要**: 起動のときの状態は `platform.resumed` では分かりません（最初の onResume では届かない）。起動時は `OnStart` で `App.OsVersion`・`Permissions.Check` などを読み、`resumed` は「戻ったときの問い直し」にだけ使ってください。

- `App.Platform` は名前空間 `SEED.Platform` のクラス `Platform`（`Platform.IsSupported` など）とは別物です（`App` の中の OS の種類）。
- 仕組み（Java の `platform/app/AppLifecycle`・`local/OsInfoCommand`・模擬の窓のフォーカス）は docs/android.md §25.20。

### デスクトップの模擬の操作（権限の状態と答え・前面と背面・OS の版。2026-10-01）

PC の Play（エディタ・単体起動）で、権限の流れ（「拒否 → 求める → 許可」「設定の画面から戻る」）と前面・背面を**端末なしで決まった状態から**試すための口です。
**起動時の状態は環境変数**、**実行中の変更は IPC（`PLATFORM_SIM`）かスクリプト（`PlatformDiagnostics`）**で与えます。どれも模擬だけで、Android の実機には効きません。

```csharp
using SEED.Platform;

// 実行中に変える（模擬だけ。Android は false・Platform.LastError == "unknown_method"）
PlatformDiagnostics.SimulatePermission(PermissionKind.PostNotifications, PermissionStatus.Denied);   // 状態を変える。変わればすぐ platform.permission_changed（Simulated = true）
PlatformDiagnostics.SimulatePermissionAnswer(PermissionKind.ExactAlarm, PermissionStatus.Granted);   // Permissions.Request のときの模擬の利用者の答え
PlatformDiagnostics.SimulatePermissionAnswer(null, null);   // kind = null ですべての種類・answer = null で「答えない」（閉じた・何も変えずに戻った）
PlatformDiagnostics.SimulateLifecycle(AppLifecyclePhase.Paused);   // 前面・背面（上の節）
// v2 の予約の種類（RecordAudio・SendSms）は常に NotApplicable で変えられない（false・"invalid_argument"）
```

| 何を | 起動時（環境変数） | 実行中（IPC。応答 `PLATFORM_SIM_OK:{返答}` / `PLATFORM_SIM_ERROR:{理由}`） | 実行中（スクリプト） |
|---|---|---|---|
| 権限の状態 | `SEED_PLATFORM_SIM_PERMISSIONS=post_notifications=denied;exact_alarm=needs_settings` | `PLATFORM_SIM:permission,<kind>,<status>` | `PlatformDiagnostics.SimulatePermission(kind, status)` |
| 求めたときの答え | `SEED_PLATFORM_SIM_PERMISSION_ANSWER=none;exact_alarm=granted`（種類を書かない項目・`all=` はすべて。既定は `granted`） | `PLATFORM_SIM:permission_answer,<kind\|all>,<status\|none>` | `PlatformDiagnostics.SimulatePermissionAnswer(kind?, status?)` |
| 前面・背面 | — | `PLATFORM_SIM:lifecycle,<resumed\|paused>` | `PlatformDiagnostics.SimulateLifecycle(phase)` |
| OS の版 | `SEED_PLATFORM_SIM_OS_VERSION=33` | — | — |

- 種類・状態は wire の名前（`post_notifications` / `exact_alarm` / `full_screen_intent`、`granted` / `denied` / `denied_permanently` / `needs_settings` / `not_applicable`、答えはさらに `none`）。
  環境変数の読めない項目はログ（`[SEED PLATFORM] 模擬: … の項目を飛ばしました`）に出して飛ばし、読めた項目だけを使います。
- `Request` の模擬の規則: 今の状態が `Granted`・`NotApplicable`・`DeniedPermanently` なら答えを当てずにその状態が結果（Android でも確認の画面が出ない）。
  `Denied`・`NeedsSettings` なら答えの状態が結果（`none` なら変わらない）。状態が変われば `permission_result` の後に `permission_changed`。
- 状態を変えると**すぐ** `permission_changed` が届きます（Android は前面へ戻ったときに気づく。模擬は背面の模擬の間に変えてもすぐ知らせる）。同じ状態への変更では届きません。
- `OpenSettings` は開かずにログだけです（変えるなら上の口で）。`App.OpenUrl` を開かないのは従来どおり `SEED_PLATFORM_SIM_NO_OPEN=1`。
- エディタの **Play を止める・始めると、実行中の変更は捨てられ起動時の設定へ戻ります**（ほかの模擬の状態と同じ）。IPC の `PLATFORM_SIM` は Play 中だけ受け付けます（`not_playing`）。
- IPC の理由: `not_playing`（Play 中でない）・`not_simulated`（模擬でない）・`unknown_verb`・`bad_arguments`（引数の数の違い）・`invalid_argument`（種類・状態の名前の誤り、v2 の予約の種類）。

### 端末の明暗（`App.UiMode`・`platform.ui_mode_changed`。W2-9）

端末の明暗の設定（ダークモード）です。SEED.UI のテーマの「端末に従う」（`UiTheme.SetBrightnessMode(UiBrightnessMode.System)`）がこれを使うので、
ふつうは直接呼びません。Android はメインプロセスが答える（IPC なし）。正典は docs/ui_theme.md §4。

```csharp
using SEED.Platform;

SystemUiMode mode = App.UiMode;                   // SystemUiMode.Dark / Light / Unknown（呼ぶたびに問い合わせる）
this.On(App.UiModeChangedEvent, (string json) =>  // "platform.ui_mode_changed"（設定が変わったとき）
{
    if (App.TryParseUiModeEvent(json, out SystemUiMode now)) Debug.Log($"明暗: {now}");
});
PlatformDiagnostics.SimulateUiMode(SystemUiMode.Light);   // デスクトップの模擬だけ: 差し替えて、変わればイベント（null で OS の設定へ戻す。Android は false）
```

| 項目 | Android | デスクトップ（模擬） |
|---|---|---|
| `UiMode` | `Configuration.uiMode` の夜の bit（YES → Dark・NO → Light・UNDEFINED → Unknown） | Windows の「既定のアプリ モード」（レジストリ `AppsUseLightTheme`。0 = Dark）。ほかの OS は Unknown。差し替えがあればその値 |
| `platform.ui_mode_changed`（data `night` = `yes` / `no` / `unknown`） | `MainActivity.onConfigurationChanged` で夜の bit が変わったとき（`configChanges` に `uiMode` があるので Activity は作り直されない） | 単体起動のウィンドウの ThemeChanged（OS の設定の変化。エディタに埋め込んだ Play では届かない）と `SimulateUiMode` |

### 予測型の戻る（`App.SetBackCallbackEnabled`・`platform.back_*`。W2 の手直し P1-3）

Android の予測型の戻る（戻るの手ぶりの途中で画面が縮み、前の画面が覗く。根ではホームへ戻る見た目）の土台です。**opt-in** で、プロジェクト設定
`android.predictive_back: true` の APK の Android 13 以上だけで働きます（docs/project_system.md・docs/android.md §25.18）。無い・false の APK（既定）では何も変わらず、
戻るは従来どおり `KeyCode.Escape` だけで届きます。ふつうは SEED.UI の戻るの段（BackDispatcher）が呼ぶので、スクリプトから直接は呼びません
（SEED.UI の部品を置いたシーンでは、受ける層の有無を自動で知らせ、手ぶりの間に閉じるものを縮めて見せる。W2 の手直し 3b。§7.18）。

```csharp
using SEED.Platform;

// 受ける層（ダイアログ・画面のスタックなど）があるかを知らせる（起動したときは「受ける」。状態が変わったときだけ呼ぶ）
bool enabled = App.SetBackCallbackEnabled(true);   // true = 受ける層がある（アプリが戻るを受け、手ぶりのイベントと Escape が届く）
App.SetBackCallbackEnabled(false);                 // false = 受ける層が無い（根。システムに任せて背面へ回る見た目。Android 13〜15 でランチャー以外から起動した根は Escape が届く）
                                                   // 返り値 = 基盤が受け付けて予測型の戻るが有効か（無効・失敗なら false。何もしていない）
App.IsPredictiveBackEnabled                        // bool: 最後の SetBackCallbackEnabled の返答の enabled（一度も呼んでいなければ false。模擬は常に false）

// 手ぶりのイベント（4 つとも BackGestureEvent.TryParse で読める）
App.BackStartedEvent      // "platform.back_started"    … 始まり（Android 14 以上。data: gesture, progress, edge, touch_x, touch_y）
App.BackProgressedEvent   // "platform.back_progressed" … 進み具合（Android 14 以上。毎フレーム。data は始まりと同じ形）
App.BackCancelledEvent    // "platform.back_cancelled"  … 取り消し（Android 14 以上。指を戻した。data: gesture）
App.BackInvokedEvent      // "platform.back_invoked"    … 確定（Android 13 以上。data: gesture）。続けて KeyCode.Escape が届く（同じフレームとは限らない）
this.On(App.BackProgressedEvent, (string json) =>
{
    if (BackGestureEvent.TryParse(json, out BackGestureEvent e))
    {
        BackGesturePhase phase = e.Phase;   // Started / Progressed / Cancelled / Invoked（イベントの名前から）
        long gesture = e.Gesture;           // 手ぶりの通し番号（1 から。同じ手ぶりの始まり〜取り消し / 確定は同じ番号。始まりの無い確定は新しい番号）
        float progress = e.Progress;        // 0〜1（始まり・進み具合だけ）
        BackEdge edge = e.Edge;             // BackEdge.Left / Right / None（端からの手ぶりでない＝ボタンの戻る）
        float x = e.TouchX, y = e.TouchY;   // 指の位置（窓の座標の px。取れないとき〈ボタンの戻る・模擬〉は 0）
        bool simulated = e.Simulated;       // デスクトップの模擬が作ったか
    }
});

// デスクトップの模擬だけ: 手ぶりのイベントを流して PC でプレビューを試す（Invoked は知らせだけ。確定の Escape は Esc キーで押す。Android は false）
PlatformDiagnostics.SimulateBackGesture(BackGesturePhase.Started, 0f, BackEdge.Left);
PlatformDiagnostics.SimulateBackGesture(BackGesturePhase.Progressed, 0.5f, BackEdge.Left);   // 進み具合は 0〜1 にそろえる
PlatformDiagnostics.SimulateBackGesture(BackGesturePhase.Invoked);                            // 既定は progress 0・BackEdge.Left
```

| 項目 | Android（`android.predictive_back: true`・13 以上） | デスクトップ（模擬） |
|---|---|---|
| `SetBackCallbackEnabled(true)` | 自分のコールバックを登録（起動したときの状態） | 記録してログだけ（変わったときだけ）。返り値は false |
| `SetBackCallbackEnabled(false)` | 36 以上: 外してシステムの背面行き（`moveTaskToBackCallback`）を登録。33〜35: ランチャーから起動した根だけ外す（それ以外は残して Escape → `App.MoveTaskToBack`） | 同上 |
| イベント | 13: 確定だけ。14 以上: 始まり・進み具合・取り消し・確定 | `SimulateBackGesture` で流したものだけ（`Simulated` = true） |
| 確定の Escape | 確定で合成の戻るキー → `KeyCode.Escape`（従来の戻るキーと同じ入口） | Esc キー（模擬は注入しない） |
| `android.predictive_back` が無い・false | 何も登録しない。イベントは届かず、戻るは従来どおり Escape だけ。`SetBackCallbackEnabled` は false | — |

> **重要**: 確定の知らせ（`platform.back_invoked`）と `KeyCode.Escape` は別の道で届き、同じフレームとは限りません。戻るの処理（閉じる・下ろす）は従来どおり Escape で 1 回だけ行い、手ぶりのイベントは見た目（縮める・プレビュー）にだけ使ってください（`Gesture` の番号で同じ手ぶりかを見分ける）。

### センサー（`Sensors`。W1-8）

端末を振る・揺らすの判定に使う**重力を除いた加速度**です。Android ではメインプロセスが `SensorManager` の `TYPE_LINEAR_ACCELERATION`
（無い端末は加速度から低域通過で重力を引いた値）を専用のスレッドで受け、たまった標本を `Read` で返します（IPC なし＝最初の呼び出しでも
`connecting` にならない。権限・機能〈`android.features`〉の opt-in は要らない）。**標本ごとのイベントは流れません**（毎フレーム `Read` する）。
アプリが前面から外れると登録を外し（背面で電池を使わない）、戻ると同じ頻度で登録し直します。デスクトップは模擬で、PC にセンサーは無いので値は 0 です
（`SimulateSample` で入れた標本だけ）。

```csharp
using SEED.Platform;

bool ok = Sensors.Start(SensorKind.LinearAcceleration);       // 既定 50 Hz（Sensors.DefaultRateHz。1〜200。大きい値は 200 にそろえる。動いていれば始め直す）
Sensors.IsSupported(SensorKind.LinearAcceleration)            // bool: 使えるか（IPC なし。Start が not_supported で失敗した後は false。最初の Start の前は true）
Sensors.GetSource(SensorKind.LinearAcceleration)              // 最後に成功した Start の出どころ: "linear_acceleration" / "accelerometer_lowpass" / "simulated"
if (Sensors.Read(SensorKind.LinearAcceleration, out SensorSample s))   // 毎フレーム呼んでよい（Android でも IPC なし）
{
    SEED.Vector3 a = s.Acceleration; // 最新の標本（m/s²。重力を除いた加速度・端末の座標系。静置でほぼ 0）
    long t = s.TimestampMs;          // 最新の標本の時刻（UTC の epoch ミリ秒。まだ無ければ 0）
    float peak = s.PeakMagnitude;    // 前回の Read からの標本の大きさ √(x²+y²+z²) の最大（m/s²）… 読むと 0 から数え直す
    int n = s.SampleCount;           // 前回の Read からの標本の数（前面から外れている間は 0）
}
Sensors.Stop(SensorKind.LinearAcceleration);                  // 止める（動いていなくても true。たまった標本は捨てる。以後の Read は not_started）
Sensors.SimulateSample(SensorKind.LinearAcceleration, new SEED.Vector3(0f, 0f, 15f)); // 模擬だけ: 標本を 1 つ入れる（PC で振りを試す。Android は false・"unknown_method"）

// 失敗の理由（Platform.LastError）
Sensors.ErrorNotSupported    // "not_supported"    … 端末にセンサーが無い（Start。以後 IsSupported は false。次に成功すれば true に戻る）
Sensors.ErrorNotStarted      // "not_started"      … Start していない種類を Read した（Stop の後も）
Sensors.ErrorRegisterFailed  // "register_failed"  … OS が登録を受け付けなかった（Android。Start をやり直せる）
Sensors.ErrorInvalidArgument // "invalid_argument" … 種類が約束に無い・頻度が 1 未満

// 例 1: PeakMagnitude が閾値を超えたら 1 回と数え、N 回で成功（閾値・回数はアプリのデータ。判定はアプリの純粋ロジック）
private const float ShakeThreshold = 12f;   // m/s²（Wake or Pay の Flutter 版 ShakeDetector と同じ閾値）
private const int RequiredShakes = 10;
private int _shakes;
private bool _wasAbove;
public override void OnStart() => Sensors.Start(SensorKind.LinearAcceleration);
public override void Update(ref NativeFrameContext ctx)
{
    if (!Sensors.Read(SensorKind.LinearAcceleration, out SensorSample s)) return;
    bool above = s.PeakMagnitude > ShakeThreshold;
    if (above && !_wasAbove && ++_shakes >= RequiredShakes) OnShaken();  // 超えた瞬間だけ数える（1 回の振りが数フレームにまたがっても 1 回）
    _wasAbove = above;
}
public override void OnDestroy() => Sensors.Stop(SensorKind.LinearAcceleration);

// 例 2: Flutter 版と同じ「5 秒振り続ける（0.5 秒を超えて止まると 0 に戻す）」— 閾値以上のフレームの間の時間を積む
private long _lastAboveMs, _heldMs;
private void Accumulate(SensorSample s, long nowMs)   // nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
{
    if (s.PeakMagnitude >= ShakeThreshold)
    {
        if (_lastAboveMs > 0) _heldMs = nowMs - _lastAboveMs > 500 ? 0 : _heldMs + (nowMs - _lastAboveMs);
        _lastAboveMs = nowMs;
        if (_heldMs >= 5000) OnShaken();
    }
    else if (_lastAboveMs > 0 && nowMs - _lastAboveMs > 500) { _heldMs = 0; _lastAboveMs = 0; }
}
```

| 項目 | Android | デスクトップ（模擬） |
|---|---|---|
| `Start` | `TYPE_LINEAR_ACCELERATION` を登録（無ければ `TYPE_ACCELEROMETER`＋低域通過。どちらも無ければ `not_supported`）。前面にいなければ前面へ戻ったときに登録 | 受け付けて状態を持つ（出どころ `simulated`・`IsSupported` は true） |
| 標本 | 専用のスレッドで受ける。頻度は OS への希望で、実際の数は `SampleCount` で分かる（希望より速いことがある） | `SimulateSample` で入れたものだけ（入れなければ値も数も 0） |
| 前面から外れる | 登録を外す（`Start` の状態とたまった標本は残り、戻ると登録し直す。外れている間の標本は無い） | 関係なし |
| `TimestampMs` | `SensorEvent.timestamp`（端末の起動からの時計）を読んだときの壁時計で UTC の epoch ミリ秒に換算 | 標本を入れた時刻 |
| Play の区切り | — | 止まる（次の回の `Read` は `not_started`） |

> **重要**: `PeakMagnitude` と `SampleCount` は**前回の `Read` からの分**で、読むと 0 から数え直します（フレームの間に来た振りの頂点を落とさないため）。1 フレームに 2 回読むと 2 回目は 0 です。読む係を 1 つのスクリプトにし、毎フレーム 1 回だけ読んでください。最新の値だけ（`Acceleration`）で判定すると、フレームの境目の間の振りを見落とします。

> **重要**: 出どころが `accelerometer_lowpass` の端末（ジャイロの無い端末など）では、重力を時定数 0.25 秒の低域通過で見積もって引くため、端末の向きを素早く変えた直後は重力の差が一瞬だけ加速度に見えます（式を JVM で動かした計算: 50 Hz で 90 度を一瞬で回すと約 12.8 m/s² が 1 標本〈20 ms〉。実機では未確認）。閾値を超えた回数・時間で判定し、1 回の山だけで成功にしないでください。

- 使い終わったら（画面を離れるとき）`Stop` してください。Android では前面にいる間は登録したままです（背面では自動で外れる）。
- 仕組み（命令・出どころの選び方・スレッド・前面の出入り・時刻の換算・adb での確かめ方）は docs/android.md §25.16。

---

## 7.14 Redraw（描く理由の申告・描き方の方針。W2-10a）

止まっている画面の多いアプリで電池を使わないための静的クラスです（名前空間 `SEED`）。プロジェクト設定 `project_settings.json` の
`"render_policy": "on_demand"`（既定は `"continuous"`＝毎フレーム描く）のとき、エンジンは入力・ジェスチャー・アニメーション・パーティクル・
`SEED.Platform` のイベント・IPC などを自分で「描く理由」にし、**理由の無いフレームが続いたら（既定 10 回）描画を止めて眠ります**。
止めている間は **`Update` などが呼ばれません**。エンジンが知らない動き（スクリプトで動かす演出・`Draw` の図形のアニメーション・時計の表示）は、ここで申告します。
方針が `continuous` なら毎フレーム描くので、呼んでも何も変わりません。

```csharp
SEED.Redraw.Request();                    // 次の 1 フレームを描く（止めていれば起こす）。どのスレッドから呼んでもよい（async の続きなど）
bool ok = SEED.Redraw.RequestAfter(1.0f); // 1 秒後（実時間）に 1 フレームを描く。止めている間はその時刻に起きる。いちばん早い予定が効く。NaN・負は false
bool ok2 = SEED.Redraw.KeepAlive(0.3f);   // 0.3 秒の間は描き続ける（延ばすだけで縮めない）。NaN・負は false
SEED.Redraw.SetContinuous(true);          // true の間は常に描く（ゲームの画面・センサーを読む画面・鳴動の画面）。false で外す
bool c = SEED.Redraw.IsContinuous;        // SetContinuous(true) の中か
SEED.Redraw.Policy                        // RedrawPolicy（get/set）: 今の方針。書くと実行中だけプロジェクト設定を上書き
SEED.Redraw.ResetPolicy();                // 上書きを外してプロジェクト設定へ戻す
// SEED.RedrawPolicy.Continuous（毎フレーム描く・既定）/ OnDemand（描く理由があるときだけ描く）

// 例: 時計の表示（実時間で描き、次の秒の変わり目に起きる）
public override void Update(ref NativeFrameContext ctx)
{
    var now = System.DateTime.Now;
    clockText.Content = now.ToString("HH:mm:ss");
    SEED.Redraw.RequestAfter(1f - now.Millisecond / 1000f);
}

// 例: 押下の演出（0.3 秒）の間だけ描き続ける
public override void OnPointerDown() { SEED.Redraw.KeepAlive(0.3f); }

// 例: 通信の完了（別スレッド）から画面を更新させる
async void Fetch() { data = await client.GetStringAsync(url); SEED.Redraw.Request(); }
```

| 描く理由（エンジンが自分で積む） | 例 |
|---|---|
| 入力 | タップ・クリック・カーソル・ホイール・キー（押している指・マウスのボタンの間は描き続ける。キーボードのキーの押しっぱなしは OS の繰り返しで起きる） |
| ジェスチャー | 指が触れている間。長押しの期限で起きる |
| 動いているもの | Animator の再生中・パーティクル・読み込み中のモデル・動いている物理のボディ・スクロールの慣性・跳ね返り・ScrollTo（W2-3） |
| 知らせ | `SEED.Platform` のイベント（目覚まし・通知・権限）・IPC の命令・画面の大きさ・向き・安全領域・Android の文字入力と音声フォーカス |

> **重要**: 止めていた時間は**ゲームの時間に入りません**。起きた最初のフレームの `Time.DeltaTime` は 1/60 秒で切り詰められ、`ElapsedTime`・`Unscaled*` も止めていた分だけ実時間より遅れます。時刻で何かをするスクリプトは実時間（`System.DateTime` など）で判定し、次に起きる時刻を `RequestAfter` で申告してください。`DeltaTime` を足し上げた時計は止まって見えます。

> **重要**: 申告されない動き（水面の波・草の風・シェーダーの時間で動く見た目・`Draw` の図形のアニメーション）は、止めている間は止まって見えます。そういう画面では `SetContinuous(true)` にしてください。Play の開始・停止で要求（`KeepAlive`・`SetContinuous`・方針の上書き・予定）はすべて外れます。

- Edit・エディタの PAUSE（デバッグカメラ）は常に毎フレーム描きます。止める判定を使うのは Play だけです。
- 設定・仕組み・描く理由の全一覧・検証の数値は docs/redraw_policy.md。

---

## 7.15 UI 部品（SEED.UI：ListView・SwipeActions。W2-3）

部品の振る舞いは C# の `SEED.UI` 名前空間（SEEDScripting に同梱。エンジンを更新すれば全プロジェクトに届く）、見た目はプレハブ（行の形）とテーマです。
スクロール自体（位置・慣性・窓の外を飛ばす）はエンジン（`CanvasScroll`）が動かします。規則の正典は `docs/ui_scroll_list.md`。

### ListView（行の多い一覧：見えている行だけを作って使い回す）

```csharp
using SEED.UI;

public class AlarmList : SEEDScript
{
    private ListView list;

    public override void OnStart()
    {
        // 窓（このノード: Canvas + CanvasClip + CanvasScroll 縦）に、行のプレハブで 1,000 件の一覧を作る
        list = new ListView(gameObject, "assets://ui/alarm_row.actor", count: 1000, rowExtent: 72f, (row, i) =>
        {
            if (row.FindChild("Label").GetComponent<SEED.Text>() is { } t) t.Content = $"Alarm {i}";
        })
        {
            Spacing = 8f,                                     // 行と行の間隔
            Recycled = (row, oldIndex) => { /* 押下の見た目・スワイプを戻す */ },
        };
    }

    public override void Update(ref NativeFrameContext ctx) => list.Update();   // 毎フレーム呼ぶ（変化が無ければ何もしない）
}

list.SetCount(n);                   // 行の数を変える（次の Update で入れ直す）
list.SetExtentOf(i => i % 2 == 0 ? 56f : 96f);  // 行ごとの長さ（null で固定へ戻す。付いたままの行も次の Update で置き直す＝毎フレーム渡し直すと畳む動きになる）
list.Refresh();                     // データが変わった: 付いている行の中身を入れ直す
list.ScrollToIndex(500, 0.3f, 0f);  // 行 500 を窓の先頭へ（揃え 0 = 先頭・0.5 = 中央・1 = 終わり。時間 0 はすぐ移す）
list.RowOf(500)                     // GameObject?: 行 500 に付いている行（見えていなければ null）
list.IndexOf(row)                   // int: 行が付いている番号（−1 = 付いていない）
list.VisibleRange                   // ListRange: 見えている行の範囲（First・Last・Count・IsEmpty）
list.CreatedRowCount                // int: 作った行の数（使い回しの入れ物。見えている行 + 前後の余白の分で止まる）
```

| 規則 | 内容 |
|---|---|
| 作る行 | 窓の長さ + 前後の `CacheExtent`（既定はスクロールの 250）と交わる行だけ。足りなければプレハブから作り（**次のフレームから使える**。作る間は隠す）、以後は使い回す |
| 使い回し | 窓の外へ出た行を、新しく見えた行へ付け替える（位置を合わせて `bind` を呼ぶ）。付いたままの行は作り直さない。付け替える前に `CancelGestures` で押下・ドラッグを取り消し、`Recycled` を呼ぶ |
| 中身の長さ | 行の数と長さ（と間隔・余白）から決めて、スクロールを `Fixed` の中身の大きさにする |
| 行のプレハブ | anchor (0,0)・スクロールの軸の pivot 0 を推奨。横いっぱいにするなら `CanvasLayoutItem` の `fill_width`。行は窓（か `content`）の子として作る |

### SwipeActions（行を横へずらすと削除・編集のボタンが出る）

```csharp
// 行のプレハブ: Row（CanvasGesture: tap=false・drag・fling・drag_axis=horizontal）
//              ├─ Actions（右端の削除ボタン）  └─ Front（ずらす見た目。受けるジェスチャーの無い CanvasGesture＝遮る板）
public class AlarmRow : SEEDScript
{
    private SwipeActions swipe;
    public override void OnStart()
    {
        swipe = new SwipeActions(gameObject.FindChild("Front"), actionsExtent: 96f)   // 左へ 96 ずらすと右の操作が見える
        {
            Group = SwipeGroup.For(gameObject.Parent),   // 同じ一覧の行は開いているのを 1 つにする
            Opened = s => { }, Closed = s => { },
        };
    }
    public override void OnGestureDragStart(SEED.GestureEvent e)  => swipe.OnDragStart(e);
    public override void OnGestureDragUpdate(SEED.GestureEvent e) => swipe.OnDragUpdate(e);
    public override void OnGestureDragEnd(SEED.GestureEvent e)    => swipe.OnDragEnd(e);
    public override void Update(ref NativeFrameContext ctx)       => swipe.Update(SEED.Time.UnscaledDeltaTime);
}

swipe.Open(); swipe.Close(); swipe.Reset();   // 開く・閉じる（動きつき）・すぐ閉じる（行の使い回し）
swipe.IsOpen / swipe.Offset / swipe.IsDragging / swipe.IsAnimating
SwipeGroup.For(listNode).CloseAll();          // 一覧のスクロールが始まったら（OnScrollStart から）
```

| 規則 | 内容（出典: Android の ItemTouchHelper。2026-09-28 に androidx のソースで確認） |
|---|---|
| 開く・閉じる | 離したときの横の速さが 120 dp/秒以上なら向きで決める。それより遅ければ、開いた量の半分以上ずらしていれば開く |
| 動き | 250ms・Material の fastOutSlowIn。動いている間は `Redraw.Request()`（on_demand でも止まらない） |
| 組 | ある行のドラッグが始まると、同じ組の他の開いている行が閉じる |
| 軸 | 縦の一覧の中では、最初の指の動きが横ならスワイプ、縦なら一覧のスクロール（W2-2 のアリーナの軸の競い） |
| ずらし方 | Front に `CanvasLayoutItem` があれば `CanvasLayoutItem.Translate`（実行中だけ・保存しない。fill_width の行も動く）、無ければ `CanvasTransform.Position` |
| レイヤー | Front の部分木の表示のレイヤーを Actions の部分木より上にする（同じレイヤーでは文字がスプライトより手前に描かれ、Actions の文字が閉じた行の上に出る） |

### SwipeActions のフルスワイプ（大きく払うとそのまま削除。W2 の手直し P2-3）

```csharp
// 行のプレハブ（templates/ui/prefabs/list_row.actor）: Row（CanvasComponent・CanvasLayoutItem fill_width・CanvasGesture 横のドラッグ）
//   ├─ Actions（CanvasComponent・fill_width/fill_height・削除の面〈color.error〉・CanvasGesture タップ・SEED.UI.GestureRelay）
//   │   └─ Label（「削除」。右の端のボタンの枠の真ん中）
//   └─ Front（fill_width・行の見た目・遮る板。レイヤーは Actions より上。CanvasComponent と縦の CanvasStack〈左右の余白 16〉＝W2 の手直し P2-5）
//       └─ Title・Sub・Divider（行の幅 − 余白に伸びる。文字の枠〈BoxWidth〉はレイアウトが伸ばさないので行のスクリプトが LayoutSize.x に合わせる）
swipe = new SwipeActions(gameObject.FindChild("Front"), actionsExtent: 96f)
{
    FullSwipe = true,                                            // 既定 false（W2-3 のまま）
    FullSwipeLabel = gameObject.FindChild("Actions/Label"),      // 構えたら Front の後ろの端に付いて動く文字（任意）
    ArmHaptic = SwipeHaptic.Vibrate,                             // 構えたときの触感（None / Tap〈既定〉/ Vibrate）
    Group = SwipeGroup.For(gameObject.Parent),
    Armed = s => { }, Disarmed = s => { },                       // 閾値を越えた・戻した
    FullSwiped = s => SEED.Events.Raise("RowDeleted", gameObject), // 流し切った → 持ち主が行を畳んでデータから消し、書き直しで Reset
};
// 開いた行の削除の面のタップ（GestureRelay.Tapped）から: 同じ確定の流れ（流し切り → FullSwiped）
if (swipe.IsOpen) swipe.Commit();

swipe.RowExtent = 0f;               // 行の幅（0 以下 = 自動: Front の LayoutSize.x → Sprite の幅）。swipe.ResolvedRowExtent で今の値
swipe.DisarmHaptic = SwipeHaptic.None;   // 解いたときの触感（既定 None）
swipe.VibrateMilliseconds = 20;     // Vibrate の長さ（既定 20 ms）
swipe.IsArmed / swipe.IsCommitted / swipe.LabelWeight / swipe.ArmCount
swipe.CommitStarted = s => { };     // 確定して流し始めた（構えたまま離した・Commit）
```

| 規則 | 内容（値は docs/backlog.md の案。iOS の値は公開されていないので決めた値。テーマのトークンで変えられる） |
|---|---|
| ドラッグの範囲 | 閉じた 0 〜 操作の側へ行の幅いっぱい |
| 構える・解く | 行の幅 × `ratio.swipe_full`（0.6）以上で構え、構えた後 × `ratio.swipe_full_cancel`（0.55）を下回ったら解く（ヒステリシス）。ボタンの幅より手前では構えない。行の幅が分からない間は構えない |
| 触感 | 構えた瞬間に 1 回（`ArmHaptic`。既定 `Tap`）。解いたときは出さない（`DisarmHaptic` で変えられる） |
| 文字 | 構えていない間は元の位置、構えている間は Front の後ろの端 ＋ `space.l`。切り替わりを `motion.swipe_full`（0.15 秒）で補間。文字の親は行いっぱいに置く |
| 離したとき | 取り消し → 構えを解いて元へ／構えている → 確定（閉じる向きへ速く払ったときは除く）／構えていない → 開く・閉じるの規則 |
| 確定 | Front を行の幅の外まで `motion.swipe_dismiss`（0.2 秒）で流し切り → `FullSwiped`。`Reset` まで指も `Open`・`Close`・`Commit` も受けない（二重に確定しない） |
| 畳む | 部品は畳まない。持ち主が `ListView.SetExtentOf` に `SwipeMath.CollapsedExtent(56f, 経過, motion.swipe_collapse)` を毎フレーム渡して行の高さを 0 へ畳み、畳み終わったらデータから消して `SetCount`・`Refresh`（見本 `templates/ui/scripts/UiGallerySections.cs`） |

| トークン | 既定 | 意味 |
|---|---|---|
| `ratio.swipe_full` | 0.6 | 構えるずらし量（行の幅に対する割合） |
| `ratio.swipe_full_cancel` | 0.55 | 構えを解くずらし量（同上） |
| `motion.swipe_full` | 0.15 | 「削除」の文字の置き場の補間（秒） |
| `motion.swipe_dismiss` | 0.2 | 確定で行を外へ流し切る（秒） |
| `motion.swipe_collapse` | 0.2 | 消した行を畳む（秒。一覧の持ち主が読む） |
| `color.on_error` | #FFFFFF | 削除の面（`color.error`）の上の文字 |

状態の移り変わりは純粋な `SEED.UI.SwipeModel`、規則の計算は `SEED.UI.SwipeMath`（`UpdateArmed`・`DecideRelease`・`LabelShift`・`CollapsedExtent` など）。正典は `docs/ui_scroll_list.md` §7.1。

## 7.16 UI 部品（SEED.UI：ボタン・トグル・スライダ・数値欄・選択・進捗とテーマ。W2-4）

部品は**アクタ（プレハブ）に付けるスクリプト**です。ScriptComponent の型名に `SEED.UI.Button` のように書いて付けます
（見本のプレハブは `templates/ui/prefabs/`、全部を並べたギャラリーは `templates/ui/scenes/ui_gallery.scene`）。
見た目はプレハブの子（Sprite・Text の色・形・位置）で、部品は「状態（値・無効・押下）→ 見た目」を 1 か所で決めて当てます。
色・角丸・大きさ・文字の大きさ・書体・動きの時間は**テーマのトークン**（`UiTokens`）から取ります。規則の正典は `docs/ui_components.md`（テーマは `docs/ui_theme.md`。W2-9）。

```csharp
using SEED.UI;

public class AlarmEditScreen : SEEDScript
{
    public override void Update(ref NativeFrameContext ctx)
    {
        // 部品は別のスクリプトなので、登録簿から引く（相手の OnStart の前は null。見つかるまで引き直してよい）
        if (UiWidget.Of<Button>(gameObject.FindChild("Save")) is { } save && !bound)
        {
            bound = true;
            save.Clicked += b => { b.Busy = true; /* 保存 → 終わったら b.Busy = false */ };
        }
    }
    private bool bound;
}

UiWidget.Of<T>(gameObject)          // T?: そのアクタに付いた部品（Button・Toggle・Slider・NumberField・Checkbox・…）
widget.Interactable                 // bool（get/set。false = 無効の見た目・操作を受けない）
widget.SetInteractable(false)       // 無効にする（見た目もすぐ変える）
widget.IsEnabled                    // bool: 操作を受けるか（Interactable と部品ごとの条件）
widget.Owner                        // GameObject: 部品のアクタ
UiRegistry.Version                  // int: 部品の登録・解除で増える（引き直しの合図）
```

### Button（ボタン）

```csharp
// プレハブ: Button（Sprite〈背景・角丸〉・CanvasGesture〈タップ・長押し〉・SEED.UI.Button）└ Label（Text）└ Icon（Sprite。任意）
button.Variant       // ButtonVariant（Filled=0〈既定〉/ Tonal=1 / Outlined=2 / Text=3）
button.Clicked       // event Action<Button>（タップ。押下がスクロールに負けたら来ない）
button.LongPressed   // event Action<Button>（長押し 500ms）
button.Released      // event Action<Button>（押下の終わり＝離した・取り消された）
button.IsPressed     // bool（押下の見た目の間）
button.Busy          // bool（get/set。処理中は押せない＝二重押しの防止）
button.SetText("保存")
button.Haptic        // bool（タップで端末を震わせる。Android だけ。既定 true）
button.LabelSize     // string（文字の大きさ。トークンの名前か数〈"18"。2026-10-02〉。既定 "text.label"。空ならプレハブのまま）
// 2026-10-02 の拡充
button.Tone          // ButtonTone（Primary=0〈既定〉/ Danger=1 =「削除」などの危険の操作。color.error・color.on_error で塗る・書く）
button.SetTone(ButtonTone.Danger)   // 見た目もすぐ変える（Filled = エラーの塗り・Outlined = エラーの枠と文字・Text = エラーの文字・Tonal は Filled と同じ）
button.FitLabel      // bool（既定 true）: ボタンのレイアウトの大きさが変わるたびに文字の枠をボタンの大きさへ合わせる
                     //（プレハブの「文字の枠 − ボタンの大きさ」の差を保つ。幅いっぱいに伸ばしたボタンでも文字が真ん中に来る。伸ばしていなければ従来と同じ）
```

### Toggle・Checkbox（スイッチ・チェックボックス）

```csharp
toggle.IsOn                          // bool（オンか）
toggle.SetOn(true, animate: true)    // つまみは motion.short 秒で動く（動く間は Redraw.KeepAlive）
toggle.Changed                       // event Action<Toggle, bool>
checkbox.IsChecked / checkbox.SetChecked(true) / checkbox.Changed   // event Action<Checkbox, bool>
```

### Slider・NumberField（スライダ・数値欄）

```csharp
slider.Min / slider.Max / slider.Step / slider.Value   // float（Step 0 = 連続。max も止まれる位置）
slider.SetValue(30f)                  // 範囲・段階へ寄せる（同じ値なら知らせない）
slider.ValueChanged                   // event Action<Slider, float>
slider.TrackLength                    // float（溝の長さ。2026-10-02: 溝はレイアウトの幅に追従する＝コンテナの fill_width・Stretch・flex で伸ばすと
                                      // 「幅 − プレハブの左右の余白」になり、画面の幅が変わるたびに置き直す。伸ばしていなければプレハブの長さのまま）
slider.TickCount                      // int（2026-10-03。刻みの数＝Flutter の divisions。既定 0 = 点なし。1 以上で溝の上に両端を含めて TickCount + 1 個の点。
                                      // 値の段階 Step とは独立〈点を描くだけ〉。上限 SliderTicks.MaxTickCount〈100〉・点の間隔 < 直径 × 2 なら描かない。
                                      // 塗りの上の点は color.on_primary・外は color.on_surface_muted・直径 size.slider_tick〈3〉）
slider.SetTickCount(4)                // 1〜5 分なら 4 刻み（値も寄せるなら slider.Step = 1f）。見た目もすぐ変える
slider.TickPrefab                     // string（点のプレハブ。既定 Slider.DefaultTickPrefab = assets://ui/prefabs/slider_tick.actor）
                                      // 点は子 Ticks〈Fill と Thumb の間〉の下に作る。Ticks の無い古い slider.actor では描かない（警告 1 度。templates/ui を取り込み直す）
number.Min / number.Max / number.Step / number.Value / number.Format / number.Suffix   // 例 Format "0"・Suffix "分"
number.SetValue(15f); number.StepBy(+1)
number.TrySetText("42")               // bool: 数でない入力は捨てる・範囲の外は収める
number.ValueChanged                   // event Action<NumberField, float>
// 同期: slider.ValueChanged += (_, v) => number.SetValue(v); number.ValueChanged += (_, v) => slider.SetValue(v);
```

### SegmentedControl・ChipGroup・RadioGroup（選択）

```csharp
// プレハブ: グループ（SEED.UI.SegmentedControl など）└ 項目（Sprite・CanvasGesture・SEED.UI.SelectItem〈Index・Disabled〉・Label・Dot）
group.SelectedIndex        // int（最初に選んでいる項目の並びの番号。無ければ -1）
group.SelectedIndices      // IReadOnlyList<int>
group.Select(2)            // プログラムから選ぶ（選べない項目は変わらない）
group.SelectionChanged     // event Action<SelectionGroup>
group.InitialSelection     // int[]（最初に選ぶ項目の Index）
chips.Multiple             // bool（ChipGroup。true = 複数〈既定〉/ false = 1 つ〈もう一度押すと外れる〉）
item.Disabled              // bool（SelectItem。選べない項目＝灰色）
group.LabelSize            // string（2026-10-02。項目の文字の大きさ: トークンの名前か数〈"18"〉。既定 "text.label"。空ならプレハブのまま）
group.SetLabelSize("app.text.chip")   // 見た目もすぐ変える（チップ・ラジオ・セグメントごとに文字の大きさを変えられる）
```

### ProgressBar・ProgressRing・ProgressSpinner（進捗）

```csharp
bar.SetValue(0.35f)        // 0..1。塗りの幅が motion.medium 秒で伸び縮み（Animate = false ならすぐ）
ring.SetValue(0.7f)        // 弧の角度 = 値 × 360（真上から時計回り・端は丸い）。形と塗りの弧で描く
ring.Thickness             // float（2026-10-02。輪の太さ。0 以下 = テーマの size.ring_thickness）。ring.SetThickness(3f) / ring.ResolvedThickness

// 不定の進捗（終わりの分からない待ち。2026-10-02。プレハブ templates/ui/prefabs/progress_spinner.actor = Sprite〈弧〉＋ SEED.UI.ProgressSpinner）
// 弧が伸び縮みしながら回る（Flutter の CircularProgressIndicator の不定の動き。色 color.primary）。回っている間（Spinning・押せる・
// 自分と祖先が表示・画面と祖先の切り抜き〈CanvasClip。スクロールの窓など〉の積と重なる。切り抜きは 2026-10-03 から）だけ描き続けを頼むので、
// 隠す・止める・窓の外へ流れると on_demand の描画は止まる
var spinner = UiWidget.Of<ProgressSpinner>(gameObject.FindChild("Spinner"))!;
spinner.SetSize(48f);      // 大きさ（弧の外側の直径。0 以下 = テーマの size.spinner 36。見た目もすぐ変える）。欄は spinner.Size
spinner.SetThickness(5f);  // 弧の太さ（0 以下 = size.spinner_thickness 4。見た目もすぐ変える）。欄は spinner.Thickness
                           // 欄（Size・Thickness）へ直に書くのはプレハブ・インスペクタの初めの値向け（動き始めた後に書いても次に見た目を作り直すまで反映されない）
spinner.SetSpinning(false);// 止める（今の姿のまま）。true で回す（既定）
float d = spinner.ResolvedSize, t = spinner.ResolvedThickness;
SpinnerArc arc = SpinnerMotion.At(seconds, 1.333f, 2.222f);   // 時刻の弧（StartDegrees・SweepDegrees。純粋な計算）
```

### UiTheme・UiTokens（テーマ：読み込み・継承・明暗・切り替え。W2-4・W2-9）

テーマはトークン（例 `color.primary`・`radius.button`）の値の表で、プロジェクトのアセットの JSON です。書いていないトークンは基のテーマ（`extends`）→
組み込みの既定のテーマ（`default_theme.json`。暗い方＋明るい方）の値。切り替えると表示中の全部品（と `ThemeStyle` を付けた飾り）がその場で見た目を当て直します。
JSON の書き方・継承・明暗・Wake or Pay のテーマの写し方・**トークンの表（名前・型・既定値・使う部品）の正典は docs/ui_theme.md**。

```csharp
using SEED.UI;

UiThemeDefinition? forest = UiTheme.Load("assets://ui/themes/forest.json");   // 継承を解いたテーマ（読めない・壊れていれば null。知らない名前・型の誤りは警告して既定の値）
UiTheme.Apply(forest);                          // すぐ切り替える（null で組み込みの既定のテーマ）。全部品をその場で当て直す
UiTheme.Apply(forest, animate: true);           // 色を行き先の motion.theme 秒（既定 0.3）・motion.theme_curve で補間する（数・書体は最初から行き先）
UiTheme.LoadAsset("assets://ui/themes/forest.json", animate: false)   // bool: Load ＋ Apply
UiTheme.FromJson("{\"brightness\":\"light\",\"seed_color\":\"#FF7043\"}")   // UiThemeDefinition?: ファイルなしで作る（Wake or Pay の seedColor など）
UiTheme.BuiltIn / UiTheme.Definition            // UiThemeDefinition: 組み込みの既定のテーマ / 当てているテーマ（Name・Brightness・Supports(明暗)・Resolve(明暗)・Warnings・ChainOrigins）
UiTheme.Current                                 // UiThemeData（今の値の表。補間の途中は途中の表）: Color / Number / Text / TryColor / Has / Describe
UiTheme.Color(UiTokens.ColorPrimary)            // Color（線形。JSON の #RRGGBB は sRGB で書き、読むときに線形へ直す）
UiTheme.Number(UiTokens.RadiusButton)           // float
UiTheme.Text(UiTokens.FontFamily)               // string（書体の assets:// のパス。空 = 組み込み）
UiTheme.Color("app.kakugo.danger")              // アプリ独自のトークン（JSON の app のグループ）
UiTheme.Changed += change => { }                // Action<UiThemeChange>（Theme・Brightness・Animated）: 切り替えごとに 1 回（補間の途中のフレームでは呼ばない）
UiTheme.Version                                 // int: 見た目が変わるたびに増える（補間の毎フレームも）
UiTheme.IsTransitioning                         // bool: 補間の途中か

// 明暗（テーマが light / dark の節か brightness で対応している明暗だけ。対応していなければテーマの明暗のまま）
UiTheme.SetBrightnessMode(UiBrightnessMode.System, animate: false)   // Theme（既定）/ System（端末に従う）/ Light / Dark（強制）
UiTheme.Brightness                              // UiBrightness.Dark / Light（表示している明暗）
UiTheme.BrightnessMode                          // 選び方
UiTheme.SystemBrightness                        // UiBrightness?（System のときに問い合わせた端末の明暗。不明は null）
UiTheme.AnimateSystemChanges = true             // 端末の明暗の変化で色を補間する（既定 false）
```

```jsonc
// assets/ui/themes/forest_round.json（継承と一部だけの上書き）
{ "name": "forest_round", "extends": "forest.json", "color": { "primary": "#00796B" }, "radius": { "button": 24 }, "font": { "weight": 0.35 } }
```

| トークン（例。全部は docs/ui_theme.md §8） | 使う所 |
|---|---|
| `color.primary`・`color.on_primary` | 塗りのボタン・オンのスイッチ・スライダ・進捗 |
| `color.background`・`color.surface`・`color.surface_variant`・`color.on_surface` | 背景・面・溝・台・文字 |
| `color.selected`・`color.on_selected`・`color.outline` | 選んだ項目・枠 |
| `color.disabled`・`color.on_disabled`・`color.state_layer` | 無効・押下の重ね色 |
| `radius.button`・`radius.chip`・`radius.card`・`radius.segment` | 角丸 |
| `size.touch_min`・`size.toggle_knob`・`size.slider_thumb`・`size.ring_thickness` | 大きさ |
| `text.title`・`text.body`・`text.label`・`text.caption` | 文字の大きさ |
| `font.family`・`font.weight`・`font.weight_title` | 部品の文字の書体・太さ（W2-9） |
| `motion.short`・`motion.medium`・`motion.theme` | 動きの時間（秒） |
| `opacity.pressed`・`opacity.disabled` | 濃さ |

> **重要**: 部品は見た目を変えたとき `SEED.Redraw.Request()`、動いている間は `Redraw.KeepAlive` を呼ぶので、`render_policy: on_demand` でも止まりません。
> テーマの補間の間もエンジンが毎フレーム当て直して描き直しを頼みます。テーマ・選び方はシーンの切り替えをまたいで残り、スクリプトを読み直すと（コンパイル・
> ホットリロード）既定へ戻って `UiTheme.Changed` の受け手も外れます。エディタに埋め込んだ Play の開始・停止では読み直さないので前の Play のテーマが残ります。
> 起動のスクリプトの `OnStart` でテーマと選び方を当て、`Changed` に足した受け手は `OnDestroy` で外してください。W2-4 の `UiTheme.Use(UiThemeData)` は無くしました（`UiTheme.Apply` を使う）。

```csharp
UiWidget.RefreshCount      // long: 全部品が見た目を作り直した回数（計測用。ホイールを回している間に他の部品が作り直されないことを見る）
UiRegistry.Count           // int: 登録している部品の数（診断用）
UiTokenCatalog.All         // IReadOnlyList<UiTokenInfo>（Name・Kind・UsedBy）: トークンの表
UiTokenCatalog.Match(token, out UiTokenKind kind)   // Known / AppDefined / UnknownName / UnknownGroup
```

### ThemeStyle（部品でない飾りをテーマに結び付ける。W2-9）

画面の背景・カード・見出し・説明の文字のような部品のスクリプトを持たない見た目は、アクタに ScriptComponent（型名 `SEED.UI.ThemeStyle`）を付けて
欄にトークンの名前を書くと、テーマが替わるたびに自分の Sprite・Text へ当て直します（空の欄は触らない。引けないトークンは 1 度だけ警告）。

| 欄 | 当てる所 | 例 |
|---|---|---|
| `SpriteColor`・`BorderColor`・`CornerRadius` | Sprite の色・縁の色・四隅の角丸 | `color.background`・`color.outline`・`radius.card` |
| `TextColor`・`TextSize` | Text の色・大きさ | `color.on_surface`・`text.title` |
| `TextFont`・`TextWeight` | Text の書体・太さ（既定 `font.family`・`font.weight`） | 見出しは `font.weight_title` |

```csharp
UiTextStyle.Apply(text, UiTheme.Current, UiTokens.TextBody)   // 自作の部品の文字へ大きさ・書体・太さを当てる（同じ値は書かない）
UiTextStyle.Apply(text, UiTheme.Current, "18")                // 2026-10-02: 大きさは数でも書ける（UiTextSize.TryResolve の規則: 空・読めない指定は変えない）
```

## 7.17 UI 部品（SEED.UI：ホイール・時刻ホイール。W2-5）

上下に流れて窓の中央の行が選ばれる**ホイールの列**（`WheelPicker`）と、それを並べた**時刻ホイール**（`TimeWheel`。値は `System.TimeOnly`）。
見本は `templates/ui/prefabs/wheel_picker.actor`・`time_wheel.actor`（行は `wheel_row.actor`）とギャラリー。スクロール（ドラッグ・慣性・行ごとのスナップ）は
エンジンの CanvasScroll、行の曲面の見た目・端をつなげる循環・イベント・触感は部品が受け持ちます。規則の正典は docs/ui_components.md §11。

```csharp
using System;
using SEED.UI;

public class AlarmEditScreen : SEEDScript
{
    private TimeWheel? wheel;

    public override void Update(ref NativeFrameContext ctx)
    {
        // 部品は別のスクリプトなので、登録簿から引く（相手の OnStart の前は null。見つかるまで引き直してよい）
        if (wheel is null && UiWidget.Of<TimeWheel>(gameObject.FindChild("TimeWheel")) is { } w)
        {
            wheel = w;
            wheel.SetValue(TimeOnly.FromDateTime(DateTime.Now), animate: false);   // 新規の初期値は現在時刻
            wheel.ValueChanged += (_, t) => { /* 回している途中も届く（同じ時刻のアラームの警告など） */ };
            wheel.ValueSettled += (_, t) => SEED.Debug.Log($"alarm {t:HH:mm}");  // 全列が止まった
        }
    }
}
```

### TimeWheel（時刻ホイール）

```csharp
// プレハブ: TimeWheel（Sprite〈透明〉・SEED.UI.TimeWheel）├ Band（全列の中央の帯）├ Meridiem ├ Hour └ Minute（それぞれ WheelPicker の列）
timeWheel.Value                        // TimeOnly（今の値。秒は 0）
timeWheel.SetValue(t, animate: true)   // 分の刻みへ丸めて各列を近い向きへ動かす（false = すぐ）。ValueChanged は 1 回だけ
timeWheel.ValueChanged                 // event Action<TimeWheel, TimeOnly>（値が変わるたび。指で回している途中も）
timeWheel.ValueSettled                 // event Action<TimeWheel, TimeOnly>（全列が止まった）
timeWheel.Use24Hour / SetUse24Hour(false)   // 24 時間表記（false = 12 時間表記＋午前/午後の列。値はそのまま）
timeWheel.MinuteStep / SetMinuteStep(5)     // 分の刻み（1 時間を割り切る数。値は最も近い刻みへ丸める。23:58 → 0:00）
timeWheel.Loop / SetLoop(false)        // 端をつなげる（既定 true）。false なら時・分の列は 0 や 23・59 の端で止まる（値は変えず列だけ作り直す）
timeWheel.HourFormat / MinuteFormat    // string（既定 "0" / "00"）
timeWheel.AmLabel / PmLabel / MeridiemOnLeft   // 午前/午後の文字（既定「午前」「午後」）・列を左に置く（既定 true）
timeWheel.HourColumn / MinuteColumn / MeridiemColumn   // WheelPicker?（列。キーボードの Focus などに）
timeWheel.IsMoving                     // bool（どれかの列が動いている）
```

- 12 時間表記で時の列が 11 ↔ 12・23 ↔ 0 を越えると午前/午後の列が動き、午前/午後の列を指で変えると時が 12 ずれます（Flutter の CupertinoDatePicker と同じ）。
- `Loop`（既定 true）が true のときは分の 59 → 00・時の 23 → 0 がそれぞれの列の中でつながります（分が一周しても時は変わりません）。
  `Loop = false` にすると時 0〜23・分 0〜（60 − 刻み）の両端で止まり（CanvasScroll の端の跳ね返り）、23 と 0 は隣り合いません。
  12 時間表記の時の列は Loop の有無によらず 24 行のまま（11 ↔ 12 の午前/午後の連動は変わりません。23 ↔ 0 の継ぎ目が無くなるだけです）。
  午前/午後の列は元々つながらないので影響しません。

### WheelPicker（ホイールの列）

```csharp
// プレハブ: WheelPicker（Sprite〈透明〉・CanvasGesture〈タップ〉・SEED.UI.WheelPicker）├ Band ├ Viewport（CanvasClip・CanvasScroll）└ Blocker
// 数の範囲（インスペクタで作れる）: Min / Max / Step / Value / Format / Suffix / Looping / LimitSelectable / SelectableMin / SelectableMax
wheel.SelectedIndex / SelectedValue    // int（中央の項目の番号 / 数の範囲なら値）
wheel.Count / IsLooping / IsMoving / IsUserInteracting / IsReady
wheel.SelectIndex(i, animate: true)    // 項目を中央へ（つなげる列は近い向きへ回る。最初の描画の前なら置けるようになったとき黙って置く）
wheel.SetValue(15, animate: true)      // 数の範囲の値へ
wheel.StepBy(+1)                       // 選べる項目を 1 つ進める（キーボードの ↓ と同じ）
wheel.Focus()                          // キーボード（↑↓）の相手にする（既定は最後に指で触れたホイール）
wheel.SelectionChanged                 // event Action<WheelPicker, int>（中央の項目が変わるたび。動きの途中も）
wheel.Settled                          // event Action<WheelPicker, int>（止まった。選べない行からは戻り終えてから）
wheel.Configure(count, i => label, looping, selectedIndex)   // 項目の数と文字を渡して作り直す（数の範囲の代わり）
wheel.SetLabels(i => label)            // 文字だけ作り直す
wheel.SetItemEnabled(i => i <= 20)     // 選べる項目（灰色で表示・止まると最も近い選べる項目へ戻る・タップでは動かない）
wheel.Haptic                           // bool（中央の行が変わるたびの軽い触感。指とその慣性の間だけ。既定 true）
wheel.Keyboard / ShowBand              // bool（キーの上下で動かす / 中央の帯を描く）
wheel.ItemExtent / TextSize            // float（0 = テーマの size.wheel_item〈32〉/ text.wheel〈21〉）
wheel.DiameterRatio / Perspective / Squeeze / Magnification / EdgeShade   // 曲面の見た目（既定は Flutter の CupertinoDatePicker）
wheel.RowPrefab                        // string（行のプレハブ。子に Label〈Text〉を持つ .actor。既定 assets://ui/prefabs/wheel_row.actor）
```

| トークン | 既定 | 使う所 |
|---|---|---|
| `size.wheel_item`・`text.wheel` | 32・21 | 行の高さ（スナップの間隔）・行の文字 |
| `radius.wheel_band`・`size.wheel_band_inset` | 8・9 | 中央の帯の角丸・左右の余白（帯の色は `color.surface_variant`） |
| `opacity.wheel_dim` | 0.447 | 中央の帯の外の行の濃さ |
| `motion.wheel`・`motion.wheel_correct` | 0.3・0.2 | タップ・キー・スクリプト・午前/午後の連動の動き（秒）・選べない行から戻る動き |

純粋な計算（エディタのテストで検算）: `WheelLook.Resolve(距離, 窓の高さ, 行の高さ, WheelLookParams)`（行の見た目）・`WheelLook.DistanceAtOffset`（逆）・
`WheelLoop`（循環の添字・近い向きの行・位置）・`TimeWheelMath`（12/24 時間・午前/午後の連動・分の刻み）・
`WheelRowWindow`（2026-10-03。作って置く行の範囲: `CacheExtent(窓の高さ, 行の高さ, WheelLookParams)`〈描ける上限 − 窓の半分 ＋ 1 行〉・
`MaxCreatedRows(…, 行の数)`〈作る行の数の上限。窓 190・行 32 で 12〉・`MaxDrawnRows`〈描く行の上限 9〉）。

> **行の使い回し**: 列は全項目ぶんの行を作りません（W2-5 から。W2-3 の ListView が見えている行と前後の余白だけをプレハブから作り、範囲から外れた行を
> 入ってきた行へ付け替える）。作る行の数は項目の数・周の数によらず列あたり 12 行まで（docs/ui_components.md §11.3）。

> **重要**: ホイールの値の変化で他の部品は作り直されません（書くのはその列の行の文字だけ）。列が動いている間はエンジンが「動いている」を申告するので
> `render_policy: on_demand` でも止まらず、止まって 10 フレームで描画が止まります。

---

## 7.18 画面の組み立て（SEED.UI：画面のスタック・タブ・ダイアログ・シート・覆い・トースト・戻るの段・フォーカス。W2-7）

1 つのシーンに画面をプレハブとして出し入れするための部品です（正典は docs/ui_navigation.md）。見本は `templates/ui/scenes/ui_navigation.scene`
（テンプレートライブラリの「UI 部品」から取り込むと `assets/ui/...`）。部品のプレハブ: `screen_stack.actor`・`screen_frame.actor`・`tab_host.actor`・
`modal_host.actor`・`dialog.actor`・`dialog_item.actor`（選択肢の一覧の行。2026-10-02）・`bottom_sheet.actor`・`top_sheet.actor`・`popup.actor`（中央のポップアップ。2026-10-02）・
`toast_host.actor`・`toast.actor`。
2026-10-02 の拡充（危険のボタン・選択肢の一覧・ボタンの縦積み・進捗の札・長い本文のスクロール・アイコンつきのトースト）の見本は `templates/ui/scenes/ui_gallery.scene` の
「画面の組み立て」の段の 2 行目のボタン。同日の画面の遷移・面の口（lane3: 作り置き `Prewarm`・渡された中身 `Push(GameObject)`・`ModalHost.CloseAll`・
動きなしの開閉・覆いの高さいっぱい・任意の面のプレハブ `ShowPlane`・中央のポップアップ `Popup`・覆いを全画面の下に残す `ModalHost.Park`・
`NavigatorRegistry` の公開）は下のコードの「2026-10-02（lane3）」の行（正典は docs/ui_navigation.md §2.8・§3.1・§3.3・§3.4・§3.6・§3.7・§5.2）。

```csharp
using SEED.UI;

// ── 画面のスタック（ScreenStack。ノードは screen_stack.actor の作り: Screens・Veil・Blocker）──
var stack = UiWidget.Of<ScreenStack>(GameObject.Find("RootStack"));
// 置いてある根: インスペクタの RootAdoptChild（置いてある根の子）に Screens の下の子の名前を書くと、RootPrefab から作る代わりに
// その子を根として引き取る（シーンにプレハブのインスタンスを置いて Edit でも実行時の見た目にする。docs/ui_navigation.md §2.7）
ScreenHandle h = stack.Push("assets://ui/prefabs/edit.actor");                     // 既定の出入り（DefaultTransition。既定 Push = 右から）
stack.Push(prefab, NavTransition.Cover);                                           // 上から覆う
stack.Push(prefab, NavTransition.Fade, args: alarmId,                              // 画面へ値を渡す（UiScreen.OnScreenEnter）
           options: new ScreenOptions { Opaque = true, KeepState = false,          // 覆われたら実体を手放す（戻ったら作り直す）
                                        IgnoreBack = true, SafeArea = true });     // 戻るを無視（鳴動の画面）・安全領域の中に置く
stack.Pop(result);            // bool: 1 つ下ろす（根だけなら false）。結果は下ろした画面の手札へ
stack.Replace(prefab);        // いちばん上を置き換える
stack.PopToRoot();            // 根まで下ろす
stack.SetRoot(prefab, NavTransition.Fade);  // 根からやり直す
stack.Depth  stack.Top  stack.CanPop  stack.IsTransitioning
stack.Changed += s => { };    // 落ち着いた（動きが終わった）後
h.Closed += x => Debug.Log(x.Result);  var r = await h.WhenClosed;   // 閉じるのを待つ
// ── 2026-10-02（lane3）: 中身の出所・作り置き・入れ替わりの順（docs/ui_navigation.md §2・§2.8）──
bool started = stack.Prewarm("assets://alarm/prefabs/edit.actor", new PrewarmOptions   // 空いた時間に隠した枠で組み立てておく（次に積むと枠ごと借りる）
{
    Mode = PrewarmMode.Reuse,       // Once（既定。1 回だけ）/ Refill（使ったら空いた時間に作り直す）/ Reuse（外れたら隠して戻し使い回す。使うたびに OnScreenEnter）
    WarmDrawFrames = 2,             // 温め描き（0 = しない）: 段 0 の画面より奥のレイヤーで描いて文字の字形を焼いておく（根が透けるスタックでは使わない）
    SafeArea = true,                // 中身を枠の Body（安全領域の中）に作る
});
stack.IsPrewarmed(prefab)  stack.GetPrewarmStage(prefab)  stack.DiscardPrewarm(prefab)   // 貸せるか・段階（PrewarmStage: Waiting/Building/WarmDrawing/Ready/Lent/Discarded）・捨てる
ScreenHandle? sh = stack.Push(body, NavTransition.Push, args, options,                    // 組み立て済みの中身（GameObject）を積む（無効なら null。KeepState は常に true）
                              ScreenContentRelease.ReturnToParent);                       // 外れたら積んだときの親へ戻す（既定 Destroy = 画面と一緒に消す）
stack.Replace(body, NavTransition.Fade);                                                  // 置き換えの版（GameObject の中身）
int index = stack.IndexOf(h);                                                             // 段の添字（根 = 0。外れた画面は -1）
// 入れ替わりの順（仕様）: 新しい画面の OnScreenEnter → 動き（古い画面は動きの間も生きて Update が回る）→ 古い画面の OnScreenExit / OnScreenHidden
//   → 新しい画面の OnScreenShown → Changed。共有の頼み（画面を点けたまま等）を入りで取って出で返すなら数え上げにする

// ── 画面のスクリプト（画面のプレハブの根に付ける。任意）──
public class EditScreen : UiScreen
{
    protected override void OnScreenEnter(object? args) { }   // 作られて値を受けた（作り直しでも）
    protected override void OnScreenShown() { }               // 上の画面になった（動きの後。タブへ戻ったときも）
    protected override void OnScreenHidden() { }              // 覆われた・タブを離れた
    protected override void OnScreenExit() { }                // 下ろされる直前
    protected override bool OnBackPressed()                    // 戻る。true = 受けた（スタックは下ろさない）
    {
        if (!dirty) return false;
        Dialog.Show(new DialogOptions { Title = "変更を保存していません", PositiveText = "戻る", NegativeText = "とどまる" })!
              .Completed += r => { if (r == DialogResult.Positive) Close(); };
        return true;
    }
    protected override bool WouldConsumeBack() => dirty;       // 今戻るが来たら OnBackPressed が true か（副作用なしの問い。
                                                               // OnBackPressed を上書きしたら同じ条件で上書きする。上書きしないと
                                                               // 「受ける」とみなされ、根でも Android の予測型の戻る〈ホームへ戻る見た目〉が出ない）
    protected override bool IsPrewarmReady => rowsBuilt;       // 2026-10-02: 作り置き（Prewarm）が温まったか（重い準備を Update で続ける画面。既定 true）
    // Close(result) で自分を下ろす・Navigator（積んだスタック）・Handle・Args
}

// ── 下のタブ（TabHost・TabBar・TabItem。tab_host.actor: Pages/Tab0..〈ScreenStack〉・TabBar/Item0..）──
var tabs = UiWidget.Of<TabHost>(GameObject.Find("TabHost"));
tabs.Select(1);               // 選んでいるタブを押すと根へ戻る（TabReselected）
tabs.SelectedIndex  tabs.CurrentStack  tabs.StackAt(i)  tabs.Count
tabs.TabChanged += (t, i) => { };  tabs.TabReselected += (t, i) => { /* 先頭へスクロール */ };
// フィールド: TabNames（Pages の子の名前）・InitialTab・BackToFirstTab（既定 true）・ResetOnLeave（既定 false）

// ── ダイアログ・シート・覆い（シーンに ModalHost〈modal_host.actor〉を置く）──
DialogHandle? d = Dialog.Show(new DialogOptions
{
    Title = "削除しますか？", Message = "…", PositiveText = "削除", NegativeText = "やめる", NeutralText = "",   // ボタン 1〜3
    DismissOnScrimTap = true, CancelableByBack = true,
});
DialogResult r = await d!.ResultAsync;          // Positive / Negative / Neutral / Dismissed（幕・戻る）。d.Completed += r => …
// 札の大きさは中身から決まる（W2 の手直し P2-1。下の「ダイアログの大きさと動き」）。文字の見積もりはスクリプトからも使える:
int lines = DialogLayout.EstimateLines("寝坊で失う最大金額が 3,000 円になります。", 16f, 264f);   // 1（エンジンの折り返しの規則・組み込みの書体の送り幅）
float h = DialogLayout.EstimateHeight(text, 16f, 264f);   // 行の数 × 16 × DialogLayout.LineHeightEm（1.4）。枠 DialogLayout.NoWrapWidth（0）は折り返さない
float w = DialogLayout.EstimateWidth("やめる", 14f);       // 1 行の幅（改行を含むなら最も広い行）= 30.1
// ── 2026-10-02 の拡充（新しい区画は templates/ui の dialog.actor〈2026-10-02 版〉と dialog_item.actor で使える）──
// 危険のボタン（「削除」「破棄して戻る」）: 種類 Danger で color.error（Positive は塗り、ほかは文字の色）
Dialog.Show(new DialogOptions { Title = "アラームを削除しますか？", PositiveText = "削除", PositiveKind = DialogButtonKind.Danger, NegativeText = "やめる" });
// 選択肢の一覧（Material の SimpleDialog。長押しのメニュー）: 題の下に項目（アイコン欄＋文字）を縦に並べ、押した項目で閉じる
var menu = Dialog.ShowMenu("7:30 のアラーム",
    new DialogMenuItem("編集する", UiIcon.Circle()),                                  // アイコン: UiIcon.Image("assets://…png", 色?)・Circle・Ring・Square
    new DialogMenuItem("共有する") { Enabled = false },                               // 選べない（灰色・押せない）
    new DialogMenuItem("削除する", UiIcon.Ring(), DialogButtonKind.Danger));          // 危険（color.error）
menu!.Completed += r => { if (r == DialogResult.Selected) Edit(menu.SelectedIndex); };   // Selected・SelectedIndex（DialogOptions.Items の添字。それ以外は -1）
// DialogOptions.Menu(題, 項目…) で作り、NegativeText などを足せば一覧の下にボタンも出る。行のプレハブは DialogOptions.ItemPrefab で替えられる
// 進捗の札（ボタンなし・幕のタップと戻るでは閉じない）: 本文を変える・外から閉じる
var busy = Dialog.ShowProgress("購入の手続きをしています…", "購入");                  // スピナー（ProgressSpinner）＋本文
busy!.SetMessage("もう少しで終わります");                                            // 本文を変える（札の高さも合わせ直す。開く前に呼んでもよい）
busy.Close(DialogResult.Positive);                                                    // 外から結果つきで閉じる（Dismiss() は Dismissed）
// 外から閉じてもボタンと同じ決め方を通る（2026-10-03）: 入力つきのダイアログを Close(DialogResult.Positive) で閉じると InputText が入る
// （入力欄がまだできていなければ初めの文字）。外から Close(DialogResult.Selected) は番号が無いので Dismissed で閉じる（警告）
// ボタンの行: 文字の幅の和 ＋ 間隔が札の中の幅に入らなければ縦に積む（右寄せ・上から中立・いいえ・はい。間 size.dialog_actions_overflow_gap = 0）
// 本文・選択肢が札に入りきらない（画面の高さ − 安全領域 − size.dialog_margin × 2 を超える）ときは、選択肢 → 本文の窓を縮めてスクロール（題とボタンは見えたまま）
// HideButtons = true でボタンの行を出さない（文字を指定していても）
ModalHandle? s = BottomSheet.Show(new SheetOptions { ContentPrefab = "assets://…/sound_list.actor", Args = …,
                                                     HalfDetent = true, StartHalf = true, HeightFraction = 0.9f });
ModalHandle? o = TopSheet.Show(new OverlayOptions { ContentPrefab = "assets://…/profile.actor" });
s.Close(result);  await s.WhenClosed;           // 中身から閉じる（結果つき）。幕・戻る・つまみで閉じたら結果 null
ModalHost.Current!.Count(ModalKind.Dialog)      // 開いている数
// ── 2026-10-02（lane3）: 全部閉じる・動きなし・高さいっぱい・任意の面・中央のポップアップ・覆いを全画面の下に残す（docs/ui_navigation.md §3）──
int closed = ModalHost.Current!.CloseAll(animate: false);   // ダイアログ → シート → 覆いの順・同じ種類は新しい順。ダイアログは Dismissed・ほかは null。
                                                            // 閉じない設定の面も閉じる。false なら手札の Closed もこの中で届く。戻り値 = 閉じた数
ModalHost.Current!.CloseAll(ModalKind.Dialog, animate: true);   // 種類を絞る
o.Close(result, animate: false);                            // 出る動きを見せずにすぐ閉じる（DialogHandle.Close(DialogResult, false)・ModalPlane.RequestClose(結果, false) も）
TopSheet.Show(new OverlayOptions { ContentPrefab = "assets://…/options.actor",
                                   Animate = false,                       // 動きなしで開く（中身が落ち着いたら降りた姿で出る）
                                   FillHeight = true, FillBottomMargin = -1f });   // 高さいっぱい（中身の根の CanvasLayoutItem の高さを合わせる。負 = space.m）
BottomSheet.Show(new SheetOptions { ContentPrefab = "assets://…/list.actor", Animate = false });   // 開く段へすぐ移す
ModalHost.Current!.ShowOverlay(options, "assets://app/prefabs/my_top_sheet.actor");    // 任意の面のプレハブで開く（ShowDialog・ShowSheet・ShowPopup も同じ形）
ModalHost.Current!.ShowPlane(ModalKind.Overlay, "assets://app/prefabs/my_plane.actor", myOptions);   // 自前の ModalPlane の派生（OnPlaneStart の Options で受ける。
                                                                                                     // 中身の UiScreen へは protected static DeliverEnter(中身の根, 値)）
ModalHandle? p = Popup.Show(new PopupOptions                // 中央のポップアップ（templates/ui/prefabs/popup.actor。ModalHost.PopupPrefab・ShowPopup）
{
    ContentPrefab = "assets://profile/prefabs/profile.actor", Args = profileId,
    DismissOnScrimTap = true, CancelableByBack = true, ShowCloseButton = true, Animate = true,
    Width = 0f, ContentHeight = 0f,                         // 0 以下 = 画面の幅 − size.popup_margin × 2（上限 size.popup_max_width）・高さは中身から
    Kind = ModalKind.Overlay,                               // 入れる帯と戻るの層（シートの上に出すなら Dialog）
});
public class ProfileContent : UiScreen, IPopupContentSize { public float PopupContentHeight => measured; }   // 札の高さを中身に合わせる（0 以下 = まだ分からない）
ScreenHandle page = root.Push("assets://options/prefabs/cap_ceiling.actor");
ModalHost.Current!.Park(p!, root, page);                    // 覆いを閉じずに全画面の下へ回す（全画面が閉じたら自動で戻る。戻るは全画面へ届く）
ModalHost.Current!.IsParked(p!)  ModalHost.Current!.Unpark(p!)   // 回しているか・先に戻す。ModalPlane.IsParked
NavigatorRegistry.IsActiveNode(gameObject)                  // 自分の画面が見えていて上の段の中にあるか（2026-10-02 に公開）
NavigatorRegistry.DispatchBack()  NavigatorRegistry.WouldHandleBack()  NavigatorRegistry.BackPreviewTarget()   // 戻るの段の Navigation の層そのもの
stack.HandleBack()  stack.WouldHandleBack()  stack.BackPreviewTarget                  // 特定のスタックへ戻るを渡す・問う・プレビューの相手（独自の戻るの層から）

// ── トースト（シーンに ToastHost〈toast_host.actor〉を置く）──
Toast.Show("保存しました");                       // ToastLength.Short（motion.toast_short）/ Long
ToastHost.Current!.Show("…", 5f);               // 秒を指定。同時に count.toast_visible 個まで・あふれた分は待つ・横へ払うと消える
Toast.Show("保存しました", UiIcon.Ring());        // 2026-10-02: 先頭のアイコン（図形か画像。大きさ size.icon・文字との間 size.icon_gap。toast.actor の Icon の子）
Toast.Show("通信に失敗しました", UiIcon.Circle(UiTheme.Color(UiTokens.ColorError)), ToastLength.Long);
ToastHost.Current!.Show("…", UiIcon.Image("assets://ui/icons/check.png"), 3f);   // 画像は色を指定しなければ画像の色のまま（白を掛ける）

// ── 戻るの段（Android の戻る・PC の Esc。部品が毎フレーム読むので、スクリプトは何もしなくてよい）──
BackDispatcher.Dispatch();                        // 画面の「戻る」ボタンから戻るを配る
using var layer = BackDispatcher.AddLayer(450, "my-panel", () => { /* 受けたら */ return true; });   // 独自の層（順は BackOrder の間に。問いなし＝いつも受けるとみなす）
using var layer2 = BackDispatcher.AddLayer(450, "my-panel", () => panel.Close(),                      // W2 の手直し 3b: 受けるかの問いつき
                                            wants: () => panel.IsOpen,                                // 副作用の無い「今押されたら受けるか」（予測型の戻るの根の判定）
                                            preview: () => panel);                                    // 任意: 手ぶりの間に縮めて見せる相手（IBackPreviewTarget。null = 縮めない）
bool handles = BackDispatcher.WouldHandle();     // 今押したらアプリが受けるか（副作用なし。どれかの層が受ける or MoveTaskToBackWhenUnhandled = false）
bool behind = BackDispatcher.WouldHandleAfterFocus(); // Focus の層より後ろの層（ダイアログ・シート・覆い・画面）が受けるか（副作用なし。背面へ回すのは数えない）。
                                                  // IBackConsumer の入力欄が「根ではキーボードを閉じた後の戻るも受ける」ために読む（W2-6 の実機の直し）
BackDispatcher.Dispatched += r => { };            // r.Handled・r.Layer
BackDispatcher.MoveTaskToBackWhenUnhandled = true; // どの層も受けなければ Platform.App.MoveTaskToBack()（既定）

// 予測型の戻るのプレビューの相手（W2 の手直し 3b。ScreenStack の上の画面・Dialog・BottomSheet・TopSheet が実装済み。独自の層で使うときだけ）
public class MyPanel : IBackPreviewTarget
{
    public bool IsBackPreviewValid => isOpen;         // 今もプレビューできるか（false になったらすぐ元へ戻される）
    public bool IsBackPreviewExiting => isClosing;    // 確定の後、閉じる動きの途中か（終わったら ClearBackPreview が呼ばれる）
    public void ApplyBackPreview(BackPreviewPose pose)    // pose.Scale（1 → 0.9）・pose.ShiftX（画面用の横のずらし。キャンバスの単位）
    {
        if (node.GetComponent<CanvasLayoutItem>() is { } item) item.VisualScale = new Vector2(pose.Scale, pose.Scale);
    }
    public void ClearBackPreview()
    {
        if (node.GetComponent<CanvasLayoutItem>() is { } item) item.VisualScale = Vector2.One;
    }
}

// ── フォーカス（キーボードで動かす相手と、画面ごとの範囲）──
UiFocus.Request(item)  UiFocus.Release(item)  UiFocus.Current  UiFocus.TopScope  UiFocus.Changed
public class MyField : UiWidget, IFocusable, IBackConsumer { … }   // FocusOwner・OnFocusChanged・HandleBack（W2-6 の入力欄の形）
```

**戻るの段の順**（`BackOrder`）: Focus（100。今のフォーカスが `IBackConsumer` なら。W2-6 の入力欄が IME を閉じる）→ Dialog（200）→ Sheet（300）→
Overlay（400）→ Navigation（500。画面のスタック・タブを**内側から**: 上の画面の `IgnoreBack`・`OnBackPressed` → 1 つ下ろす → 最初のタブ以外なら最初のタブへ）→
どれも受けなければ `SEED.Platform.App.MoveTaskToBack()`（閉じずに背面へ。デスクトップの模擬はログだけ）。閉じられないダイアログも戻るは受けます（後ろへ回さない）。

**予測型の戻る（W2 の手直し 3b。`android.predictive_back: true` の Android 13 以上）**: 戻るの段はフレームに 1 回 `WouldHandle()` を計算し、変わったときだけ
`App.SetBackCallbackEnabled` で基盤へ知らせます（根ではシステムが背面へ回し、ホームへ戻る見た目が出る。無効〈PC・設定なし〉と分かったら以後は計算しない）。
手ぶりの間は最初に受ける層の相手を縮めて見せ（画面は指の向きへ少しずれ、下の画面が覗く・ダイアログの札・シートの板は下の辺・覆いの板は上の辺を留める）、
Escape で確定すると縮んだ姿勢から閉じる・下ろす（閉じなければ元へ戻る）。問えない層（`IBackConsumer`・`OnBackPressed` を上書きした画面・問いの無い `AddLayer`）は
「受ける」とみなします。PC では `PlatformDiagnostics.SimulateBackGesture` で流した手ぶり＋Esc キーで試せます。

**ダイアログの大きさと動き（W2 の手直し P2-1。2026-09-29。正典は docs/ui_navigation.md §3.2）**: 札の幅は `size.dialog_width`、高さは
「余白 `size.dialog_padding` ＋ 題 ＋ `size.dialog_title_gap` ＋ 本文 ＋ `size.dialog_actions_gap` ＋ ボタンの行 ＋ 余白」（空の題・本文とその間隔は数えない。
本文が無ければ題 → ボタンの行は `size.dialog_actions_gap`）で、`Dialog` が札の `CanvasLayoutItem.PreferredSize` と背景の `Sprite.Size` に書きます
（既定のテーマで 題 ＋ 本文 1 行 ＋ ボタン = 178.4 dp。純粋な計算は `DialogMetrics.Arrange`・`DialogMetrics.Sections`）。区画ごとの間隔は札の CanvasStack の
等間隔ではなく、上の区画の枠の高さに足します（札の CanvasStack の間隔は 0）。題・本文の高さは見積もり（`DialogLayout`。`Text.Measure` は W2-6c）:
行の数はエンジンの折り返しの規則（語・空白のぶら下げ・日本語は 1 文字ずつ・禁則・強制分割）と組み込みの書体の送り幅（全角 0.7168 em ＝ 書体の 1000 / 1395）で求め、
題と本文の `Text.LineSpacing` を `DialogLayout.LineHeightEm`（1.4）にして描く行送りと一致させます（テーマの `font.family` でほかの書体を当てると合いません）。
出入りの動きの札の大きさ（`ratio.dialog_scale_from` 0.9 ↔ 1）は `CanvasLayoutItem.VisualScale`（札の矩形の中心の周りに背景・文字・ボタンが一体で縮む）に
「開き具合の倍率 × 予測型の戻るのプレビューの倍率」（`DialogMetrics.CardScale`）を書きます。戻るを確定した後は、縮めた姿勢のまま閉じます。

**2026-10-02 の区画（正典は docs/ui_navigation.md §3.2）**: 札は上から 題・進捗（スピナーと本文の行）・本文（切り抜く窓の中）・選択肢の一覧（窓の中）・
1 行の入力欄・ボタンの行（`DialogMetrics.Sections(DialogContentHeights, DialogSpacing)`。区画の番号 `TitleSection`〜`ButtonsSection`）。
選択肢の一覧の上下の空きと、一覧が札の端に来るときの札の余白は `size.dialog_items_inset`（12）、行の高さ `size.dialog_item_height`（48）、行は札の幅いっぱい
（札の左右の余白を 0 にして、ほかの区画は中の幅で真ん中に置く）。札の高さが上限（`DialogMetrics.MaxCardHeight` = 画面 − 安全領域 − `size.dialog_margin` × 2）を
超えると `DialogMetrics.Fit` が選択肢 → 本文の窓を 1 行まで縮め、その窓をスクロールにします（縮めない窓はスクロールも切り抜きも止める＝従来の見た目）。
ボタンの横並び・縦積みは `DialogActionsLayout.Arrange`、選択肢の行の見た目は `DialogItemLooks`。**古いプレハブ**（2026-10-02 より前の dialog.actor）でも動きますが、
本文はスクロールせず、進捗の札のスピナーは出ず（本文だけ）、選択肢の一覧は出せません（警告）。プレハブを取り込み直してください。

**画面の遷移・面の口（2026-10-02。lane3。正典は docs/ui_navigation.md §2.8・§3）**: `ScreenStack.Prewarm` は空いた時間（出入りの動きの無い間）に
隠した枠（`FramePrefab`）の中で画面を組み立てておき、次にそのプレハブを積むときに枠ごと貸す（プレハブの組み立ての重いフレームが無い。中身の出所は
渡された中身 > 置いてある根 > 作り置き > プレハブ）。温まった = できあがって 2 フレーム経ち `UiScreen.IsPrewarmReady` が true（上限 300 フレーム）。
`ModalHost.CloseAll` はダイアログ → シート → 覆いの順に閉じ、ダイアログの結果は `Dismissed`、シート・覆いは null（閉じない設定の面も閉じる。`animate: false` なら
出る動きなしでこの中で閉じ、手札の知らせもこの中で届く。作りかけの面は見せずに取りやめる）。`ModalHost.Park(面, スタック, 全画面)` で回した面は戻るの層で数えず
（戻るは全画面へ届く）、フォーカスの範囲も後ろへ回り、全画面が閉じると自動で戻る。中央のポップアップの札の大きさは `PopupCardMath`（幅 = 画面 − `size.popup_margin` × 2・
上限 `size.popup_max_width`、高さ = 中身 ＋ `size.popup_padding` × 2・上限 = 安全領域 × `ratio.popup_max_height`）。

**重なりと入力**: 画面のスタックの段 i は `LayerBias = i × LayerStep`（既定 `layer.stack_step` = 10,000。タブの中のスタックは 1,000）、
覆い・シート・ダイアログ・トーストは帯（`layer.overlay`・`sheet`・`dialog`・`toast` = 100 万・200 万・300 万・400 万）。
**画面の中の表示のレイヤーは段の値より小さく**（タブの中なら 1,000 未満）保ってください。積んだ画面の枠（`screen_frame.actor`）は遮る板を持ち、
下の画面は入力を受けません。落ち着いた後は不透明な画面の下の画面を隠します（描かない）。

| テーマのトークン（抜粋。全部は docs/ui_theme.md §8） | 既定 | 意味 |
|---|---|---|
| `motion.push`・`motion.cover`・`motion.fade` と `*_curve` | 0.3 秒・Material 3 standard | 画面の出入り（曲線は x1・y1・x2・y2） |
| `motion.overlay`・`motion.overlay_curve` | 0.22 秒・easeOut | 上からの覆い（Flutter 版の top_sheet） |
| `motion.dialog`・`motion.sheet`・`motion.toast` | 0.2・0.25・0.2 秒 | ダイアログ・シート・トーストの出入り |
| `size.dialog_width`・`size.dialog_padding`・`size.dialog_button_height` | 312・24・40 dp | ダイアログの札の幅・内側の余白（上下左右）・ボタンの高さ |
| `size.dialog_title_gap`・`size.dialog_actions_gap` | 16・24 dp | ダイアログの題 → 本文・本文（無ければ題）→ ボタンの行の間隔（W2 の手直し P2-1。Flutter の AlertDialog〈Material 3〉の contentPadding の上 16・下 24） |
| `motion.toast_short`・`motion.toast_long` | 2・3.5 秒 | トーストを見せる時間（実時間） |
| `opacity.scrim`・`opacity.dialog_scrim` | 0.54・0.32 | 幕の濃さ |
| `ratio.push_parallax`・`ratio.dialog_scale_from`・`ratio.sheet_max_height` | 0.3・0.9・0.9 | 視差・ダイアログの出始めの大きさ・シートの高さ |
| `ratio.back_preview_scale`・`size.back_preview_shift`・`motion.back_preview_curve` | 0.9・8 dp・(0, 0, 0, 1) | 予測型の戻るのプレビュー（いちばん小さい倍率・画面のずらし・進み具合の曲線。3b。取り消しで戻る時間は `motion.short`） |
| `size.popup_margin`・`size.popup_max_width`・`size.popup_padding`・`ratio.popup_max_height`・`radius.popup` | 16・560・8 dp・0.8・28 | 中央のポップアップ（札と画面の端の余白・幅の上限・札の内側の余白・高さの上限〈安全領域に対する割合〉・角丸。2026-10-02） |

> **重要**: 画面のプレハブは次のフレームにできあがる（`Instantiate` の遅延）ので、画面の枠 → 中身の 2 フレームかけて作り、できあがるまで隠します。
> 積み下ろしは並びをすぐ変え、動きは順に流します（動いている途中の次の操作は、今の動きを飛ばしてから始める）。
> 動きの間は `Redraw.KeepAlive` で描き続け、落ち着いたら `render_policy: on_demand` で描画が止まります。
> **出入りの時計**（2026-09-30）: 入ってくる画面の動きは、その画面が `OnScreenEnter` を受けて 1 フレーム描いた後から始まり（動きの無い `None` の入れ替えも同じ。待つ間は入力を止める）、
> 動き始めたフレームの経過は数えず、1 フレームで進める時間は `MotionStep.MaxFrameSeconds`（1/30 秒）まで。重いフレームで動きが飛ばない代わりに、その分だけ長くかかります
> （ダイアログ・上からの覆い・トーストの出入りも上限つき。下からのシートは対象外）。背面へ回ると動きはその姿で止まり、前面へ戻ると続きから動きます。詳しくは docs/ui_navigation.md §2。

## 7.19 グラフ（SEED.UI：LineChart・BarChart。W2-8）

折れ線・棒（縦・横・積み上げ）・軸・吹き出し・パンとズームのグラフ。プレハブ（`templates/ui/prefabs/line_chart.actor`・`bar_chart.actor`。目盛りの文字は
`chart_label.actor`）を置き、画面のスクリプトからデータを渡す。大きさは**レイアウトが決めた大きさ**（`CanvasTransform.LayoutSize`。縦の `CanvasStack` の
cross_align stretch・`CanvasLayoutItem` の fill_width で伸ばせば画面の幅に合う。前のフレームの描画の値。W2 の手直し P2-4）。レイアウトの表に無い所
（3D ワールドキャンバスの下など）ではグラフのノードの **Sprite の幅・高さ**（dp のキャンバスでは dp）。最初のレイアウトを読めるまで（最大 3 フレーム）は描かない。
正典は `docs/ui_charts.md`（作り・大きさ・描き方と性能の数値・目盛りの選び方・パンとズーム・吹き出し・日付線のハンドル・トークン）。見本は `templates/ui/scenes/ui_charts.scene`。

```csharp
using SEED.UI;

// データの形: X・値は軸の書式に合わせた数（日付 = DateOnly.DayNumber、時刻 = 0 時からの分、数）。値が null の点は「記録なし」
var points = new List<ChartPoint>
{
    ChartPoint.Minutes(new DateOnly(2026, 9, 27), TimeSpan.FromMinutes(425)),  // 9/27 7:05
    ChartPoint.Minutes(new DateOnly(2026, 9, 28), null),                        // 記録なし（点を打たずに前後をつなぐ）
    ChartPoint.Day(new DateOnly(2026, 9, 29), 410),                             // X = 日の番号・値 = 410
};

// 折れ線（LineChart）
var line = UiWidget.Of<LineChart>(GameObject.Find("WakeWeek"))!;
line.XFormat = ChartValueFormat.Date;          // 横軸の目盛り M/d（1・2・3・7・14・30・61・91・182・365 日から間引く）
line.YFormat = ChartValueFormat.TimeOfDay;     // 縦軸の目盛り H:mm（15・30・60・120・180・360・720 分から）。上下 30 分の余白・最小 2 時間
line.Interactive = false;                      // パンとズームを受けない（30 日の島）。true なら横のドラッグ・払う・ピンチ・± で 1〜MaxZoom 倍
line.Smooth = true;                            // 単調な 3 次補間（点と点の間で行き過ぎない）
line.FillArea = true;                          // 線の下の塗り（線の色から下へ透明へのグラデーション）
line.FixedXRange = new ChartRange(first.DayNumber, today.DayNumber);   // X の全体を固定（null = データの最初〜最後）
line.TooltipFormatter = p => $"{ChartFormat.Date(p.X)} {ChartFormat.TimeOfDay(p.Y ?? 0, padHour: true)}";  // 吹き出し（M/d HH:mm）
line.SetSeries(points);                        // 系列 0（SetSeries(index, points) で系列を足す。色は color.chart_series_N）
line.SetReferenceLine(average, "平均 7:12");   // 基準線（平均の横線）
line.PointSelected += (chart, series, index) => { /* タップ・長押し・日付線のハンドルで選んだ点（外したら -1, -1） */ };
line.HandleHaptic = true;                      // 日付線のハンドル（プレハブの子 Handle）で点が変わるたびの軽い触感（既定 true。1 フレームに 1 回まで）

// 棒（BarChart）: 値を下から積む（BarDatum(x, 段1, 段2, ...)）。合計 0 の列も最低の高さで出す
var bars = UiWidget.Of<BarChart>(GameObject.Find("PenaltyHistory"))!;
bars.Orientation = BarOrientation.Vertical;    // Horizontal = 横の棒（X が上から下）
bars.SlotWidth = 1f;                           // 列の間隔（X の単位。日なら 1）
bars.SetData(days.Select(d => new BarDatum(d.Day.DayNumber, d.Coins, d.Yen)).ToList());
bars.BarSelected += (chart, index) => { /* 列をタップ（空の高さまで当たり） */ };
bars.Select(lastLossIndex);                    // スクリプトから選ぶ（notify: true で BarSelected を出す）
bars.XLabelFormatter = (v, step) => $"{(int)v}月";   // 目盛りの文字を独自に（値, 刻み）

// 共通（ChartView）
line.ZoomIn(); line.ZoomOut(); line.ZoomBy(2.0);        // 真ん中を中心に（上下限で止まる。animate: false ですぐ）
line.ShowRange(new ChartRange(start, start + 30));      // 範囲をちょうど見せる
line.ScrollToEnd();                                     // 右端（最新）へ
line.ClearSelection();
line.Viewport.Zoom; line.Viewport.Visible;              // 今の倍率・見える範囲（ChartViewport）
line.ValueRangeOptions = LineChart.DefaultRangeOptions(ChartValueFormat.TimeOfDay);   // 値の範囲の決め方（AutoRangeOptions）
line.FixedValueRange = new ChartRange(300, 600);        // 値の範囲を固定（null = 自動）
line.MarkDirty();                                       // 書式・刻みの候補などの欄を変えたら作り直す
```

| 部品・型 | 役割 |
|---|---|
| `ChartView`（土台） | 欄: `Interactive`・`MaxZoom`（6）・`ShowTooltip`・`XFormat`・`YFormat`・`XSteps`・`YSteps`（刻みの候補 "15,30,60"）・`ShowYAxis`・`ShowXAxis`・`ShowGrid`・`EmptyText`（「まだ記録はありません」）・`LabelPrefab`・`HandleHaptic`（日付線のハンドルの触感。既定 true。P2-4）。スクリプト: `XLabelFormatter`・`YLabelFormatter`・`ValueRangeOptions`・`FixedValueRange`・`FixedXRange`・`Viewport`・`ViewChanged`・`ZoomIn/ZoomOut/ZoomBy`・`ShowRange`・`ScrollToEnd`・`ClearSelection`・`MarkDirty`・計測の `RebuildCount`・`LastDrawCount`・`LastRebuildMs`・`LastPaintMs` |
| `LineChart` | 欄: `Smooth`・`FillArea`・`ShowDots`（点が詰まる倍率では打たない）・`Gaps`（`Connect` / `Break`）。`SetSeries`・`ClearSeries`・`GetSeries`・`SetReferenceLine`・`ClearReferenceLine`・`Select(series, index, notify)`・`Selected`・`PointSelected`・`TooltipFormatter`・`DefaultRangeOptions(format)`。プレハブの子 `Handle` があれば日付線のハンドル（下の表） |
| `BarChart` | 欄: `Orientation`・`SlotWidth`・`ShowEmptyBars`・`HighlightSelection`。`SetData`・`Get`・`Select(index, notify)`・`SelectX`・`SelectedIndex`・`BarSelected`・`TooltipFormatter`・`StackColors`（日付線のハンドルは無い） |
| 純粋な計算 | `ChartTicks`（`NiceStep`・`StepFromCandidates`・`Generate`・`NiceBounds`）・`ChartAxis`・`ChartAutoRange`（`AutoRangeOptions`）・`ChartFormat`（`TimeOfDay`・`Date`・`Number`）・`ChartMapping`・`ChartViewport`・`ChartFling`・`MonotoneCubic`・`ChartHit`（ハンドルの吸い付き `NearestValuedX`）・`BarGeometry`・`ChartLayout`（`HandlePosition`・`HandleFingerPlotX`・`InsidePlot`）・`LinePath`・`ChartSizing`（大きさの選び方 `Choose`・待つ上限 `DefaultMaxLayoutWaitFrames`）・`ChartLayoutWait`・`ChartTokens`・`ChartLook` |

| 操作 | 振る舞い |
|---|---|
| タップ・長押し | 折れ線は横の距離 24 dp 以内の最寄りの点、棒は押した列（縦は問わない）を選んで吹き出し。選んだ点がパンで外へ出たら隠す |
| 横のドラッグ・払う | パン（指の下の値が付いてくる）と慣性（1 秒で速度 0.135 倍）。端で止まる。倍率 1 や `Interactive = false` ではドラッグを受けない（親のスクロールへ渡す） |
| 2 本指のピンチ | 倍率 = 始めの倍率 × `e.Scale`（1〜MaxZoom）。始めのフォーカスの値を今のフォーカスへ置く（ズームと 2 本指のパン） |
| ± のボタン | グラフの子に `ZoomIn`・`ZoomOut`（`SEED.UI.Button`）を置くとつながる。真ん中を中心に 2 倍・½（0.25 秒）。上限・下限で押せない。置き場は部品が決めない（見本は anchor x 1 でグラフの右の端に付け、グラフが親の幅に伸び縮みしても付いてくる。W2 の手直し P2-5） |
| 日付線のハンドル（折れ線。W2 の手直し P2-4） | 選んだ点の縦の線（日付線）の下の丸（直径 `size.chart_handle` 18・当たり 48 dp・面の下の縁に乗る）。押して横へ引くと、パン・ピンチより先に指を取り、選んでいる系列の値のある点のうち見えている範囲で指の X に最も近い点へ吸い付く（日付線・点・吹き出しが付いてくる。面の外の指は端の点で止まる・自動のパンは無い）。点が変わるたびに `PointSelected` と触感（`HandleHaptic`）。縦の移動は取らない（縦のスクロールへ渡る）。倍率 1 でも効く。離しても取り消されても選びは残す |

> **重要**: グラフは毎フレーム `SEED.Draw` で描く（`render_policy: on_demand` で止まっている間は描かない）。データ・見える範囲・大きさ・テーマが変わったフレームだけ
> 位置を計算し直し、目盛りの文字（プレハブのノード）は変わった値だけを書き換える。性能の数値（365 点の折れ線・365 本の棒）は `docs/ui_charts.md` §3.1。
> 大きさ（`LayoutSize`）も毎フレーム読む（コンテナ・画面の回転で変わった次のフレームに追従する。フレームで最初の読み出しがレイアウトの表の索引を作る費用は同じ §3.1 の末尾）。

## 7.20 文字入力（SEED.UI.TextField・SEED.TextInput・SEED.TextMeasure。W2-6）

1 行の入力欄。日本語の変換（変換中の文字の下線・確定）・数字だけの欄（数字のキーボード）・完了のアクション・カーソル（点滅）・選択・最大の長さ・
貼り付けとコピーの禁止・キーボードを避ける（スクロールの中身の末尾の余白と送り・ダイアログの持ち上げ）・戻るで先にキーボードを閉じる。
プレハブは `templates/ui/prefabs/text_field.actor`（文字の欄）・`number_input.actor`（数値の欄。幅 112・数字は中央で大きめ）。
本文・選択・変換中の区間はエンジンが持ち（PC は IME とキー、Android は IME の知らせをエンジンが吸収する）、部品は見た目とフォーカスを受け持つ。
添字は string の添字（UTF-16 の単位）。正典は `docs/ui_text_input.md`、見本は `templates/ui/scenes/ui_text_input.scene`。

```csharp
using SEED.UI;

// 入力欄（プレハブを置き、画面のスクリプトから引く。欄はインスペクタでも決められる）
var field = UiWidget.Of<TextField>(gameObject.FindChild("NameField"))!;
field.Placeholder = "例：田中太郎";            // 例の文（本文が空のときに薄く出す）
field.Kind = TextInputKind.Text;               // Text（文字。日本語の変換）/ Number（0〜9 だけ。全角の数字は半角へ・ほかは捨てる。数字のキーボード）
field.Action = TextInputAction.Done;           // キーボードのアクションのボタン・PC の Enter（Done・Next・Go・Search・Send・Previous・None）
field.MaxLength = 20;                          // 最大の長さ（見た目の文字＝書記素の数。0 = 制限なし。変換中は超えてよく、確定したら切り詰める）
field.AllowPaste = false;                      // 貼り付けを禁止（PC の Ctrl+V・Shift+Insert を止め、Android は IME からの一度の大きな挿入を戻す）
field.AllowCopy = false;                       // コピー・切り取りを禁止（PC の Ctrl+C・Ctrl+X）
field.Align = TextFieldAlign.Center;           // 揃え（Left / Center。数値の欄は Center）
field.TextSize = TextFieldTokens.TextFieldNumber;   // 文字の大きさのトークン（既定 text.field 16・数値の欄は text.field_number 24）
field.Filled = false;                          // 塗りのある種類（既定は枠だけ = Material 3 の Outlined）
field.SelectAllOnFocus = false;                // フォーカスを得たら全選択
field.UnfocusOnDone = true;                    // 完了（Done）でフォーカスを外す（キーボードも隠れる）
field.AvoidKeyboard = true;                    // キーボードを避ける（祖先の縦の CanvasScroll の末尾の余白と送り・ダイアログの持ち上げ）
field.SetError(true);                          // エラーの見た目（枠とカーソルが color.error）
// 2026-10-02 の拡充
field.SetPadding(8f);                          // 欄ごとの左右の内側の余白（Padding。負 = テーマの size.field_padding〈既定〉・0 は余白なし。幅の狭い数値の欄）
field.SetAllowSelection(false);                // 選択を許さない（AllowSelection。Flutter の enableInteractiveSelection: false）: 長押しは全選択にせず
                                               // カーソルを置くだけ・SelectAllOnFocus と SelectAll() も選ばない・キーボードや IME の選択はカーソルへ畳む
                                               // 2026-10-03: フォーカスの場へコピー・切り取りの禁止も渡す（畳む前の Ctrl+A → Ctrl+C も写さない。次のフォーカスから）
// 欄は自分のレイアウトの大きさの変化を見て中身を置き直す（コンテナが幅を伸ばした・ダイアログが入力の枠の幅〈札の中の幅〉に合わせた）

string text = field.Text;                      // 本文（フォーカスの間はエンジンの本文に追従する）
field.SetText("30");                           // 本文を置く（フォーカスの間は入力中の本文も差し替える。notify: true で TextChanged も出す）
bool written = field.SetTextUnlessFocused("3");// フォーカスが無いときだけ置く（スライダなど外の値との双方向）
field.Focus(); field.Unfocus(); field.SelectAll();
bool f = field.IsFocused; bool c = field.IsComposing; TextInputState s = field.State;   // s.Text・s.SelectionStart/End・s.CompositionStart/End・s.Caret

field.TextChanged += (tf, t) => { };           // 本文が変わった（打鍵・変換中の文字の変化〈1 文字ごと〉・貼り付け・確定）
field.Submitted += (tf, action) => { };        // 完了などのアクション（Done・Next …）
field.FocusChanged += (tf, focused) => { };    // フォーカスを得た・失った（欄の外のタップ・戻る・完了・別の欄のタップ）
field.PasteBlocked += tf => { };               // 貼り付けを禁止した欄で貼り付けを止めた

// 数字の欄の値（Wake or Pay の数値の欄: 数字として読めれば範囲へ収める・読めなければ前の値のまま）
if (NumberText.TryParseClamped(field.Text, 1, 5, out long minutes)) slider.SetValue(minutes, notify: false);
field.SetText(NumberText.Format(minutes));     // フォーカスが外れた・完了したときに収めた値の文字へ直す

// ダイアログの 1 行の入力（名前の変更。Positive のときだけ InputText が入る。前後の空白を落とす）
var handle = Dialog.Show(new DialogOptions
{
    Title = "名前の変更", PositiveText = "変更", NegativeText = "やめる",
    Input = new DialogInputOptions { Text = current, Placeholder = "例：田中太郎", MaxLength = 20, TrimResult = true, SubmitOnDone = true },
});
handle!.Completed += r => { if (r == DialogResult.Positive && handle.InputText is { Length: > 0 } name) current = name; };

// 部品を使わずに直接（入力欄の場。同時に 1 つ）
int session = SEED.TextInput.Begin(new TextInputOptions { Kind = TextInputKind.Number, Action = TextInputAction.Done, MaxLength = 7,
                                                           AllowPaste = true, AllowCopy = true }, "1000");   // 選択は既定で末尾
foreach (var e in SEED.TextInput.TakeEvents(session)) { }   // e.Kind: TextChanged / SelectionChanged / Action（e.Action）/ PasteBlocked / KeyboardShown（e.KeyboardHeight）/ KeyboardHidden
SEED.TextInput.TryGetState(session, out var st);            // 本文・選択・変換中の区間・版
SEED.TextInput.SetText(session, "12", 2, 2); SEED.TextInput.SetSelection(session, 0, 2);
SEED.TextInput.ShowKeyboard(session); SEED.TextInput.HideKeyboard(session);
SEED.TextInput.SetCaretRect(session, rectInScreenPixels);   // PC の IME の候補窓が避ける矩形（画面の画素）
bool active = SEED.TextInput.IsActive(session); int now = SEED.TextInput.ActiveSession;
SEED.TextInput.End(session);                                // キーボードを隠し、PC の IME の許可を外す（変換中の文字は確定扱い）
float kb = SEED.TextInput.KeyboardHeight;                   // ソフトキーボードの高さ（画面の下端から・画面の画素・見えていなければ 0）
bool shown = SEED.TextInput.KeyboardVisible;

// 1 行の文字の寸法（描画と同じ送り幅。記法は解かない。W2-6c の Text.Measure の芽）
float w = SEED.TextMeasure.LineWidth("こんにちは", text.FontPath, text.FontSize);
float[] stops = SEED.TextMeasure.CaretOffsets("abc", text.FontPath, text.FontSize);   // 長さ = 文字数 + 1（添字 i の前の端の x）
var (ascent, descent) = SEED.TextMeasure.Metrics(text.FontPath, text.FontSize);

// スクロールの中身の末尾の余白（実行中だけ。入力欄がキーボードを避けるときに使う）
scroll.EndInset = new Vector2(0f, 300f);
```

| 操作 | 振る舞い |
|---|---|
| 欄のタップ | フォーカス（タップの位置へカーソル）。フォーカスの間のタップはカーソルを移し、閉じられていたキーボードを出し直す |
| 欄の長押し | すべてを選ぶ（選択を許さない欄〈AllowSelection = false〉はタップと同じ。2026-10-02） |
| 欄の外のタップ（押して動かさずに離す） | フォーカスを外す（キーボードが隠れる）。スクロールのドラッグでは外さない。別の欄・ボタンのタップはそちらが先に受ける。OS に取り消された指（`TouchPhase.Canceled`。Android の戻るのジェスチャーが端の指を奪ったときなど）はタップと数えない |
| 戻る（Android の戻る・PC の Esc） | 戻るの段（BackDispatcher）の Focus の層: 必ずフォーカスを外す。キーボードが出ていれば（PC は常に）受ける。Android でキーボードを閉じた後の戻る（1 回目は IME が閉じるので 2 回目）は、後ろに戻るを受ける層（ダイアログ・シート・覆い・画面のスタック）があればそこへ回し（ダイアログが閉じる）、無い根の画面では受ける（アプリは背面へ回らない。3 回目で背面へ） |
| PC のキー | 文字（IME の変換・確定を含む）・Backspace・Delete・←→（Shift で選択）・Home・End・Enter（アクション）・Ctrl+A・Ctrl+C/X/V（禁止の欄では止める）。変換中のキーは IME が受ける |
| Android | 文字・数字のキーボード（EditorInfo）、変換中の文字と確定、完了のボタン（onEditorAction）、キーボードの表示と高さ（WindowInsets）。数字の欄でも IME の側でかなへ切り替えられるので、エンジンが数字以外を捨てる |

> **重要**: 入力欄にフォーカスがある間も、キーの状態（`Input.GetKey` など）は従来どおり届く（入力欄が受けたキーもゲームの入力に残る）。ゲームのショートカットを
> 打鍵で動かしたくない画面は `SEED.TextInput.ActiveSession != 0` の間は止める。エディタのショートカット（Play 中の Ctrl+Z など）は入力欄が受けたキーでは動かない。

## 7.21 Localization（多言語：文字列の表・言語の切り替え・UI への結び付け。SEED.Localization）

ゲーム・アプリの文字列を言語ごとに差し替える。データは `assets://locale/index.json`（言語の一覧）と `assets://locale/<言語>.json`（キー → 文。
入れ子は `.` でつないだキー）。見本は `templates/locale`（テンプレートの取り込みで `assets/locale` へ入る）。正典は `docs/localization.md`。
起動の言語は 保存した値（SaveData の `l10n.language`）→ 端末の言語 → index の `default` の順で決まる。

```csharp
using SEED.Localization;

// 文を引く（無いキーは欠けの方針どおり: 開発中 "[key]"・配布用 "key"。キーごとに 1 回だけ警告）
string s = L10n.Get("menu.start");                                   // 今の言語 → fallback → 既定の言語 の順に探す
string g = L10n.Get("greeting", ("name", playerName));               // {name} の差し込み（params (string name, object? value)[]）
string f = L10n.Format("score", 1200);                               // {0} の差し込み（params object?[]。名前つきの組は Get へ渡す）
string m = L10n.Get("money", ("amount", 1234567));                   // 表の "{amount:N0}" → 今の言語の文化で "1,234,567"
string c = L10n.Plural("coins", n);                                  // 複数形（0 は zero を先に → 言語の規則の形 → other → 普通のキー。数は {n}）
string c2 = L10n.Plural("coins", n, ("who", name));                  // ほかの差し込みも渡せる（("n", …) を渡すとそちらが勝つ）
bool has = L10n.Has("menu.start");                                   // あるか（警告しない）
bool ok = L10n.TryGet("menu.start", out string text);                // 無ければ false（警告しない）

// 言語
string lang = L10n.Language;                                         // 今の言語のコード（"ja"。言語の一覧が無ければ空）
LocaleLanguage? cur = L10n.CurrentLanguage;                          // 今の言語（Code・Name・Fallback・CultureName）
IReadOnlyList<LocaleLanguage> all = L10n.Languages;                  // 言語の一覧（index.json の順。Name は言語を選ぶ画面に出す名前）
string def = L10n.DefaultLanguage;                                   // 既定の言語（index の default）
string? sys = L10n.SystemLanguage;                                   // 端末の言語（"ja-JP"。Android〈Invariant〉・取れない環境では null）
bool known = L10n.SetLanguage("en");                                 // 切り替え（表を読み替えて Changed・SaveData の "l10n.language" へ保存。一覧に無ければ false）
L10n.SetLanguage("en", save: false);                                 // この実行の間だけ切り替える
L10n.FollowSystemLanguage();                                         // 保存を消して端末の言語（当たらなければ既定の言語）へ
bool follows = L10n.IsFollowingSystem;                               // 保存していない（端末に従っている）か
IReadOnlyList<string> chain = L10n.FallbackChain;                   // 探す順（例 ["en", "ja"]）

// 知らせ（SEED.Events のイベント名 "l10n.changed"。引数は今の言語のコードの string）
this.On(L10n.Changed, (string code) => Refresh());

// 置き場・読み直し・欠け
L10n.Configure("assets://mygame/locale");                            // 置き場（既定 L10n.DefaultRoot = "assets://locale"。読み込み済みなら読み直して Changed）
bool reloaded = L10n.PollChanges();                                  // データファイルが書き換わっていたら読み直して Changed（既定では誰も呼ばない）
L10n.Reload();                                                       // index.json と表を読み直して Changed（今の言語を保つ）
L10n.MissingPolicy = MissingKeyPolicy.Key;                           // 欠けの方針（Key = "key"・Marked = "[key]"・Empty = ""。既定は IsDebugAllowed なら Marked、そうでなければ Key）
IReadOnlyCollection<string> missing = L10n.MissingKeys;             // 欠けていたキー（翻訳の漏れの確かめ。言語の切り替え・読み直しで数え直す）

// 数・日付・時刻（今の言語の文化。Android〈Invariant〉では不変文化 → 言語ごとの書式は表に書いて渡す）
string num = L10n.FormatNumber(1234.5, 1);                           // "1,234.5"（en-US）/ "1.234,5"（de-DE）。小数の桁 0〜15（既定 0）
string day = L10n.FormatDate(date, L10n.Get("format.date"));         // .NET の日付の書式（既定 "d"）
string time = L10n.FormatTime(new TimeOnly(7, 5), "H:mm");           // .NET の時刻の書式（既定 "t"）
System.Globalization.CultureInfo culture = L10n.Culture;             // 今の言語の文化（index の culture。Invariant では Name が空）
```

```csharp
// UI への結び付け（アクタに ScriptComponent を付ける。型名は完全名で書く）
//   SEED.Localization.LocalizedText        … 同じアクタの Text へ「キー」の文字を入れる（言語が替わると入れ直す）
//   SEED.Localization.LocalizedLabel       … 部品の文字へ当てる。判定は Button → 選択のグループ（SegmentedControl・ChipGroup・RadioGroup。
//                                            項目ごとに「キー.番号」）→ Toggle・Checkbox（子 Label の Text）→ アクタ自身の Text の順
//   SEED.Localization.LocalizationReloader … 開発中（IsDebugAllowed）だけ IntervalSeconds ごとに L10n.PollChanges()
public string Key;                          // LocalizedText・LocalizedLabel の欄: 言語の表のキー（空なら文字を変えない）
public List<LocalizedArg> Args;             // 同: 差し込み（LocalizedArg { Name, Value }。値は文字列のまま）
public float IntervalSeconds;               // LocalizationReloader の欄: 調べる間隔（秒・実時間・0.25〜60・既定 1）

// スクリプトから（相手の OnStart の前は null）
LocalizedText? label = LocalizedBinding.Of<LocalizedText>(actor);   // LocalizedLabel も同じ
label.SetArg("amount", 1200);               // 実行中の差し込み（同じ名前はインスペクタの差し込みより勝つ。数は文化で書く）
label.ClearArgs();                          // 実行中の差し込みを外す
label.SetCount(3);                          // 複数形として引く（ClearCount() で戻す）
label.SetKey("hud.money");                  // キーを変えて当て直す
label.Apply();                              // 当て直す
ILocalizedTarget? target = label.Target;    // 今の当てる先（KindName・IsAlive・Apply(LocalizedRequest)）

// 当てる先を直接使う（ILocalizedTarget: TextLabelTarget・ButtonLabelTarget・ChildLabelTarget<T>（Toggle・Checkbox の子 Label）・SelectionLabelTarget）
new TextLabelTarget(text).Apply(new LocalizedRequest("menu.start"));
new ButtonLabelTarget(button).Apply(new LocalizedRequest("ui.dialog.ok"));
string r1 = new LocalizedRequest("coins", args, count: 3).Resolve();   // 今の言語の文（count があれば複数形）
string r2 = new LocalizedRequest("theme_mode").ResolveChild("0");      // "theme_mode.0" の文
ILocalizedTarget? found = LocalizedTargetTable.Find(actor);           // 判定の表で当てる先を探す
```

| データ（`<言語>.json`） | 読み方 |
|---|---|
| `"menu": { "start": "…" }` | 入れ子は `.` でつないだキー（`menu.start`） |
| `"_about": "…"` | `_` で始まる鍵は説明（読まない） |
| `"coins": { "one": "{n} coin", "other": "{n} coins" }` | 子がすべて `zero`・`one`・`two`・`few`・`many`・`other` なら複数形のまとまり |
| `"days": ["日", "月"]` | 配列は番号のキー（`days.0`） |
| `"todo": null` | 訳していない（次の言語を探す） |
| `{name}`・`{0}`・`{name:N0}`・`{{` `}}` | 差し込み・順・書式・波かっこそのもの。**渡していない `{…}` はそのまま残す**（Text の `{num}`・`{color}` などの記法と共存） |

| 複数形の規則 | 言語 |
|---|---|
| other だけ | ja・zh・ko・th・vi・id など |
| 1 = one | en・de・es・it・nl・pt-PT など（表に無い言語の既定） |
| 0 と 1 = one | fr・pt（ブラジル）・hi など |
| one / few / many | ru・uk・be・pl |
| one / few / other | cs・sk |
| zero / one / two / few / many / other | ar |

> **重要**: `L10n.Changed` は今の言語のコードの **string で発火**する。購読は `this.On(L10n.Changed, (string code) => …)`。引数なしの `() => …` では呼ばれない（SEED.Events は引数の型が合う購読だけを呼ぶ）。

> **重要**（Android）: Android の CoreCLR は Invariant（`runtime/android/dotnet_runtime.json` の `System.Globalization.Invariant=true`）なので、`L10n.SystemLanguage` は null（起動の言語は 保存した値 → 既定の言語）、数・日付の書式は不変文化（月・曜日の名前は英語）。言語ごとの書式（`"format.date": "M月d日"`）と曜日の名前は表に書いて引く。

> **重要**: Edit（Play していないとき）はスクリプトが動かないので、LocalizedText のアクタはプレハブの文字のまま見える。SEED.UI の部品の既定の文字列（`DialogOptions.DefaultPositiveText` の "OK"・`TimeWheel` の午前/午後・`ChartView.EmptyText`）は L10n を読まない。`Dialog.Show` などへは `L10n.Get` で引いて渡す（対応表は `docs/localization.md` §10）。

---

## 7.22 Binding（観測できる値と UI 部品への結び付け。SEED.Binding）

状態を観測値（`Observable<T>`・`Computed`・`ObservableList<T>`）で持ち、UI は `Bind.*` で 1 行ずつ結ぶ。作った時点の値ですぐ当て、
値が変わるたびに当て直す（画面のコードに「いつ UI を直すか」を書かない）。トグル・チェックボックス・スライダ・文字の欄・選択は双方向。
`owner`（ふつうは `this`）を渡すと、そのスクリプトの破棄で自動で外れる（`this.On` と同じ仕組み）。正典は `docs/ui_binding.md`、
見本は `templates/ui/scripts/UiBindingDemo.cs`。

```csharp
using SEED.Binding;

// 観測できる値（set は等しければ何もしない。違えばその場で購読と結び付けへ知らせる＝即時・同期）
var count = new Observable<int>(0);                                  // new Observable<T>(initial, comparer = null)
count.Value++;                                                       // 変わったら即座に知らせる
IDisposable sub = count.Subscribe(n => Debug.Log($"{n}"));           // 変わったときだけ呼ぶ（購読した時点では呼ばない）。Dispose で解除
count.Subscribe(this, n => Debug.Log($"{n}"));                       // スクリプトの破棄で自動で外れる（BindingOwner の拡張）
count.Notify();                                                      // 値は同じままで知らせる（参照型の中身を書き換えたとき）
int subscribers = count.SubscriberCount;                             // 今の購読の数（結び付けを含む）
IReadOnlyObservable<int> readOnly = count;                           // 読むだけの口（Value・Subscribe）。Computed・Bind.Deferred も同じ

// 導いた値（依存 1〜3 個。結果が前と違うときだけ知らせる。購読がある間だけ依存を購読する＝結び付けへ渡して捨ててよい）
Computed<int> total = Computed.From(price, qty, (p, n) => p * n);   // Computed.From(a, f) / From(a, b, f) / From(a, b, c, f)
Computed<string> text = Computed.From(total, t => $"{t:N0} 円");
bool connected = total.IsConnected;                                  // 依存を購読しているか（購読があるか）

// 観測できる一覧（変化の種類と位置を起きた順に 1 件ずつ知らせる）
var items = new ObservableList<string>();                            // new ObservableList<T>(IEnumerable<T> items) で写して作る
items.Add("a"); items.Insert(0, "z"); items.RemoveAt(1); items.Remove("z");
items[0] = "b";                                                      // Replace（同じ項目でも知らせる＝行の描き直しの合図）
items.Move(2, 0);                                                    // Move（同じ位置なら知らせない）
items.Clear(); items.ReplaceAll(new[] { "x", "y" });                 // Reset（空の Clear は知らせない）
int n = items.Count; int i = items.IndexOf("x"); bool has = items.Contains("y");
items.Subscribe(this, (ListChange<string> c) => Debug.Log($"{c.Kind} {c.Index} {c.OldIndex} {c.Item} {c.OldItem}"));
// ListChangeKind: Insert・Remove・Replace・Move・Reset（Reset の Index は ListChange<T>.NoIndex = -1。今の一覧を読み直す）
```

```csharp
// 結び付け（どれも IDisposable を返す。先頭に owner を渡す多重定義はスクリプトの破棄で自動で外れる）
// 一方向（値 → UI）。source は IReadOnlyObservable<T>（Observable・Computed・Bind.Deferred）
IDisposable b1 = Bind.Text(this, label, title);                      // Text.Content ← string
Bind.Text(this, label, count, n => $"{n} 回");                       // 書式
Bind.Text(this, label, money, "hud.money", "amount");                // L10n.Get("hud.money", ("amount", 値))。L10n.Changed でも引き直す
Bind.Visible(this, badge, hasNew);                                   // GameObject.Visible ← bool（子孫ごと）
Bind.Visible(this, badge, count, n => n > 0);                        // 判定つき
Bind.Color(this, sprite, color);                                     // Sprite.Color ← Color
Bind.Color(this, sprite, status, s => s == 0 ? okColor : ngColor);   // 変換つき
Bind.To(this, count, n => button.SetText($"{n}"));                   // 任意の処理（作った時点で 1 回・変わるたびに）

// 双方向（値 ⇔ 部品）。部品そのもの（UiWidget.Of<T> の後）か、部品の付くノード（GameObject。部品の OnStart を待つ）を渡す
Bind.Toggle(this, toggleOrNode, notifyOn);                           // Observable<bool>。SetOn(v, animate, notify: false) ⇔ Toggle.Changed
Bind.Checkbox(this, checkboxOrNode, agreed);                         // Observable<bool>。SetChecked ⇔ Checkbox.Changed
Bind.Slider(this, sliderOrNode, volume);                             // Observable<float>。SetValue（範囲・段階へ寄せた見た目）⇔ ValueChanged
Bind.TextField(this, fieldOrNode, name);                             // Observable<string>。SetTextUnlessFocused ⇔ TextChanged（1 文字ごと）
Bind.Selection(this, groupOrNode, themeIndex);                       // Observable<int>（−1 = 選ばない）。SegmentedControl・RadioGroup・ChipGroup

// 一覧（ObservableList → SEED.UI.ListView。ListView 自身は変えない）
var list = new ListView(scroll, rowPrefab, 0, rowExtent, Bind.RowBinder(items, BindRow));   // 作るときの bind は一覧から
Bind.List(this, list, items, BindRow);                               // Insert・Remove・Reset → SetCount、Move → Refresh、Replace → 見えている行へ BindRow
void BindRow(GameObject row, string item, int index) { }             // Action<GameObject, T, int>。ListView.Update は今までどおり毎フレーム

// まとめる（任意。1 フレームに何度変わっても、フレームの区切り〈LateUpdate の頭〉で最新の値を 1 回だけ知らせる）
Bind.Text(this, label, Bind.Deferred(score), s => $"{s:N0}");        // IReadOnlyObservable<T> Bind.Deferred(source)

// 自作の当てる先（部品の代わりに挟む口。テストは偽物を差す）
Bind.OneWay(this, (IBindTarget<T>)target, source);                   // IBindTarget<T>: IsAlive・IsReady・Write(T)
Bind.OneWay(this, target, source, convert);                          // 変換つき
Bind.TwoWay(this, (ITwoWayBindTarget<T>)widget, observable);         // ＋ Listen(Action<T>) → IDisposable
Bind.List(this, (IListBindTarget)rows, items);                       // IsAlive・SetCount(int)・Refresh()・RebindRow(int)
IDisposable off = new DisposableAction(() => widget.Changed -= h);   // Dispose で 1 回だけ呼ぶ口（DisposableAction.Empty は何もしない）

// 寿命
someDisposable.AddTo(this);                                          // 任意の IDisposable をスクリプトの破棄で外す（UniRx の AddTo）
var bag = new DisposableBag(); bag.Add(binding); bag.Dispose();      // まとめて外す袋（Dispose の後に Add した物はその場で外す）
BindingFrame.Tick();                                                 // フレームの区切り（エンジンが LateUpdate の頭で呼ぶ。普段は呼ばない）
int pending = BindingFrame.PendingCount;                             // 区切りを待っている仕事の数（診断用）
const int depth = BindingLimits.MaxReentrantDepth;                   // 再入（購読の中で値を変える）を許す段 = 1
```

| 規則 | 内容 |
|---|---|
| 即時 | 値の変更はその場で購読と結び付けへ届く。UI 部品への書き込みは部品の規則（`Visible` はフレームの末尾・`ListView` は次の `Update`） |
| 観測値が正 | 双方向は作った時点で観測値の値を部品へ当てる。部品へ書いている間の部品の知らせ・部品から入れた値の知らせは往復しない（留め金） |
| 部品を待つ | 部品のスクリプトの OnStart の前（Instantiate したプレハブは次のフレーム）・選択の項目が集まる前は書かずに待ち、区切りで最新の値を当てる |
| 消えたら外れる | Text・Sprite・アクタ・部品（登録簿から外れた）が消えたら、次の書き込み・区切りで自分を外す |
| 再入 | 購読の中の変更は今の知らせを配り終えてから最新の値でもう 1 周（一覧は起きた順）。1 段を超えたら値は入るが知らせず警告 1 回 |
| 区切り | `BindingFrame.Tick` はフレームに 1 回、LateUpdate のフェーズの頭（Update の後・描画の前）。区切りの途中で積まれた仕事は次の区切り |

> **重要**: 画面のスクリプトに `Bind` という名前のメソッドがあると、その中の `Bind.Text(…)` はメソッドを指してコンパイルできない（CS0119）。`SEED.Binding.Bind.Text(…)` と書くか、`using BindTo = SEED.Binding.Bind;` のように別名を付ける。

> **重要**: `Bind.Selection` は 1 つ選ぶグループ用（値は `SelectedIndex`）。複数を選ぶ `ChipGroup`（`Multiple = true`）には向かない。`Bind.Slider` の観測値は寄せる前の値のまま（部品の見た目は範囲・段階へ寄せた値。利用者が動かすと寄せた値が入る）。`Bind.TextField` は打っている最中の欄を外の値で上書きしない。

> **重要**: owner なしで作った結び付けは、当てる先が消えても値が変わるまで観測値に残る。画面のスクリプトからは `owner: this` を渡す。観測値はスクリプトのフェーズ（メインスレッド）だけから使う。

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
