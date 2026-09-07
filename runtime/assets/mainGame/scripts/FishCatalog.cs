// 自動生成 — 編集しないで「図鑑画像を生成」で再生成
// （エディタ: Tools > 図鑑画像を生成 / cmd: generate_fish_thumbnails /
//   エディタ無し: python tools/gen_fish_catalog.py）
// 生成元: runtime/assets/mainGame/actors/Fish/Lv<N>/*.actor

using System;
using System.Collections.Generic;

/// <summary>
/// 図鑑 1 種ぶんの静的データ（prefab から自動抽出した内容）。
/// </summary>
[Serializable]
public struct FishCatalogEntry
{
    /// <summary>魚レベル（1〜<see cref="FishCatalog.MaxLevel"/>）。prefab の置き場所 Lv&lt;N&gt; が正典。</summary>
    public int level;

    /// <summary>アクタ名（.actor の name。ローマ字の種別 ID として使える）。</summary>
    public string actorName;

    /// <summary>表示名（Fish スクリプトの「表示名」。未入力なら <see cref="actorName"/> と同じ）。</summary>
    public string displayName;

    /// <summary>prefab の assets:// パス。</summary>
    public string actorPath;

    /// <summary>図鑑画像（透過 PNG）の assets:// パス。<c>SEED.Sprite.TexturePath</c> にそのまま入る。</summary>
    public string imagePath;
}

/// <summary>
/// 全魚種の図鑑データ表。エディタの「図鑑画像を生成」で丸ごと再生成される。
/// 並び順は「レベル昇順 → アクタ名の辞書順」で固定。
/// </summary>
public static class FishCatalog
{
    /// <summary>収録されている最大の魚レベル。</summary>
    public const int MaxLevel = 10;

