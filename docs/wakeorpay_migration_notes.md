# Wake or Pay の回避コードを今夜の SEED API へ置き換える手引き（2026-10-03。タスク L3-8）

2026-10-02 夜〜10-03 未明に、Wake or Pay（`D:\SEED_projects\WakeOrPay`）がアプリ側で回避していた不足を埋める API が SEED に入った。
この文書は「アプリの回避コード → 置き換え先の API → 手順と注意」を 1 対 1 で対応づけた**作業の手引き**で、作業そのものはアプリ側で行う（エンジンは変えない）。

- **エンジン側の API 名・シグネチャ**は、ブランチ lane3-scripting（772c7309 の時点の作業ツリー）の `scripting/src/Api/**` の実コードを読んで確かめた。
  docs と食い違う所はコードに合わせた。各 API の規則の正典は [ui_navigation.md](ui_navigation.md)・[ui_components.md](ui_components.md)・
  [localization.md](localization.md)・[ui_binding.md](ui_binding.md)・[scripting_api.md](scripting_api.md)・[editor_device_presets.md](editor_device_presets.md)。ここには「どこを何に替えるか」だけを書く。
- **アプリ側のファイル・クラス・行番号**は、2026-10-03 に `D:\SEED_projects\WakeOrPay\assets\scripts\` を grep・Read して確かめた値
  （パスはこのフォルダからの相対。`App/...`・`Domain/...`）。アプリを編集すれば行はずれるので、目安として使う。
- 項目の番号は M-00〜M-17。残件は [backlog.md](backlog.md) の「Wake or Pay 側の置き換え（朝以降）」で追う。

---

## 0. 先に読む注意

### 0.1 Wake or Pay の画面・実機では、どの API もまだ確かめていない

置き換え先の API は、どれも **Wake or Pay の Play・Pixel 6a の実機では一度も動かしていない**。SEED の中での確かめの範囲は次のとおり。
段ごとに Play で確かめ（§1）、エンジンの不具合らしいものは backlog の該当の節へ書く。

| API | SEED の中で確かめた範囲 | 出典 |
|---|---|---|
| `Dialog` の危険のボタン・選択肢の一覧（`ShowMenu`）・進捗の札（`ShowProgress`）・ボタンの縦積み・入力欄の幅の直し | PC の Play（ギャラリー・ui_text_input.scene。2026-10-02）。外から閉じる口がボタンと同じ決め方を通る直し（2026-10-03。レビュー #9）は単体テストまで | ui_navigation.md §11・backlog「2026-10-03 の UI 部品の手直し」 |
| `Slider` の溝の追従・`Button.FitLabel`・`ProgressSpinner`・`ProgressRing.Thickness` | PC の Play（ギャラリー。2026-10-02） | ui_components.md §13.6 |
| `Slider.TickCount`・スピナーが切り抜きの外で止まる | 単体テストと C# のビルドまで（Play 未確認） | ui_components.md §13.6 |
| `ScreenStack.Prewarm`・`Push(GameObject, …)`・`ModalHost.Park`・`CloseAll`・`Popup`・`OverlayOptions.FillHeight`／`Animate`・`NavigatorRegistry` の公開 | 単体テスト（純粋な計算）と C# のビルドまで（Play 未確認） | backlog「2026-10-02 の画面の遷移・面の口（lane3）で残したもの」(1) |
| `SEED.Localization`（`L10n`・`LocalizedText`・`LocalizedLabel`） | 単体テスト（LocalizationTests 62 件）まで。L10n とその部品は Play 未確認 | localization.md §13 |
| `SEED.Binding` | 単体テスト（BindingTests 53 件）まで（Play 未確認） | ui_binding.md §9 |
| `GetScript`・`Instances`・`FindInstance` | 単体テスト（ScriptRegistryTests 30 件）と一時のハーネスまで（Play 未確認） | backlog「スクリプトを引く」 |
| 動的ノード API（`Children`・`GetChild`・`Create2D`・`AddComponent`・`AddScript`） | docs に確かめの記録を見つけられなかった（**要確認**） | scripting_api.md §7「動的ノード」 |
| 実行先「PC（端末の模擬: …）」 | 単体テストまで。実起動は未検証 | editor_device_presets.md §4 |

### 0.2 置き換えなくても、新しい DLL で変わること

SEED を更新した時点で（アプリを触らなくても）次の振る舞いが変わっている。段 0 の基準の撮影（M-00 の手順 1）で崩れていないかを見る。

- **`Button.FitLabel`（既定 true）**: SEED.UI.Button の文字の枠を、レイアウトの大きさが変わるたびにボタンの大きさへ合わせる。伸ばしていないボタンは変わらない。
  `WrappedText.FitWidth` でボタンの文字の枠を書いている所（M-02）は、今は両方が書いている。
- **ダイアログの入力欄の幅**: 札の中の幅（264）にそろうようになった（`DialogInputFit` と同じ値を書く。M-03）。
- **ダイアログを外から閉じる**（`DialogHandle.Close(DialogResult)`・`Dismiss()`・`ModalHandle.Close`・`ModalHost.CloseAll`）は、ボタンと同じ決め方を通る
  （`Close(DialogResult.Positive)` で `InputText` が入る・入力欄のフォーカスとキーボードの持ち上げを片付ける。2026-10-03。レビュー #9）。
- **面のプレハブが無い**（`popup.actor` を入れずに `Popup.Show` した等）と、60 フレームで「面のスクリプトが始まりません」のエラーを出して手札を閉じる（以前は戻るを飲み込み続けた。レビュー #20）。
- 「Play 中の変更をプレハブへ書き戻す」が既定で無効になった（コミット 4cac1dbf。`editor_preferences.json` の `prefab_write_back_enabled`）。

置き換えたときにだけ効く挙動の違い（`CloseAll` の結果・`Push(GameObject)` の 2 回目が null・`PrewarmMode.Reuse` の `OnScreenExit` など）は、各項目の「注意」に書いた。

---

## 1. 推奨の順序

**templates/ui の取り込み直し → 部品 → ナビ → L10n → Binding** の順に、1 段ずつ置き換える。段ごとに Play（実行先「PC（端末の模擬: Pixel 6a 半分）」。M-17）と
`dotnet run --project tests/DomainTests`（Wake or Pay の根で）を回し、Wake or Pay 側の版の管理に段ごとに記録する。

| 段 | 項目 | なぜこの順か | Play で見る所（README の PC での確かめの命令も使う） |
|---|---|---|---|
| 0 | M-00 | **先にやらないと新しい部品が出ない**（選択肢の一覧・進捗のスピナー・刻みの点・ポップアップの面がプレハブ側にある） | 置き換えの前の基準の撮影と、写した後の比べ（ダイアログ・トースト・時刻ホイール・数値のサブ画面） |
| 1 | M-01〜M-06（部品） | 画面の中で閉じた小さな置き換え。ほかの回避と結びつかない | 数値のサブ画面のスライダ（溝・点・1 ずつの寄せ・欄との往復）、7 つのボタンの文字が真ん中か、名前の変更のダイアログの入力欄、長押しのメニュー、削除・破棄のボタンの色、開発用のサンプルの進捗 |
| 2 | M-07 → M-08 → M-09 → M-10 → M-11 → M-12（ナビ。易しい順） | M-10（ポップアップ）と M-11（覆いを全画面の下へ）は結びついているので続けて行う。M-12（作り置き）は編集画面の `HiddenScrollKeeper` と一緒に確かめるので最後 | 鳴動で開いている面がすべて消えるか、オプションの覆いの高さ（411 dp・回転）、庭の住人の止まり方、ポップアップの出入り・×・幕・戻る・札の高さ、オプション → 上限金額の最大値 → 戻る・プロフィール → 編集 → 戻る（`nav,probe`・`nav,backgesture`）、編集画面の開き方とスクロールの位置（`nav,scroll`） |
| 3 | M-13（L10n） | 呼び出しは多いが機械的。Domain の純粋さの制約（DomainTests は SEED を参照しない）を先に決める | 文が今と同じか（欠けの印 `[key]` が出ない・`L10n.MissingKeys` が 0 件）、DomainTests が通るか |
| 4 | M-14〜M-16（Binding・スクリプトを引く・動的ノード） | 13 の画面に広がるので 1 画面ずつ。L10n の後にすると `Bind.Text` の L10n 版をそのまま使える | 状態が変わる操作（コインの増減・アラームの追加と削除・テーマの交換・権限の変化）で画面が直るか、トグルの往復 |
| 随時 | M-17 | 段 0 から使う | — |

---

## 2. 対応表（一覧）

| # | アプリ側の回避（場所） | 置き換え先 | 段 | 手間 |
|---|---|---|---|---|
| M-00 | `assets/ui/prefabs` の古い写し（dialog・slider・toast が古く、popup・dialog_item・progress_spinner・slider_tick が無い） | templates/ui の 7 ファイルを写し直す | 0 | 小 |
| M-01 | `FullWidthSlider`（`App/Widgets/FullWidthSlider.cs:31`） | `SEED.UI.Slider`（溝は追従済み）＋ `TickCount`・`SetTickCount`・`TickPrefab` | 1 | 中 |
| M-02 | `WrappedText.FitWidth`（ボタンの文字。7 行） | `Button.FitLabel`（既定 true） | 1 | 小 |
| M-03 | `DialogInputFit`（`App/Widgets/DialogInputFit.cs:17`） | 消す（Dialog 側で直った） | 1 | 小 |
| M-04 | `AlarmDialogs.ShowRowMenu`（ボタン 2 つのダイアログで長押しのメニュー） | `Dialog.ShowMenu`・`DialogMenuItem` | 1 | 小 |
| M-05 | 赤にしていない「削除」「破棄して戻る」（回避ではなく未対応） | `DialogOptions.PositiveKind`・`NegativeKind` = `DialogButtonKind.Danger` | 1 | 小 |
| M-06 | 手作りの回転（`OptionsOverlayScreen.ApplyProgress`） | `ProgressSpinner`（または `Dialog.ShowProgress`） | 1 | 小 |
| M-07 | `AppNavigator.DismissModals`（戻るの繰り返し） | `ModalHost.CloseAll(animate: false)` | 2 | 小 |
| M-08 | `TopSheetFit`（中身の側で高さいっぱいを計算） | `OverlayOptions.FillHeight`・`FillBottomMargin`（動きなしは `Animate = false`） | 2 | 小 |
| M-09 | `NodeVisibility.IsShown`（`IsActiveNode` が internal だった代わり） | `NavigatorRegistry.IsActiveNode` | 2 | 小 |
| M-10 | プロフィールの中央のポップアップ（`PopupPlane`・`IPopupContent`・`PopupRequest`・`OverlayPrefab` の一時の差し替え） | `SEED.UI.Popup.Show(PopupOptions)`・`IPopupContentSize`・`ModalHost.PopupPrefab` | 2 | 中 |
| M-11 | `ModalParking`・`ModalUnderPage`・`ParkedPagePreview`・`IParkedPage` | `ModalHost.Park`・`Unpark`・`IsParked`（＋ `NavigatorRegistry`・`ScreenStack` の戻るの口） | 2 | 大 |
| M-12 | `PrebuiltContent`・`IPrebuiltBody`（隠した置き場で作り置きして `SetParent`） | `ScreenStack.Prewarm(prefab, PrewarmOptions)`／`Push(GameObject, …, ScreenContentRelease)` | 2 | 大 |
| M-13 | `StringTable`（文字列の表） | `SEED.Localization.L10n`（`{{ }}` の書き方は同じ）・`LocalizedText`・`LocalizedLabel` | 3 | 大（機械的） |
| M-14 | 画面ごとの `Render()` と `StateVersion` の見比べ・`WidgetBinder.SyncToggle` | `SEED.Binding`（`Observable<T>`・`Computed`・`Bind.Text`／`Visible`／`Toggle`／`Slider`／`List`） | 4 | 大 |
| M-15 | 自前の `static Current`・行の登録簿（`AlarmRow.Rows`） | `SEEDScript.Instances<T>()`・`FindInstance<T>()`・`gameObject.GetScript<T>()` | 4 | 小 |
| M-16 | 子の名前の規約（`AlarmRow{0}` など）・`PrefabPool` | `GameObject.Children`・`GetChild`・`ChildCount`・`SiblingIndex`・`Create2D`・`AddComponent<T>`・`AddScript<T>` | 4 | 任意 |
| M-17 | 端末の模擬の手動の環境変数 | エディタの実行先「PC（端末の模擬: …）」 | 随時 | 小 |

置き換えないもの（`HiddenScrollKeeper`・`KeepScreenOnLease` など）は §4。

---

## 3. 項目ごとの手順

### M-00 基準を撮り、templates/ui を取り込み直す（最初に必ず）

**今の場所**: `D:\SEED_projects\WakeOrPay\assets\ui\prefabs\` に SEED の `templates/ui/prefabs` の写しが 28 個ある。
2026-10-03 に今の templates と中身を比べた（改行・空白を無視した diff）結果、**25 個は同じ中身で、違うのは次の 3 個だけ。どれも templates 側の更新で、アプリ独自の手直しは無い**。

| ファイル | Wake or Pay の写し | templates の今 | 古いままだと |
|---|---|---|---|
| `dialog.actor` | Card の直下に Title・Message・Input・Buttons | Progress・Body・Items の区画を足した形（ui_navigation.md §3.2 の図） | 選択肢の一覧が出ない（警告して題だけ）・進捗の札にスピナーが無い・長い本文がスクロールしない |
| `slider.actor` | 子 `Ticks`・欄 `TickPrefab` が無い | ある | 刻みの点を描かない（警告 1 度） |
| `toast.actor` | 子 `Icon` が無い | ある | トーストのアイコンが出ない（文字だけ） |

無いファイル: `popup.actor`（`ModalHost.PopupPrefab` の既定）・`dialog_item.actor`（選択肢の 1 行）・`progress_spinner.actor`・`slider_tick.actor`（刻みの点）。

**手順**:

1. 置き換えの前に、今のまま Play して基準を撮る（アラームの一覧・編集画面・数値のサブ画面・ダイアログ・オプションの覆い・プロフィールのポップアップ・庭）。§0.2 の変化はここで分かる。
2. 写す。**推奨は B**:
   - **B. 要るファイルだけ写す**: `<SEED>/templates/ui/prefabs/` から `assets/ui/prefabs/` へ、上書き 3 個（`dialog.actor`・`slider.actor`・`toast.actor`）と
     新規 4 個（`popup.actor`・`dialog_item.actor`・`progress_spinner.actor`・`slider_tick.actor`）。7 個のうち外を参照するのは `slider.actor` → `slider_tick.actor` だけ
     （2026-10-03 に `assets://` を grep して確かめた）。
   - **A. エディタの取り込み**: ファイル → テンプレートをインポート... →「UI 部品（W2-4）」の `prefabs` を選び、**「既存ファイルを上書きする」にチェック**してインポート
     （チェックを忘れると既存の 3 個は「触らなかったファイル」になり古いまま）。取り込みの単位はカテゴリの直下の子（[template_library.md](template_library.md) §3「エントリの粒度」）なので
     `prefabs` の 45 個がまとめて入り、閉包で見本の画面（`nav_*`）が参照する `ui/scripts/NavSampleScreen.cs`・`ui/scripts/UiGalleryListRow.cs`・`ui/textures/avatar.png` もコピーされる。
     スクリプトはプロジェクトのスクリプトとして組み込まれ、パッケージにも入る（パッケージ化は全 `.cs` を起点にする）ので、要らなければ後で消す。
