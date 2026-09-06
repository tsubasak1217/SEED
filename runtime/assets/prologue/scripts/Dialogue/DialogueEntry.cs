// ============================================================================
//  DialogueEntry.cs
//  会話 1 行分のデータ定義（データドリブンの最小単位）。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// カメラの移動方法を表す文字列定数。
///
/// 【なぜ列挙型でなく文字列か】
/// インスペクタの構造体リストは列挙型メンバを読み取り専用表示にしてしまうため
/// （docs/scripting_api.md「構造体のリスト」参照）、シーン側から編集できる
/// 文字列で持ち、比較はこの定数を通して行う（直書きによるタイプミスを防ぐ）。
/// </summary>
public static class DialogueCameraMode
{
    /// <summary>即座に目標のカメラ姿勢へ切り替える（カット）。</summary>
    public const string Cut = "cut";

    /// <summary>目標のカメラ姿勢へ時間を掛けて滑らかに移動する。</summary>
    public const string Lerp = "lerp";

    /// <summary>未入力（空文字）のときに採用する既定の移動方法。</summary>
    public const string Default = Cut;

    /// <summary>
    /// 入力文字列を既知のモードへ正規化する。
    /// 空文字・未知の文字列は <see cref="Default"/> として扱い、
    /// データの打ち間違いで会話が止まらないようにする。
    /// </summary>
    /// <param name="mode">シーンデータに書かれた文字列。</param>
    /// <returns><see cref="Cut"/> または <see cref="Lerp"/>。</returns>
    public static string Normalize(string mode)
    {
        if (string.IsNullOrEmpty(mode)) return Default;
        // 大文字小文字・前後の空白の揺れを吸収する（データ入力の事故対策）
        var trimmed = mode.Trim().ToLowerInvariant();
        return trimmed == Lerp ? Lerp : Cut;
    }
}

/// <summary>
/// 会話 1 行分のデータ。
///
/// 【責務】
///  「誰が」「何を喋り」「そのときカメラをどこへ動かし」「前後に何を起動するか」
///  という 1 行ぶんの情報だけを持つ。表示や進行のロジックは一切持たない。
///
/// 【使い方（シーン側）】
///  DialogueDirector の「会話データ」リストへ 1 件ずつ追加する。
///  cameraTarget には空アクター（CamTarget_*）をドロップし、その Transform の
///  位置・回転がそのままカメラ姿勢として使われる。
/// </summary>
[System.Serializable]
public struct DialogueEntry
{
    /// <summary>名札に表示する話者名。空文字なら名札は空欄になる。</summary>
    [SerializeField(Label = "話者名", Tooltip = "名札に表示する名前（空文字なら空欄）")]
    public string speaker;

    /// <summary>本文。"\n" で改行できる（自動折り返しは無いので手動で改行を入れる）。</summary>
    [SerializeField(Label = "本文", Tooltip = "表示する台詞。\n で改行（自動折り返しは無い）")]
    public string text;

    /// <summary>
    /// この行で寄せるカメラ姿勢の目標。位置と回転をそのままカメラへコピーする。
    /// 未設定（IsValid == false）ならカメラは動かさない。
    /// </summary>
    [SerializeField(Label = "カメラ目標", Tooltip = "この台詞で寄せるカメラ姿勢の空アクター。未設定ならカメラは動かさない")]
    public SEED.Transform cameraTarget;

    /// <summary>カメラの移動方法（<see cref="DialogueCameraMode"/> の文字列）。</summary>
    [SerializeField(Label = "カメラ移動方法", Tooltip = "cut = 即切り替え / lerp = 補間移動")]
    public string cameraMode;

    /// <summary>lerp のときの移動時間（秒）。cut のときは使われない。</summary>
    [SerializeField(Label = "補間時間(秒)", Tooltip = "cameraMode が lerp のときの移動時間")]
    public float lerpDuration;

    /// <summary>この行の表示を開始した瞬間に呼ぶイベント（表情差し替え・SE 再生など）。</summary>
    [SerializeField(Label = "開始時イベント", Tooltip = "この台詞の表示を始めた瞬間に呼ばれる")]
    public SEED.ScriptEvent onStart;

    /// <summary>この行を読み終えて次へ進む瞬間に呼ぶイベント。</summary>
    [SerializeField(Label = "終了時イベント", Tooltip = "この台詞を読み終えて次へ進む瞬間に呼ばれる")]
    public SEED.ScriptEvent onEnd;
}
