using System.Net.Http.Json;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace Deathmatch
{
    // OCB Inside ranks: count raw values per player, send them in batches to
    // Inside ({Url}/cs2/stats) and show the rank Inside answers with. All
    // counting happens on the game thread; only the HTTP calls run in the
    // background, their results go back with Server.NextFrame.
    public partial class Deathmatch
    {
        // By SteamID64, so a map change or a reconnect loses nothing.
        private readonly Dictionary<string, InsideStatBuffer> insideBuffers = new();
        private readonly Dictionary<int, InsideActivity> insideActivity = new();
        // Last known clan tag per SteamID64, set again after a respawn.
        private readonly Dictionary<string, string> insideClanTags = new();
        private readonly LinkedList<InsideBatch> insidePending = new();
        private readonly object insidePendingLock = new();
        private readonly SemaphoreSlim insideSendLock = new(1, 1);
        private static readonly HttpClient insideHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
        private CounterStrikeSharp.API.Modules.Timers.Timer? insideTickTimer;
        private CounterStrikeSharp.API.Modules.Timers.Timer? insideFlushTimer;

        // Minimum movement per second (units) that counts as active.
        private const float InsideMoveThreshold = 8f;

        private bool InsideEnabled => Config.InsideRanks.Enabled && !string.IsNullOrWhiteSpace(Config.InsideRanks.ServerKey);

        private string InsideUrl(string path) => Config.InsideRanks.Url.TrimEnd('/') + "/cs2/" + path;

        private string InsidePendingFile => Path.Combine(ModuleDirectory, "inside-pending.json");

        public void InsideLoad()
        {
            if (!InsideEnabled)
                return;
            InsideLoadPending();
            insideTickTimer = AddTimer(1.0f, InsideTick, TimerFlags.REPEAT);
            insideFlushTimer = AddTimer(Math.Max(15, Config.InsideRanks.FlushSeconds), () => InsideFlush(), TimerFlags.REPEAT);
            foreach (var cmd in Config.InsideRanks.RankCommands.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                AddCommand(cmd, "Shows your OCB rank", (player, _) => InsideRankCommand(player));
            foreach (var cmd in Config.InsideRanks.TopCommands.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                AddCommand(cmd, "Shows the OCB top 10", (player, _) => InsideTopCommand(player));
            _ = InsideSendPending();
        }

        public void InsideUnload()
        {
            if (!InsideEnabled)
                return;
            insideTickTimer?.Kill();
            insideFlushTimer?.Kill();
            InsideFlush(send: false);
            InsideSavePending();
        }

        private static bool InsideIsHuman(CCSPlayerController? p) =>
            p != null && p.IsValid && !p.IsBot && !p.IsHLTV && p.SteamID.ToString().Length == 17;

        private InsideStatBuffer InsideBuffer(CCSPlayerController p)
        {
            var id = p.SteamID.ToString();
            if (!insideBuffers.TryGetValue(id, out var b))
            {
                b = new InsideStatBuffer { SteamID64 = id };
                insideBuffers[id] = b;
            }
            b.Name = p.PlayerName;
            return b;
        }

        private void InsideMarkActive(CCSPlayerController p)
        {
            if (insideActivity.TryGetValue(p.Slot, out var a))
                a.LastActive = Server.CurrentTime;
        }

        // Once per second: active time for every living player who moved or
        // dealt damage within the AFK window.
        private void InsideTick()
        {
            var now = Server.CurrentTime;
            foreach (var p in Utilities.GetPlayers())
            {
                if (!InsideIsHuman(p))
                    continue;
                if (!insideActivity.TryGetValue(p.Slot, out var a))
                {
                    a = new InsideActivity { LastActive = now };
                    insideActivity[p.Slot] = a;
                }
                var pawn = p.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || !p.PawnIsAlive || pawn.AbsOrigin == null)
                {
                    a.HasPosition = false;
                    continue;
                }
                var o = pawn.AbsOrigin;
                if (a.HasPosition)
                {
                    float dx = o.X - a.X, dy = o.Y - a.Y, dz = o.Z - a.Z;
                    if (dx * dx + dy * dy + dz * dz >= InsideMoveThreshold * InsideMoveThreshold)
                        a.LastActive = now;
                }
                a.X = o.X; a.Y = o.Y; a.Z = o.Z; a.HasPosition = true;
                if (now - a.LastActive <= Config.InsideRanks.AfkSeconds)
                    InsideBuffer(p).ActiveSeconds++;
            }
        }

        // Called from OnPlayerDeath after the attacker's streak was raised.
        public void InsideOnDeath(CCSPlayerController victim, CCSPlayerController? attacker, CCSPlayerController? assister, bool headshot, bool knife, int attackerStreak)
        {
            if (!InsideEnabled)
                return;
            // Only deaths by another player (or bot) count, not suicides or team switches.
            bool byPlayer = attacker != null && attacker.IsValid && attacker != victim;
            if (InsideIsHuman(victim) && byPlayer)
                InsideBuffer(victim).Deaths++;
            if (InsideIsHuman(attacker) && attacker != victim)
            {
                var b = InsideBuffer(attacker!);
                b.Kills++;
                if (headshot) b.Headshots++;
                if (knife) b.KnifeKills++;
                b.BestStreak = Math.Max(b.BestStreak, attackerStreak);
                if (b.KillStreaks.Count < 500)
                    b.KillStreaks.Add(attackerStreak);
                InsideMarkActive(attacker!);
            }
            if (InsideIsHuman(assister) && assister != attacker && assister != victim)
                InsideBuffer(assister!).Assists++;
        }

        // Called from OnPlayerHurt.
        public void InsideOnHurt(CCSPlayerController victim, CCSPlayerController? attacker, int damage)
        {
            if (!InsideEnabled || !InsideIsHuman(attacker) || attacker == victim || damage <= 0)
                return;
            var b = InsideBuffer(attacker!);
            b.Damage += Math.Min(damage, 500);
            b.Hits++;
            InsideMarkActive(attacker!);
        }

        public void InsideOnConnect(CCSPlayerController p)
        {
            if (!InsideEnabled || !InsideIsHuman(p))
                return;
            insideActivity[p.Slot] = new InsideActivity { LastActive = Server.CurrentTime };
            var steamId = p.SteamID.ToString();
            _ = InsideFetchRank(steamId, status =>
            {
                if (status?.Player != null)
                    InsideApplyClanTag(steamId, status.Player);
            });
        }

        public void InsideOnDisconnect(CCSPlayerController p)
        {
            if (!InsideEnabled)
                return;
            insideActivity.Remove(p.Slot);
            insideClanTags.Remove(p.SteamID.ToString());
            InsideFlush();
        }

        public void InsideOnSpawn(CCSPlayerController p)
        {
            if (!InsideEnabled || !Config.InsideRanks.ShowClanTag || !InsideIsHuman(p))
                return;
            if (insideClanTags.TryGetValue(p.SteamID.ToString(), out var tag))
                InsideSetClan(p, tag);
        }

        // Moves the counted values into a batch and sends everything pending.
        public void InsideFlush(bool send = true)
        {
            if (!InsideEnabled)
                return;
            var players = insideBuffers.Values.Where(b => !b.IsEmpty).ToList();
            insideBuffers.Clear();
            if (players.Count > 0)
            {
                var batch = new InsideBatch { Map = Server.MapName, Mode = ActiveMode?.Name ?? "", Players = players };
                lock (insidePendingLock)
                {
                    insidePending.AddLast(batch);
                    while (insidePending.Count > Math.Max(1, Config.InsideRanks.MaxPendingBatches))
                        insidePending.RemoveFirst();
                }
            }
            if (send)
                _ = InsideSendPending();
        }

        private HttpRequestMessage InsideRequest(HttpMethod method, string url)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Add("X-Server-Key", Config.InsideRanks.ServerKey);
            return req;
        }

        // Sends the pending batches in order; stops at the first failure and
        // keeps the rest (the backend ignores a batch it already has).
        private async Task InsideSendPending()
        {
            if (!await insideSendLock.WaitAsync(0))
                return;
            try
            {
                while (true)
                {
                    InsideBatch? batch;
                    lock (insidePendingLock)
                        batch = insidePending.First?.Value;
                    if (batch == null)
                        return;
                    using var req = InsideRequest(HttpMethod.Post, InsideUrl("stats"));
                    req.Content = JsonContent.Create(batch);
                    HttpResponseMessage resp;
                    try
                    {
                        resp = await insideHttp.SendAsync(req);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"[Deathmatch] Inside not reachable, {insidePending.Count} batch(es) pending: {ex.Message}");
                        return;
                    }
                    using (resp)
                    {
                        if ((int)resp.StatusCode >= 500)
                        {
                            Logger.LogWarning($"[Deathmatch] Inside answered {(int)resp.StatusCode}, retrying later");
                            return;
                        }
                        lock (insidePendingLock)
                            insidePending.Remove(batch);
                        if (!resp.IsSuccessStatusCode)
                        {
                            // 400/401: retrying the same batch won't help.
                            Logger.LogError($"[Deathmatch] Inside rejected a batch ({(int)resp.StatusCode}), check Url and Server Key");
                            continue;
                        }
                        var body = await resp.Content.ReadFromJsonAsync<InsideStatsResponse>();
                        if (body != null)
                            Server.NextFrame(() => InsideApplyStatuses(body.Players));
                    }
                }
            }
            finally
            {
                insideSendLock.Release();
            }
        }

        private CCSPlayerController? InsideOnlinePlayer(string steamId) =>
            Utilities.GetPlayers().FirstOrDefault(p => InsideIsHuman(p) && p.SteamID.ToString() == steamId);

        private void InsideApplyStatuses(List<InsidePlayerStatus> statuses)
        {
            foreach (var st in statuses)
            {
                InsideApplyClanTag(st.SteamID64, st);
                var p = InsideOnlinePlayer(st.SteamID64);
                if (p == null || !Config.InsideRanks.AnnounceRankUp)
                    continue;
                if (st.RankedUp)
                    Server.PrintToChatAll(Localizer["Chat.Prefix"] + " " + Localizer["Chat.Inside.RankUp", p.PlayerName, st.RankName]);
                foreach (var m in st.NewMilestones)
                    p.PrintToChat(Localizer["Chat.Prefix"] + " " + Localizer["Chat.Inside.Milestone", m]);
            }
        }

        private void InsideApplyClanTag(string steamId, InsidePlayerStatus st)
        {
            if (!Config.InsideRanks.ShowClanTag || string.IsNullOrEmpty(st.RankName))
                return;
            var tag = "[" + st.RankName + "]";
            insideClanTags[steamId] = tag;
            var p = InsideOnlinePlayer(steamId);
            if (p != null)
                InsideSetClan(p, tag);
        }

        private static void InsideSetClan(CCSPlayerController p, string tag)
        {
            if (p.Clan == tag)
                return;
            p.Clan = tag;
            Utilities.SetStateChanged(p, "CCSPlayerController", "m_szClan");
        }

        private async Task InsideFetchRank(string steamId, Action<InsideRankResponse?> onGameThread)
        {
            try
            {
                using var req = InsideRequest(HttpMethod.Get, InsideUrl("rank") + "?steam_id64=" + Uri.EscapeDataString(steamId));
                using var resp = await insideHttp.SendAsync(req);
                var body = resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<InsideRankResponse>() : null;
                Server.NextFrame(() => onGameThread(body));
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Deathmatch] Inside rank lookup failed: {ex.Message}");
                Server.NextFrame(() => onGameThread(null));
            }
        }

        private void InsideRankCommand(CCSPlayerController? player)
        {
            if (!InsideEnabled || !InsideIsHuman(player))
                return;
            var steamId = player!.SteamID.ToString();
            _ = InsideFetchRank(steamId, r =>
            {
                var p = InsideOnlinePlayer(steamId);
                if (p == null)
                    return;
                if (r?.Player == null)
                {
                    p.PrintToChat(Localizer["Chat.Prefix"] + " " + Localizer["Chat.Inside.Offline"]);
                    return;
                }
                var st = r.Player;
                p.PrintToChat(Localizer["Chat.Prefix"] + " " + Localizer["Chat.Inside.Rank", st.RankName, st.XP, r.Position?.ToString() ?? "-"]);
                if (!string.IsNullOrEmpty(st.NextRankName))
                    p.PrintToChat(Localizer["Chat.Prefix"] + " " + Localizer["Chat.Inside.NextRank", st.NextRankName, Math.Max(0, st.NextXP - st.XP)]);
            });
        }

        private void InsideTopCommand(CCSPlayerController? player)
        {
            if (!InsideEnabled || !InsideIsHuman(player))
                return;
            var steamId = player!.SteamID.ToString();
            _ = Task.Run(async () =>
            {
                InsideTopResponse? body = null;
                try
                {
                    using var req = InsideRequest(HttpMethod.Get, InsideUrl("top") + "?limit=10");
                    using var resp = await insideHttp.SendAsync(req);
                    if (resp.IsSuccessStatusCode)
                        body = await resp.Content.ReadFromJsonAsync<InsideTopResponse>();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[Deathmatch] Inside top lookup failed: {ex.Message}");
                }
                Server.NextFrame(() =>
                {
                    var p = InsideOnlinePlayer(steamId);
                    if (p == null)
                        return;
                    if (body == null)
                    {
                        p.PrintToChat(Localizer["Chat.Prefix"] + " " + Localizer["Chat.Inside.Offline"]);
                        return;
                    }
                    p.PrintToChat(Localizer["Chat.Prefix"] + " " + Localizer["Chat.Inside.TopHeader"]);
                    foreach (var e in body.Players)
                        p.PrintToChat(Localizer["Chat.Inside.TopLine", e.Position, e.Name, e.RankName, e.XP]);
                });
            });
        }

        private void InsideLoadPending()
        {
            try
            {
                if (!File.Exists(InsidePendingFile))
                    return;
                var list = JsonSerializer.Deserialize<List<InsideBatch>>(File.ReadAllText(InsidePendingFile));
                File.Delete(InsidePendingFile);
                if (list == null)
                    return;
                lock (insidePendingLock)
                    foreach (var b in list)
                        insidePending.AddLast(b);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Deathmatch] Could not read pending Inside batches: {ex.Message}");
            }
        }

        private void InsideSavePending()
        {
            try
            {
                List<InsideBatch> list;
                lock (insidePendingLock)
                    list = insidePending.ToList();
                if (list.Count == 0)
                {
                    if (File.Exists(InsidePendingFile))
                        File.Delete(InsidePendingFile);
                    return;
                }
                File.WriteAllText(InsidePendingFile, JsonSerializer.Serialize(list));
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Deathmatch] Could not save pending Inside batches: {ex.Message}");
            }
        }
    }
}
