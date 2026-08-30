using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SEEDEditor.Panels.SpriteRig.Mesh;
using SEEDEditor.Panels.SpriteRig.Model;

namespace SEEDEditor.Panels.SpriteRig;

/// <summary>
/// スプライトリグ 1 タブぶんの編集キャンバス（描画と入力だけを担当する）。
///
/// 画像・メッシュ・ツールの状態はすべて <see cref="SpriteRigDocument"/> が持ち、
/// このコントロールは「画像ピクセル座標 ←→ 画面座標」の変換と、
/// マウス／キー入力をドキュメントの編集操作へ橋渡しすることに専念する。
///
/// 座標系は <c>.sprite_mesh</c> と同じ「左上原点・+X 右・+Y 下」の画像ピクセルで、
/// 画面座標へは <c>screen = image * Zoom + Offset</c> の相似変換のみで写す
/// （回転を持たないので、逆変換も割り算だけで済む）。
/// </summary>
public sealed class SpriteRigCanvas : FrameworkElement
{
    // ── 表示パラメータ（マジックナンバー排除）──────────────────────

    /// <summary>キャンバスの背景色。</summary>
    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));

    /// <summary>画像の外周を示す枠線。</summary>
    private static readonly Pen ImageBorderPen = CreatePen(Color.FromRgb(0x50, 0x50, 0x50), 1.0);

    /// <summary>三角形のワイヤ表示色。</summary>
    private static readonly Pen TrianglePen = CreatePen(Color.FromArgb(0xAA, 0x4F, 0xC3, 0xF7), 1.0);

    /// <summary>外周輪郭の色。</summary>
    private static readonly Pen OuterContourPen = CreatePen(Color.FromRgb(0xFF, 0xC1, 0x07), 1.6);

    /// <summary>穴輪郭の色。</summary>
    private static readonly Pen HoleContourPen = CreatePen(Color.FromRgb(0xE5, 0x73, 0x73), 1.6);

    /// <summary>作図中ポリゴンの色。</summary>
    private static readonly Pen PendingPolygonPen = CreatePen(Color.FromRgb(0x81, 0xC7, 0x84), 1.6);

    /// <summary>ピクセルグリッドの色。</summary>
    private static readonly Pen PixelGridPen = CreatePen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 1.0);

    /// <summary>輪郭頂点ハンドルの塗り。</summary>
    private static readonly Brush ContourHandleBrush = CreateBrush(Color.FromRgb(0xFF, 0xC1, 0x07));

    /// <summary>内部点ハンドルの塗り。</summary>
    private static readonly Brush InteriorHandleBrush = CreateBrush(Color.FromRgb(0x90, 0xA4, 0xAE));

    /// <summary>選択中ハンドルの塗り。</summary>
    private static readonly Brush SelectedHandleBrush = CreateBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));

    /// <summary>ハンドルの枠線。</summary>
    private static readonly Pen HandleBorderPen = CreatePen(Color.FromRgb(0x20, 0x20, 0x20), 1.0);

    /// <summary>頂点ハンドルの一辺（画面ピクセル）。</summary>
    private const double HandleSize = 7.0;

    /// <summary>選択中ハンドルの一辺（画面ピクセル）。</summary>
    private const double SelectedHandleSize = 9.0;

    /// <summary>ホイール 1 ノッチあたりのズーム倍率。</summary>
    private const double ZoomStep = 1.15;

    /// <summary>ピクセルグリッドを描き始めるズーム倍率（これ未満では潰れるので描かない）。</summary>
    private const double PixelGridMinZoom = 6.0;

    /// <summary>ZoomToFit で画像の周囲に残す余白の比率。</summary>
    private const double FitMarginRatio = 0.04;

    /// <summary>ヒット判定半径（画面ピクセル）。実際の判定は画像ピクセルへ換算して使う。</summary>
    private const double HitRadiusInScreenPixels = 7.0;

    // ── 状態 ─────────────────────────────────────────────────────

    /// <summary>編集対象のドキュメント（null なら空表示）。</summary>
    private SpriteRigDocument? _document;

    /// <summary>表示用ビットマップ（ドキュメントの画像から 1 度だけ作る）。</summary>
    private BitmapSource? _bitmap;

    /// <summary>中ボタンパン中の直前カーソル位置（画面座標）。</summary>
    private Point? _panAnchor;

    /// <summary>カーソルの現在位置（画像ピクセル・作図プレビュー用）。</summary>
    private Vec2 _cursorInImage;

    /// <summary>ドキュメントの内容が変わったときに発火する（パネルがタイトル等を更新する）。</summary>
    public event Action? DocumentModified;

    /// <summary>編集対象のドキュメント。</summary>
    public SpriteRigDocument? Document
    {
        get => _document;
        set
        {
            _document = value;
            _bitmap = value == null ? null : SpriteImageLoader.CreateBitmap(value.Image);
            UpdateBitmapScalingMode();
            InvalidateVisual();
        }
    }

    /// <summary>コントロールを初期化する。</summary>
    public SpriteRigCanvas()
    {
        Focusable = true;
        // 背景を自前で描くため、透明部分でもマウスイベントを拾えるようにする
        ClipToBounds = true;
    }

    // ============================================================
    //  座標変換
    // ============================================================

    /// <summary>画像ピクセル座標を画面座標へ変換する。</summary>
    /// <param name="p">画像ピクセル座標。</param>
    private Point ImageToScreen(Vec2 p)
    {
        var document = _document!;
        return new Point(p.X * document.Zoom + document.OffsetX, p.Y * document.Zoom + document.OffsetY);
    }

    /// <summary>画面座標を画像ピクセル座標へ変換する。</summary>
    /// <param name="p">画面座標。</param>
    private Vec2 ScreenToImage(Point p)
    {
        var document = _document!;
        return new Vec2((p.X - document.OffsetX) / document.Zoom, (p.Y - document.OffsetY) / document.Zoom);
    }

    /// <summary>
    /// ズーム倍率に応じて画像の拡大補間方法を切り替える。
    ///
    /// 等倍以上ではピクセルをぼかさず（ドット単位で輪郭を合わせられるように）、
    /// 縮小時は滑らかに描く。<b>OnRender の中で呼んではならない</b>
    /// （依存関係プロパティの変更が再描画を誘発してループになるため）。
    /// </summary>
    private void UpdateBitmapScalingMode()
    {
        bool magnifying = _document != null && _document.Zoom >= 1.0;
        RenderOptions.SetBitmapScalingMode(this,
            magnifying ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    /// <summary>
    /// 画像がキャンバスへ収まるようズームとオフセットを設定する。
    /// </summary>
    public void ZoomToFit()
    {
        if (_document == null || ActualWidth <= 0.0 || ActualHeight <= 0.0) return;

        double scaleX = ActualWidth * (1.0 - FitMarginRatio * 2.0) / _document.Image.Width;
        double scaleY = ActualHeight * (1.0 - FitMarginRatio * 2.0) / _document.Image.Height;
        double zoom = Math.Clamp(Math.Min(scaleX, scaleY),
            SpriteRigDocument.MinZoom, SpriteRigDocument.MaxZoom);

        _document.Zoom = zoom;
        _document.OffsetX = (ActualWidth - _document.Image.Width * zoom) * 0.5;
        _document.OffsetY = (ActualHeight - _document.Image.Height * zoom) * 0.5;
        UpdateBitmapScalingMode();
        InvalidateVisual();
    }

    // ============================================================
    //  描画
    // ============================================================

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0.0, 0.0, ActualWidth, ActualHeight));
        if (_document == null || _bitmap == null) return;

        var document = _document;
        var topLeft = ImageToScreen(Vec2.Zero);
        var bottomRight = ImageToScreen(new Vec2(document.Image.Width, document.Image.Height));
        var imageRect = new Rect(topLeft, bottomRight);

        // ── 画像本体 ──
        dc.DrawImage(_bitmap, imageRect);
        dc.DrawRectangle(null, ImageBorderPen, imageRect);

        if (document.ShowPixelGrid && document.Zoom >= PixelGridMinZoom) DrawPixelGrid(dc, imageRect);

        DrawTriangles(dc, document);
        DrawContours(dc, document);
        DrawPendingPolygon(dc, document);
        DrawHandles(dc, document);
    }

    /// <summary>ピクセル境界のグリッドを描く（十分拡大しているときのみ）。</summary>
    private void DrawPixelGrid(DrawingContext dc, Rect imageRect)
    {
        var document = _document!;
        for (int x = 0; x <= document.Image.Width; x++)
        {
            double sx = document.OffsetX + x * document.Zoom;
            dc.DrawLine(PixelGridPen, new Point(sx, imageRect.Top), new Point(sx, imageRect.Bottom));
        }
        for (int y = 0; y <= document.Image.Height; y++)
        {
            double sy = document.OffsetY + y * document.Zoom;
            dc.DrawLine(PixelGridPen, new Point(imageRect.Left, sy), new Point(imageRect.Right, sy));
        }
    }

    /// <summary>三角形のワイヤフレームを描く。</summary>
    private void DrawTriangles(DrawingContext dc, SpriteRigDocument document)
    {
        var mesh = document.Mesh;
        if (mesh.Triangles.Count == 0) return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (int t = 0; t + Triangulation.IndicesPerTriangle <= mesh.Triangles.Count;
                 t += Triangulation.IndicesPerTriangle)
            {
                var a = ImageToScreen(mesh.Vertices[mesh.Triangles[t]]);
                var b = ImageToScreen(mesh.Vertices[mesh.Triangles[t + 1]]);
                var c = ImageToScreen(mesh.Vertices[mesh.Triangles[t + 2]]);
                ctx.BeginFigure(a, isFilled: false, isClosed: true);
                ctx.LineTo(b, isStroked: true, isSmoothJoin: false);
                ctx.LineTo(c, isStroked: true, isSmoothJoin: false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, TrianglePen, geometry);
    }

    /// <summary>輪郭ポリゴン（外周・穴）を描く。</summary>
    private void DrawContours(DrawingContext dc, SpriteRigDocument document)
    {
        foreach (var polygon in document.Mesh.Polygons)
        {
            if (polygon.Points.Count < SpriteRigMesh.MinPolygonVertices) continue;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(ImageToScreen(polygon.Points[0]), isFilled: false, isClosed: true);
                for (int i = 1; i < polygon.Points.Count; i++)
                    ctx.LineTo(ImageToScreen(polygon.Points[i]), isStroked: true, isSmoothJoin: false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, polygon.IsHole ? HoleContourPen : OuterContourPen, geometry);
        }
    }

    /// <summary>作図中のポリゴン（未確定の折れ線 + カーソルまでのプレビュー）を描く。</summary>
    private void DrawPendingPolygon(DrawingContext dc, SpriteRigDocument document)
    {
        if (document.PendingPolygon.Count == 0) return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(ImageToScreen(document.PendingPolygon[0]), isFilled: false, isClosed: false);
            for (int i = 1; i < document.PendingPolygon.Count; i++)
                ctx.LineTo(ImageToScreen(document.PendingPolygon[i]), isStroked: true, isSmoothJoin: false);
            // 最後の頂点からカーソルまでを点線ではなく実線で伸ばす（次に置かれる辺のプレビュー）
            ctx.LineTo(ImageToScreen(_cursorInImage), isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, PendingPolygonPen, geometry);

        foreach (var point in document.PendingPolygon)
            DrawHandle(dc, ImageToScreen(point), PendingPolygonPen.Brush, HandleSize);
    }

    /// <summary>編集可能な点（輪郭頂点・内部点・作図点）のハンドルを描く。</summary>
    private void DrawHandles(DrawingContext dc, SpriteRigDocument document)
    {
        for (int p = 0; p < document.Mesh.Polygons.Count; p++)
        {
            var points = document.Mesh.Polygons[p].Points;
            for (int i = 0; i < points.Count; i++)
            {
                bool selected = document.SelectedPoint == new SpriteRigPointRef(p, i);
                DrawHandle(dc, ImageToScreen(points[i]),
                    selected ? SelectedHandleBrush : ContourHandleBrush,
                    selected ? SelectedHandleSize : HandleSize);
            }
        }

        for (int i = 0; i < document.Mesh.InteriorPoints.Count; i++)
        {
            bool selected = document.SelectedPoint == SpriteRigPointRef.Interior(i);
            DrawHandle(dc, ImageToScreen(document.Mesh.InteriorPoints[i]),
                selected ? SelectedHandleBrush : InteriorHandleBrush,
                selected ? SelectedHandleSize : HandleSize);
        }
    }

    /// <summary>ハンドル 1 個（正方形）を描く。</summary>
    private static void DrawHandle(DrawingContext dc, Point center, Brush brush, double size)
    {
        double half = size * 0.5;
        dc.DrawRectangle(brush, HandleBorderPen,
            new Rect(center.X - half, center.Y - half, size, size));
    }

    /// <summary>色から Freeze 済みのブラシを作る。</summary>
    private static Brush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>色と太さから Freeze 済みのペンを作る。</summary>
    private static Pen CreatePen(Color color, double thickness)
    {
        var pen = new Pen(CreateBrush(color), thickness);
        pen.Freeze();
        return pen;
    }

    // ============================================================
    //  入力
    // ============================================================

    /// <inheritdoc/>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_document == null) return;

        // カーソル位置の画像座標を固定したままズームする
        Point screen = e.GetPosition(this);
        Vec2 anchor = ScreenToImage(screen);

        double factor = e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep;
        double zoom = Math.Clamp(_document.Zoom * factor,
            SpriteRigDocument.MinZoom, SpriteRigDocument.MaxZoom);

        _document.Zoom = zoom;
        _document.OffsetX = screen.X - anchor.X * zoom;
        _document.OffsetY = screen.Y - anchor.Y * zoom;

        UpdateBitmapScalingMode();
        InvalidateVisual();
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (_document == null) return;
        Focus();

        // 中ボタン: パン開始
        if (e.ChangedButton == MouseButton.Middle)
        {
            _panAnchor = e.GetPosition(this);
            CaptureMouse();
            e.Handled = true;
            return;
        }

        // 右ボタン: 作図のキャンセル
        if (e.ChangedButton == MouseButton.Right)
        {
            if (_document.PendingPolygon.Count > 0)
            {
                _document.CancelPendingPolygon();
                InvalidateVisual();
            }
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;
        HandleLeftClick(ScreenToImage(e.GetPosition(this)));
        e.Handled = true;
    }

    /// <summary>
    /// 左クリックを現在のツールに応じた編集操作へ振り分ける。
    /// </summary>
    /// <param name="position">クリック位置（画像ピクセル）。</param>
    private void HandleLeftClick(Vec2 position)
    {
        var document = _document!;
        // ボーン／ウェイトモードは Phase B1b で実装する（B1a では何もしない）
        if (document.EditMode != SpriteRigEditMode.Mesh) return;

        double hitRadius = HitRadiusInScreenPixels / document.Zoom;

        switch (document.Tool)
        {
            case SpriteRigMeshTool.Select:
                document.SelectedPoint = document.HitTestPoint(position, hitRadius);
                break;

            case SpriteRigMeshTool.DrawPolygon:
                HandleDrawPolygonClick(document, position, hitRadius);
                break;

            case SpriteRigMeshTool.AddVertex:
                document.AddVertexAt(position, hitRadius);
                RaiseModified();
                break;

            case SpriteRigMeshTool.MoveVertex:
                var target = document.HitTestPoint(position, hitRadius);
                if (target is { } handle && document.BeginPointDrag(handle))
                {
                    document.SelectedPoint = handle;
                    CaptureMouse();
                }
                break;

            case SpriteRigMeshTool.DeleteVertex:
                if (document.HitTestPoint(position, hitRadius) is { } victim &&
                    document.DeletePoint(victim))
                {
                    RaiseModified();
                }
                break;
        }
        InvalidateVisual();
    }

    /// <summary>
    /// ポリゴン描画ツールのクリック処理。始点付近をクリックしたら閉じて確定する。
    /// </summary>
    private void HandleDrawPolygonClick(SpriteRigDocument document, Vec2 position, double hitRadius)
    {
        if (document.PendingPolygon.Count >= SpriteRigMesh.MinPolygonVertices &&
            Vec2.Distance(document.PendingPolygon[0], position) <= hitRadius)
        {
            if (document.CommitPendingPolygon()) RaiseModified();
            return;
        }
        document.AddPendingPolygonPoint(position);
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_document == null) return;

        Point screen = e.GetPosition(this);
        _cursorInImage = ScreenToImage(screen);

        // 中ボタンパン
        if (_panAnchor is { } anchor && e.MiddleButton == MouseButtonState.Pressed)
        {
            _document.OffsetX += screen.X - anchor.X;
            _document.OffsetY += screen.Y - anchor.Y;
            _panAnchor = screen;
            InvalidateVisual();
            return;
        }

        // 頂点ドラッグ
        if (_document.IsDraggingPoint && e.LeftButton == MouseButtonState.Pressed)
        {
            _document.UpdatePointDrag(_cursorInImage);
            InvalidateVisual();
            return;
        }

        // 作図中はプレビュー線がカーソルへ追従する
        if (_document.PendingPolygon.Count > 0) InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_document == null) return;

        if (e.ChangedButton == MouseButton.Middle && _panAnchor != null)
        {
            _panAnchor = null;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left && _document.IsDraggingPoint)
        {
            _document.EndPointDrag();
            ReleaseMouseCapture();
            RaiseModified();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_document == null) return;

        switch (e.Key)
        {
            case Key.Enter:
                // 作図中ポリゴンを確定する
                if (_document.PendingPolygon.Count > 0 && _document.CommitPendingPolygon())
                {
                    RaiseModified();
                    InvalidateVisual();
                    e.Handled = true;
                }
                break;

            case Key.Escape:
                if (_document.PendingPolygon.Count > 0)
                {
                    _document.CancelPendingPolygon();
                    InvalidateVisual();
                    e.Handled = true;
                }
                break;

            case Key.Delete:
                if (_document.SelectedPoint is { } selected && _document.DeletePoint(selected))
                {
                    RaiseModified();
                    InvalidateVisual();
                    e.Handled = true;
                }
                break;
        }
    }

    /// <summary>ドキュメントが変更されたことをパネルへ知らせる。</summary>
    private void RaiseModified() => DocumentModified?.Invoke();

    /// <summary>外部（パネルのツールバー操作など）から再描画を促す。</summary>
    public void Refresh() => InvalidateVisual();
}
