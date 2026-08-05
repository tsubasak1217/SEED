using System;

namespace SEEDEditor.Scripting;

/// <summary>
/// インスペクタの行末に「デフォルトに戻す」ボタン（⟲）を表示する属性。
///
/// ボタンを押すと、そのフィールドは**宣言時の初期化子の値**へ戻る。
/// 初期化子が無い場合は言語既定値（数値 0 / bool false / string 空 / 参照は未設定）。
/// リセットは通常の値編集と同じ経路（SET_SCRIPT_FIELD）を通るため Ctrl+Z で取り消せる。
///
/// [SerializeField] と併用したときのみ意味を持つ（インスペクタに出ないフィールドには行が無いため）。
/// 対応型は数値（float/double/int/long/short）・bool・string・参照フィールド。
/// [Serializable] ネストクラス**そのもの**に付けた場合はボタンを出さない
/// （子フィールドを一括で戻すと Undo が 1 手にまとまらないため。子フィールド個別には付けられる）。
/// </summary>
/// <example>[SerializeField, ResetButton] private float speed = 1.5f;</example>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class ResetButtonAttribute : Attribute
{
}
