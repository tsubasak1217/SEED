# データバインディング（SEED.Binding）— 正典

観測できる値（Observable・Computed・ObservableList）と、UI 部品への結び付け（`Bind.*`）。2026-10-03（タスク L3-4）。
コードは `scripting/src/Api/Binding/`（純粋な部分は `Model/`、部品の口と当てる先は `Bind.*.cs`・`Targets/`）、
テストは `editor/tests/BindingTests`、見本は `templates/ui/scripts/UiBindingDemo.cs`。
スクリプト API の一覧は `docs/scripting_api.md` §7.22。

---

## 1. 考え方

アプリの画面のスクリプトは「状態が変わったら UI の文字・表示・トグルを直す」コードを画面ごとに手書きしていた
（Wake or Pay の `Render()`・`Apply…()`・`SyncToggle`・状態の版 `StateVersion` を毎フレーム見比べる、など）。
SEED.Binding は次の 2 つだけを用意して、画面のコードから「いつ UI を直すか」を消す。

1. **観測できる値**: 状態を `Observable<T>`（値）・`ObservableList<T>`（一覧）で持つ。導いた値は `Computed`。
2. **結び付け**: `Bind.Text(this, label, _count, n => $"{n} 回")` のように、値と UI 部品を 1 行で結ぶ。
   作った時点の値ですぐ当て、値が変わるたびに当て直す。トグル・スライダ・文字の欄・選択は双方向。

手本は Flutter の `ValueNotifier` / `ValueListenableBuilder` と UniRx の `ReactiveProperty` / `AddTo` の最小部分。SEED の流儀で次を守る。

- **明示的**: 結び付けは `Bind.*` の呼び出しだけ。属性・名前の文字列・リフレクションで自動に結ばない。
- **寿命はスクリプトに追従**: `owner: this` を渡すと、そのスクリプトの破棄で自動で外れる（`this.On` と同じ仕組み）。
- **部品の公開 API は変えない**: 部品の既存の口（`SetOn(…, notify: false)`・`Changed` など）を呼ぶ小さな当てる先（`Targets/`）を挟む。
- **テストできる**: 部品の代わりに `IBindTarget<T>` を挟むので、結び付けの規則はエンジン無しで確かめられる。

---

## 2. 早見

```csharp
using SEED;
using SEED.Binding;
using SEED.UI;
using SEEDEditor.Scripting;

public class CounterScreen : SEEDScript
{
    private readonly Observable<int> _count = new(0);          // 状態
    private readonly Observable<bool> _notify = new(true);
    private readonly ObservableList<string> _history = new();
    private ListView? _list;

    public override void OnStart()
    {
        if (gameObject.FindChild("Count").GetComponent<Text>() is { } label)
            Bind.Text(this, label, _count, n => $"押した回数: {n}");              // 値 → 文字
        Bind.Visible(this, gameObject.FindChild("Badge"), _count, n => n >= 10);  // 値 → 表示
        Bind.Toggle(this, gameObject.FindChild("Notify"), _notify);               // 値 ⇔ トグル（トグルの OnStart を待つ）
        _list = new ListView(gameObject.FindChild("List"), "assets://ui/prefabs/list_row.actor", 0, 56f,
                             Bind.RowBinder(_history, BindRow));
        Bind.List(this, _list, _history, BindRow);                                 // 一覧 → ListView
    }

    public override void Update(ref NativeFrameContext ctx) => _list?.Update();

    private void OnPlus() { _count.Value++; _history.Insert(0, $"{_count.Value} 回目"); }   // 状態を変えるだけ

    private static void BindRow(GameObject row, string item, int index)
    {
        if (row.FindChild("Title").GetComponent<Text>() is { } title) title.Content = item;
    }
}
```

---

## 3. 観測できる値

### 3.1 `Observable<T>`

| API | 約束 |
|---|---|
| `new Observable<T>(initial, comparer = null)` | 最初の値（知らせない）。比べ方は既定で `EqualityComparer<T>.Default` |
| `Value`（get/set） | set は今の値と**等しければ何もしない**。違えば入れて、その場で購読と結び付けへ知らせる（**即時・同期**） |
| `Subscribe(Action<T>) → IDisposable` | 変わったときだけ呼ぶ（購読した時点の値では呼ばない）。Dispose で解除（二重は無害） |
| `Subscribe(owner, Action<T>)` | 同じ購読をスクリプトの破棄で自動で外す（`BindingOwner` の拡張） |
| `Notify()` | 値は同じままで知らせる（参照型の中身を書き換えたとき。Flutter の `notifyListeners`） |
| `SubscriberCount` | 今の購読の数（結び付けを含む。診断・テスト用） |

