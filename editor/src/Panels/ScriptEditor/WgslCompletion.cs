using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>WGSL 補完候補の種別（表示記号と優先度の決定に使う）。</summary>
public enum WgslSymbolKind
{
    /// <summary>言語キーワード（fn / let / if など）。</summary>
    Keyword,
    /// <summary>型（f32 / vec3&lt;f32&gt; / ShadingSurface など）。</summary>
    Type,
    /// <summary>組み込み関数・契約ヘルパ関数。</summary>
    Function,
    /// <summary>構造体フィールド（ShadingSurface / LightSample のメンバ）。</summary>
    Field,
    /// <summary>定数（SHADING_* など）。</summary>
    Constant,
    /// <summary>関数骨格を挿入するスニペット（shade_default など）。</summary>
    Snippet,
    /// <summary>現在の文書内から拾った識別子（辞書に無い語）。</summary>
    DocumentWord,
}

/// <summary>
/// WGSL シェーディングアセット用の静的辞書補完エンジン。
///
/// Roslyn（C# の意味解析）には一切依存せず、
///   1. シェーディング契約のシンボル（ShadingSurface / LightSample / ヘルパ関数 / 定数）
///   2. WGSL 言語のキーワード・型・組み込み関数
///   3. 現在の文書内に出現する識別子（辞書に無いものを低優先で追加）
/// を候補として返す。
///
/// 【契約シンボルの正典】
///   runtime/src/engine/core/renderer/shaders/shading_contract.wgsl
///   ShadingSurface / LightSample にフィールドを追加・変更した場合は、
///   本ファイルの <see cref="SurfaceFields"/> / <see cref="LightFields"/> /
///   <see cref="ContractFunctions"/> / <see cref="ContractConstants"/> も必ず更新すること
///   （ここは正典の「写し」であり、自動生成ではない）。
/// </summary>
public static class WgslCompletion
{
    // ── 優先度（数値が大きいほど上に出る）──────────────────────
    // AvalonEdit の CompletionList は Priority 降順で整列する。
    // 文脈に直結する候補ほど高く、文書内の未知語は最下位にする。
    private const double PrioritySnippet      = 40.0;   // 関数骨格（最も打鍵数を削減できる）
    private const double PriorityField        = 30.0;   // 構造体フィールド（"." 直後の本命）
    private const double PriorityContractFunc = 20.0;   // 契約ヘルパ関数
    private const double PriorityBuiltinFunc  = 12.0;   // WGSL 組み込み関数
    private const double PriorityType         = 10.0;   // 型
    private const double PriorityKeyword      =  8.0;   // キーワード
    private const double PriorityConstant     =  6.0;   // 定数
    private const double PriorityDocumentWord =  1.0;   // 文書内の未知語（最下位）

    /// <summary>文書内識別子として拾う最小文字数（1〜2 文字の記号的な語はノイズになるため除外）。</summary>
    private const int DocumentWordMinLength = 3;

    /// <summary>文書内識別子として取り込む最大件数（巨大ファイルで候補が溢れるのを防ぐ）。</summary>
    private const int DocumentWordMaxCount = 200;

    /// <summary>文書内識別子の抽出対象とする最大文字数（これを超える文書では抽出を諦める）。</summary>
    private const int DocumentScanMaxLength = 200_000;

    /// <summary>
    /// メンバーアクセス判定でキャレット直前を何文字さかのぼって見るか。
    /// 「識別子 + '.' + 入力途中」はごく短い範囲に収まるため、全文コピー＋全文走査を避ける。
    /// </summary>
    private const int MemberAccessLookbackLength = 256;

    /// <summary>構造体名: シェーディング対象の表面情報。</summary>
    public const string TypeShadingSurface = "ShadingSurface";

    /// <summary>構造体名: 1 ライト分のサンプル情報。</summary>
    public const string TypeLightSample = "LightSample";

