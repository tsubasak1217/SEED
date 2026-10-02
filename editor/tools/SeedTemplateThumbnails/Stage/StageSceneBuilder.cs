// ============================================================
//  StageSceneBuilder.cs — 1 件のテンプレートを撮る「舞台」のシーンを組み立てる
//
//  【舞台の形】（舞台の種類ごと。ThumbnailSample の stage_kind）
//    ui（2D の既定）:
//      ThumbnailStage（dp のルートのキャンバス・ThumbnailStage.cs）
//      ├ Background（親いっぱい・テーマの color.background）
//      └ center: Body（親いっぱい・縦の Stack で中央寄せ）→ Holder0..（大きさの決まった枠）→ テンプレート
//        fill  : テンプレート（根の直下。親に合わせて広がる受け皿・画面）
//    model（3D の既定）: ThumbnailCamera（主カメラ・ThumbnailStage.cs）・ThumbnailLight（平行光）・テンプレート（原点）
//    root_canvas: ThumbnailStage（背景だけの dp のルート）・テンプレート（そのままルートのキャンバスとして）
//
//  【置くテンプレート】
//  エディタの「テンプレートアクタを追加」と同じく、テンプレートをまっさらな 1 本の木にしてから置く
//  （TemplateActorFlattener。入れ子の展開・プレハブの印の除去）。その上に見本の上書き（patch・instances）を当てる。
//  stand_in があれば自分の代わりにそのテンプレートを置く（受け皿に開かせる面など）。
// ============================================================

using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SEEDEditor.Packaging.Collect;
using SEEDEditor.Templates;
using SEEDEditor.Templates.Actors;

namespace SEEDEditor.Tools.SeedTemplateThumbnails.Stage;

/// <summary>舞台のシーンの組み立て。</summary>
public sealed class StageSceneBuilder
{
    /// <summary>舞台のシーンを置くフォルダ（一時のプロジェクトのアセットルートからの相対）。</summary>
    public const string SceneFolder = "__thumbnails";

    /// <summary>シーンの拡張子。</summary>
    private const string SceneExtension = ".scene";

    /// <summary>シーンの名前の欄名。</summary>
    private const string SceneNameKey = "name";

    /// <summary>シーンのアクタの配列の欄名。</summary>
    private const string SceneActorsKey = "actors";

    /// <summary>札の頭（"t" + 件の番号）。</summary>
    private const string TicketPrefix = "t";

    /// <summary>札の番号の書式（並びが文字の順でもそろうよう 3 桁）。</summary>
    private const string TicketNumberFormat = "000";

    /// <summary>アセットの仮想パスの頭。</summary>
    private const string AssetsScheme = "assets://";

    /// <summary>大きさの決まった枠とみなす最小の辺（これ以下は「親が大きさを決める」仮の 1）。</summary>
    private const double MinDefiniteSize = 1.0;

    /// <summary>枠の大きさを読むコンポーネントの型（先にあるものを優先。ランタイムの「自分の大きさ」と同じ順）。</summary>
    private static readonly string[] SizeSourceComponentTypes = ["SpriteComponent", "CanvasComponent"];

    /// <summary>中心から正方形の端までの割る数（一辺の半分）。</summary>
    private const double Halves = 2;

    /// <summary>大きさの欄名（Sprite・Canvas の data）。</summary>
    private const string WidthKey = "width", HeightKey = "height";

    /// <summary>シーンの JSON の書き方（日本語をそのまま・字下げ）。</summary>
    private static readonly JsonSerializerOptions SceneJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ── 舞台のスクリプトの欄名（templates/ui/scripts/ThumbnailStage.cs と同じ）──

    private const string FieldTicket = "Ticket";
    private const string FieldSettle = "SettleSeconds";
    private const string FieldGiveUp = "GiveUpSeconds";
    private const string FieldTarget = "Target";

    /// <summary>ライブラリルートの絶対パス。</summary>
    private readonly string _libraryRoot;

    /// <summary>ライブラリ相対パス → カタログのエントリ（stand_in の requires を引くため）。</summary>
    private readonly IReadOnlyDictionary<string, TemplateActorEntry> _entriesByPath;