    /// <summary>全エントリ（レベル昇順 → アクタ名順）。</summary>
    public static readonly FishCatalogEntry[] Entries =
    {
        new FishCatalogEntry { level = 1, actorName = "iwashi", displayName = "iwashi", actorPath = "assets://mainGame/actors/Fish/Lv1/iwashi.actor", imagePath = "assets://mainGame/textures/zukan/Lv1/iwashi.png" },
        new FishCatalogEntry { level = 1, actorName = "kumanomi", displayName = "kumanomi", actorPath = "assets://mainGame/actors/Fish/Lv1/kumanomi.actor", imagePath = "assets://mainGame/textures/zukan/Lv1/kumanomi.png" },
        new FishCatalogEntry { level = 1, actorName = "nanyouhagi", displayName = "nanyouhagi", actorPath = "assets://mainGame/actors/Fish/Lv1/nanyouhagi.actor", imagePath = "assets://mainGame/textures/zukan/Lv1/nanyouhagi.png" },
        new FishCatalogEntry { level = 1, actorName = "tatsunootoshigo", displayName = "tatsunootoshigo", actorPath = "assets://mainGame/actors/Fish/Lv1/tatsunootoshigo.actor", imagePath = "assets://mainGame/textures/zukan/Lv1/tatsunootoshigo.png" },
        new FishCatalogEntry { level = 2, actorName = "aji", displayName = "aji", actorPath = "assets://mainGame/actors/Fish/Lv2/aji.actor", imagePath = "assets://mainGame/textures/zukan/Lv2/aji.png" },
        new FishCatalogEntry { level = 2, actorName = "kawahagi", displayName = "kawahagi", actorPath = "assets://mainGame/actors/Fish/Lv2/kawahagi.actor", imagePath = "assets://mainGame/textures/zukan/Lv2/kawahagi.png" },
        new FishCatalogEntry { level = 2, actorName = "sacabambaspis", displayName = "sacabambaspis", actorPath = "assets://mainGame/actors/Fish/Lv2/sacabambaspis.actor", imagePath = "assets://mainGame/textures/zukan/Lv2/sacabambaspis.png" },
        new FishCatalogEntry { level = 2, actorName = "tobiuo", displayName = "tobiuo", actorPath = "assets://mainGame/actors/Fish/Lv2/tobiuo.actor", imagePath = "assets://mainGame/textures/zukan/Lv2/tobiuo.png" },
        new FishCatalogEntry { level = 3, actorName = "akaei", displayName = "akaei", actorPath = "assets://mainGame/actors/Fish/Lv3/akaei.actor", imagePath = "assets://mainGame/textures/zukan/Lv3/akaei.png" },
        new FishCatalogEntry { level = 3, actorName = "ika", displayName = "ika", actorPath = "assets://mainGame/actors/Fish/Lv3/ika.actor", imagePath = "assets://mainGame/textures/zukan/Lv3/ika.png" },
        new FishCatalogEntry { level = 3, actorName = "mendako", displayName = "mendako", actorPath = "assets://mainGame/actors/Fish/Lv3/mendako.actor", imagePath = "assets://mainGame/textures/zukan/Lv3/mendako.png" },
        new FishCatalogEntry { level = 3, actorName = "tako", displayName = "tako", actorPath = "assets://mainGame/actors/Fish/Lv3/tako.actor", imagePath = "assets://mainGame/textures/zukan/Lv3/tako.png" },
        new FishCatalogEntry { level = 4, actorName = "chouchinankou", displayName = "chouchinankou", actorPath = "assets://mainGame/actors/Fish/Lv4/chouchinankou.actor", imagePath = "assets://mainGame/textures/zukan/Lv4/chouchinankou.png" },
        new FishCatalogEntry { level = 4, actorName = "ishigakidai", displayName = "ishigakidai", actorPath = "assets://mainGame/actors/Fish/Lv4/ishigakidai.actor", imagePath = "assets://mainGame/textures/zukan/Lv4/ishigakidai.png" },
        new FishCatalogEntry { level = 4, actorName = "madai", displayName = "madai", actorPath = "assets://mainGame/actors/Fish/Lv4/madai.actor", imagePath = "assets://mainGame/textures/zukan/Lv4/madai.png" },
        new FishCatalogEntry { level = 5, actorName = "hamachi", displayName = "hamachi", actorPath = "assets://mainGame/actors/Fish/Lv5/hamachi.actor", imagePath = "assets://mainGame/textures/zukan/Lv5/hamachi.png" },
        new FishCatalogEntry { level = 5, actorName = "iseebi", displayName = "iseebi", actorPath = "assets://mainGame/actors/Fish/Lv5/iseebi.actor", imagePath = "assets://mainGame/textures/zukan/Lv5/iseebi.png" },
        new FishCatalogEntry { level = 5, actorName = "katsuo", displayName = "katsuo", actorPath = "assets://mainGame/actors/Fish/Lv5/katsuo.actor", imagePath = "assets://mainGame/textures/zukan/Lv5/katsuo.png" },
        new FishCatalogEntry { level = 6, actorName = "maguro", displayName = "maguro", actorPath = "assets://mainGame/actors/Fish/Lv6/maguro.actor", imagePath = "assets://mainGame/textures/zukan/Lv6/maguro.png" },
        new FishCatalogEntry { level = 6, actorName = "manta", displayName = "manta", actorPath = "assets://mainGame/actors/Fish/Lv6/manta.actor", imagePath = "assets://mainGame/textures/zukan/Lv6/manta.png" },
        new FishCatalogEntry { level = 6, actorName = "napoleonfish", displayName = "napoleonfish", actorPath = "assets://mainGame/actors/Fish/Lv6/napoleonfish.actor", imagePath = "assets://mainGame/textures/zukan/Lv6/napoleonfish.png" },
        new FishCatalogEntry { level = 7, actorName = "coelacanth", displayName = "coelacanth", actorPath = "assets://mainGame/actors/Fish/Lv7/coelacanth.actor", imagePath = "assets://mainGame/textures/zukan/Lv7/coelacanth.png" },
        new FishCatalogEntry { level = 7, actorName = "kajiki", displayName = "kajiki", actorPath = "assets://mainGame/actors/Fish/Lv7/kajiki.actor", imagePath = "assets://mainGame/textures/zukan/Lv7/kajiki.png" },
        new FishCatalogEntry { level = 7, actorName = "manbou", displayName = "manbou", actorPath = "assets://mainGame/actors/Fish/Lv7/manbou.actor", imagePath = "assets://mainGame/textures/zukan/Lv7/manbou.png" },
        new FishCatalogEntry { level = 8, actorName = "hohojirozame", displayName = "hohojirozame", actorPath = "assets://mainGame/actors/Fish/Lv8/hohojirozame.actor", imagePath = "assets://mainGame/textures/zukan/Lv8/hohojirozame.png" },
        new FishCatalogEntry { level = 8, actorName = "ryuuguunotsukai", displayName = "ryuuguunotsukai", actorPath = "assets://mainGame/actors/Fish/Lv8/ryuuguunotsukai.actor", imagePath = "assets://mainGame/textures/zukan/Lv8/ryuuguunotsukai.png" },
        new FishCatalogEntry { level = 8, actorName = "shumokuzame", displayName = "shumokuzame", actorPath = "assets://mainGame/actors/Fish/Lv8/shumokuzame.actor", imagePath = "assets://mainGame/textures/zukan/Lv8/shumokuzame.png" },
        new FishCatalogEntry { level = 9, actorName = "daiouika", displayName = "daiouika", actorPath = "assets://mainGame/actors/Fish/Lv9/daiouika.actor", imagePath = "assets://mainGame/textures/zukan/Lv9/daiouika.png" },
        new FishCatalogEntry { level = 9, actorName = "jinbeezame", displayName = "jinbeezame", actorPath = "assets://mainGame/actors/Fish/Lv9/jinbeezame.actor", imagePath = "assets://mainGame/textures/zukan/Lv9/jinbeezame.png" },
        new FishCatalogEntry { level = 9, actorName = "megalodon", displayName = "megalodon", actorPath = "assets://mainGame/actors/Fish/Lv9/megalodon.actor", imagePath = "assets://mainGame/textures/zukan/Lv9/megalodon.png" },
        new FishCatalogEntry { level = 10, actorName = "kaiju", displayName = "kaiju", actorPath = "assets://mainGame/actors/Fish/Lv10/kaiju.actor", imagePath = "assets://mainGame/textures/zukan/Lv10/kaiju.png" },
    };

