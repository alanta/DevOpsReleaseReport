using System.Net.Http.Json;
using ReleaseReport.Shared;

namespace BlazorApp.Client;

public class BackendService
{
    private readonly HttpClient _client;
    private readonly ILogger<BackendService> _logger;

    public BackendService(HttpClient client, ILogger<BackendService> logger)
    {
        _client = client;
        _logger = logger;

        _logger.LogInformation("API base url = {ApiBaseAddress}", _client.BaseAddress);
    }

    public async Task<Release[]> LoadReport()
    {
        return await _client.GetFromJsonAsync<Release[]>("/api/ReleaseReport") ?? Array.Empty<Release>();
    }

    public async Task<Release?> Refresh(int id)
    {
        return await _client.GetFromJsonAsync<Release>($"/api/Refresh/{id}");
    }
}