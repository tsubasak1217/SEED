// ============================================================
//  SeedColorTable.cs — エディタのボタン配色の「唯一の出所」
//
//  【役割】
//  ボタン（Button / ToggleButton / RepeatButton とその派生スタイル）の
//  状態別の背景色・文字色を 1 か所で定義する。
//
//  【なぜ WPF に依存しないのか】
//  ここを WPF の Color / Brush で書くと、色の検査（コントラスト比の自動テスト）に
//  WPF の読み込みが要る。色そのものは単なる数値なので、
//  この表は「16 進文字列の定数」だけで持ち、
//    - WPF 側（XAML）は <see cref="SeedThemeColors"/> 経由で Color に変換して使う
//    - テスト（editor/tests/ThemeContrastTests）はこのファイルだけをリンクして検査する
//  という 2 方向から同じ値を参照する形にしてある。
//
//  【変更するときの手順】
//  1. ここの定数を書き換える
//  2. `dotnet run --project editor/tests/ThemeContrastTests` を通す
//     （コントラスト比が基準を割ると落ちる）
//  XAML 側やコードビハインド側に色を直接書かないこと。規約は docs/editor_ui_style.md。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;

namespace SEEDEditor.Theme;

/// <summary>
/// ボタン配色の定義表。16 進文字列だけを持ち、WPF には依存しない。
/// </summary>
public static class SeedColorTable
{
    // ══════════════════════════════════════════════════════════
    //  下地（ボタンが置かれる面の色）
    //
    //  透明背景のスタイル（リンク風・アイコン専用）は、
    //  「下地の色 × 文字色」でコントラストが決まる。
    //  エディタで使われている 3 つの下地のうち最も明るいものを
    //  最悪ケースとして検査に使う。
    // ══════════════════════════════════════════════════════════

    /// <summary>ウィンドウ本体の背景。</summary>
    public const string SURFACE_WINDOW = "#1E1E1E";

    /// <summary>モーダルダイアログの背景。</summary>
    public const string SURFACE_DIALOG = "#252526";

    /// <summary>パネルのツールバー帯の背景（エディタで最も明るい下地）。</summary>
    public const string SURFACE_TOOLBAR = "#2D2D2D";

    // ══════════════════════════════════════════════════════════
    //  ダイアログの文字と入力欄
    //
    //  コードで組む小さなモーダル（アカウント系・テキスト入力・音声トリム）が
    //  それぞれ同じ値を自前で持っていたため、ここへ集約した。
    //  利用は Theme/SeedDialogTheme.cs 経由。
    // ══════════════════════════════════════════════════════════

    /// <summary>入力欄の背景。</summary>
    public const string FIELD_BG = "#1A1A1A";

    /// <summary>入力欄・区切りの枠線。</summary>
    public const string FIELD_BORDER = "#3F3F46";

    /// <summary>本文の文字色（ボタンの文字と同じ明度で揃える）。</summary>
    public const string DIALOG_TEXT = BUTTON_FG;

    /// <summary>補足・説明の文字色。</summary>
    public const string DIALOG_DIM_TEXT = "#999999";

    /// <summary>成功・肯定を示す文字色。</summary>
    public const string DIALOG_SUCCESS_TEXT = "#8ACB8A";

    /// <summary>エラーを示す文字色。</summary>
    public const string DIALOG_ERROR_TEXT = "#E88F8F";

    /// <summary>
    /// 一覧の選択行の背景。
    ///
    /// <para>
    /// ★WPF 既定の <c>ListBoxItem</c> は、フォーカスが外れた選択行を
    /// **明るい灰色**（`SystemColors.Control` 系）で塗る。暗いダイアログの中で
    /// 明るい文字色を継いだまま塗られると、選択した行だけが読めなくなる。
    /// ボタンのホバー色と同じ理由で、既定に任せずここで決める。
    /// 値はパネル側の選択色（`Vc.Selection`）と揃えてある。
    /// </para>
    /// </summary>
    public const string DIALOG_LIST_SELECTION_BG = "#264F78";

    /// <summary>一覧のホバー行の背景（「少し明るい」に留める）。</summary>
    public const string DIALOG_LIST_HOVER_BG = "#2A2A2B";

    // ══════════════════════════════════════════════════════════
    //  通常ボタン（暗黙スタイル）
    // ══════════════════════════════════════════════════════════

