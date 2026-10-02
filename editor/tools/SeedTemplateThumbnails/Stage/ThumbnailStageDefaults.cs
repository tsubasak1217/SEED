// ============================================================
//  ThumbnailStageDefaults.cs — 舞台（見本を撮る一時のシーン）の既定値
//
//  【役割】
//  カタログの thumbnail_sample に書かれていない値の既定をここ 1 か所に集める（マジックナンバーを散らさない）。
//  テンプレートごとの違いはカタログ（データ）が上書きする。ここを変えると全件の見た目が変わるので、
//  変えたら全件を撮り直す（docs/template_library.md §9.10「撮り直すとき」）。
// ============================================================

namespace SEEDEditor.Tools.SeedTemplateThumbnails.Stage;

/// <summary>舞台の既定値と、舞台の JSON に使う名前の表。</summary>
public static class ThumbnailStageDefaults
{
    // ============================================================
    //  大きさと倍率
    // ============================================================

    /// <summary>UI の舞台の幅（dp）。スマートフォンの縦画面の幅（360 dp）に合わせる。</summary>
    public const double StageWidthDp = 360;

    /// <summary>UI の舞台の高さ（dp。正方形にして、中央の切り出しがそのまま見本になるようにする）。</summary>
    public const double StageHeightDp = 360;

    /// <summary>
    /// 1 dp の画素数（ランタイムの表示倍率の模擬 SEED_SIM_SCALE_FACTOR に渡す）。1 より大きくして細かく描き、
    /// 縮めるときに面積の平均で滑らかにする。OS の表示倍率に左右されない（同じ PC なら何度撮っても同じ絵）。
    /// </summary>
    public const double RenderScale = 1.5;

    /// <summary>3D の舞台の窓（画素）。正方形。</summary>
    public const int ModelWindowPx = 540;

    /// <summary>ルートのキャンバスを置く舞台（2D の見本）の窓の幅（画素。16:9）。</summary>
    public const int RootCanvasWindowWidthPx = 960;

    /// <summary>ルートのキャンバスを置く舞台（2D の見本）の窓の高さ（画素）。</summary>
    public const int RootCanvasWindowHeightPx = 540;

    // ============================================================
    //  並べ方
    // ============================================================

    /// <summary>中央に置くときの、舞台の縁から部品までの余白（dp）。</summary>
    public const double CenterPaddingDp = 16;

    /// <summary>同じテンプレートを並べるとき（instances）の間隔（dp）。</summary>
    public const double InstanceSpacingDp = 12;

    // ============================================================
    //  時間
    // ============================================================

    /// <summary>見本の操作ができてから撮るまでの秒（開く動き・伸び縮みを落ち着かせる。motion.medium の倍程度）。</summary>
    public const double SettleSeconds = 0.8;

    /// <summary>見本の操作をやり直す上限の秒（受け皿・部品の OnStart を待つ）。</summary>
    public const double GiveUpSeconds = 10;

    // ============================================================
    //  色
    // ============================================================

    /// <summary>舞台の背景に当てるテーマのトークン（テーマの背景の色。部品が乗る地の色）。</summary>
    public const string BackgroundToken = "color.background";

    /// <summary>
    /// 背景の色（sRGB の 0〜255。既定のテーマ default_theme.json の暗い方の color.background = #121018）。
    /// テーマの色が当たる前の 1 フレーム目・3D の舞台の消去の色・余白（横長の見本を正方形に収めるときの上下）に使う。
    /// </summary>
    public static readonly byte[] BackgroundSrgb = [0x12, 0x10, 0x18];

    /// <summary>
    /// 全体を正方形に収める（contain）ときの余白の色（sRGB 0〜255。既定のテーマの暗い方の color.surface_variant = #2E2A3A）。
    /// 背景と同じ色にすると横長の画面の縁が見えなくなるので、少し明るい色で「画面の外」だと分かるようにする。
    /// </summary>
    public static readonly byte[] LetterboxSrgb = [0x2E, 0x2A, 0x3A];

    // ============================================================
    //  3D の舞台
    // ============================================================

    /// <summary>カメラの位置（m）。原点に置いた人の大きさのモデルを正面から収める。</summary>
    public static readonly double[] CameraPosition = [0, 1.0, 3.6];

    /// <summary>カメラの向き（度。オイラー角）。</summary>
    public static readonly double[] CameraRotation = [0, 0, 0];

    /// <summary>カメラの縦の画角（度）。</summary>
    public const double CameraFovDeg = 35;

    /// <summary>カメラの手前の切り取り（m）。</summary>
    public const double CameraNear = 0.1;

    /// <summary>カメラの奥の切り取り（m）。</summary>
    public const double CameraFar = 1000;

    /// <summary>平行光の向き（度。オイラー角。斜め上から）。</summary>
    public static readonly double[] LightRotation = [50, -30, 0];

    /// <summary>平行光の強さ（templates/scenes/cameraModeTest.scene の平行光と同じ）。</summary>
    public const double LightIntensity = 2.4;

    // ============================================================
    //  舞台のノードの名前（ランタイムのシーンの中で見分けるため）
    // ============================================================

    /// <summary>舞台の根（dp のキャンバス・ThumbnailStage を付ける）。</summary>
    public const string StageRootName = "ThumbnailStage";

    /// <summary>背景のノード。</summary>
    public const string BackgroundName = "Background";

    /// <summary>中央に並べる入れ物のノード。</summary>
    public const string BodyName = "Body";

    /// <summary>テンプレート 1 つ分の枠のノードの名前の頭（Holder0, Holder1 …）。</summary>
    public const string HolderNamePrefix = "Holder";

    /// <summary>3D の舞台のカメラのノード。</summary>
    public const string CameraName = "ThumbnailCamera";

    /// <summary>3D の舞台の光のノード。</summary>
    public const string LightName = "ThumbnailLight";

    /// <summary>舞台のスクリプト（ライブラリ相対パス。templates/ui/scripts/ThumbnailStage.cs）。</summary>
    public const string StageScriptRelPath = "ui/scripts/ThumbnailStage.cs";

    /// <summary>舞台のスクリプトのコンポーネント名。</summary>
    public const string StageScriptComponentName = "ThumbnailStage";
}