3. Play で基準と比べる。lane2 の比べ（ui_navigation.md §11）では、ふつうのダイアログの札の中の差は字の縁だけ（最大 3/255）・文字だけのトーストは差 0 だった。

**注意**:

- テンプレートアクタ（ヒエラルキーの右クリック）からの追加は既にあるファイルを上書きしないので、取り込み直しには使えない（template_library.md §9.7）。
- 新しいトークン（`size.popup_*`・`size.spinner`・`size.slider_tick`・`size.dialog_items_inset` など）は SEED の既定のテーマ（DLL に埋め込み）から来るので、`wop_base.json` に足さなくても動く。

---

### M-01 `FullWidthSlider` → `SEED.UI.Slider`（溝の追従は済み）＋ 刻みの点

**今の場所**:

- `App/Widgets/FullWidthSlider.cs:31`（`FullWidthSlider : UiWidget`。`Configure(min, max, value, tickCount)` 98 行・`SetValue(float value, bool notify = false)` 110 行・
  点は作り置きの `Ticks/Tick0`〜`Tick30`〈`MaxTicks` 31。43・46 行〉）。13〜18 行の注記に「SEED の Slider がレイアウトの幅に追従するようになれば置き換えられる」。
- 使う所は `App/Screens/Alarms/Sub/NumberScreen.cs` だけ: 132（フィールド）・217（`UiWidget.Of<FullWidthSlider>(gameObject.FindChild(SliderPath))`）・
  219（`Configure(min, max, _value, NumberFieldRules.TickCount(min, max))`）・220（`ValueChanged +=`）・161（外す）・230 と 242（`SetValue(value)`）。
- ノードは `assets/alarms/prefabs/sub_number.actor` の `Slider`（Track・Fill・Ticks・Thumb）。溝の左右の余白はトークン `app.size.slider_inset`（24。`wop_base.json` 86 行）、
  点の直径は `app.size.slider_tick`（3。87 行）。
- 刻みは `Domain/Forms/NumberFieldRules.cs:92`（`TickCount`: 幅が `MaxTickedSpan` 30 以下なら幅、超えたら 0）。**刻みがあると値も 1 ずつへ寄せる**（`StepSize = (Max − Min) / TickCount`）。

**置き換え先**（`scripting/src/Api/UI/Widgets/Slider.cs`）:

```csharp
public sealed class Slider : UiWidget
{
    public const string DefaultTickPrefab = "assets://ui/prefabs/slider_tick.actor";
    public float Min;
    public float Max = 1f;
    public float Step;                          // 値の段階（0 = 連続）。TickCount とは独立
    public float Value;
    public int TickCount;                       // 2026-10-03。0 = 点なし。点は両端を含めて TickCount + 1 個（上限 SliderTicks.MaxTickCount = 100）
    public string TickPrefab = DefaultTickPrefab;
    public event Action<Slider, float>? ValueChanged;
    public bool IsDragging { get; }
    public float TrackLength { get; }           // 溝の長さ（レイアウトの幅に追従した値）
    public void SetValue(float value, bool notify = true);   // 既定 notify = true（FullWidthSlider は false）
    public void SetTickCount(int count);
}
```

**手順**:

1. M-00 で `slider.actor`・`slider_tick.actor` を写しておく。
2. `sub_number.actor` の `Slider` を `slider.actor` のインスタンスに替える（名前は `Slider` のまま・幅いっぱい・高さ 48）。
   溝の左右の余白はプレハブの Track の位置から読む（templates の slider.actor は部品 240・Track の x = 10・幅 220 ＝ 余白 10）。今の 24 にそろえるなら、
   インスタンスで Track の x を 24・幅を「部品の幅 − 48」にする（溝の長さはレイアウトの幅に追従して決め直すので、余白が合っていればよい）。
3. `NumberScreen` の `FullWidthSlider` を `Slider` にし、`Configure` の代わりに:

   ```csharp
   _slider = UiWidget.Of<Slider>(gameObject.FindChild(SliderPath));
   if (_slider is null) return;
   int ticks = NumberFieldRules.TickCount(request.Spec.Min, request.Spec.Max);
   _slider.Min = request.Spec.Min;
   _slider.Max = Math.Max(request.Spec.Min, request.Spec.Max);
   _slider.Step = ticks > 0 ? 1f : 0f;     // FullWidthSlider は刻みがあると 1 ずつへ寄せた（SEED は Step を別に指定する）
   _slider.SetTickCount(ticks);
   _slider.SetValue(_value, notify: false);
   _slider.ValueChanged += OnSliderChanged;
   ```

   `OnSliderChanged(Slider slider, float raw)` の中（230 行）と `OnFieldEdited`（242 行）の `SetValue(value)` は `SetValue(value, notify: false)` にする。
