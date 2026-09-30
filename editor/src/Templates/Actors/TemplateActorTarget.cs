// ============================================================
//  TemplateActorTarget.cs — テンプレートアクタの追加先
//
//  【役割】
//  「どこへ入れるか」を運ぶ値オブジェクトと、入れてよいかの判定（純粋な規則）。
//  ヒエラルキーの右クリックで決まり（右クリックしたノードの子 / 空白ならルート）、
//  テンプレートアクタの窓が開いている間は同じ追加先へ続けて入れる。
//
//  【規則】（既存の「2D アクタ」「3D アクタ」の追加と同じ。ランタイムの validate_reparent_kind と一致）
//  - 3D アクタは 2D アクタの子にできない。
//  - 2D アクタは「2D アクタ」か「Canvas を持つ 3D アクタ」の子にしかできない。
//  - キャンバス編集タブのルートへは 3D アクタを入れられない（2D はルートのキャンバスの子になる）。
//  - ルート（空白の右クリック）へ 2D を入れると、ランタイムが Canvas の規則で置く
//    （テンプレートのルートが Canvas を持たなければ最初のルートキャンバスの子。
//     キャンバスが 1 つも無ければ既定のキャンバスを作ってその子。ドロップ配置と同じ）。
//
//  【追加先の見失い】
//  ノードは DFS 番号で送るが、番号はツリーの編集でずれる。そこで右クリックの時点の
//  安定キー（ルートからの名前パス。ActorNode.StableKey）も持っておき、追加の直前に
//  ヒエラルキーで引き直す（HierarchyPanel.TryRefreshTemplateActorTarget）。
// ============================================================

namespace SEEDEditor.Templates.Actors;

/// <summary>
/// テンプレートアクタの追加先。
/// </summary>
public sealed record TemplateActorTarget
{
    /// <summary>既存の追加と同じ文言: 3D を 2D の子にしようとしたとき。</summary>
    public const string Reject3DUnder2D = "3Dアクターは2Dアクターの子にできません";

    /// <summary>既存の追加と同じ文言: 2D を Canvas の無い 3D の子にしようとしたとき。</summary>
    public const string Reject2DUnderPlain3D = "Canvasを持たない3Dアクターに2Dアクターは追加できません";

    /// <summary>既存の追加と同じ文言: キャンバス編集タブのルートへ 3D を入れようとしたとき。</summary>
    public const string Reject3DAtCanvasEditRoot = "キャンバス編集中はルートに3Dアクターを追加できません";

    /// <summary>追加先の世界線（ヒエラルキーが表示中のタブ。ビューポートは 0）。</summary>
    public uint WorldLine { get; init; }

    /// <summary>親の DFS 番号（null ならルートへ入れる）。</summary>
    public int? ParentDfs { get; init; }

    /// <summary>親の名前（窓の「追加先」に出す）。</summary>
    public string ParentName { get; init; } = "";

    /// <summary>親の安定キー（追加の直前に引き直すため）。</summary>
    public string ParentStableKey { get; init; } = "";

    /// <summary>親が 2D アクタか。</summary>
    public bool ParentIs2D { get; init; }

    /// <summary>親が Canvas コンポーネントを持つか。</summary>
    public bool ParentHasCanvas { get; init; }

    /// <summary>キャンバス編集タブか（ルートへ 3D を入れられない）。</summary>
    public bool IsCanvasEditTab { get; init; }

    /// <summary>ルートへ入れる追加先か。</summary>
    public bool IsRoot => ParentDfs is null;

    /// <summary>
    /// 窓に出す「追加先」の説明。
    /// </summary>
    /// <returns>例「ルート」「「Canvas」の子」。</returns>
    public string Describe() =>
        IsRoot
            ? "ルート（2D の部品は最初の Canvas の下へ。Canvas が無ければ作る）"
            : $"「{ParentName}」の子";

    /// <summary>
    /// このテンプレートをこの追加先へ入れてよいかを判定する。
    /// </summary>
    /// <param name="templateIs2D">テンプレートのルートが 2D アクタか。</param>
    /// <returns>入れられないなら理由（既存の追加と同じ文言）。入れられるなら null。</returns>
    public string? RejectReason(bool templateIs2D)
    {
        if (IsRoot)
            return !templateIs2D && IsCanvasEditTab ? Reject3DAtCanvasEditRoot : null;

        if (!templateIs2D && ParentIs2D) return Reject3DUnder2D;
        if (templateIs2D && !ParentIs2D && !ParentHasCanvas) return Reject2DUnderPlain3D;
        return null;
    }
}
