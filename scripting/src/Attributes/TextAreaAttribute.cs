using System;

namespace SEEDEditor.Scripting;

/// <summary>
/// <c>string</c> フィールドをインスペクタで**複数行のテキストボックス**として編集させる属性。
///
/// ## 使い方
/// <c>[SerializeField]</c> との併用が前提である（インスペクタに出ないフィールドの
/// 表示形状だけ指定しても意味がないため、単独で付けても何も起きない）。
/// <code>
/// [SerializeField, TextArea]                              private string memo = "";   // 既定 3 行
/// [SerializeField, TextArea(6)]                           private string body = "";   // 6 行
/// [SerializeField, TextArea(MinLines = 2, MaxLines = 8)]   private string note = "";
/// </code>
///
/// ## 効果があるのは string だけ
/// 数値・真偽値・参照・列挙型など <c>string</c> 以外のフィールドに付けても無視される
/// （行の形は型で決まるため。誤って付けても値は壊れない）。
/// <c>[System.Serializable]</c> 構造体リストの <c>string</c> メンバにも付けられる。
///
/// ## 改行の保存形式（重要）
/// エディタ ⇔ ランタイムの IPC は「1 行 = 1 コマンド」のテキストプロトコルなので、
/// 生の改行を含む値はそのまま送れない。そのため <c>[TextArea]</c> を付けた
/// **トップレベル（およびネストクラス）の string フィールド**は、
/// 改行をバックスラッシュ + n の 2 文字へエスケープした形で送受信・保存する
/// （エスケープ規約の正典は <see cref="SEED.ScriptTextArea"/>）。
/// 構造体リストの中の string は JSON 文字列として既にエスケープされるので二重にはしない。
/// スクリプト側のフィールドへ入る値は、どちらの経路でも**実際の改行を含む文字列**である。
/// </summary>
/// <example>[SerializeField(Label = "本文"), TextArea(4)] public string text;</example>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class TextAreaAttribute : Attribute
{
    /// <summary>行数を省略したときに使う既定の表示行数。</summary>
    public const int DefaultLines = 3;

    /// <summary>
    /// 表示行数（初期値）。実際の行数は <see cref="MinLines"/> / <see cref="MaxLines"/> で
    /// 挟み込んだ値になる（解決は <see cref="SEED.ScriptTextArea.ResolveLines"/> が正典）。
    /// </summary>
    public int Lines { get; }

    /// <summary>
    /// 表示行数の下限。0（既定）なら下限を指定しない。
    /// <see cref="Lines"/> より大きい値を指定した場合はこちらが優先される。
    /// </summary>
    public int MinLines { get; set; }

    /// <summary>
    /// 表示行数の上限。0（既定）なら上限を指定しない。
    /// <see cref="Lines"/> より小さい値を指定した場合はこちらが優先される。
    /// </summary>
    public int MaxLines { get; set; }

    /// <summary>行数を省略した形（既定 <see cref="DefaultLines"/> 行）。</summary>
    public TextAreaAttribute() : this(DefaultLines) { }

    /// <summary>行数を明示する形。</summary>
    /// <param name="lines">表示行数（1 未満は 1 行として扱われる）。</param>
    public TextAreaAttribute(int lines)
    {
        Lines = lines;
    }
}
