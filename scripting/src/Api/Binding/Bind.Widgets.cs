using System;
using SEED.UI;
using SEEDEditor.Scripting;

namespace SEED.Binding;

// ============================================================
//  Bind.Widgets.cs — UI 部品（SEED.UI）との双方向の結び付け（実行中だけの部分）
//
//      Bind.Toggle(this, _notifyToggle, _notify);                      // 部品そのもの（UiWidget.Of で引いた後）
//      Bind.Toggle(this, gameObject.FindChild("Notify"), _notify);     // 部品の付くノード（部品の OnStart の前でよい）
//      Bind.Slider(this, volumeNode, _volume);
//      Bind.TextField(this, nameNode, _name);
//      Bind.Selection(this, themeNode, _themeIndex);                   // SegmentedControl・RadioGroup・ChipGroup（1 つ選ぶ）
//  値 → 部品: 作った時点で観測値の値を部品へ当て（観測値が正）、観測値が変わるたびに当てる（部品へは知らせずに書く）。
//  部品 → 値: 利用者の操作で観測値へ入る（観測値のほかの購読・結び付けにも届く）。往復は結び付けの留め金で止まる。
//  ノードから結ぶと、部品のスクリプトが始まる（登録簿に載る）までフレームの区切りごとに待ち、始まったら当てる
//  （Instantiate したプレハブの部品は次のフレームから）。部品が破棄されたら（登録簿から外れたら）自分を外す。
// ============================================================

public static partial class Bind
{
    // ── トグル ─────────────────────────────────────────────

    /// <summary>bool の観測値とトグルを双方向に結ぶ（つまみは最初はすぐ置き、その後の値の変化では動かす）。</summary>
    /// <param name="toggle">トグル。</param>
    /// <param name="source">オンかの観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Toggle(Toggle toggle, Observable<bool> source)
        => TwoWay(new ToggleTarget(Ref(toggle)), source);

    /// <summary>bool の観測値とトグルを双方向に結ぶ（owner の破棄で自動で外れる）。</summary>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="toggle">トグル。</param>
    /// <param name="source">オンかの観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Toggle(SEEDScript owner, Toggle toggle, Observable<bool> source)
        => Own(owner, Toggle(toggle, source));

    /// <summary>bool の観測値と、ノードに付くトグルを双方向に結ぶ（トグルの OnStart を待つ）。</summary>
    /// <param name="node">トグルの付くノード。</param>
    /// <param name="source">オンかの観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Toggle(GameObject node, Observable<bool> source)
        => TwoWay(new ToggleTarget(new WidgetRef<Toggle>(node)), source);

    /// <summary>bool の観測値と、ノードに付くトグルを双方向に結ぶ（トグルの OnStart を待つ。owner の破棄で自動で外れる）。</summary>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="node">トグルの付くノード。</param>
    /// <param name="source">オンかの観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Toggle(SEEDScript owner, GameObject node, Observable<bool> source)
        => Own(owner, Toggle(node, source));

    // ── チェックボックス ────────────────────────────────────

    /// <summary>bool の観測値とチェックボックスを双方向に結ぶ。</summary>
    /// <param name="checkbox">チェックボックス。</param>
    /// <param name="source">オンかの観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Checkbox(Checkbox checkbox, Observable<bool> source)
        => TwoWay(new CheckboxTarget(Ref(checkbox)), source);

    /// <summary>bool の観測値とチェックボックスを双方向に結ぶ（owner の破棄で自動で外れる）。</summary>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="checkbox">チェックボックス。</param>
    /// <param name="source">オンかの観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Checkbox(SEEDScript owner, Checkbox checkbox, Observable<bool> source)
        => Own(owner, Checkbox(checkbox, source));

    /// <summary>bool の観測値と、ノードに付くチェックボックスを双方向に結ぶ（OnStart を待つ）。</summary>
    /// <param name="node">チェックボックスの付くノード。</param>
    /// <param name="source">オンかの観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Checkbox(GameObject node, Observable<bool> source)
        => TwoWay(new CheckboxTarget(new WidgetRef<Checkbox>(node)), source);

    /// <summary>bool の観測値と、ノードに付くチェックボックスを双方向に結ぶ（OnStart を待つ。owner の破棄で自動で外れる）。</summary>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="node">チェックボックスの付くノード。</param>
    /// <param name="source">オンかの観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Checkbox(SEEDScript owner, GameObject node, Observable<bool> source)
        => Own(owner, Checkbox(node, source));

    // ── スライダ ───────────────────────────────────────────

    /// <summary>float の観測値とスライダを双方向に結ぶ（部品の見た目は範囲・段階へ寄せた値）。</summary>
    /// <param name="slider">スライダ。</param>
    /// <param name="source">値の観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Slider(Slider slider, Observable<float> source)
        => TwoWay(new SliderTarget(Ref(slider)), source);

