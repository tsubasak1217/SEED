using System;
using System.Collections.Generic;

namespace SEED;

/// <summary>
/// 名前付きイベントの購読テーブルとディスパッチ本体（エンジン内部実装）。
///
/// 利用者向けの入口は <see cref="Events"/>（本クラスは公開しない）。
/// 「イベント名 → 購読リスト」を保持し、発火（Raise）時に同期で全ハンドラを呼ぶ。
///
/// 【設計方針】
/// - イベント名は大文字小文字を区別する（<see cref="StringComparer.Ordinal"/>）。
/// - 引数の種別が一致する購読だけを呼ぶ（暗黙変換はしない）。不一致は警告 1 回で握り潰す。
/// - ハンドラ内の例外は 1 件ずつ捕捉して <see cref="Debug.LogError"/> へ流し、残りのハンドラは続行する。
/// - 発火中の Subscribe / Unsubscribe / Clear は進行中の発火には反映されず次回から反映される
///   （発火前にハンドラ配列をスナップショットするため、イテレーション中のコレクション変更で落ちない）。
/// - 同名イベントの再入は MaxDispatchDepth 段で打ち切る（無限再帰の保険）。
///
/// 【ALC 安全性（ホットリロード）】
/// 静的テーブルが保持するのはイベント名（string）・種別（enum）・ハンドラ（デリゲート）だけで、
/// ユーザースクリプトの Type や Assembly は保持しない。それでもデリゲート自体は
/// ユーザーアセンブリのメソッドを指すため、掴んだままではアンロード可能な AssemblyLoadContext を
/// 解放できない。そこでホットリロード時（ScriptBridge.CompileScripts）に
/// <see cref="ClearAll"/> を呼び、テーブルを丸ごと空にする。
/// </summary>
internal static class EventBus
{
    // ── 定数（マジックナンバー禁止のためすべて名前付き）────────────

    /// <summary>
    /// 同一イベント名の再入（ハンドラの中から同じ名前を Raise する）を許す最大深さ。
    /// これを超える発火は実行せず、警告 1 回を出して打ち切る（無限再帰でスタックを溢れさせない）。
    /// </summary>
    private const int MaxDispatchDepth = 8;

    /// <summary>1 つの発火につき最初に到達する再入深さ（発火していない状態が 0）。</summary>
    private const int InitialDispatchDepth = 0;

    /// <summary>購読 ID の初期値（0 は無効な購読を表す予約値なので 1 から採番する）。</summary>
    private const long FirstSubscriptionId = 1;

    /// <summary>登録されていない購読の ID（未登録ハンドルの目印）。</summary>
    internal const long InvalidSubscriptionId = 0;

    /// <summary>購読が 0 件のときの呼び出し件数。</summary>
    private const int NoHandlersInvoked = 0;

    // ── 状態 ────────────────────────────────────────────────

    /// <summary>購読テーブルと採番の排他ロック（スクリプトは通常単一スレッドだが安全側に倒す）。</summary>
    private static readonly object Gate = new();

    /// <summary>イベント名 → チャネル（購読リスト＋再入深さ）。名前は大文字小文字を区別する。</summary>
    private static readonly Dictionary<string, EventChannel> Channels = new(StringComparer.Ordinal);

    /// <summary>次に払い出す購読 ID。</summary>
    private static long _nextSubscriptionId = FirstSubscriptionId;

    /// <summary>
    /// 同じ内容の警告を出したかの記録（保持するのは文字列キーのみ。ユーザー型は入らない）。
    /// 型不一致や空名のような設定ミスは毎フレーム鳴り続けるとログを潰すため 1 回だけ出す。
    /// </summary>
    private static readonly HashSet<string> WarnedKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// イベント名 1 つ分の購読リストと再入深さ。
    /// 再入深さをチャネル単位に持つことで、別名イベントの入れ子は制限せずに済む。
    /// </summary>
    private sealed class EventChannel
    {
        /// <summary>この名前を購読しているハンドル（登録順。呼び出しもこの順）。</summary>
        public readonly List<EventSubscription> Subscriptions = new();