    /// <summary>指定レベルのエントリだけを列挙する（並び順は <see cref="Entries"/> と同じ）。</summary>
    /// <param name="lv">魚レベル。該当が無ければ空列挙を返す。</param>
    public static IEnumerable<FishCatalogEntry> ForLevel(int lv)
    {
        foreach (FishCatalogEntry entry in Entries)
        {
            if (entry.level == lv) { yield return entry; }
        }
    }

    /// <summary>アクタ名で 1 件引く。見つからなければ false。</summary>
    /// <param name="actorName">.actor の name（大文字小文字は区別しない）。</param>
    /// <param name="found">見つかったエントリ。</param>
    public static bool TryGetByActorName(string actorName, out FishCatalogEntry found)
    {
        foreach (FishCatalogEntry entry in Entries)
        {
            if (string.Equals(entry.actorName, actorName, StringComparison.OrdinalIgnoreCase))
            {
                found = entry;
                return true;
            }
        }
        found = default;
        return false;
    }

    /// <summary>表示名で 1 件引く。見つからなければ false。</summary>
    /// <param name="displayName">Fish スクリプトの表示名（大文字小文字は区別しない）。</param>
    /// <param name="found">見つかったエントリ。</param>
    public static bool TryGetByDisplayName(string displayName, out FishCatalogEntry found)
    {
        foreach (FishCatalogEntry entry in Entries)
        {
            if (string.Equals(entry.displayName, displayName, StringComparison.OrdinalIgnoreCase))
            {
                found = entry;
                return true;
            }
        }
        found = default;
        return false;
    }
}