    /// <summary>float の観測値とスライダを双方向に結ぶ（owner の破棄で自動で外れる）。</summary>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="slider">スライダ。</param>
    /// <param name="source">値の観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Slider(SEEDScript owner, Slider slider, Observable<float> source)
        => Own(owner, Slider(slider, source));

    /// <summary>float の観測値と、ノードに付くスライダを双方向に結ぶ（OnStart を待つ）。</summary>
    /// <param name="node">スライダの付くノード。</param>
    /// <param name="source">値の観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Slider(GameObject node, Observable<float> source)
        => TwoWay(new SliderTarget(new WidgetRef<Slider>(node)), source);

    /// <summary>float の観測値と、ノードに付くスライダを双方向に結ぶ（OnStart を待つ。owner の破棄で自動で外れる）。</summary>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="node">スライダの付くノード。</param>
    /// <param name="source">値の観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Slider(SEEDScript owner, GameObject node, Observable<float> source)
        => Own(owner, Slider(node, source));

    // ── 文字の欄 ───────────────────────────────────────────

    /// <summary>文字の観測値と文字の欄を双方向に結ぶ（打っている最中の欄は外の値で上書きしない）。</summary>
    /// <param name="field">文字の欄。</param>
    /// <param name="source">本文の観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable TextField(TextField field, Observable<string> source)
        => TwoWay(new TextFieldTarget(Ref(field)), source);

    /// <summary>文字の観測値と文字の欄を双方向に結ぶ（owner の破棄で自動で外れる）。</summary>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="field">文字の欄。</param>
    /// <param name="source">本文の観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable TextField(SEEDScript owner, TextField field, Observable<string> source)
        => Own(owner, TextField(field, source));

    /// <summary>文字の観測値と、ノードに付く文字の欄を双方向に結ぶ（OnStart を待つ）。</summary>
    /// <param name="node">文字の欄の付くノード。</param>
    /// <param name="source">本文の観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable TextField(GameObject node, Observable<string> source)
        => TwoWay(new TextFieldTarget(new WidgetRef<TextField>(node)), source);

    /// <summary>文字の観測値と、ノードに付く文字の欄を双方向に結ぶ（OnStart を待つ。owner の破棄で自動で外れる）。</summary>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="node">文字の欄の付くノード。</param>
    /// <param name="source">本文の観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable TextField(SEEDScript owner, GameObject node, Observable<string> source)
        => Own(owner, TextField(node, source));

    // ── 選択のグループ（SegmentedControl・RadioGroup・ChipGroup）──

    /// <summary>
    /// 番号の観測値と選択のグループを双方向に結ぶ（値は選んでいる項目の並びの番号。−1 = 何も選んでいない。項目が集まるのを待つ）。
    /// </summary>
    /// <param name="group">選択のグループ。</param>
    /// <param name="source">選んでいる番号の観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Selection(SelectionGroup group, Observable<int> source)
        => TwoWay(new SelectionTarget(Ref(group)), source);

    /// <summary>番号の観測値と選択のグループを双方向に結ぶ（owner の破棄で自動で外れる）。</summary>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="group">選択のグループ。</param>
    /// <param name="source">選んでいる番号の観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Selection(SEEDScript owner, SelectionGroup group, Observable<int> source)
        => Own(owner, Selection(group, source));

    /// <summary>番号の観測値と、ノードに付く選択のグループを双方向に結ぶ（OnStart と項目が集まるのを待つ）。</summary>
    /// <param name="node">選択のグループの付くノード。</param>
    /// <param name="source">選んでいる番号の観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Selection(GameObject node, Observable<int> source)
        => TwoWay(new SelectionTarget(new WidgetRef<SelectionGroup>(node)), source);

    /// <summary>番号の観測値と、ノードに付く選択のグループを双方向に結ぶ（OnStart と項目が集まるのを待つ。owner の破棄で自動で外れる）。</summary>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="node">選択のグループの付くノード。</param>
    /// <param name="source">選んでいる番号の観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Selection(SEEDScript owner, GameObject node, Observable<int> source)
        => Own(owner, Selection(node, source));

    /// <summary>部品そのものの引き当てを作る（null は呼び出し側の誤り）。</summary>
    /// <typeparam name="TWidget">部品の型。</typeparam>
    /// <param name="widget">部品。</param>
    /// <returns>引き当て。</returns>
    private static WidgetRef<TWidget> Ref<TWidget>(TWidget widget) where TWidget : UiWidget
    {
        ArgumentNullException.ThrowIfNull(widget);
        return new WidgetRef<TWidget>(widget);
    }
}
