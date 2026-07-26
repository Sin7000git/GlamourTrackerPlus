using System.Net.Http.Headers;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace GlamourTracker.Services.FashionReport;

internal sealed class FashionReportClient : IDisposable
{
    private const string BaseUrl = "https://fashionreportxiv.com";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient httpClient;
    private readonly IPluginLog log;

    public FashionReportClient(IPluginLog log)
    {
        this.log = log;
        httpClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(25),
            BaseAddress = new Uri(BaseUrl),
        };
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GlamourTrackerPlus", "0.6"));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<FashionReportStateDto?> GetReportStateAsync(CancellationToken ct)
    {
        return await GetJsonAsync<FashionReportStateDto>("/api/report-state", ct).ConfigureAwait(false);
    }

    public async Task<FashionReportHintItemsDto?> GetHintItemsAsync(string hint, string slot, CancellationToken ct)
    {
        var path = $"/api/hint?hint={Uri.EscapeDataString(hint)}&slot={Uri.EscapeDataString(slot)}";
        return await GetJsonAsync<FashionReportHintItemsDto>(path, ct).ConfigureAwait(false);
    }

    public async Task<FashionReportItemDetailDto?> GetItemAsync(string name, CancellationToken ct)
    {
        var path = $"/api/item?name={Uri.EscapeDataString(name)}";
        return await GetJsonAsync<FashionReportItemDetailDto>(path, ct).ConfigureAwait(false);
    }

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await httpClient.GetAsync(path, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var snippet = body.Length > 200 ? body[..200] : body;
                PluginFileLog.Error(
                    "fashion.http",
                    $"HTTP {(int)response.StatusCode} for {path}: {snippet}");
                this.log.Warning($"Fashion Report request failed ({(int)response.StatusCode}): {path}");
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                var snippet = body.Length > 200 ? body[..200] : body;
                PluginFileLog.Error("fashion.http", $"JSON parse failed for {path}: {ex.Message} body={snippet}", ex);
                this.log.Warning(ex, $"Fashion Report JSON parse failed: {path}");
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PluginFileLog.Error("fashion.http", $"Request failed for {path}", ex);
            this.log.Warning(ex, $"Fashion Report request failed: {path}");
            return null;
        }
    }

    public void Dispose() => httpClient.Dispose();
}
