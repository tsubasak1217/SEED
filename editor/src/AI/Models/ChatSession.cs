// ============================================================
//  ChatSession.cs — チャットセッションデータモデル
//
//  1 回の会話セッションを表す。AI API への送信履歴と
//  画面表示アイテムの両方を保持し、セッション切り替え時の
//  再レンダリングに使用する。
// ============================================================

namespace SEEDEditor.AI.Models;

/// <summary>
/// チャットセッション — 1 回の会話セッションを表す。
/// AI へ送るメッセージ履歴と画面表示用アイテムを両方保持する。
/// </summary>
public class ChatSession
{
    /// <summary>セッションの一意 ID（8 文字 HEX）</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// セッションタイトル。
    /// 最初のユーザーメッセージの先頭 30 文字から自動生成される。
    /// </summary>
    public string Title { get; set; } = "新しいチャット";

    /// <summary>セッション作成日時（表示用文字列）</summary>
    public string CreatedAt { get; set; } = DateTime.Now.ToString("yyyy/MM/dd HH:mm");

    /// <summary>AI API へ送信する会話履歴（system メッセージは含まない）</summary>
    public List<ChatMessage> Messages { get; set; } = new();

    /// <summary>
    /// チャット画面に表示するアイテム一覧。
    /// セッション切り替え時にこのリストから画面を再構築する。
    /// </summary>
    public List<ChatDisplayItem> DisplayItems { get; set; } = new();
}

/// <summary>
/// チャット画面に表示する 1 アイテム。
/// 発話者名・本文・カラー種別を保持する。
/// </summary>
public class ChatDisplayItem
{
    /// <summary>発話者名（"あなた" / "AI" / "システム" / "エラー" など）</summary>
    public string Sender { get; set; } = "";

    /// <summary>表示するテキスト</summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// カラー種別文字列。
    /// "system"（緑）/ "user"（青）/ "ai"（紫）/ "error"（赤）/ "tool"（グレー）
    /// </summary>
    public string ColorType { get; set; } = "system";
}
