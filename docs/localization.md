# 多言語（ローカライズ）— `SEED.Localization`

ゲーム・アプリの文字列を言語ごとに差し替える仕組みの正典（2026-10-02 作成）。
スクリプト API の一覧は [scripting_api.md](scripting_api.md) §7.21、残件は [backlog.md](backlog.md)「ローカライズ（SEED.Localization）」。

手本は Unity Localization と Flutter の intl だが、SEED の流儀に合わせて小さく作ってある:

| SEED の流儀 | この仕組みでの形 |
|---|---|
| データは `assets://` の JSON（差し替えるだけで変わる） | `assets://locale/index.json`（言語の一覧）と `assets://locale/<言語>.json`（キー → 文） |
| スクリプトは `SEEDScript`・通知は `SEED.Events` | 静的な窓口 `L10n`、切り替えの知らせ `L10n.Changed` を `this.On` で受ける |
| UI 部品は `SEED.UI`（部品のスクリプトはプレハブに付ける） | `LocalizedText`（Text）・`LocalizedLabel`（Button・Toggle・Checkbox・選択のグループ・Text）を付ける。部品には手を入れない |
| 設定は `SaveData` | 選んだ言語は SaveData のキー `l10n.language` |

## 0. ファイルの構成

| ファイル | 役割 |
|---|---|
| `scripting/src/Api/Localization/L10n.cs` | 静的な窓口（Get・Format・Plural・SetLanguage・Changed・書式）。中身は LocaleCatalog |
| `scripting/src/Api/Localization/Model/LocaleCatalog.cs` | 本体（言語の一覧・表・今の言語・探す順・欠けの方針・保存・書き換えの検知）。純粋な計算 |
| `…/Model/LocaleIndex.cs`・`LocaleLanguage.cs` | index.json の読み込み・言語の引き当て（Match）・探す順（BuildChain） |
| `…/Model/LocaleTable.cs`・`LocaleJson.cs` | 言語の表の読み込み（平たん化・複数形のまとまり）と JSON の共通の約束 |
| `…/Model/LocaleFormatter.cs` | 差し込み（`{name}`・`{0}`・`{name:書式}`・`{{ }}`） |
| `…/Model/PluralRules.cs`・`PluralRuleKind.cs`・`PluralCategory.cs` | 複数形の規則の表と形の種類 |
| `…/Model/LocaleCulture.cs` | 文化の引き当て・端末の言語・数と日付の書式（Invariant の環境では不変文化） |
| `…/Model/MissingKeyPolicy.cs`・`MissingKeyText.cs` | 欠けたキーの方針 |
| `…/Model/ILocaleSource.cs`・`ILocaleStore.cs`・`LocalePaths.cs`・`LocaleSwitchResult.cs` | 読み込み元・保存先（差し替えの口）・置き場・切り替えの結果 |
| `…/AssetLocaleSource.cs`・`SaveDataLocaleStore.cs` | 実行中の読み込み元（`SEED.Assets`）と保存先（`SEED.SaveData`） |
| `…/LocalizedBinding.cs`・`LocalizedText.cs`・`LocalizedLabel.cs`・`LocalizedArg.cs`・`LocalizedRegistry.cs` | UI への結び付けのスクリプト |
| `…/ILocalizedTarget.cs`・`LocalizedRequest.cs`・`Targets/*.cs` | 当てる先（部品の外から文字を当てる小さなクラス）と判定の表 `LocalizedTargetTable` |
| `…/LocalizationReloader.cs` | 開発中だけデータファイルの書き換えを拾うスクリプト |
| `templates/locale/index.json`・`ja.json`・`en.json` | 見本の表（SEED.UI の固定文字列・よく使う言葉・書式・書き方の見本） |
| `editor/tests/LocalizationTests/` | 単体テスト（純粋な部分・Invariant の環境・見本の表・テンプレートの取り込み） |