4. `FullWidthSlider.cs` を消す。トークン `app.size.slider_inset`・`app.size.slider_tick` は使わなくなる（点の直径は SEED の `size.slider_tick` 3 で同じ）。

**注意**:

- **既定の知らせが逆**: SEED の `SetValue` は既定で `ValueChanged` を出す。`ValueChanged` の中で丸めた値を知らせつきで書くと `OnSliderChanged` がもう 1 回来る
  （同じ値なら `SetValue` が何もしないので回り続けはしない。コードを読んだ範囲）。
- **範囲だけを変えても描き直さない**: `Min`・`Max`・`Step` は欄の代入で、見た目を作り直すのは `SetValue`（値が変わったとき）と `SetTickCount`（数が変わったとき）だけ。
  値も刻みの数も前と同じで範囲だけが違うと、つまみが古い範囲の位置に残る。上の順（範囲 → 刻み → 値）で避けられない組み合わせが出るなら、部品の OnStart の前に欄を書く
  （`gameObject.FindChild(SliderPath).GetScript<Slider>()` は OnStart 前でも引ける。M-15）。後者は推論で、Play で確かめていない（backlog に記録）。
- 点の色は塗りの上が `color.on_primary`・外が `color.on_surface_muted`（FullWidthSlider と同じ）。点がくっつく（間隔 < 直径 × 2）と描かない（幅 220 の溝で 37 刻み以上。30 刻みは描く）。
- 刻みの点は単体テストまで（Play 未確認）。

---

### M-02 `WrappedText.FitWidth`（ボタンの文字）→ `Button.FitLabel`

**今の場所**: `App/Widgets/WrappedText.cs:78`（`FitWidth(GameObject node)`: 文字のノードの `Text.BoxWidth` を自分のレイアウトの幅にする）。呼ぶ所は 9 ファイル 14 行で、
そのうち**ボタンの文字は 7 か所**（どのボタンのプレハブにも `SEED.UI.Button` が付いていることを `.actor` の grep で確かめた）:

| 場所（`App/Screens/` から） | ノード |
|---|---|
| `Onboarding/OnboardingScreen.cs:227`・`228` | `…/PrimaryButton/Label`・`…/SecondaryButton/Label`（36・37 行） |
| `Options/CapCeilingScreen.cs:99` | `…/SaveButton/Label`（35 行） |
| `Options/NoRingHelpScreen.cs:57`・`58` | `SettingsButton/Label`・`SiteButton/Label` |
| `Options/OptionsOverlayScreen.cs:209` | `CloseArea/CloseButton/Label`（55 行） |
| `Shop/ShopScreen.cs:126` の `CenteredLines` のうち `DevChargeLabelPath` | `…/DevChargeButton/Label`（38・53 行） |

残り（`Activity/ActivityScreen.cs:112`・`Alarms/Sub/NumberScreen.cs:280`・`Profiles/ProfileHeadView.cs:138〜140`・`Profiles/ProfileOverlayScreen.cs:159`・`160` の 7 行と、
`ShopScreen.cs:126` の `CenteredLines` の残りの 3 つ）はボタンでない文字なので残す（§4）。

**置き換え先**（`scripting/src/Api/UI/Widgets/Button.cs`）:

```csharp
[SerializeField(Label = "文字の枠を大きさに合わせる")]
public bool FitLabel = true;   // 2026-10-02。レイアウトの大きさが変わるたびに
                               // 文字の枠 = レイアウトの大きさ ＋（プレハブの文字の枠 − プレハブのボタンの大きさ）（軸ごと。ButtonLabelFit.BoxSize）
```

**手順**: 上の 6 行を消し、`ShopScreen` は `CenteredLines`（53 行）から `DevChargeLabelPath` を外す。ボタンのプレハブは触らない（`FitLabel` は既定で効いている）。

**注意**:

- `FitLabel` は DLL を更新した時点で効いているので、今は両方が枠を書いている（同じ幅なら食い違わない。ui_components.md §13.7）。消すと枠は「ボタンのレイアウトの大きさ ＋ プレハブの差」
  だけで決まる。プレハブで文字の枠とボタンの大きさが違うボタンは `FitWidth` の値（文字のノードのレイアウトの幅）と違うことがあるので、撮影で真ん中に来ているかを見る。
- `FitLabel` が効くのは、根に Sprite・子 `Label` に Text がある SEED.UI.Button だけ（`OnWidgetStart` で読む）。違う幅にしたいボタンは `FitLabel = false`。

---

### M-03 `DialogInputFit` → 消す

**今の場所**: `App/Widgets/DialogInputFit.cs:17`（`Apply(ModalHandle)`: `Card/Input` の幅を入力欄 `TextField` の Sprite の幅へ毎フレーム書く。14 行に「SEED 側で直ったら消してよい」）。
呼ぶのは `App/Screens/Profiles/ProfileEditScreen.cs:124`（名前の変更のダイアログ。212 行で開く）だけ。

**置き換え先**: 無い。Dialog の側で直った（ui_navigation.md §3.2「入力欄の幅（直し）」: 入力欄を札の中の幅 264 にそろえ、入力欄のスクリプトが始まったときと割り付けのたびに当てる）。

**手順**: 124 行と `DialogInputFit.cs` を消す。

**注意**: 直しは DLL の側なので、今も両方が同じ 264 を書いている（害は無い）。古い `dialog.actor` でも効く見込み（推論。PC で確かめたのは templates の dialog.actor）。

---

### M-04 長押しのメニュー（`ShowRowMenu`）→ `Dialog.ShowMenu`

**今の場所**: `App/Screens/Alarms/AlarmDialogs.cs:68`（`ShowRowMenu(string timeText, StringTable strings, Action onDuplicate, Action onDelete)`: 題 = 時刻・Negative = 削除・Positive = 複製の
ボタン 2 つのダイアログ。12〜13 行の注記に「SimpleDialog の部品が無いので代える」）。呼ぶのは `App/Screens/Alarms/AlarmListScreen.cs:287`（`OnRowLongPressed`）だけ。

**置き換え先**（`Navigation/Dialog.cs`・`Navigation/Model/DialogModel.cs`）:

```csharp
public static DialogHandle? Dialog.ShowMenu(string title, params DialogMenuItem[] items);
public DialogMenuItem(string text, UiIcon? icon = null, DialogButtonKind kind = DialogButtonKind.Default);   // bool Enabled { get; init; } = true
// 結果: 押した項目で DialogResult.Selected、番号は DialogHandle.SelectedIndex（Items の添字。ほかの結果では −1）。幕のタップ・戻るは Dismissed
```

**手順**:

```csharp
public static void ShowRowMenu(string timeText, StringTable strings, Action onDuplicate, Action onDelete)
{
    DialogHandle? handle = Dialog.ShowMenu(timeText,
        new DialogMenuItem(strings.Get(MenuDuplicateKey)),
        new DialogMenuItem(strings.Get(MenuDeleteKey), kind: DialogButtonKind.Danger));
    if (handle is null) return;
    handle.Completed += result =>
    {
        if (result != DialogResult.Selected) return;   // 幕・戻る（Dismissed）は今と同じく何もしない
        if (handle.SelectedIndex == 0) onDuplicate();
        else if (handle.SelectedIndex == 1) onDelete();
    };
}
```

**注意**:

- **M-00 が先**: 古い `dialog.actor`（`Items` が無い）と `dialog_item.actor` が無い状態では一覧が出ず、警告を出して題だけのダイアログになる。
- 項目の並び（複製 → 削除）とアイコンの有無は Flutter 版に合わせて決める（**要判断**）。
- ボタンを指定しないので一覧の下にボタンは出ない（`NegativeText` などを足せば出る）。

---

### M-05 赤にしていない「削除」「破棄して戻る」→ `DialogButtonKind.Danger`（追加の項目）

回避のコードではなく、`AlarmDialogs.cs:12` の注記どおり「危険の種類が無いので赤にならない」まま残している所。

**今の場所**: `App/Screens/Alarms/AlarmDialogs.cs:91`（削除の確認。Positive = 削除）・`:109`（未保存の確認。Negative = 破棄して戻る）・
`App/Screens/Garden/Edit/GardenEditDialogs.cs:22`（模様替えの未保存。Negative = 破棄）。`App/Screens/Options/SampleDataFlow.cs:62`（サンプルの作成・削除の確認）は中身による（要判断）。

**置き換え先**（`DialogOptions`）:

```csharp
public DialogButtonKind PositiveKind { get; init; }   // Danger: color.error の塗り・color.on_error の文字
public DialogButtonKind NegativeKind { get; init; }   // Danger: color.error の文字
public DialogButtonKind NeutralKind { get; init; }    // Danger: color.error の文字
```

**手順**: 削除の確認に `PositiveKind = DialogButtonKind.Danger`、破棄のボタンに `NegativeKind = DialogButtonKind.Danger` を足す。注記（12〜13 行）を直す。

**注意**: Positive の危険は「赤の塗り」になる（Flutter 版が赤の文字だったなら見た目が違う。**要判断**）。古い dialog.actor でも効く。

---

### M-06 手作りの回転 → `ProgressSpinner`（または `Dialog.ShowProgress`）

**今の場所**: `App/Screens/Options/OptionsOverlayScreen.cs:177`（`ApplyProgress(float dt)`: 開発用の島の `ProgressRow`〈51 行〉を出し、子 `Ring`〈52 行。progress_ring.actor の値の輪〉の
`CanvasTransform.Rotation` を毎フレーム 300°/秒〈`SpinnerDegreesPerSecond` 90 行・`_spinnerAngle` 114 行〉で回して `Redraw.Request()`）。`OnWidgetUpdate` の 141 行から毎フレーム。
中身はサンプルデータの作成・削除の進捗（`SampleDataFlow`）。

