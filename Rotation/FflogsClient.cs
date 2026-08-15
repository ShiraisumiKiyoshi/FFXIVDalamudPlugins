using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;

namespace FreeAction.Rotation;

/// <summary>
/// FFLogs V1 API 客户端（REST，使用 API key）。
/// API key 获取：登录 fflogs 后在 https://www.fflogs.com/profile/ 或 https://cn.fflogs.com/profile/ 生成。
/// V1 API 比 V2（OAuth2 + GraphQL）简单得多：只需一个 API key，直接 REST 调用。
/// </summary>
public sealed class FflogsClient : IDisposable
{
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly bool _useCnDomain;

    // 调试统计：source.type 分布
    private readonly Dictionary<string, int> _typeStats = new();
    private readonly object _statsLock = new();

    private static readonly HttpClient _http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("FreeAction-Dalamud-Plugin/1.0");
        return c;
    }

    /// <param name="apiKey">V1 API key（在 fflogs 个人资料页生成）</param>
    /// <param name="useCnDomain">true = cn.fflogs.com, false = www.fflogs.com</param>
    public FflogsClient(string apiKey, bool useCnDomain = true)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("apiKey 不能为空", nameof(apiKey));
        _apiKey = apiKey.Trim();
        _useCnDomain = useCnDomain;
        _baseUrl = useCnDomain ? "https://cn.fflogs.com" : "https://www.fflogs.com";
    }

    /// <summary>调试用：不带 filter 直接拉一段原始 JSON。</summary>
    public async Task<string> DebugGetEventsRawAsync(string reportCode, long start, long end, bool withHostility = false)
    {
        string url = $"{_baseUrl}/v1/report/events/{Uri.EscapeDataString(reportCode)}" +
                     $"?start={start}&end={end}" +
                     (withHostility ? "&hostility=1" : "") +
                     $"&api_key={Uri.EscapeDataString(_apiKey)}";
        return await GetAsync(url).ConfigureAwait(false);
    }

    public void Dispose() { }

    /// <summary>简单 GET 请求并返回 JSON 文本。</summary>
    private async Task<string> GetAsync(string url)
    {
        using var resp = await _http.GetAsync(url).ConfigureAwait(false);
        var respText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string hint = resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "（401: API key 无效或未填写，请去 fflogs 个人资料页生成）"
                : "";
            throw new InvalidOperationException($"请求失败 ({(int)resp.StatusCode}) {hint}: {Truncate(respText, 300)}");
        }
        return respText;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";

    /// <summary>
    /// 获取报告中所有战斗列表。
    /// V1 端点：GET /v1/report/fights/{code}?api_key={key}
    /// </summary>
    public async Task<List<FflogsFight>> GetFightsAsync(string reportCode)
    {
        string url = $"{_baseUrl}/v1/report/fights/{Uri.EscapeDataString(reportCode)}?api_key={Uri.EscapeDataString(_apiKey)}";
        var text = await GetAsync(url).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        var result = new List<FflogsFight>();
        if (!root.TryGetProperty("fights", out var fightsArr) || fightsArr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var f in fightsArr.EnumerateArray())
        {
            long start = f.TryGetProperty("start_time", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
            long end = f.TryGetProperty("end_time", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;
            string name = f.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            int encounterId = f.TryGetProperty("encounterID", out var eid) && eid.ValueKind == JsonValueKind.Number ? eid.GetInt32() : 0;
            bool kill = f.TryGetProperty("kill", out var k) && k.ValueKind == JsonValueKind.True;
            int difficulty = f.TryGetProperty("difficulty", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0;
            int bossId = f.TryGetProperty("boss", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : 0;

            result.Add(new FflogsFight
            {
                Id = f.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt32() : 0,
                EncounterId = encounterId,
                Name = name,
                BossName = name,  // V1 直接用 name 作为 boss 名
                StartTime = start,
                EndTime = end,
                DurationMs = end - start,
                Kill = kill,
                Difficulty = difficulty,
            });
        }
        return result;
    }

    /// <summary>
    /// 获取指定战斗中 boss 敌方的施法事件（用于校正点）。
    /// V1 端点：GET /v1/report/events/{code}?start={start}&amp;end={end}&amp;api_key={key}&amp;hostility=1&amp;filter=type="cast"
    /// 用战斗的 start_time/end_time 作为时间窗口，hostility=1 筛选敌方事件，filter 进一步筛选 cast 类型。
    /// 自动翻页直到拉取完毕或达到上限。
    /// </summary>
    public async Task<List<FflogsCastEvent>> GetEnemyCastsAsync(string reportCode, int fightId, long fightStartMs, long fightEndMs)
    {
        // V1 API 的 source.type 值是 "Boss"/"NPC"/"Player"/"Pet" 等，不是 "enemy"
        // 所以 filter 只用 type="cast" 拉所有施法事件，然后在客户端过滤掉玩家
        string filter = "type=\"cast\"";
        string filterEnc = HttpUtility.UrlEncode(filter);

        var allCasts = new List<FflogsCastEvent>();
        long windowStart = fightStartMs;
        long windowEnd = fightEndMs > fightStartMs ? fightEndMs : (fightStartMs + 3_600_000); // 兜底：1 小时
        bool hasMore = true;
        int pageCount = 0;

        while (hasMore)
        {
            pageCount++;
            if (pageCount > 100) break; // 防御性：避免死循环

            string url = $"{_baseUrl}/v1/report/events/{Uri.EscapeDataString(reportCode)}" +
                         $"?start={windowStart}&end={windowEnd}" +
                         $"&api_key={Uri.EscapeDataString(_apiKey)}" +
                         $"&filter={filterEnc}";

            var text = await GetAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (!root.TryGetProperty("events", out var eventsArr) || eventsArr.ValueKind != JsonValueKind.Array)
                break;

            foreach (var ev in eventsArr.EnumerateArray())
            {
                long ts = ev.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0;
                string abilityName = "";
                int gameId = 0;
                if (ev.TryGetProperty("ability", out var ab) && ab.ValueKind == JsonValueKind.Object)
                {
                    if (ab.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String)
                        abilityName = an.GetString() ?? "";
                    // V1 用 guid 表示技能 ID（就是游戏内 ActionID）
                    if (ab.TryGetProperty("guid", out var g) && g.ValueKind == JsonValueKind.Number)
                        gameId = g.GetInt32();
                }
                // V1 API 的 source 信息分散在顶层字段：
                //   sourceID: int (引用 fights 接口的 friendlies/enemies 数组)
                //   sourceIsFriendly: bool (true=友方/玩家, false=敌方/boss)
                //   source.name (部分版本有嵌套对象，但经常为空)
                string srcName = "";
                string srcType = "";
                if (ev.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object)
                {
                    if (src.TryGetProperty("name", out var sn) && sn.ValueKind == JsonValueKind.String)
                        srcName = sn.GetString() ?? "";
                    if (src.TryGetProperty("type", out var st) && st.ValueKind == JsonValueKind.String)
                        srcType = st.GetString() ?? "";
                }
                // 关键字段：sourceIsFriendly (V1 顶层)
                bool? sourceIsFriendly = null;
                if (ev.TryGetProperty("sourceIsFriendly", out var sif))
                {
                    if (sif.ValueKind == JsonValueKind.True) sourceIsFriendly = true;
                    else if (sif.ValueKind == JsonValueKind.False) sourceIsFriendly = false;
                }

                // 调试统计：sourceIsFriendly 分布
                string statKey = sourceIsFriendly switch
                {
                    true => "friendly=true",
                    false => "friendly=false",
                    _ => "friendly=(missing)",
                };
                lock (_statsLock)
                {
                    _typeStats[statKey] = _typeStats.TryGetValue(statKey, out var v) ? v + 1 : 1;
                }

                // 客户端过滤：只保留敌方（sourceIsFriendly = false）的施法事件
                if (sourceIsFriendly == true)
                    continue;

                allCasts.Add(new FflogsCastEvent
                {
                    Timestamp = ts,
                    RelativeMs = ts - fightStartMs,
                    AbilityName = abilityName,
                    GameId = gameId,
                    SourceName = srcName,
                    SourceType = srcType,
                });
            }

            // 翻页：V1 用 nextPageTimestamp 字段
            if (root.TryGetProperty("nextPageTimestamp", out var npt) && npt.ValueKind == JsonValueKind.Number)
            {
                long nx = npt.GetInt64();
                if (nx <= windowStart) { hasMore = false; break; }
                windowStart = nx;
            }
            else
            {
                hasMore = false;
            }

            if (allCasts.Count > 5000) { hasMore = false; }
        }

        // 调试：打印所有事件的 source.type 分布到 chat
        if (DebugStatsCallback != null)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("[FreeAction] 调试 source.type 分布（所有事件，含玩家）: ");
            lock (_statsLock)
            {
                foreach (var kv in _typeStats)
                    sb.Append($"{kv.Key}={kv.Value}, ");
            }
            DebugStatsCallback(sb.ToString());
        }

        return allCasts;
    }

    /// <summary>调试回调：用于打印 source.type 分布统计。null 时不调用。</summary>
    public Action<string>? DebugStatsCallback { get; set; }
}

/// <summary>fflogs 战斗信息。</summary>
public class FflogsFight
{
    public int Id { get; set; }
    public int EncounterId { get; set; }
    public string Name { get; set; } = "";
    public string BossName { get; set; } = "";
    public long StartTime { get; set; }
    public long EndTime { get; set; }
    public long DurationMs { get; set; }
    public bool Kill { get; set; }
    public int Difficulty { get; set; }

    public string DisplayLabel
    {
        get
        {
            string dur = TimelineEntry.FormatTime(DurationMs);
            string killStr = Kill ? "击杀" : "未击杀";
            return $"#{Id} {BossName} ({killStr}) {dur}";
        }
    }
}

/// <summary>fflogs 施法事件。</summary>
public class FflogsCastEvent
{
    public long Timestamp { get; set; }
    public long RelativeMs { get; set; }
    public string AbilityName { get; set; } = "";
    public int GameId { get; set; }
    public string SourceName { get; set; } = "";
    public string SourceType { get; set; } = "";  // Boss / NPC / Pet / Player 等

    public string DisplayLabel
    {
        get
        {
            string time = TimelineEntry.FormatTime(RelativeMs);
            string idHex = GameId > 0 ? $"0x{GameId:X}" : "-";
            string src = string.IsNullOrEmpty(SourceType) ? SourceName : $"{SourceName}({SourceType})";
            return $"[{time}] {AbilityName} ({idHex})  by {src}";
        }
    }
}
