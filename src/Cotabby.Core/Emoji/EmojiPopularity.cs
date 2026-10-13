namespace Cotabby.Core.Emoji;

/// <summary>
/// Curated popularity prior keyed by canonical gemoji alias, most used first. The matcher uses the
/// rank as a late tiebreak and the bare colon panel uses the head of the list as starter suggestions.
/// Aliases missing from the active catalog never rank or resolve.
/// </summary>
public static class EmojiPopularity
{
    /// <summary>Ranked aliases, most popular first. The index is the rank.</summary>
    public static readonly IReadOnlyList<string> Ordered =
    [
        // Core reactions
        "joy", "heart", "sob", "pray", "thumbsup", "fire", "ok_hand", "tada", "eyes", "heart_eyes",
        "smile", "smirk", "grin", "sweat_smile", "rofl", "blush", "clap", "raised_hands", "wave", "100",
        "thinking", "cry", "wink", "sunglasses", "sparkles", "rocket", "skull", "pensive", "weary", "muscle",
        "thumbsdown", "facepalm", "shrug", "see_no_evil", "smiley", "laughing", "kissing_heart", "yum", "smiling_imp",
        // Faces
        "slightly_smiling_face", "upside_down_face", "relaxed", "relieved", "neutral_face", "expressionless",
        "unamused", "roll_eyes", "flushed", "pleading_face", "disappointed", "tired_face", "sleepy",
        "yawning_face", "scream", "fearful", "cold_sweat", "disappointed_relieved", "sweat", "hushed",
        "astonished", "dizzy_face", "open_mouth", "grimacing", "confused", "worried", "frowning_face",
        "persevere", "confounded", "triumph", "angry", "rage", "innocent", "nerd_face", "partying_face",
        "woozy_face", "zany_face", "hugs", "shushing_face", "lying_face", "raised_eyebrow", "star_struck",
        "stuck_out_tongue", "stuck_out_tongue_winking_eye", "drooling_face", "sleeping", "mask", "hot_face",
        "cold_face", "sneezing_face", "nauseated_face", "money_mouth_face", "cowboy_hat_face", "smiling_face_with_tear",
        // Hands and people
        "point_up", "point_down", "point_left", "point_right", "v", "crossed_fingers", "fist", "facepunch",
        "handshake", "writing_hand", "nail_care", "open_hands", "raised_hand", "vulcan_salute", "call_me_hand",
        "metal", "middle_finger", "ok_woman", "raising_hand", "tipping_hand_person",
        // Hearts and symbols
        "orange_heart", "yellow_heart", "green_heart", "blue_heart", "purple_heart", "black_heart", "white_heart",
        "broken_heart", "two_hearts", "revolving_hearts", "heartbeat", "heartpulse", "sparkling_heart", "cupid",
        "gift_heart", "anger", "boom", "dizzy", "sweat_drops", "dash", "star", "star2", "zzz", "exclamation",
        "question", "bangbang", "white_check_mark", "heavy_check_mark", "x", "negative_squared_cross_mark",
        "warning", "no_entry", "recycle", "droplet", "zap", "snowflake", "rainbow",
        // Celebration, food, drink
        "confetti_ball", "balloon", "gift", "birthday", "cake", "champagne", "clinking_glasses", "beers", "beer",
        "wine_glass", "cocktail", "coffee", "pizza", "hamburger", "fries", "hotdog", "taco", "sushi", "ramen",
        "doughnut", "cookie", "ice_cream", "lollipop", "candy", "chocolate_bar", "popcorn", "apple", "banana",
        "watermelon", "strawberry", "cherries", "peach", "eggplant", "hot_pepper", "avocado", "bread",
        // Animals and nature
        "dog", "cat", "mouse", "rabbit", "fox_face", "bear", "panda_face", "koala", "tiger", "lion", "cow", "pig",
        "frog", "monkey", "monkey_face", "hear_no_evil", "speak_no_evil", "chicken", "penguin", "bird",
        "baby_chick", "duck", "owl", "wolf", "horse", "unicorn", "bee", "bug", "butterfly", "snail", "turtle",
        "snake", "octopus", "whale", "dolphin", "fish", "shark", "elephant", "giraffe", "hedgehog", "sloth",
        "sheep", "deer", "peacock", "parrot", "flamingo", "seedling", "herb", "four_leaf_clover", "evergreen_tree",
        "palm_tree", "cactus", "christmas_tree", "maple_leaf", "fallen_leaf", "leaves", "mushroom", "sunflower",
        "rose", "tulip", "cherry_blossom", "bouquet", "sunny", "cloud", "ocean", "full_moon", "crescent_moon",
        "earth_americas",
        // Objects, travel, tech
        "poop", "ghost", "alien", "robot", "clown_face", "jack_o_lantern", "gem", "crown", "ring", "lipstick",
        "tophat", "mortar_board", "dress", "shirt", "trophy", "medal_sports", "soccer", "basketball", "football",
        "baseball", "tennis", "volleyball", "8ball", "dart", "bowling", "video_game", "game_die", "musical_note",
        "notes", "microphone", "headphones", "guitar", "clapper", "art", "bulb", "flashlight", "computer",
        "keyboard", "iphone", "camera", "movie_camera", "tv", "telephone", "email", "envelope", "package",
        "memo", "pencil2", "paperclip", "scissors", "pushpin", "round_pushpin", "calendar", "bar_chart",
        "chart_with_upwards_trend", "clipboard", "books", "book", "newspaper", "mag", "lock", "unlock", "key",
        "hammer", "wrench", "gear", "link", "moneybag", "dollar", "credit_card", "bell", "loudspeaker", "mega",
        "speech_balloon", "thought_balloon", "airplane", "car", "taxi", "bus", "train", "bike", "ship",
        "rotating_light", "house", "office", "hospital", "school", "mountain", "beach_umbrella"
    ];

    /// <summary>Rank of an alias outside the curated list. Sorts after every ranked alias.</summary>
    public const int NotRanked = int.MaxValue;

    private static readonly Dictionary<string, int> RankByAlias = BuildRanks();

    private static Dictionary<string, int> BuildRanks()
    {
        var map = new Dictionary<string, int>(Ordered.Count);
        for (int i = 0; i < Ordered.Count; i++) map.TryAdd(Ordered[i], i);
        return map;
    }

    /// <summary>The popularity rank of an alias. Lower is more popular.</summary>
    public static int Rank(string alias) =>
        RankByAlias.TryGetValue(alias.ToLowerInvariant(), out var rank) ? rank : NotRanked;

    /// <summary>Aliases that seed the bare colon panel for a user with no history, in popularity order.</summary>
    public static IReadOnlyList<string> StarterAliases(int limit) => Ordered.Take(Math.Max(0, limit)).ToArray();
}