`Model/` の型はエンジンの API（Assets・SaveData・Debug）を使わない。テストはこのフォルダをリンクで取り込み、読み込み元と保存先を
辞書へ差し替えて検査する（エンジンの API を使い始めるとテストがビルドできなくなる＝検知器）。

## 1. はじめかた

1. エディタのテンプレートの取り込み（`templates/`）で **`locale` のカテゴリ**（`index.json`・`ja.json`・`en.json`）を選んで取り込む。
   プロジェクトの `assets/locale/` に入る（§10）。
2. 文字を出すアクタに ScriptComponent（型名 `SEED.Localization.LocalizedText`）を付け、インスペクタの「キー」に `ui.dialog.cancel` のように書く。
   部品（Button など）の文字は `SEED.Localization.LocalizedLabel`。スクリプトからは `L10n.Get("ui.dialog.cancel")`。
3. 言語を選ぶ画面で `L10n.SetLanguage("en")`。表示中の `LocalizedText`・`LocalizedLabel` はその場で入れ替わり、次の起動も英語で始まる。

```csharp
using SEED.Localization;

string title = L10n.Get("menu.start");                         // 今の言語の文
string hello = L10n.Get("greeting", ("name", playerName));     // {name} の差し込み
string coins = L10n.Plural("coins", coinCount);                // 複数形（数は {n}）
L10n.SetLanguage("en");                                        // 切り替え（SaveData の l10n.language へ保存）
this.On(L10n.Changed, (string code) => RebuildTexts());        // 切り替え・読み直しの知らせ（スクリプトの寿命に追従）
```

> Edit（Play していないとき）はスクリプトが動かないので、`LocalizedText` を付けたアクタはプレハブ・シーンに書いた文字のまま見える。
> プレハブの Text には既定の言語の文を書いておくと、Edit でも Play でも同じ見た目になる。

## 2. データの形

### 2.1 言語の一覧（`index.json`）

```json
{
  "_about": "説明（読まない）",
  "default": "ja",
  "languages": [
    { "code": "ja", "name": "日本語", "fallback": null, "culture": "ja-JP" },
    { "code": "en", "name": "English", "fallback": "ja", "culture": "en-US" }
  ]
}
```

| 鍵 | 意味 | 省略したとき |
|---|---|---|
| `default` | 既定の言語。どの言語の表にも無いキーの最後の行き先・端末の言語が当たらないときの言語 | 先頭の言語（一覧に無い言語なら警告して先頭） |
| `languages[].code` | 言語のコード。表のファイル名（`<code>.json`）・保存・切り替えに使う。`"pt-BR"` のように地域つきも可（`_` は `-` に書きそろえる） | 必須（無ければ警告して飛ばす） |
| `languages[].name` | 言語の名前（その言語で書く。言語を選ぶ画面にそのまま出す） | コード |
| `languages[].fallback` | その言語の表に無いキーを次に探す言語 | `null` = 既定の言語へ直接落ちる（一覧に無い言語なら警告して同じ扱い） |
| `languages[].culture` | 数・日付の書式に使う文化の名前 | コード（Invariant の環境では使われない。§9） |

### 2.2 言語の表（`<code>.json`）

```json
{
  "_about": "説明（読まない）",
  "menu": { "start": "はじめる", "quit": "おわる" },
  "greeting": "こんにちは、{name} さん",
  "coins": { "one": "{n} coin", "other": "{n} coins" },
  "weekday_short": ["日", "月", "火", "水", "木", "金", "土"],
  "todo": null
}
```

