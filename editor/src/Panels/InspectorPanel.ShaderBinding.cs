using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SEEDEditor.Controls;

namespace SEEDEditor.Panels;

/// <summary>
/// シェーダパラメータ行（<c>override</c> 宣言由来）の「送信先」1 式。
///
/// 同じ構文・同じワイヤ表現のパラメータが 3 か所に現れるため、行の作り方は 1 本にまとめ、
/// **違いはこのレコードのデータだけ**にしてある:
///   - 水面シェーディングアセット（WaterVolume スロット）
///   - L3 シェーディングアセット・カメラ添付（Camera スロット）
///   - L3 シェーディングアセット・シーン添付（シーン設定ウィンドウ）
///
/// 接頭辞を <see cref="Func{TResult}"/> で受け取るのは、水面・カメラでは
/// 「送信時点で選択中のアクタ」を埋め込む必要があるため（行の生成時に固定すると、
/// 選択が変わった直後の 1 送信が別アクタへ飛ぶ余地が残る）。
/// </summary>
/// <param name="ParamsJson">ランタイムが送ってきたパラメータ配列の JSON。</param>
/// <param name="SetPrefix">値送信コマンドの接頭辞（末尾はカンマまで。後ろに <c>{name},{x},{y},{z},{w}</c> が付く）。</param>
/// <param name="ResetPrefix">既定値へ戻すコマンドの接頭辞（後ろに <c>{name}</c> が付く）。</param>
/// <param name="BindPrefix"><c>@ref</c> バインド設定コマンドの接頭辞（後ろに <c>{name},{binding}</c> が付く）。</param>
/// <param name="Description">ツールチップに出す種別の説明（例「水面シェーダ」）。</param>
/// <param name="RequiresActorSelection">
/// 送信にアクタ選択が要るか。シーン添付は要らない（シーン全体の設定なので）。
/// </param>
/// <param name="DialogOwner">
/// 色ピッカー・参照選択ウィンドウの親。null ならインスペクタの所属ウィンドウを使う。
/// シーン設定ウィンドウから呼ぶ場合はそのウィンドウを渡す（背面に隠れないようにする）。
/// </param>
public sealed record ShaderParamTarget(
    string        ParamsJson,
    Func<string>  SetPrefix,
    Func<string>  ResetPrefix,
    Func<string>  BindPrefix,
    string        Description,
    bool          RequiresActorSelection,
    Window?       DialogOwner = null);

/// <summary>
/// InspectorPanel の「水面シェーダ <c>@ref</c> パラメータ（参照バインド行）」実装（Phase W8.3）。
///
/// 水面シェーディングアセットが <c>@ref</c> を付けたパラメータは、値を手で入れる行ではなく
/// 「シーン内のどのコンポーネントのどの変数から値を貰うか」を指定する行になる。
/// バインド文字列の書式は <c>"アクタ名|スロット名|変数名"</c>（正典はランタイム側）。
///
/// データフロー:
///   1. アクタをドロップ → <see cref="ShaderBindingDropResolver"/> が
///      <c>GET_BINDABLE_SOURCES:{dfsId},{valueType}</c> を送る
///   2. ランタイムが <c>BINDABLE_SOURCES:{json}</c> を返す
///      → <see cref="OnBindableSourcesReceived"/> が保留中の解決へ渡す
///   3. <see cref="ReferenceSelectorWindow"/> で 2 段選択（1 ページ目=コンポーネント／2 ページ目=変数）
///   4. 確定したら <c>SET_WATER_SHADER_BINDING:{actor},{slot},{name},{binding}</c> を送る
///      （Undo はランタイム側の共通機構が拾うので C# 側では何もしない）
///
/// **候補の一覧を C# 側にミラーしてはならない**。どのコンポーネントのどの変数が
/// どの型で供給できるかはランタイムの唯一の表が決める。
/// </summary>
public partial class InspectorPanel
{
    // ── バインド行のレイアウト定数（マジックナンバー回避）──────────
    /// <summary>見出し列の幅（既存の水面パラメータ行と揃える）。</summary>
    private const double BindingCaptionWidth = 90;

