using System;
using System.Collections.Generic;

namespace SEEDEditor.Panels.SpriteRig.Mesh;

/// <summary>
/// ボーン階層に対する計算と編集操作をまとめた静的ユーティリティ。
///
/// <para>【編集表現 ⇔ 保存表現の変換規約（B1b の要）】</para>
/// <para>
/// <c>.sprite_mesh</c> の <c>bones</c> は<b>親ローカルの TRS</b>（position / rotation / scale）
/// で保存されるが、ユーザーが触るのは「根元（head）と先端（tip）の 2 点」である。
/// この 2 表現は次の規則で 1:1 に対応する:
/// </para>
/// <code>
///   global(b)  = global(parent(b)) * Affine2.FromTrs(position, rotation, scale)
///   head(b)    = global(b) を原点 (0, 0) へ適用した点
///   tip(b)     = global(b) を (Length, 0) へ適用した点
/// </code>
/// <para>逆向き（head / tip → TRS）は <see cref="SetHeadAndTip"/> が行う:</para>
/// <code>
///   localHead  = global(parent)⁻¹ * head          → position
///   localTip   = global(parent)⁻¹ * tip
///   rotation   = atan2(localTip - localHead) の角度（度）
///   Length     = |localTip - localHead| / scale.X
/// </code>
/// <para>
/// つまり<b>ボーンのローカル +X 方向が骨の向き</b>で、長さはスケール適用前の値になる。
/// スケールを 1 以外にしなければ Length はそのまま画面上のピクセル長である。
/// </para>
///
/// <para>
/// 親の添字は「名前による参照」（<see cref="SpriteRigBone.Parent"/>）で保持されているので、
/// 配列の並び順に依存しない（親が子より後ろにいても正しく解決される）。
/// </para>
/// </summary>
public static class SpriteRigSkeleton
{
    /// <summary>長さが記録されていないボーン（旧ファイル・退化）を描くときの既定長（ピクセル）。</summary>
    public const double DefaultBoneLength = 24.0;

    /// <summary>これ未満の長さは「長さ無し」とみなす（ゼロ除算とゼロ長ベクトルの回避）。</summary>
    public const double MinBoneLength = 1.0e-6;

    /// <summary>ランタイムが受理するボーン数の上限（<c>MAX_SPRITE_BONES</c> と一致）。</summary>
    public const int MaxBones = 128;

    /// <summary>新規ボーン名の既定プレフィクス（<c>bone_1</c>, <c>bone_2</c>, …）。</summary>
    public const string NewBoneNamePrefix = "bone";

    // ============================================================
    //  階層の解決
    // ============================================================

    /// <summary>
    /// ボーン名 → 添字の索引を作る（同名が複数あれば最初のものが勝つ）。
    /// </summary>
    /// <param name="bones">対象のボーン一覧。</param>
    public static Dictionary<string, int> BuildNameIndex(IReadOnlyList<SpriteRigBone> bones)
    {
        var index = new Dictionary<string, int>(bones.Count, StringComparer.Ordinal);
        for (int i = 0; i < bones.Count; i++)
        {
            if (string.IsNullOrEmpty(bones[i].Name)) continue;
            index.TryAdd(bones[i].Name, i);
        }
        return index;
    }

    /// <summary>
    /// 各ボーンの親の添字を返す（ルートまたは解決不能なら -1）。
    /// 自分自身を親にしている場合もルート扱いにして、循環で無限ループしないようにする。
    /// </summary>
    /// <param name="bones">対象のボーン一覧。</param>
    public static int[] BuildParentIndices(IReadOnlyList<SpriteRigBone> bones)
    {
        var nameIndex = BuildNameIndex(bones);
        var parents = new int[bones.Count];
        for (int i = 0; i < bones.Count; i++)
        {
            string parentName = bones[i].Parent;
            if (string.IsNullOrEmpty(parentName) || !nameIndex.TryGetValue(parentName, out int p) || p == i)
            {
                parents[i] = -1;
                continue;
            }
            parents[i] = p;
        }
        return parents;
    }