    /// <summary>
    /// 組み立てを作る。
    /// </summary>
    /// <param name="libraryRoot">ライブラリルートの絶対パス。</param>
    /// <param name="catalog">カタログ（stand_in の requires を引く）。</param>
    public StageSceneBuilder(string libraryRoot, TemplateActorCatalog catalog)
    {
        _libraryRoot = Path.GetFullPath(libraryRoot);
        var map = new Dictionary<string, TemplateActorEntry>(AssetPathUtil.PathComparer);
        foreach (var e in catalog.Entries) map[e.TemplateRelPath] = e;
        _entriesByPath = map;
    }

    /// <summary>
    /// 1 件の計画を作る。
    /// </summary>
    /// <param name="index">件の番号（札に使う）。</param>
    /// <param name="entry">カタログのエントリ。</param>
    /// <param name="sample">撮り方。</param>
    /// <param name="errors">組み立てられなかった理由の積み先（1 つでもあれば計画は null）。</param>
    /// <returns>計画（組み立てられなければ null）。</returns>
    public StagePlan? Build(int index, TemplateActorEntry entry, ThumbnailSample sample, List<string> errors)
    {
        int before = errors.Count;
        string ticket = TicketPrefix + index.ToString(TicketNumberFormat, CultureInfo.InvariantCulture);

        // ── 1. 置くテンプレート（stand_in があればそちら）をまっさらな木にする ──
        string placedRel = entry.TemplateRelPath;
        var required = new List<string>(entry.RequiredRelPaths);
        if (sample.StandIn.Length > 0)
        {
            placedRel = AssetPathUtil.NormalizeRelative(FolderOf(entry.TemplateRelPath) + "/" + sample.StandIn);
            if (_entriesByPath.TryGetValue(placedRel, out var standInEntry)) required.AddRange(standInEntry.RequiredRelPaths);
            // 受け皿が実行中に読むのは自分（ダイアログの面など）なので、自分のファイルも一緒にコピーする
            required.Add(entry.TemplateRelPath);
        }
        var template = LoadFlattened(placedRel, errors);
        if (template is null) return null;

        var kind = sample.Kind ?? (TemplateActorFlattener.IsActor2D(template) ? StageKind.Ui : StageKind.Model);
        string templateName = template[TemplateActorFlattener.NameKey] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n : "";

        // ── 2. 舞台のスクリプトの欄（データの値の上に札・待ち秒・既定の対象を足す）──
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FieldTarget] = templateName,
        };
        foreach (var (key, value) in sample.ScriptFields) fields[key] = value;
        fields[FieldTicket] = ticket;
        fields[FieldSettle] = sample.SettleSeconds.ToString("R", CultureInfo.InvariantCulture);
        fields[FieldGiveUp] = ThumbnailStageDefaults.GiveUpSeconds.ToString("R", CultureInfo.InvariantCulture);
        var stageScript = StageNodes.Script(
            ThumbnailStageDefaults.StageScriptComponentName,
            AssetsScheme + ThumbnailStageDefaults.StageScriptRelPath,
            fields);

        // ── 3. 舞台の種類ごとにアクタを並べる ──
        var actors = new JsonArray();
        int windowW, windowH;
        switch (kind)
        {
            case StageKind.Ui:
            {
                var stage = BuildUiStage(template, sample, stageScript, errors);
                actors.Add(stage);
                windowW = ToPixels(sample.StageWidthDp);
                windowH = ToPixels(sample.StageHeightDp);
                break;
            }
            case StageKind.Model:
            {
                windowW = windowH = ThumbnailStageDefaults.ModelWindowPx;
                actors.Add(StageNodes.Node3D(ThumbnailStageDefaults.CameraName, sample.CameraPosition, sample.CameraRotation,
                    [StageNodes.MainCamera(sample.CameraFovDeg, windowW, windowH, ThumbnailStageDefaults.BackgroundSrgb), stageScript]));
                actors.Add(StageNodes.Node3D(ThumbnailStageDefaults.LightName, [0, 0, 0], sample.LightRotation,
                    [StageNodes.DirectionalLight(sample.LightIntensity)]));
                ApplyPatches(template, sample.Patch, null, errors);
                actors.Add(template);
                break;
            }
            default: // StageKind.RootCanvas
            {
                windowW = sample.WindowWidthPx;
                windowH = sample.WindowHeightPx;
                // 背景だけの dp のルート（舞台のスクリプトもここ）→ その上にテンプレートのルートのキャンバス
                actors.Add(StageNodes.Node2D(ThumbnailStageDefaults.StageRootName,
                    [StageNodes.Canvas(windowW / ThumbnailStageDefaults.RenderScale, windowH / ThumbnailStageDefaults.RenderScale, dpUnit: true), stageScript],
                    [Background()]));
                ApplyPatches(template, sample.Patch, null, errors);
                actors.Add(template);
                break;
            }
        }
        if (errors.Count > before) return null;

        // ── 4. 切り出し・収め方・書き出し先 ──
        var fit = sample.Fit ?? (kind == StageKind.Ui ? ThumbnailFit.Crop : ThumbnailFit.Contain);
        var crop = CropFor(kind, sample, windowW, windowH);
        string sceneRel = SceneFolder + "/" + FolderOf(entry.TemplateRelPath) + "_"
                          + Path.GetFileNameWithoutExtension(entry.TemplateRelPath) + SceneExtension;
        var scene = new JsonObject
        {
            [SceneNameKey] = Path.GetFileNameWithoutExtension(sceneRel),
            [SceneActorsKey] = actors,
        };

        return new StagePlan
        {
            Entry = entry,
            Sample = sample,
            Kind = kind,
            Ticket = ticket,
            SceneRelPath = sceneRel,
            SceneJson = scene.ToJsonString(SceneJsonOptions),
            WindowWidthPx = windowW,
            WindowHeightPx = windowH,
            Fit = fit,
            Crop = crop,
            RequiredRelPaths = required.Distinct(AssetPathUtil.PathComparer).ToList(),
            OutputPath = AssetPathUtil.ToAbsolute(_libraryRoot, entry.ThumbnailRelPath),
        };
    }

    // ============================================================
    //  UI の舞台
    // ============================================================

    /// <summary>dp のルート・背景・テンプレート（中央の枠か根の直下）を組む。</summary>
    private static JsonObject BuildUiStage(JsonObject template, ThumbnailSample sample, JsonObject stageScript, List<string> errors)
    {
        var root = StageNodes.Node2D(ThumbnailStageDefaults.StageRootName,
            [StageNodes.Canvas(sample.StageWidthDp, sample.StageHeightDp, dpUnit: true), stageScript],
            [Background()]);
        var children = StageNodes.ChildrenOf(root);

        // 並べる置き場（instances が無ければ 1 つ）
        var instances = sample.Instances.Count > 0 ? sample.Instances : [new JsonObject()];

        if (sample.Placement == StagePlacement.Fill)
        {
            foreach (var instancePatch in instances)
            {
                var copy = (JsonObject)template.DeepClone();
                ApplyPatches(copy, sample.Patch, instancePatch, errors);
                children.Add(copy);
            }
            return root;
        }

        // ── center: 大きさの決まった枠に入れ、縦の Stack で中央へ ──
        var size = HolderSize(template, sample, errors);
        if (size is null) return root;
        var body = StageNodes.Node2D(ThumbnailStageDefaults.BodyName,
            [StageNodes.Canvas(1, 1, dpUnit: false), StageNodes.LayoutFill(),
             StageNodes.CenterStack(ThumbnailStageDefaults.CenterPaddingDp, sample.SpacingDp)]);
        var bodyChildren = StageNodes.ChildrenOf(body);
        for (int i = 0; i < instances.Count; i++)
        {
            var copy = (JsonObject)template.DeepClone();
            ApplyPatches(copy, sample.Patch, instances[i], errors);
            bodyChildren.Add(StageNodes.Node2D(
                ThumbnailStageDefaults.HolderNamePrefix + i.ToString(CultureInfo.InvariantCulture),
                [StageNodes.Canvas(size.Value.Width, size.Value.Height, dpUnit: false)],
                [copy]));
        }
        children.Add(body);
        return root;
    }

    /// <summary>
    /// 中央に置く枠の大きさ（dp）。データの size が無ければ、テンプレートの根の Sprite、次に Canvas の大きさ
    /// （どちらも 1 より大きいときだけ。1 は「親が決める」仮の大きさ）。決まらなければ誤り。
    /// </summary>
    private static (double Width, double Height)? HolderSize(JsonObject template, ThumbnailSample sample, List<string> errors)
    {
        if (sample.HolderWidthDp is { } w && sample.HolderHeightDp is { } h) return (w, h);
        foreach (var type in SizeSourceComponentTypes)
        {
            var data = StageNodes.FindComponentDataByType(template, type);
            if (data?[WidthKey] is JsonValue wv && data[HeightKey] is JsonValue hv
                && wv.TryGetValue<double>(out var dw) && hv.TryGetValue<double>(out var dh)
                && dw > MinDefiniteSize && dh > MinDefiniteSize)
                return (dw, dh);
        }
        errors.Add("テンプレートの根に大きさの決まった Sprite / Canvas がありません。thumbnail_sample の size に枠の大きさ（dp）を書いてください");
        return null;
    }

    // ============================================================
    //  共通
    // ============================================================

    /// <summary>背景（親いっぱい・テーマの背景の色。テーマが当たる前も同じ色）。</summary>
    private static JsonObject Background() => StageNodes.Node2D(ThumbnailStageDefaults.BackgroundName,
    [
        StageNodes.FillSprite(ThumbnailStageDefaults.BackgroundSrgb),
        StageNodes.LayoutFill(),
        StageNodes.Script("ThemeStyle", "SEED.UI.ThemeStyle",
            new Dictionary<string, string> { ["SpriteColor"] = ThumbnailStageDefaults.BackgroundToken }),
    ]);

    /// <summary>共通の上書き → 置き場ごとの上書きの順に当てる。</summary>
    private static void ApplyPatches(JsonObject node, JsonObject shared, JsonObject? instance, List<string> errors)
    {
        ActorPatch.Apply(node, shared, errors);
        if (instance is not null) ActorPatch.Apply(node, instance, errors);
    }

    /// <summary>テンプレートを読んでまっさらな木にする（エディタの追加と同じ。入れ子の展開・印の除去）。</summary>
    private JsonObject? LoadFlattened(string libraryRel, List<string> errors)
    {
        var raw = TemplateActorInstaller.LoadNestedFromLibrary(_libraryRoot, libraryRel);
        if (raw is null)
        {
            errors.Add($"テンプレートを読めません: {libraryRel}");
            return null;
        }
        var flat = TemplateActorFlattener.Flatten(raw, source => TemplateActorInstaller.LoadNestedFromLibrary(_libraryRoot, source));
        foreach (var warning in flat.Warnings) errors.Add($"展開できません: {warning}");
        return flat.Root;
    }

    /// <summary>切り出す正方形（窓の画素）。ui は frame（dp）と focus、他は窓の短い辺の正方形を focus の位置に。</summary>
    private static CropSquare CropFor(StageKind kind, ThumbnailSample sample, int windowW, int windowH)
    {
        double shortSide = Math.Min(windowW, windowH);
        double size = kind == StageKind.Ui && sample.FrameDp is { } frame
            ? Math.Min(frame * ThumbnailStageDefaults.RenderScale, shortSide)
            : shortSide;
        double cx = sample.FocusX * windowW, cy = sample.FocusY * windowH;
        double x = Math.Clamp(cx - size / Halves, 0, windowW - size);
        double y = Math.Clamp(cy - size / Halves, 0, windowH - size);
        return new CropSquare(x, y, size);
    }

    /// <summary>dp を窓の画素にする（舞台の倍率を掛けて丸める）。</summary>
    private static int ToPixels(double dp) => (int)Math.Round(dp * ThumbnailStageDefaults.RenderScale);

    /// <summary>ライブラリ相対パスの最初の段（カタログのフォルダ）。</summary>
    private static string FolderOf(string libraryRel)
    {
        int slash = libraryRel.IndexOf('/');
        return slash < 0 ? libraryRel : libraryRel[..slash];
    }
}
