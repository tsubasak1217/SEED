// ============================================================
//  StagePlan.cs — 1 件のテンプレートを撮る計画（舞台のシーン・窓・切り出し・書き出し先）
// ============================================================

using SEEDEditor.Templates.Actors;

namespace SEEDEditor.Tools.SeedTemplateThumbnails.Stage;

/// <summary>撮った画像から正方形を切り出す矩形（窓の画素。撮った画像の大きさが違えば比で直す）。</summary>
/// <param name="X">左（画素）。</param>
/// <param name="Y">上（画素）。</param>
/// <param name="Size">一辺（画素）。</param>
public readonly record struct CropSquare(double X, double Y, double Size);

/// <summary>
/// 1 件のテンプレートを撮る計画。<see cref="StageSceneBuilder"/> が作り、ランタイムの手順が使う。
/// </summary>
public sealed class StagePlan
{
    /// <summary>カタログのエントリ。</summary>
    public required TemplateActorEntry Entry { get; init; }

    /// <summary>撮り方（カタログの thumbnail_sample）。</summary>
    public required ThumbnailSample Sample { get; init; }

    /// <summary>舞台の種類。</summary>
    public required StageKind Kind { get; init; }

    /// <summary>件の番号から作る札（舞台のスクリプトの合図に添える。前の件の合図と取り違えない）。</summary>
    public required string Ticket { get; init; }

    /// <summary>舞台のシーンのアセット相対パス（一時のプロジェクトの中。例 "__thumbnails/ui_button.scene"）。</summary>
    public required string SceneRelPath { get; init; }

    /// <summary>舞台のシーンの JSON。</summary>
    public required string SceneJson { get; init; }

    /// <summary>撮る窓の幅（画素）。</summary>
    public required int WindowWidthPx { get; init; }

    /// <summary>撮る窓の高さ（画素）。</summary>
    public required int WindowHeightPx { get; init; }

    /// <summary>正方形への収め方。</summary>
    public required ThumbnailFit Fit { get; init; }

    /// <summary>切り出す正方形（<see cref="ThumbnailFit.Crop"/> のとき）。</summary>
    public required CropSquare Crop { get; init; }

    /// <summary>一緒にコピーするファイル（ライブラリ相対パス。カタログの requires と受け皿が開く面）。</summary>
    public required IReadOnlyList<string> RequiredRelPaths { get; init; }

    /// <summary>書き出す画像の絶対パス（templates/&lt;フォルダ&gt;/thumbnails/&lt;名前&gt;.png）。</summary>
    public required string OutputPath { get; init; }

    /// <summary>テンプレートのファイル名（拡張子なし。--only と一覧の表示に使う）。</summary>
    public string Name => Path.GetFileNameWithoutExtension(Entry.TemplateRelPath);
}
