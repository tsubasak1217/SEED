using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SEEDEditor.Panels.SpriteRig.Mesh;
using SEEDEditor.Panels.SpriteRig.Model;

namespace SEEDEditor.Panels.SpriteRig;

/// <summary>
/// <see cref="SpriteRigCanvas"/> のうち Phase B1b（ボーン表示・ウェイト表示・両モードの入力）を担う部分。
///
/// <para>
/// 描画も入力も、状態は一切持たずに <see cref="SpriteRigDocument"/> を読み書きするだけにしてある
/// （キャンバスは「見せ方」と「マウスの意味づけ」だけの責務）。
/// </para>
/// </summary>
public sealed partial class SpriteRigCanvas
{
    // ── ボーン表示のパラメータ ──────────────────────────────────

    /// <summary>通常ボーンの塗り。</summary>
    private static readonly Brush BoneFillBrush = CreateBrush(Color.FromArgb(0xB0, 0x7E, 0xC8, 0xE3));

    /// <summary>選択中ボーンの塗り。</summary>
    private static readonly Brush SelectedBoneFillBrush = CreateBrush(Color.FromArgb(0xD0, 0xFF, 0xA7, 0x26));

    /// <summary>ボーン輪郭のペン。</summary>
    private static readonly Pen BoneOutlinePen = CreatePen(Color.FromRgb(0x10, 0x20, 0x28), 1.0);

    /// <summary>関節（根元・先端）の丸の塗り。</summary>
    private static readonly Brush JointBrush = CreateBrush(Color.FromRgb(0xEC, 0xEF, 0xF1));

    /// <summary>選択中ハンドルの丸の塗り。</summary>
    private static readonly Brush SelectedJointBrush = CreateBrush(Color.FromRgb(0xFF, 0xD5, 0x4F));

    /// <summary>親の先端と子の根元が離れているときに引く連結線。</summary>
    private static readonly Pen BoneLinkPen = CreateDashedPen(Color.FromArgb(0x90, 0xB0, 0xBE, 0xC5), 1.0);

    /// <summary>作成中ボーンのプレビュー線。</summary>
    private static readonly Pen PendingBonePen = CreatePen(Color.FromRgb(0x81, 0xC7, 0x84), 2.0);