**置き換え先**（`Widgets/ProgressSpinner.cs`。プレハブ `templates/ui/prefabs/progress_spinner.actor`）:

```csharp
public sealed class ProgressSpinner : UiWidget
{
    public float Size;            // 弧の外側の直径（0 以下 = size.spinner 36）
    public float Thickness;       // 0 以下 = size.spinner_thickness 4
    public bool Spinning = true;
    public void SetSpinning(bool spinning);
    public void SetSize(float size);
    public void SetThickness(float thickness);
}
// 札で出すなら（Navigation/Dialog.cs）: ボタンなし・幕のタップと戻るでは閉じない
public static DialogHandle? Dialog.ShowProgress(string message, string title = "");
// DialogHandle.SetMessage(string)・Close(DialogResult[, bool animate])・Dismiss()
```

**手順**（今の置き場のまま）:

1. M-00 で `progress_spinner.actor` を写す。`options_overlay.actor` の `ProgressRow/Ring` を `progress_spinner.actor` のインスタンスに替える（名前は `Ring` のままでよい。大きさは `Size` で今の輪に合わせる）。
2. `ApplyProgress` を「行を出す・文字を当てる」だけにし、`_spinnerAngle`・`SpinnerDegreesPerSecond`・`Rotation` の書き込み・毎フレームの `Redraw.Request()` を消す
   （スピナーは見えている間だけ自分で `Redraw.KeepAlive` し、隠すと 0.1 秒で止まる）。

札にするなら `SampleDataFlow` で `Dialog.ShowProgress(文)` を開き、進捗で `SetMessage`、終わったら `Close(DialogResult.Positive)`（覆いの上のダイアログの帯に出る。見た目が変わるので**要判断**。
古い dialog.actor ではスピナーの無い札になるので M-00 が先）。

**注意**: 今の輪は `size.ring_thickness`（`wop_base.json` 11 行の 10。起床確認の外周の輪と共有）で描いている。スピナーへ替えればこの共有は解ける
（輪のまま残すなら `ProgressRing.Thickness`〈0 以下 = テーマ〉で部品ごとに太さを書ける）。

---

### M-07 `AppNavigator.DismissModals` → `ModalHost.CloseAll(animate: false)`

**今の場所**: `App/Shell/AppNavigator.cs:653`（`private static void DismissModals()`: `ModalKindsToDismiss`〈131 行。Dialog → Sheet → Overlay〉ごとに `host.HandleBack(kind)` を
`MaxModalDismissalsPerKind`〈134 行。8〉回まで繰り返す）。呼ぶのは `ApplyShow` の 606 行（鳴動画面・結果画面を出す直前。出すかは `Domain/Routing/AlarmSlotStep.cs` の `DismissModals`）。

**置き換え先**（`Navigation/ModalHost.CloseAll.cs`）:

```csharp
public int ModalHost.CloseAll(bool animate = true);                  // ダイアログ → シート → 覆い（ポップアップを含む）・同じ種類は新しい順。戻り値 = 閉じた数
public int ModalHost.CloseAll(ModalKind kind, bool animate = true);
```

**手順**: 606 行を `if (step.DismissModals) ModalHost.Current?.CloseAll(animate: false);` にし、`DismissModals` と 131・134 行を消す。

**注意（挙動の違い）**:

- 戻るでは閉じない面（`CancelableByBack = false`・進捗の札）も閉じる（今は戻るで閉じられず、8 回で諦めて残っていた）。
- `animate: false` なら出る動きなしでこの呼び出しの中で閉じ、手札の `Closed`・`Completed` もこの中で閉じる順に届く。結果は **`DialogHandle` なら `DialogResult.Dismissed`、
  ほかの手札は null**（2026-10-03。レビュー #23。`ShowDialog` 以外の帯が Dialog の面も null）。手札の知らせの中で開いた面は閉じずに残る。
- 全画面の下へ回した覆い（今の `ModalParking`。M-11 の後は SEED の `Park`）も閉じる。`_profileOverlay`・`_optionsOverlay` は手札の `IsClosed` で見ているので、そのままで合う見込み（推論）。

---

### M-08 `TopSheetFit` → `OverlayOptions.FillHeight`・`FillBottomMargin`（動きなしは `Animate = false`）

**今の場所**: `App/Screens/Common/TopSheetFit.cs:11`（`ContentHeight(GameObject content, float bottomMargin)` 22 行: 覆いの高さ − 上の安全領域 − つまみの行〈`Panel/HandleRow`〉−
下の余白 − 下の安全領域）。呼ぶのは `App/Screens/Options/OptionsOverlayScreen.cs:202`（`FitLayout` で中身の根の `CanvasLayoutItem.PreferredSize.y` へ書く。下の余白は
`SheetBottomMargin` = 12〈78 行〉）。覆いを開くのは `AppNavigator.OpenOverlay`（294・295 行。`OpenOptionsOverlay` の 375 行から）。プロフィールは今は覆いでなくポップアップ（M-10）。

**置き換え先**（`Navigation/Model/OverlayOptions.cs`）:

```csharp
public const float ThemeFillMargin = -1f;
public bool FillHeight { get; init; }                           // 中身の根の CanvasLayoutItem.PreferredSize.y を毎フレーム SheetMath.OverlayFillHeight に合わせる
public float FillBottomMargin { get; init; } = ThemeFillMargin; // 負 = テーマの space.m
public bool Animate { get; init; } = true;                      // false = 中身が落ち着いたら降りた姿で出す
```

**手順**:

1. `OpenOverlay` を `TopSheet.Show(new OverlayOptions { ContentPrefab = contentPrefab, FillHeight = true, FillBottomMargin = 12f })` にする
   （Wake or Pay のテーマの `space.m` が 12 とは限らないので値で渡す）。
2. 同じ変更で `OptionsOverlayScreen.cs` の 202〜207 行（TopSheetFit の if ブロック）と `TopSheetFit.cs` を消す（両方が書くと、余白が違えば毎フレーム取り合う）。
   `OverlayPlane.FindNode` を使うのは TopSheetFit だけ。`OverlayPlane.HandleOf`（OptionsOverlayScreen.cs:125）が残るので `OverlayPlane` は残す。
3. （任意）覆いを開き直して全画面の下で降りる動きを見せている所（`ReturnFrom` の 2 つ目の場合）は `Animate = false` にできる。M-11 で決める。

**注意**: 中身の根に `CanvasLayoutItem` が要る（無ければ警告して中身の高さのまま）・中身の根は `fill_height` にしない。合わせた高さが 1 フレーム落ち着くまで覆いを見せない。

---

### M-09 `NodeVisibility.IsShown` → `NavigatorRegistry.IsActiveNode`（追加の項目）

**今の場所**: `App/Widgets/NodeVisibility.cs:21`（`IsShown`: 自分と祖先の `Visible` をたどる。8〜9 行に「`IsActiveNode` が internal で使えないための代わり」）。
呼ぶのは `App/Screens/Garden/GardenScreen.cs:139`・`App/Screens/Garden/GardenEditScreen.cs:133`（庭の住人を歩かせるか）。

**置き換え先**（`Navigation/NavigatorRegistry.cs`。2026-10-02 に公開）:

```csharp
public static bool NavigatorRegistry.IsActiveNode(GameObject node);   // 見えていて、祖先の画面のスタックのいちばん上の段の中にあるか
```

**手順**: `NodeVisibility.IsShown(gameObject)` を `NavigatorRegistry.IsActiveNode(gameObject)` にし、`NodeVisibility.cs` を消す。

**注意（挙動の違い）**: `IsActiveNode` は「祖先の画面の枠がスタックのいちばん上か」も見るので、透ける画面（`Opaque = false`）の下・全画面を積み始めた直後（並びは操作の時点で変わる）から
false になる見込み（推論）。今の `IsShown` は落ち着いて下の画面が隠れるまで true。覆い・シート・ダイアログに隠れているかはどちらも見ない。
「シェルが全画面に覆われても、タブの中の画面には `OnScreenHidden` が届かない」（backlog W4 (1) の後半）は残り。

---

### M-10 プロフィールの中央のポップアップ → `SEED.UI.Popup`

**今の場所**:

| 回避 | 場所 |
|---|---|
| 自前の面 | `App/Screens/Common/PopupPlane.cs:33`（`PopupPlane : ModalPlane`。札の大きさ `FitCard` 277・準備 `Prepare` 316・動きなしで閉じる `CloseImmediately` 129）。プレハブ `assets://app/prefabs/center_popup.actor`（`AppNavigator.PopupPlanePrefab` 58 行） |
| 中身の高さの約束 | `App/Screens/Common/IPopupContent.cs:7`（`float NaturalHeight { get; }`）。実装は `App/Screens/Profiles/ProfileOverlayScreen.cs:32` だけ |
| 開き方 | `App/Screens/Common/PopupRequest.cs:8`（`record PopupRequest(bool Instant, string CloseLabel)`。`OverlayOptions.Args` で面へ渡す） |
| 欄の一時の差し替え | `AppNavigator.OpenPopup`（305〜323 行: `host.OverlayPrefab` を `PopupPlanePrefab` へ替えて `ShowOverlay`、finally で戻す）・`OpenProfilePopup` 430・`CloseImmediately` 508 |
| 見た目のトークン | `wop_base.json` の `app.popup.*`（129 行〜。margin・max_height・inset・radius・frame・frame_image・close_*・scrim） |

**置き換え先**（`Navigation/Popup.cs`・`Navigation/Model/PopupOptions.cs`・`Navigation/IPopupContentSize.cs`・`Navigation/ModalHost.cs`。namespace `SEED.UI`）:

```csharp
public static ModalHandle? Popup.Show(PopupOptions options);                      // ModalHost.Current の ShowPopup（無ければ null・警告）
public ModalHandle ModalHost.ShowPopup(PopupOptions options);                     // 面のプレハブ = PopupPrefab（既定 "assets://ui/prefabs/popup.actor"）
public ModalHandle ModalHost.ShowPopup(PopupOptions options, string planePrefab);
public sealed class PopupOptions   // どれも { get; init; }
{
    public string ContentPrefab;  public object? Args;
    public bool DismissOnScrimTap = true;  public bool CancelableByBack = true;
    public bool Animate = true;  public bool ShowCloseButton = true;
    public float Width;           // 0 以下 = 覆う領域の幅 − size.popup_margin × 2（上限 size.popup_max_width）
    public float ContentHeight;   // 0 以下 = 中身から
    public ModalKind Kind = ModalKind.Overlay;
}
public interface IPopupContentSize { float PopupContentHeight { get; } }   // 札の内側の余白を含まない。0 以下 = まだ分からない（分かるまで見せない）
// 動きなしで閉じる: ModalHandle.Close(object? result, bool animate) に animate: false
```

**手順**:

1. M-00 で `popup.actor` を入れる（無いと面のスクリプトが始まらず、60 フレームでエラーを出して手札を閉じる）。
2. `ProfileOverlayScreen : UiScreen, IPopupContent` を `IPopupContentSize` にし、`NaturalHeight` を `PopupContentHeight` に改名する（意味は同じ）。
3. `OpenPopup` を次にする（欄の差し替え・`PopupRequest`・`DismissGesture = None` は要らない。SEED の Popup は指で払っては閉じない）:

   ```csharp
   public ModalHandle? OpenPopup(string contentPrefab, bool instant) =>
       Popup.Show(new PopupOptions { ContentPrefab = contentPrefab, Animate = !instant });
   ```

4. `CloseImmediately`（508 行）の PopupPlane の分岐を `modal.Close(null, animate: false)` にする（覆いも同じ口で動きなしにできる）。
5. 見た目: SEED のトークン（`size.popup_margin` 16・`size.popup_max_width` 560・`size.popup_padding` 8・`ratio.popup_max_height` 0.8・`radius.popup`・`opacity.dialog_scrim`）を
   `wop_base.json` に足して `app.popup.*` の値を写す。装飾の枠（`Frame`・`app.popup.frame_image`）は SEED の popup.actor に無いので、要るなら写した popup.actor の `Card` に
   「並べない」子として足す（Card は縦の CanvasStack なので、並べないと中身の下に積まれる）。× の字は popup.actor の `CloseButton/Label` の文字（`PopupOptions` に欄は無い）。
6. `PopupPlane.cs`・`IPopupContent.cs`・`PopupRequest.cs`・`center_popup.actor`・`PopupPlanePrefab` を消す。デバッグの命令（`nav,probe` の `popup=`、
   ノード `CenterPopup/Card/CloseButton`。README の W3-6 の節）を popup.actor のノードの名前に合わせる。

**注意**:

- 札の大きさの式は SEED の `PopupCardMath`（幅は上記・高さの上限は min(安全領域の高さ × `ratio.popup_max_height`, 安全領域の高さ − 余白 × 2)。ui_navigation.md §3.6）で、
  今の `FitCard` とは違う所がある。撮影で比べる。
- 帯は既定で覆い（`Kind = ModalKind.Overlay`）。戻るの層・`CloseAll` の扱いは今と同じ。
- M-11（Park）と続けて行う（下へ回す面の主な相手がこのポップアップ）。M-10 を先にし、M-11 では `Popup.Show` の手札を Park する。

---

### M-11 `ModalParking`・`ModalUnderPage`・`ParkedPagePreview` → `ModalHost.Park`・`Unpark`・`IsParked`

**今の場所**:

| 回避 | 場所 |
|---|---|
| 置き場 | `App/Shell/ModalParking.cs:20`（戻るの層 `ParkedBackOrder`〈25 行。(Sheet + Overlay) / 2〉・束ねて毎フレーム回す。`Park` 62・`FindCovering` 77・`Pump` 81・`HandleBack` 117） |
| 1 つの面 | `App/Shell/ModalUnderPage.cs:31`（面の根の底上げの書き換え・元へ戻す・フォーカスの範囲を後ろ／前へ・見せずに閉じる。`BeginReveal` 89・`Lower` 148・`Restore` 169） |
| 予測型の戻るのプレビュー | `App/Shell/ParkedPagePreview.cs:20`（`IBackPreviewTarget`。9 行に「SEED の StackPreview がスクリプトから引けない」） |
| 全画面の約束 | `App/Shell/IParkedPage.cs:8`（`HandleParkedBack()`）。`App/Screens/Common/FullScreenBase.cs:24` が実装し、全画面 13 個（設定・上限金額の最大値・鳴らないときは・プロフィール編集・アラーム編集・模様替え・種屋・ペナルティ履歴・起床の履歴・数値／選択肢／音／曜日のサブ画面）が継ぐ |
| 呼ぶ所 | `App/Shell/AppNavigator.cs`: 生成 163・`PushOver` 455（Park 461）・`ReturnFrom` 479（`FindCovering` 484・Park〈Revealing〉502）・`Pump` 521・`Dispose` 525・`DebugParking` 203 |
| 決め方（残す） | `Domain/Routing/ModalParkRules.cs:22`・`ModalParkStage`・`ModalParkStep`・`ModalParkFacts`（SEED を使わない純粋な状態機械） |

**置き換え先**（`Navigation/ModalHost.Parking.cs`・`NavigatorRegistry.cs`・`ScreenStack.BackPreview.cs`）:

```csharp
public bool ModalHost.Park(ModalHandle handle, ScreenStack stack, ScreenHandle page);   // 全画面を積んだ直後に呼ぶ。page が閉じたら自動で Unpark
public bool ModalHost.Unpark(ModalHandle handle);
public bool ModalHost.IsParked(ModalHandle handle);
// 自前の戻るの層から使う口（ui_navigation.md §5.2）
public static bool NavigatorRegistry.DispatchBack();
public static bool NavigatorRegistry.WouldHandleBack();
public static IBackPreviewTarget? NavigatorRegistry.BackPreviewTarget();
public bool ScreenStack.HandleBack();
public bool ScreenStack.WouldHandleBack();
public IBackPreviewTarget? ScreenStack.BackPreviewTarget { get; }
public int ScreenStack.IndexOf(ScreenHandle handle);   // 積まれていない・外れた画面は −1
```

**手順**:

1. `PushOver`: `PushFullScreen` の後の `_parking.Park(…)` を `ModalHost.Current?.Park(modal, root, page)` にする（積んだ時点で並びは変わっているので段が分かる）。
2. 戻る: SEED の Park で回した面は戻るの層で数えず、予測型の戻るの相手にもならない。戻るは画面のスタックへ届いて全画面が下り、`page` の手札が閉じると自動で `Unpark` する。
   予測型の戻るのプレビューもスタックのもの（上の画面の枠が縮み、下が見える）になるので、`ParkedPagePreview`・戻るの層 `ParkedBackOrder`・`IParkedPage.HandleParkedBack` は要らなくなる。
   全画面の戻るに「覆いへ戻る」の結果（`BackToOptions`・`ProfileEditScreen.BackToProfile`）を付けたいなら、`FullScreenBase` の `OnBackPressed` で `ReturnToOptions(this)`・`ReturnToProfile(this)` を呼んで
   true を返し、`WouldConsumeBack` も同じ条件で上書きする（上書きしないと「受ける」とみなされ、根でも Android の予測型の戻るが出ない。scripting_api.md の UiScreen の例）。
3. `ReturnFrom`（479 行）:
   - 「1. 下に回した覆いがある」: 覆いは全画面の下で見えているので `page.Close(result)` だけでよい（`BeginReveal`・`CancelReveal` は要らない）。回しているかは `ModalHost.IsParked(手札)`。
   - 「2. 無い（鳴動で閉じられた）」: 開き直した手札を **`page.Close(result)` より前に** `Park(modal, root, pageHandle)` する。`Park` は積まれている画面にしか回せず
     （`IndexOf(page) < 0` なら警告して false）、`Close` は並びをその場で変えるため（コードを読んだ推論）。面のスクリプトが動く前の Park は、面が始まったときに当たる。
     閉じられなかったら `modal.Close(null, animate: false)`。
4. `_parking.Pump()`（521）・`Dispose`（525）・`ModalParking.cs`・`ModalUnderPage.cs`・`ParkedPagePreview.cs`・`IParkedPage.cs`（と FullScreenBase の実装）を消す。
   `DebugParking`（203）とデバッグの命令の `park=` を `ModalHost.IsParked` で書き直す。
5. 覆いへ戻るか・見せずに閉じるかの決め方（`ModalParkRules` など）はアプリに残す。SEED の Park で要らなくなる段（`ModalParkStage` の Covering／Revealing の見せ方）は状態機械の側で整理する（**要判断**）。

**注意**:

- 底上げは「全画面の枠の実効の底上げ − 段の値 × 0.5」に書き換わる。下の画面の中の表示のレイヤーは段の値の半分より小さく保つ（ui_navigation.md §3.7・§6）。
  全画面の上に開いたダイアログ・シートは従来どおり先に戻るを受ける。
- 全画面を `Replace` で替えると古い手札が閉じた時点で戻る（残すなら新しい手札で Park し直す）。`PopToRoot` で覆いを見せたくなければ先に `Close(null, false)`。
- 「見せずに閉じる」（今の `faces` を隠して閉じる）は `Close(null, animate: false)`。
- 予測型の戻るの見え方は SEED のスタックのプレビューになる。今の `ParkedPagePreview` は「下の画面を視差の位置に置いて跳びを避けた」写しなので、Android の手ぶりで同じに見えるかを実機で比べる。
- `Park` は覆いの帯の面を想定して作った（シート・ダイアログを回すのも同じ規則で動く見込み・未確認）。
- `IParkedPage` を外すと全画面 13 個の戻るの経路が変わるので、13 個の戻る（端からの手ぶり・3 ボタン・画面の ←）を確かめる。

