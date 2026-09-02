using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor;

/// <summary>コンポーネントが対応するアクター種別。</summary>
public enum ComponentActorTarget
{
    /// <summary>2D/3D 両方に追加可能。</summary>
    Common,
    /// <summary>3D アクター専用。</summary>
    Actor3D,
    /// <summary>2D アクター専用。</summary>
    Actor2D,
}

/// <summary>
/// コンポーネント選択リストの 1 エントリ。
/// </summary>
/// <param name="TypeId">
/// ランタイムへ送る型 ID（ADD_COMPONENT の {type}）。プラグインは "Plugin:{名前}"。
/// </param>
/// <param name="Label">一覧に表示する名前。日本語や空白を含んでよい（表示専用）。</param>
/// <param name="DefaultName">
/// 追加時のスロット既定名（ADD_COMPONENT の {name}）。
/// <para>
/// <b>Label と別に持つ理由</b>: 表示名は読みやすさ優先で "Water Volume" のように
/// 空白入りにしたいが、スロット名は識別子的に "Water" としたい。
/// 以前は表示名とは別に switch 文で既定名を返す関数があり、
/// 「名前欄へ書き込む値（Label）」と「既定名と見なす値（switch の戻り値）」が
/// 食い違っていた。その結果、いったん別の種別を選んでから選び直すと
/// 「前に選んだ種別の名前のまま追加される」不具合になっていた。
/// 既定名をエントリ自身に持たせ、書き込みも判定も同じ値を使う。
/// </para>
/// </param>
/// <param name="Description">一覧に表示する説明文。</param>
/// <param name="Target">対応アクター種別。</param>
public sealed record ComponentEntry(
    string TypeId,
    string Label,
    string DefaultName,
    string Description,
    ComponentActorTarget Target = ComponentActorTarget.Common);

/// <summary>
/// アクターへ追加できる ECS コンポーネントの一覧（唯一の情報源）。
///
/// <para>
/// WPF に依存しない純粋なデータ／ロジックだけを置く。
/// ここに WPF 型を持ち込むと <c>editor/tests/ComponentCatalogTests</c> が
/// ビルドできなくなり、設計の崩れが即座に検出される。
/// </para>
/// </summary>
public static class ComponentCatalog
{
    /// <summary>プラグイン種別 ID の接頭辞。</summary>
    public const string PluginPrefix = "Plugin:";

    /// <summary>カテゴリ順のコンポーネント一覧。表示順もこの並びに従う。</summary>
    public static readonly IReadOnlyList<(string Category, IReadOnlyList<ComponentEntry> Items)> Categories =
        new List<(string, IReadOnlyList<ComponentEntry>)>
    {
        ("レンダリング", new List<ComponentEntry>
        {
            new("ModelComponent", "Model", "Model", "3D モデルをアクタにアタッチ", ComponentActorTarget.Actor3D),
            new("SkyboxComponent", "Skybox", "Skybox", "equirectangular（正距円筒）画像1枚を天球として描画。CameraLocked/WorldAnchored", ComponentActorTarget.Actor3D),
        }),
        ("環境", new List<ComponentEntry>
        {
            new("WaterVolumeComponent", "Water Volume", "Water", "海・池などの水領域。水面描画と水中判定を提供", ComponentActorTarget.Actor3D),
            new("WaterLinkComponent", "Water Link", "WaterLink", "2つの水域をつなぐ開口（扉・窓・穴・バルブ）。水位グラフで水が行き来する", ComponentActorTarget.Actor3D),
            new("InteractionSourceComponent", "Interaction Source", "Interaction", "動く物に付ける。移動速度を共有フィールドへ焼き、草を押し倒す（将来は水の波紋・雪泥の轍も）", ComponentActorTarget.Actor3D),
            new("CoverEmitterComponent", "Cover Emitter", "CoverEmitter", "地表へ雪・落ち葉・濡れを積もらせる。範囲は全域/直方体/マスク画像から選ぶ", ComponentActorTarget.Actor3D),
        }),
        ("UI", new List<ComponentEntry>
        {
            new("CanvasComponent", "Canvas", "Canvas", "UI 矩形領域をアクタにアタッチ（幅・高さ指定）。3D アクタにアタッチするとワールド空間に配置", ComponentActorTarget.Common),
            new("SpriteComponent", "Sprite", "Sprite", "2D スプライト画像をキャンバスに表示", ComponentActorTarget.Common),
            new("SkinnedSpriteComponent", "Skinned Sprite", "SkinnedSprite", ".sprite_mesh のメッシュを子アクター（ボーン）で変形して表示する 2D スプライト", ComponentActorTarget.Common),
            new("TextComponent", "Text", "Text", "キャンバスに文字列を表示（HUD の数値・ラベル）。内容はスクリプトから毎フレーム差し替えられる", ComponentActorTarget.Common),
        }),
        ("ライト", new List<ComponentEntry>
        {
            new("LightComponent", "Light", "Light", "光源（directional / point / spot / rect）をアクターにアタッチ。向き・位置は Transform から", ComponentActorTarget.Actor3D),
            new("JointAttachComponent", "ジョイントアタッチ", "JointAttach", "モデルのジョイント（ボーン）へ追従するソケット", ComponentActorTarget.Actor3D),
        }),
        ("エフェクト", new List<ComponentEntry>
        {
            new("ParticleEmitterComponent", "Particle Emitter", "ParticleEmitter", "GPUパーティクルエミッタ。放出レート・寿命・色・サイズ補間などをデータドリブンに設定", ComponentActorTarget.Actor3D),
        }),
        // 「ツール」カテゴリ: シーン編集の道具として使う汎用コンポーネント。
        // ControlPoint は川・巡回ルート・カメラフライスルーなど用途に依存しない
        // 「順序付き点列」そのものを提供する土台なので、特定用途の「環境」ではなく
        // 用途中立の「ツール」に置く（将来の同種コンポーネントもここへ集める）。
        ("ツール", new List<ComponentEntry>
        {
            new("ControlPointComponent", "Control Point", "ControlPoint", "シーン上に順序付きの点列を置く汎用パス。川・巡回ルート・カメラパスなどの共通土台", ComponentActorTarget.Actor3D),
        }),
        ("カメラ", new List<ComponentEntry>
        {
            new("CameraComponent", "Camera", "Camera", "Play モードで使用するゲームカメラ", ComponentActorTarget.Actor3D),
        }),
        ("描画", new List<ComponentEntry>
        {
            new("LineRendererComponent", "Line Renderer", "LineRenderer", "点列を結ぶ 3D の線を描く（釣り糸・ロープ・軌跡）。点列はスクリプトから毎フレーム更新できる", ComponentActorTarget.Actor3D),
        }),
        ("物理", new List<ComponentEntry>
        {
            new("ColliderComponent", "Collider", "Collider", "衝突判定形状・リジッドボディをアクターにアタッチ（Box・Sphere・Capsule、重力有無は内部で設定）", ComponentActorTarget.Actor3D),
            new("Collider2dComponent", "Collider 2D", "Collider2D", "2D コライダー・リジッドボディをアクターにアタッチ（Box・Circle・Capsule、ピクセル単位）", ComponentActorTarget.Common),
        }),
        ("サウンド", new List<ComponentEntry>
        {
            new("AudioComponent", "Audio Source", "Audio", "BGM/SE の再生。3D 距離減衰・パン対応", ComponentActorTarget.Common),
        }),
        ("アニメーション", new List<ComponentEntry>
        {
            new("AnimatorComponent", "Animator", "Animator", "キーフレームアニメーションクリップ（.anim）の再生", ComponentActorTarget.Common),
        }),
        ("入力", new List<ComponentEntry>
        {
            new("InputMapComponent", "InputMap", "InputMap", ".inputmap アセットをアクタにアタッチ", ComponentActorTarget.Common),
        }),
        ("スクリプト", new List<ComponentEntry>
        {
            new("ScriptComponent", "Script", "Script", "スクリプトをアクタにアタッチ", ComponentActorTarget.Common),
        }),
    };