| 書き方 | 読み方 |
|---|---|
| 入れ子のオブジェクト | `.` でつないだ 1 本のキーに平たくする（`menu.start`） |
| 先頭が `_` の鍵 | 説明（読まない。`_about`・`_source` など。Wake or Pay の strings.ja.json と同じ約束） |
| 子の鍵がすべて `zero` `one` `two` `few` `many` `other` で値が文字列か null のオブジェクト | **複数形のまとまり**（§5）。子は普通のキー（`coins.one`）としても引ける。まとまりの名前（`coins`）を `Get` で引くと `other` の形 |
| 配列 | 番号のキー（`weekday_short.0`〜`weekday_short.6`） |
| 数・真偽値 | 書いたままの文字列（`10` → `"10"`・`true` → `"true"`） |
| `null` | 訳していない（無いのと同じ。次の言語を探す） |
| 空の鍵・重なったキー（`"a.b"` と `{"a":{"b"}}`） | 警告。重なりは後のものを使う |
| コメント（`//` `/* */`）・末尾のカンマ | 許す（手で書くファイルのため。SEED.UI のテーマの JSON と同じ） |

壊れた JSON・最上位がオブジェクトでないファイルは警告して空の表として扱う（例外にしない。その言語のキーは次の言語から探す）。

### 2.3 置き場

既定は `assets://locale`（`L10n.DefaultRoot`）。`L10n.Configure("assets://mygame/locale")` で変えられる（起動のスクリプトの `OnStart` で、
ほかのスクリプトが文を引く前に呼ぶ。読み込み済みなら読み直して `Changed` を知らせる）。表は `<置き場>/<code>.json`。
読み込みは `SEED.Assets.TryReadText`（PAK 同梱・実ファイルのどちらでも同じパス。scripting_api.md §7.75）。

## 3. 言語の決まり方

**起動のとき**（最初に `L10n` が使われたとき 1 回）: ① 保存した値（SaveData の `l10n.language`）→ ② 端末の言語（`L10n.SystemLanguage`）→
③ `index.json` の `default`。それぞれ一覧の言語へ当て、当たらなければ次へ（保存した値が一覧に無ければ警告。保存した値は消さない）。

**当て方（`LocaleIndex.Match`）**: そのまま（大文字小文字・`_` と `-` は区別しない）→ 後ろの区切りを落としていく（`zh-Hant-TW` → `zh-Hant` → `zh`）→
同じ言語の別の地域（`en-GB` → 一覧の `en-US`。一覧の先のもの）→ 当たらなければ null。
`zh-CN` → `zh-Hans` のような書記体系の対応はしない（一覧に `zh-CN` か `zh` を書く）。

**探す順**: 今の言語 → その `fallback` → その `fallback` … → 既定の言語（同じ言語は 2 度入れない＝輪でも止まる）。`L10n.FallbackChain` で見られる。

**切り替え**: `L10n.SetLanguage(code, save: true)` は一覧の言語へ当て（無ければ警告して false）、探す順の表を読み替え、`Changed` を知らせる。
`save` なら SaveData の `l10n.language` へ書いて `SaveData.Save()` で書き出す（同じ値なら書かない）。`save: false` はこの実行の間だけ。
`L10n.FollowSystemLanguage()` は保存を消して端末の言語（当たらなければ既定）へ戻す（言語を選ぶ画面の「端末に合わせる」。`L10n.IsFollowingSystem`）。

## 4. 差し込み

| 書き方 | 意味 |
|---|---|
| `{name}` | 渡した名前の値（大文字小文字を区別する）。`L10n.Get(key, ("name", value))` |
| `{0}` `{1}` | 渡した順の値（`L10n.Format(key, a, b)`。名前つきで渡した値も順で引ける） |
| `{name:N0}` `{0:0.0}` | 値が数・日付（`IFormattable`）なら `:` の後ろを .NET の書式として今の言語の文化で書く（読めない書式は書式なし） |
| `{{` `}}` | 波かっこそのもの（Wake or Pay の StringTable と同じ） |
| それ以外の `{…}` | **そのまま残す**。Text の差し込みスロットの記法（`{num}`・`{num.3}`・`{string}`・`{image}`・`{color}`〜`{/color}`）を表の文に書けば、Text がそのまま解く。名前の無い `{}`・渡していない名前・閉じていない `{` も残す |