---

### M-12 `PrebuiltContent` → `ScreenStack.Prewarm`（または `Push(GameObject, …, ReturnToParent)`）

**今の場所**:

| 回避 | 場所 |
|---|---|
| 作り置き | `App/Shell/PrebuiltContent.cs:60`（`PrebuiltContent<TBody>`。`AttachTo` 137・`Tick` 153・`Lend` 177・`Return` 207・置き場 `assets://app/prefabs/prebuilt_parking.actor`〈63 行〉）と段階 `PrebuiltStage`（9 行） |
| 中身の約束 | `App/Shell/IPrebuiltBody.cs:14`（`Root`・`IsWarm` 23・`Tick()`・`OnReturned()`） |
| 作るもの | アラーム編集画面の**中身だけ**（`AlarmEditBody`。プレハブ `AlarmPrefabs.EditBody` = `assets://alarms/prefabs/alarm_edit_body.actor`。`App/Boot/AppServices.cs:76`）。画面そのもの（`AlarmPrefabs.Edit` = `assets://alarms/prefabs/alarm_edit.actor`。上のバー・スクロールの窓・保存のボタン）は毎回プレハブから積む |
| 作り始めの判断 | `AppServices.MayPrebuild`（204 行。起動から `PrebuildDelaySeconds`〈33 行。1 秒〉・根のスタックが動いていない・鳴動画面を出していない）・`Tick` は 193 行・置き場は `App/Boot/AppBoot.cs:79` |
| 貸し借り | `App/Screens/Alarms/AlarmEditScreen.cs:146`（`Lend` でスクロールの窓 `BodyScrollPath` へ）・244（`Return`。`OnScreenExit` の中） |
| 確かめの命令 | `App/DevTools/NavDebugCommands.cs:104`・142〜143（`nav,prebuild[,on\|off]`） |

**置き換え先**（`Navigation/ScreenStack.Prewarm.cs`・`ScreenStack.Content.cs`・`Model/PrewarmOptions.cs`・`Model/ScreenContentSource.cs`・`UiScreen.cs`）:

```csharp
public bool ScreenStack.Prewarm(string prefab, PrewarmOptions? options = null);    // 同じプレハブの作り置きがあれば false
public bool ScreenStack.IsPrewarmed(string prefab);
public PrewarmStage? ScreenStack.GetPrewarmStage(string prefab);                  // Waiting / Building / WarmDrawing / Ready / Lent / Discarded
public bool ScreenStack.DiscardPrewarm(string prefab);
public sealed class PrewarmOptions   // どれも { get; init; }
{
    public PrewarmMode Mode = PrewarmMode.Once;   // Once / Refill / Reuse
    public int WarmDrawFrames;                    // 温め描き（0 = しない）
    public bool SafeArea = true;
}
protected internal virtual bool UiScreen.IsPrewarmReady => true;                   // 重い準備を Update で続ける画面は済むまで false
public ScreenHandle? ScreenStack.Push(GameObject content, NavTransition? transition = null, object? args = null,
                                      ScreenOptions? options = null, ScreenContentRelease release = ScreenContentRelease.Destroy);
public ScreenHandle? ScreenStack.Replace(GameObject content, NavTransition? transition = null, object? args = null,
                                         ScreenOptions? options = null, ScreenContentRelease release = ScreenContentRelease.Destroy);
public enum ScreenContentRelease { Destroy = 0, ReturnToParent = 1 }
```

**2 つの道（要判断）**:

| 道 | 中身 | 向き・不向き |
|---|---|---|
| A. 画面ごと作り置き（推奨） | `alarm_edit.actor` に中身を入れた形で `Prewarm(…, new PrewarmOptions { Mode = PrewarmMode.Reuse, WarmDrawFrames = 2 })`。次の `PushFullScreen` で枠ごと借りる | 画面と中身を分けなくてよい。Reuse は外れるたびに `OnScreenExit`、使うたびに `OnScreenEnter` が届くので、前のアラームの状態（島・トグル・時刻・スクロールの位置）を Enter で必ず作り直す |
| B. 中身だけ作り置き（今の形に近い） | アプリの置き場で作った中身を `Push(body, NavTransition.Push, args, options, ScreenContentRelease.ReturnToParent)` | `Push(GameObject)` で積むのは画面全体なので、今の「軽い画面 ＋ 窓の中の中身」を作り直す必要がある（中身の根に UiScreen を付け、上のバー・窓・保存のボタンも中身に含める） |

**手順（A）**:

1. 編集画面のプレハブ（`alarm_edit.actor`）に中身（`alarm_edit_body.actor`）を入れる。
2. `MayPrebuild` が true になったときに 1 回 `root.Prewarm(AlarmPrefabs.Edit, new PrewarmOptions { Mode = PrewarmMode.Reuse, WarmDrawFrames = 2 })`。SEED も「出入りの動きの無いフレーム」を
   待って作り始めるが、鳴動中は作らないなどアプリの事情は呼ぶ時機で決める。
3. `AlarmEditScreen`: `Lend`・`Return` を消し、`AlarmEditBody.IsWarm`（`AlarmEditBody.cs:92`）の条件を `protected override bool IsPrewarmReady` へ移す。`OnScreenEnter(args)` で毎回すべての表示を
   作り直し、スクロールの位置を戻す。
4. `PrebuiltContent.cs`・`IPrebuiltBody.cs`・`prebuilt_parking.actor`・`AppServices.AlarmEditBodies`・`AppBoot.cs:79` を消し、`nav,prebuild` を `IsPrewarmed`・`GetPrewarmStage`・`DiscardPrewarm` で書き直す
   （off で比べるなら `DiscardPrewarm` してから `Prewarm` しない）。

**注意**:

- 温め描き（`WarmDrawFrames`）は段 0 の画面より 1 段奥のレイヤーで描くので、**根の画面が不透明な前提**（RootStack の根のシェルが透けるなら使わない）。
- 作り置きはプレハブ 1 つにつき 1 つ。同じ画面を 2 つ積むと 2 つ目はプレハブから作る。今の PrebuiltContent の「その場で作った中身を作り置きとして引き取る」に当たるものは無い（Once / Refill / Reuse のどれか）。
- 隠した作り置きの部分木も毎フレームの `UI/2D スクリーン座標収集`・`UI/ポインタイベント` の走査に乗る（backlog W3-6 (2)。今の置き場と同じ）。
- 中身の出所の順は「渡された中身 > 置いてある根 > 作り置き > プレハブ」（`ScreenContentPlan`）。
- 編集画面の `HiddenScrollKeeper`（§4。置き換えない）と一緒に動くことを確かめる（Reuse で戻した画面の 2 回目の Enter と、覆われたときの範囲の固定）。
- B の `Push(GameObject)` の約束: 同じ中身をほかの段が持っていると null・警告（2026-10-03。レビュー #22）。元の親は枠へ移すフレームに読み、無ければシーンの根へ移して隠す（#21）。
  中身を作ったのと同じフレームに積んでよい。作り直せないので `KeepState` は常に true として扱う。

---

### M-13 `StringTable` → `SEED.Localization`（`L10n`・`LocalizedText`・`LocalizedLabel`）

**今の場所**:

- `Domain/Text/StringTable.cs:21`（`Get` 83・`Format(string key, params (string Name, string Value)[] args)` 96・`Substitute` 105。無いキーは `⟦キー⟧`〈24 行〉を返して `MissingKeys` に記録）。
- 表は `assets/common/data/strings.ja.json`。`Domain/Catalogs/GameData.cs:104` で読み、`App/Boot/AppServices.cs:92`（`Strings`）で配る（`App/Boot/AppBoot.cs:192` の `TryLoadStrings` も）。
- `.Get(` の呼び出しは 62 ファイル 206 か所。**Domain の純粋なコードも StringTable を受け取っている**（`tests/DomainTests/DomainTests.csproj` は `assets/scripts/Domain/**` を SEED なしでコンパイルする）。

**置き換え先**（`scripting/src/Api/Localization/`。namespace `SEED.Localization`）:

```csharp
public static class L10n
{
    public const string Changed = "l10n.changed";                     // 切り替え・読み直しの知らせ（引数は言語のコード）
    public const string DefaultRoot = LocalePaths.DefaultRoot;        // "assets://locale"
    public static string Get(string key);
    public static string Get(string key, params (string name, object? value)[] args);    // {name}・{name:書式}・{{ }}
    public static string Format(string key, params object?[] args);                      // {0}・{1}
    public static string Plural(string key, long n, params (string name, object? value)[] args);
    public static bool Has(string key);
    public static bool TryGet(string key, out string text);
    public static IReadOnlyCollection<string> MissingKeys { get; }
    public static void Configure(string root);
    public static bool SetLanguage(string code, bool save = true);
}
// UI の文字: ScriptComponent「SEED.Localization.LocalizedText」（同じアクタの Text）・「SEED.Localization.LocalizedLabel」（Button などの部品）。欄 Key・Args
// スクリプトから: LocalizedBinding.Of<LocalizedText>(actor)?.SetArg(name, value) / SetCount(n) / SetKey(key)
```

**手順**（localization.md §11 の手順に、Domain の制約を足したもの）:

1. `strings.ja.json` を `assets/locale/ja.json` へ写し、`assets/locale/index.json`（`{"default":"ja","languages":[{"code":"ja","name":"日本語"}]}`）を置く。
   データの形（入れ子を `.` でつなぐ・`_` で始まる鍵は説明・`{名前}`・`{{ }}`）は同じなので中身は変えない。既定の置き場なので `Configure` は要らない。
2. **Domain は L10n を直接呼ばない**（DomainTests が SEEDScripting を参照しない）。道は 2 つ（**要判断**）:
   - (a) StringTable の形（`Get`・`Format`）を Domain のインターフェースにし、App 側に L10n へ委ねる実装を置く。DomainTests は今の StringTable（JSON から作る）を使い続ける。
     `AppServices.Strings` の型を替えるだけで、App の呼び出しの多くはそのまま動く。
   - (b) Domain が文を作るのをやめ、キーと差し込みの値を返して App 側で `L10n.Get` で引く（変更の量が大きい）。
