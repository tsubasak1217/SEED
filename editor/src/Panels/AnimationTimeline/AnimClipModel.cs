// ============================================================
//  AnimClipModel.cs — .anim クリップの編集用データモデル
//
//  Rust 側（runtime/src/engine/animation/*）の AnimationClip / AnimTrack /
//  Keyframe 構造と 1:1 対応する編集用モデル。値は value_type に応じて
//  float 配列（要素数 1/2/3/4）または bool として保持し、シリアライズ時に
//  value_type 通りの JSON 表現（裸の数値・配列・真偽値）へ変換する。
//
//  events（イベントマーカー）は本パネルでは編集対象外だが、ロード→保存の
//  ラウンドトリップで内容を失わないよう、そのまま保持して書き戻す。
// ============================================================

using System.Collections.Generic;

namespace SEEDEditor.Panels.AnimationTimeline;

/// <summary>補間方式。Rust 側の文字列表現（"step"/"linear"/"bezier"）とそのまま対応する。</summary>
internal static class AnimInterp
{
    public const string Step   = "step";
    public const string Linear = "linear";
    public const string Bezier = "bezier";

    public static readonly string[] All = { Step, Linear, Bezier };
}

/// <summary>ループモード。Rust 側の文字列表現（"once"/"loop"/"ping_pong"）とそのまま対応する。</summary>
internal static class AnimLoopMode
{
    public const string Once     = "once";
    public const string Loop     = "loop";
    public const string PingPong = "ping_pong";

    public static readonly string[] All = { Once, Loop, PingPong };
}

/// <summary>値の型。Rust 側の value_type 文字列表現とそのまま対応する。</summary>
internal static class AnimValueType
{
    public const string Float = "float";
    public const string Vec2  = "vec2";
    public const string Vec3  = "vec3";
    public const string Color = "color";
    public const string Bool  = "bool";
}

/// <summary>トラックの対象（アクターパス・コンポーネント種別・プロパティ名）。</summary>
internal sealed class AnimTarget
{
    /// <summary>対象アクターへの相対パス。空文字列は「自分自身（クリップを持つアクター）」を表す。</summary>
    public string ActorPath { get; set; } = "";
    /// <summary>コンポーネント種別（例: "actor_transform"）。</summary>
    public string Component { get; set; } = "";
    /// <summary>プロパティ名（例: "position"）。</summary>
    public string Property { get; set; } = "";
}

/// <summary>
/// 1 キーフレーム。
/// Values は value_type に応じた要素数（float=1, vec2=2, vec3=3, color=4）の配列として保持する。
/// value_type が bool の場合は Values[0] が 0.0/1.0 として真偽を表す。
/// </summary>
internal sealed class AnimKey
{
    /// <summary>クリップ先頭からの時刻（秒）。</summary>
    public float Time { get; set; }
    /// <summary>値本体（value_type に応じた要素数）。</summary>
    public float[] Values { get; set; } = { 0f };
    /// <summary>補間方式（<see cref="AnimInterp"/>）。</summary>
    public string Interp { get; set; } = AnimInterp.Linear;
    /// <summary>ベジェ入タンジェント（省略可・bezier 補間時のみ意味を持つ）。</summary>
    public float[]? InTangent { get; set; }
    /// <summary>ベジェ出タンジェント（省略可・bezier 補間時のみ意味を持つ）。</summary>
    public float[]? OutTangent { get; set; }
}

/// <summary>1 トラック（対象プロパティ 1 つに対するキーフレーム列）。</summary>
internal sealed class AnimTrack
{
    public AnimTarget Target { get; set; } = new();
    /// <summary>値の型（<see cref="AnimValueType"/>）。</summary>
    public string ValueType { get; set; } = AnimValueType.Float;
    /// <summary>キーフレーム列（時刻昇順を維持する）。</summary>
    public List<AnimKey> Keys { get; set; } = new();
}

/// <summary>イベントマーカー（本パネルでは編集対象外・ラウンドトリップのため保持のみ）。</summary>
internal sealed class AnimEventMarker
{
    public float Time { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// .anim クリップ全体の編集用モデル。
/// AnimationTimelinePanel はこのインスタンスを直接編集し、保存時にファイルへ書き出す。
/// </summary>
internal sealed class AnimClip
{
    public string Name { get; set; } = "";
    public float Duration { get; set; } = 1f;
    /// <summary>ループモード（<see cref="AnimLoopMode"/>）。</summary>
    public string LoopMode { get; set; } = AnimLoopMode.Once;
    public List<AnimTrack> Tracks { get; set; } = new();
    /// <summary>編集対象外のイベント一覧（保持のみ）。</summary>
    public List<AnimEventMarker> Events { get; set; } = new();
}
