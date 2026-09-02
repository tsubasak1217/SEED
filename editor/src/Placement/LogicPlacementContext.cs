using System;

namespace SEEDEditor.Placement;

/// <summary>
/// ロジック配置ダイアログを「どこから・何に対して」開いたかを表す文脈。
///
/// <para>
/// ダイアログ本体は WPF の見た目とパラメータ編集だけに責任を持ち、
/// 「2D か 3D か」「新規アクタか制御点か」「グループをどのアクタの下に作るか」
/// といった呼び出し元固有の事情は<b>すべてこの型に集約</b>する。
/// これにより、ヒエラルキーの右クリックからも、インスペクタの
/// ControlPoint セクションからも、同じダイアログを使い回せる。
/// </para>
/// </summary>
public sealed class LogicPlacementContext
{
    /// <summary>2D 配置かどうか（true なら CanvasTransform ベース）。</summary>
    public bool Is2D { get; init; }

    /// <summary>
    /// グループフォルダを作る親アクタの DFS id。ルート直下に作るなら null。
    /// 基準点「対象アクタの位置」の解決にも使う。
    /// </summary>
    public uint? ParentDfs { get; init; }

    /// <summary>
    /// 制御点への追加モードかどうか。
    /// true のとき非表示にするのは「配置元」だけ（制御点は実体を伴わないので
    /// 空アクタ／アクタファイルの選択に意味が無い）。基準点・地形接地を含む
    /// それ以外の項目はアクタ配置とまったく同じものを出す。
    /// </summary>
    public bool IsControlPointMode { get; init; }

    /// <summary>
    /// 対象アクタがキャンバス上のアクタ（<c>CanvasTransform</c> 持ち＝2D アクタ）かどうか。
    ///
    /// <para>
    /// 制御点は座標データとしては常に 3 成分なので <see cref="Is2D"/> は false のまま扱うが、
    /// <b>親が 2D アクタなら地形は存在しない</b>ので接地は意味を持たない。
    /// アクタ配置の「2D なら接地チェックを隠す」規則を、制御点でも同じ形で効かせるための旗。
    /// </para>
    /// </summary>
    public bool TargetIsCanvasActor { get; init; }

    /// <summary>
    /// 地形接地が意味を持つ文脈か（＝「地面に沿わせる」を出してよいか）。
    /// 3D 空間に置かれるものだけが対象で、キャンバス上のものは対象外。
    /// </summary>
    public bool SupportsGrounding => !Is2D && !TargetIsCanvasActor;

    /// <summary>制御点モードでの対象アクタ DFS id。</summary>
    public uint ActorDfsId { get; init; }

    /// <summary>制御点モードでの対象スロット添字。</summary>
    public uint SlotIdx { get; init; }

    /// <summary>
    /// 制御点モードでの残り追加可能数（上限までの空き）。
    /// 超過ぶんはランタイム側でも切り詰められるが、ダイアログ上で
    /// 事前に警告するために受け取る。負値・未設定は「不明」として扱う。
    /// </summary>
    public int RemainingControlPointCapacity { get; init; } = -1;

    /// <summary>
    /// 生成するグループフォルダ名を、既存名と重複しない形へ整える関数。
    /// ヒエラルキーの命名規則（`名前(2)` 等）を再実装しないための注入点。
    /// null なら候補名をそのまま使う。
    /// </summary>
    public Func<string, string>? MakeUniqueGroupName { get; init; }
}
