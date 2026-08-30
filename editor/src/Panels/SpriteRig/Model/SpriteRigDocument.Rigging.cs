using System;
using System.Collections.Generic;
using SEEDEditor.Panels.SpriteRig.Mesh;

namespace SEEDEditor.Panels.SpriteRig.Model;

/// <summary>
/// <see cref="SpriteRigDocument"/> のうち Phase B1b（ボーン編集・ウェイト編集）が担う部分。
///
/// <para>
/// メッシュ編集側（B1a）と同じ 3 段階の作法を守る:
/// </para>
/// <list type="number">
///   <item><see cref="SpriteRigDocument.History"/> へ操作前スナップショットを積む</item>
///   <item>ボーン／ウェイトを書き換える</item>
///   <item>未保存フラグを立てる</item>
/// </list>
///
/// <para>
/// ボーンもウェイトも <see cref="SpriteRigMesh"/> の一部なので、
/// 既存のスナップショット方式の Undo/Redo にそのまま乗る（履歴機構の追加は不要）。
/// </para>
///
/// <para>
/// ドラッグやブラシストロークのように連続する操作は、
/// <b>開始時に 1 回だけ</b>スナップショットを積む（1 ストローク = Undo 1 回）。
/// </para>
/// </summary>
public sealed partial class SpriteRigDocument
{
    /// <summary>ボーンハンドルのヒット判定半径の既定値（画像ピクセル）。</summary>
    public const double DefaultBoneHitRadius = 7.0;

    // ============================================================
    //  状態
    // ============================================================

    /// <summary>ボーン編集モードでの現在のツール。</summary>
    public SpriteRigBoneTool BoneTool { get; set; } = SpriteRigBoneTool.Create;

    /// <summary>現在選択中のボーン添字（範囲外なら -1 を返す）。</summary>
    public int SelectedBoneIndex
    {
        get => _selectedBoneIndex >= 0 && _selectedBoneIndex < Mesh.Bones.Count ? _selectedBoneIndex : -1;
        set => _selectedBoneIndex = value;
    }

    /// <summary>ウェイトモードで選択中の頂点添字（詳細行の数値編集対象）。</summary>
    public HashSet<int> SelectedVertices { get; } = new();

    /// <summary>自動ウェイトのパラメータ（タブごとに独立して覚える）。</summary>
    public AutoWeights.Options AutoWeightOptions { get; } = new();

    /// <summary>true なら自動ウェイトを選択頂点だけに適用する。</summary>
    public bool AutoWeightSelectedOnly { get; set; }

    /// <summary>ウェイトペイントのブラシパラメータ。</summary>
    public WeightPaint.BrushOptions Brush { get; } = new();

    /// <summary>true なら全ボーンを色分けして表示する（false = 選択ボーンのヒートマップ）。</summary>
    public bool ShowAllBoneColors { get; set; }

    /// <summary>選択中ボーンの添字（生値。範囲チェック前）。</summary>
    private int _selectedBoneIndex = -1;

    /// <summary>ボーン作成中の根元位置（作成していなければ null）。</summary>
    private Vec2? _createHead;

    /// <summary>ボーン作成中の先端位置（カーソル追従）。</summary>
    private Vec2 _createTip;

    /// <summary>
    /// 連鎖作成の親ボーン添字。次に作るボーンはこのボーンの子になり、根元はその先端に吸着する。
    /// -1 = 連鎖していない（クリックした位置がそのまま根元になる）。
    /// </summary>
    private int _chainParentIndex = -1;

    /// <summary>ドラッグ中のボーンハンドル（未ドラッグなら null）。</summary>
    private SpriteRigBoneHandle? _dragBoneHandle;

    /// <summary>ボーン作成のドラッグ中か。</summary>
    public bool IsCreatingBone => _createHead != null;

    /// <summary>ボーンハンドルをドラッグ中か。</summary>
    public bool IsDraggingBone => _dragBoneHandle != null;

    /// <summary>ボーン作成が連鎖中（次のボーンの親が決まっている）か。</summary>
    public bool IsBoneChainActive => _chainParentIndex >= 0;

