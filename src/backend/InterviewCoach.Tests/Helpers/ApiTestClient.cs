using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;

namespace InterviewCoach.Tests.Helpers;

public static class ApiTestClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Guid CreateDeterministicGuid(int value)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(value).CopyTo(bytes, 0);
        BitConverter.GetBytes(value * 397).CopyTo(bytes, 4);
        BitConverter.GetBytes(value * 101).CopyTo(bytes, 8);
        BitConverter.GetBytes(value * 17).CopyTo(bytes, 12);
        return new Guid(bytes);
    }

    public static async Task<JsonElement> PostJsonAndReadAsync(HttpClient client, string url, object payload)
    {
        var response = await client.PostAsJsonAsync(url, payload, JsonOptions);
        response.IsSuccessStatusCode.Should().BeTrue($"POST {url} should succeed but was {(int)response.StatusCode}");

        var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.Clone();
    }

    public static async Task<JsonElement> GetJsonAndReadAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.IsSuccessStatusCode.Should().BeTrue($"GET {url} should succeed but was {(int)response.StatusCode}");

        var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.Clone();
    }

    public static async Task<string> LoginAndSetBearerAsync(
        HttpClient client,
        string email,
        string password,
        string? displayName = null)
    {
        await RegisterIfMissingAsync(client, email, password, displayName);

        var loginPayload = new
        {
            email,
            password
        };

        var response = await client.PostAsJsonAsync("/api/auth/login", loginPayload, JsonOptions);
        response.IsSuccessStatusCode.Should().BeTrue($"POST /api/auth/login should succeed but was {(int)response.StatusCode}");

        var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var token = body.RootElement.GetProperty("token").GetString();
        token.Should().NotBeNullOrWhiteSpace();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return token!;
    }

    public static async Task RegisterIfMissingAsync(
        HttpClient client,
        string email,
        string password,
        string? displayName = null)
    {
        var registerPayload = new
        {
            email,
            password,
            displayName
        };

        var response = await client.PostAsJsonAsync("/api/auth/register", registerPayload, JsonOptions);
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return;
        }

        response.IsSuccessStatusCode.Should().BeTrue($"POST /api/auth/register should succeed or conflict but was {(int)response.StatusCode}");
    }
}