    /// <summary>全カテゴリを平坦化した全エントリ（プラグインは含まない）。</summary>
    public static IEnumerable<ComponentEntry> AllEntries =>
        Categories.SelectMany(c => c.Items);

    /// <summary>
    /// ロード済みプラグイン名から「プラグイン」カテゴリのエントリを生成する。
    /// プラグインは表示名と既定名を区別しないため、両方にプラグイン名を使う。
    /// </summary>
    /// <param name="pluginNames">ロード済みプラグイン名。</param>
    public static List<ComponentEntry> PluginEntries(IReadOnlyList<string> pluginNames) =>
        pluginNames
            .Select(name => new ComponentEntry(
                $"{PluginPrefix}{name}",
                name,
                name,
                $"{name} プラグインをアクターにアタッチ",
                ComponentActorTarget.Common))
            .ToList();

    /// <summary>
    /// 型 ID に対応する既定のスロット名を返す。
    ///
    /// <para>
    /// カタログを唯一の情報源として引くため、表示一覧と既定名がずれることが
    /// 構造的に起こらない。未知の型 ID は型 ID そのものを返す（安全側）。
    /// </para>
    /// </summary>
    /// <param name="typeId">コンポーネント型 ID。</param>
    public static string DefaultNameOf(string typeId)
    {
        if (string.IsNullOrEmpty(typeId)) return string.Empty;

        // プラグインはカタログに載らないので接頭辞を外した名前を使う。
        if (typeId.StartsWith(PluginPrefix, StringComparison.Ordinal))
            return typeId[PluginPrefix.Length..];

        foreach (var entry in AllEntries)
        {
            if (entry.TypeId == typeId) return entry.DefaultName;
        }
        return typeId;
    }

    /// <summary>
    /// 選択種別が変わったときに名前入力欄へ入れるべき値を決める。
    ///
    /// <para>
    /// 規則: 名前欄が空か、<b>直前に選んでいた種別の既定名そのまま</b>（＝ユーザーが
    /// 手で書き換えていない）なら、新しい種別の既定名へ差し替える。
    /// ユーザーが自分で入力した名前は保つ。
    /// </para>
    /// <para>
    /// <b>不具合の再発防止</b>: 以前は「差し替える値」に表示名（Label）を使い、
    /// 「差し替えてよいか」の判定には別テーブルの既定名を使っていた。
    /// 両者が異なる種別（例: 表示 "Water Volume" / 既定名 "Water"）を一度選ぶと、
    /// 次に別の種別を選んでも判定が成立せず、前の種別の名前のまま
    /// ADD_COMPONENT が送られていた。書き込みと判定を同じ
    /// <see cref="DefaultNameOf"/> に統一して構造的に潰す。
    /// </para>
    /// </summary>
    /// <param name="currentText">現在の名前入力欄の内容。</param>
    /// <param name="prevTypeId">直前に選択していた型 ID（未選択なら null）。</param>
    /// <param name="newTypeId">新しく選択した型 ID。</param>
    /// <returns>名前入力欄へ設定すべき文字列。</returns>
    public static string NextDefaultName(string? currentText, string? prevTypeId, string newTypeId)
    {
        var text = currentText ?? string.Empty;
        bool untouched = string.IsNullOrEmpty(text)
                         || text == DefaultNameOf(prevTypeId ?? string.Empty);
        return untouched ? DefaultNameOf(newTypeId) : text;
    }
}
