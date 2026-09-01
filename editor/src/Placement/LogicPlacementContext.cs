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
    /// true のとき「配置元」「基準点」の選択は意味を持たないので非表示にする
    /// （制御点はアクタ相対座標で、実体を伴わないため）。
    /// </summary>
    public bool IsControlPointMode { get; init; }

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