    /// <summary>補完候補 1 件。</summary>
    /// <param name="Name">挿入・フィルタに使う文字列。</param>
    /// <param name="Kind">種別（表示記号と優先度の決定に使う）。</param>
    /// <param name="Detail">ツールチップに出す説明（型・シグネチャ＋日本語説明）。</param>
    /// <param name="Priority">表示順の優先度（大きいほど上）。</param>
    /// <param name="SnippetBody">
    /// スニペットの挿入本文。null 以外なら <see cref="Name"/> ではなくこの本文を挿入する。
    /// 本文中の <see cref="CaretMarker"/> が確定後のキャレット位置、行頭には呼び出し行の
    /// インデントが自動付与される。
    /// </param>
    public sealed record Entry(
        string         Name,
        WgslSymbolKind Kind,
        string         Detail,
        double         Priority,
        string?        SnippetBody = null);

    /// <summary>スニペット本文内で確定後のキャレット位置を示すマーカー。</summary>
    public const string CaretMarker = "$0";

    // ── 契約シンボル（正典 = shading_contract.wgsl の写し）───────

    /// <summary>ShadingSurface のフィールド（フィールド名, 型, 日本語説明）。</summary>
    private static readonly (string Name, string Type, string Doc)[] SurfaceFields =
    {
        ("world_pos",     "vec3<f32>", "ワールド座標"),
        ("normal",        "vec3<f32>", "シェーディング法線 N（法線マップ適用後・正規化済み）"),
        ("geo_normal",    "vec3<f32>", "幾何法線 Ng（面そのものの向き）"),
        ("vertex_normal", "vec3<f32>", "補間頂点法線 Nv（法線マップ適用前）"),
        ("view_dir",      "vec3<f32>", "表面→カメラ方向 V（正規化済み）"),
        ("base_color",    "vec3<f32>", "アルベド（リニア）"),
        ("roughness",     "f32",       "ラフネス（0..1）"),
        ("metallic",      "f32",       "メタリック（0=誘電体 / 1=金属）"),
        ("occlusion",     "f32",       "アンビエントオクルージョン（0..1）"),
        ("emissive",      "vec3<f32>", "エミッシブ（リニア HDR）"),
        ("transmission",  "f32",       "拡散透過（0..1・逆光透け）"),
        ("user_data",     "f32",       "マテリアルの自由回線（0..1）"),
        ("render_tag",    "u32",       "アクタ単位のセマンティックタグ（0..15）"),
        ("shading_model", "u32",       "シェーディングモデル ID（0..3）"),
        ("frag_coord",    "vec2<f32>", "フラグメント座標（ピクセル座標）"),
    };

    /// <summary>LightSample のフィールド（フィールド名, 型, 日本語説明）。</summary>
    private static readonly (string Name, string Type, string Doc)[] LightFields =
    {
        ("direction",        "vec3<f32>", "表面→光源方向 L（正規化済み）"),
        ("color",            "vec3<f32>", "実効放射輝度（減衰・円錐・影まで織り込み済み）"),
        ("color_unshadowed", "vec3<f32>", "影を掛ける前の放射輝度"),
        ("distance",         "f32",       "光源までの距離（平行光は大きな定数）"),
        ("kind",             "u32",       "ライト種別（0=directional / 1=point / 2=spot / 3=rect）"),
    };