        /// <summary>現在この名前を発火中の入れ子段数（0 = 発火していない）。</summary>
        public int DispatchDepth = InitialDispatchDepth;
    }

    // ── 発火時にハンドラへ渡す引数（ボクシングを避けるための入れ物）──

    /// <summary>
    /// 発火 1 回分の引数。種別ごとに異なるフィールドへ実値を入れて運ぶ。
    /// object で運ぶと発火のたびにボクシングが発生するため、
    /// 種別タグ＋各型のフィールドを持つ構造体にしている。
    /// </summary>
    internal readonly struct EventPayload
    {
        /// <summary>引数の種別（購読側とこの種別が一致したときだけ呼ぶ）。</summary>
        public readonly EventArgKind Kind;
        /// <summary>String 種別のときの実値。</summary>
        public readonly string StringValue;
        /// <summary>Float 種別のときの実値。</summary>
        public readonly float FloatValue;
        /// <summary>GameObject 種別のときの実値。</summary>
        public readonly GameObject GameObjectValue;

        private EventPayload(EventArgKind kind, string stringValue, float floatValue, GameObject gameObjectValue)
        {
            Kind            = kind;
            StringValue     = stringValue;
            FloatValue      = floatValue;
            GameObjectValue = gameObjectValue;
        }

        /// <summary>引数なしのペイロード。</summary>
        public static EventPayload None()
            => new(EventArgKind.None, string.Empty, default, default);

        /// <summary>文字列引数のペイロード（null は空文字として扱う）。</summary>
        public static EventPayload Of(string? value)
            => new(EventArgKind.String, value ?? string.Empty, default, default);

        /// <summary>float 引数のペイロード。</summary>
        public static EventPayload Of(float value)
            => new(EventArgKind.Float, string.Empty, value, default);

        /// <summary>GameObject 引数のペイロード。</summary>
        public static EventPayload Of(GameObject value)
            => new(EventArgKind.GameObject, string.Empty, default, value);
    }

    // ── 購読 ────────────────────────────────────────────────

    /// <summary>
    /// イベント名とハンドラを購読テーブルへ登録し、解除用ハンドルを返す。
    /// 名前が空・ハンドラが null のときは登録せず、警告 1 回のうえ解除済みハンドルを返す
    /// （呼び出し側が null チェック不要になるよう、null は返さない）。
    /// </summary>
    /// <param name="name">イベント名（大文字小文字を区別）。</param>
    /// <param name="kind">ハンドラが受け取る引数の種別。</param>
    /// <param name="handler">ハンドラ本体（種別に対応するデリゲート型であること）。</param>
    internal static EventSubscription Subscribe(string? name, EventArgKind kind, Delegate? handler)
    {
        // 空名・null ハンドラは設定ミス。登録すると解除もできない幽霊購読になるため弾く。
        if (string.IsNullOrEmpty(name))
        {
            WarnOnce("subscribe:empty-name", "SEED.Events.Subscribe: イベント名が空です。購読は登録されません。");
            return DeadSubscription(name ?? string.Empty, kind);
        }
        if (handler is null)
        {
            WarnOnce($"subscribe:null-handler:{name}",
                     $"SEED.Events.Subscribe: イベント「{name}」のハンドラが null です。購読は登録されません。");
            return DeadSubscription(name, kind);
        }

        lock (Gate)
        {
            // このイベント名のチャネルが無ければ作る（初回購読時に生成）。
            if (!Channels.TryGetValue(name, out var channel))
            {
                channel = new EventChannel();
                Channels.Add(name, channel);
            }

            var subscription = new EventSubscription(_nextSubscriptionId++, name, kind, handler);
            channel.Subscriptions.Add(subscription);
            return subscription;
        }
    }

