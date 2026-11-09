using System.Text.Json.Serialization;

namespace Deathmatch
{
    // Data sent to and received from OCB Inside (see Functions/InsideStats.cs).
    public class InsideStatBuffer
    {
        [JsonPropertyName("steam_id64")] public string SteamID64 { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("kills")] public int Kills { get; set; }
        [JsonPropertyName("deaths")] public int Deaths { get; set; }
        [JsonPropertyName("headshots")] public int Headshots { get; set; }
        [JsonPropertyName("knife_kills")] public int KnifeKills { get; set; }
        [JsonPropertyName("assists")] public int Assists { get; set; }
        [JsonPropertyName("damage")] public int Damage { get; set; }
        [JsonPropertyName("hits")] public int Hits { get; set; }
        [JsonPropertyName("active_seconds")] public int ActiveSeconds { get; set; }
        [JsonPropertyName("best_streak")] public int BestStreak { get; set; }
        // Streak count at each kill, for the streak bonus.
        [JsonPropertyName("kill_streaks")] public List<int> KillStreaks { get; set; } = new();

        [JsonIgnore]
        public bool IsEmpty => Kills == 0 && Deaths == 0 && Assists == 0 && Damage == 0 && ActiveSeconds == 0;
    }

    public class InsideBatch
    {
        [JsonPropertyName("batch_id")] public string BatchId { get; set; } = Guid.NewGuid().ToString();
        [JsonPropertyName("game")] public string Game { get; set; } = "deathmatch";
        [JsonPropertyName("map")] public string Map { get; set; } = "";
        [JsonPropertyName("mode")] public string Mode { get; set; } = "";
        [JsonPropertyName("players")] public List<InsideStatBuffer> Players { get; set; } = new();
    }

    public class InsidePlayerStatus
    {
        [JsonPropertyName("steam_id64")] public string SteamID64 { get; set; } = "";
        [JsonPropertyName("xp")] public long XP { get; set; }
        [JsonPropertyName("gained")] public long Gained { get; set; }
        [JsonPropertyName("rank_name")] public string RankName { get; set; } = "";
        [JsonPropertyName("rank_color")] public string RankColor { get; set; } = "";
        [JsonPropertyName("next_rank_name")] public string? NextRankName { get; set; }
        [JsonPropertyName("next_xp")] public long NextXP { get; set; }
        [JsonPropertyName("ranked_up")] public bool RankedUp { get; set; }
        [JsonPropertyName("new_milestones")] public List<string> NewMilestones { get; set; } = new();
    }

    public class InsideStatsResponse
    {
        [JsonPropertyName("players")] public List<InsidePlayerStatus> Players { get; set; } = new();
    }

    public class InsideRankResponse
    {
        [JsonPropertyName("player")] public InsidePlayerStatus? Player { get; set; }
        [JsonPropertyName("position")] public int? Position { get; set; }
        [JsonPropertyName("kills")] public long Kills { get; set; }
        [JsonPropertyName("deaths")] public long Deaths { get; set; }
        [JsonPropertyName("headshots")] public long Headshots { get; set; }
        [JsonPropertyName("active_seconds")] public long ActiveSeconds { get; set; }
    }

    public class InsideTopEntry
    {
        [JsonPropertyName("position")] public int Position { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("xp")] public long XP { get; set; }
        [JsonPropertyName("rank_name")] public string RankName { get; set; } = "";
    }

    public class InsideTopResponse
    {
        [JsonPropertyName("players")] public List<InsideTopEntry> Players { get; set; } = new();
    }

    // Per slot, for the AFK check.
    public class InsideActivity
    {
        public float X, Y, Z;
        public bool HasPosition;
        public float LastActive;
    }
}
