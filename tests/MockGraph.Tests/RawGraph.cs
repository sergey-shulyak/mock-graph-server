using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace MockGraph.Tests;

public sealed record RawResponse(int Status, JsonNode? Body, HttpResponseHeaders Headers);

/// <summary>Wire-level requests with a bearer token and optional headers, for assertions the SDK models hide.</summary>
public static class RawGraph
{
    public static async Task<RawResponse> Get(HttpClient http, string token, string url, params (string Name, string Value)[] headers) =>
        await Send(http, HttpMethod.Get, token, url, null, headers);

    public static async Task<RawResponse> Post(HttpClient http, string token, string url, object body, params (string Name, string Value)[] headers) =>
        await Send(http, HttpMethod.Post, token, url, body, headers);

    private static async Task<RawResponse> Send(
        HttpClient http, HttpMethod method, string? token, string url, object? body, (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(method, url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return new RawResponse((int)response.StatusCode, string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text), response.Headers);
    }
}