同じ名前を 2 つ渡すと先のものを使う。`null` の値は空文字。差し込みの名前に Text の記法の名前（`num`・`string`・`image`・`color`）を使わないこと
（Text の記法を L10n が先に置き換えてしまう）。

## 5. 複数形

`L10n.Plural(key, n, args…)` は今の言語の規則で形（`zero` `one` `two` `few` `many` `other`）を選び、`{n}` に数を入れる。引き方の順:
① `n = 0` ならどの言語でも `key.zero`（「アイテムはありません」を書けるように。Flutter の intl と同じ）→ ② 規則の形 `key.<形>` → ③ `key.other` →
④ 普通のキー `key`（日本語のように形の変わらない言語は `"coins": "コイン {n} 枚"` と 1 文で書いてよい）→ ⑤ 無ければ探す順の次の言語で、
**その言語の規則**で ①〜④ → どこにも無ければ欠けの方針（§6）。`args` に `("n", …)` を渡すとそちらが勝つ。

| 規則（`PluralRuleKind`） | 言語（`PluralRules` の表） | 整数の形 |
|---|---|---|
| `OtherOnly` | ja・zh・ko・yue・th・vi・id・ms・lo・my・km | other だけ |
| `OneOther`（表に無い言語の既定） | en・de・nl・sv・da・nb・it・es・el・tr・hu・**pt-PT** など | 1 = one・ほか other |
| `ZeroOneAsOne` | fr・pt（ブラジル）・hi・bn・fa・gu・kn・zu・am・hy | 0 と 1 = one・ほか other |
| `OneTwoOther` | he（iw） | 1 = one・2 = two・ほか other |
| `EastSlavic` | ru・uk・be | 末尾 1（11 を除く）= one・末尾 2〜4（12〜14 を除く）= few・ほか many |
| `Polish` | pl | 1 = one・末尾 2〜4（12〜14 を除く）= few・ほか many（21 は many） |
| `CzechSlovak` | cs・sk | 1 = one・2〜4 = few・ほか other |
| `Arabic` | ar | 0 = zero・1 = one・2 = two・下 2 桁 3〜10 = few・11〜99 = many・ほか other |

表はコードそのもの（`pt-PT`）→ 言語の部分（`pt`）の順に引く。負の数は絶対値で判定する。**整数だけ**を扱い、CLDR の小数の形と
「百万の many」（fr・es・it・pt の 1 000 000）は扱わない（無い形は other に落ちるので、書かなければ困らない）。規則を足すときは表に 1 行足す。

## 6. 欠けたキー

探す順のどこにも無いキーを `Get`・`Format`・`Plural` で引くと、`L10n.MissingPolicy`（`MissingKeyPolicy`）どおりの文を返す:

| 方針 | 返す文 | 既定 |
|---|---|---|
| `Marked` | `[menu.start]` | `Application.IsDebugAllowed` のとき（エディタ・開発用のビルド。欠けが一目で分かる） |
| `Key` | `menu.start` | 配布用のビルド（画面が空にならない） |
| `Empty` | 空文字 | — |

欠けは**キーごとに 1 回だけ** `Debug.LogWarning`（`[SEED.Localization] キー「…」がどの言語の表にもありません（探した順: en → ja）`）。
言語を切り替える・読み直すと数え直す。欠けたキーは `L10n.MissingKeys` で一覧にできる（翻訳の漏れの確かめ・テスト）。
`L10n.Has`・`L10n.TryGet` は警告しない（欠けに数えない）。

## 7. UI への結び付け

### 7.1 `LocalizedText`（同じアクタの Text）

文字のアクタに ScriptComponent（型名 **`SEED.Localization.LocalizedText`**）を付ける。

| 欄 | 意味 |
|---|---|
| キー（`Key`） | 言語の表のキー。空なら文字を変えない |
| 差し込み（`Args`） | 名前と値の組のリスト（`[System.Serializable]` の構造体 `LocalizedArg` のリスト。値は文字列のまま差し込む） |

