#if PROFILE_UNLOCK_TESTING
namespace FH6LocalCryptoTool;

public enum JournalRewardKind { Horn, Phrase }

// Exact installed ProgressionThreads -> CollectionCategoryProgress bucket ->
// ProgressionThreadObjectives.DeferredReward links, authenticated 2026-10-15.
// All 33 phrase links independently match the existing Journal-reward catalog.
// No lock-message/name matching, playlist-date guesses or license grants.
internal static class JournalRewardUnlockCatalog
{
    internal sealed record Reward(JournalRewardKind Kind, ulong Item, ulong Category, int Required, string CategoryName);
    internal static IReadOnlyList<Reward> Items { get; } = Array.AsReadOnly(new Reward[]
    {
        new(JournalRewardKind.Horn, 0x1CA87B198FC628ABUL, 0x4676FF86AA21DC44UL, 500, "Tokyo City Food Delivery"),
        new(JournalRewardKind.Horn, 0x1CEC7435413D43E0UL, 0x8D8EDF10324BBF91UL, 3500, "Car Collection"),
        new(JournalRewardKind.Horn, 0x21199E27583D26C5UL, 0x5AF859058BA69F8EUL, 400, "Danger Signs"),
        new(JournalRewardKind.Horn, 0x23DF07DE99422A01UL, 0x4E62D6C233753A3AUL, 800, "Horizon Play"),
        new(JournalRewardKind.Horn, 0x2F24CA199C56045CUL, 0x21D2813026C3C5CFUL, 250, "Wristband Events"),
        new(JournalRewardKind.Horn, 0x3066788F752E6CDFUL, 0xFFCADDFF743A6ED2UL, 600, "Horizon Stunt Party"),
        new(JournalRewardKind.Horn, 0x39164AF31B57AE09UL, 0xE514D9FB4DBBD1B7UL, 100, "Speed Zones"),
        new(JournalRewardKind.Horn, 0x445248F36134A25BUL, 0x8D8EDF10324BBF91UL, 225, "Car Collection"),
        new(JournalRewardKind.Horn, 0x4676268AE1D6776FUL, 0x75135FB3A14D21EUL, 3000, "Horizon Promo"),
        new(JournalRewardKind.Horn, 0x4C28588D643EF307UL, 0x8D8EDF10324BBF91UL, 675, "Car Collection"),
        new(JournalRewardKind.Horn, 0x580E0BC1925958D1UL, 0xC086BF3044D56781UL, 200, "Link Skills"),
        new(JournalRewardKind.Horn, 0x58944B8006B532DCUL, 0x313A2F4FA0ED3029UL, 5000, "Dirt Racing"),
        new(JournalRewardKind.Horn, 0x59019BBEF59D03D9UL, 0x1C0DA0F3F7E4DDFAUL, 1250, "Bonus Boards"),
        new(JournalRewardKind.Horn, 0x63A6D3CF8A522A11UL, 0x1C0DA0F3F7E4DDFAUL, 4500, "Bonus Boards"),
        new(JournalRewardKind.Horn, 0x711FEE24784AFE55UL, 0x66126953D5D401B4UL, 1500, "Discovery"),
        new(JournalRewardKind.Horn, 0x7ED3BAF1515BC670UL, 0x87BD2060162E36DUL, 100, "Car Upgrades"),
        new(JournalRewardKind.Horn, 0x7F1B8154667078D1UL, 0xDCEEFDD80B2E2B3FUL, 900, "Speed Traps"),
        new(JournalRewardKind.Horn, 0x7F920F7ADA4BE6E2UL, 0xC91D514E6700B975UL, 1250, "Garages"),
        new(JournalRewardKind.Horn, 0x80071D6FA7E18115UL, 0xF94B3C553968B8F3UL, 5000, "Road Racing"),
        new(JournalRewardKind.Horn, 0x8915DFAD9019DD1EUL, 0x5AF859058BA69F8EUL, 100, "Danger Signs"),
        new(JournalRewardKind.Horn, 0x8A491E94A447AD13UL, 0x75135FB3A14D21EUL, 750, "Horizon Promo"),
        new(JournalRewardKind.Horn, 0x93B3A1993C94DF06UL, 0x66126953D5D401B4UL, 500, "Discovery"),
        new(JournalRewardKind.Horn, 0x93B6823FFC9E8C31UL, 0x3D7066628EDDE1EDUL, 1000, "Photography"),
        new(JournalRewardKind.Horn, 0x9A97107A6ECBF237UL, 0xE514D9FB4DBBD1B7UL, 1300, "Speed Zones"),
        new(JournalRewardKind.Horn, 0x9B5FC36C4B9AA76BUL, 0x423FEFB257E39986UL, 500, "Festival Playlist"),
        new(JournalRewardKind.Horn, 0x9E0270A9B1AD223DUL, 0x5F01C9BCFA2E42D5UL, 500, "Houses"),
        new(JournalRewardKind.Horn, 0xA1B1A1A182074D6AUL, 0x8D8EDF10324BBF91UL, 1050, "Car Collection"),
        new(JournalRewardKind.Horn, 0xAD90FB7DDE4502D6UL, 0x8D8EDF10324BBF91UL, 1800, "Car Collection"),
        new(JournalRewardKind.Horn, 0xB022F8CEB1CD98D1UL, 0xF92C8E05B78B9FE8UL, 100, "Skills"),
        new(JournalRewardKind.Horn, 0xBEEAB97116CD918AUL, 0x5F01C9BCFA2E42D5UL, 1000, "Houses"),
        new(JournalRewardKind.Horn, 0xC2CAAEACDF3516F0UL, 0x75135FB3A14D21EUL, 100, "Horizon Promo"),
        new(JournalRewardKind.Horn, 0xC4A0AE16786D391EUL, 0xD9B8B280086A9026UL, 4500, "Cross Country Racing"),
        new(JournalRewardKind.Horn, 0xCEB3374C93E2666CUL, 0x596580E377FA1D7EUL, 500, "Cross Country Rivals"),
        new(JournalRewardKind.Horn, 0xD0664447D66DEFDCUL, 0x8B9AD09D4A6D9A0UL, 1500, "Horizon Legend"),
        new(JournalRewardKind.Horn, 0xDEBD38F01A042165UL, 0x8D8EDF10324BBF91UL, 450, "Car Collection"),
        new(JournalRewardKind.Horn, 0xE69846F00F9414EBUL, 0xCB2A80FC117EC3ABUL, 2000, "Regional Mascots"),
        new(JournalRewardKind.Horn, 0xE708053CD0204DE1UL, 0xDCC05D7106E8542CUL, 100, "Drift Zones"),
        new(JournalRewardKind.Phrase, 0x8BC632CD2EABC71UL, 0x1C0DA0F3F7E4DDFAUL, 3000, "Bonus Boards"),
        new(JournalRewardKind.Phrase, 0x10EA7C24CA5BA75CUL, 0x27B4C52DAAD64D27UL, 500, "Horizon Life Events"),
        new(JournalRewardKind.Phrase, 0x2505FE232BDFD647UL, 0xB63EC2C5DF17D631UL, 700, "Trailblazers"),
        new(JournalRewardKind.Phrase, 0x258BD90A0F77F192UL, 0xB63EC2C5DF17D631UL, 100, "Trailblazers"),
        new(JournalRewardKind.Phrase, 0x2636FB58734A188EUL, 0x1C0DA0F3F7E4DDFAUL, 50, "Bonus Boards"),
        new(JournalRewardKind.Phrase, 0x302930BB096A6427UL, 0xDCC05D7106E8542CUL, 1000, "Drift Zones"),
        new(JournalRewardKind.Phrase, 0x4A814A805209703DUL, 0x75135FB3A14D21EUL, 50, "Horizon Promo"),
        new(JournalRewardKind.Phrase, 0x4CF9B25D3D2D0A56UL, 0x1C0DA0F3F7E4DDFAUL, 250, "Bonus Boards"),
        new(JournalRewardKind.Phrase, 0x4E02725829F3CE72UL, 0x596580E377FA1D7EUL, 200, "Cross Country Rivals"),
        new(JournalRewardKind.Phrase, 0x53BA7F27E2AE289AUL, 0x1C0DA0F3F7E4DDFAUL, 2000, "Bonus Boards"),
        new(JournalRewardKind.Phrase, 0x54E9B8460E9DDDDEUL, 0xE514D9FB4DBBD1B7UL, 2000, "Speed Zones"),
        new(JournalRewardKind.Phrase, 0x60490A6694A29B31UL, 0x3387A81CA1468F4EUL, 200, "Road Rivals"),
        new(JournalRewardKind.Phrase, 0x6D0E48CD3978504AUL, 0x1C0DA0F3F7E4DDFAUL, 450, "Bonus Boards"),
        new(JournalRewardKind.Phrase, 0x6E66A271F2A13D4EUL, 0xE514D9FB4DBBD1B7UL, 700, "Speed Zones"),
        new(JournalRewardKind.Phrase, 0x745822E74F926B6DUL, 0x9F165B173C54EF6AUL, 200, "Dirt Rivals"),
        new(JournalRewardKind.Phrase, 0x80B6AB6112D62768UL, 0x1C0DA0F3F7E4DDFAUL, 2500, "Bonus Boards"),
        new(JournalRewardKind.Phrase, 0x94CC640EB5CC1923UL, 0x1C0DA0F3F7E4DDFAUL, 1000, "Bonus Boards"),
        new(JournalRewardKind.Phrase, 0x9BC9F273BD90C370UL, 0x1C0DA0F3F7E4DDFAUL, 1500, "Bonus Boards"),
        new(JournalRewardKind.Phrase, 0xA4A26AE5F05ED6C1UL, 0xC086BF3044D56781UL, 100, "Link Skills"),
        new(JournalRewardKind.Phrase, 0xAC1FF7B1C36A7DD6UL, 0xF92C8E05B78B9FE8UL, 200, "Skills"),
        new(JournalRewardKind.Phrase, 0xBA67661170DBA169UL, 0xDCC05D7106E8542CUL, 400, "Drift Zones"),
        new(JournalRewardKind.Phrase, 0xBD1FB8F5826112FAUL, 0x5AF859058BA69F8EUL, 700, "Danger Signs"),
        new(JournalRewardKind.Phrase, 0xBE4A7A0BAA1AECB8UL, 0x423FEFB257E39986UL, 150, "Festival Playlist"),
        new(JournalRewardKind.Phrase, 0xC64027B048DF8F22UL, 0xDCEEFDD80B2E2B3FUL, 300, "Speed Traps"),
        new(JournalRewardKind.Phrase, 0xCB999E5F8FDF945EUL, 0x27B4C52DAAD64D27UL, 1500, "Horizon Life Events"),
        new(JournalRewardKind.Phrase, 0xCDCBFB24FDD947EAUL, 0x4E62D6C233753A3AUL, 2425, "Horizon Play"),
        new(JournalRewardKind.Phrase, 0xD32BD8D60E43A256UL, 0xDCEEFDD80B2E2B3FUL, 1200, "Speed Traps"),
        new(JournalRewardKind.Phrase, 0xD43A75380E7F2284UL, 0x4E62D6C233753A3AUL, 3125, "Horizon Play"),
        new(JournalRewardKind.Phrase, 0xD4C1EC6CC671524EUL, 0x4E62D6C233753A3AUL, 575, "Horizon Play"),
        new(JournalRewardKind.Phrase, 0xE500E2303248F3B9UL, 0x4E62D6C233753A3AUL, 50, "Horizon Play"),
        new(JournalRewardKind.Phrase, 0xEC92F09A7B76328CUL, 0x4E62D6C233753A3AUL, 1350, "Horizon Play"),
        new(JournalRewardKind.Phrase, 0xF0B3AABC8191104FUL, 0xFFCADDFF743A6ED2UL, 150, "Horizon Stunt Party"),
        new(JournalRewardKind.Phrase, 0xF7EEF8D5691CAD23UL, 0xDCEEFDD80B2E2B3FUL, 2100, "Speed Traps"),
    });
    internal static IReadOnlyDictionary<(JournalRewardKind Kind, ulong Id), Reward> ByItem { get; } = Validate();
    static Dictionary<(JournalRewardKind, ulong), Reward> Validate()
    {
        if (Items.Count != 70 || Items.Count(r => r.Kind == JournalRewardKind.Horn) != 37 ||
            Items.Count(r => r.Kind == JournalRewardKind.Phrase) != 33)
            throw new InvalidOperationException("Journal reward mapping coverage changed.");
        foreach (var r in Items)
        {
            var category = CollectionJournalCatalog.Data.Categories.Single(c => c.Id == r.Category);
            if (r.Required <= 0 || r.Required > category.Maximum ||
                (r.Kind == JournalRewardKind.Horn ? !CarHornUnlockState.AllHorns.Any(h => h.Id == r.Item) :
                    !ForzaLinkPhraseUnlockState.AllRewardPhrases.Any(p => p.Id == r.Item && p.JournalReward)))
                throw new InvalidOperationException("Journal reward definition mismatch.");
        }
        if (!Items.Where(r => r.Kind == JournalRewardKind.Phrase).Select(r => r.Item).ToHashSet()
            .SetEquals(ForzaLinkPhraseUnlockState.AllRewardPhrases.Where(p => p.JournalReward).Select(p => p.Id)))
            throw new InvalidOperationException("Journal phrase reward coverage mismatch.");
        return Items.ToDictionary(r => (r.Kind, r.Item));
    }
}
#endif

