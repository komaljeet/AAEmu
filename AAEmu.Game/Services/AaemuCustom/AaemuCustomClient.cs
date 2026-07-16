using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using AAEmu.Game.Models;

using NLog;

namespace AAEmu.Game.Services.AaemuCustom;

/// <summary>
/// Best-effort HTTP client for the aaemu-custom Rust sidecar.
/// Every call is safe to make when the sidecar is down or disabled: failures
/// are logged and a sensible default is returned so gameplay is never blocked.
/// </summary>
public sealed class AaemuCustomClient
{
    private static readonly Lazy<AaemuCustomClient> _instance = new(() => new AaemuCustomClient());
    public static AaemuCustomClient Instance => _instance.Value;

    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly HttpClient _http = new() { Timeout = System.TimeSpan.FromSeconds(5) };

    public bool Enabled => AppConfiguration.Instance.AaemuCustom?.Enabled ?? false;

    public string BaseUrl => (AppConfiguration.Instance.AaemuCustom?.BaseUrl ?? "http://127.0.0.1:1281").TrimEnd('/');

    // --- low-level helpers -------------------------------------------------

    private async Task<JsonDocument?> SendAsync(HttpMethod method, string path, object? body)
    {
        if (!Enabled)
            return null;
        try
        {
            using var req = new HttpRequestMessage(method, BaseUrl + path);
            if (body != null)
            {
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Warn($"aaemu-custom {method} {path} -> {(int)resp.StatusCode}");
                return null;
            }
            return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"aaemu-custom {method} {path} failed: {ex.Message}");
            return null;
        }
    }

    private Task<JsonDocument?> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);

    private Task<JsonDocument?> PostAsync(string path, object? body) => SendAsync(HttpMethod.Post, path, body);

    // field extractors
    private static long GetLong(JsonElement el, string name, long dflt = -1) =>
        el.TryGetProperty(name, out var v) && v.TryGetInt64(out var i) ? i : dflt;

    private static int GetInt(JsonElement el, string name, int dflt = -1) =>
        el.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : dflt;

    private static double GetDouble(JsonElement el, string name, double dflt = -1) =>
        el.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : dflt;

    private static float GetSingle(JsonElement el, string name, float dflt = -1f) =>
        el.TryGetProperty(name, out var v) && v.TryGetSingle(out var f) ? f : dflt;

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // --- world_bank --------------------------------------------------------

    public async Task<bool> HourlyIntegrityCheckAsync()
    {
        var doc = await PostAsync("/world-bank/integrity", null).ConfigureAwait(false);
        return doc != null;
    }

    public async Task<string?> GetEconomyHealthAsync()
    {
        var doc = await GetAsync("/world-bank/health").ConfigureAwait(false);
        return doc?.RootElement.ValueKind == JsonValueKind.Object ? GetString(doc.RootElement, "health") : null;
    }

    public async Task<long> RunDailyTaxAsync()
    {
        var doc = await PostAsync("/world-bank/tax/run", null).ConfigureAwait(false);
        return doc != null ? GetLong(doc.RootElement, "collected", 0) : 0;
    }

    public async Task<bool> MintGoldAsync(long accountId, long? characterId, long amount)
    {
        var doc = await PostAsync("/world-bank/mint", new { account_id = accountId, character_id = characterId, amount }).ConfigureAwait(false);
        return doc != null;
    }

    public async Task<bool> LogTransactionAsync(long accountId, long? characterId, string txType, long amount)
    {
        var doc = await PostAsync("/world-bank/log", new { account_id = accountId, character_id = characterId, tx_type = txType, amount }).ConfigureAwait(false);
        return doc != null;
    }

    public async Task<bool> FlagRmtSuspectAsync(long accountId)
    {
        var doc = await PostAsync($"/world-bank/rmt/{accountId}", null).ConfigureAwait(false);
        return doc != null && GetInt(doc.RootElement, "flagged", 0) != 0;
    }

    // --- labor -------------------------------------------------------------

    public async Task<int> TickLaborAsync(long accountId)
    {
        var doc = await PostAsync($"/labor/tick/{accountId}", null).ConfigureAwait(false);
        return doc != null ? GetInt(doc.RootElement, "pool", -1) : -1;
    }

    public async Task<int> SpendLaborAsync(long accountId, int amount)
    {
        var doc = await PostAsync("/labor/spend", new { account_id = accountId, amount }).ConfigureAwait(false);
        return doc != null ? GetInt(doc.RootElement, "pool", -1) : -1;
    }

    /// <summary>
    /// Notify-only labor spend: advances the sidecar's <c>total_labor_spent</c> (which
    /// drives the gold multiplier) without requiring the sidecar's own pool to cover it.
    /// Safe to fire-and-forget from <c>AccountManager.UpdateLabor</c>. Returns true if the
    /// sidecar acknowledged the notification.
    /// </summary>
    public async Task<bool> RecordLaborSpentAsync(long accountId, int amount)
    {
        var doc = await PostAsync("/labor/spent", new { account_id = accountId, amount }).ConfigureAwait(false);
        return doc != null;
    }

    public async Task<int> GetLaborAsync(long accountId)
    {
        var doc = await GetAsync($"/labor/{accountId}").ConfigureAwait(false);
        return doc != null ? GetInt(doc.RootElement, "pool", -1) : -1;
    }

    // --- gold_scaling ------------------------------------------------------

    public async Task<double> GetMultiplierAsync(long accountId)
    {
        var doc = await GetAsync($"/gold/multiplier/{accountId}").ConfigureAwait(false);
        return doc != null ? GetDouble(doc.RootElement, "multiplier", -1) : -1;
    }

    public async Task<long> CalculateFishGoldAsync(long accountId)
    {
        var doc = await GetAsync($"/gold/fish/{accountId}").ConfigureAwait(false);
        return doc != null ? GetLong(doc.RootElement, "gold", -1) : -1;
    }

    public async Task<(long gold, long gilda)> CalculateTradepackRewardAsync(long accountId)
    {
        var doc = await GetAsync($"/gold/tradepack/{accountId}").ConfigureAwait(false);
        if (doc == null)
            return (-1, -1);
        return (GetLong(doc.RootElement, "gold", -1), GetLong(doc.RootElement, "gilda", -1));
    }

    public async Task<long> CalculateCoinpurseGoldAsync(long accountId, long baseGold)
    {
        var doc = await PostAsync("/gold/coinpurse", new { account_id = accountId, base_gold = baseGold }).ConfigureAwait(false);
        return doc != null ? GetLong(doc.RootElement, "gold", -1) : -1;
    }

    // --- starter_perks -----------------------------------------------------

    public async Task<int> GrantStarterPerksAsync(long characterId, long accountId)
    {
        var doc = await PostAsync("/perks/grant", new { character_id = characterId, account_id = accountId }).ConfigureAwait(false);
        return doc != null ? GetInt(doc.RootElement, "granted", 0) : 0;
    }

    public async Task<bool> GrantFlightCapabilityAsync(long mountId)
    {
        var doc = await PostAsync($"/perks/flight/{mountId}", null).ConfigureAwait(false);
        return doc != null && GetInt(doc.RootElement, "updated", 0) != 0;
    }

    // --- boss_respawn ------------------------------------------------------

    public async Task<List<BossMemberLoot>> OnBossKilledAsync(long bossId, long raidId, List<(long CharacterId, long AccountId)> members)
    {
        var body = new
        {
            boss_id = bossId,
            raid_id = raidId,
            members = members.Select(m => new { character_id = m.CharacterId, account_id = m.AccountId }).ToList(),
        };
        var doc = await PostAsync("/boss/kill", body).ConfigureAwait(false);
        var loot = new List<BossMemberLoot>();
        if (doc?.RootElement.TryGetProperty("loot", out var arr) == true && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                loot.Add(new BossMemberLoot
                {
                    CharacterId = GetLong(item, "character_id", 0),
                    Gold = GetLong(item, "gold", 0),
                    Thunderstruck = item.TryGetProperty("thunderstruck", out var ts) && ts.ValueKind == JsonValueKind.True,
                });
            }
        }

        return loot;
    }

    public async Task<List<long>> GetBossesReadyToSpawnAsync()
    {
        var doc = await GetAsync("/boss/ready").ConfigureAwait(false);
        var list = new List<long>();
        if (doc?.RootElement.TryGetProperty("bosses", out var arr) == true && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.TryGetInt64(out var id))
                    list.Add(id);
            }
        }
        return list;
    }

    // --- honor -------------------------------------------------------------

    public async Task<long> GrantEventHonorAsync(long accountId, long baseHonor)
    {
        var doc = await PostAsync("/honor/event", new { account_id = accountId, base_honor = baseHonor }).ConfigureAwait(false);
        // -1 = sidecar down/disabled; the caller falls back to native HonorRate scaling.
        // A real grant of 0 (base_honor == 0) is distinct from this failure sentinel.
        return doc != null ? GetLong(doc.RootElement, "honor", -1) : -1;
    }

    public async Task<long> UseSkillPointTomeAsync(long accountId, long characterId)
    {
        var doc = await PostAsync("/honor/tome", new { account_id = accountId, character_id = characterId }).ConfigureAwait(false);
        return doc != null ? GetLong(doc.RootElement, "skill_points", -1) : -1;
    }

    public async Task<long> GetHonorShopPriceAsync(long itemId)
    {
        var doc = await GetAsync($"/honor/price/{itemId}").ConfigureAwait(false);
        return doc != null ? GetLong(doc.RootElement, "price", -1) : -1;
    }

    // --- combat_normalization ----------------------------------------------

    public async Task<long> CalculateDamageAsync(long attackerId, long baseSkillDamage)
    {
        var doc = await PostAsync("/combat/damage", new { attacker_id = attackerId, base_skill_damage = baseSkillDamage }).ConfigureAwait(false);
        return doc != null ? GetLong(doc.RootElement, "damage", -1) : -1;
    }

    public async Task<long> CalculateDamageTakenAsync(long defenderId, long incomingDamage)
    {
        var doc = await PostAsync("/combat/damage-taken", new { defender_id = defenderId, incoming_damage = incomingDamage }).ConfigureAwait(false);
        return doc != null ? GetLong(doc.RootElement, "damage_taken", -1) : -1;
    }

    public async Task<(long ap, long dp)> GetCombatStatsAsync(long characterId)
    {
        var doc = await GetAsync($"/combat/stats/{characterId}").ConfigureAwait(false);
        if (doc == null)
            return (-1, -1);
        return (GetLong(doc.RootElement, "attack_power", -1), GetLong(doc.RootElement, "defense_power", -1));
    }

    // --- vehicle / mount ---------------------------------------------------

    public async Task<float> GetVehicleSpeedAsync(long vehicleId, float buffs = 0f)
    {
        var doc = await GetAsync($"/vehicle/speed/{vehicleId}?buffs={buffs.ToString(System.Globalization.CultureInfo.InvariantCulture)}").ConfigureAwait(false);
        return doc != null ? GetSingle(doc.RootElement, "speed", -1f) : -1f;
    }

    public async Task<float> GetMountSpeedAsync(long mountId, float buffs = 0f)
    {
        var doc = await GetAsync($"/mount/speed/{mountId}?buffs={buffs.ToString(System.Globalization.CultureInfo.InvariantCulture)}").ConfigureAwait(false);
        return doc != null ? GetSingle(doc.RootElement, "speed", -1f) : -1f;
    }
}