    /// <summary>
    /// 全ボーンのバインドポーズ<b>グローバル</b>変換（ルート → 自分の合成）を返す。
    ///
    /// 親子の並び順に依存しないよう、メモ化した再帰で解決する。
    /// 循環を検出したボーンは恒等変換（＝ルート直下扱い）にして、UI が固まらないようにする。
    /// </summary>
    /// <param name="bones">対象のボーン一覧。</param>
    public static Affine2[] ComputeGlobals(IReadOnlyList<SpriteRigBone> bones)
    {
        int count = bones.Count;
        var parents = BuildParentIndices(bones);
        var globals = new Affine2[count];
        // 0 = 未計算 / 1 = 計算中（循環検出用）/ 2 = 計算済み
        var state = new byte[count];

        for (int i = 0; i < count; i++) Resolve(i);
        return globals;

        // ローカル関数: 添字 i のグローバル変換を求める（親を先に解決する）
        void Resolve(int i)
        {
            if (state[i] == 2) return;
            var local = Affine2.FromTrs(bones[i].Position, bones[i].Rotation, bones[i].Scale);

            if (state[i] == 1)
            {
                // 循環している。これ以上辿らず、その場でローカル変換を確定させる
                globals[i] = local;
                state[i] = 2;
                return;
            }

            state[i] = 1;
            int parent = parents[i];
            if (parent < 0)
            {
                globals[i] = local;
            }
            else
            {
                Resolve(parent);
                globals[i] = Affine2.Multiply(globals[parent], local);
            }
            state[i] = 2;
        }
    }

    /// <summary>
    /// ボーンの根元（head）の位置をキャンバス座標で返す。
    /// </summary>
    /// <param name="globals">
    /// <see cref="ComputeGlobals"/> の結果。
    /// </param>
    /// <param name="index">対象ボーンの添字。</param>
    public static Vec2 HeadOf(Affine2[] globals, int index) => globals[index].Transform(Vec2.Zero);

    /// <summary>
    /// ボーンの先端（tip）の位置をキャンバス座標で返す。
    /// 長さが記録されていないボーンは <see cref="DefaultBoneLength"/> のスタブ長で描く。
    /// </summary>
    /// <param name="bones">ボーン一覧。</param>
    /// <param name="globals"><see cref="ComputeGlobals"/> の結果。</param>
    /// <param name="index">対象ボーンの添字。</param>
    public static Vec2 TipOf(IReadOnlyList<SpriteRigBone> bones, Affine2[] globals, int index)
    {
        double length = bones[index].Length;
        if (length < MinBoneLength) length = DefaultBoneLength;
        return globals[index].Transform(new Vec2(length, 0.0));
    }

    /// <summary>
    /// 指定ボーンが <paramref name="ancestor"/> の子孫（自身を含む）かどうか。
    /// 親の付け替えで循環を作らないための検査に使う。
    /// </summary>
    /// <param name="bones">ボーン一覧。</param>
    /// <param name="index">検査するボーンの添字。</param>
    /// <param name="ancestor">祖先候補の添字。</param>
    public static bool IsDescendantOf(IReadOnlyList<SpriteRigBone> bones, int index, int ancestor)
    {
        if (index == ancestor) return true;
        var parents = BuildParentIndices(bones);
        int current = parents[index];
        // 循環していても必ず止まるよう、辿る回数をボーン数で打ち切る
        for (int steps = 0; current >= 0 && steps <= bones.Count; steps++)
        {
            if (current == ancestor) return true;
            current = parents[current];
        }
        return false;
    }

    /// <summary>親の添字から直接の子の添字一覧を返す。</summary>
    /// <param name="bones">ボーン一覧。</param>
    /// <param name="parentIndex">親の添字（-1 ならルートたちを返す）。</param>
    public static List<int> ChildrenOf(IReadOnlyList<SpriteRigBone> bones, int parentIndex)
    {
        var parents = BuildParentIndices(bones);
        var children = new List<int>();
        for (int i = 0; i < bones.Count; i++)
        {
            if (parents[i] == parentIndex) children.Add(i);
        }
        return children;
    }

