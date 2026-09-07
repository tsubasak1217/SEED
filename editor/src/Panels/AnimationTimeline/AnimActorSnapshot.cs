// ============================================================
//  AnimActorSnapshot.cs — 選択アクタの「現在値」抽出（純ロジック）
//
//  【役割】
//   ランタイムが push する ACTOR_COMPONENTS JSON から、アニメーション可能な
//   プロパティ（AnimPropertyRegistry の各エントリ）の**現在値**を取り出す。
//
//  【何のために必要か】
//   タイムラインの「キー挿入（I キー）」は、ビューポートでアクタを動かした
//   結果をそのままキーにするための機能。つまり挿入する値は
//   「いまアクタが持っている値」でなければならない。その値の唯一の入手経路が
//   ACTOR_COMPONENTS JSON なので、その解析をここへ切り出して単体テストする。
//
//  【JSON の形（InspectorPanel の解析と同じ）】
//   ・root.transform        … 3D: px,py,pz / ex,ey,ez（度）/ sx,sy,sz
//   ・root.canvas_transform … 2D: px,py / rotation / sx,sy
//   ・root.components[]     … type ごとのコンポーネント。
//        SpriteComponent / SkinnedSpriteComponent → cr,cg,cb,ca
//        TextComponent                            → text_r..text_a, font_size
//
//  【既知の制限】
//   複数スロット（同種コンポーネントを複数持つアクタ）は最初の 1 つだけを見る。
//   .anim 側の TrackTarget もスロットを表現できないため、この制限は
//   フォーマット側の制約と一致している。
//
//  本クラスは WPF に依存しない（editor/tests から直接リンクしてテストする）。
// ============================================================

using System.Collections.Generic;
using System.Text.Json;

namespace SEEDEditor.Panels.AnimationTimeline;

/// <summary>
/// 選択アクタの現在値スナップショット。
/// キーは (component, property) の組で、AnimPropertyRegistry の登録名と一致する。
/// </summary>
internal sealed class AnimActorSnapshot
{
    /// <summary>(component, property) → 値（value_type の要素数ぶん）。</summary>
    private readonly Dictionary<(string Component, string Property), float[]> _values = new();

    /// <summary>このスナップショットの対象アクタの DFS ID（未解析なら -1）。</summary>
    public int ActorDfsId { get; private init; } = -1;

    /// <summary>取得できたプロパティの (component, property) 一覧。</summary>
    public IEnumerable<(string Component, string Property)> AvailableProperties => _values.Keys;

    /// <summary>指定プロパティの現在値を取得する。未取得なら null。</summary>
    public float[]? TryGet(string component, string property)
        => _values.TryGetValue((component, property), out var v) ? v : null;

    /// <summary>値を持っているか。</summary>
    public bool Has(string component, string property) => _values.ContainsKey((component, property));

    /// <summary>1 件も値を持たない（＝キー挿入に使えない）か。</summary>
    public bool IsEmpty => _values.Count == 0;

    // ── 解析 ────────────────────────────────────────────────────