    /// <summary>契約が提供する標準ライブラリ関数（関数名, シグネチャ, 日本語説明）。</summary>
    private static readonly (string Name, string Signature, string Doc)[] ContractFunctions =
    {
        ("shading_saturate",       "fn shading_saturate(x: f32) -> f32",                         "0..1 へクランプ"),
        ("shading_saturate3",      "fn shading_saturate3(v: vec3<f32>) -> vec3<f32>",            "0..1 へクランプ（成分ごと）"),
        ("shading_nan_guard",      "fn shading_nan_guard(c: vec3<f32>) -> vec3<f32>",            "NaN/Inf/負値を除去し安全な放射輝度へ"),
        ("shading_luminance",      "fn shading_luminance(c: vec3<f32>) -> f32",                  "Rec.709 相対輝度"),
        ("shading_linear_to_srgb", "fn shading_linear_to_srgb(c: vec3<f32>) -> vec3<f32>",       "リニア→sRGB"),
        ("shading_srgb_to_linear", "fn shading_srgb_to_linear(c: vec3<f32>) -> vec3<f32>",       "sRGB→リニア"),
        ("shading_posterize",      "fn shading_posterize(x: f32, steps: f32) -> f32",            "階調の量子化（トゥーン段数化）"),
        ("shade_light",
            "fn shade_light(N: vec3<f32>, V: vec3<f32>, L: vec3<f32>, albedo: vec3<f32>, F0: vec3<f32>, metallic: f32, roughness: f32, radiance: vec3<f32>) -> vec3<f32>",
            "Cook-Torrance BRDF 1 ライト分"),
        ("shade_model_0",
            "fn shade_model_0(sf: ShadingSurface, li: LightSample) -> vec3<f32>",
            "エンジン標準 PBR（上書き不可・呼び出して土台にできる）"),
        ("distribution_ggx", "fn distribution_ggx(N: vec3<f32>, H: vec3<f32>, roughness: f32) -> f32",
            "GGX 法線分布項（pbr_common.wgsl 由来・利用可）"),
        ("geometry_smith",   "fn geometry_smith(N: vec3<f32>, V: vec3<f32>, L: vec3<f32>, roughness: f32) -> f32",
            "Smith 幾何減衰項（pbr_common.wgsl 由来・利用可）"),
        ("fresnel_schlick",  "fn fresnel_schlick(cos_theta: f32, F0: vec3<f32>) -> vec3<f32>",
            "Schlick 近似フレネル項（pbr_common.wgsl 由来・利用可）"),
    };

    /// <summary>契約が提供する定数（定数名, 型, 日本語説明）。</summary>
    private static readonly (string Name, string Type, string Doc)[] ContractConstants =
    {
        ("SHADING_CONTRACT_VERSION", "u32",       "契約バージョン（アセット先頭の // @shading_contract と対応）"),
        ("SHADING_RADIANCE_MAX",     "f32",       "放射輝度の上限（発散防止のクランプ値）"),
        ("SHADING_LUMA_REC709",      "vec3<f32>", "Rec.709 輝度係数"),
        ("SHADING_DIELECTRIC_F0",    "f32",       "誘電体の垂直反射率 F0（既定 0.04 相当）"),
        ("PI",                       "f32",       "円周率（pbr_common.wgsl 由来）"),
    };

    /// <summary>上書き可能なシェーディング関数のスニペット（関数名, 日本語説明）。</summary>
    private static readonly (string Name, string Doc)[] ShadeSnippets =
    {
        ("shade_default", "既定のシェーディング関数（shading_model の指定が無いマテリアルで呼ばれる）"),
        ("shade_model_1", "シェーディングモデル 1 のシェーディング関数"),
        ("shade_model_2", "シェーディングモデル 2 のシェーディング関数"),
        ("shade_model_3", "シェーディングモデル 3 のシェーディング関数"),
    };

    // ── WGSL 言語シンボル ───────────────────────────────────

    /// <summary>WGSL のキーワード。</summary>
    private static readonly string[] Keywords =
    {
        "fn", "let", "var", "const", "struct", "return", "if", "else", "for", "loop", "while",
        "break", "continue", "switch", "case", "default", "discard", "true", "false",
        "alias", "override", "bitcast",
    };

    /// <summary>WGSL の型（よく使う具体化済みベクタ・行列を含む）。</summary>
    private static readonly string[] Types =
    {
        "f32", "i32", "u32", "bool",
        "vec2", "vec3", "vec4",
        "vec2<f32>", "vec3<f32>", "vec4<f32>",
        "vec2<i32>", "vec3<i32>", "vec4<i32>",
        "vec2<u32>", "vec3<u32>", "vec4<u32>",
        "mat2x2", "mat3x3", "mat4x4",
        "mat2x2<f32>", "mat3x3<f32>", "mat4x4<f32>",
        "array", "atomic", "ptr",
        TypeShadingSurface, TypeLightSample,
    };