    /// <summary>ブラシ円のペン。</summary>
    private static readonly Pen BrushCirclePen = CreatePen(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF), 1.2);

    /// <summary>選択頂点のマーカーのペン。</summary>
    private static readonly Pen SelectedVertexPen = CreatePen(Color.FromRgb(0xFF, 0xFF, 0xFF), 1.6);

    /// <summary>骨の胴（菱形）の根元側の半幅（画面ピクセル）。</summary>
    private const double BoneRootHalfWidth = 5.0;

    /// <summary>骨の胴の「肩」を置く位置（根元からの割合）。Unity 風の菱形にするための比率。</summary>
    private const double BoneShoulderRatio = 0.18;

    /// <summary>関節の丸の半径（画面ピクセル）。</summary>
    private const double JointRadius = 3.5;

    /// <summary>選択中の関節の丸の半径（画面ピクセル）。</summary>
    private const double SelectedJointRadius = 5.0;

    /// <summary>ウェイト表示の三角形の塗りの不透明度。</summary>
    private const byte WeightFillAlpha = 0x99;

    /// <summary>ウェイト表示の頂点マーカーの半径（画面ピクセル）。</summary>
    private const double WeightVertexRadius = 3.0;

    /// <summary>選択頂点マーカーの半径（画面ピクセル）。</summary>
    private const double SelectedVertexRadius = 5.5;

    /// <summary>ボーン色分け表示に使う色相の刻み（黄金角。隣り合うボーンの色が最も離れる）。</summary>
    private const double BoneHueStep = 137.507764;

    /// <summary>ボーン色分けの彩度。</summary>
    private const double BoneColorSaturation = 0.65;

    /// <summary>ボーン色分けの明度。</summary>
    private const double BoneColorValue = 0.95;

    /// <summary>ボーン名を変更したい（キャンバスでダブルクリックされた）ことを知らせる。</summary>
    public event Action<int>? BoneRenameRequested;

    /// <summary>ボーンや頂点の選択が変わったことを知らせる（パネルの一覧・詳細行の同期用）。</summary>
    public event Action? RigSelectionChanged;

    // ============================================================
    //  描画
    // ============================================================

    /// <summary>
    /// ボーンを「根元太・先端細の菱形 + 関節の丸」で描く（Unity Skinning Editor 風）。
    /// </summary>
    /// <param name="dc">描画コンテキスト。</param>
    /// <param name="document">対象ドキュメント。</param>
    private void DrawBones(DrawingContext dc, SpriteRigDocument document)
    {
        var bones = document.Mesh.Bones;
        if (bones.Count == 0) return;

        var globals = document.ComputeBoneGlobals();
        int selected = document.SelectedBoneIndex;

        // ── 親の先端と子の根元が離れている場合の連結線（先に描いて骨の下に敷く）──
        var parents = SpriteRigSkeleton.BuildParentIndices(bones);
        for (int i = 0; i < bones.Count; i++)
        {
            int parent = parents[i];
            if (parent < 0) continue;

            Vec2 parentTip = document.BoneTip(globals, parent);
            Vec2 head = document.BoneHead(globals, i);
            if (Vec2.DistanceSquared(parentTip, head) < Geometry2D.Epsilon) continue;
            dc.DrawLine(BoneLinkPen, ImageToScreen(parentTip), ImageToScreen(head));
        }

        // ── 骨の胴 ──
        for (int i = 0; i < bones.Count; i++)
        {
            Point head = ImageToScreen(document.BoneHead(globals, i));
            Point tip = ImageToScreen(document.BoneTip(globals, i));
            dc.DrawGeometry(i == selected ? SelectedBoneFillBrush : BoneFillBrush,
                BoneOutlinePen, BuildBoneShape(head, tip));
        }

        // ── 関節 ──
        for (int i = 0; i < bones.Count; i++)
        {
            Point head = ImageToScreen(document.BoneHead(globals, i));
            Point tip = ImageToScreen(document.BoneTip(globals, i));
            bool isSelected = i == selected;
            dc.DrawEllipse(isSelected ? SelectedJointBrush : JointBrush, BoneOutlinePen, head,
                isSelected ? SelectedJointRadius : JointRadius, isSelected ? SelectedJointRadius : JointRadius);
            dc.DrawEllipse(isSelected ? SelectedJointBrush : JointBrush, BoneOutlinePen, tip,
                JointRadius, JointRadius);
        }

        // ── 作成中ボーンのプレビュー ──
        if (document.PendingBoneHead is { } pendingHead)
        {
            Point from = ImageToScreen(pendingHead);
            Point to = ImageToScreen(document.PendingBoneTip);
            dc.DrawGeometry(BoneFillBrush, PendingBonePen, BuildBoneShape(from, to));
        }
    }

    /// <summary>
    /// 骨 1 本ぶんの菱形（根元太 → 先端細）のジオメトリを作る。
    /// </summary>
    /// <param name="head">根元の画面座標。</param>
    /// <param name="tip">先端の画面座標。</param>
    private static Geometry BuildBoneShape(Point head, Point tip)
    {
        double dx = tip.X - head.X;
        double dy = tip.Y - head.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);

        var geometry = new StreamGeometry();
        if (length < Geometry2D.Epsilon)
        {
            // 長さ 0 のときは点として扱う（描くものが無いので空ジオメトリ）
            geometry.Freeze();
            return geometry;
        }

        // 骨に沿う単位ベクトルと、その法線
        double ux = dx / length;
        double uy = dy / length;
        double nx = -uy;
        double ny = ux;

        // 太さは骨の長さに引きずられないよう、画面ピクセルで固定する
        double halfWidth = Math.Min(BoneRootHalfWidth, length * 0.5);
        double shoulder = length * BoneShoulderRatio;

        var shoulderLeft = new Point(head.X + ux * shoulder + nx * halfWidth,
                                     head.Y + uy * shoulder + ny * halfWidth);
        var shoulderRight = new Point(head.X + ux * shoulder - nx * halfWidth,
                                      head.Y + uy * shoulder - ny * halfWidth);

        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(head, isFilled: true, isClosed: true);
            ctx.LineTo(shoulderLeft, isStroked: true, isSmoothJoin: false);
            ctx.LineTo(tip, isStroked: true, isSmoothJoin: false);
            ctx.LineTo(shoulderRight, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// ウェイトの可視化（三角形の面塗り）。
    ///
    /// WPF の <c>DrawingContext</c> には頂点カラー補間が無いため、
    /// <b>三角形ごとに 3 頂点の色の平均</b>で塗る。頂点そのものには正確な色の丸を重ねるので、
    /// 面で大まかな分布を、点で正確な値を読めるようにしてある。
    /// </summary>
    /// <param name="dc">描画コンテキスト。</param>
    /// <param name="document">対象ドキュメント。</param>
    private void DrawWeightOverlay(DrawingContext dc, SpriteRigDocument document)
    {
        var mesh = document.Mesh;
        if (mesh.Vertices.Count == 0 || mesh.Triangles.Count == 0) return;

        var vertexColors = BuildVertexColors(document);
        // 同じ色のブラシを三角形ごとに作り直さないよう、1 回の描画の間だけ使い回す
        var brushCache = new Dictionary<Color, Brush>();

        for (int t = 0; t + Triangulation.IndicesPerTriangle <= mesh.Triangles.Count;
             t += Triangulation.IndicesPerTriangle)
        {
            int ia = mesh.Triangles[t];
            int ib = mesh.Triangles[t + 1];
            int ic = mesh.Triangles[t + 2];
            if (ia >= vertexColors.Length || ib >= vertexColors.Length || ic >= vertexColors.Length) continue;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(ImageToScreen(mesh.Vertices[ia]), isFilled: true, isClosed: true);
                ctx.LineTo(ImageToScreen(mesh.Vertices[ib]), isStroked: false, isSmoothJoin: false);
                ctx.LineTo(ImageToScreen(mesh.Vertices[ic]), isStroked: false, isSmoothJoin: false);
            }
            geometry.Freeze();

            Color average = AverageColor(vertexColors[ia], vertexColors[ib], vertexColors[ic]);
            dc.DrawGeometry(
                CachedBrush(brushCache, Color.FromArgb(WeightFillAlpha, average.R, average.G, average.B)),
                null, geometry);
        }

        // ── 頂点そのもの（正確な色）と選択マーカー ──
        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            Point center = ImageToScreen(mesh.Vertices[i]);
            dc.DrawEllipse(CachedBrush(brushCache, vertexColors[i]), null,
                center, WeightVertexRadius, WeightVertexRadius);
            if (document.SelectedVertices.Contains(i))
                dc.DrawEllipse(null, SelectedVertexPen, center, SelectedVertexRadius, SelectedVertexRadius);
        }
    }

    /// <summary>
    /// 頂点ごとの表示色を作る。
    ///
    /// 通常は<b>選択ボーンの影響度ヒートマップ</b>（青 → 赤）。
    /// <see cref="SpriteRigDocument.ShowAllBoneColors"/> が true のときは
    /// <b>全ボーンの色をウェイトで混ぜた色</b>にして、どこがどのボーンの領域かを一目で見せる。
    /// </summary>
    /// <param name="document">対象ドキュメント。</param>
    private static Color[] BuildVertexColors(SpriteRigDocument document)
    {
        var mesh = document.Mesh;
        var colors = new Color[mesh.Vertices.Count];

        if (document.ShowAllBoneColors)
        {
            var palette = BuildBonePalette(mesh.Bones.Count);
            for (int v = 0; v < colors.Length; v++)
            {
                double r = 0.0, g = 0.0, b = 0.0;
                if (v < mesh.Weights.Count)
                {
                    foreach (var influence in mesh.Weights[v])
                    {
                        if (influence.BoneIndex < 0 || influence.BoneIndex >= palette.Length) continue;
                        Color c = palette[influence.BoneIndex];
                        r += c.R * influence.Weight;
                        g += c.G * influence.Weight;
                        b += c.B * influence.Weight;
                    }
                }
                colors[v] = Color.FromRgb(ToByte(r), ToByte(g), ToByte(b));
            }
            return colors;
        }

        int selected = document.SelectedBoneIndex;
        for (int v = 0; v < colors.Length; v++)
        {
            double weight = selected < 0 ? 0.0 : document.GetInfluenceWeight(v, selected);
            colors[v] = HeatColor(weight);
        }
        return colors;
    }

    /// <summary>
    /// ボーン色分け表示のパレットを作る（黄金角で色相を回して隣接ボーンの色を離す）。
    /// </summary>
    /// <param name="boneCount">ボーン本数。</param>
    public static Color[] BuildBonePalette(int boneCount)
    {
        var palette = new Color[Math.Max(boneCount, 1)];
        for (int i = 0; i < palette.Length; i++)
            palette[i] = FromHsv(i * BoneHueStep % 360.0, BoneColorSaturation, BoneColorValue);
        return palette;
    }

    /// <summary>
    /// ウェイト 0〜1 を「青 → シアン → 緑 → 黄 → 赤」のヒートマップ色へ写す。
    /// </summary>
    /// <param name="weight">影響度（0〜1）。</param>
    public static Color HeatColor(double weight)
    {
        double t = Math.Clamp(weight, 0.0, 1.0);
        // 色相 240°(青) → 0°(赤)。彩度・明度は一定にして、明るさの錯覚で値を誤読しないようにする
        return FromHsv(240.0 * (1.0 - t), 0.85, 0.95);
    }

    /// <summary>HSV から RGB の色を作る。</summary>
    /// <param name="hueDegrees">色相（度・0〜360）。</param>
    /// <param name="saturation">彩度（0〜1）。</param>
    /// <param name="value">明度（0〜1）。</param>
    private static Color FromHsv(double hueDegrees, double saturation, double value)
    {
        double hue = ((hueDegrees % 360.0) + 360.0) % 360.0 / 60.0;
        int sector = (int)Math.Floor(hue) % 6;
        double fraction = hue - Math.Floor(hue);

        double p = value * (1.0 - saturation);
        double q = value * (1.0 - saturation * fraction);
        double t = value * (1.0 - saturation * (1.0 - fraction));

        (double r, double g, double b) = sector switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q),
        };
        return Color.FromRgb(ToByte(r * 255.0), ToByte(g * 255.0), ToByte(b * 255.0));
    }

    /// <summary>色に対応する Freeze 済みブラシをキャッシュから引く（無ければ作って登録する）。</summary>
    /// <param name="cache">1 回の描画の間だけ生きるキャッシュ。</param>
    /// <param name="color">欲しい色。</param>
    private static Brush CachedBrush(Dictionary<Color, Brush> cache, Color color)
    {
        if (cache.TryGetValue(color, out var brush)) return brush;
        brush = CreateBrush(color);
        cache[color] = brush;
        return brush;
    }

    /// <summary>3 色の平均を返す（三角形の面塗り用）。</summary>
    private static Color AverageColor(Color a, Color b, Color c) => Color.FromRgb(
        ToByte((a.R + b.R + c.R) / 3.0),
        ToByte((a.G + b.G + c.G) / 3.0),
        ToByte((a.B + b.B + c.B) / 3.0));

    /// <summary>0〜255 へ丸めてバイト化する。</summary>
    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0.0, 255.0);

    /// <summary>ウェイトモードのブラシ範囲をカーソル位置に描く。</summary>
    /// <param name="dc">描画コンテキスト。</param>
    /// <param name="document">対象ドキュメント。</param>
    private void DrawBrushCursor(DrawingContext dc, SpriteRigDocument document)
    {
        if (!IsMouseOver && !document.IsPaintingWeight) return;
        double radius = document.Brush.Radius * document.Zoom;
        dc.DrawEllipse(null, BrushCirclePen, ImageToScreen(_cursorInImage), radius, radius);
    }

    /// <summary>破線のペンを作る（Freeze 済み）。</summary>
    /// <param name="color">線の色。</param>
    /// <param name="thickness">線の太さ。</param>
    private static Pen CreateDashedPen(Color color, double thickness)
    {
        var pen = new Pen(CreateBrush(color), thickness)
        {
            DashStyle = new DashStyle(new double[] { 3.0, 3.0 }, 0.0),
        };
        pen.Freeze();
        return pen;
    }

    // ============================================================
    //  入力（ボーンモード）
    // ============================================================

    /// <summary>
    /// ボーンモードの左ボタン押下を処理する。
    /// </summary>
    /// <param name="position">押した位置（画像ピクセル）。</param>
    /// <param name="isDoubleClick">ダブルクリックか（名前変更を要求する）。</param>
    private void HandleBoneLeftDown(Vec2 position, bool isDoubleClick)
    {
        var document = _document!;
        double hitRadius = HitRadiusInScreenPixels / document.Zoom;

        if (isDoubleClick)
        {
            int target = document.HitTestBone(position, hitRadius);
            if (target >= 0)
            {
                document.SelectedBoneIndex = target;
                RigSelectionChanged?.Invoke();
                BoneRenameRequested?.Invoke(target);
            }
            return;
        }

        if (document.BoneTool == SpriteRigBoneTool.Create)
        {
            document.BeginBoneCreate(position);
            CaptureMouse();
            return;
        }

        // 選択 / 移動: まず関節ハンドル、無ければ骨そのものを掴む
        if (document.HitTestBoneHandle(position, hitRadius) is { } handle &&
            document.BeginBoneDrag(handle))
        {
            CaptureMouse();
            RigSelectionChanged?.Invoke();
            return;
        }

        int hit = document.HitTestBone(position, hitRadius);
        if (hit >= 0)
        {
            document.SelectedBoneIndex = hit;
            RigSelectionChanged?.Invoke();
        }
    }

    // ============================================================
    //  入力（ウェイトモード）
    // ============================================================

    /// <summary>
    /// ウェイトモードの左ボタン押下を処理する。
    ///
    /// <list type="bullet">
    ///   <item><b>左ドラッグ</b>: ブラシで塗る</item>
    ///   <item><b>Ctrl + 左</b>: カーソル下のボーンを対象ボーンに選ぶ</item>
    ///   <item><b>Shift + 左</b>: 頂点を選ぶ（詳細行の数値編集対象。Shift 押しっぱなしで複数選択）</item>
    /// </list>
    /// </summary>
    /// <param name="position">押した位置（画像ピクセル）。</param>
    private void HandleWeightLeftDown(Vec2 position)
    {
        var document = _document!;
        double hitRadius = HitRadiusInScreenPixels / document.Zoom;

        // Ctrl: ボーンの持ち替え
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            int bone = document.HitTestBone(position, hitRadius);
            if (bone >= 0)
            {
                document.SelectedBoneIndex = bone;
                RigSelectionChanged?.Invoke();
            }
            return;
        }

        // Shift: 頂点選択（追加選択）
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            int vertex = document.HitTestVertex(position, hitRadius);
            if (vertex >= 0)
            {
                if (!document.SelectedVertices.Add(vertex)) document.SelectedVertices.Remove(vertex);
                RigSelectionChanged?.Invoke();
            }
            return;
        }

        // 素の左ドラッグ: ペイント
        if (!document.BeginWeightStroke()) return;
        document.PaintWeightAt(position);
        CaptureMouse();
        RaiseModifiedFromRigging();
    }

    /// <summary>ボーン／ウェイトモードのマウス移動を処理する。</summary>
    /// <param name="position">カーソル位置（画像ピクセル）。</param>
    /// <param name="leftPressed">左ボタンが押されているか。</param>
    /// <returns>処理した場合 true（再描画は呼び出し側が行う）。</returns>
    private bool HandleRiggingMouseMove(Vec2 position, bool leftPressed)
    {
        var document = _document!;

        if (document.IsCreatingBone)
        {
            document.UpdateBoneCreate(position);
            return true;
        }
        if (document.IsDraggingBone && leftPressed)
        {
            document.UpdateBoneDrag(position);
            return true;
        }
        if (document.IsPaintingWeight && leftPressed)
        {
            if (document.PaintWeightAt(position)) RaiseModifiedFromRigging();
            return true;
        }
        // ウェイトモードはブラシ円がカーソルへ追従するので、常に描き直す
        return document.EditMode == SpriteRigEditMode.Weight;
    }

    /// <summary>ボーン／ウェイトモードの左ボタン解放を処理する。</summary>
    /// <returns>処理した場合 true。</returns>
    private bool HandleRiggingMouseUp()
    {
        var document = _document!;

        if (document.IsCreatingBone)
        {
            bool created = document.CommitBoneCreate();
            ReleaseMouseCapture();
            if (created)
            {
                RaiseModifiedFromRigging();
                RigSelectionChanged?.Invoke();
            }
            return true;
        }
        if (document.IsDraggingBone)
        {
            document.EndBoneDrag();
            ReleaseMouseCapture();
            RaiseModifiedFromRigging();
            return true;
        }
        if (document.IsPaintingWeight)
        {
            document.EndWeightStroke();
            ReleaseMouseCapture();
            RaiseModifiedFromRigging();
            return true;
        }
        return false;
    }

    /// <summary>
    /// ボーン／ウェイトモードのキー入力を処理する。
    /// </summary>
    /// <param name="key">押されたキー。</param>
    /// <returns>処理した場合 true。</returns>
    private bool HandleRiggingKeyDown(Key key)
    {
        var document = _document!;

        switch (key)
        {
            case Key.Escape:
                // 作成中の骨と連鎖を打ち切る
                if (document.EditMode == SpriteRigEditMode.Bone)
                {
                    document.CancelBoneCreate();
                    return true;
                }
                return false;

            case Key.Delete:
                if (document.EditMode == SpriteRigEditMode.Bone &&
                    document.SelectedBoneIndex >= 0 &&
                    document.DeleteBone(document.SelectedBoneIndex))
                {
                    RaiseModifiedFromRigging();
                    RigSelectionChanged?.Invoke();
                    return true;
                }
                return false;

            default:
                return false;
        }
    }

    /// <summary>ボーン／ウェイト操作による変更をパネルへ知らせる。</summary>
    private void RaiseModifiedFromRigging() => RaiseModified();
}
