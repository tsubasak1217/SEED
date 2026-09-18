// ============================================================
//  ResourceAssemblyInitializer.cs — リソースの探索先をエディタ本体へ向ける
//
//  【なぜ要るのか】
//  App.xaml が読み込む辞書は `pack://application:,,,/resources/icons/Icons.xaml`
//  のように **アセンブリ名を書かない** 形になっている。この形の pack URI は
//  WPF の「リソースアセンブリ」から探され、その既定値は **起動アセンブリ**である。
//  このプローブが起動アセンブリなので、そのままではエディタ本体の中にある辞書が
//  1 つも見つからない（IOException: リソース 'resources/icons/icons.xaml' を検索できません）。
//
//  【なぜ公開 API で設定できないのか（実測）】
//  <c>Application.ResourceAssembly</c> の setter は「まだ確定していないとき」しか
//  受け付けない。ところが <c>System.Windows.Application</c> 型に触れた時点で
//  既定値（＝このプローブ）が確定してしまうため、`Main` でも `ModuleInitializer` でも
//  代入は「設定後に変更することはできません」で失敗する。
//  そこで内部フィールドを直接書き換える。
//
//  【この手が許される範囲】
//  ここは **検証用プローブ限定** の措置である。エディタ本体は自分自身が
//  起動アセンブリなので、この問題は起きない（本体側は何も変えなくてよい）。
//  将来の .NET でフィールド名が変わったら、下の警告が出たうえで
//  「アイコンが出ない PNG」になる。静かに壊れないよう、必ずログを出す。
// ============================================================

using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SEEDEditor.Tests.VersionControlPanelPreview;

/// <summary>
/// WPF のリソース探索先をエディタ本体アセンブリへ向ける初期化。
/// </summary>
internal static class ResourceAssemblyInitializer
{
    /// <summary>エディタ本体のアセンブリ名。</summary>
    private const string EDITOR_ASSEMBLY_NAME = "SEEDEditor";

    /// <summary>WPF がリソースアセンブリを覚えている内部フィールドの名前。</summary>
    private const string RESOURCE_ASSEMBLY_FIELD = "_resourceAssembly";

    /// <summary>
    /// リソース解決の実体を持ち得る型。
    /// BaseUriHelper は .NET の版によって置かれているアセンブリが違う
    /// （実測: .NET 9 では PresentationFramework 側）。見つかったものだけ書き換える。
    /// </summary>
    private static readonly string[] ResourceHolderTypeNames =
    {
        "System.Windows.Application, PresentationFramework",
        "System.Windows.Navigation.BaseUriHelper, PresentationFramework",
        "System.Windows.Navigation.BaseUriHelper, WindowsBase",
    };

    /// <summary><c>Main</c> より前に走り、リソースの探索先を向け直す。</summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        var editor  = Assembly.Load(EDITOR_ASSEMBLY_NAME);
        var applied = 0;

        foreach (var typeName in ResourceHolderTypeNames)
        {
            // 候補のうち存在しないものは黙って飛ばす（版によって置き場が違うため）。
            var type = Type.GetType(typeName, throwOnError: false);

            var field = type?.GetField(
                RESOURCE_ASSEMBLY_FIELD,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            if (field is null) continue;

            field.SetValue(null, editor);
            applied++;
        }

        // 1 つも書き換えられなければ、アイコンも共通書式も無い PNG になる。
        // 静かに壊れると「そういう見た目なのか」と誤解されるので必ず知らせる。
        if (applied == 0)
        {
            Console.WriteLine(
                $"  [警告] WPF のリソース探索先を {EDITOR_ASSEMBLY_NAME} へ向けられませんでした。"
                + "アイコンと共通書式が描かれない PNG になります。");
        }
    }
}
