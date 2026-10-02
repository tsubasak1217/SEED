// ============================================================
//  LocalizationPanelMessages.cs — 文字列表（ローカライズ）のパネルの文言（1 か所に集める）
//
//  XAML とコードビハインドに日本語を散らさないため、画面に出す文言はここから流し込む
//  （VersionControlMessages と同じ流儀）。書式付きの文言は関数にする。
// ============================================================

using System.Collections.Generic;

namespace SEEDEditor.Panels.Localization;

/// <summary>文字列表のパネルの文言。</summary>
internal static class LocalizationPanelMessages
{
    // ── タブ・見出し ───────────────────────────────────────

    /// <summary>パネルの題（タブ名。MainWindow の LayoutAnchorable の Title と同じ）。</summary>
    public const string PanelTitle = "文字列表（ローカライズ）";

    /// <summary>未保存の印（タブ名の後ろ）。</summary>
    public const string DirtyMark = "*";

    /// <summary>キーの列の見出し。</summary>
    public const string KeyColumnHeader = "キー";

    /// <summary>種類の列の見出し。</summary>
    public const string KindColumnHeader = "種類";

    /// <summary>複数形の形の行の種類。</summary>
    public const string KindPluralForm = "複数形";

    /// <summary>ほかの言語では複数形のまとまりのキーの 1 文の行の種類。</summary>
    public const string KindPluralPlain = "1 文";

    /// <summary>既定の言語の印（列の見出し）。</summary>
    public const string DefaultMark = "既定";

    /// <summary>読み取り専用の印（列の見出し）。</summary>
    public const string ReadOnlyMark = "読み取り専用";

    // ── 要約・状態 ─────────────────────────────────────────

    /// <summary>上部の要約（未訳の数とキーの数）。</summary>
    public static string Summary(int missing, int keys, int shown) =>
        shown == keys ? $"未訳 {missing} 件 / キー {keys} 件" : $"未訳 {missing} 件 / キー {keys} 件（{shown} 件を表示）";

    /// <summary>警告の数。</summary>
    public static string WarningCount(int count) => $"警告 {count} 件";

    /// <summary>説明の欄の見出し。</summary>
    public const string CommentsHeader = "説明: ";

    /// <summary>説明が無いときの案内。</summary>
    public const string NoComments = "説明はありません（表のオブジェクトに \"_about\" など \"_\" で始まる鍵を書くと、ここに出ます）";

    /// <summary>行を選んでいないときの案内。</summary>
    public const string NoSelection = "行を選ぶと、そのキーの説明（\"_\" で始まる鍵）がここに出ます。升目をダブルクリック（または F2・文字の入力）で編集、Enter で確定します";

    /// <summary>保存した。</summary>
    public static string Saved(IEnumerable<string> files) => $"保存しました（{string.Join("・", files)}）";

    /// <summary>保存するものが無かった。</summary>
    public const string NothingToSave = "変更はありません";

    /// <summary>保存に失敗した。</summary>
    public static string SaveFailed(string reason) => $"保存できませんでした: {reason}";

    /// <summary>読み直した。</summary>
    public const string Reloaded = "ディスクから読み直しました";

    // ── 置き場が無いとき ───────────────────────────────────

    /// <summary>置き場が無いときの本文。</summary>
    public static string MissingFolder(string display) =>
        $"このプロジェクトには文字列表（{display} の index.json）がありません。";

    /// <summary>置き場が無いときの補足。</summary>
    public const string MissingFolderHint =
        "「見本から作る」で templates/locale の index.json・ja.json・en.json を assets/locale へコピーします（既にあるファイルは上書きしません）。";

    /// <summary>テンプレートのライブラリが見つからない。</summary>
    public const string TemplateLibraryMissing = "テンプレートのライブラリ（templates/）が見つかりません";

    // ── 操作の確認・入力 ───────────────────────────────────

    /// <summary>キーの名前の変更の入力の題。</summary>
    public const string RenameCaption = "キーの名前の変更";

    /// <summary>キーの名前の変更の説明（複数形のまとまりは形ごと替わる）。</summary>
    public static string RenamePrompt(string key, bool isPluralGroup) => isPluralGroup
        ? $"複数形のまとまり「{key}」の新しい名前（形 .one / .other … もいっしょに替わります）:"
        : $"「{key}」の新しい名前（. で区切ると入れ子になります）:";

    /// <summary>削除の確認の題。</summary>
    public const string DeleteCaption = "キーの削除";

