using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using SEEDEditor.Controls;

namespace SEEDEditor.Panels;

/// <summary>
/// InspectorPanel の「参照ピッカーのドロップ解決」実装。
///
/// エディタ内の参照フィールド（スクリプトの [SerializeField]・WaterVolume の制御点参照・
/// WaterLink の接続先・Canvas の基準カメラ参照）はすべて共通の
/// <see cref="ReferencePicker"/> で描かれ、ドロップの解決だけをここへ委ねる。
/// IPC（GET_ACTOR_COMPONENTS）を持つのがインスペクタだけだからである。
///
/// 解決規則（仕様）:
///   ・落とされたのがコンポーネント（インスペクタの見出しをドラッグ）で、その型が
///     要求型に一致する → そのコンポーネント自身を即設定
///   ・そうでなければ「所有アクタ」として解釈し、アクタ内の適合コンポーネントを数える
///       0 件   → ドロップ不可（ドラッグ中に枠を赤くして拒否。落ちてしまった場合は警告）
///       1 件   → 即設定
///       複数   → 選択ウィンドウ（ReferenceSelectorWindow）で選ばせる
///   ・スロット名を保存できない参照（WantSlotName=false）は選択ウィンドウを出さず、
///     存在検証だけを行う（ランタイム規約どおり先頭スロットへ解決される）
/// </summary>
public partial class InspectorPanel : IReferenceDropResolver
{
    // ── ドロップ解決の保留状態 ────────────────────────────────
    //
    // GET_ACTOR_COMPONENTS の応答は非同期 IPC で届くため、
    // 「どのフィールドの、どのアクタに対する問い合わせだったか」をここに保持し、
    // 応答受信（OnActorComponentsReceived）で続きを処理する。

    /// <summary>参照ドロップの解決待ち 1 件分。</summary>
    /// <param name="DroppedActorDfsId">問い合わせ中のアクタ DFS ID。</param>
    /// <param name="OwnerActorId">ドロップを受けた時点で選択されていたアクタ（＝参照の持ち主）。</param>
    /// <param name="Spec">参照フィールドの要求仕様。</param>
    /// <param name="Apply">確定値（アクタ名, スロット名）を適用するコールバック。</param>
    private sealed record PendingReferenceResolution(
        int DroppedActorDfsId, int OwnerActorId,
        ReferenceFieldSpec Spec, Action<string, string?> Apply);

    /// <summary>現在解決待ちの参照ドロップ（無ければ null）。</summary>
    private PendingReferenceResolution? _pendingReference;

    // ── ドラッグ元（コンポーネント見出し）────────────────────

    /// <summary>
    /// インスペクタのコンポーネント見出しから参照ドラッグを開始する。
    ///
    /// 参照ピッカーは「ドロップされたコンポーネント自身」と「その所有アクタ」の
    /// どちらとしても解釈するため、所有アクタの全構成をペイロードへ同梱する。
    /// これによりドラッグ中に IPC 往復なしで可否（＝適合ゼロなら拒否）を出せる。
    /// </summary>
    /// <param name="dragSource">ドラッグの発生元要素（見出しの Border）。</param>
    /// <param name="slotIdx">ドラッグするコンポーネントのスロット添字。</param>
    /// <param name="slotName">ドラッグするコンポーネントのスロット名。</param>
    /// <param name="typeId">ドラッグするコンポーネントの種別（ACTOR_COMPONENTS の "type"）。</param>
    private void StartComponentReferenceDrag(
        DependencyObject dragSource, int slotIdx, string slotName, string typeId)
    {
        if (_currentActorId < 0) return;

        // 直近に受信した自アクタの構成（OnActorComponentsReceived が必ず入れている）。
        // 取れない場合はドラッグ中の判定材料が減るだけで、ドロップ時の IPC 解決は成立する。
        var snapshot = ActorComponentCache.TryGet(_currentActorId);

        // ドラッグ元がスクリプトスロットなら .cs パスも同梱する
        // （スクリプト参照フィールドは型名まで一致を要求するため）。
        var draggedEntry = snapshot?.Components
            .FirstOrDefault(c => c.SlotIdx == slotIdx && c.TypeId == typeId);

        var payload = new ComponentDragPayload
        {
            ActorDfsId         = _currentActorId,
            ActorName          = snapshot?.ActorName ?? _currentActorName,
            SlotIdx            = slotIdx,
            SlotName           = slotName,
            TypeId             = typeId,
            ScriptPath         = draggedEntry?.ScriptPath ?? "",
            HasTransform       = snapshot?.HasTransform ?? false,
            HasCanvasTransform = snapshot?.HasCanvasTransform ?? false,
            OwnerComponents    = snapshot is null
                ? new List<ActorComponentEntry> { new(typeId, slotIdx, slotName, "") }
                : new List<ActorComponentEntry>(snapshot.Components),
        };

        var data = new DataObject(ReferenceDragFormats.InspectorComponent, payload.ToJson());
        // 参照ピッカー側は Move を返す（Hierarchy からのドラッグと効果を揃えるため）。
        DragDrop.DoDragDrop(dragSource, data, DragDropEffects.Move);
    }

