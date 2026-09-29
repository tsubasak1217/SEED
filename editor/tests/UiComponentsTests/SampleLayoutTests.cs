using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace UiComponentsTests;

/// <summary>
/// 見本のシーン（templates/ui/scenes/ui_gallery.scene・ui_charts.scene）と一覧の行のプレハブ（templates/ui/prefabs/list_row.actor）が、
/// dp の画面いっぱい＋安全領域に合わせる作り（W2 の手直し P2-5。docs/ui_theme.md §9・docs/ui_charts.md §8）になっているかを JSON から確かめる:
/// <list type="number">
///   <item>ルートの下の Body（安全領域 4 辺・親いっぱい・縦の CanvasStack）→ Page（伸ばす重み・切り抜き・中身の大きさは自動・窓そのものが縦の
///         CanvasStack）→ Content（左右の余白を持つ縦の CanvasStack）。背景（と重ねる面・トースト）はルートの直下（画面全体）に残す</item>
///   <item>最も狭い画面（360 dp）でも並べ切れる: 折り返し（CanvasWrap）の子の自分の幅・横の CanvasStack の伸ばさない子の和が、その入れ物の中身の幅以下。
///         コンテナが並べる子は大きさを持つ（枠の無い文字はレイアウトが測らない＝docs/canvas_camera_rework.md §6.3 の規則 4）</item>
///   <item>WakeHistory の ± はグラフの右の端に付く（anchor x 1）・一覧の行の Front は左右の余白を持つ縦の CanvasStack（文字と区切り線が行の幅に伸びる）</item>
/// </list>
/// 大きさの見積もりはレイアウトの「自分の大きさ」の規則（canvas_camera_rework.md §6.3 の規則 4）の簡略版: preferred → CanvasComponent を持たない
/// コンテナなら中身 → CanvasComponent → Sprite → Text の枠。単位は dp（見本のルートは dp）。
/// </summary>
public static class SampleLayoutTests
{
    /// <summary>確かめる最も狭い画面の幅（dp。W2 の手直し P2-5 の仕様で PC の模擬〈倍率 1.5〉でも撮る幅）。</summary>
    private const float NarrowestScreenDp = 360f;
    /// <summary>浮動小数の比較の許容量（dp）。</summary>
    private const float Eps = 1e-3f;
    /// <summary>右の端に付ける anchor の x。</summary>
    private const float RightAnchor = 1f;
    /// <summary>見本のシーン。</summary>
    private static readonly string[] Scenes = { "ui_gallery.scene", "ui_charts.scene" };

    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("P2-5 見本: ルート → Body（安全領域・縦の Stack）→ Page（flex・切り抜き・中身の大きさは自動・縦の Stack）→ Content（縦の Stack・余白）", () =>
        {
            foreach (var scene in Scenes)
            {
                var root = LoadScene(scene);
                var names = Children(root).Select(Name).ToArray();
                Check.True(names.Contains("Background") && names.Contains("Body"), $"{scene}: 背景と Body はルートの直下（{string.Join(",", names)}）");
                var background = Child(root, "Background");
                Check.True(Bool(Data(background, "CanvasLayoutItemComponent"), "fill_width") && Bool(Data(background, "CanvasLayoutItemComponent"), "fill_height"),
                    $"{scene}: 背景は画面の端まで（安全領域の外まで）塗る");

                var body = Child(root, "Body");
                Check.True(Data(body, "CanvasComponent").HasValue, $"{scene}: Body は CanvasComponent（安全領域で縮める箱）");
                var safe = Data(body, "CanvasSafeAreaComponent");
                Check.True(safe.HasValue && new[] { "enabled", "left", "top", "right", "bottom" }.All(k => Bool(safe, k)), $"{scene}: Body は安全領域の 4 辺");
                var bodyItem = Data(body, "CanvasLayoutItemComponent");
                Check.True(Bool(bodyItem, "fill_width") && Bool(bodyItem, "fill_height"), $"{scene}: Body は親いっぱい");
                var bodyStack = Data(body, "CanvasStackComponent");
                Check.Equal("vertical", Str(bodyStack, "direction"), $"{scene}: Body は縦の Stack");
                Check.Equal("stretch", Str(bodyStack, "cross_align"), $"{scene}: Body の子は幅いっぱい");

                var page = Child(body, "Page");
                Check.True(Num(Data(page, "CanvasLayoutItemComponent"), "flex") > 0f, $"{scene}: Page は残りの高さ（flex）");
                Check.True(Data(page, "CanvasClipComponent").HasValue, $"{scene}: Page は切り抜く");
                Check.Equal("auto", Str(Data(page, "CanvasScrollComponent"), "content_size"), $"{scene}: Page の中身の大きさは自動（中身の並びに合う）");
                // 窓そのものを縦の Stack にして Content へ幅を渡す（スクロールの軸は箱の長さを決めずに並べる＝中身の大きさ = 並べた長さ）
                var pageStack = Data(page, "CanvasStackComponent");
                Check.Equal("vertical", Str(pageStack, "direction"), $"{scene}: Page（窓）は縦の Stack");
                Check.Equal("stretch", Str(pageStack, "cross_align"), $"{scene}: Page の子（Content）は窓の幅いっぱい");

                var content = Child(page, "Content");
                var contentStack = Data(content, "CanvasStackComponent");
                Check.Equal("vertical", Str(contentStack, "direction"), $"{scene}: Content は縦の Stack");
                Check.Equal("stretch", Str(contentStack, "cross_align"), $"{scene}: Content の子は幅いっぱい");
                Check.True(Padding(content, "left") > 0f && Padding(content, "right") > 0f, $"{scene}: Content は左右の余白を持つ");
            }
            // ギャラリー: 帯はページの上（Body の縦の Stack の先頭）、重ねる面・トーストは画面全体（ルートの直下）
            var gallery = LoadScene("ui_gallery.scene");
            Check.Equal("ThemeBar,Page", string.Join(",", Children(Child(gallery, "Body")).Select(Name)), "ギャラリー: Body の子は帯とページ");
            var rootNames = Children(gallery).Select(Name).ToArray();
            Check.True(rootNames.Contains("Modals") && rootNames.Contains("Toasts"), "ギャラリー: 重ねる面・トーストはルートの直下（画面全体）");
        });