    /// <summary>
    /// 購読を解除する。未登録・解除済み・null はいずれも無害に無視する。
    /// 発火中に呼ばれてもテーブルからの除去だけを行い、進行中の発火には影響しない
    /// （発火はスナップショットに対して行っているため）。
    /// </summary>
    internal static void Unsubscribe(EventSubscription? subscription)
    {
        if (subscription is null) return;
        if (subscription.Id == InvalidSubscriptionId) return;   // 未登録の解除済みハンドル

        lock (Gate)
        {
            if (Channels.TryGetValue(subscription.Name, out var channel))
            {
                // ID 一致で 1 件だけ除去する（同じデリゲートを複数回購読していても取り違えない）。
                for (var i = 0; i < channel.Subscriptions.Count; i++)
                {
                    if (channel.Subscriptions[i].Id != subscription.Id) continue;
                    channel.Subscriptions.RemoveAt(i);
                    break;
                }

                // 購読が 0 件になり、発火中でもないチャネルは辞書から捨てる（名前の溜め込み防止）。
                if (channel.Subscriptions.Count == 0 && channel.DispatchDepth == InitialDispatchDepth)
                    Channels.Remove(subscription.Name);
            }

            // ハンドラ参照を手放し、以後 IsActive == false にする（二重解除も無害になる）。
            subscription.MarkReleased();
        }
    }

    // ── 発火 ────────────────────────────────────────────────

    /// <summary>
    /// イベントを同期発火し、実際に呼び出したハンドラ件数を返す。
    ///
    /// 呼び出すのは引数種別が一致する購読だけ。不一致の購読は呼ばずに警告 1 回を出す。
    /// ハンドラ内の例外は 1 件ずつ捕捉してエラーログへ流し、残りのハンドラは続行する。
    /// </summary>
    internal static int Raise(string? name, in EventPayload payload)
    {
        if (string.IsNullOrEmpty(name))
        {
            WarnOnce("raise:empty-name", "SEED.Events.Raise: イベント名が空です。発火しません。");
            return NoHandlersInvoked;
        }

        // ── 1. スナップショットを取る ──
        // 発火中に Subscribe / Unsubscribe されてもコレクション変更例外で落ちないよう、
        // 呼び出すハンドラ（種別＋デリゲート）を配列へ写してからロックを離す。
        // この設計により、発火中の購読変更は次回の発火から反映される。
        EventChannel channel;
        (EventArgKind Kind, Delegate Handler)[] snapshot;
        lock (Gate)
        {
            if (!Channels.TryGetValue(name, out var found) || found.Subscriptions.Count == 0)
                return NoHandlersInvoked;   // 誰も購読していないイベントは何もしない（正常系）

            channel = found;

            // 再入（ハンドラの中から同じ名前を Raise）の深さ上限。無限再帰の保険。
            if (channel.DispatchDepth >= MaxDispatchDepth)
            {
                WarnOnce($"raise:max-depth:{name}",
                         $"SEED.Events.Raise: イベント「{name}」の再入が上限 {MaxDispatchDepth} 段に達したため打ち切りました" +
                         "（ハンドラの中で同じイベントを発火し続けていないか確認してください）。");
                return NoHandlersInvoked;
            }

            snapshot = new (EventArgKind, Delegate)[channel.Subscriptions.Count];
            for (var i = 0; i < channel.Subscriptions.Count; i++)
            {
                var sub = channel.Subscriptions[i];
                // 登録中の購読は Handler が非 null（解除時にリストからも除去されるため）。
                snapshot[i] = (sub.Kind, sub.Handler!);
            }

            channel.DispatchDepth++;
        }

        // ── 2. スナップショットに対して同期ディスパッチ ──
        var invoked = NoHandlersInvoked;
        try
        {
            foreach (var (kind, handler) in snapshot)
            {
                // 引数種別が違う購読は呼ばない（float 購読へ string を渡さない）。警告は 1 回だけ。
                if (kind != payload.Kind)
                {
                    WarnOnce($"raise:kind-mismatch:{name}:{kind}:{payload.Kind}",
                             $"SEED.Events: イベント「{name}」を{DescribeKind(payload.Kind)}で発火しましたが、" +
                             $"{DescribeKind(kind)}で購読しているハンドラがあります。型が一致しないため呼び出しません。");
                    continue;
                }

                try
                {
                    InvokeHandler(kind, handler, payload);
                    invoked++;
                }
                catch (Exception ex)
                {
                    // 1 つのハンドラの例外で他のハンドラを巻き添えにしない（隔離してログのみ）。
                    Debug.LogError($"SEED.Events: イベント「{name}」のハンドラで例外が発生しました: {ex}");
                }
            }
        }
        finally
        {
            // 例外経路でも必ず深さを戻す。空になったチャネルはここで片付ける。
            lock (Gate)
            {
                channel.DispatchDepth--;
                if (channel.Subscriptions.Count == 0 && channel.DispatchDepth == InitialDispatchDepth)
                    Channels.Remove(name);
            }
        }

        return invoked;
    }