知らせの順は登録の順。配っている途中に外した購読は（まだ呼んでいなくても）呼ばず、足した購読はその知らせでは呼ばない。
1 つの購読の例外はエラーログへ出して握り、残りの購読は続ける（`SEED.Events` と同じ）。

### 3.2 `Computed`

```csharp
var total = Computed.From(_price, _count, (p, n) => p * n);   // 依存 1〜3 個（4 個以上は重ねる）
var label = Computed.From(total, t => $"{t:N0} 円");
```

- 依存が変わったら計算し直し、**結果が前と違うときだけ**知らせる。
- **購読がある間だけ依存を購読する**。最後の購読が外れたら依存の購読も外す。なので結び付けへ直接渡して捨ててよい
  （`Bind.Visible(this, badge, Computed.From(_count, n => n > 0))` は、結び付けが外れれば依存まで外れる）。
- 購読が無い間の `Value` は呼ばれるたびに計算する（覚えない）。購読がある間は最後に計算した値。
- 依存の変化からの計算で例外が起きたら、エラーログへ出して前の値のまま（依存元のほかの購読は止めない）。
- 依存がひし形（A → B、A と B → C）だと、C は A の変化のたびに途中の値を 1 回知らせることがある（最後は正しい値になる）。

### 3.3 `ObservableList<T>` と `ListChange<T>`

| 操作 | 知らせ（`ListChange.Kind`・`Index`・`OldIndex`・`Item`・`OldItem`） |
|---|---|
| `Add(item)`・`Insert(i, item)` | `Insert`・入れた位置・入れた項目 |
| `RemoveAt(i)`・`Remove(item)` | `Remove`・外した位置・外した項目（`Remove` は無ければ false で知らせない） |
| `this[i] = item` | `Replace`・位置・新しい項目と前の項目（**同じ項目でも知らせる**＝行を描き直す合図に使える） |
| `Move(from, to)` | `Move`・`OldIndex` = 元・`Index` = 先（同じ位置なら知らせない） |
| `Clear()`・`ReplaceAll(items)` | `Reset`・位置は `ListChange<T>.NoIndex`（−1）。今の一覧を読み直す（空の Clear は知らせない） |

`Count`・`this[i]`・`IndexOf`・`Contains`・列挙（列挙の途中で変えると `InvalidOperationException`）・`Subscribe`（変化を起きた順に 1 件ずつ）。
範囲の外の位置は `List<T>` と同じ `ArgumentOutOfRangeException`（知らせず、一覧も変えない）。

---

## 4. 結び付け（`Bind`）

どれも `IDisposable` を返し、Dispose で外す（二重は無害）。**`owner` を受ける多重定義**（先頭の引数に `this`）は、
そのスクリプトの破棄で自動で外れる。値 → UI は作った時点の値ですぐ当てる。一方向の口は `IReadOnlyObservable<T>`
（`Observable`・`Computed`・`Bind.Deferred`）を受ける。双方向の口は書ける `Observable<T>` を受ける。

### 4.1 一方向（値 → UI）

| 口 | 当てる先 |
|---|---|
| `Bind.Text(text, IReadOnlyObservable<string>)` | `Text.Content` |
| `Bind.Text(text, source, Func<T, string> format)` | 値を書式で文字にして `Text.Content` |
| `Bind.Visible(node, IReadOnlyObservable<bool>)`・`Bind.Visible(node, source, Func<T, bool>)` | `GameObject.Visible`（子孫ごと。反映はフレームの末尾） |
| `Bind.Color(sprite, IReadOnlyObservable<Color>)`・`Bind.Color(sprite, source, Func<T, Color>)` | `Sprite.Color` |
| `Bind.To(source, Action<T>)` | 任意の処理（作った時点で 1 回・変わるたびに呼ぶ） |
| `Bind.OneWay(IBindTarget<T>, source)`・`Bind.OneWay(target, source, convert)` | 自作の当てる先（§4.5） |