    /// <summary>通常状態の背景。</summary>
    public const string BUTTON_BG = "#3A3A3D";

    /// <summary>ホバー状態の背景（「少し明るい灰色」に留める）。</summary>
    public const string BUTTON_BG_HOVER = "#4A4A4F";

    /// <summary>押下状態の背景（沈み込みを暗さで表す）。</summary>
    public const string BUTTON_BG_PRESSED = "#2E2E31";

    /// <summary>無効状態の背景。</summary>
    public const string BUTTON_BG_DISABLED = "#303033";

    /// <summary>通常・押下状態の文字色。</summary>
    public const string BUTTON_FG = "#DCDCDC";

    /// <summary>ホバー状態の文字色（押せることを示すために一段明るくする）。</summary>
    public const string BUTTON_FG_HOVER = "#FFFFFF";

    /// <summary>無効状態の文字色。</summary>
    public const string BUTTON_FG_DISABLED = "#8A8A8A";

    /// <summary>枠線の色（枠を出したいボタンだけが使う）。</summary>
    public const string BUTTON_BORDER = "#55555A";

    /// <summary>キーボードフォーカス枠の色（どの背景に対しても 3:1 以上になる明度）。</summary>
    public const string BUTTON_BORDER_FOCUS = "#9CC2F0";

    // ══════════════════════════════════════════════════════════
    //  主操作ボタン（Seed.Button.Primary）
    // ══════════════════════════════════════════════════════════

    /// <summary>主操作ボタンの通常背景。</summary>
    public const string PRIMARY_BG = "#0E639C";

    /// <summary>主操作ボタンのホバー背景。</summary>
    public const string PRIMARY_BG_HOVER = "#1177BB";

    /// <summary>主操作ボタンの押下背景。</summary>
    public const string PRIMARY_BG_PRESSED = "#0A4E7A";

    /// <summary>主操作ボタンの無効背景。</summary>
    public const string PRIMARY_BG_DISABLED = "#1E3A4D";

    /// <summary>主操作ボタンの文字色。</summary>
    public const string PRIMARY_FG = "#FFFFFF";

    /// <summary>主操作ボタンの無効時の文字色。</summary>
    public const string PRIMARY_FG_DISABLED = "#9FB6C6";

    // ══════════════════════════════════════════════════════════
    //  完了・生成ボタン（Seed.Button.Success）
    //
    //  「実行すると成果物ができる」操作に使う（パッケージのビルドなど）。
    //  主操作（Primary）と使い分けるのは、同じ画面に主操作が別にあるとき
    //  どちらが最終行為かを色で見分けられるようにするため。
    // ══════════════════════════════════════════════════════════

    /// <summary>完了・生成ボタンの通常背景。</summary>
    public const string SUCCESS_BG = "#1A5A1A";

    /// <summary>完了・生成ボタンのホバー背景。</summary>
    public const string SUCCESS_BG_HOVER = "#226A22";

    /// <summary>完了・生成ボタンの押下背景。</summary>
    public const string SUCCESS_BG_PRESSED = "#144714";

    /// <summary>完了・生成ボタンの文字色。</summary>
    public const string SUCCESS_FG = "#FFFFFF";

    // ══════════════════════════════════════════════════════════
    //  危険操作ボタン（Seed.Button.Danger）
    // ══════════════════════════════════════════════════════════

    /// <summary>危険操作ボタンの通常背景。</summary>
    public const string DANGER_BG = "#A1373B";

    /// <summary>危険操作ボタンのホバー背景。</summary>
    public const string DANGER_BG_HOVER = "#C04448";

    /// <summary>危険操作ボタンの押下背景。</summary>
    public const string DANGER_BG_PRESSED = "#802B2E";

    /// <summary>危険操作ボタンの文字色。</summary>
    public const string DANGER_FG = "#FFFFFF";

    // ══════════════════════════════════════════════════════════
    //  リンク風ボタン（Seed.Button.Link）— 背景は常に透明
    // ══════════════════════════════════════════════════════════

    /// <summary>リンク風ボタンの通常文字色。</summary>
    public const string LINK_FG = "#4FA3E3";

    /// <summary>リンク風ボタンのホバー文字色。</summary>
    public const string LINK_FG_HOVER = "#7FC1F0";

