using System;

namespace SEED;

/// <summary>
/// GameObject の 2D キャンバストランスフォーム（CanvasTransform）へのアクセサ。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// 2D キャンバス上のスプライト等の位置・回転・スケールを制御する（3D の Transform に相当）。
/// プロパティへの代入は即座にゲーム世界へ反映される。
/// </summary>
public readonly struct CanvasTransform : IComponentHandle<CanvasTransform>
{
    /// <summary>この CanvasTransform が属するエンティティ。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキー）。</summary>
    private const string Comp = "CanvasTransform";

    // ── レイアウトの結果の欄（読み取り専用。Rust 側 runtime/src/engine/core/scripting/canvas_layout_results_api.rs と一致させる）──

    /// <summary>欄: 前のフレームの描画のレイアウトの表にこのノードがあったか（1 要素。0/1）。</summary>
    private const string FieldHasLayout = "has_layout";
    /// <summary>欄: レイアウトの大きさ（2 要素。ノードのキャンバスの単位）。</summary>
    private const string FieldLayoutSize = "layout_size";
    /// <summary>欄: 画面の矩形（4 要素。x, y, 幅, 高さの画素）。</summary>
    private const string FieldLayoutRect = "layout_rect";
    /// <summary>画面の矩形の要素数（x, y, 幅, 高さ）。</summary>
    private const int LayoutRectLength = 4;
    /// <summary>画面の矩形: 左上の X の位置。</summary>
    private const int LayoutRectX = 0;
    /// <summary>画面の矩形: 左上の Y の位置。</summary>
    private const int LayoutRectY = 1;
    /// <summary>画面の矩形: 幅の位置。</summary>
    private const int LayoutRectWidth = 2;
    /// <summary>画面の矩形: 高さの位置。</summary>
    private const int LayoutRectHeight = 3;

    internal CanvasTransform(Entity entity) { _entity = entity; }

    /// <summary>
    /// この CanvasTransform が指すエンティティ（アセンブリ内部用）。
    /// SEED.Draw が「この Canvas ノードのローカル空間」を指定するために使う。
    /// </summary>
    internal Entity Owner => _entity;

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<CanvasTransform>.ComponentKindName => Comp;
    static CanvasTransform IComponentHandle<CanvasTransform>.FromEntity(Entity slotEntity) => new(slotEntity);

    // ── 参照の生存判定 ─────────────────────────────────

    /// <summary>
    /// この参照が生存しているか（指すエンティティが実在し CanvasTransform を保持しているか）。
    ///
    /// [SerializeField] の参照フィールドで「解決できたか／破棄されていないか」を
    /// 判定するために使う。<b>null は「未設定」</b>（Nullable 宣言のみ）を意味し、
    /// <b>IsValid == false は「未解決または破棄済み」</b>を意味する。
    /// World が公開されていない場面（ライフサイクル外）でも false になる。
    /// </summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    // ── 所有アクタへの橋渡し ───────────────────────────────

    /// <summary>
    /// この CanvasTransform を持つアクタ（GameObject）。
    ///
    /// <para><b>なぜそのまま GameObject になるのか</b>：
    /// CanvasTransform / Transform だけは<b>アクタのルート entity へ直付け</b>されており
    /// （それ以外のコンポーネントはスロット entity 格納型。正典は
    /// <c>runtime/src/engine/core/scripting/host_api.rs</c> の <c>resolve_component_slot</c>）、
    /// この CanvasTransform が指す entity はアクタのルート entity そのものだからである。
    /// </para>
    ///
    /// <para><b>用途</b>：<c>[SerializeField]</c> の CanvasTransform 参照フィールドから、
    /// その<b>アクタの他コンポーネント</b>（Sprite / Text など）へ辿るための入口。
    /// <code>
    /// if (target.GameObject.GetComponent&lt;Sprite&gt;() is { } sprite) { sprite.Color = c; }
    /// </code>
    /// </para>
    ///
    /// <para><b><see cref="IsValid"/> が false のとき</b>：
    /// 参照が<b>未解決</b>なら entity は <see cref="Entity.None"/> なので
    /// <c>GameObject.IsValid</c> も false になる。参照先が<b>破棄済み</b>の場合は
    /// entity の世代が古いだけなので <c>GameObject.IsValid</c> は true を返しうるが、
    /// その場合も <c>GetComponent&lt;T&gt;()</c> は世代不一致で解決に失敗して null を返す。
    /// どちらの場合も例外にはならない。
    /// </para>
    /// </summary>
    public GameObject GameObject => new(_entity);

    /// <summary>XY 平面上の位置（親 Canvas 基準の相対座標・ワールドユニット）。</summary>
    public Vector2 Position
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "position", out var v) ? v : Vector2.Zero;
        set => ScriptHost.TrySetVec2(_entity, Comp, "position", value);
    }

    /// <summary>Z 軸周りの回転（度）。</summary>
    public float Rotation
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "rotation", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "rotation", value);
    }

    /// <summary>XY スケール。</summary>
    public Vector2 Scale
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "scale", out var v) ? v : Vector2.One;
        set => ScriptHost.TrySetVec2(_entity, Comp, "scale", value);
    }

    /// <summary>
    /// 回転・スケールの基準点（正規化 [0,1]）。
    /// (0,0)=コンテンツ左上、(0.5,0.5)=中央、(1,1)=右下が position に対応する。
    /// </summary>
    public Vector2 Pivot
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "pivot", out var v) ? v : Vector2.Zero;
        set => ScriptHost.TrySetVec2(_entity, Comp, "pivot", value);
    }

    /// <summary>
    /// 親 Canvas 内の position 基準点（正規化 [0,1]）。
    /// (0,0)=親 Canvas 左上、(0.5,0.5)=中央、(1,1)=右下。
    /// </summary>
    public Vector2 Anchor
    {
        get => ScriptHost.TryGetVec2(_entity, Comp, "anchor", out var v) ? v : Vector2.Zero;
        set => ScriptHost.TrySetVec2(_entity, Comp, "anchor", value);
    }

    /// <summary>
    /// このアクターのスクリーン座標（ウィンドウ左上原点・ピクセル。get のみ）。
    ///
    /// 親 Canvas のアンカー・スケールモード・親チェーンの回転などをすべて反映した
    /// 最終的な描画位置（ピボット点）を返す。エンジンが描画と同一の座標変換で
    /// フレームごとに計算した値のため、Position（親 Canvas 相対）と異なり
    /// 画面上の絶対位置として扱える。2D アクターでない場合は Zero。
    /// </summary>
    public Vector2 ScreenPosition
        => ScriptHost.TryGetScreenPosition(_entity, out var v) ? v : Vector2.Zero;

    // ── レイアウトの結果（読み取り専用。前のフレームの描画のレイアウトの表）────────────

    /// <summary>
    /// 前のフレームの描画のレイアウトの表にこのノードがあったか（get のみ）。
    /// false のとき <see cref="LayoutSize"/> と <see cref="LayoutRect"/> は Zero。
    ///
    /// <para><b>1 フレーム遅れ</b>：レイアウトの表はスクリプトのフェーズの後（描画）で作るので、Update などで読む値は
    /// <b>前のフレームの描画</b>の値（<see cref="CanvasScroll.ViewportSize"/> と同じ）。このフレームに位置・レイアウトの部品を
    /// 書き換えても、読めるのは次のフレームから（レイアウトが大きさを決めていない軸の <see cref="LayoutSize"/> だけは、
    /// 読んだ時点の Sprite の大きさ・Text の枠そのもの）。</para>
    ///
    /// <para><b>false になるとき</b>：まだ描画していない（Play の最初のフレーム・シーンを読み込んで最初の描画の前・このフレームに生成したノード）、
    /// 3D ワールドキャンバス（CanvasComponent を持つ 3D アクター）の下のノード、3D アクターの下のノード、フォルダ、
    /// エディタの Edit（読むのは Play のゲームの画面の表だけ）。非表示・非アクティブのノードは表の行があるので true。</para>
    /// </summary>
    public bool HasLayout => ScriptHost.TryGetBool(_entity, Comp, FieldHasLayout, out var b) && b;

    /// <summary>
    /// レイアウトが決めたこのノードの大きさ（get のみ。前のフレームの描画の値。<see cref="HasLayout"/> が false なら Zero）。
    ///
    /// <para><b>単位</b>：このノードのキャンバスの単位（<see cref="Sprite.Width"/> / <see cref="Sprite.Height"/> と同じ。dp のキャンバスの下なら dp）。</para>
    ///
    /// <para><b>決め方</b>（軸ごと）：CanvasComponent を持つノードはキャンバス領域の大きさ（コンテナ・親に合わせる・安全領域・中身に合わせる・
    /// dp のルートを反映）。持たないノードは、レイアウトが大きさを決めた軸（コンテナが伸ばした・セルいっぱい・親に合わせた）ならその大きさ、
    /// それ以外は Sprite の大きさ → Text の枠 → レイアウトが割り当てた矩形 → 0。Sprite を持つノードでは描かれるスプライトの大きさと一致する
    /// （コンテナに伸ばされても <c>Sprite.Width</c> は元の値のままなので、描く大きさを知りたいときはこちらを読む）。</para>
    /// </summary>
    public Vector2 LayoutSize
        => ScriptHost.TryGetVec2(_entity, Comp, FieldLayoutSize, out var v) ? v : Vector2.Zero;

    /// <summary>
    /// このノードの矩形の画面の上の外接矩形（get のみ。前のフレームの描画の値。<see cref="HasLayout"/> が false なら <see cref="Rect.Zero"/>）。
    ///
    /// <para><b>座標</b>：画面の画素・描画ターゲットの左上が原点・Y 下向き（<see cref="ScreenPosition"/>・<see cref="Screen.SafeArea"/>・
    /// <see cref="Input.MousePos"/> と同じ）。dp にするには <see cref="Screen.DpScale"/> で割る。</para>
    ///
    /// <para><b>矩形</b>：<see cref="LayoutSize"/> の矩形を、このノードの位置・pivot・回転・Scale（描画と同じ行列）で画面へ写した 4 隅の外接矩形。
    /// Sprite を持つノードは描かれるスプライトの 4 隅、CanvasComponent を持つノードはキャンバス領域（CanvasClip で切る矩形と同じ）。
    /// 回転していれば 4 隅を囲む矩形。</para>
    /// </summary>
    public Rect LayoutRect
    {
        get
        {
            Span<float> v = stackalloc float[LayoutRectLength];
            return ScriptHost.TryGetFloats(_entity, Comp, FieldLayoutRect, v) == LayoutRectLength
                ? new Rect(v[LayoutRectX], v[LayoutRectY], v[LayoutRectWidth], v[LayoutRectHeight])
                : Rect.Zero;
        }
    }
}