Play で今の言語の文字を `Text.Content` へ入れ、`L10n.Changed` を `this.On` で購読して言語が替わったら入れ直す（スクリプトの破棄で自動で外れる）。
アクタに Text が無ければ 1 度だけ警告する。文字を変えたら `SEED.Redraw.Request()`（`render_policy: on_demand` でも描き直す）。

### 7.2 `LocalizedLabel`（部品の文字）と当てる先の表

部品のアクタ（Button なら `SEED.UI.Button` が付いたアクタ）に ScriptComponent（型名 **`SEED.Localization.LocalizedLabel`**）を付ける。
欄は LocalizedText と同じ。**部品には手を入れず**、部品の外の小さな当てる先（`ILocalizedTarget`）が公開の API で文字を当てる。
当てる先は 1 か所の表 `LocalizedTargetTable` の上から決める:

| 順 | 種類 | 当て方 |
|---|---|---|
| 1 | `SEED.UI.Button` | `Button.SetText`（子の Label の Text と `Button.Text` の欄） |
| 2 | 選択のグループ（`SegmentedControl`・`ChipGroup`・`RadioGroup`） | 項目（直下の `SelectItem`）ごとに子 Label の Text へ **`キー.番号`**（番号は `SelectItem.Index`）。表では配列で書ける: `"theme_mode": ["端末に合わせる", "明るい", "暗い"]` |
| 3 | `SEED.UI.Toggle` | 子 **Label** の Text（templates/ui の toggle.actor には無いので、足すか別の Text に LocalizedText） |
| 4 | `SEED.UI.Checkbox` | 同上 |
| 5 | アクタ自身の Text | `Text.Content` |

部品は `OnStart` で登録簿（`UiRegistry`）に載るまで引けない（スクリプトの `OnStart` の順は決まっていない）ので、LocalizedLabel は登録簿が変わるたびに
当てる先を探し直し、種類が替われば当て直す（選択のグループは項目が増えるので毎回当て直す）。30 フレーム（`LocalizedLabel.MissingTargetGraceFrames`）
待っても当てる先が無ければ 1 度だけ警告する。当てる文字の欄が無い（Toggle に子 Label が無いなど）ときも 1 度だけ警告する。
種類を足すときは `Targets/` に当てる先のクラスを足し、表に 1 行足す。

### 7.3 スクリプトから

```csharp
using SEED.Localization;

// 自分で文字を作る（切り替えに追従するなら L10n.Changed を受ける。引数は今の言語のコード）
public override void OnStart()
{
    this.On(L10n.Changed, (string code) => Refresh());   // 引数なしの () => … では届かない（SEED.Events は引数の型が合う購読だけを呼ぶ）
    Refresh();
}
void Refresh() => label.Content = L10n.Get("hud.money", ("amount", money));

// シーンの LocalizedText へ実行中の値を渡す（相手の OnStart の前は null）
if (LocalizedBinding.Of<LocalizedText>(moneyLabelActor) is { } money)
{
    money.SetArg("amount", 1200);    // 同じ名前はインスペクタの差し込みより勝つ。数は今の言語の文化で書く（{amount:N0}）
    money.SetCount(3);               // 複数形として引く（ClearCount で戻す）
    money.SetKey("hud.money_short"); // キーを変える
}

// 当てる先を直接使う（スクリプトで作った Text など）
new TextLabelTarget(text).Apply(new LocalizedRequest("menu.start"));
```

### 7.4 SEED.UI の部品の文字（コードで渡すもの）

ダイアログ・トースト・時刻ホイールの午前/午後のように、部品の API へ文字を渡すものは `L10n.Get` で引いて渡す
（ダイアログのボタンに LocalizedLabel を付けても、`Dialog.Show` が渡された文字で上書きする）。