3. App 側で文を自分で持つ所は、`this.On(L10n.Changed, (string _) => …)` で作り直す（引数なしの `() => …` は届かない）。固定の文字は `LocalizedText`・`LocalizedLabel` へ移せる
   （ダイアログ・トーストなど部品の API へ渡す文字は `L10n.Get` で引いて渡す。ダイアログのボタンに LocalizedLabel を付けても `Dialog.Show` が上書きする）。
4. 欠けの印は `⟦key⟧` → `[key]`（開発中）／`key`（配布用。`MissingKeyPolicy`）。「無いキー 0 件」は DomainTests では今の StringTable、Play では `L10n.MissingKeys` で見る。
5. 日付・時刻の書式は数字だけの書式にする（Android は不変文化。localization.md §9）。

**注意**:

- 差し込みの値は StringTable が `(string, string)`、L10n が `(string, object?)`。組のリテラルを `params` に並べている所はそのまま書けるが、`(string Name, string Value)[]` の配列を
  渡している所は写し替えが要る。数を文字列にせず渡すと今の言語の文化で書かれる（`{amount:N0}`）。
- スクリプトの `OnStart` の順は決まっていない。表を既定の置き場に置けば、起動の最初の `L10n.Get` から引ける。
- 英語を足すまでは見た目は変わらないので、段 3 は「同じ文が出る」ことの確かめになる。

---

### M-14 画面ごとの `Render()` と `StateVersion` の見比べ → `SEED.Binding`

**今の場所**:

- `App/Boot/AppServices.cs:161`（`StateVersion`）・224（`OnStateChanged` で `StateVersion++`・`Redraw.Request()`。22〜23 行に「Riverpod の購読の代わり」）。
- 各画面は `OnWidgetUpdate` で `_renderedVersion != services.StateVersion` を見比べ、違えば `Render(...)` で全部の文字・表示を書き直す。`Render` は 13 か所:
  `App/Screens/Shop/ShopScreen.cs:95`・`App/Shell/AppHeader.cs:87`・`Activity/WakeHistoryScreen.cs:66`・`Activity/ActivityScreen.cs:117`・`Garden/GardenScreen.cs:196`・
  `Activity/PenaltyHistoryScreen.cs:100`・`Ringing/ResultScreen.cs:124`・`Profiles/ProfileOverlayScreen.cs:111`・`Ringing/WakeChecks/MathCheckView.cs:104`・`Ringing/RingingScreen.cs:137`・
  `Options/OptionsOverlayScreen.cs:147`・`Onboarding/OnboardingScreen.cs:164`・`Alarms/AlarmListScreen.cs:175`（`AppHeader` 以外は `App/Screens/` の下）。
- 部品の始まりを待つのは `App/Widgets/WidgetBinder.cs:106`（`SyncToggle`）と `ButtonBinder`。

**置き換え先**（`scripting/src/Api/Binding/`。namespace `SEED.Binding`）:

```csharp
public sealed class Observable<T> : IReadOnlyObservable<T>
{
    public Observable(T initial = default!, IEqualityComparer<T>? comparer = null);
    public T Value { get; set; }                 // 等しければ何もしない。違えば即時・同期で知らせる
    public IDisposable Subscribe(Action<T> handler);
    public void Notify();
}
public static class Computed { public static Computed<T> From<TA, T>(IReadOnlyObservable<TA> a, Func<TA, T> compute); /* 依存 2・3 個の版も */ }
public static partial class Bind
{
    public static IDisposable Text<T>(SEEDScript owner, Text text, IReadOnlyObservable<T> source, Func<T, string> format);
    public static IDisposable Text<T>(SEEDScript owner, Text text, IReadOnlyObservable<T> source, string l10nKey, string argName);   // L10n 版（差し込み 1 つ）
    public static IDisposable Visible<T>(SEEDScript owner, GameObject node, IReadOnlyObservable<T> source, Func<T, bool> visible);
    public static IDisposable Toggle(SEEDScript owner, GameObject node, Observable<bool> source);     // ノードから結ぶと部品の OnStart を待つ
    public static IDisposable Slider(SEEDScript owner, GameObject node, Observable<float> source);
    public static IDisposable List<T>(SEEDScript owner, ListView list, ObservableList<T> items, Action<GameObject, T, int> bindRow);
    public static Action<GameObject, int> RowBinder<T>(ObservableList<T> items, Action<GameObject, T, int> bindRow);
    public static IDisposable To<T>(SEEDScript owner, IReadOnlyObservable<T> source, Action<T> apply);
}
```

**手順**（1 画面ずつ。ui_binding.md §7 の手順を Wake or Pay の形に合わせたもの）:

1. **観測値は App 側に置く**（Domain の `AppState` は SEED を参照しない純粋な型で、状態の遷移は新しい `AppState` を作る形）。`AppServices` に `Observable<AppState>` を 1 つ足し、
   `OnStateChanged` で `Value = state` にする（`StateVersion` は移し終えるまで残してよい）。
2. 画面の `Render` が読む値を `Computed.From(観測値, s => …ViewBuilder.Build(…))` のような観測値にし、`OnWidgetStart` で `Bind.Text(this, …)`・`Bind.Visible(this, …)` を 1 回だけ結ぶ。
   `_renderedVersion` の見比べと `Render` を消す。
3. `WidgetBinder.SyncToggle` は `Bind.Toggle(this, node, 観測値)` にする。
4. **名前の衝突**: `Bind` という名前のメソッドがあるクラスの中では `Bind.Text(…)` がメソッドを指して CS0119 になる。Wake or Pay では `App/Screens/Alarms/AlarmRow.cs:193`・
   `App/Screens/Common/PopupPlane.cs:253`（M-10 で消える）・`App/Screens/Ringing/WakeChecks/WakeCheckInputSlot.cs:145`・`App/Widgets/DigitsFieldSlot.cs:71`・`App/Widgets/NumberFieldSlot.cs:82` にある。
   その中では `SEED.Binding.Bind.Text(…)` と書くか `using BindTo = SEED.Binding.Bind;`。

**注意**:

- 比べ方: `Observable<AppState>` は既定で `EqualityComparer<AppState>.Default`（record なら値の等価）。保存のたびに必ず知らせたいなら参照で比べる比べ方を渡す（推論・未検証）。
- `AlarmListScreen.cs:147〜151` は状態の版のほかに権限の版（`PermissionMonitor.Version`）と時計の分替わりも見ている。これらは別の観測値にする。
- レイアウトで決まるもの（`WrappedText.Fit`・`StackFit`・M-08 の後の残り）は値の変化ではないので、今までどおり毎フレーム合わせる。
- `Bind.Text` の L10n 版は差し込み 1 つ・複数形なし（2 つ以上は `Computed` で文を作って `Bind.Text(text, IReadOnlyObservable<string>)` へ）。
- 一覧（`Bind.List`）は `ListView` を作るときの bind を `Bind.RowBinder(一覧, bindRow)` にする（作った後に変えられない）。

---

### M-15 自前の `static Current`・行の登録簿 → `SEEDScript.FindInstance`・`GetScript`

**今の場所**:

| 場所 | 中身 | 置き換え |
|---|---|---|
| `App/Screens/Garden/GardenScreen.cs:93`・`App/Screens/Garden/GardenEditScreen.cs:82`（`public static … Current`。104・99 行で入れ、114・107 行で外す） | デバッグの命令（`App/DevTools/GardenDebugCommands.cs:139`・`141`・`166`）から引くだけ | `SEEDScript.FindInstance<GardenScreen>()`・`FindInstance<GardenEditScreen>()` |
| `App/Screens/Alarms/AlarmRow.cs:119`・`122`（`static Dictionary<(uint, uint), AlarmRow> Rows`・`…AlarmRowModel> Models`。242 行で登録） | 行のアクタ → 行のスクリプト・行のモデル | `row.GetScript<AlarmRow>()`（OnStart 前でも引ける）。モデルは行のスクリプトの欄に持てる（任意） |

**置き換え先**（`scripting/src/SEEDScript.Scripts.cs`・`Api/GameObject.Scripts.cs`）:

```csharp
public static T[] SEEDScript.Instances<T>() where T : SEEDScript;      // 生存中の全部（生成順・その時点の写し）
public static T? SEEDScript.FindInstance<T>() where T : SEEDScript;    // 最初の 1 つ（無ければ null）
public T? GameObject.GetScript<T>() where T : SEEDScript;               // このアクタの最初の 1 つ（OnStart 前も返す）
public T[] GameObject.GetScripts<T>() where T : SEEDScript;
public bool GameObject.HasScript<T>() where T : SEEDScript;
public bool GameObject.TryGetScript<T>(out T script) where T : SEEDScript;
public T? GameObject.GetScriptInChildren<T>(bool includeSelf = true) where T : SEEDScript;
public T? GameObject.GetScriptInParent<T>(bool includeSelf = true) where T : SEEDScript;
```

**注意**:

- `Instances`・`FindInstance` に載るのは OnStart を迎えたもので、同じフレームに OnStart を迎えるスクリプト同士では先のものから後のものが見えない（今の手書きの Current と同じ）。
  毎回配列を作るので毎フレーム大量に呼ばない。
- 置き換えないもの: `AppServices.Current`（`App/Boot/AppServices.cs:41`。スクリプトでない）・`WakeCheckInputSlot.Current`（`WakeCheckInputSlot.cs:35`）・`WakeCheckViewBase.Active`
  （`WakeCheckViewBase.cs:26`。どちらもスクリプトでなく「今使っている 1 つ」の意味）・`GameObject.Find("RootStack")`・`Find("TabHost")`（ScreenStack は根とタブで複数あるので FindInstance では選べない）。