    /// <summary>作成中ボーンの根元（作成していなければ null）。キャンバスのプレビュー描画用。</summary>
    public Vec2? PendingBoneHead => _createHead;

    /// <summary>作成中ボーンの先端。キャンバスのプレビュー描画用。</summary>
    public Vec2 PendingBoneTip => _createTip;

    // ============================================================
    //  ボーンの幾何（キャンバス座標）
    // ============================================================

    /// <summary>全ボーンのバインドポーズ グローバル変換を計算する。</summary>
    public Affine2[] ComputeBoneGlobals() => SpriteRigSkeleton.ComputeGlobals(Mesh.Bones);

    /// <summary>ボーンの根元位置（キャンバス座標）。</summary>
    /// <param name="globals"><see cref="ComputeBoneGlobals"/> の結果。</param>
    /// <param name="index">ボーンの添字。</param>
    public Vec2 BoneHead(Affine2[] globals, int index) => SpriteRigSkeleton.HeadOf(globals, index);

    /// <summary>ボーンの先端位置（キャンバス座標。長さ未記録なら既定長のスタブ）。</summary>
    /// <param name="globals"><see cref="ComputeBoneGlobals"/> の結果。</param>
    /// <param name="index">ボーンの添字。</param>
    public Vec2 BoneTip(Affine2[] globals, int index) => SpriteRigSkeleton.TipOf(Mesh.Bones, globals, index);

    // ============================================================
    //  ボーンの作成（クリック → ドラッグ → リリース）
    // ============================================================

    /// <summary>
    /// ボーン作成を開始する。連鎖中なら根元は直前ボーンの先端へ吸着する。
    /// </summary>
    /// <param name="position">押した位置（画像ピクセル）。</param>
    public void BeginBoneCreate(Vec2 position)
    {
        Vec2 head = ClampToImage(position);
        if (_chainParentIndex >= 0 && _chainParentIndex < Mesh.Bones.Count)
        {
            var globals = ComputeBoneGlobals();
            head = BoneTip(globals, _chainParentIndex);
        }
        _createHead = head;
        _createTip = head;
    }

    /// <summary>作成中ボーンの先端をカーソルへ追従させる。</summary>
    /// <param name="position">カーソル位置（画像ピクセル）。</param>
    public void UpdateBoneCreate(Vec2 position)
    {
        if (_createHead == null) return;
        _createTip = ClampToImage(position);
    }

    /// <summary>
    /// 作成中のボーンを確定する。長さが 0 に近ければ作らずに破棄する。
    /// </summary>
    /// <returns>作成できた場合 true。</returns>
    public bool CommitBoneCreate()
    {
        if (_createHead is not { } head)
        {
            return false;
        }

        Vec2 tip = _createTip;
        _createHead = null;

        if (Vec2.Distance(head, tip) < SpriteRigSkeleton.MinBoneLength) return false;
        if (Mesh.Bones.Count >= SpriteRigSkeleton.MaxBones) return false;

        History.Push("ボーンを追加", Mesh);

        // 連鎖中でなければ、根元を含むボーンがあればそれを親にする（無ければルート＝添字 0）
        int parent = _chainParentIndex;
        if (parent < 0) parent = FindParentCandidate(head);

        int created = SpriteRigSkeleton.AddBone(Mesh.Bones, parent, head, tip);

        // 作った骨の先端が次の根元候補になる（Esc で連鎖終了）
        _chainParentIndex = created;
        SelectedBoneIndex = created;
        MarkDirty();
        return true;
    }

    /// <summary>作成中のボーンと連鎖状態を破棄する（Esc）。</summary>
    public void CancelBoneCreate()
    {
        _createHead = null;
        _chainParentIndex = -1;
    }

