namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    // Immutable migration input. Do not derive this from the mutable runtime
    // catalog: changing a future content catalog must not change migration 053's
    // checksum. These are the reviewed Tier III/IV IDs for all 62 branches.
    private const string ClassSuitAttributeEligibleItemIdsSql =
        "1034, 1035, 1434, 1435, 1734, 1735, 1834, 1835, " +
        "2033, 2034, 2043, 2044, 2133, 2134, 2143, 2144, " +
        "2153, 2154, 2163, 2164, 2233, 2234, 2243, 2244, " +
        "2253, 2254, 2263, 2264, 2333, 2334, 2343, 2344, " +
        "2433, 2434, 2443, 2444, 2533, 2534, 2543, 2544, " +
        "2553, 2554, 2563, 2564, 2633, 2634, 2643, 2644, " +
        "2653, 2654, 2663, 2664, 2733, 2734, 2743, 2744, " +
        "2753, 2754, 2763, 2764, 2833, 2834, 2843, 2844, " +
        "2853, 2854, 2863, 2864, 2933, 2934, 2943, 2944, " +
        "2953, 2954, 2963, 2964, 3033, 3034, 3043, 3044, " +
        "3053, 3054, 3063, 3064, 3133, 3134, 3143, 3144, " +
        "3153, 3154, 3163, 3164, 3232, 3235, 3236, 3237, " +
        "3242, 3245, 3246, 3247, 3252, 3255, 3256, 3257, " +
        "3262, 3265, 3266, 3267, 3333, 3334, 3343, 3344, " +
        "3353, 3354, 3363, 3364, 3433, 3434, 3443, 3444, " +
        "3453, 3454, 3463, 3464";
}