当てる先が見えないとき（`Text.IsValid` / `Sprite.IsValid` が false・アクタが破棄された）は書かず、**フレームの区切り（LateUpdate の頭。World が見える）で確かめ直して**、本当に消えていれば自分を外す。見えないだけなら最新の値を 1 回当てる。別のスクリプトの `OnDestroy` の中（World が見えず `IsValid` が false になる）で観測値を変えても、結び付けは外れない（2026-10-03）。
`GameObject` の「動かす／止める」（Active）の口がまだ無いので `Bind.Active` は無い（`docs/backlog.md`）。

### 4.2 L10n との組み合わせ

```csharp
Bind.Text(this, moneyLabel, _money, "hud.money", "amount");   // ja.json: "hud": { "money": "所持金 {amount:N0} 円" }
```

値が変わるたびに `L10n.Get("hud.money", ("amount", 値))` を入れ、言語の切り替え・表の読み直し（`L10n.Changed`）でも
同じ値で引き直す。数・日付は今の言語の文化で書かれる。キーだけの固定の文字は `LocalizedText` / `LocalizedLabel`（docs/localization.md）。
差し込みが 2 つ以上・複数形などは `Computed` で文を作って `Bind.Text(text, IReadOnlyObservable<string>)` へ渡し、
言語の切り替えは `this.On(L10n.Changed, (string _) => …)` で観測値の元を変える（`Bind.Text` の L10n 版は差し込み 1 つだけ）。

### 4.3 双方向（値 ⇔ 部品）

| 口 | 値 → 部品 | 部品 → 値 |
|---|---|---|
| `Bind.Toggle(toggle or node, Observable<bool>)` | `SetOn(v, animate, notify: false)`（最初の 1 回はつまみをすぐ置き、その後は動かす） | `Toggle.Changed` |
| `Bind.Checkbox(checkbox or node, Observable<bool>)` | `SetChecked(v, notify: false)` | `Checkbox.Changed` |
| `Bind.Slider(slider or node, Observable<float>)` | `SetValue(v, notify: false)`（範囲・段階へ寄せた見た目。観測値は寄せる前のまま） | `Slider.ValueChanged` |
| `Bind.TextField(field or node, Observable<string>)` | `SetTextUnlessFocused(v)`（打っている最中の欄は上書きしない） | `TextField.TextChanged`（1 文字ごと・変換中を含む） |
| `Bind.Selection(group or node, Observable<int>)` | `Select(i)`（−1 は全部外す。範囲の外は書かず警告 1 回） | `SelectionGroup.SelectionChanged` → `SelectedIndex` |
| `Bind.TwoWay(ITwoWayBindTarget<T>, Observable<T>)` | 自作の部品（§4.5） | |

- **観測値が正**: 作った時点で観測値の値を部品へ当てる（部品の初めの値は捨てる）。
- **往復の留め金**: 部品へ書いている間の部品の知らせは観測値へ返さない（`SelectionGroup.Select` は必ず知らせるが止まる）。
  部品の値を観測値へ入れている間の観測値の知らせは部品へ書き戻さない。同じ観測値を 2 つの部品へ結ぶと、片方の操作がもう片方へ届く。
- **部品の OnStart を待つ**: 部品のスクリプトは画面のスクリプトより後に始まることがある（Instantiate したプレハブの部品は次のフレーム）。
  **ノード（`GameObject`）から結ぶ**と、部品が登録簿（`UiRegistry`）に載るまでフレームの区切りごとに待ち、載ったら知らせの口を付けて最新の値を当てる。
  選択のグループは子の項目が集まる（最初の Update）まで待つ。部品そのもの（`UiWidget.Of<T>` で引いた後）を渡してもよい。
- 部品が破棄されたら（登録簿から外れたら）自分を外す。同じノードに部品が作り直されても付け直さない（結び直す）。
- 複数を選ぶ `ChipGroup`（`Multiple = true`）は番号 1 つでは表せないので `Bind.Selection` に向かない。

### 4.4 一覧（`ObservableList` → `ListView`）

```csharp
_list = new ListView(scroll, RowPrefab, 0, RowExtent, Bind.RowBinder(_alarms, BindRow));  // 行の入れ方は一覧から
Bind.List(this, _list, _alarms, BindRow);                                                  // 数・中身の変化を写す
public override void Update(ref NativeFrameContext ctx) => _list.Update();                // 今までどおり毎フレーム
```

`ListView` は作るときに行の入れ方（bind）を受け取って変えられないので、作るときの bind を `Bind.RowBinder(一覧, bindRow)` にし、
同じ一覧・同じ bindRow を `Bind.List` にも渡す。`ListView` 自身は変えない。写し方:

| 一覧の変化 | ListView の口 |
|---|---|
| 結んだ時点 | `SetCount(今の数)` |
| `Insert`・`Remove`・`Reset` | `SetCount(今の数)`（次の `ListView.Update` で見えている行を入れ直す） |
| `Move` | `Refresh()` |
| `Replace` | その行が見えていれば（`RowOf(i)`）すぐ `bindRow(row, items[i], i)` |

### 4.5 自作の当てる先

`IBindTarget<T>`（`IsAlive`・`IsReady`・`Write`）、`ITwoWayBindTarget<T>`（＋ `Listen(Action<T>) → IDisposable`）、
`IListBindTarget`（`IsAlive`・`SetCount`・`Refresh`・`RebindRow`）を実装すれば、アプリの自作の部品も `Bind.OneWay` / `Bind.TwoWay` /
`Bind.List(IListBindTarget, …)` で結べる。`IsReady` が false の間は書かずに待ち、フレームの区切りで true になったら最新の値を当てる。
`Listen` は `IsReady` になってから 1 回だけ呼ばれる。部品のイベントの購読を外す口は `new DisposableAction(() => widget.Changed -= h)`。

---

## 5. フレームと再入の規則

- **即時**: 値の変更はその場で購読と結び付けへ届く（遅延しない）。UI 部品への書き込みは部品の規則に従う
  （`Visible` はフレームの末尾に反映・`ListView.SetCount` は次の `ListView.Update` で行を入れ直す、など）。
- **`Bind.Deferred(source)`**（任意）: 1 フレームに何度変わっても、**フレームの区切りで最新の値を 1 回だけ**知らせる観測値を作る。
  一方向の結び付けへそのまま渡す（`Bind.Text(this, label, Bind.Deferred(_score), s => $"{s:N0}")`）。区切りまでの `Value` は
  最後に知らせた値。そのフレームに元が知らせてきたら、値が元へ戻っていても 1 回知らせる。双方向には使えない（読むだけ）。
- **フレームの区切り**（`BindingFrame.Tick`）: エンジンが**フレームに 1 回、LateUpdate のフェーズの頭**（全スクリプトの Update の後・描画の前）で呼ぶ。
  Update・ジェスチャー・スクロールのコールバックで変えた値はそのフレームの描画に間に合う。LateUpdate 以降に変えた分は次のフレームの区切り。
  区切りの仕事: `Bind.Deferred` の知らせ・部品の用意（OnStart・選択の項目）を待っている結び付けの確かめ。区切りの途中で積まれた仕事は次の区切り。
- **再入**（購読の中で値を変える）: 入れ子に呼ばず、今の知らせを全員へ配り終えてから最新の値でもう 1 周配る
  （周の中で何度変えても次の周は最新の値 1 回。最後に配った値へ戻っただけなら次の周は無い）。どの購読も最後に受け取るのは同じ最新の値。
  一覧の変化はまとめられないので、待ち行列で**起きた順に**配る。
- **深さの上限**: 再入は `BindingLimits.MaxReentrantDepth`（= 1）段だけ許す。超えた変更は値（一覧）には入るが知らせず、
  観測値（一覧）ごとに 1 回だけ警告する（`[SEED.Binding] … 再入が上限（1 段）を超えました`）。A の購読が B を、B の購読が A を変える往復も止まる。
- スクリプトのフェーズ（メインスレッド）だけから使う（鍵は掛けない）。

---

## 6. 寿命

| 書き方 | 外れる時 |
|---|---|
| `Bind.X(this, …)` | このスクリプトの破棄（OnDestroy の直後・DestroyComponent）。`this.On` と同じ `SEEDScript.UnsubscribeAllEvents` |
| `observable.Subscribe(this, handler)`・`list.Subscribe(this, handler)` | 同上 |
| `disposable.AddTo(this)` | 同上（任意の IDisposable。UniRx の AddTo） |
| `Bind.X(…)`（owner なし）の戻り値 | Dispose したとき。当てる先が消えたことに気づいたとき（次の書き込み・区切り） |
| `new DisposableBag()` | 袋の Dispose（パネルを開いている間だけ、など画面より短い寿命のまとまり） |