```csharp
var handle = Dialog.Show(new DialogOptions
{
    Title = L10n.Get("alarm.delete.title"),
    PositiveText = L10n.Get("ui.dialog.delete"), PositiveKind = DialogButtonKind.Danger,
    NegativeText = L10n.Get("ui.dialog.cancel"),
});
timeWheel.AmLabel = L10n.Get("ui.time_wheel.am");   // 部品の欄（見た目への反映の時期は部品の作りに従う）
```

## 8. ホットリロード

`L10n.PollChanges()` は `index.json` と読んだ言語の表の更新の印（`Assets.GetModifiedTime`。UNIX 秒）を読んだときと比べ、違えば読み直して
`Changed` を知らせる（今の言語を保つ）。**既定では誰も呼ばない**。

開発中に使うときは、シーンのどこか 1 か所に ScriptComponent（型名 **`SEED.Localization.LocalizationReloader`**）を付ける。
`Application.IsDebugAllowed` のときだけ、「間隔（秒）」（`IntervalSeconds`。既定 1 秒・0.25〜60 秒。実時間）ごとに `PollChanges` を呼ぶ。
`OnStart` でも 1 回調べる（エディタの常駐の Play は前の Play の表を持ち越すので、Play の前に書き換えた分をここで拾う）。

| 場面 | 書き換えた表が効くとき |
|---|---|
| Play 中に JSON を保存 | LocalizationReloader があれば次の間隔で（LocalizedText・LocalizedLabel が入れ直す） |
| Play を止めて JSON を直し、もう一度 Play | LocalizationReloader があれば開始で。無ければ前の表のまま（スクリプトを読み直すまで） |
| スクリプトのホットリロード・事前コンパイル DLL の読み込み | `L10n` を捨てて次に使われたときに読み直す（`ScriptBridge` が `L10n.ResetForReload`。置き場は既定・言語は決まり方の順に戻る） |
| PAK 同梱（配布物） | 印が取れない（0）ので読み直さない |

更新の印は秒の単位なので、同じ秒に 2 回保存すると 2 回目を拾わないことがある。`render_policy: on_demand` で描画を止めている間は
`Update` が来ないので調べない（画面に触れると動く）。

## 9. 数・日付・時刻と Android の文化（Invariant）

```csharp
L10n.FormatNumber(1234.5, 1);                          // "1,234.5"（en-US）/ "1.234,5"（de-DE）。小数の桁は 0〜15
L10n.FormatDate(date, L10n.Get("format.date"));         // 言語ごとの書式を表に書いて渡す（ja "M月d日" / en "MMM d"）
L10n.FormatTime(new TimeOnly(7, 5), L10n.Get("format.time"));   // ja "H:mm" → "7:05" / en "h:mm tt" → "7:05 AM"
L10n.Culture                                            // 今の言語の文化（index の culture。Invariant の環境では不変文化＝Name が空）
```

**実情（2026-10-02 に設定を読んで確かめた）**:

- **PC（Windows）**: `scripting/SEEDScripting.csproj` は `InvariantGlobalization` を設定していない。ランタイムは `SEEDScripting.runtimeconfig.json` を
  そのまま使うので、OS の ICU で動く。`L10n.SystemLanguage` は OS の表示言語（`CultureInfo.CurrentUICulture`。例 `ja-JP`）、書式は言語の `culture` どおり。
- **Android（CoreCLR）**: `runtime/android/dotnet_runtime.json` の `runtime_properties` が **`System.Globalization.Invariant = true`** を CLR の起動前に
  設定する（アプリのプロセスから端末の ICU を読めないため。docs/android.md §17.6）。そこでは:
  - `CultureInfo.CurrentUICulture` は不変文化 → **`L10n.SystemLanguage` は null**。起動の言語は 保存した値 → 既定の言語 になる（端末の言語で始められない）
  - `CultureInfo.GetCultureInfo("ja-JP")` は `CultureNotFoundException`（`LocaleCulture.Resolve` が捕まえて不変文化にする）→ 書式はすべて不変文化
    （数は `1,234.5`、月・曜日の名前と `tt` は英語、標準の書式 `"d"` は `MM/dd/yyyy`）
  - 言語の表・切り替え・差し込み・複数形は文化に依らないので PC と同じに動く