    /// <summary>種別に応じてデリゲートを実型へキャストして呼ぶ。</summary>
    private static void InvokeHandler(EventArgKind kind, Delegate handler, in EventPayload payload)
    {
        switch (kind)
        {
            case EventArgKind.None:       ((Action)handler)();                                    break;
            case EventArgKind.String:     ((Action<string>)handler)(payload.StringValue);         break;
            case EventArgKind.Float:      ((Action<float>)handler)(payload.FloatValue);           break;
            case EventArgKind.GameObject: ((Action<GameObject>)handler)(payload.GameObjectValue); break;
            default:
                // 列挙値を増やしてここを更新し忘れた場合の検出用（利用者コードでは起きない）。
                Debug.LogError($"SEED.Events: 未対応の引数種別 {kind} です（EventBus.InvokeHandler の更新漏れ）。");
                break;
        }
    }

    // ── 一括クリア ──────────────────────────────────────────

    /// <summary>指定イベント名の購読をすべて解除する。</summary>
    internal static void Clear(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;

        lock (Gate)
        {
            if (!Channels.TryGetValue(name, out var channel)) return;
            foreach (var sub in channel.Subscriptions) sub.MarkReleased();
            channel.Subscriptions.Clear();
            if (channel.DispatchDepth == InitialDispatchDepth) Channels.Remove(name);
        }
    }

    /// <summary>
    /// 全イベントの購読を破棄する。
    ///
    /// ホットリロード（スクリプトアセンブリの再コンパイル＝ALC アンロード）時に
    /// ScriptBridge から必ず呼ぶこと。ユーザーアセンブリのメソッドを指すデリゲートを
    /// 静的テーブルが握ったままだと ALC がアンロードできず、ホットリロードが破綻する。
    /// 警告の 1 回だけの記録もここでリセットし、リロード後は再度警告が出るようにする。
    /// </summary>
    internal static void ClearAll()
    {
        lock (Gate)
        {
            foreach (var channel in Channels.Values)
            {
                foreach (var sub in channel.Subscriptions) sub.MarkReleased();
                channel.Subscriptions.Clear();
            }
            Channels.Clear();
            WarnedKeys.Clear();
        }
    }

    /// <summary>指定イベント名の現在の購読件数（デバッグ・テスト用）。</summary>
    internal static int SubscriberCount(string? name)
    {
        if (string.IsNullOrEmpty(name)) return NoHandlersInvoked;
        lock (Gate)
        {
            return Channels.TryGetValue(name, out var channel) ? channel.Subscriptions.Count : NoHandlersInvoked;
        }
    }

    // ── 補助 ────────────────────────────────────────────────

    /// <summary>テーブルへ登録しない解除済みハンドルを作る（null を返さないための代替値）。</summary>
    private static EventSubscription DeadSubscription(string name, EventArgKind kind)
    {
        var dead = new EventSubscription(InvalidSubscriptionId, name, kind, () => { });
        dead.MarkReleased();
        return dead;
    }

    /// <summary>同じ内容の警告は 1 回だけ出す（毎フレーム発火でログを潰さないため）。</summary>
    private static void WarnOnce(string key, string message)
    {
        lock (Gate)
        {
            if (!WarnedKeys.Add(key)) return;
        }
        Debug.LogWarning(message);
    }

    /// <summary>種別を利用者向けの表記へ変換する（警告文用）。</summary>
    private static string DescribeKind(EventArgKind kind) => kind switch
    {
        EventArgKind.None       => "引数なし",
        EventArgKind.String     => "string 引数",
        EventArgKind.Float      => "float 引数",
        EventArgKind.GameObject => "GameObject 引数",
        _                       => kind.ToString(),
    };
}