- `UiWidget.Of<T>(actor)` は OnStart の後だけ、`GetScript<T>()` は OnStart の前も返す。部品の準備ができている前提の所は `Of` のままにする。

---

### M-16 子の名前の規約・小さなプレハブの使い回し → 動的ノード API（任意）

**今の場所**:

- 行に連番の名前を付けて名前で引く: `App/Screens/Alarms/AlarmListScreen.cs:55`（`AlarmRow{0}`）・`App/Screens/Alarms/Sub/ChoiceListScreen.cs:24`（`ChoiceRow{0}`）・
  `App/Screens/Alarms/Sub/SoundScreen.cs:27`（`SoundRow{0}`）・`App/Screens/Profiles/ProfileOverlayScreen.cs:40`（`Row{0}`）・`FullWidthSlider` の `Ticks/Tick{0}`（M-01 で消える）。
- 形・文字の小さなプレハブを作って使い回す `App/Widgets/PrefabPool.cs:18`（Instantiate は次のフレームにできるので `IsReady` で待つ。13〜14 行）。庭の絵の層・当たりの層・棚・種屋・
  ペナルティの履歴・テーマの行・プロフィールの編集の 9 ファイルが使う。
- （参考）`FindChild(` は 56 ファイル 219 か所。パスの定数で子を引く書き方は `FindChild` の正しい使い方なので、置き換えの対象ではない。

**置き換え先**（`scripting/src/Api/GameObject.cs`）:

```csharp
public int ChildCount { get; }                     // 論理の子（フォルダは透過）。読みはフレームの始めの木
public GameObject GetChild(int index);             // 範囲の外なら IsValid = false
public GameObject[] Children { get; }              // その時点の写し
public int SiblingIndex { get; }                   // 分からなければ −1
public void SetSiblingIndex(int index);  public void SetAsFirstSibling();  public void SetAsLastSibling();   // フレーム末尾に適用
public static GameObject Create2D(string name, GameObject parent = default);   // 空の 2D アクタ
public T? AddComponent<T>() where T : struct, IComponentHandle<T>;            // その場で作られ、同じフレームに値を書ける
public bool RemoveComponent<T>(int index = 0) where T : struct, IComponentHandle<T>;
public bool AddScript<T>() where T : SEEDScript;                              // インスタンスはフレーム末尾。次のフレームに OnStart
// CanvasTransform.Size（get/set）= CanvasLayoutItem.PreferredSize の近道
```

**手順**（どれも任意）:

1. 行の名前の規約: `GameObject.Instantiate` の戻り値を一覧で持つか、親の `GetChild(i)`・`Children` で引く（並びはレイアウトの並び）。名前の規約を残しても動く。
2. PrefabPool: Sprite 1 つ・Text 1 つの小さなプレハブなら `Create2D` ＋ `AddComponent<Sprite>()`・`AddComponent<Text>()` で同じフレームに値を書ける（`IsReady` の待ちが要らない）。
   ただし `ChildCount` などの読み・`LayoutSize` は次のフレームから。

**注意**:

- 同じフレームに作った子は `ChildCount`・`Children` に入らない（フレームの始めの木）。
- `Create`・`AddComponent`・`AddScript` で作った物は「スクリプトが作ったもの」として扱われる。プレハブ由来のノードへの `AddScript`・`AddComponent` は「Play 中の変更をプレハブへ書き戻す」を
  有効にしていると焼かれる（2 回目のレビュー #4。書き戻しは既定で無効になった）。
- `AddComponent<T>()` で足せない種類がある（Model・Camera・Canvas など。scripting_api.md §7「動的ノード」の表）。
- 動的ノード API の Play での確かめの記録は docs に見つけられなかった（**要確認**）。

---

### M-17 端末の模擬の手動の環境変数 → 実行先「PC（端末の模擬: …）」

**今の場所**: Wake or Pay のリポジトリ（`assets/scripts`・README・`docs`）に `SEED_SIM_WINDOW_SIZE`・`SEED_SIM_SCALE_FACTOR`・`SEED_SIM_SAFE_AREA`・`SEED_SIM_KEYBOARD_HEIGHT`・
`--render-quality=` の記述は無い（2026-10-03 に grep して 0 件）。手で付けていたのは確かめの手順（起動の命令）の側。

**置き換え先**: エディタのツールバーの実行先で「PC（端末の模擬: Pixel 6a 半分）」などを選んで ▶（別ウィンドウの Play。端末は `editor/config/device_presets.json` に 1 件足せば増える。
[editor_device_presets.md](editor_device_presets.md)）。同梱は Pixel 6a 実寸（1080×2400・×2.625）・Pixel 6a 半分（540×1200・×1.3125）・Pixel 6a dp 等倍（411×914）・
小さい電話（720×1600・×2）・タブレット（1200×1920・×1.5）。

**手順**: 段ごとの Play の確かめをこの実行先で行う（これまでの PC の 540×1200 は「Pixel 6a 半分」と同じ大きさ）。

**注意**:

- 4 つの環境変数は、エディタの環境に同じ名前があってもプリセットの値で上書きされる。ほかの環境変数（`SEED_PLATFORM_SIM_PERMISSIONS`・`SEED_PLATFORM_SIM_OS_VERSION`・`SEED_PLATFORM_SIM_NO_OPEN`
  など。Wake or Pay の README 334・429・457〜460 行）は受け継がれるので、エディタを起動する前に付ける。
- Stop しても常駐を使い回すので、窓の大きさを手で変えた後は、別の端末か PC を選んで Play し直すまでその大きさのまま。

---

## 4. 置き換えないもの

| 回避 | 場所 | 残す理由 |
|---|---|---|
| `HiddenScrollKeeper` | `App/Widgets/HiddenScrollKeeper.cs:21`（`AlarmEditScreen.cs:115`・`153`） | 隠れた `CanvasScroll`（中身の大きさが自動）が見えている子だけで中身を測り、位置が 0 へ戻る不具合（backlog W3-7 (1)）は**直っていない**。M-12 と一緒に確かめる。アクティビティのタブなどにも同じことが起きうる（Wake or Pay の README の W3-7「未対応」） |
| `KeepScreenOnLease`・`ContinuousRedrawLease`・`RedrawHold` | `App/Widgets/KeepScreenOnLease.cs:10`・`ContinuousRedrawLease.cs:10`・`RedrawHold.cs:8`（`RingingScreen.cs:95`・`104`・`ShakeCheckView.cs:82`・`114`・`GardenScreen.cs:66`・`GardenEditScreen.cs:67`） | 入れ替わりの順（新しい画面の `OnScreenEnter` が古い画面の `OnScreenExit` より先）は**仕様**として書かれた（ui_navigation.md §2「入れ替わりの知らせの順」）。数え上げはその推奨の形。作り置きでない `KeepState = false` の画面は `OnScreenExit` なしで消える制限があるが、鳴動画面は `KeepState = true` で積んでいる（`AppNavigator.cs` の `ApplyShow`）ので当たらない |
| `ModalParkRules`・`ModalParkStage`・`ModalParkStep`・`ModalParkFacts` | `Domain/Routing/` | 覆いへ戻るか・見せずに閉じるかはアプリの決め方（M-11 の後に要らなくなる段だけ整理する） |
| `WrappedText.Fit`・`FitWidth`（ボタン以外）・`TextFit`・`StackFit` | `App/Widgets/WrappedText.cs:43`・`78`・`TextFit.cs:12`・`StackFit.cs` | `Text.Measure`（W2-6c）が無く、「中身に合わせるコンテナの背景が伸びない」（backlog W3-0）も残っている |
| `KeyboardReveal` | `App/Widgets/KeyboardReveal.cs` | キーボードを出したまま並びが変わったときの送り直しの口が無い（backlog W3-2b (2)） |
| 生存の判定（`CanvasTransform` の有無） | `PrebuiltContent.cs` の `Exists` など | `GameObject.IsValid` は消えたかを判別できない（backlog W3-6 (5)）。M-12 で PrebuiltContent は消えても、同じ判定を使う所は残る |
| `AppServices.Current`・`WakeCheckInputSlot.Current`・`WakeCheckViewBase.Active`・`GameObject.Find("RootStack")` | M-15 | スクリプトでない／「今使っている 1 つ」の意味／同じ型が複数ある |
| 試し聴きをメディアの音量で鳴らす | `App/Services/SoundPreview.cs:12` | 音の流れ（メディア／アラーム）を選ぶ API が無い |
| 絵文字を落として語だけ出す | `Domain/Text/PictographText.cs:10` | 書体に絵文字が無い（backlog W3-1 (9)） |
| テーマの `text.*`（sp × 1.395） | `assets/common/themes/wop_base.json` | 文字の大きさの意味の違い（backlog W3-1 (4)）の補い。今夜の API とは関係ない |
| `SEED_PLATFORM_SIM_*` | Wake or Pay の README の模擬の表 | SEED の模擬の正式な口（回避ではない） |
| パッケージの `additional_folders` | `assets/packaging_settings.json` 51〜56 行 | 末尾が `/` のフォルダの参照の収録は SEED 側で直った（backlog W3-0 の最初の項目。2026-10-03）ので外してよいが、任意（外すならドライランで収録の件数を比べる） |

---

## 5. 関連

- 残件・未検証の点の置き場: [backlog.md](backlog.md)「Wake or Pay 側の置き換え（朝以降）」。エンジン側の元の項目は「W3: Wake or Pay の移植で見つかったエンジンの不具合・制限」
  （W3-1〜W3-7）・「2026-10-02 の UI 部品の拡充（lane2）で残したもの」・「2026-10-02 の画面の遷移・面の口（lane3）で残したもの」・「2026-10-03 の UI 部品の手直し（lane3）で残したもの」・
  「ローカライズ」・「データバインディング」・「スクリプトを引く」・「2 回目のレビュー」の節。
- Wake or Pay 側の記録: `D:\SEED_projects\WakeOrPay\README.md`（W3-6・W3-7 の「SEED 側の課題」と「PC での確かめの命令」）。