    /// <summary>WGSL の組み込み関数（関数名, シグネチャ概要, 日本語説明）。</summary>
    private static readonly (string Name, string Signature, string Doc)[] BuiltinFunctions =
    {
        ("dot",          "dot(a, b) -> f32",              "内積"),
        ("cross",        "cross(a, b) -> vec3<f32>",      "外積"),
        ("normalize",    "normalize(v) -> vecN",          "正規化"),
        ("length",       "length(v) -> f32",              "ベクトル長"),
        ("distance",     "distance(a, b) -> f32",         "2 点間距離"),
        ("mix",          "mix(a, b, t)",                  "線形補間"),
        ("pow",          "pow(x, y)",                     "べき乗"),
        ("exp",          "exp(x)",                        "指数関数"),
        ("exp2",         "exp2(x)",                       "2 のべき乗"),
        ("log",          "log(x)",                        "自然対数"),
        ("log2",         "log2(x)",                       "底 2 の対数"),
        ("sqrt",         "sqrt(x)",                       "平方根"),
        ("inverseSqrt",  "inverseSqrt(x)",                "平方根の逆数"),
        ("abs",          "abs(x)",                        "絶対値"),
        ("sign",         "sign(x)",                       "符号（-1/0/+1）"),
        ("min",          "min(a, b)",                     "最小値"),
        ("max",          "max(a, b)",                     "最大値"),
        ("clamp",        "clamp(x, lo, hi)",              "範囲クランプ（0..1 なら shading_saturate が簡潔）"),
        ("saturate",     "saturate(x)",                   "0..1 クランプ（未対応環境では shading_saturate を使う）"),
        ("select",       "select(f, t, cond)",            "条件選択（cond が true なら t）"),
        ("step",         "step(edge, x)",                 "ステップ関数"),
        ("smoothstep",   "smoothstep(lo, hi, x)",         "スムーズステップ"),
        ("floor",        "floor(x)",                      "切り捨て"),
        ("ceil",         "ceil(x)",                       "切り上げ"),
        ("round",        "round(x)",                      "四捨五入"),
        ("trunc",        "trunc(x)",                      "0 方向へ切り捨て"),
        ("fract",        "fract(x)",                      "小数部"),
        ("modf",         "modf(x)",                       "整数部と小数部に分解"),
        ("sin",          "sin(x)",                        "正弦"),
        ("cos",          "cos(x)",                        "余弦"),
        ("tan",          "tan(x)",                        "正接"),
        ("asin",         "asin(x)",                       "逆正弦"),
        ("acos",         "acos(x)",                       "逆余弦"),
        ("atan",         "atan(x)",                       "逆正接"),
        ("atan2",        "atan2(y, x)",                   "逆正接（2 引数）"),
        ("reflect",      "reflect(I, N)",                 "反射ベクトル"),
        ("refract",      "refract(I, N, eta)",            "屈折ベクトル"),
        ("faceForward",  "faceForward(N, I, Nref)",       "視線側を向く法線を返す"),
        ("all",          "all(v) -> bool",                "全成分が true か"),
        ("any",          "any(v) -> bool",                "いずれかの成分が true か"),
        ("dpdx",         "dpdx(x)",                       "画面 X 方向の微分"),
        ("dpdy",         "dpdy(x)",                       "画面 Y 方向の微分"),
        ("fwidth",       "fwidth(x)",                     "|dpdx| + |dpdy|"),
        ("transpose",    "transpose(m)",                  "行列の転置"),
        ("determinant",  "determinant(m)",                "行列式"),
    };

    // ── 文脈解決用の正規表現 ────────────────────────────────