    /// <summary>
    /// リンク風ボタンの押下文字色。
    /// 暗い下地では「押下＝暗くする」がそのままコントラスト不足になるため
    /// （深い青にすると 4.5:1 を割る）、通常 → ホバー → 押下 の順に明るくしていく。
    /// </summary>
    public const string LINK_FG_PRESSED = "#A5D6F5";

    /// <summary>リンク風ボタンの無効文字色。</summary>
    public const string LINK_FG_DISABLED = "#8A8A8A";

    // ══════════════════════════════════════════════════════════
    //  アイコン専用ボタン（Seed.Button.Icon）— 背景は透明＋白の薄がけ
    // ══════════════════════════════════════════════════════════

    /// <summary>アイコン専用ボタンのホバー時に下地へ重ねる色（ARGB）。</summary>
    public const string ICON_OVERLAY_HOVER = "#22FFFFFF";

    /// <summary>アイコン専用ボタンの押下時に下地へ重ねる色（ARGB）。</summary>
    public const string ICON_OVERLAY_PRESSED = "#3AFFFFFF";

    /// <summary>アイコン専用ボタンの通常のアイコン色。</summary>
    public const string ICON_FG = "#DCDCDC";

    /// <summary>アイコン専用ボタンのホバー時のアイコン色。</summary>
    public const string ICON_FG_HOVER = "#FFFFFF";

    /// <summary>アイコン専用ボタンの無効時のアイコン色。</summary>
    public const string ICON_FG_DISABLED = "#8A8A8A";

    // ══════════════════════════════════════════════════════════
    //  トグルの ON 状態（ToggleButton の暗黙スタイル）
    // ══════════════════════════════════════════════════════════

    /// <summary>トグル ON の背景（主操作と同じアクセント色で「効いている」ことを示す）。</summary>
    public const string TOGGLE_BG_CHECKED = PRIMARY_BG;

    /// <summary>トグル ON かつホバーの背景。</summary>
    public const string TOGGLE_BG_CHECKED_HOVER = PRIMARY_BG_HOVER;

    /// <summary>トグル ON の文字色。</summary>
    public const string TOGGLE_FG_CHECKED = PRIMARY_FG;

    // ══════════════════════════════════════════════════════════
    //  コントラストの基準（WCAG 2.1）
    // ══════════════════════════════════════════════════════════

    /// <summary>通常の文字に要求するコントラスト比（WCAG AA）。</summary>
    public const double MIN_RATIO_TEXT = 4.5;

    /// <summary>無効状態など、可読性より「押せないこと」が優先される場合の下限。</summary>
    public const double MIN_RATIO_DISABLED = 3.0;

    /// <summary>フォーカス枠など、文字ではない目印に要求する比（WCAG AA 非テキスト）。</summary>
    public const double MIN_RATIO_NON_TEXT = 3.0;

    // ══════════════════════════════════════════════════════════
    //  検査対象の一覧（テストはこの表を回すだけでよい）
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// コントラストを検査する 1 件分。
    /// </summary>
    /// <param name="StateName">状態の名前（失敗時の表示に使う）。</param>
    /// <param name="BackgroundHex">下地の色（#RRGGBB）。</param>
    /// <param name="OverlayArgbHex">下地へ重ねる半透明色（#AARRGGBB）。重ねないときは null。</param>
    /// <param name="ForegroundHex">前景（文字・アイコン・枠）の色（#RRGGBB）。</param>
    /// <param name="MinimumRatio">要求するコントラスト比の下限。</param>
    public readonly record struct ContrastCase(
        string StateName,
        string BackgroundHex,
        string? OverlayArgbHex,
        string ForegroundHex,
        double MinimumRatio);

