// ============================================================================
//  MissionKind.cs
//  チュートリアルのミッションの「種類」を表す列挙型。
// ============================================================================

/// <summary>
/// ミッションの種類【判定クラスの選択キー】。
///
/// 1 つの値が 1 つの判定クラス（<see cref="IMission"/> の実装）に対応する。
/// 種類を増やすときは、この列挙子を足して
/// <see cref="MissionFactory"/> の対応表へ 1 行加えるだけでよい。
///
/// 保存形式はメンバ名の文字列なので、並べ替えても既存データの意味は変わらない。
/// </summary>
public enum MissionKind
{
    /// <summary>指定地点まで歩く（目印の半径内に入ったらクリア）。</summary>
    Move,

    /// <summary>竿を構えて左右を向く（右端・左端の 2 つのサブ目標）。</summary>
    Ready,

    /// <summary>構えから仕掛けを投げる（投げられたらクリア）。</summary>
    Cast,

    /// <summary>ウキが沈んだ瞬間に合わせる（やり取りが始まったらクリア）。</summary>
    Hook,

    /// <summary>1 周ミスなしでビートを刻む。</summary>
    Beat,

    /// <summary>魚を巻いて釣り上げる（ビートバトルなし）。</summary>
    Reel,

    /// <summary>指定した種類の魚を釣る（他の魚を釣っても続行）。</summary>
    CatchTarget,

    /// <summary>掛かった魚をより大きな魚に食わせる（わらしべ連鎖）。</summary>
    ChainCatch,

    /// <summary>指定した数の漂流物を拾う。</summary>
    DriftPickup,

    /// <summary>魚を釣り上げる（失敗しても掛かった状態から再開する）。</summary>
    Land,

    /// <summary>締めの演出（怪獣のジャンプとカメラ寄せ）。</summary>
    Cutscene,
}
