// ============================================================
//  SeedButtonStyle.cs — 共通ボタン書式をコードから使うための入口
//
//  【役割】
//  SeedButtonStyles.xaml が公開しているスタイルのキーを定数で持ち、
//  コードで組んだ画面（ダイアログ・インスペクタの動的な行など）から
//  綴り間違いなく参照できるようにする。
//
//  【なぜ必要か】
//  XAML の StaticResource は綴りを間違えてもビルドが通り、
//  起動して初めて「スタイルが当たらない」と分かる壊れ方をする。
//  コード側は文字列を直書きせずここの定数を使うことで、
//  キー名の変更がコンパイルエラーとして出るようにする。
//
//  【使い方】
//    button.Style = SeedButtonStyle.Get(SeedButtonStyle.PRIMARY);
//  もしくは
//    SeedButtonStyle.Apply(button, SeedButtonStyle.ICON);
//  Style を指定しない場合は暗黙スタイル（通常ボタン）が自動で当たるので、
//  「普通のボタン」には何も書かなくてよい。
// ============================================================

using System.Windows;
using System.Windows.Controls.Primitives;

namespace SEEDEditor.Theme;

/// <summary>
/// 共通ボタン書式のスタイルキーと、その取得・適用。
/// </summary>
public static class SeedButtonStyle
{
    /// <summary>通常ボタンの土台。暗黙スタイルと同じ見た目（明示したいときだけ使う）。</summary>
    public const string BASE = "Seed.Button.Base";

    /// <summary>主操作（OK / 作成 / 送信 など、その画面で一番押してほしいもの）。</summary>
    public const string PRIMARY = "Seed.Button.Primary";

    /// <summary>完了・生成（実行すると成果物ができる操作。パッケージのビルドなど）。</summary>
    public const string SUCCESS = "Seed.Button.Success";

    /// <summary>危険操作（削除 / 破棄 など、取り返しのつかないもの）。</summary>
    public const string DANGER = "Seed.Button.Danger";

    /// <summary>リンク風（文章中の副次操作）。</summary>
    public const string LINK = "Seed.Button.Link";

    /// <summary>アイコン専用（背景は透明。ツールバーや行末の小さなボタン）。</summary>
    public const string ICON = "Seed.Button.Icon";

    /// <summary>ダイアログの決定・取消ボタン（最小幅を揃える）。</summary>
    public const string DIALOG = "Seed.Button.Dialog";

    /// <summary>ダイアログの主操作（主操作色＋ダイアログの寸法）。</summary>
    public const string DIALOG_PRIMARY = "Seed.Button.DialogPrimary";

    /// <summary>枠線つき（面として見せたい大きめの選択肢）。</summary>
    public const string OUTLINED = "Seed.Button.Outlined";

    /// <summary>色見本（背景色そのものが値であるボタン）。</summary>
    public const string SWATCH = "Seed.Button.Swatch";

    /// <summary>
    /// キーからスタイルを取り出す。
    /// </summary>
    /// <param name="key">このクラスの定数のいずれか。</param>
    /// <returns>
    /// 対応するスタイル。<see cref="Application.Current"/> が無い
    /// （ヘッドレス起動・単体テスト）ときは null。
    /// </returns>
    public static Style? Get(string key)
        => Application.Current?.TryFindResource(key) as Style;

    /// <summary>
    /// ボタンへスタイルを適用する。見つからないときは何もしない
    /// （暗黙スタイルのまま＝通常ボタンの見た目になり、画面は壊れない）。
    /// </summary>
    /// <param name="button">対象のボタン。</param>
    /// <param name="key">このクラスの定数のいずれか。</param>
    public static void Apply(ButtonBase button, string key)
    {
        var style = Get(key);
        if (style != null) button.Style = style;
    }
}