    /// <summary>
    /// ACTOR_COMPONENTS JSON を解析して現在値スナップショットを作る。
    /// 解析できないフィールドは単に「値なし」として扱い、例外は投げない
    /// （JSON の形が将来変わってもタイムラインが落ちないようにするため）。
    /// </summary>
    public static AnimActorSnapshot Parse(string json)
    {
        var snapshot = new AnimActorSnapshot();
        if (string.IsNullOrWhiteSpace(json)) return snapshot;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return snapshot;

            var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetInt32() : -1;
            var result = new AnimActorSnapshot { ActorDfsId = id };

            ReadTransform(root, result);
            ReadCanvasTransform(root, result);
            ReadComponents(root, result);
            return result;
        }
        catch (JsonException)
        {
            return snapshot;
        }
    }

    // ── 各セクションの読み取り ───────────────────────────────────

    /// <summary>3D の Transform（位置・Euler 回転・スケール）を読む。</summary>
    private static void ReadTransform(JsonElement root, AnimActorSnapshot dst)
    {
        if (!root.TryGetProperty("transform", out var t) || t.ValueKind != JsonValueKind.Object) return;

        dst.Set(TransformComponent, PositionProperty, F(t, "px"), F(t, "py"), F(t, "pz"));
        dst.Set(TransformComponent, RotationProperty, F(t, "ex"), F(t, "ey"), F(t, "ez"));
        dst.Set(TransformComponent, ScaleProperty,    F(t, "sx", 1f), F(t, "sy", 1f), F(t, "sz", 1f));
    }

    /// <summary>2D の CanvasTransform（位置・角度・スケール）を読む。</summary>
    private static void ReadCanvasTransform(JsonElement root, AnimActorSnapshot dst)
    {
        if (!root.TryGetProperty("canvas_transform", out var ct) || ct.ValueKind != JsonValueKind.Object) return;

        dst.Set(CanvasTransformComponent, PositionProperty, F(ct, "px"), F(ct, "py"));
        dst.Set(CanvasTransformComponent, RotationProperty, F(ct, "rotation"));
        dst.Set(CanvasTransformComponent, ScaleProperty,    F(ct, "sx", 1f), F(ct, "sy", 1f));
    }

    /// <summary>components 配列から Sprite / Text の色・フォントサイズを読む（各種別の最初の 1 つ）。</summary>
    private static void ReadComponents(JsonElement root, AnimActorSnapshot dst)
    {
        if (!root.TryGetProperty("components", out var comps) || comps.ValueKind != JsonValueKind.Array) return;

        foreach (var comp in comps.EnumerateArray())
        {
            if (comp.ValueKind != JsonValueKind.Object) continue;
            var type = comp.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";

            switch (type)
            {
                case SpriteComponentType:
                case SkinnedSpriteComponentType:
                    if (!dst.Has(SpriteComponent, ColorProperty))
                        dst.Set(SpriteComponent, ColorProperty,
                            F(comp, "cr", 1f), F(comp, "cg", 1f), F(comp, "cb", 1f), F(comp, "ca", 1f));
                    break;

                case TextComponentType:
                    if (!dst.Has(TextComponent, ColorProperty))
                        dst.Set(TextComponent, ColorProperty,
                            F(comp, "text_r", 1f), F(comp, "text_g", 1f),
                            F(comp, "text_b", 1f), F(comp, "text_a", 1f));
                    if (!dst.Has(TextComponent, FontSizeProperty) && comp.TryGetProperty("font_size", out _))
                        dst.Set(TextComponent, FontSizeProperty, F(comp, "font_size"));
                    break;
            }
        }
    }

    // ── 補助 ────────────────────────────────────────────────────

    /// <summary>値を登録する（同じキーは後勝ちにせず、呼び出し側で Has を確認する運用）。</summary>
    private void Set(string component, string property, params float[] values)
        => _values[(component, property)] = values;

    /// <summary>数値フィールドを読む（欠落・型違いは fallback）。</summary>
    private static float F(JsonElement el, string name, float fallback = 0f)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetSingle() : fallback;

    // ── 名前定数（AnimPropertyRegistry / ランタイムのレジストリ名と一致させる）──

    public const string TransformComponent       = "actor_transform";
    public const string CanvasTransformComponent = "canvas_transform";
    public const string SpriteComponent          = "sprite";
    public const string TextComponent            = "text";

    public const string PositionProperty = "position";
    public const string RotationProperty = "rotation";
    public const string ScaleProperty    = "scale";
    public const string ColorProperty    = "color";
    public const string FontSizeProperty = "font_size";

    /// <summary>ACTOR_COMPONENTS の components[].type 文字列。</summary>
    private const string SpriteComponentType        = "SpriteComponent";
    private const string SkinnedSpriteComponentType = "SkinnedSpriteComponent";
    private const string TextComponentType          = "TextComponent";

    /// <summary>
    /// 「全変換キー」で自動生成するトラックの組（位置・回転・スケール）。
    /// 3D / 2D どちらのコンポーネントかは呼び出し側がスナップショットの中身で決める。
    /// </summary>
    public static readonly string[] TransformProperties =
    {
        PositionProperty, RotationProperty, ScaleProperty,
    };
}