    /// <summary>
    /// 全スタイル・全状態のコントラスト検査項目。
    /// スタイルを足したらここにも足す（足し忘れると検査されないため）。
    /// </summary>
    public static IReadOnlyList<ContrastCase> ContrastCases { get; } = new ContrastCase[]
    {
        // ── 通常ボタン（暗黙スタイル）──
        new("通常ボタン/通常",   BUTTON_BG,          null, BUTTON_FG,          MIN_RATIO_TEXT),
        new("通常ボタン/ホバー", BUTTON_BG_HOVER,    null, BUTTON_FG_HOVER,    MIN_RATIO_TEXT),
        new("通常ボタン/押下",   BUTTON_BG_PRESSED,  null, BUTTON_FG,          MIN_RATIO_TEXT),
        new("通常ボタン/無効",   BUTTON_BG_DISABLED, null, BUTTON_FG_DISABLED, MIN_RATIO_DISABLED),
        // フォーカスは背景を変えない。文字は通常と同じなので、枠が見えるかだけを見る。
        new("通常ボタン/フォーカス枠", BUTTON_BG,        null, BUTTON_BORDER_FOCUS, MIN_RATIO_NON_TEXT),
        new("通常ボタン/フォーカス枠(ホバー中)", BUTTON_BG_HOVER, null, BUTTON_BORDER_FOCUS, MIN_RATIO_NON_TEXT),

        // ── 主操作ボタン ──
        new("主操作/通常",   PRIMARY_BG,          null, PRIMARY_FG,          MIN_RATIO_TEXT),
        new("主操作/ホバー", PRIMARY_BG_HOVER,    null, PRIMARY_FG,          MIN_RATIO_TEXT),
        new("主操作/押下",   PRIMARY_BG_PRESSED,  null, PRIMARY_FG,          MIN_RATIO_TEXT),
        new("主操作/無効",   PRIMARY_BG_DISABLED, null, PRIMARY_FG_DISABLED, MIN_RATIO_DISABLED),
        new("主操作/フォーカス枠", PRIMARY_BG,     null, BUTTON_BORDER_FOCUS, MIN_RATIO_NON_TEXT),

        // ── 完了・生成ボタン ──
        new("完了/通常",   SUCCESS_BG,         null, SUCCESS_FG, MIN_RATIO_TEXT),
        new("完了/ホバー", SUCCESS_BG_HOVER,   null, SUCCESS_FG, MIN_RATIO_TEXT),
        new("完了/押下",   SUCCESS_BG_PRESSED, null, SUCCESS_FG, MIN_RATIO_TEXT),
        new("完了/フォーカス枠", SUCCESS_BG,   null, BUTTON_BORDER_FOCUS, MIN_RATIO_NON_TEXT),

        // ── 危険操作ボタン ──
        new("危険操作/通常",   DANGER_BG,         null, DANGER_FG, MIN_RATIO_TEXT),
        new("危険操作/ホバー", DANGER_BG_HOVER,   null, DANGER_FG, MIN_RATIO_TEXT),
        new("危険操作/押下",   DANGER_BG_PRESSED, null, DANGER_FG, MIN_RATIO_TEXT),
        new("危険操作/フォーカス枠", DANGER_BG,   null, BUTTON_BORDER_FOCUS, MIN_RATIO_NON_TEXT),

        // ── トグル ON ──
        new("トグルON/通常",   TOGGLE_BG_CHECKED,       null, TOGGLE_FG_CHECKED, MIN_RATIO_TEXT),
        new("トグルON/ホバー", TOGGLE_BG_CHECKED_HOVER, null, TOGGLE_FG_CHECKED, MIN_RATIO_TEXT),

        // ── リンク風（透明背景。最も明るい下地＝ツールバー帯で検査する）──
        new("リンク/通常",   SURFACE_TOOLBAR, null, LINK_FG,          MIN_RATIO_TEXT),
        new("リンク/ホバー", SURFACE_TOOLBAR, null, LINK_FG_HOVER,    MIN_RATIO_TEXT),
        new("リンク/押下",   SURFACE_TOOLBAR, null, LINK_FG_PRESSED,  MIN_RATIO_TEXT),
        new("リンク/無効",   SURFACE_TOOLBAR, null, LINK_FG_DISABLED, MIN_RATIO_DISABLED),

        // ── アイコン専用（透明背景＋白の薄がけ。同じく最も明るい下地で検査する）──
        new("アイコン/通常",   SURFACE_TOOLBAR, null,                 ICON_FG,          MIN_RATIO_TEXT),
        new("アイコン/ホバー", SURFACE_TOOLBAR, ICON_OVERLAY_HOVER,   ICON_FG_HOVER,    MIN_RATIO_TEXT),
        new("アイコン/押下",   SURFACE_TOOLBAR, ICON_OVERLAY_PRESSED, ICON_FG_HOVER,    MIN_RATIO_TEXT),
        new("アイコン/無効",   SURFACE_TOOLBAR, null,                 ICON_FG_DISABLED, MIN_RATIO_DISABLED),

        // ── ダイアログの文字（ボタンと同じ画面に出るので一緒に担保する）──
        new("ダイアログ/本文",     SURFACE_DIALOG, null, DIALOG_TEXT,         MIN_RATIO_TEXT),
        new("ダイアログ/補足",     SURFACE_DIALOG, null, DIALOG_DIM_TEXT,     MIN_RATIO_TEXT),
        new("ダイアログ/成功",     SURFACE_DIALOG, null, DIALOG_SUCCESS_TEXT, MIN_RATIO_TEXT),
        new("ダイアログ/エラー",   SURFACE_DIALOG, null, DIALOG_ERROR_TEXT,   MIN_RATIO_TEXT),
        new("ダイアログ/入力欄",   FIELD_BG,       null, DIALOG_TEXT,         MIN_RATIO_TEXT),
        new("ダイアログ/一覧の選択行", DIALOG_LIST_SELECTION_BG, null, DIALOG_TEXT, MIN_RATIO_TEXT),
        new("ダイアログ/一覧のホバー行", DIALOG_LIST_HOVER_BG,   null, DIALOG_TEXT, MIN_RATIO_TEXT),
    };

