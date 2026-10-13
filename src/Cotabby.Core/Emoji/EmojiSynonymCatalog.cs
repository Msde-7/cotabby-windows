namespace Cotabby.Core.Emoji;

/// <summary>
/// Curated intent and slang overlay for emoji search. Maps the words people type
/// (<c>lol</c>, <c>ty</c>, <c>congrats</c>) to the canonical gemoji aliases they mean.
/// Values must be real catalog aliases. A value that matches no entry is inert.
/// </summary>
public static class EmojiSynonymCatalog
{
    /// <summary>Lowercased query word to the canonical aliases it boosts.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Map = new Dictionary<string, string[]>
    {
        // Laughter and joy
        ["lol"] = ["joy", "rofl"],
        ["lmao"] = ["rofl", "joy"],
        ["lmfao"] = ["rofl", "joy"],
        ["haha"] = ["joy", "grin"],
        ["hahaha"] = ["joy", "rofl"],
        ["dying"] = ["joy", "skull"],
        ["dead"] = ["skull", "joy"],
        ["funny"] = ["joy", "rofl"],
        ["laugh"] = ["joy", "laughing"],
        ["happy"] = ["smile", "joy", "blush"],
        ["smiley"] = ["smiley", "smile"],

        // Affection
        ["love"] = ["heart", "heart_eyes", "kissing_heart"],
        ["luv"] = ["heart", "heart_eyes"],
        ["crush"] = ["heart_eyes", "smiling_face_with_three_hearts"],
        ["kiss"] = ["kissing_heart", "kiss"],
        ["hug"] = ["hugs"],
        ["hugs"] = ["hugs"],
        ["cute"] = ["smiling_face_with_three_hearts", "heart_eyes"],
        ["adore"] = ["heart_eyes"],

        // Sadness / pain
        ["sad"] = ["cry", "sob", "pensive"],
        ["crying"] = ["sob", "cry"],
        ["sob"] = ["sob"],
        ["depressed"] = ["pensive", "disappointed"],
        ["heartbroken"] = ["broken_heart"],
        ["pain"] = ["sob", "weary"],
        ["tired"] = ["tired_face", "weary"],
        ["exhausted"] = ["weary", "tired_face"],
        ["sleepy"] = ["sleeping", "yawning_face"],
        ["bored"] = ["yawning_face", "expressionless"],

        // Anger
        ["angry"] = ["rage", "angry"],
        ["mad"] = ["rage", "angry"],
        ["rage"] = ["rage"],
        ["annoyed"] = ["unamused", "expressionless"],
        ["ugh"] = ["unamused", "weary"],

        // Reactions / internet slang
        ["omg"] = ["scream", "astonished", "flushed"],
        ["omfg"] = ["scream", "astonished"],
        ["wtf"] = ["cursing_face", "rage"],
        ["smh"] = ["facepalm", "disappointed"],
        ["idk"] = ["shrug", "thinking"],
        ["idc"] = ["shrug"],
        ["meh"] = ["neutral_face", "expressionless"],
        ["oops"] = ["sweat_smile", "grimacing"],
        ["yikes"] = ["grimacing", "fearful"],
        ["cringe"] = ["grimacing", "weary"],
        ["sus"] = ["eyes", "raised_eyebrow"],
        ["shocked"] = ["astonished", "open_mouth"],
        ["surprised"] = ["open_mouth", "astonished"],
        ["confused"] = ["confused", "thinking"],
        ["thinking"] = ["thinking"],
        ["facepalm"] = ["facepalm"],
        ["shrug"] = ["shrug"],
        ["mindblown"] = ["exploding_head"],
        ["wow"] = ["astonished", "star_struck"],
        ["scared"] = ["fearful", "scream"],
        ["nervous"] = ["sweat_smile", "grimacing"],
        ["sick"] = ["nauseated_face", "mask"],
        ["ill"] = ["mask", "nauseated_face"],
        ["drunk"] = ["woozy_face"],
        ["crazy"] = ["zany_face"],
        ["cool"] = ["sunglasses"],
        ["nerd"] = ["nerd_face"],
        ["rich"] = ["money_mouth_face", "moneybag"],

        // Approval / gestures
        ["ty"] = ["pray"],
        ["thanks"] = ["pray", "clap"],
        ["thank"] = ["pray"],
        ["thx"] = ["pray"],
        ["please"] = ["pray"],
        ["pls"] = ["pray"],
        ["plz"] = ["pray"],
        ["yes"] = ["white_check_mark", "thumbsup"],
        ["yep"] = ["thumbsup"],
        ["no"] = ["x", "thumbsdown"],
        ["nope"] = ["thumbsdown"],
        ["ok"] = ["ok_hand", "white_check_mark"],
        ["okay"] = ["ok_hand"],
        ["like"] = ["thumbsup", "heart"],
        ["dislike"] = ["thumbsdown"],
        ["agree"] = ["thumbsup", "100"],
        ["disagree"] = ["thumbsdown"],
        ["perfect"] = ["ok_hand", "100"],
        ["nice"] = ["thumbsup", "ok_hand"],
        ["great"] = ["thumbsup", "100"],
        ["clap"] = ["clap"],
        ["applause"] = ["clap"],
        ["wave"] = ["wave"],
        ["hi"] = ["wave"],
        ["hey"] = ["wave"],
        ["hello"] = ["wave"],
        ["bye"] = ["wave"],
        ["goodbye"] = ["wave"],
        ["strong"] = ["muscle"],
        ["flex"] = ["muscle"],
        ["gym"] = ["muscle"],
        ["this"] = ["point_up", "100"],

        // Hype / celebration
        ["fire"] = ["fire"],
        ["lit"] = ["fire"],
        ["hot"] = ["fire", "hot_face"],
        ["100"] = ["100"],
        ["hundred"] = ["100"],
        ["party"] = ["tada", "partying_face"],
        ["celebrate"] = ["tada", "clinking_glasses"],
        ["celebration"] = ["tada"],
        ["congrats"] = ["tada", "clap"],
        ["congratulations"] = ["tada", "clap"],
        ["win"] = ["trophy", "tada"],
        ["winner"] = ["trophy", "1st_place_medal"],
        ["boom"] = ["boom"],
        ["explode"] = ["exploding_head", "boom"],
        ["magic"] = ["sparkles"],
        ["sparkle"] = ["sparkles"],
        ["shiny"] = ["sparkles", "gem"],

        // Common nouns
        ["money"] = ["moneybag", "money_mouth_face", "dollar"],
        ["cash"] = ["moneybag", "dollar"],
        ["idea"] = ["bulb"],
        ["smart"] = ["bulb", "nerd_face"],
        ["food"] = ["hamburger", "pizza"],
        ["hungry"] = ["drooling_face", "fork_and_knife"],
        ["eat"] = ["fork_and_knife", "hamburger"],
        ["drink"] = ["beer", "cocktail"],
        ["beer"] = ["beer", "beers"],
        ["wine"] = ["wine_glass"],
        ["coffee"] = ["coffee"],
        ["cake"] = ["cake", "birthday"],
        ["bday"] = ["birthday", "tada"],
        ["birthday"] = ["birthday", "tada"],
        ["gift"] = ["gift"],
        ["present"] = ["gift"],
        ["music"] = ["musical_note", "notes"],
        ["game"] = ["video_game", "game_die"],
        ["gaming"] = ["video_game"],
        ["work"] = ["briefcase", "computer"],
        ["code"] = ["computer", "keyboard"],
        ["bug"] = ["bug"],
        ["phone"] = ["iphone"],
        ["call"] = ["telephone"],
        ["email"] = ["email"],
        ["time"] = ["alarm_clock", "watch"],
        ["search"] = ["mag"],
        ["rocket"] = ["rocket"],
        ["launch"] = ["rocket"],
        ["shipit"] = ["rocket"],
        ["fly"] = ["airplane"],
        ["travel"] = ["airplane", "earth_americas"],
        ["home"] = ["house"],
        ["poop"] = ["poop"],
        ["ghost"] = ["ghost"],
        ["alien"] = ["alien"],
        ["robot"] = ["robot"],
        ["clown"] = ["clown_face"],
        ["skull"] = ["skull"],
        ["king"] = ["crown"],
        ["queen"] = ["crown"],
        ["trophy"] = ["trophy"],
        ["soccer"] = ["soccer"],
        ["sun"] = ["sunny"],
        ["rain"] = ["umbrella", "cloud_with_rain"],
        ["rainbow"] = ["rainbow"],
        ["snow"] = ["snowflake", "snowman"],
        ["star"] = ["star", "star2"],
        ["lightning"] = ["zap"],
        ["dog"] = ["dog"],
        ["puppy"] = ["dog"],
        ["cat"] = ["cat"],
        ["kitten"] = ["cat"],
    };

    /// <summary>
    /// Aliases to boost for a query. <c>Exact</c> holds the aliases of a key the query equals,
    /// <c>Prefix</c> the aliases of keys the query is a proper prefix of, minus anything in <c>Exact</c>.
    /// </summary>
    public static (HashSet<string> Exact, HashSet<string> Prefix) BoostedAliases(string rawQuery)
    {
        var query = rawQuery.ToLowerInvariant().Trim();
        var exact = new HashSet<string>();
        var prefix = new HashSet<string>();
        if (query.Length == 0) return (exact, prefix);

        if (Map.TryGetValue(query, out var direct)) exact.UnionWith(direct);

        // A single letter would pull in dozens of intent words.
        if (query.Length >= 2)
        {
            foreach (var (key, aliases) in Map)
            {
                if (key != query && key.StartsWith(query, StringComparison.Ordinal)) prefix.UnionWith(aliases);
            }
        }
        prefix.ExceptWith(exact);
        return (exact, prefix);
    }
}
