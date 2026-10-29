#if PROFILE_UNLOCK_TESTING
using System.Text;
using System.Text.RegularExpressions;

namespace FH6LocalCryptoTool;

internal static class ExplorationProgressCatalog
{
    internal sealed record Mapping(ulong Challenge, string Kind, string Entity, ulong Key = 0, int Stars = 0);
    internal static ulong Hash(string text)
    {
        ulong h = 14695981039346656037;
        foreach (byte b in Encoding.UTF8.GetBytes(text)) h = unchecked((h ^ b) * 1099511628211);
        return h;
    }
    // EntityModel permanent landmark names. All 74 exact name hashes match both
    // fully discovered references. Three temporary car-meet triggers are NOT
    // permanent locations and must not be inserted into LandmarkSaveState.
    internal static IReadOnlyList<string> Landmarks { get; } = Array.AsReadOnly(@"
arashiyama_bamboo_forest bandai_azuma_snow_corridor drift_circuit hirosaki_castle hotel_area_06 ine
irobuohashi_bridge lake_nukabira minami_awa_double_road mountain_circuit mt_haruna narai_juku_hot_springs
norikura_skyline okishinaimura okuibuki_parking_lot plains_settlement ruriko_ji_temple seaside_circuit
shikisai_no_oka shirakawa_go snow_monster_forest soni_highlands sunflower_farm tea_farm temple_of_nachi_falls
venus_line wind_farm yahikoyama_panorama_ropeway yoshinoyama radio_towers oyashirazu_cliff_tunnels
lake_viewing_platform matsumi_great_bridge cherry_fields fuji_shibazakura hakone_nanamagari
inakadate_tanbo_rice_art izu_skyline jodogahama_beach legend_island_circuit seaside_offroad_circuit
seaside_park torii_gate airfield bandai_azuma_skyline sekibe_kaijo_bridge cedar_avenue naruo_golf_course
kinakuji_temple kitayama_big_daisugi loop_bridge meoto_iwa_rock rollercoaster_road satta_pass seabed_road
stadium osaka_tunnels festival_site legend_island_festival_site spaceport docks ski_resort shibuya_crossing
ginkgo_avenue shinjuku_national_garden mall_cut_through tokyo_railway_station akihabara railway_mall_cut_through
daikoku_parking_lot rainbow_bridge tatsumi_parking_lot tokyo_tower edogawa_baseball_stadium
".Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // Complete ten-ID region set agrees exactly in two independent fully
    // discovered snapshots; the early save is a six-ID subset. Individual
    // name-to-native-ID links are NOT inferred. Only full-region sync is enabled.
    internal static IReadOnlyList<ulong> AllRegions { get; } = Array.AsReadOnly(new ulong[]
    {
        0xA99D148283EDEBDF, 0xF284A5645BBE1997, 0x3DB353B3BFFBD5F6, 0xD4B53B4F0C984CB6,
        0xA9EBF51E14D8A05B, 0xB8CD0180B7A0BD3C, 0x2C9DE4501237DB94, 0x2B31648D10759D01,
        0xCAD04B84D97E55C0, 0xCAA02088A61207C2
    });
    static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["kinkaku_ji_temple"] = "kinakuji_temple", ["cedar_lane"] = "cedar_avenue",
        ["irabuohashi_bridge"] = "irobuohashi_bridge", ["meoto_iwa"] = "meoto_iwa_rock",
        ["golf_course"] = "naruo_golf_course", ["daikoku_car_meet_trigger"] = "daikoku_car_meet",
        ["okuibuki_car_meet_trigger"] = "okuibuki_car_meet", ["festival_car_meet_trigger"] = "festival_car_meet"
    };
    internal static IReadOnlyDictionary<ulong, Mapping> ByChallenge { get; } = Build();
    static Dictionary<ulong, Mapping> Build()
    {
        var result = new Dictionary<ulong, Mapping>();
        foreach (var c in CollectionJournalCatalog.Data.Challenges)
        {
            var paths = c.Objectives.SelectMany(o => o.Stats).ToArray();
            if (c.Objectives.Any(o => o.Kind != "SimpleStatObjective")) continue;
            var board = paths.Length == 1 ? Regex.Match(paths[0], @"\AFreeroam/Boards/Interacted\.GameplayId\.(DISCOUNT_BOARD_XP_[ABC]_\d{3}|MASCOTS_REGION_\d{1,2}_\d{3})\.Track\.\*\z") : Match.Empty;
            if (board.Success && c.Objectives.Length == 1 && c.Objectives[0].Target == 1)
            {
                string id = board.Groups[1].Value;
                string entity = id.ToLowerInvariant().Replace("mascots_region_", "mascot_region_", StringComparison.Ordinal);
                result.Add(c.Id, new(c.Id, id.StartsWith("MASCOTS_", StringComparison.Ordinal) ? "Mascot" : "Board", entity)); continue;
            }
            if (c.Objectives.All(o => o.Target == 1))
            {
                var region = paths.Select(s => Regex.Match(s, @"\ADiscovery/MapRegionDiscovered\.RegionId\.([a-z_]+)\.Track\.820\z")).Where(m => m.Success).ToArray();
                if (region.Length == 1 && paths.Length == 1) { result.Add(c.Id, new(c.Id, "Region", region[0].Groups[1].Value)); continue; }
                var landmark = paths.Select(s => Regex.Match(s, @"\ADiscovery/TimesEnteredTrigger\.TriggerZoneName\.([a-z0-9_]+)\.Track\.820\z")).Where(m => m.Success).ToArray();
                if (landmark.Length == 1)
                {
                    string trigger = landmark[0].Groups[1].Value, entity = Aliases.GetValueOrDefault(trigger, trigger);
                    if (Landmarks.Contains(entity) || entity is "daikoku_car_meet" or "okuibuki_car_meet" or "festival_car_meet")
                        result.Add(c.Id, new(c.Id, "Landmark", entity, Hash(entity)));
                    continue;
                }
            }
            if (c.Objectives.Length != 1 || paths.Length != 1 || c.Objectives[0].Target is < 1 or > 3) continue;
            var story = Regex.Match(paths[0], @"\AHorizonStories/StarsEarnedPerChapter\.StoryId\.([a-f0-9-]{36})\.ChapterId\.([a-f0-9-]{36})\.Track\.\*\z");
            if (story.Success && Guid.TryParseExact(story.Groups[1].Value, "D", out _) && Guid.TryParseExact(story.Groups[2].Value, "D", out _))
                result.Add(c.Id, new(c.Id, "Story", story.Groups[2].Value, Hash(story.Groups[2].Value), (int)c.Objectives[0].Target));
        }
        return result;
    }
}
#endif
