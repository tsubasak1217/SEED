using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.Runtime;

/// <summary>
/// WGSL シェーディングアセットの「ランタイム由来の検証」を仲介するサービス。
///
/// 【責務】
/// - 検証依頼（VALIDATE_WGSL）の送信と、応答（WGSL_DIAG）の request_id 相関
/// - 応答 JSON の <see cref="WgslDiagnostic"/> への変換
/// - 応答が来ないケースのタイムアウト回収（pending のリーク防止）
///
/// 【責務外】
/// - 波線描画・行/オフセット変換・UI 表示（すべて ScriptEditorPanel 側）
/// - パイプの生成・接続管理（RuntimeManager 側）
///
/// 【IPC 契約（Rust 側と一致・変更禁止）】
/// - 送信: <c>VALIDATE_WGSL:{request_id},{json_source}</c>
///   request_id は 10 進整数、json_source は JSON 文字列リテラル（前後の " 込み・改行は \n）。
/// - 受信: <c>WGSL_DIAG:{request_id},{json_array}</c>
///   json_array は <c>[{"message": string, "line": number|null, "variant": string}]</c>。成功時は空配列。
/// </summary>
public sealed class WgslValidationService : IDisposable
{
    /// <summary>
    /// 1 回の検証依頼を待つ最大時間。
    /// これを超えたら「検証不可」として null を返し、pending エントリを必ず回収する
    /// （ランタイムが Play へ遷移した・落ちた等で応答が来ない場合のリークを防ぐ）。
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    /// <summary>ランタイム（IPC 送受信の実体）。</summary>
    private readonly RuntimeManager _runtime;

    /// <summary>request_id → 応答待ちタスク。応答受信・タイムアウトのどちらでも必ず除去する。</summary>
    private readonly ConcurrentDictionary<long, TaskCompletionSource<string>> _pending = new();

    /// <summary>送信 request_id（1 から単調増加）。</summary>
    private long _nextRequestId;

    /// <summary>破棄済みフラグ（多重購読解除の防止）。</summary>
    private bool _disposed;

    /// <summary>ランタイムの受信イベントを購読して相関を開始する。</summary>
    public WgslValidationService(RuntimeManager runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _runtime.WgslDiagnosticsReceived += OnDiagnosticsReceived;
    }

    /// <summary>
    /// WGSL ソースの検証をランタイムへ依頼し、診断一覧を返す。
    ///
    /// 戻り値が <c>null</c>（= 呼び出しが null Task を返す、または Task の結果が null）の場合は
    /// 「検証できなかった」ことを表し、呼び出し側は既存表示を維持する。
    /// 空リストは「検証した結果エラー無し」を表す。
    /// </summary>
    /// <param name="source">検証対象の WGSL ソース全文。</param>
    /// <returns>診断一覧（空 = エラー無し）。検証不可なら null。</returns>
    public async Task<IReadOnlyList<WgslDiagnostic>?> ValidateAsync(string source)
    {
        // 送信できない状況（Play/Pause 中・パイプ未接続）は「検証不可」とし、エラー扱いしない。
        long requestId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        if (!_runtime.SendValidateWgsl(requestId, source))
        {
            _pending.TryRemove(requestId, out _);
            return null;
        }

        try
        {
            // 応答待ち（タイムアウト付き）。時間切れなら検証不可として扱う。
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(RequestTimeout)).ConfigureAwait(false);
            if (completed != tcs.Task) return null;

            return ParseDiagnostics(await tcs.Task.ConfigureAwait(false));
        }
        finally
        {
            // 応答・タイムアウト・例外のいずれでも pending を必ず回収する。
            _pending.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// ランタイムからの WGSL_DIAG 受信（パイプ受信スレッド）。
    /// request_id で待機中のタスクを完了させる。未知の id は既にタイムアウトした依頼なので無視する。
    /// </summary>
    private void OnDiagnosticsReceived(long requestId, string jsonArray)
    {
        if (_pending.TryGetValue(requestId, out var tcs))
            tcs.TrySetResult(jsonArray);
    }

    /// <summary>
    /// 応答 JSON 配列を <see cref="WgslDiagnostic"/> のリストへ変換する。
    /// 壊れた JSON は「検証不可」ではなく空（エラー無し）として扱わず、null を返して既存表示を維持する。
    /// </summary>
    private static IReadOnlyList<WgslDiagnostic>? ParseDiagnostics(string jsonArray)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonArray);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var list = new List<WgslDiagnostic>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                // message: 必須。無ければその要素は捨てる（表示できないため）。
                if (!element.TryGetProperty("message", out var messageProp)
                    || messageProp.ValueKind != JsonValueKind.String)
                    continue;

                // line: number | null。null / 欠落 / 非数値はすべて「行不明」として null にする。
                int? line = null;
                if (element.TryGetProperty("line", out var lineProp)
                    && lineProp.ValueKind == JsonValueKind.Number
                    && lineProp.TryGetInt32(out var lineValue))
                    line = lineValue;

                // variant: 欠落時は空文字（ラベル整形側で括弧を省く）。
                string variant = element.TryGetProperty("variant", out var variantProp)
                              && variantProp.ValueKind == JsonValueKind.String
                    ? variantProp.GetString() ?? string.Empty
                    : string.Empty;

                list.Add(new WgslDiagnostic(messageProp.GetString() ?? string.Empty, line, variant));
            }
            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>ランタイムの受信イベント購読を解除する。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtime.WgslDiagnosticsReceived -= OnDiagnosticsReceived;
    }
}
