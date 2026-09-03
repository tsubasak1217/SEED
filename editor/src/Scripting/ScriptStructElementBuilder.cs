using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using SEEDEditor.Controls;

namespace SEEDEditor.Scripting;

/// <summary>
/// 構造体配列の「要素 1 個ぶんの中身」（メンバ行の並び）を組み立てる。
///
/// 【役割分担】
/// - <see cref="ScriptArrayFieldBuilder"/> … 配列そのもの（折りたたみ・要素の追加削除・JSON の束ね）
/// - 本クラス                              … 要素 1 個の中身（メンバ行の並びとメンバ値の JSON 化）
/// - <see cref="ScriptInspectorBuilder"/>   … 型別の行 UI（スカラ・参照）の実体
///
/// 【値の流れ】
/// 要素 1 個は JSON オブジェクト文字列（<c>{"spawnDistance":10.0,"fishPrefabs":["a.actor"]}</c>）。
/// これを <see cref="SEED.ScriptStructArray.DecodeMembers"/> で「メンバ名 → 文字列表現」へ開き、
/// 各メンバ行へ渡す。行が編集されたらその 1 メンバだけ差し替えて JSON オブジェクトへ組み直し、
/// 呼び出し元（配列ビルダー）へ返す。配列ビルダーが全要素を束ねて 1 本の文字列として書き戻すため、
/// シーン保存形式も IPC も変わらない。
/// </summary>
internal static class ScriptStructElementBuilder
{
    /// <summary>メンバ行全体の左インデント（px）。要素の折りたたみ配下であることを示す。</summary>
    private const double MemberIndent = 6;

    /// <summary>
    /// メンバ名と入れ子配列の折りたたみキーを繋ぐ区切り（ドットパス表記に合わせる）。
    /// 配列ビルダー側も要素の並び替えで「その要素配下の折りたたみ状態」をまとめて動かすために参照するため、
    /// 綴りを二重管理しないようここを唯一の定義とする。
    /// </summary>
    internal const string MemberKeySeparator = ".";

    /// <summary>
    /// 要素 1 個ぶんのメンバ行パネルを生成する。
    /// </summary>
    /// <param name="arrayInfo">配列フィールドの型情報（<c>StructMembers</c> が非 null であること）。</param>
    /// <param name="objectJson">この要素の現在値（JSON オブジェクト文字列）。</param>
    /// <param name="onObjectChanged">
    /// メンバが編集されたときの通知。組み直した JSON オブジェクト文字列を渡す。
    /// </param>
    /// <param name="onRefDrop">参照メンバのドロップ解決役（null ならドロップ不可）。</param>
    /// <param name="assetPathToVirtual">絶対パス → assets:// 仮想パスの変換（string 配列メンバのドロップ用）。</param>
    /// <param name="expandStates">折りたたみ状態のストア（入れ子配列メンバ用）。</param>
    /// <param name="expandKey">この要素の折りたたみ状態キー（メンバ名を足して入れ子へ配る）。</param>
    public static UIElement Build(
        ScriptArrayFieldInfo    arrayInfo,
        string                  objectJson,
        Action<string>          onObjectChanged,
        IReferenceDropResolver? onRefDrop,
        Func<string, string>?   assetPathToVirtual,
        ExpandStateStore?       expandStates,
        string                  expandKey)
    {
        var panel = new StackPanel { Margin = new Thickness(MemberIndent, 1, 0, 3) };

        // メンバのレイアウト（JSON との対応表）。UI 側のメンバ一覧と同じ型から導かれる。
        if (!SEED.ScriptStructArray.TryGetLayout(arrayInfo.ElementType, out var layout)) return panel;

        // 現在値を「メンバ名 → 文字列表現」へ開く（欠損・壊れ値は既定値で埋まる）。
        // 編集で書き換えるので可変の辞書へ写しておく。
        var values = new Dictionary<string, string>();
        foreach (var kv in SEED.ScriptStructArray.DecodeMembers(objectJson, layout))
            values[kv.Key] = kv.Value;

        // 1 メンバの編集を JSON オブジェクトへ反映して通知する
        void CommitMember(string name, string value)
        {
            if (values.TryGetValue(name, out var current) && current == value) return;   // 無変化なら送らない
            values[name] = value;
            onObjectChanged(SEED.ScriptStructArray.EncodeMembers(values, layout));
        }

        foreach (var member in arrayInfo.StructMembers!)
        {
            var name = member.Field.Name;
            var raw  = values.TryGetValue(name, out var v) ? v : null;

            // 入れ子の配列メンバ（List<string> など）は配列 UI をそのまま再利用する
            if (member.Array is not null)
            {
                panel.Children.Add(ScriptArrayFieldBuilder.Build(
                    member, name, raw,
                    text => CommitMember(name, text),
                    onRefDrop, assetPathToVirtual,
                    expandStates, expandKey + MemberKeySeparator + name));
                continue;
            }

            // スカラ・参照メンバは通常のフィールド行をそのまま使う
            var row = ScriptInspectorBuilder.BuildValueRow(
                member, raw, text => CommitMember(name, text), onRefDrop);
            if (row is not null) panel.Children.Add(row);
        }

        return panel;
    }
}
