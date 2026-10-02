namespace SEED.Localization;

// ============================================================
//  LocalizedText.cs — 同じアクタの Text（キャンバスの文字）へ多言語の文字を入れるスクリプト（docs/localization.md §7.1）
//
//  【付け方】文字のアクタに ScriptComponent（型名 "SEED.Localization.LocalizedText"）を付け、インスペクタの「キー」に
//  言語の表のキーを書く（差し込みがあれば「差し込み」に名前と値）。Play で今の言語の文字が入り、言語を切り替えると入れ直す。
//  【スクリプトから】LocalizedBinding.Of<LocalizedText>(actor) で引いて SetArg("money", 1200)・SetCount(n)・SetKey(key)。
//  アクタに Text が無ければ 1 度だけ警告する。
// ============================================================

/// <summary>同じアクタの Text へ多言語の文字を入れる。</summary>
public sealed class LocalizedText : LocalizedBinding
{
    /// <summary>Text が無いことを警告したか。</summary>
    private bool _warnedNoText;

    /// <inheritdoc />
    protected override ILocalizedTarget? FindTarget(GameObject actor) => LocalizedTargetTable.TextOf(actor);

    /// <inheritdoc />
    protected override void OnTargetMissing()
    {
        // Text はエンジンのコンポーネントなので OnStart の時点で有る（無ければ付け忘れ）。すぐ 1 度だけ警告する
        if (_warnedNoText) return;
        _warnedNoText = true;
        Debug.LogWarning($"{LogPrefix} LocalizedText: アクタ {Owner.Name} に Text がありません（キー {Key}）。" +
                         "部品（Button など）の文字には LocalizedLabel を使います");
    }
}
