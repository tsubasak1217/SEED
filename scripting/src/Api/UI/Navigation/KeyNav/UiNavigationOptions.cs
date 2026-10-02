using System;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  UiNavigationOptions.cs — 方向キー・パッドの移動の設定（UiNavigator のインスペクタ。2026-10-03。L3-6。docs/ui_navigation.md §7.2）
//
//  入力は InputMap のアクション名（既定 ui.up・ui.down・ui.left・ui.right・ui.submit・ui.cancel と、スティックの ui.move〈Axis2D〉）か、
//  素の入力（SEED.Input の矢印キー・Enter／Space・Escape）。どちらを読むかは InputSource:
//    Auto     … UiNavigator のアクターに InputMap があればアクション、無ければ素の入力（既定）
//    InputMap … アクションだけ（InputMap が無ければ何も読まない）
//    Raw      … 素の入力だけ
//    Both     … 両方（どちらで押してもよい）
//  InputMap はアクションの有無を問う口を持たない（知らない名前は常に false。runtime の action_map.rs）ので、Auto は
//  「InputMap コンポーネントがあるか」で決める。ゲームパッド（D-pad・スティック・South／East）は SEED.Input に口が無く、
//  InputMap のアクション経由でだけ読める（テンプレート templates/input/uiNavigation.inputmap に既定の割り当て）。
// ============================================================

/// <summary>方向キー・パッドの入力の出所。</summary>
public enum NavInputSource
{
    /// <summary>UiNavigator のアクターに InputMap があればアクション、無ければ素の入力。</summary>
    Auto = 0,
    /// <summary>InputMap のアクションだけ。</summary>
    InputMap = 1,
    /// <summary>素の入力（矢印キー・Enter／Space・Escape）だけ。</summary>
    Raw = 2,
    /// <summary>両方（どちらで押してもよい）。</summary>
    Both = 3,
}

/// <summary>方向キー・パッドの移動の設定。</summary>
[Serializable]
public sealed class UiNavigationOptions
{
    /// <summary>上へ移るアクションの既定の名前。</summary>
    public const string DefaultUpAction = "ui.up";
    /// <summary>下へ移るアクションの既定の名前。</summary>
    public const string DefaultDownAction = "ui.down";
    /// <summary>左へ移るアクションの既定の名前。</summary>
    public const string DefaultLeftAction = "ui.left";
    /// <summary>右へ移るアクションの既定の名前。</summary>
    public const string DefaultRightAction = "ui.right";
    /// <summary>決定のアクションの既定の名前。</summary>
    public const string DefaultSubmitAction = "ui.submit";
    /// <summary>キャンセル（戻る）のアクションの既定の名前。</summary>
    public const string DefaultCancelAction = "ui.cancel";
    /// <summary>スティック（Axis2D。各 [-1, 1]・Y の正 = 上）のアクションの既定の名前。</summary>
    public const string DefaultMoveAction = "ui.move";
    /// <summary>フォーカスの枠のプレハブの既定（templates/ui を assets/ui へ取り込んだ置き場）。</summary>
    public const string DefaultFocusRingPrefab = "assets://ui/prefabs/focus_ring.actor";

    /// <summary>入力の出所（既定 Auto）。</summary>
    [SerializeField(Label = "入力の出所")]
    public NavInputSource InputSource = NavInputSource.Auto;

    /// <summary>上へ移るアクション（Bool。押している間 true＝condition Press）。</summary>
    [SerializeField(Label = "上のアクション")]
    public string UpAction = DefaultUpAction;

    /// <summary>下へ移るアクション。</summary>
    [SerializeField(Label = "下のアクション")]
    public string DownAction = DefaultDownAction;

    /// <summary>左へ移るアクション。</summary>
    [SerializeField(Label = "左のアクション")]
    public string LeftAction = DefaultLeftAction;

    /// <summary>右へ移るアクション。</summary>
    [SerializeField(Label = "右のアクション")]
    public string RightAction = DefaultRightAction;

    /// <summary>決定のアクション（成立した瞬間に押す）。</summary>
    [SerializeField(Label = "決定のアクション")]
    public string SubmitAction = DefaultSubmitAction;

    /// <summary>キャンセルのアクション（成立した瞬間に BackDispatcher.Dispatch）。</summary>
    [SerializeField(Label = "キャンセルのアクション")]
    public string CancelAction = DefaultCancelAction;

    /// <summary>スティックのアクション（Axis2D。空なら読まない）。</summary>
    [SerializeField(Label = "スティックのアクション")]
    public string MoveAction = DefaultMoveAction;

    /// <summary>端で反対側の端へ回るか（既定 false = 端で止まる）。</summary>
    [SerializeField(Label = "端で反対側へ回る")]
    public bool Wrap;

    /// <summary>キャンセルで戻るの段（BackDispatcher）へ配るか（既定 true。false ならキャンセルを読まない＝ゲームが自分で扱う）。</summary>
    [SerializeField(Label = "キャンセルで戻る")]
    public bool CancelDispatchesBack = true;

    /// <summary>スクロールの中の部品へ移ったら見える位置までスクロールするか（既定 true）。</summary>
    [SerializeField(Label = "スクロールして見せる")]
    public bool ScrollIntoView = true;

    /// <summary>フォーカスの枠のプレハブ（根に縁だけの角丸の Sprite と CanvasLayoutItem を持つ .actor。空なら枠を出さない）。</summary>
    [SerializeField(Label = "枠のプレハブ"), AssetReference("actor")]
    public string FocusRingPrefab = DefaultFocusRingPrefab;
}