Android でも言語どおりに出すには、**言語ごとの書式を表に書き**（`"format.date": "M月d日"` のように数字だけの書式なら不変文化でも同じに出る）、
**曜日・月の名前も表から引く**（`L10n.Get($"ui.weekday_short.{(int)date.DayOfWeek}")`）。端末の言語を確実に取るには、ランタイムが
OS の言語を読む口（`App.Locale` の候補）が要る（backlog）。

この振る舞いは `editor/tests/LocalizationTests` が子のプロセスを `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` で走らせて確かめている
（`CurrentUICulture` が不変文化・`SystemLanguage` が null・文化を引いても例外にならず不変文化の書式・本体が既定の言語で始まる）。

## 10. テンプレートと SEED.UI の固定文字列の対応表

`templates/locale/` は `templates/` の 1 カテゴリ（フォルダ名 `locale`）。テンプレートの取り込み（`editor/src/Templates/`）はライブラリを
アセットの根と同じ形として扱うので、`locale` のカテゴリ（またはファイル）を選ぶと**そのまま `<プロジェクト>/assets/locale/` へコピーされ、
既定の置き場で読める**（`editor/tests/LocalizationTests` が `TemplateLibrary`・`TemplateImporter` で計画・コピー・読み込みまで確かめている）。
`index.json` だけを選ぶと言語の表は付いてこない（参照で辿れない）ので、カテゴリごと選ぶ。カテゴリの表示名は今は `locale` のまま（backlog）。

SEED.UI の部品が持つ固定の文字列（**部品は L10n を読まない**。今回は部品の既定の文字列を置き換えない。アプリが部品へ渡すときに使う）:

| キー | ja | en | 部品の中の固定文字列 |
|---|---|---|---|
| `ui.dialog.ok` | OK | OK | `DialogOptions.DefaultPositiveText`（どのボタンも指定しないときの Positive） |
| `ui.dialog.cancel`・`yes`・`no`・`close`・`later`・`quit`・`delete` | キャンセル・はい・いいえ・閉じる・あとで・やめる・削除 | Cancel・Yes・No・Close・Later・Quit・Delete | （dialog.actor の見本の「やめる」「あとで」。ボタンの文字はアプリが渡す） |
| `ui.dialog.progress` | 処理しています… | Working… | dialog.actor の進捗の札の見本の文字（`Dialog.ShowProgress(message)` に渡す） |
| `ui.nav.back` | 戻る | Back | （画面の戻るのボタンの文字） |
| `ui.list_row.delete` | 削除 | Delete | list_row.actor の削除の面の文字 |
| `ui.time_wheel.am`・`pm` | 午前・午後 | AM・PM | `TimeWheel.AmLabel`・`PmLabel`（英語なら `MeridiemOnLeft = false` も） |
| `ui.chart.empty` | まだ記録はありません | No records yet | `ChartView.EmptyText` |
| `ui.weekday_short.0`〜`6` | 日〜土 | Sun〜Sat | chip_group.actor の見本の曜日（`DayOfWeek` の番号） |
| `ui.locale.language`・`follow_system` | 言語・端末の言語に合わせる | Language・Use device language | 言語を選ぶ画面 |
| `format.date`・`date_long`・`time` | M月d日・yyyy年M月d日・H:mm | MMM d・MMMM d, yyyy・h:mm tt | `ChartFormat` の日付 `M/d`・時刻 `H:mm` は固定（グラフの目盛りは言語に依らない） |
| `sample.*` | — | — | 書き方の見本（`{name}`・`{0}`・`{amount:N0}`・複数形・zero）。使わなければ消してよい |