    // ══════════════════════════════════════════════════════════
    //  変換ヘルパー
    // ══════════════════════════════════════════════════════════

    /// <summary>16 進表記 1 チャンネル分の桁数。</summary>
    private const int HEX_DIGITS_PER_CHANNEL = 2;

    /// <summary>不透明度の最大値（8 bit）。</summary>
    private const double ALPHA_MAX = 255.0;

    /// <summary>
    /// "#RRGGBB" または "#AARRGGBB" を (A, R, G, B) へ分解する。
    /// </summary>
    /// <param name="hex">16 進表記の色。先頭の # は省略可。</param>
    /// <returns>各チャンネルの 0-255 の値。</returns>
    public static (byte A, byte R, byte G, byte B) Parse(string hex)
    {
        var body = hex.StartsWith('#') ? hex[1..] : hex;

        // #RRGGBB は不透明として扱う
        if (body.Length == HEX_DIGITS_PER_CHANNEL * 3)
            return (byte.MaxValue, Channel(body, 0), Channel(body, 1), Channel(body, 2));

        if (body.Length == HEX_DIGITS_PER_CHANNEL * 4)
            return (Channel(body, 0), Channel(body, 1), Channel(body, 2), Channel(body, 3));

        throw new FormatException($"色の指定が 16 進 6 桁でも 8 桁でもありません: {hex}");
    }

    /// <summary>16 進文字列から n 番目のチャンネルを取り出す。</summary>
    /// <param name="body"># を除いた 16 進文字列。</param>
    /// <param name="index">チャンネルの位置（0 始まり）。</param>
    private static byte Channel(string body, int index)
        => byte.Parse(
            body.Substring(index * HEX_DIGITS_PER_CHANNEL, HEX_DIGITS_PER_CHANNEL),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture);

    /// <summary>
    /// 半透明色を下地へ重ねた「実際に見える色」を求める。
    /// 透明背景のスタイルは、この合成結果と文字色でコントラストが決まる。
    /// </summary>
    /// <param name="overlayArgbHex">重ねる色（#AARRGGBB）。null なら下地をそのまま返す。</param>
    /// <param name="backgroundHex">下地の色（#RRGGBB）。</param>
    /// <returns>合成後の色（#RRGGBB）。</returns>
    public static string CompositeOver(string? overlayArgbHex, string backgroundHex)
    {
        if (string.IsNullOrEmpty(overlayArgbHex)) return backgroundHex;

        var (a, r, g, b)   = Parse(overlayArgbHex);
        var (_, br, bg, bb) = Parse(backgroundHex);
        var alpha = a / ALPHA_MAX;

        return "#"
             + Mix(r, br, alpha).ToString("X2", CultureInfo.InvariantCulture)
             + Mix(g, bg, alpha).ToString("X2", CultureInfo.InvariantCulture)
             + Mix(b, bb, alpha).ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>1 チャンネル分のアルファ合成。</summary>
    /// <param name="front">前面のチャンネル値。</param>
    /// <param name="back">背面のチャンネル値。</param>
    /// <param name="alpha">前面の不透明度（0.0～1.0）。</param>
    private static byte Mix(byte front, byte back, double alpha)
        => (byte)Math.Round(front * alpha + back * (1.0 - alpha));
}
