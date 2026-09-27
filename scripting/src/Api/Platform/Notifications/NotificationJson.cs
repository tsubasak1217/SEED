using System.Buffers;
using System.Text;
using System.Text.Json;

namespace SEED.Platform;

/// <summary>
/// 通知（<see cref="Notifications"/>）の JSON の名前と書き込み（内部用。W1-5）。
///
/// 名前の正典は Rust 側 runtime/src/engine/platform/bridge/wire.rs の <c>wire::notification</c>（Java 側は PlatformContract.java の
/// <c>*NOTIFICATION*</c>）。値を変えるときは 3 か所を必ず揃える。書き込みは Utf8JsonWriter（リフレクションなし）。
/// </summary>
internal static class NotificationJson
{
    /// <summary>通知のモジュール。</summary>
    internal const string Module = "notification";

    /// <summary>チャネルを作る。</summary>
    internal const string MethodEnsureChannel = "ensure_channel";

    /// <summary>通知を出す。</summary>
    internal const string MethodShow = "show";

    /// <summary>通知を消す。</summary>
    internal const string MethodCancel = "cancel";

    /// <summary>通知が有効か。</summary>
    internal const string MethodAreEnabled = "are_enabled";

    // ── 欄の名前 ──
    internal const string KeyId = "id";
    internal const string KeyChannelId = "channel_id";
    internal const string KeyChannelName = "name";
    internal const string KeyImportance = "importance";
    internal const string KeyDescription = "description";
    internal const string KeyTitle = "title";
    internal const string KeyBody = "body";
    internal const string KeyOngoing = "ongoing";
    internal const string KeyCategory = "category";
    internal const string KeyActions = "actions";
    internal const string KeyActionId = "id";
    internal const string KeyActionLabel = "label";
    internal const string KeyPayloadJson = "payload_json";
    internal const string KeyEnabled = "enabled";

    // ── 重要度の文字列（NotificationImportance と対応）──
    internal const string ImportanceLow = "low";
    internal const string ImportanceDefault = "default";
    internal const string ImportanceHigh = "high";

    /// <summary>重要度を wire の名前にする。</summary>
    internal static string ImportanceName(NotificationImportance importance) => importance switch
    {
        NotificationImportance.Low => ImportanceLow,
        NotificationImportance.High => ImportanceHigh,
        _ => ImportanceDefault,
    };

    /// <summary>ensure_channel の引数の JSON を作る。</summary>
    internal static string WriteChannel(string channelId, string name, NotificationImportance importance, string description) =>
        Write(writer =>
        {
            writer.WriteString(KeyChannelId, channelId);
            writer.WriteString(KeyChannelName, name);
            writer.WriteString(KeyImportance, ImportanceName(importance));
            writer.WriteString(KeyDescription, description);
        });

    /// <summary>show の引数の JSON を作る（null の欄は空文字・操作の null の要素は空の操作として書き、検査は受け手に任せる）。</summary>
    internal static string WriteRequest(NotificationRequest request) =>
        Write(writer =>
        {
            writer.WriteString(KeyId, request.Id ?? string.Empty);
            writer.WriteString(KeyChannelId, request.ChannelId ?? string.Empty);
            writer.WriteString(KeyTitle, request.Title ?? string.Empty);
            writer.WriteString(KeyBody, request.Body ?? string.Empty);
            writer.WriteBoolean(KeyOngoing, request.Ongoing);
            writer.WriteString(KeyCategory, request.Category ?? string.Empty);
            writer.WriteStartArray(KeyActions);
            foreach (NotificationAction? action in request.Actions ?? System.Array.Empty<NotificationAction>())
            {
                writer.WriteStartObject();
                writer.WriteString(KeyActionId, action?.Id ?? string.Empty);
                writer.WriteString(KeyActionLabel, action?.Label ?? string.Empty);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString(KeyPayloadJson, request.PayloadJson ?? string.Empty);
        });

    /// <summary>ID だけのオブジェクト（cancel の引数）を作る。</summary>
    internal static string IdObject(string id) => PlatformJson.StringObject((KeyId, id));

    /// <summary>オブジェクト 1 つの JSON を書く（中身は <paramref name="body"/> が書く）。</summary>
    private static string Write(System.Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
