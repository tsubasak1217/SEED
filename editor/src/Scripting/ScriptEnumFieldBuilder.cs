using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using static SEEDEditor.Scripting.ScriptFieldWidgets;

namespace SEEDEditor.Scripting;

/// <summary>
/// 列挙型（enum）フィールド 1 件分のインスペクタ行（ドロップダウン）を組み立てるビルダー。
///
/// 【保存書式】
/// 値は enum メンバ名の文字列（例 <c>"Lerp"</c>）。書式・変換の正典は
/// <see cref="SEED.ScriptEnumField"/> であり、ランタイム側の値注入と同じ実装を共有する。
///
/// 【選択肢】
/// エディタは Roslyn でコンパイルした型を直接持っているので、選択肢は
/// <see cref="ScriptFieldInfo.EnumOptions"/>（リフレクションで採取済み）をそのまま使う。
///
/// 【候補に無い保存値の扱い】
/// 列挙子を削除・改名した後などで保存値が候補に無い場合でも、
/// 先頭に <see cref="MissingMarker"/> 付きの行を足して選択状態を保ち、**値は書き換えない**
/// （勝手に別の列挙子へ倒すと、名前を戻したときに元へ復帰できなくなるため）。
/// 実行時は <see cref="SEED.ScriptEnumField.TryParse"/> が失敗し、
/// スクリプト宣言時の初期値がそのまま使われる。
///
/// 単体フィールドからも、構造体リストの要素メンバからも
/// <c>ScriptInspectorBuilder.BuildValueRow</c> 経由で同じ UI が使われる。
/// </summary>
internal static class ScriptEnumFieldBuilder
{
    /// <summary>候補に無い保存値の行に付ける印（値そのものには含めない表示専用の接頭辞）。</summary>
    private const string MissingMarker = "⚠ ";

    /// <summary>保存値が空（宣言に無い数値など、名前を持たない値）のときに出す文言。</summary>
    private const string UnsetText = "(未設定)";

    /// <summary>ドロップダウンの最小幅（px）。短い列挙子名でも掴みやすい幅を確保する。</summary>
    private const double ComboMinWidth = 80;

    /// <summary>
    /// 列挙型フィールドの行を作る。
    /// </summary>
    /// <param name="field">フィールド情報（ラベル・ツールチップ・宣言時初期値）。</param>
    /// <param name="options">選択肢（メンバ名の宣言順一覧）。</param>
    /// <param name="raw">現在のシリアライズ値（メンバ名）。null なら宣言時初期値を表示する。</param>
    /// <param name="onChange">値変更の通知（選ばれたメンバ名をそのまま渡す）。</param>
    public static UIElement Build(
        ScriptFieldInfo       field,
        IReadOnlyList<string> options,
        string?               raw,
        Action<string>        onChange)
    {
        // 表示する現在値: 保存値が無ければ宣言時初期値のメンバ名へフォールバックする。
        var current = raw ?? SEED.ScriptEnumField.ToText(field.Field.FieldType, field.DefaultValue);

        // ツールチップ（完全なラベル・説明）は行のラベル側に付くので、コンボには付けない
        //（float / string など他のスカラ行と同じ作法に揃える）。
        var combo = MakeComboBox();
        combo.MinWidth = ComboMinWidth;

        // 候補に無い保存値（列挙子の削除・改名後など）は印付きの行を先頭に足して選択を維持する。
        // 保存値が空（名前を持たない値）のときは「(未設定)」を同じ位置に出す。
        // 見つかった場合の位置はそのまま、足した場合は先頭（0 番）が選択位置になる。
        var found         = IndexOf(options, current);
        var hasMissingRow = found < 0;
        if (hasMissingRow)
            combo.Items.Add(new EnumOption(
                current,
                current.Length == 0 ? UnsetText : MissingMarker + current));

        foreach (var name in options)
            combo.Items.Add(new EnumOption(name, name));

        // 項目を詰め終えてから選択と通知を繋ぐ（差し込み中の SelectionChanged で
        // 値を書き戻してしまわないよう、ハンドラの登録は必ずこの後に行う）。
        combo.SelectedIndex = hasMissingRow ? 0 : found;

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not EnumOption option) return;
            // 同じ値の再選択（候補に無い行を選び直した場合など）では書き戻さない
            if (option.Value == current) return;
            current = option.Value;
            onChange(option.Value);
        };

        return MakeRow(field, null, combo);
    }

    /// <summary>選択肢の中から保存値（大文字小文字を区別しない）の位置を探す（無ければ -1）。</summary>
    private static int IndexOf(IReadOnlyList<string> options, string value)
    {
        if (value.Length == 0) return -1;
        for (int i = 0; i < options.Count; i++)
            if (string.Equals(options[i], value, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// ドロップダウンの項目 1 件。
    ///
    /// 表示（<see cref="Display"/>）と保存値（<see cref="Value"/>）を分けているのは、
    /// 候補に無い保存値へ印を添えて表示しても値そのものは変えないため。
    /// </summary>
    /// <param name="Value">保存する enum メンバ名。</param>
    /// <param name="Display">コンボに表示する文字列。</param>
    private sealed record EnumOption(string Value, string Display)
    {
        /// <summary>ComboBox は項目の <c>ToString()</c> を表示に使うため、表示文字列を返す。</summary>
        public override string ToString() => Display;
    }
}
