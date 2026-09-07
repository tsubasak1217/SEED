using System;
using System.Collections.Generic;
using System.Linq;

namespace SEED;

/// <summary>
/// <c>[SerializeField]</c> を付けた「列挙型（enum）フィールド」の型判定・保存書式・
/// 文字列⇔値の相互変換をまとめた共通ヘルパー（エディタとランタイムが共有する唯一の正典）。
///
/// 【保存書式】
/// 値は **enum メンバ名の文字列**（例 <c>"Lerp"</c>）で保存する。数値ではなく名前にするのは、
/// - 列挙子の並べ替え・値の振り直しに強い（数値だと意味が入れ替わる）
/// - シーン JSON を人が読んで意味が分かる
/// の 2 点のため。基底型（<c>int</c> / <c>byte</c> など）が何であっても名前で扱うので影響を受けない。
///
/// 【読み取りの規則】
/// - メンバ名の照合は**大文字小文字を区別しない**（<c>"lerp"</c> でも <c>Lerp</c> になる）。
/// - **宣言に無い名前・空文字は変換失敗**として扱い、呼び出し側は
///   「値を書き込まない」＝スクリプト宣言時の初期値を保つ（警告は出さない）。
///   数値表記（<c>"3"</c>）も受け付けない。宣言に無い値が実体へ入り込むのを防ぐため。
///
/// 【非対応】
/// - <c>[Flags]</c> 属性付きの列挙型（複数選択の UI が別物になるため、従来どおり読み取り専用表示）。
/// - <c>Nullable&lt;TEnum&gt;</c>（<c>MyEnum?</c>）と enum の配列／リスト。
/// </summary>
public static class ScriptEnumField
{
    // ─── 型タグ・書式定数（エディタ／Rust と共有する正典）───────

    /// <summary>
    /// 列挙型フィールドの型タグ。
    /// Rust 側 <c>script_component.rs</c> の <c>ENUM_TYPE_TAG</c> と一致させること。
    /// </summary>
    public const string TypeTag = "enum";

    /// <summary>
    /// 値が未設定（＝宣言時の初期値を使う）ことを表す文字列。
    /// 変換は必ず失敗し、呼び出し側は現在値を維持する。
    /// </summary>
    public const string UnsetValue = "";

    /// <summary><c>[Flags]</c> 属性の型名（アセンブリ ID 差異を吸収するため名前で照合する）。</summary>
    private const string FlagsAttributeName = "FlagsAttribute";

    // ─── 型判定 ───────────────────────────────────────────────

    /// <summary>
    /// 「インスペクタのドロップダウンで編集できる列挙型」かを判定する。
    ///
    /// <c>[Flags]</c> 付きは複数選択の意味を持つため対象外（false）。
    /// 判定はエディタ（<c>ScriptCompiler</c>）・ランタイム（<c>ScriptBridge</c>）の双方から
    /// 必ずこのメソッドを通すこと（片側だけの独自判定は型タグのずれを生む）。
    /// </summary>
    public static bool IsEnumFieldType(Type? type)
        => type is { IsEnum: true } && !HasFlagsAttribute(type);

    /// <summary><c>[Flags]</c> 属性が付いているかを属性名で判定する。</summary>
    private static bool HasFlagsAttribute(Type type)
        => type.GetCustomAttributesData().Any(a => a.AttributeType.Name == FlagsAttributeName);

    // ─── 選択肢 ───────────────────────────────────────────────

    /// <summary>
    /// 列挙型の選択肢（メンバ名）を宣言順で返す。
    /// 対象外の型なら空配列（呼び出し側は「列挙型フィールドではない」として扱う）。
    /// </summary>
    public static IReadOnlyList<string> GetOptions(Type? type)
        => IsEnumFieldType(type) ? Enum.GetNames(type!) : Array.Empty<string>();

    // ─── 値 ⇔ 文字列 ──────────────────────────────────────────

    /// <summary>
    /// 保存文字列（メンバ名）を列挙型の値へ変換する。
    ///
    /// 大文字小文字を区別せずに**宣言済みメンバ名だけ**を照合する
    /// （<c>Enum.TryParse</c> は数値文字列や未定義の合成値も通してしまうため、
    ///  名前一覧との照合を自前で行ってから <c>Enum.Parse</c> する）。
    /// </summary>
    /// <param name="type">対象の列挙型。</param>
    /// <param name="text">保存文字列（メンバ名）。空・未知の名前は失敗。</param>
    /// <param name="value">変換に成功したときの列挙型の値（boxed）。</param>
    /// <returns>変換できたら true。</returns>
    public static bool TryParse(Type? type, string? text, out object? value)
    {
        value = null;
        if (!IsEnumFieldType(type) || string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text!.Trim();
        // 宣言されているメンバ名と一致するものだけを受け付ける（数値表記は弾く）
        var name = Enum.GetNames(type!)
            .FirstOrDefault(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase));
        if (name is null) return false;

        value = Enum.Parse(type!, name);
        return true;
    }

    /// <summary>
    /// 列挙型の値を保存文字列（メンバ名）へ変換する。
    ///
    /// 宣言に無い数値（未定義の値がキャストで入っている場合）は名前を持たないため
    /// <see cref="UnsetValue"/> を返す。インスペクタ側は「候補に無い値」として扱う。
    /// </summary>
    public static string ToText(Type? type, object? value)
    {
        if (!IsEnumFieldType(type) || value is null) return UnsetValue;
        try { return Enum.GetName(type!, value) ?? UnsetValue; }
        catch (ArgumentException) { return UnsetValue; }   // 型が食い違う値が渡された場合
    }
}