## 11. Wake or Pay への当て方

Wake or Pay（`D:\SEED_projects\WakeOrPay`）は今、文言を `assets/common/data/strings.ja.json` に置き、アプリの `StringTable`
（`assets/scripts/Domain/Text/StringTable.cs`）で引いている。データの形（入れ子を `.` でつなぐ・`_` で始まる説明・`{名前}`・`{{ }}`）は
L10n と同じなので、次の順で移せる（アプリ側の作業。エンジンは変えない）:

1. `strings.ja.json` を `assets/locale/ja.json` へ移し、`assets/locale/index.json`（`{"default":"ja","languages":[{"code":"ja","name":"日本語"}]}`）を置く。
   英語を足すときは `en.json` と一覧の 1 行（`"fallback": "ja"`）。
2. `StringTable.Get(key)` → `L10n.Get(key)`、`StringTable.Format(key, ("name", value))` → `L10n.Get(key, ("name", value))`
   （値は文字列でなくてよい。数は今の言語の文化で書く）。`StringTable` を残すなら中身を L10n へ委ねる薄い殻にする。
3. 欠けの印は `⟦key⟧` → `[key]`（開発中）／`key`（配布用）。アプリのテストの「無いキー 0 件」は `L10n.MissingKeys` で確かめられる。
4. 画面の文字を作り直す所（`Refresh` など）で `this.On(L10n.Changed, (string _) => Refresh())` を受け、設定の画面に言語の選択
   （`L10n.Languages` の `Name` を並べ、`L10n.SetLanguage(code)`・「端末に合わせる」は `L10n.FollowSystemLanguage()`）を足す。
5. 日付・時刻の書式（`format.…` のキー）は数字だけの書式にしておく（Android は不変文化。§9）。

## 12. 上級: `LocaleCatalog` を直接使う

`L10n` は 1 つの表（`assets://locale`）を持つ静的な窓口。別の表（台本・MOD の表など）を別に持ちたいときは、`LocaleCatalog` を直接作れる:

```csharp
var catalog = new LocaleCatalog(new MySource(), new MyStore(), warn: msg => SEED.Debug.LogWarning(msg), systemLanguage: () => L10n.Language);
catalog.Configure("assets://story/locale");
string line = catalog.Get("chapter1.line3", new (string, object?)[] { ("name", hero) });
```

`ILocaleSource`（`TryReadText`・`GetModifiedTime`）と `ILocaleStore`（`GetString`・`SetString`・`Delete`）を実装して渡す。
本体はゲームのスレッドだけから使う（ロックしない）。

## 13. テスト

`dotnet run --project editor/tests/LocalizationTests`（61 件。2026-10-02）。純粋な部分（`Model/`）だけを取り込み、エンジン無しで回る:
表の平たん化・一覧の読み込みと言語の引き当て・探す順・差し込み・複数形の規則・本体（起動の言語・欠けの方針と警告の回数・保存のキー・
切り替え・端末に合わせる・書き換えの検知・置き場・壊れたデータ）・書式・Invariant の環境（子のプロセス）・見本の表（ja と en のキーと差し込みの一致）・
テンプレートの取り込み。`L10n`・`LocalizedText`・`LocalizedLabel`・`LocalizationReloader` はエンジンの上でしか動かない（Play での確かめは backlog）。

## 14. 制限

- 文字列だけを差し替える（画像・音声・フォントの言語ごとの差し替えは無い。フォントは Text の `FontPath` を言語ごとの表から引いて当てる）。
- 書字方向（右から左）・縦書きは無い。
- 複数形は整数だけ（§5）。序数（1st・2nd）・性別による変化は無い。
- Android は端末の言語を取れず、書式は不変文化（§9）。
- SEED.UI の部品の既定の文字列（`DialogOptions.DefaultPositiveText` など）は L10n を読まない（§10）。
- 残件と候補は backlog.md「ローカライズ（SEED.Localization）」。