    /// <summary>
    /// 新規ボーンの親候補を選ぶ。指定位置に最も近い<b>先端</b>を持つボーンがあればその子にし、
    /// 遠ければ最初のボーン（ルート）の子にする。
    /// </summary>
    /// <param name="head">新しいボーンの根元位置。</param>
    private int FindParentCandidate(Vec2 head)
    {
        if (Mesh.Bones.Count == 0) return -1;

        var globals = ComputeBoneGlobals();
        int best = 0;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < Mesh.Bones.Count; i++)
        {
            double distance = Vec2.Distance(BoneTip(globals, i), head);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }
        return bestDistance <= DefaultBoneHitRadius ? best : 0;
    }

    // ============================================================
    //  ボーンの選択・移動
    // ============================================================

    /// <summary>
    /// 指定位置に最も近いボーンハンドル（根元 / 先端）を探す。
    /// </summary>
    /// <param name="position">探索位置（画像ピクセル）。</param>
    /// <param name="radius">ヒット半径（画像ピクセル）。</param>
    /// <returns>見つかったハンドル。無ければ null。</returns>
    public SpriteRigBoneHandle? HitTestBoneHandle(Vec2 position, double radius = DefaultBoneHitRadius)
    {
        var globals = ComputeBoneGlobals();
        SpriteRigBoneHandle? best = null;
        double bestDistance = radius;

        for (int i = 0; i < Mesh.Bones.Count; i++)
        {
            double headDistance = Vec2.Distance(BoneHead(globals, i), position);
            if (headDistance <= bestDistance)
            {
                bestDistance = headDistance;
                best = SpriteRigBoneHandle.Head(i);
            }
            double tipDistance = Vec2.Distance(BoneTip(globals, i), position);
            if (tipDistance <= bestDistance)
            {
                bestDistance = tipDistance;
                best = SpriteRigBoneHandle.Tip(i);
            }
        }
        return best;
    }

    /// <summary>
    /// 指定位置に最も近いボーン（骨の線分そのもの）を探す。
    /// </summary>
    /// <param name="position">探索位置（画像ピクセル）。</param>
    /// <param name="maxDistance">骨に触れたとみなす最大距離（画像ピクセル）。</param>
    /// <returns>見つかったボーンの添字。無ければ -1。</returns>
    public int HitTestBone(Vec2 position, double maxDistance = DefaultBoneHitRadius)
    {
        var globals = ComputeBoneGlobals();
        int best = -1;
        double bestDistance = maxDistance;

        for (int i = 0; i < Mesh.Bones.Count; i++)
        {
            double distance = Geometry2D.DistancePointSegment(
                BoneHead(globals, i), BoneTip(globals, i), position);
            if (distance > bestDistance) continue;
            bestDistance = distance;
            best = i;
        }
        return best;
    }

    /// <summary>
    /// ボーンハンドルのドラッグを開始する（Undo スナップショットはここで 1 回だけ積む）。
    /// </summary>
    /// <param name="handle">掴んだハンドル。</param>
    /// <returns>開始できた場合 true。</returns>
    public bool BeginBoneDrag(SpriteRigBoneHandle handle)
    {
        if (handle.BoneIndex < 0 || handle.BoneIndex >= Mesh.Bones.Count) return false;

        History.Push(handle.IsTip ? "ボーンの先端を移動" : "ボーンの根元を移動", Mesh);
        _dragBoneHandle = handle;
        SelectedBoneIndex = handle.BoneIndex;
        return true;
    }

    /// <summary>ドラッグ中のボーンハンドルを移動する。</summary>
    /// <param name="position">新しい位置（画像ピクセル）。</param>
    public void UpdateBoneDrag(Vec2 position)
    {
        if (_dragBoneHandle is not { } handle) return;
        if (handle.BoneIndex >= Mesh.Bones.Count) return;

        Vec2 clamped = ClampToImage(position);
        if (handle.IsTip) SpriteRigSkeleton.MoveTip(Mesh.Bones, handle.BoneIndex, clamped);
        else SpriteRigSkeleton.MoveHead(Mesh.Bones, handle.BoneIndex, clamped);
    }

    /// <summary>ボーンハンドルのドラッグを終了する。</summary>
    public void EndBoneDrag()
    {
        if (_dragBoneHandle == null) return;
        _dragBoneHandle = null;
        MarkDirty();
    }

    // ============================================================
    //  ボーンの削除・名前変更・親の付け替え
    // ============================================================

    /// <summary>
    /// ボーンを削除する。子は削除されたボーンの親へ付け替わり（ワールド姿勢は保つ）、
    /// そのボーンを参照していたウェイトは除かれて再正規化される。
    /// </summary>
    /// <param name="index">削除するボーンの添字。</param>
    /// <returns>削除できた場合 true（最後の 1 本は消せない）。</returns>
    public bool DeleteBone(int index)
    {
        if (index < 0 || index >= Mesh.Bones.Count) return false;
        if (Mesh.Bones.Count <= 1) return false;

        History.Push("ボーンを削除", Mesh);
        SpriteRigSkeleton.DeleteBone(Mesh.Bones, index);
        SpriteRigSkeleton.RemapWeightsAfterBoneRemoval(Mesh.Weights, index);
        if (_selectedBoneIndex >= Mesh.Bones.Count) _selectedBoneIndex = Mesh.Bones.Count - 1;
        _chainParentIndex = -1;
        MarkDirty();
        return true;
    }

    /// <summary>
    /// ボーンの名前を変える（子の親参照も追随する）。
    /// </summary>
    /// <param name="index">対象ボーンの添字。</param>
    /// <param name="newName">新しい名前。</param>
    /// <returns>変更できた場合 true（空・重複なら false）。</returns>
    public bool RenameBone(int index, string newName)
    {
        if (index < 0 || index >= Mesh.Bones.Count) return false;
        if (string.Equals(Mesh.Bones[index].Name, newName, StringComparison.Ordinal)) return true;
        // 履歴を汚さないよう、成立しない改名は積む前に弾く
        if (!SpriteRigSkeleton.CanRename(Mesh.Bones, index, newName)) return false;

        History.Push("ボーン名を変更", Mesh);
        SpriteRigSkeleton.RenameBone(Mesh.Bones, index, newName);
        MarkDirty();
        return true;
    }

    /// <summary>
    /// ボーンの親を付け替える（ワールド上の姿勢は変わらない）。
    /// </summary>
    /// <param name="index">対象ボーンの添字。</param>
    /// <param name="newParentIndex">新しい親の添字（-1 でルート化）。</param>
    /// <returns>付け替えられた場合 true（循環になる場合は false）。</returns>
    public bool ReparentBone(int index, int newParentIndex)
    {
        if (index < 0 || index >= Mesh.Bones.Count) return false;
        // 循環になる付け替えは履歴を積む前に弾く
        if (newParentIndex >= Mesh.Bones.Count) return false;
        if (newParentIndex >= 0 && SpriteRigSkeleton.IsDescendantOf(Mesh.Bones, newParentIndex, index)) return false;

        History.Push("ボーンの親を変更", Mesh);
        SpriteRigSkeleton.Reparent(Mesh.Bones, index, newParentIndex);
        MarkDirty();
        return true;
    }

    // ============================================================
    //  ウェイト
    // ============================================================

    /// <summary>
    /// 自動ウェイトを割り当てる（全頂点、または <see cref="AutoWeightSelectedOnly"/> なら選択頂点だけ）。
    /// </summary>
    /// <returns>割り当てた頂点数。</returns>
    public int ApplyAutoWeights()
    {
        if (Mesh.Vertices.Count == 0) return 0;

        var targets = AutoWeightSelectedOnly && SelectedVertices.Count > 0 ? SelectedVertices : null;
        if (Mesh.Bones.Count == 0) return 0;

        History.Push("自動ウェイト", Mesh);
        int applied = AutoWeights.Apply(Mesh, AutoWeightOptions, targets);
        MarkDirty();
        return applied;
    }

    /// <summary>全頂点のウェイトをルート 1 本へ初期化する。</summary>
    public void ResetWeights()
    {
        History.Push("ウェイトを初期化", Mesh);
        Mesh.ResetWeightsToRoot();
        MarkDirty();
    }

    /// <summary>
    /// ウェイトペイントの 1 ストロークを開始する（Undo スナップショットをここで 1 回だけ積む）。
    /// </summary>
    /// <returns>開始できた場合 true。</returns>
    public bool BeginWeightStroke()
    {
        if (SelectedBoneIndex < 0 || Mesh.Vertices.Count == 0) return false;
        History.Push("ウェイトをペイント", Mesh);
        _weightStrokeActive = true;
        return true;
    }

    /// <summary>
    /// ストローク中の 1 点ぶんブラシを当てる。
    /// </summary>
    /// <param name="position">ブラシ中心（画像ピクセル）。</param>
    /// <returns>1 頂点でも変わった場合 true。</returns>
    public bool PaintWeightAt(Vec2 position)
    {
        if (!_weightStrokeActive || SelectedBoneIndex < 0) return false;
        return WeightPaint.ApplyBrush(
            Mesh.Vertices, Mesh.Weights, Mesh.Triangles, SelectedBoneIndex, position, Brush);
    }

    /// <summary>ウェイトペイントのストロークを終了する。</summary>
    public void EndWeightStroke()
    {
        if (!_weightStrokeActive) return;
        _weightStrokeActive = false;
        MarkDirty();
    }

    /// <summary>ウェイトペイントのストローク中か。</summary>
    public bool IsPaintingWeight => _weightStrokeActive;

    /// <summary>ストローク中かどうかの生フラグ。</summary>
    private bool _weightStrokeActive;

    /// <summary>
    /// 1 頂点の 1 ボーンぶんのウェイトを数値で設定する（詳細行の編集）。
    /// 残りの影響は合計 1.0 になるよう按分され、最大 4 本の制約も保たれる。
    /// </summary>
    /// <param name="vertexIndex">対象頂点の添字。</param>
    /// <param name="boneIndex">対象ボーンの添字。</param>
    /// <param name="weight">設定するウェイト（0〜1）。</param>
    /// <returns>設定できた場合 true。</returns>
    public bool SetInfluenceWeight(int vertexIndex, int boneIndex, double weight)
    {
        if (vertexIndex < 0 || vertexIndex >= Mesh.Weights.Count) return false;
        if (boneIndex < 0 || boneIndex >= Mesh.Bones.Count) return false;

        History.Push("ウェイトを編集", Mesh);
        Mesh.Weights[vertexIndex] = WeightPaint.SetBoneWeight(Mesh.Weights[vertexIndex], boneIndex, weight);
        MarkDirty();
        return true;
    }

    /// <summary>
    /// 指定頂点における指定ボーンのウェイトを返す（範囲外なら 0）。
    /// </summary>
    /// <param name="vertexIndex">頂点の添字。</param>
    /// <param name="boneIndex">ボーンの添字。</param>
    public double GetInfluenceWeight(int vertexIndex, int boneIndex)
    {
        if (vertexIndex < 0 || vertexIndex >= Mesh.Weights.Count) return 0.0;
        return WeightPaint.GetWeight(Mesh.Weights[vertexIndex], boneIndex);
    }

    /// <summary>
    /// 指定位置に最も近い派生頂点の添字を返す（ウェイトモードの頂点選択用）。
    /// </summary>
    /// <param name="position">探索位置（画像ピクセル）。</param>
    /// <param name="radius">ヒット半径（画像ピクセル）。</param>
    /// <returns>見つかった頂点の添字。無ければ -1。</returns>
    public int HitTestVertex(Vec2 position, double radius = DefaultHitRadius)
    {
        int best = -1;
        double bestDistance = radius;
        for (int i = 0; i < Mesh.Vertices.Count; i++)
        {
            double distance = Vec2.Distance(Mesh.Vertices[i], position);
            if (distance > bestDistance) continue;
            bestDistance = distance;
            best = i;
        }
        return best;
    }

    /// <summary>範囲外になった選択状態を捨てる（Undo やメッシュ再構築の後に呼ぶ）。</summary>
    public void ClampSelections()
    {
        if (_selectedBoneIndex >= Mesh.Bones.Count) _selectedBoneIndex = Mesh.Bones.Count - 1;
        SelectedVertices.RemoveWhere(i => i < 0 || i >= Mesh.Vertices.Count);
        _chainParentIndex = -1;
        _createHead = null;
        _dragBoneHandle = null;
        _weightStrokeActive = false;
    }
}