        h.Add("P2-5 見本: 360 dp の画面でも並べ切れる（折り返しの子・横の Stack の伸ばさない子の和が中身の幅以下・並べる子は大きさを持つ）", () =>
        {
            foreach (var scene in Scenes)
            {
                var body = Child(LoadScene(scene), "Body");
                int checkedContainers = Walk(body, NarrowestScreenDp, $"{scene}:Body");
                Check.True(checkedContainers > 0, $"{scene}: 確かめたコンテナがある");
            }
        });

        h.Add("P2-5 見本: WakeHistory の ± はグラフの右の端に付く・一覧の行の文字と区切り線は行の幅に伸びる", () =>
        {
            var history = FindNode(LoadScene("ui_charts.scene"), "WakeHistory");
            Check.True(history.HasValue, "グラフの見本に WakeHistory がある");
            foreach (var button in new[] { "ZoomOut", "ZoomIn" })
            {
                var t = Child(history!.Value, button).GetProperty("canvas_transform");
                Check.Close(RightAnchor, t.GetProperty("anchor")[0].GetSingle(), Eps, $"{button}: anchor x 1（グラフの右の端に付く）");
                Check.True(t.GetProperty("position")[0].GetSingle() < 0f, $"{button}: 位置は右の端から左へ");
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "prefabs", "list_row.actor")));
            var front = Child(doc.RootElement.Clone(), "Front");
            Check.True(Data(front, "CanvasComponent").HasValue, "Front は CanvasComponent（子の並びの箱）");
            var stack = Data(front, "CanvasStackComponent");
            Check.Equal("vertical", Str(stack, "direction"), "Front は縦の Stack");
            Check.Equal("stretch", Str(stack, "cross_align"), "Front の子は行の幅いっぱい");
            var pad = stack!.Value.GetProperty("padding");
            Check.True(pad.GetProperty("left").GetSingle() > 0f && pad.GetProperty("right").GetSingle() > 0f, "文字と区切り線の左右に余白");
            // 縦の並び（上の余白 ＋ 子の高さ）が行の高さに収まり、区切り線が行の下の端に来る
            float rowHeight = Num(Data(front, "CanvasComponent"), "height");
            float bottom = pad.GetProperty("top").GetSingle() + Children(front).Sum(c => OwnSize(c)[1]) + Num(stack, "spacing") * (Children(front).Count() - 1);
            Check.Close(rowHeight, bottom, Eps, "上の余白 ＋ Title ＋ Sub ＋ Divider = 行の高さ（区切り線が下の端）");
        });
    }

    // ── 並べ切れるかの見積もり ─────────────────────────────────────

    /// <summary>
    /// ノードの下をたどり、コンテナごとに「中身の幅に収まるか」を確かめる（確かめたコンテナの数を返す）。
    /// </summary>
    /// <param name="node">ノード。</param>
    /// <param name="available">ノードに使える幅（dp）。</param>
    /// <param name="path">失敗のメッセージのための道筋。</param>
    private static int Walk(JsonElement node, float available, string path)
    {
        int count = 0;
        float inner = available - PaddingX(node);
        var items = LaidOutChildren(node).ToList();
        if (Data(node, "CanvasWrapComponent") is { } wrap && Enabled(wrap))
        {
            count++;
            foreach (var child in items)
            {
                var size = OwnSize(child);
                CheckHasSize(child, size, path);
                Check.True(size[0] <= inner + Eps, $"{path}/{Name(child)}: 折り返しの子の幅 {size[0]} ≦ 中身の幅 {inner}");
                count += Walk(child, size[0], $"{path}/{Name(child)}");
            }
            return count;
        }
        if (Data(node, "CanvasStackComponent") is { } stack && Enabled(stack))
        {
            count++;
            bool horizontal = Str(stack, "direction") == "horizontal";
            bool stretch = Str(stack, "cross_align") == "stretch";
            float gaps = Num(stack, "spacing") * Math.Max(0, items.Count - 1);
            if (horizontal)
            {
                float fixedWidth = items.Where(c => Flex(c) <= 0f).Sum(c => OwnSize(c)[0]);
                Check.True(fixedWidth + gaps <= inner + Eps, $"{path}: 横の Stack の伸ばさない子の和 {fixedWidth + gaps} ≦ 中身の幅 {inner}");
                float totalFlex = items.Sum(Flex);
                float remaining = Math.Max(0f, inner - fixedWidth - gaps);
                foreach (var child in items)
                {
                    var size = OwnSize(child);
                    CheckHasSize(child, size, path);
                    float width = Flex(child) > 0f ? ClampMax(child, remaining * Flex(child) / totalFlex) : size[0];
                    count += Walk(child, width, $"{path}/{Name(child)}");
                }
            }
            else
            {
                foreach (var child in items)
                {
                    var size = OwnSize(child);
                    CheckHasSize(child, size, path);
                    if (!stretch) Check.True(size[0] <= inner + Eps, $"{path}/{Name(child)}: 縦の Stack の子の幅 {size[0]} ≦ 中身の幅 {inner}");
                    count += Walk(child, stretch ? inner : size[0], $"{path}/{Name(child)}");
                }
            }
            return count;
        }
        // コンテナでないノード: 親の幅に合わせる子（Page の Content など）だけ同じ幅でたどる（ほかの子は部品が自分で置く）
        foreach (var child in Children(node).Where(c => Bool(Data(c, "CanvasLayoutItemComponent"), "fill_width")))
            count += Walk(child, available, $"{path}/{Name(child)}");
        return count;
    }

    /// <summary>コンテナが並べる子は大きさを持つ（0 だと重なる。枠の無い文字は測られない）。</summary>
    private static void CheckHasSize(JsonElement child, float[] size, string path)
        => Check.True(size[0] > 0f && size[1] > 0f, $"{path}/{Name(child)}: コンテナが並べる子は大きさを持つ（{size[0]}×{size[1]}。枠の無い文字は測られない）");

    /// <summary>
    /// ノードの自分の大きさ（dp）の見積もり: preferred → CanvasComponent を持たないコンテナなら中身 → CanvasComponent → Sprite → Text の枠（軸ごと）。
    /// </summary>
    private static float[] OwnSize(JsonElement node)
    {
        var item = Data(node, "CanvasLayoutItemComponent");
        float[] preferred = { Num(item, "preferred_width"), Num(item, "preferred_height") };
        float[] content = ContentSize(node);
        var canvas = Data(node, "CanvasComponent");
        var sprite = Data(node, "SpriteComponent");
        var text = Data(node, "TextComponent");
        var size = new float[2];
        string[] canvasKeys = { "width", "height" }, textKeys = { "box_width", "box_height" };
        for (int a = 0; a < 2; a++)
        {
            size[a] = preferred[a] > 0f ? preferred[a]
                : canvas is null && content[a] > 0f ? content[a]
                : canvas.HasValue ? Num(canvas, canvasKeys[a])
                : sprite.HasValue ? Num(sprite, canvasKeys[a])
                : text.HasValue ? Num(text, textKeys[a])
                : 0f;
        }
        return size;
    }

    /// <summary>コンテナの中身の大きさ（dp。コンテナでなければ 0）。折り返しは 1 行に 1 つ並べたときの幅（最も狭く並べた幅）。</summary>
    private static float[] ContentSize(JsonElement node)
    {
        var items = LaidOutChildren(node).Select(OwnSize).ToList();
        if (items.Count == 0) return new float[2];
        float padX = PaddingX(node), padY = PaddingY(node);
        if (Data(node, "CanvasWrapComponent") is { } wrap && Enabled(wrap))
            return new[] { items.Max(s => s[0]) + padX, items.Sum(s => s[1]) + Num(wrap, "run_spacing") * (items.Count - 1) + padY };
        if (Data(node, "CanvasStackComponent") is { } stack && Enabled(stack))
        {
            float gaps = Num(stack, "spacing") * (items.Count - 1);
            return Str(stack, "direction") == "horizontal"
                ? new[] { items.Sum(s => s[0]) + gaps + padX, items.Max(s => s[1]) + padY }
                : new[] { items.Max(s => s[0]) + padX, items.Sum(s => s[1]) + gaps + padY };
        }
        return new float[2];
    }

    /// <summary>コンテナが並べる子（CanvasTransform を持ち、並べない指定でなく、詰める設定で隠れていない）。</summary>
    private static IEnumerable<JsonElement> LaidOutChildren(JsonElement node)
    {
        var container = Data(node, "CanvasWrapComponent") ?? Data(node, "CanvasStackComponent");
        if (container is null) yield break;
        bool collapse = Str(container, "hidden_children") != "keep_space";
        foreach (var child in Children(node))
        {
            if (!child.TryGetProperty("canvas_transform", out _)) continue;
            if (Bool(Data(child, "CanvasLayoutItemComponent"), "ignore_layout")) continue;
            if (collapse && child.TryGetProperty("visible", out var visible) && !visible.GetBoolean()) continue;
            yield return child;
        }
    }

    /// <summary>伸ばす重み（無ければ 0）。</summary>
    private static float Flex(JsonElement node) => Num(Data(node, "CanvasLayoutItemComponent"), "flex");

    /// <summary>幅の上限（max_width。0 はなし）で収める。</summary>
    private static float ClampMax(JsonElement node, float width)
    {
        float max = Num(Data(node, "CanvasLayoutItemComponent"), "max_width");
        return max > 0f ? Math.Min(width, max) : width;
    }

    /// <summary>コンテナの左右の余白の和（dp）。</summary>
    private static float PaddingX(JsonElement node) => Padding(node, "left") + Padding(node, "right");

    /// <summary>コンテナの上下の余白の和（dp）。</summary>
    private static float PaddingY(JsonElement node) => Padding(node, "top") + Padding(node, "bottom");

    /// <summary>コンテナの余白の 1 辺（コンテナでなければ 0）。</summary>
    private static float Padding(JsonElement node, string side)
    {
        var container = Data(node, "CanvasWrapComponent") ?? Data(node, "CanvasStackComponent");
        return container is { } c && c.TryGetProperty("padding", out var pad) && pad.TryGetProperty(side, out var v) ? v.GetSingle() : 0f;
    }

    // ── JSON の読み方 ─────────────────────────────────────────────

    /// <summary>出力へ写した見本のシーンのルート（最初のアクター）。</summary>
    private static JsonElement LoadScene(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenes", name)));
        return doc.RootElement.GetProperty("actors")[0].Clone();
    }

    /// <summary>ノードの名前。</summary>
    private static string Name(JsonElement node) => node.GetProperty("name").GetString() ?? "";

    /// <summary>ノードの子の並び。</summary>
    private static IEnumerable<JsonElement> Children(JsonElement node)
        => node.TryGetProperty("children", out var kids) ? kids.EnumerateArray() : Enumerable.Empty<JsonElement>();

    /// <summary>名前の直下の子（無ければ失敗）。</summary>
    private static JsonElement Child(JsonElement node, string name)
    {
        foreach (var c in Children(node))
            if (Name(c) == name) return c;
        throw new AssertionException($"{Name(node)} に子 {name} が無い");
    }

    /// <summary>名前のノードを深さ優先で探す。</summary>
    private static JsonElement? FindNode(JsonElement node, string name)
    {
        if (Name(node) == name) return node;
        foreach (var c in Children(node))
            if (FindNode(c, name) is { } found) return found;
        return null;
    }

    /// <summary>ノードの最初のその型のコンポーネントの data（無ければ null）。</summary>
    private static JsonElement? Data(JsonElement node, string type)
    {
        if (!node.TryGetProperty("components", out var comps)) return null;
        foreach (var c in comps.EnumerateArray())
        {
            var comp = c.GetProperty("component");
            if (comp.GetProperty("type").GetString() == type) return comp.GetProperty("data");
        }
        return null;
    }

    /// <summary>コンポーネントが有効か（enabled の欄が無ければ有効）。</summary>
    private static bool Enabled(JsonElement? data)
        => data is { } d && (!d.TryGetProperty("enabled", out var e) || e.GetBoolean());

    /// <summary>真偽の欄（無ければ false）。</summary>
    private static bool Bool(JsonElement? data, string key)
        => data is { } d && d.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>数の欄（無ければ 0）。</summary>
    private static float Num(JsonElement? data, string key)
        => data is { } d && d.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : 0f;

    /// <summary>文字の欄（無ければ空）。</summary>
    private static string Str(JsonElement? data, string key)
        => data is { } d && d.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