    /// <summary>バインド行の文字サイズ（既存の水面パラメータ行と揃える）。</summary>
    private const double BindingFontSize = 11;

    /// <summary>バインド行全体の上下余白。</summary>
    private static readonly Thickness BindingRowMargin = new(0, 4, 0, 2);

    /// <summary>変数名ラベル・警告マークの左余白。</summary>
    private static readonly Thickness BindingSideLabelMargin = new(4, 0, 0, 0);

    // ── バインド行の配色（既存インスペクタの配色に揃える）──────────
    /// <summary>見出しの文字色。</summary>
    private static readonly Brush BindingCaptionBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));

    /// <summary>変数名ラベルの文字色（見出しより一段落とす）。</summary>
    private static readonly Brush BindingVariableBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    /// <summary>警告（解決できないバインド）の文字色。ReferencePicker の ⚠ と同色。</summary>
    private static readonly Brush BindingWarnBrush = new SolidColorBrush(Color.FromRgb(0xEE, 0xAA, 0x44));

    // ── バインド文字列の書式 ───────────────────────────────────
    /// <summary>バインド文字列 "アクタ名|スロット名|変数名" の区切り文字。</summary>
    private const char BindingSeparator = '|';

    /// <summary>バインド文字列の要素数（アクタ名・スロット名・変数名）。</summary>
    private const int BindingElementCount = 3;

    /// <summary>
    /// 選択ウィンドウのページ数（1 ページ目=コンポーネント／2 ページ目=変数）。
    /// アクタはドロップで決まっているので、選ばせるのは残り 2 要素だけである。
    /// </summary>
    private const int BindingSelectorPageCount = 2;

    // ── ランタイムへ渡す値型名（GET_BINDABLE_SOURCES の value_type）──
    /// <summary>スカラー（range / float パラメータ）。</summary>
    private const string BindableValueTypeScalar = "f32";

    /// <summary>3 成分ベクトル（color パラメータ）。</summary>
    private const string BindableValueTypeVector3 = "vec3";

    /// <summary>アセット宣言の型名のうち、色（vec3）として扱うもの。</summary>
    private const string ShaderParamTypeColor = "color";

    /// <summary>警告アイコンのキー（バインドが解決できないときに行へ添える）。</summary>
    private const string BindingWarnIconKey = "Icon.Warning";

    /// <summary>警告アイコンの一辺サイズ（px）。</summary>
    private const double BindingWarnIconSize = 12;

    /// <summary>バインドが解決できないときのツールチップ文言。</summary>
    private const string BindingUnresolvedTooltip =
        "バインド先が解決できません（アクタ・コンポーネント・変数が消えた、または型が変わった可能性があります）";

    // ============================================================
    //  バインド元候補（BINDABLE_SOURCES 応答）のモデル
    // ============================================================

    /// <summary>バインドできる変数 1 個。</summary>
    /// <param name="Name">バインド文字列の 3 要素目に入る値（変数の識別子）。</param>
    /// <param name="Label">選択ウィンドウ 2 ページ目に出す表示名。</param>
    private sealed record BindableVariable(string Name, string Label);

    /// <summary>バインド元 1 件（＝アクタ内の 1 スロット）。</summary>
    /// <param name="Slot">バインド文字列の 2 要素目に入る値（スロット名）。</param>
    /// <param name="Label">選択ウィンドウ 1 ページ目に出す表示名（コンポーネント種別）。</param>
    /// <param name="Variables">そのスロットが供給できる変数一覧（型は既にランタイムで絞り込み済み）。</param>
    private sealed record BindableSource(string Slot, string Label, IReadOnlyList<BindableVariable> Variables);

    /// <summary>
    /// BINDABLE_SOURCES の応答 JSON を解析する。壊れていれば空リストを返す
    /// （インスペクタを落とさず「候補なし」として扱う）。
    /// </summary>
    private static List<BindableSource> ParseBindableSources(string json)
    {
        var result = new List<BindableSource>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var slot  = entry.TryGetProperty("slot",  out var s) ? s.GetString() ?? "" : "";
                var label = entry.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(slot)) continue;   // スロット名が無いとバインド文字列を作れない

                var vars = new List<BindableVariable>();
                if (entry.TryGetProperty("variables", out var va) && va.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in va.EnumerateArray())
                    {
                        var vname  = v.TryGetProperty("name",  out var vn) ? vn.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(vname)) continue;
                        var vlabel = v.TryGetProperty("label", out var vl) ? vl.GetString() ?? vname : vname;
                        vars.Add(new BindableVariable(vname, vlabel));
                    }
                }
                if (vars.Count == 0) continue;              // 選んでも進めない行は出さない

                result.Add(new BindableSource(slot, string.IsNullOrEmpty(label) ? slot : label, vars));
            }
        }
        catch (JsonException ex)
        {
            EditorLog.Write($"InspectorPanel: BINDABLE_SOURCES を解析できません: {ex.Message}");
        }
        return result;
    }

    // ============================================================
    //  GET_BINDABLE_SOURCES の往復
    // ============================================================

    /// <summary>バインド元候補の問い合わせ 1 件分の保留状態。</summary>
    /// <param name="OwnerActorId">問い合わせた時点で選択されていたアクタ（＝バインドの持ち主）。</param>
    /// <param name="OnReceived">応答が届いたときに呼ぶ続き処理。</param>
    private sealed record PendingBindableSources(
        int OwnerActorId, Action<List<BindableSource>> OnReceived);

    /// <summary>
    /// アクタ選択に紐づかない問い合わせを表す番兵（シーン設定ウィンドウからのバインド行）。
    /// 実在の DFS ID は 0 以上なので衝突しない。
    /// </summary>
    private const int NoOwnerActorId = -1;

    /// <summary>現在解決待ちのバインド元問い合わせ（無ければ null）。</summary>
    private PendingBindableSources? _pendingBindableSources;

    /// <summary>
    /// バインド元候補を問い合わせる。応答は非同期に届くため、続き処理をコールバックで受け取る。
    ///
    /// 応答（BINDABLE_SOURCES）にはアクタ DFS ID が含まれないので、突き合わせは
    /// 「直前に投げた 1 件」だけを保持する方式で行う。バインド行のドロップは
    /// モーダルな操作であり同時に 2 件走ることはない。
    /// </summary>
    /// <param name="actorDfsId">候補を持つ側（ドロップされた）アクタの DFS ID。</param>
    /// <param name="valueType">要求する値型（<c>f32</c> / <c>vec3</c>）。</param>
    /// <param name="onReceived">応答受信時の続き処理。</param>
    /// <param name="requiresActorSelection">
    /// アクタ選択に紐づく問い合わせか。シーン設定ウィンドウのバインド行は
    /// アクタに属さないので false（このとき応答時の所有者照合も行わない）。
    /// </param>
    private void RequestBindableSources(
        int actorDfsId, string valueType, bool requiresActorSelection,
        Action<List<BindableSource>> onReceived)
    {
        if (_runtime is null) return;
        if (requiresActorSelection && _currentActorId < 0) return;

        // 所有者を持たない問い合わせは NoOwnerActorId で記録し、応答時の照合を飛ばす。
        _pendingBindableSources = new PendingBindableSources(
            requiresActorSelection ? _currentActorId : NoOwnerActorId, onReceived);

        // アクタ名は BINDABLE_SOURCES の応答に含まれないため、キャッシュに無い場合に備えて
        // 構成も一緒に取り寄せておく（IPC は順序どおりに処理・返送されるので、
        // 応答を捌く時点ではキャッシュが埋まっている）。
        if (ActorComponentCache.TryGet(actorDfsId) is null)
            _runtime.SendToRuntime($"GET_ACTOR_COMPONENTS:{actorDfsId}");

        _runtime.SendToRuntime($"GET_BINDABLE_SOURCES:{actorDfsId},{valueType}");
    }

    /// <summary>
    /// BINDABLE_SOURCES 応答の受信（ワーカースレッドから来るので UI スレッドへ渡す）。
    /// </summary>
    private void OnBindableSourcesReceived(string json)
    {
        Dispatcher.InvokeAsync(() =>
        {
            // 保留をローカルへ退避してから即リセットする（再帰・取りこぼし防止）
            var pending = _pendingBindableSources;
            _pendingBindableSources = null;
            if (pending is null) return;

            // 応答待ちの間に選択が変わっていたら、その IPC 宛先はもう別のアクタを指している。
            // 誤ったアクタへバインドを書き込まないよう破棄する。
            if (pending.OwnerActorId != NoOwnerActorId && pending.OwnerActorId != _currentActorId)
                return;

            pending.OnReceived(ParseBindableSources(json));
        });
    }

    // ============================================================
    //  値型の対応表
    // ============================================================

    /// <summary>
    /// アセット宣言の型名（color / range / float）を、GET_BINDABLE_SOURCES へ渡す
    /// 値型名へ変換する。色だけが 3 成分、それ以外はスカラーである。
    /// </summary>
    private static string BindableValueTypeOf(string paramType)
        => paramType == ShaderParamTypeColor ? BindableValueTypeVector3 : BindableValueTypeScalar;

    // ============================================================
    //  バインド行の生成
    // ============================================================

    /// <summary>
    /// <c>@ref</c> パラメータ 1 個ぶんの参照バインド行を作る。
    ///
    /// 行の構成: [見出し] [参照ピッカー] [→ 変数名] [⚠]（＋ <c>@reset</c> があればリセットボタン）。
    /// ピッカー自身はアクタ名／スロット名しか表示できないため、変数名は右隣の
    /// 小さなラベルとツールチップで補う（ReferencePicker は書き換えない）。
    /// </summary>
    /// <param name="target">送信先 1 式（IPC 接頭辞・ダイアログ親・説明）。</param>
    /// <param name="paramName">アセット内のパラメータ識別子（IPC のキー）。</param>
    /// <param name="paramLabel">表示ラベル。</param>
    /// <param name="paramType">アセット宣言の型名（color / range / float）。</param>
    /// <param name="binding">現在のバインド文字列（"アクタ名|スロット名|変数名"。未設定なら空）。</param>
    /// <param name="bindingOk">バインドが今解決できているか（false なら ⚠ を出す）。</param>
    /// <param name="canReset">アセットが <c>@reset</c> を付けているか。</param>
    /// <param name="sendResetParam">値をアセット既定値へ戻す IPC を送る処理（呼び出し側と共有）。</param>
    private UIElement BuildShaderBindingRow(
        ShaderParamTarget target, string paramName, string paramLabel, string paramType,
        string binding, bool bindingOk, bool canReset, Action<string> sendResetParam)
    {
        // ── 現在のバインドを 3 要素へ割る（壊れていれば「未設定」扱い）──
        var parts = binding.Split(BindingSeparator);
        var hasBinding   = parts.Length == BindingElementCount;
        var boundActor   = hasBinding ? parts[0] : "";
        var boundSlot    = hasBinding ? parts[1] : "";
        var boundVarName = hasBinding ? parts[2] : "";

        // ── バインドの設定・解除 IPC（空文字列を送ると解除）──
        // Undo はランタイム側の共通機構が拾うので、ここで履歴を積んではいけない。
        void SendBinding(string value)
        {
            if (target.RequiresActorSelection && _currentActorId < 0) return;
            _runtime?.SendToRuntime($"{target.BindPrefix()}{paramName},{value}");
        }

        // ── 変数名ラベル（ピッカーが表示できない 3 要素目を補う）──
        var variableBlock = new TextBlock
        {
            Foreground        = BindingVariableBrush,
            FontSize          = BindingFontSize,
            Margin            = BindingSideLabelMargin,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // ── 解決できないバインドの警告（ReferencePicker 自身の警告とは別要因）──
        //    アクタは居るがコンポーネント／変数が無い・型が変わった、を知らせる。
        var warnBlock = SEEDEditor.Controls.AppIcon.Create(BindingWarnIconKey, BindingWarnIconSize);
        warnBlock.SetBrush(BindingWarnBrush);
        warnBlock.Margin            = BindingSideLabelMargin;
        warnBlock.VerticalAlignment = VerticalAlignment.Center;
        warnBlock.ToolTip           = BindingUnresolvedTooltip;
        warnBlock.Visibility        = Visibility.Collapsed;

        // 変数名ラベルと警告表示を現在値から作り直す。
        void RefreshSideLabels(string variableName, bool resolved)
        {
            var hasVariable    = !string.IsNullOrEmpty(variableName);
            variableBlock.Text = hasVariable ? $"→ {variableName}" : "";
            variableBlock.ToolTip = hasVariable
                ? $"バインド先の変数: {variableName}"
                : null;
            warnBlock.Visibility = hasVariable && !resolved ? Visibility.Visible : Visibility.Collapsed;
        }
        RefreshSideLabels(boundVarName, bindingOk);

        // ── 参照ピッカー本体 ──
        // Kind はどのアクタでも一旦受け付ける GameObject にしておき、
        // 「そのアクタが本当に供給できるか」は GET_BINDABLE_SOURCES の応答で判定する。
        var spec = new ReferenceFieldSpec
        {
            Kind         = ReferenceKindCatalog.GameObjectKind,
            WantSlotName = true,
            Caption      = null,                        // 見出しは行側で出すため作らせない
            UnsetText    = "（バインドなし）",
            ExtraTooltip = "アクタをドロップしてコンポーネントと変数を選びます",
            ClearTooltip = "バインドを解除する（保存値／既定値へ戻ります）",
        };

        ReferencePicker? picker = null;
        var resolver = new ShaderBindingDropResolver(
            this, target, BindableValueTypeOf(paramType),
            (actorName, slotName, variableName) =>
            {
                // 3 要素が揃ったのでバインドを確定する。
                // 表示（アクタ名／スロット名）は resolver が apply 経由で更新済みなので、
                // ここでは IPC 送信と変数名ラベルの更新だけを行う。
                SendBinding($"{actorName}{BindingSeparator}{slotName}{BindingSeparator}{variableName}");
                RefreshSideLabels(variableName, resolved: true);
            });

        picker = ReferencePicker.Create(
            spec, boundActor, boundSlot,
            (actor, _) =>
            {
                // ここへ非 null で来るのは resolver 経由の確定時のみで、その IPC は
                // 上のコールバックが既に送っている。✕（解除）だけをここで処理する。
                if (actor is not null) return;
                SendBinding("");
                RefreshSideLabels("", resolved: true);
            },
            resolver);

        // ── 行レイアウト（[見出し] ピッカー [→ 変数名] [⚠]）──
        var grid = new Grid { Margin = BindingRowMargin };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BindingCaptionWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var caption = new TextBlock
        {
            Text              = paramLabel,
            Foreground        = BindingCaptionBrush,
            FontSize          = BindingFontSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(caption, 0);
        Grid.SetColumn(picker.Element, 1);
        Grid.SetColumn(variableBlock, 2);
        Grid.SetColumn(warnBlock, 3);
        grid.Children.Add(caption);
        grid.Children.Add(picker.Element);
        grid.Children.Add(variableBlock);
        grid.Children.Add(warnBlock);
        grid.ToolTip = $"{paramName}（{target.Description}のパラメータ／参照バインド）";

        if (!canReset) return grid;

        // `@ref` と `@reset` の併用: リセットは「バインドを解除してから既定値へ戻す」。
        // 片方だけだとバインドが値を上書きし続けるので、必ず 2 通とも送る。
        return SEEDEditor.Controls.ResetButtonFactory.Wrap(
            grid,
            $"「{paramLabel}」のバインドを解除し、アセットの既定値に戻す（Ctrl+Z で取り消せます）",
            () =>
            {
                SendBinding("");
                RefreshSideLabels("", resolved: true);
                picker?.SetReference(null, null);
                sendResetParam(paramName);
            });
    }

    // ============================================================
    //  バインド行専用のドロップ解決役
    // ============================================================

    /// <summary>
    /// 水面シェーダ <c>@ref</c> 行のドロップ解決役。
    ///
    /// 既存の <see cref="InspectorPanel"/> 本体の解決（GET_ACTOR_COMPONENTS で
    /// 「要求型のコンポーネントを持つか」を見る流れ）とは別物である。バインドは
    /// 「コンポーネントの型」ではなく「供給できる変数の型」で決まるため、
    /// 候補は必ず GET_BINDABLE_SOURCES に問い合わせる。
    /// </summary>
    private sealed class ShaderBindingDropResolver : IReferenceDropResolver
    {
        /// <summary>IPC と選択ウィンドウの親を持つインスペクタ。</summary>
        private readonly InspectorPanel _owner;

        /// <summary>送信先 1 式（アクタ選択の要否・ダイアログの親）。</summary>
        private readonly ShaderParamTarget _target;

        /// <summary>要求する値型（<c>f32</c> / <c>vec3</c>）。</summary>
        private readonly string _valueType;

        /// <summary>3 要素（アクタ名・スロット名・変数名）が確定したときに呼ぶ処理。</summary>
        private readonly Action<string, string, string> _onBound;

        /// <param name="owner">IPC を持つインスペクタ。</param>
        /// <param name="target">送信先 1 式。</param>
        /// <param name="valueType">要求する値型（<c>f32</c> / <c>vec3</c>）。</param>
        /// <param name="onBound">確定した (アクタ名, スロット名, 変数名) を受け取る処理。</param>
        public ShaderBindingDropResolver(
            InspectorPanel owner, ShaderParamTarget target, string valueType,
            Action<string, string, string> onBound)
        {
            _owner     = owner;
            _target    = target;
            _valueType = valueType;
            _onBound   = onBound;
        }

        /// <summary>
        /// ドラッグ中の可否判定。アクタとして解釈できるなら「判定不能」を返して受け付け、
        /// 実際に供給できるかはドロップ後の GET_BINDABLE_SOURCES で判定する
        /// （型の正典はランタイム側にしかないため、ここでは判断しない）。
        /// </summary>
        public ReferenceDropVerdict Evaluate(IDataObject data, ReferenceFieldSpec spec)
            => ReferenceDragData.TryGetActorDfsId(data) is null
                ? ReferenceDropVerdict.Reject
                : ReferenceDropVerdict.Unknown;

        /// <summary>
        /// ドロップされたアクタのバインド元候補を問い合わせ、2 段選択で確定する。
        /// </summary>
        /// <param name="data">ドロップされたドラッグデータ。</param>
        /// <param name="spec">参照フィールドの要求仕様（この解決役では表示用途のみ）。</param>
        /// <param name="apply">ピッカーの表示更新（アクタ名, スロット名）。変数名は別経路で返す。</param>
        public void Resolve(IDataObject data, ReferenceFieldSpec spec, Action<string, string?> apply)
        {
            var dfsId = ReferenceDragData.TryGetActorDfsId(data);
            if (dfsId is null) return;

            // ドラッグデータに乗っているアクタ名を最優先で使う（コンポーネント見出しからの
            // ドラッグはここで確定する）。無ければ構成キャッシュ、それも無ければ
            // 応答受信時に取り寄せ済みのキャッシュから引く。
            var actorNameFromDrag = ComponentDragPayload.FromDragData(data)?.ActorName ?? "";

            _owner.RequestBindableSources(
                dfsId.Value, _valueType, _target.RequiresActorSelection, sources =>
            {
                var actorName = !string.IsNullOrEmpty(actorNameFromDrag)
                    ? actorNameFromDrag
                    : ActorComponentCache.TryGet(dfsId.Value)?.ActorName ?? "";
                if (string.IsNullOrEmpty(actorName))
                {
                    EditorLog.Write(
                        $"InspectorPanel: バインド先アクタ（DFS ID {dfsId.Value}）の名前を取得できません");
                    return;
                }
                // バインド文字列は '|' 区切りなので、名前に区切り文字を含むアクタは復元できない。
                if (!ValidateActorName(actorName)) return;

                if (sources.Count == 0)
                {
                    // 候補ゼロ: 選択ウィンドウは出さず、理由をログに残して何もしない。
                    EditorLog.Write(
                        $"InspectorPanel: 「{actorName}」には {_valueType} を供給できる変数がありません");
                    return;
                }

                if (!TrySelectBinding(sources, actorName, out var slotName, out var variableName)) return;

                apply(actorName, slotName);                 // ピッカーの表示（アクタ名 / スロット名）
                _onBound(actorName, slotName, variableName); // 3 要素そろえてバインド確定
            });
        }

        /// <summary>
        /// 2 段選択ウィンドウ（1 ページ目=コンポーネント／2 ページ目=変数）を出す。
        /// キャンセルされたら false を返す。
        /// </summary>
        /// <param name="sources">バインド元候補。</param>
        /// <param name="actorName">説明文へ出すアクタ名。</param>
        /// <param name="slotName">確定したスロット名。</param>
        /// <param name="variableName">確定した変数名。</param>
        private bool TrySelectBinding(
            List<BindableSource> sources, string actorName,
            out string slotName, out string variableName)
        {
            slotName = variableName = "";

            // ── 1 ページ目の表示名 → 元エントリ ──
            // 同じ label が複数スロットに出うるので、重複するものだけスロット名を添えて区別する。
            var labelCounts = new Dictionary<string, int>();
            foreach (var s in sources)
                labelCounts[s.Label] = labelCounts.TryGetValue(s.Label, out var c) ? c + 1 : 1;

            var sourceByDisplay = new Dictionary<string, BindableSource>();
            var sourceItems     = new List<string>(sources.Count);
            foreach (var s in sources)
            {
                var display = labelCounts[s.Label] > 1 ? $"{s.Label} ({s.Slot})" : s.Label;
                // それでも衝突する場合（同名スロット）に備えて必ず一意化する
                var unique = display;
                for (int i = 2; sourceByDisplay.ContainsKey(unique); i++) unique = $"{display} #{i}";
                sourceByDisplay[unique] = s;
                sourceItems.Add(unique);
            }

            // ── 2 ページ目の表示名 → 変数名 ──
            // 直近に表示したページぶんだけ保持すればよい（戻るで作り直される）。
            var variableByDisplay = new Dictionary<string, string>();

            ReferenceSelectorPage? NextPage(IReadOnlyList<string> path)
            {
                // 2 ページ目まで選び終わったら確定（null を返すと選択パスが返る）
                if (path.Count != 1) return null;
                if (!sourceByDisplay.TryGetValue(path[0], out var entry)) return null;

                variableByDisplay.Clear();
                var varCounts = new Dictionary<string, int>();
                foreach (var v in entry.Variables)
                    varCounts[v.Label] = varCounts.TryGetValue(v.Label, out var c) ? c + 1 : 1;

                var varItems = new List<string>(entry.Variables.Count);
                foreach (var v in entry.Variables)
                {
                    var display = varCounts[v.Label] > 1 ? $"{v.Label} ({v.Name})" : v.Label;
                    var unique  = display;
                    for (int i = 2; variableByDisplay.ContainsKey(unique); i++) unique = $"{display} #{i}";
                    variableByDisplay[unique] = v.Name;
                    varItems.Add(unique);
                }

                return new ReferenceSelectorPage(
                    "バインドする変数を選択",
                    $"「{entry.Label}」から値を受け取る変数を選択してください。",
                    varItems);
            }

            var selected = ReferenceSelectorWindow.Show(
                _target.DialogOwner ?? Window.GetWindow(_owner),
                new ReferenceSelectorPage(
                    "バインド元のコンポーネントを選択",
                    $"「{actorName}」から値を受け取るコンポーネントを選択してください。",
                    sourceItems),
                NextPage);

            if (selected is not { Count: BindingSelectorPageCount }) return false;  // キャンセル
            if (!sourceByDisplay.TryGetValue(selected[0], out var source))    return false;
            if (!variableByDisplay.TryGetValue(selected[1], out var variable)) return false;

            slotName     = source.Slot;
            variableName = variable;
            return true;
        }
    }
}