    /// <summary>
    /// 階層を深さ優先で辿った表示順（添字と深さの組）を返す。ボーン一覧 UI のツリー表示に使う。
    /// 循環などで到達できないボーンも取りこぼさないよう、最後に深さ 0 で並べる。
    /// </summary>
    /// <param name="bones">ボーン一覧。</param>
    public static List<(int Index, int Depth)> BuildDisplayOrder(IReadOnlyList<SpriteRigBone> bones)
    {
        var parents = BuildParentIndices(bones);
        var order = new List<(int, int)>(bones.Count);
        var visited = new bool[bones.Count];

        for (int i = 0; i < bones.Count; i++)
        {
            if (parents[i] < 0) Visit(i, 0);
        }
        // どのルートからも辿れなかったボーン（循環の輪の中など）を救済する
        for (int i = 0; i < bones.Count; i++)
        {
            if (!visited[i]) order.Add((i, 0));
        }
        return order;

        void Visit(int index, int depth)
        {
            if (visited[index]) return;
            visited[index] = true;
            order.Add((index, depth));
            for (int c = 0; c < bones.Count; c++)
            {
                if (parents[c] == index) Visit(c, depth + 1);
            }
        }
    }

    // ============================================================
    //  編集操作
    // ============================================================

    /// <summary>
    /// ボーンの根元と先端をキャンバス座標で指定し、親ローカルの TRS + 長さへ変換して書き込む。
    /// スケールは変更しない（既存値を保つ）。
    /// </summary>
    /// <param name="bones">ボーン一覧。</param>
    /// <param name="index">対象ボーンの添字。</param>
    /// <param name="head">新しい根元位置（キャンバス座標）。</param>
    /// <param name="tip">新しい先端位置（キャンバス座標）。</param>
    public static void SetHeadAndTip(IReadOnlyList<SpriteRigBone> bones, int index, Vec2 head, Vec2 tip)
    {
        var bone = bones[index];
        Affine2 parentGlobal = ParentGlobalOf(bones, index);
        Affine2 toLocal = parentGlobal.Inverse();

        Vec2 localHead = toLocal.Transform(head);
        Vec2 localTip = toLocal.Transform(tip);
        Vec2 direction = localTip - localHead;

        bone.Position = localHead;

        double length = direction.Length;
        if (length >= MinBoneLength)
        {
            bone.Rotation = Math.Atan2(direction.Y, direction.X) * 180.0 / Math.PI;
            // 長さはスケール適用前の値。スケールが 0 に潰れている場合は 1 とみなす
            double scaleX = Math.Abs(bone.Scale.X) < MinBoneLength ? 1.0 : bone.Scale.X;
            bone.Length = length / scaleX;
        }
        else
        {
            // 根元と先端が一致 → 向きは決められないので回転は保ち、長さだけ 0 にする
            bone.Length = 0.0;
        }
    }

    /// <summary>親のグローバル変換を返す（ルートなら恒等変換）。</summary>
    /// <param name="bones">ボーン一覧。</param>
    /// <param name="index">対象ボーンの添字。</param>
    public static Affine2 ParentGlobalOf(IReadOnlyList<SpriteRigBone> bones, int index)
    {
        var parents = BuildParentIndices(bones);
        int parent = parents[index];
        if (parent < 0) return Affine2.Identity;
        return ComputeGlobals(bones)[parent];
    }

    /// <summary>
    /// 新しいボーンを根元・先端指定で追加する。
    /// </summary>
    /// <param name="bones">追加先のボーン一覧。</param>
    /// <param name="parentIndex">親ボーンの添字（-1 ならルート）。</param>
    /// <param name="head">根元位置（キャンバス座標）。</param>
    /// <param name="tip">先端位置（キャンバス座標）。</param>
    /// <param name="name">ボーン名（空なら自動採番）。</param>
    /// <returns>追加されたボーンの添字。上限に達していれば -1。</returns>
    public static int AddBone(
        List<SpriteRigBone> bones, int parentIndex, Vec2 head, Vec2 tip, string name = "")
    {
        if (bones.Count >= MaxBones) return -1;

        var bone = new SpriteRigBone
        {
            Name = string.IsNullOrEmpty(name) ? MakeUniqueName(bones, NewBoneNamePrefix) : name,
            Parent = parentIndex >= 0 && parentIndex < bones.Count ? bones[parentIndex].Name : string.Empty,
        };
        bones.Add(bone);
        SetHeadAndTip(bones, bones.Count - 1, head, tip);
        return bones.Count - 1;
    }