    // ── IReferenceDropResolver 実装 ───────────────────────────

    /// <summary>
    /// ドラッグ中の可否判定。IPC を伴わず、手元にある情報だけで判断する。
    /// 判断材料が無い場合は Unknown を返し、ドロップ後の IPC 往復で検証する。
    /// </summary>
    public ReferenceDropVerdict Evaluate(IDataObject data, ReferenceFieldSpec spec)
    {
        // ① インスペクタのコンポーネント見出しからのドラッグ:
        //    ペイロードに所有アクタの全構成が入っているので常に確定判定できる。
        if (ComponentDragPayload.FromDragData(data) is { } payload)
        {
            // ドロップ対象そのものが要求型に一致する
            // （スクリプト参照は .cs の型名まで見るので Matches に委ねる）
            if (ReferenceKindCatalog.Matches(payload.ToDraggedEntry(), spec.Kind))
                return ReferenceDropVerdict.Accept;
            // 一致しなければ「その所有者（アクタ）」として解釈し直す
            return EvaluateSnapshot(payload.ToOwnerSnapshot(), spec);
        }

        // ② Hierarchy / シーンビューからのアクタドラッグ:
        //    構成キャッシュがあれば確定判定、無ければ判定不能。
        var dfsId = ReferenceDragData.TryGetActorDfsId(data);
        if (dfsId is null) return ReferenceDropVerdict.Reject;

        var snapshot = ActorComponentCache.TryGet(dfsId.Value);
        return snapshot is null
            ? ReferenceDropVerdict.Unknown
            : EvaluateSnapshot(snapshot, spec);
    }

    /// <summary>アクタ構成のスナップショットが要求型を満たすかを判定する。</summary>
    private static ReferenceDropVerdict EvaluateSnapshot(
        ActorComponentSnapshot snapshot, ReferenceFieldSpec spec)
    {
        // ルート直付け型（GameObject / Transform / CanvasTransform）は保持の有無だけ
        if (!ReferenceKindCatalog.NeedsSlotSelection(spec.Kind))
            return ReferenceKindCatalog.RootAttachedSatisfied(
                       spec.Kind, snapshot.HasTransform, snapshot.HasCanvasTransform)
                ? ReferenceDropVerdict.Accept
                : ReferenceDropVerdict.Reject;

        return snapshot.ComponentsMatching(spec.Kind).Count > 0
            ? ReferenceDropVerdict.Accept
            : ReferenceDropVerdict.Reject;
    }

    /// <summary>
    /// ドロップされたデータを解決して値を確定する。
    /// コンポーネント直ドロップは即時、アクタドロップは GET_ACTOR_COMPONENTS の往復後に確定する。
    /// </summary>
    public void Resolve(IDataObject data, ReferenceFieldSpec spec, Action<string, string?> apply)
    {
        if (_runtime is null || _currentActorId < 0) return;

        // ── ① コンポーネント直ドロップ: そのコンポーネント自身が要求型なら即確定 ──
        if (ComponentDragPayload.FromDragData(data) is { } payload &&
            ReferenceKindCatalog.Matches(payload.ToDraggedEntry(), spec.Kind))
        {
            if (string.IsNullOrEmpty(payload.ActorName)) return;
            if (!ValidateActorName(payload.ActorName)) return;
            apply(payload.ActorName, spec.WantSlotName
                ? ReferenceKindCatalog.SlotNameToSave(payload.ToDraggedEntry(), spec.Kind)
                : null);
            return;
        }

        // ── ② アクタ（または非適合コンポーネントの所有アクタ）として解決 ──
        var dfsId = ReferenceDragData.TryGetActorDfsId(data);
        if (dfsId is null) return;

        _pendingReference = new PendingReferenceResolution(
            dfsId.Value, _currentActorId, spec, apply);
        _runtime.SendToRuntime($"GET_ACTOR_COMPONENTS:{dfsId.Value}");
    }

    // ── 応答受信後の解決 ──────────────────────────────────────

