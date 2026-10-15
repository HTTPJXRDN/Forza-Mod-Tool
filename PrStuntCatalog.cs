#if PROFILE_UNLOCK_TESTING
namespace FH6LocalCryptoTool;

// Installed ObjectModelGame medal scores joined through EntityModel's exact
// scribe_data GUIDs, 2026-10-15. 111 base-world stunts / 333 Journal star rows.
// Native save units: metres, metres/second, drift points, seconds remaining.
// Seasonal stunts, leaderboard submissions and reward grants are not included.
internal static class PrStuntCatalog
{
    internal sealed record Stunt(string Entity, string Activity, string SaveType, float Bronze, float Silver, float Gold)
    {
        internal string Key => Entity + "_save_state";
        internal float Threshold(int stars) => stars == 3 ? Gold : stars == 2 ? Silver : Bronze;
    }
    internal static IReadOnlyList<Stunt> Items { get; } = Build().AsReadOnly();
    internal static IReadOnlyDictionary<string, Stunt> ByEntity { get; } = Items.ToDictionary(s => s.Entity, StringComparer.Ordinal);

    static List<Stunt> Build()
    {
        var result = new List<Stunt>();
        void Add(string prefix, string activity, string type, float[,] scores, int[]? ids = null)
        {
            for (int i = 0; i < scores.GetLength(0); i++)
                result.Add(new($"{prefix}_{(ids is null ? i + 1 : ids[i]):00}", activity, type, scores[i, 0], scores[i, 1], scores[i, 2]));
        }
        Add("danger_sign", "DangerSign", "DangerSignState", new float[,]
        {
            {152.4f,243.84f,335.28f}, {91.44f,182.88f,274.32f}, {365.76f,457.2f,548.64f}, {304.8f,396.24f,487.68f},
            {121.92f,213.36f,304.8f}, {167.64f,259.08f,350.52f}, {182.88f,274.32f,365.76f}, {76.2f,167.64f,259.08f},
            {152.4f,243.84f,335.28f}, {152.4f,243.84f,335.28f}, {60.96f,152.4f,243.84f}, {91.44f,182.88f,274.32f},
            {182.88f,274.32f,365.76f}, {45.72f,137.16f,228.6f}, {396.24f,518.16f,640.08f}, {30.48f,121.92f,213.36f},
            {152.4f,243.84f,335.28f}, {213.36f,304.8f,396.24f}, {274.32f,396.24f,518.16f}, {243.84f,335.28f,426.72f}
        });
        Add("drift_zone", "DriftZone", "DriftZoneState", new float[,]
        {
            {50000,75000,100000}, {50000,75000,100000}, {65000,100000,130000}, {80000,120000,160000},
            {70000,105000,140000}, {80000,120000,160000}, {45000,70000,95000}, {65000,100000,130000},
            {60000,90000,120000}, {70000,115000,155000}, {70000,105000,140000}, {50000,75000,100000},
            {70000,105000,135000}, {80000,120000,160000}, {80000,120000,160000}, {25000,35000,45000},
            {60000,95000,125000}, {50000,80000,105000}, {60000,95000,125000}, {140000,240000,340000}
        });
        Add("speed_trap", "SpeedTrap", "CameraState", new float[,]
        {
            {42.46759f,64.81895f,84.93518f}, {37.99732f,58.11354f,75.99464f}, {20.11623f,31.29191f,40.23245f},
            {33.52705f,51.40813f,67.05409f}, {33.52705f,51.40813f,67.05409f}, {24.5865f,37.99732f,51.40813f},
            {26.82164f,40.23245f,53.64327f}, {29.05677f,44.70273f,58.11354f}, {42.46759f,62.58382f,82.70004f},
            {40.23245f,60.34868f,80.4649f}, {46.93786f,71.52436f,93.87572f}, {51.40813f,78.22977f,102.8163f},
            {44.70273f,67.05409f,89.40546f}, {35.76218f,53.64327f,71.52436f}, {42.46759f,64.81895f,84.93518f},
            {49.173f,73.7595f,98.346f}, {26.82164f,40.23245f,53.64327f}, {26.82164f,37.99732f,46.93786f},
            {35.76218f,53.64327f,71.52436f}, {37.99732f,58.11354f,75.99464f}, {33.52705f,51.40813f,67.05409f},
            {46.93786f,71.52436f,96.11086f}, {46.93786f,71.52436f,93.87572f}, {51.40813f,78.22977f,102.8163f},
            {35.76218f,53.64327f,71.52436f}, {44.70273f,67.05409f,87.17032f}, {35.76218f,53.64327f,71.52436f},
            {31.29191f,46.93786f,62.58382f}, {31.29191f,46.93786f,62.58382f}, {42.46759f,64.81895f,87.17032f}
        });
        Add("speed_zone", "SpeedZone", "CameraState", new float[,]
        {
            {24.5865f,37.99732f,49.173f}, {17.88109f,26.82164f,35.76218f}, {24.5865f,37.99732f,51.40813f},
            {20.11623f,31.29191f,40.23245f}, {29.05677f,44.70273f,58.11354f}, {37.99732f,55.87841f,73.7595f},
            {20.11623f,31.29191f,42.46759f}, {20.11623f,29.0576f,37.9984f}, {42.46759f,62.58382f,82.70004f},
            {22.35136f,33.52705f,44.70273f}, {20.11623f,31.29191f,42.46759f}, {24.5865f,37.99732f,51.40813f},
            {33.52705f,44.70273f,53.64327f}, {26.82164f,40.23245f,53.64327f}, {22.35136f,33.52705f,42.46759f},
            {44.70273f,67.05409f,89.40546f}, {26.82164f,40.23245f,53.64327f}, {13.41082f,26.82164f,40.23245f},
            {24.5865f,35.76218f,46.93786f}, {24.5865f,37.99732f,51.40813f}, {26.82164f,40.23245f,53.64327f},
            {26.82164f,42.46759f,55.87841f}, {29.05677f,44.70273f,58.11354f}, {42.46759f,64.81895f,84.93518f},
            {20.11623f,29.0576f,37.9984f}, {29.05677f,44.70273f,58.11354f}, {22.35136f,33.52705f,42.46759f},
            {44.70273f,67.05409f,89.40546f}, {46.93786f,69.28923f,91.64059f}, {24.5865f,37.99732f,49.173f}
        });
        Add("trailblazer", "Trailblazer", "TrailblazerState", new float[,]
        {
            {150,250,350}, {5,10,15}, {35,40,45}, {37,42,47}, {35,40,45}, {17,22,27},
            {37,42,47}, {28,33,38}, {20,30,40}, {30,35,40}, {33,38,43}
        }, new[] {2,3,4,9,11,19,20,23,27,30,34});
        return result;
    }
}
#endif