    /// <summary>
    /// 既存名と衝突しないボーン名を作る（<c>bone</c> → <c>bone_1</c> → <c>bone_2</c> …）。
    /// </summary>
    /// <param name="bones">ボーン一覧。</param>
    /// <param name="baseName">元にする名前。</param>
    public static string MakeUniqueName(IReadOnlyList<SpriteRigBone> bones, string baseName)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bone in bones) used.Add(bone.Name);

        if (!string.IsNullOrEmpty(baseName) && !used.Contains(baseName)) return baseName;

        string prefix = string.IsNullOrEmpty(baseName) ? NewBoneNamePrefix : baseName;
        for (int suffix = 1; ; suffix++)
        {
            string candidate = $"{prefix}_{suffix}";
            if (!used.Contains(candidate)) return candidate;
        }
    }

    /// <summary>
    /// その名前へ改名できるか（空でなく、他のボーンと重複しないか）を検査する。
    /// 呼び出し側が「履歴を積む前に弾く」ために使う。
    /// </summary>
    /// <param name="bones">ボーン一覧。</param>
    /// <param name="index">対象ボーンの添字。</param>
    /// <param name="newName">新しい名前。</param>
    public static bool CanRename(IReadOnlyList<SpriteRigBone> bones, int index, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return false;
        string trimmed = newName.Trim();
        for (int i = 0; i < bones.Count; i++)
        {
            if (i != index && string.Equals(bones[i].Name, trimmed, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>
    /// ボーンの名前を変更し、そのボーンを親に指している子の参照も追随させる。
    /// </summary>
    /// <param name="bones">ボーン一覧。</param>
    /// <param name="index">対象ボーンの添字。</param>
    /// <param name="newName">新しい名前（空・重複なら失敗する）。</param>
    /// <returns>変更できた場合 true。</returns>
    public static bool RenameBone(IReadOnlyList<SpriteRigBone> bones, int index, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return false;
        newName = newName.Trim();

        string oldName = bones[index].Name;
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return true;

        for (int i = 0; i < bones.Count; i++)
        {
            if (i != index && string.Equals(bones[i].Name, newName, StringComparison.Ordinal)) return false;
        }

        bones[index].Name = newName;
        foreach (var bone in bones)
        {
            if (string.Equals(bone.Parent, oldName, StringComparison.Ordinal)) bone.Parent = newName;
        }
        return true;
    }

    /// <summary>
    /// 親を付け替える。<b>ワールド上の姿勢（根元・先端）は変えない</b>ので、
    /// 見た目が動かないまま階層だけが変わる。
    /// </summary>
    /// <param name="bones">ボーン一覧。</param>
    /// <param name="index">対象ボーンの添字。</param>
    /// <param name="newParentIndex">新しい親の添字（-1 でルート化）。</param>
    /// <returns>付け替えられた場合 true（循環になる場合は false）。</returns>
    public static bool Reparent(IReadOnlyList<SpriteRigBone> bones, int index, int newParentIndex)
    {
        if (newParentIndex >= bones.Count) return false;
        // 自分自身や自分の子孫を親にすると循環する
        if (newParentIndex >= 0 && IsDescendantOf(bones, newParentIndex, index)) return false;

        var globals = ComputeGlobals(bones);
        Vec2 head = HeadOf(globals, index);
        Vec2 tip = TipOf(bones, globals, index);

        bones[index].Parent = newParentIndex < 0 ? string.Empty : bones[newParentIndex].Name;
        SetHeadAndTip(bones, index, head, tip);
        return true;
    }

    /// <summary>
    /// ボーンの根元を動かす（先端との相対関係＝向きと長さは保つ）。
    /// ボーン全体が平行移動するので、<b>子孫もそのまま追従する</b>。
    /// </summary>
    /// <param name="bones">ボーン一覧。</param>
    /// <param name="index">対象ボーンの添字。</param>
    /// <param name="head">新しい根元位置（キャンバス座標）。</param>
    public static void MoveHead(IReadOnlyList<SpriteRigBone> bones, int index, Vec2 head)
    {
        Affine2 toLocal = ParentGlobalOf(bones, index).Inverse();
        bones[index].Position = toLocal.Transform(head);
    }

    /// <summary>
    /// ボーンの先端を動かす（根元は固定・向きと長さが変わる）。
    ///
    /// <b>先端に生えている子ボーン</b>（ローカル位置が旧先端と一致するもの）は
    /// 新しい先端へ付け直すので、骨の鎖が切れずに伸縮する。
    /// </summary>
    /// <param name="bones">ボーン一覧。</param>
    /// <param name="index">対象ボーンの添字。</param>
    /// <param name="tip">新しい先端位置（キャンバス座標）。</param>
    public static void MoveTip(IReadOnlyList<SpriteRigBone> bones, int index, Vec2 tip)
    {
        var globals = ComputeGlobals(bones);
        Vec2 head = HeadOf(globals, index);
        double oldLength = bones[index].Length;

        // 先端に付いている子を、変換前に記録しておく
        var attachedChildren = new List<int>();
        var parents = BuildParentIndices(bones);
        for (int i = 0; i < bones.Count; i++)
        {
            if (parents[i] != index) continue;
            // 子のローカル位置が (旧長さ, 0) なら「先端に生えている」とみなす
            if (Math.Abs(bones[i].Position.X - oldLength) < TipAttachTolerance &&
                Math.Abs(bones[i].Position.Y) < TipAttachTolerance)
            {
                attachedChildren.Add(i);
            }
        }

        SetHeadAndTip(bones, index, head, tip);

        double newLength = bones[index].Length;
        foreach (int child in attachedChildren)
        {
            bones[child].Position = new Vec2(newLength, 0.0);
        }
    }

    /// <summary>子ボーンが「先端に生えている」とみなすローカル座標の許容誤差（ピクセル）。</summary>
    public const double TipAttachTolerance = 1.0e-6;

    /// <summary>
    /// ボーンを削除し、その子は<b>削除されたボーンの親へ付け替える</b>（ワールド姿勢は保つ）。
    /// ルートボーンが 1 本も残らなくなる削除は拒否する（<c>.sprite_mesh</c> は bones 空を許さない）。
    /// </summary>
    /// <param name="bones">ボーン一覧。</param>
    /// <param name="index">削除するボーンの添字。</param>
    /// <returns>削除できた場合 true。</returns>
    public static bool DeleteBone(List<SpriteRigBone> bones, int index)
    {
        if (index < 0 || index >= bones.Count) return false;
        if (bones.Count <= 1) return false;   // 最後の 1 本は消させない

        var parents = BuildParentIndices(bones);
        int newParent = parents[index];

        // 子を先に付け替える（この時点ではまだ削除ボーンが居るのでワールド姿勢を保てる）
        for (int i = 0; i < bones.Count; i++)
        {
            if (parents[i] == index) Reparent(bones, i, newParent);
        }
        bones.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// ボーン削除に合わせてウェイトの添字を詰め直し、消えたボーンの影響を除いて再正規化する。
    /// </summary>
    /// <param name="weights">頂点ごとの影響一覧（その場で書き換える）。</param>
    /// <param name="removedBoneIndex">削除されたボーンの添字。</param>
    public static void RemapWeightsAfterBoneRemoval(
        List<List<SpriteRigInfluence>> weights, int removedBoneIndex)
    {
        for (int v = 0; v < weights.Count; v++)
        {
            var remapped = new List<SpriteRigInfluence>(weights[v].Count);
            foreach (var influence in weights[v])
            {
                if (influence.BoneIndex == removedBoneIndex) continue;   // 消えたボーンの影響は捨てる
                int index = influence.BoneIndex > removedBoneIndex
                    ? influence.BoneIndex - 1
                    : influence.BoneIndex;
                remapped.Add(new SpriteRigInfluence(index, influence.Weight));
            }
            weights[v] = WeightPaint.Normalize(remapped);
        }
    }
}