    /// <summary>
    /// GET_ACTOR_COMPONENTS の応答 JSON で保留中の参照ドロップを解決する。
    /// 呼び出し側（OnActorComponentsReceived）が「保留中かつ ID 一致」を確認してから呼ぶこと。
    /// </summary>
    private void ResolvePendingReference(string json)
    {
        // 保留をローカルへ退避してから即リセットする（再帰・取りこぼし防止）
        var pending = _pendingReference;
        _pendingReference = null;
        if (pending is null) return;

        // 応答待ちの間に選択が変わっていたら、その UI・IPC 宛先はもう別のアクタを
        // 指している。誤ったアクタへ書き込まないよう破棄する。
        if (pending.OwnerActorId != _currentActorId) return;

        var snapshot = ActorComponentSnapshot.TryParse(json);
        if (snapshot is null)
        {
            EditorLog.Write("InspectorPanel: 参照ドロップの解決に失敗（ACTOR_COMPONENTS を解析できません）");
            return;
        }

        var spec      = pending.Spec;
        var kindLabel = ReferenceKindCatalog.DisplayName(spec.Kind);

        if (string.IsNullOrEmpty(snapshot.ActorName))
        {
            MessageBox.Show("ドロップされたアクターの名前を取得できませんでした。",
                "参照設定エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!ValidateActorName(snapshot.ActorName)) return;

        // ── ルート直付け型（GameObject / Transform / CanvasTransform）──
        if (!ReferenceKindCatalog.NeedsSlotSelection(spec.Kind))
        {
            if (!ReferenceKindCatalog.RootAttachedSatisfied(
                    spec.Kind, snapshot.HasTransform, snapshot.HasCanvasTransform))
            {
                ShowKindMissingWarning(snapshot.ActorName, kindLabel);
                return;
            }
            pending.Apply(snapshot.ActorName, null);
            return;
        }

        // ── スロット格納型 ──
        var matches = snapshot.ComponentsMatching(spec.Kind);
        if (matches.Count == 0)
        {
            ShowKindMissingWarning(snapshot.ActorName, kindLabel);
            return;
        }

        // スロット名を保存できない参照は、存在確認だけでアクタ名を確定する
        // （ランタイムは先頭スロットへ解決する規約）。
        if (!spec.WantSlotName)
        {
            pending.Apply(snapshot.ActorName, null);
            return;
        }

        if (matches.Count == 1)
        {
            pending.Apply(snapshot.ActorName,
                ReferenceKindCatalog.SlotNameToSave(matches[0], spec.Kind));
            return;
        }

        // 同種スロットが複数 → 選択ウィンドウ（次フェーズで 2 ページ目を積める構造）
        // 表示名（items）と保存値（saveNames）は別物になり得る
        // （スクリプト参照でスロット名が空のときは保存値 null ＝ 型名で先頭解決）。
        // 選択結果は「何番目を選んだか」で保存値へ引き当てる。
        var items     = new List<string>(matches.Count);
        var saveNames = new List<string?>(matches.Count);
        foreach (var m in matches)
        {
            items.Add(ActorComponentSnapshot.SlotDisplayName(m, spec.Kind));
            saveNames.Add(ReferenceKindCatalog.SlotNameToSave(m, spec.Kind));
        }

        var selected = ReferenceSelectorWindow.Show(
            Window.GetWindow(this),
            new ReferenceSelectorPage(
                $"{kindLabel} を選択",
                $"「{snapshot.ActorName}」には {kindLabel} が {matches.Count} 件あります。\n"
                + "参照するコンポーネントを選択してください。",
                items));
        if (selected is { Count: > 0 })
        {
            var idx = items.IndexOf(selected[0]);
            pending.Apply(snapshot.ActorName, idx >= 0 ? saveNames[idx] : selected[0]);
        }
    }

    /// <summary>要求型を持っていないアクタが落とされたときの警告。</summary>
    private static void ShowKindMissingWarning(string actorName, string kindLabel)
        => MessageBox.Show($"「{actorName}」は {kindLabel} を持っていません。",
            "参照設定エラー", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>
    /// 参照値として保存できるアクタ名かを検証する。
    /// スクリプト参照の書式が "アクタ名|スロット名" のため、区切り文字を含む名前は復元できない。
    /// </summary>
    private static bool ValidateActorName(string actorName)
    {
        if (!actorName.Contains(SEED.ScriptReference.SlotSeparator)) return true;
        MessageBox.Show(
            $"アクタ名に '{SEED.ScriptReference.SlotSeparator}' を含むアクターは参照できません。\n"
            + "アクタ名を変更してください。",
            "参照設定エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }
}