    /// <summary>削除の確認の本文。</summary>
    public static string DeleteConfirm(IReadOnlyList<string> keys) => keys.Count == 1
        ? $"「{keys[0]}」を全部の言語の表から消しますか？（保存するまでファイルは変わりません）"
        : $"選んだ {keys.Count} 件のキーを全部の言語の表から消しますか？（保存するまでファイルは変わりません）";

    /// <summary>言語の追加の題。</summary>
    public const string AddLanguageCaption = "言語の追加";

    /// <summary>言語のコードの入力の説明。</summary>
    public const string AddLanguageCodePrompt = "言語のコード（ja・en・pt-BR など。表のファイル名 <コード>.json になります）:";

    /// <summary>言語の名前の入力の説明。</summary>
    public static string AddLanguageNamePrompt(string code) => $"言語「{code}」の名前（その言語での呼び名。言語を選ぶ画面に出ます）:";

    /// <summary>言語の名前の変更の題。</summary>
    public const string RenameLanguageCaption = "言語の名前";

    /// <summary>言語の名前の変更の説明。</summary>
    public static string RenameLanguagePrompt(string code) => $"言語「{code}」の名前（その言語での呼び名）:";

    /// <summary>言語を外す確認の題。</summary>
    public const string RemoveLanguageCaption = "言語を一覧から外す";

    /// <summary>言語を外す確認の本文。</summary>
    public static string RemoveLanguageConfirm(string code, string fileName) =>
        $"言語「{code}」を index.json から外しますか？\n\n表のファイル {fileName} は消しません（一覧に載らない表は実行中に読まれません）。" +
        "同じコードで言語を足し直すと、その表がまた使われます。";

    /// <summary>未保存のまま別の置き場を開く・読み直すときの確認の題。</summary>
    public const string UnsavedCaption = "文字列表";

    /// <summary>未保存のまま別の置き場を開く・読み直すときの確認の本文。</summary>
    public const string UnsavedConfirm = "文字列表に未保存の変更があります。保存しますか？\n\n［はい］保存してから続ける ／ ［いいえ］変更を捨てて続ける ／ ［キャンセル］やめる";

    /// <summary>終了時の確認の本文。</summary>
    public const string UnsavedOnExit = "文字列表（ローカライズ）に未保存の変更があります。終了する前に保存しますか？";

    /// <summary>操作に失敗したときの頭。</summary>
    public static string Failed(string what, string reason) => $"{what}できませんでした: {reason}";

    // ── 言語のメニュー ─────────────────────────────────────

    /// <summary>既定の言語にする。</summary>
    public const string MenuSetDefault = "既定の言語にする";

    /// <summary>fallback の小メニュー。</summary>
    public const string MenuFallback = "次に探す言語（fallback）";

    /// <summary>fallback なし。</summary>
    public const string MenuFallbackNone = "なし（既定の言語へ直接）";

    /// <summary>名前を変える。</summary>
    public const string MenuRenameLanguage = "名前を変える…";

    /// <summary>一覧から外す。</summary>
    public const string MenuRemoveLanguage = "この言語を一覧から外す…";

    /// <summary>言語を足す。</summary>
    public const string MenuAddLanguage = "言語を追加…";

    /// <summary>言語のメニューの項目名（"日本語 (ja)"）。</summary>
    public static string LanguageLabel(string name, string code) =>
        string.Equals(name, code, System.StringComparison.Ordinal) ? code : $"{name} ({code})";

    // ── 外部変更の帯 ───────────────────────────────────────

    /// <summary>外部変更の帯の本文。</summary>
    public static string ChangedOnDisk(IReadOnlyList<string> files) =>
        $"文字列表のファイルがディスク上で変更されました（{string.Join("・", files)}）。未保存の編集があるため読み直していません。";

    /// <summary>読み直す（編集を捨てる）。</summary>
    public const string ChangedReload = "読み直す（編集を捨てる）";

    /// <summary>このまま編集を続ける。</summary>
    public const string ChangedKeep = "このまま編集を続ける";

    // ── 升目 ───────────────────────────────────────────────

    /// <summary>列の見出しのツールチップ（探す順・ファイル）。</summary>
    public static string ColumnToolTip(string fileName, IReadOnlyList<string> chain, string loadError) =>
        $"{fileName}\n探す順: {string.Join(" → ", chain)}" + (loadError.Length > 0 ? $"\n{loadError}" : string.Empty) +
        "\n右クリックで既定の言語・次に探す言語・名前・一覧から外す";
}