- スクリプトの破棄の後に預けた物はその場で外れる（漏れない）。当てる先が消えて自分で外れた結び付けは、預けた袋が大きくなったときに捨てる。
- owner なしで作り、当てる先が消えた後に値が一度も変わらない結び付けは、観測値が捨てられるまで残る（**owner を渡すのが推奨**）。
- スクリプトの読み直し（ホットリロード）では、区切りを待っている仕事を捨てる（`BindingFrame.ResetForReload`。旧アセンブリを握らない）。
- **名前の衝突**: 画面のスクリプトに `Bind` という名前のメソッドがあると、その中の `Bind.Text(…)` はメソッドを指してコンパイルできない（CS0119）。
  `SEED.Binding.Bind.Text(…)` と書くか、`using BindTo = SEED.Binding.Bind;` のように別名を付ける（Wake or Pay の `PopupPlane.Bind()` など）。

---

## 7. Wake or Pay の `Render()` / `Refresh()` の置き換え

いまの書き方（ショップの画面）: 状態の版を毎フレーム見比べ、変わったら全部の文字と表示を書き直す。

```csharp
protected override void OnWidgetUpdate(float dt)
{
    if (_renderedVersion != services.StateVersion)            // 版を見比べる
    {
        _renderedVersion = services.StateVersion;
        ShopView view = ShopViewBuilder.Build(services.State.Wallet, …);
        NodeText.Set(gameObject, CoinsLabelPath, view.CoinsLabel);   // 全部書き直す
        NodeLook.SetVisible(gameObject, DevChargePath, view.ShowsDevCharge);
    }
    ApplyButtonLabel();                                         // 部品が始まったか毎フレーム見る
}
```

結び付けにすると: 状態（財布）が観測値を持ち、画面は OnStart で 1 回結ぶだけ。版の見比べ・書き直しの関数・部品の待ちが消える。

```csharp
// 状態の側（AppState など）: 値を観測値で持つ
public readonly Observable<long> Coins = new(0);
public readonly Observable<bool> DevChargeAllowed = new(false);

// 画面の側: OnStart で 1 回だけ
protected override void OnWidgetStart()
{
    var wallet = AppServices.Current!.State.Wallet;
    if (TextAt(CoinsLabelPath) is { } coins)
        Bind.Text(this, coins, wallet.Coins, "shop.coins", "amount");   // 言語が替わっても引き直す
    Bind.Visible(this, gameObject.FindChild(DevChargePath), wallet.DevChargeAllowed);
    Bind.Visible(this, gameObject.FindChild(ButtonGapPath), wallet.DevChargeAllowed);
}
// コインが増えた（どこからでも）: wallet.Coins.Value += 100; → 文字が直る
```

移し方の目安: ①画面ごとの `Render` が読んでいる状態の欄を `Observable` に替える（保存の形は変えない）、②`Render` の 1 行を
`Bind.*` 1 行へ、③状態の版（`StateVersion`）を見比べるコードを消す、④部品を待つコード（`WidgetBinder.SyncToggle`）を
`Bind.Toggle(this, node, …)` へ。レイアウトで決まるもの（文字の折り返し・高さの合わせ）は値の変化ではなくレイアウトの変化なので、今までどおり。

---

## 8. 見本とテスト

- 見本: `templates/ui/scripts/UiBindingDemo.cs`（カウンタ・トグル・一覧・Computed・Deferred。シーンの子の名前は先頭のコメント。
  デバッグの命令 `SCRIPT_DEBUG:binding,plus|toggle|clear|burst|state`）。ギャラリーのシーンには置いていない（コンパイルの確かめだけ）。
- テスト: `dotnet run --project editor/tests/BindingTests`（Observable・Computed・ObservableList・一方向・双方向・一覧・Deferred・
  BindingFrame・DisposableBag。部品の代わりに偽の当てる先）。部品の口（`Bind.Text`・`Bind.Toggle`…）と当てる先（`Targets/`）は
  エンジンの上でしか動かないので単体テストの外。

---

## 9. 制限（残件は docs/backlog.md「データバインディング」）

- Play・実機での確かめは未了（単体テストとコンパイルだけ）。
- 一覧の数を観測値として出す口（`ObservableList.Count` の観測値）は無い（`Subscribe` で自分の観測値を変える）。
- `Bind.Text` の L10n 版は差し込み 1 つ・複数形なし。
- 部品が同じノードに作り直されたとき（スクリプトの付け替え）は付け直さない。
- Computed のひし形の依存は途中の値を知らせることがある（§3.2）。
- `Bind.Active`（GameObject の動かす／止める）は GameObject 側の口が無いので無い。