    /// <summary>キャレット直前が「識別子 + '.'（+ 入力途中の識別子）」かを判定する。</summary>
    private static readonly Regex MemberAccessRegex =
        new(@"([A-Za-z_][A-Za-z0-9_]*)\s*\.\s*[A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>関数の仮引数宣言から「引数名: 型名」を拾う（型は契約構造体のみ対象）。</summary>
    private static readonly Regex ParamTypeRegex =
        new(@"([A-Za-z_][A-Za-z0-9_]*)\s*:\s*(ShadingSurface|LightSample)\b", RegexOptions.Compiled);

    /// <summary>文書内の識別子を拾う。</summary>
    private static readonly Regex IdentifierRegex =
        new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    // ── 公開 API ───────────────────────────────────────────

    /// <summary>
    /// キャレット位置に対する補完候補を組み立てて返す。
    /// </summary>
    /// <param name="text">文書の全文。</param>
    /// <param name="caretOffset">キャレットのオフセット。</param>
    /// <param name="prefix">入力済みの識別子（置換対象・フィルタ語）。文書内語の抽出から除外する。</param>
    public static IReadOnlyList<Entry> GetEntries(string text, int caretOffset, string prefix)
    {
        // 安全のためオフセットを文書範囲へクランプする（非同期処理との競合で範囲外になり得る）。
        if (caretOffset < 0) caretOffset = 0;
        if (caretOffset > text.Length) caretOffset = text.Length;

        // メンバーアクセス（"xxx." の直後）なら、対象のフィールドだけを返す。
        var memberOwner = ResolveMemberAccessOwner(text, caretOffset);
        if (memberOwner is not null)
            return BuildMemberEntries(memberOwner);

        // 通常の補完: 契約シンボル + 言語シンボル + 文書内識別子。
        var entries = new List<Entry>();
        entries.AddRange(BuildSnippetEntries());
        entries.AddRange(BuildContractEntries());
        entries.AddRange(BuildLanguageEntries());
        AddDocumentWords(entries, text, caretOffset, prefix);
        return entries;
    }

    // ── 文脈判定 ───────────────────────────────────────────

    /// <summary>
    /// キャレット直前が「識別子 + '.'」の形なら、その識別子の型名を返す。
    /// 型は「文書内の関数仮引数宣言（fn ...(name: ShadingSurface, ...)）」を正規表現で走査して解決する。
    ///
    /// 戻り値:
    /// - "ShadingSurface" / "LightSample": その構造体のフィールドのみを出す
    /// - "" （空文字）: '.' ではあるが型が特定できない → 両構造体のフィールドを出す
    /// - null: メンバーアクセスではない → 通常の補完
    /// </summary>
    private static string? ResolveMemberAccessOwner(string text, int caretOffset)
    {
        // キャレット直前の一定長だけを見る（毎打鍵で全文を走査しないため）
        int lookbackStart = Math.Max(0, caretOffset - MemberAccessLookbackLength);
        var head  = text[lookbackStart..caretOffset];
        var match = MemberAccessRegex.Match(head);
        if (!match.Success) return null;

        var owner = match.Groups[1].Value;

        // 変数名そのものが構造体名（型名を直接書いた場合）ならそれを採用する。
        if (owner is TypeShadingSurface or TypeLightSample) return owner;

        // 仮引数宣言から型を引く。同名が複数あれば最後に見つかった宣言（＝キャレットに近い側）を採る。
        string? resolved = null;
        foreach (Match param in ParamTypeRegex.Matches(text))
        {
            if (param.Groups[1].Value == owner)
                resolved = param.Groups[2].Value;
        }

        // 解決できなければ空文字（＝両構造体のフィールドを出す）。誤って通常補完へ倒さない。
        return resolved ?? string.Empty;
    }

    // ── 候補の構築 ─────────────────────────────────────────

    /// <summary>メンバーアクセス時のフィールド候補を返す（型が不明なら両構造体分）。</summary>
    private static List<Entry> BuildMemberEntries(string ownerType)
    {
        var entries = new List<Entry>();
        if (ownerType is TypeShadingSurface or "")
            entries.AddRange(SurfaceFields.Select(f => new Entry(
                f.Name, WgslSymbolKind.Field,
                $"{TypeShadingSurface}.{f.Name}: {f.Type}\n{f.Doc}", PriorityField)));
        if (ownerType is TypeLightSample or "")
            entries.AddRange(LightFields.Select(f => new Entry(
                f.Name, WgslSymbolKind.Field,
                $"{TypeLightSample}.{f.Name}: {f.Type}\n{f.Doc}", PriorityField)));
        return entries;
    }

    /// <summary>上書き可能なシェーディング関数の骨格スニペットを返す。</summary>
    private static IEnumerable<Entry> BuildSnippetEntries()
        => ShadeSnippets.Select(s => new Entry(
            s.Name,
            WgslSymbolKind.Snippet,
            $"fn {s.Name}(sf: {TypeShadingSurface}, li: {TypeLightSample}) -> vec3<f32>\n{s.Doc}\n（関数の骨格を挿入します）",
            PrioritySnippet,
            // 本文: 行頭インデントは挿入位置に合わせて付与される（WgslCompletionData 側）。
            $"fn {s.Name}(sf: {TypeShadingSurface}, li: {TypeLightSample}) -> vec3<f32> {{\n    {CaretMarker}\n}}"));

    /// <summary>契約シンボル（構造体・フィールド・ヘルパ関数・定数）の候補を返す。</summary>
    private static IEnumerable<Entry> BuildContractEntries()
    {
        // 構造体フィールドは "." を打たずに名前で引ける方が便利なため、通常補完にも含める
        // （所属が分かるよう Detail の先頭に構造体名を出す）。
        foreach (var f in SurfaceFields)
            yield return new Entry(f.Name, WgslSymbolKind.Field,
                $"{TypeShadingSurface}.{f.Name}: {f.Type}\n{f.Doc}", PriorityField);
        foreach (var f in LightFields)
            yield return new Entry(f.Name, WgslSymbolKind.Field,
                $"{TypeLightSample}.{f.Name}: {f.Type}\n{f.Doc}", PriorityField);

        foreach (var fn in ContractFunctions)
            yield return new Entry(fn.Name, WgslSymbolKind.Function,
                $"{fn.Signature}\n{fn.Doc}", PriorityContractFunc);

        foreach (var c in ContractConstants)
            yield return new Entry(c.Name, WgslSymbolKind.Constant,
                $"{c.Name}: {c.Type}\n{c.Doc}", PriorityConstant);
    }

    /// <summary>WGSL 言語のキーワード・型・組み込み関数の候補を返す。</summary>
    private static IEnumerable<Entry> BuildLanguageEntries()
    {
        foreach (var kw in Keywords)
            yield return new Entry(kw, WgslSymbolKind.Keyword, $"{kw}\nWGSL キーワード", PriorityKeyword);

        foreach (var t in Types)
        {
            // 契約構造体は説明を厚くする（フィールド一覧への入口になるため）。
            var doc = t switch
            {
                TypeShadingSurface => "シェーディング対象の表面情報（法線・アルベド・ラフネス等）",
                TypeLightSample    => "1 ライト分のサンプル情報（方向・放射輝度・種別）",
                _                  => "WGSL 型",
            };
            yield return new Entry(t, WgslSymbolKind.Type, $"{t}\n{doc}", PriorityType);
        }

        foreach (var fn in BuiltinFunctions)
            yield return new Entry(fn.Name, WgslSymbolKind.Function,
                $"{fn.Signature}\n{fn.Doc}（WGSL 組み込み）", PriorityBuiltinFunc);
    }

    /// <summary>
    /// 現在の文書から識別子を抽出し、辞書に無いものだけを低優先で追加する。
    /// 「今まさに入力中の語」（キャレット直前の prefix と一致する出現）は候補にしない
    /// （打った端から自分自身が候補に出るのを防ぐ）。
    /// </summary>
    private static void AddDocumentWords(List<Entry> entries, string text, int caretOffset, string prefix)
    {
        if (text.Length > DocumentScanMaxLength) return;

        var known = new HashSet<string>(entries.Select(e => e.Name), StringComparer.Ordinal);
        int added = 0;

        // 入力中の語の範囲（この範囲に重なる出現は自分自身なので無視する）
        int prefixStart = caretOffset - prefix.Length;

        foreach (Match m in IdentifierRegex.Matches(text))
        {
            if (added >= DocumentWordMaxCount) break;
            if (m.Length < DocumentWordMinLength) continue;

            // 入力中の語そのものは除外する
            if (prefix.Length > 0 && m.Index == prefixStart) continue;

            var word = m.Value;
            if (!known.Add(word)) continue;

            entries.Add(new Entry(word, WgslSymbolKind.DocumentWord,
                $"{word}\nこのファイル内の識別子", PriorityDocumentWord));
            added++;
        }
    }
}
