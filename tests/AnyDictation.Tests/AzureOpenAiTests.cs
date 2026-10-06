using System.Net;
using System.Text;
using Xunit;

namespace AnyDictation.Tests;

public class AzureOpenAiTests
{
    const string Key = "AZURE-TEST-KEY";
    static TranscriptionRequest Request(string key = Key, string? endpoint = null)
    {
        var profile = TestProfiles.Create(ProviderKind.AzureOpenAi);
        if (endpoint != null) profile.Endpoint = endpoint;
        return new(profile, key, WavEncoder.Encode(new byte[640], 16000, 16, 1));
    }

    static FakeHandler Handler(string body = "{\"text\":\"こんにちは\"}", HttpStatusCode status = HttpStatusCode.OK) => new()
    {
        Respond = _ => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") },
    };

    [Theory]
    [InlineData("https://example.openai.azure.com")]
    [InlineData("https://example.openai.azure.com/")]
    public async Task AzureDeploymentApiUsesApiKeyAndDeploymentName(string endpoint)
    {
        var handler = Handler();
        using var http = new HttpClient(handler);
        var client = TranscriptionClients.Create(ProviderKind.AzureOpenAi, http);
        Assert.Equal("こんにちは", await client.TranscribeAsync(Request(endpoint: endpoint), default));
        var (req, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://example.openai.azure.com/openai/deployments/gpt-transcribe/audio/transcriptions?api-version=2025-03-01-preview", req.RequestUri!.AbsoluteUri);
        Assert.Equal(Key, Assert.Single(req.Headers.GetValues("api-key")));
        Assert.Null(req.Headers.Authorization);
        Assert.False(req.Headers.Contains("Ocp-Apim-Subscription-Key"));
        Assert.Empty(http.DefaultRequestHeaders);
        Assert.Contains("name=file", body);
        Assert.Contains("filename=audio.wav", body);
        Assert.Contains("Content-Type: audio/wav", body);
        Assert.Contains("RIFF", body);
        Assert.Contains("name=model", body);
        Assert.Contains("gpt-transcribe", body);
        Assert.Contains("name=language", body);
        Assert.Contains("\r\n\r\nja\r\n", body);
        Assert.Contains("name=response_format", body);
        Assert.Contains("\r\n\r\njson\r\n", body);
        Assert.DoesNotContain(Key, body);
    }

    [Theory]
    [InlineData("https://example.openai.azure.com/")]
    [InlineData("http://localhost:8000/")]
    public async Task AzureAlwaysRequiresKey(string endpoint)
    {
        var handler = Handler();
        using var http = new HttpClient(handler);
        var client = TranscriptionClients.Create(ProviderKind.AzureOpenAi, http);
        var error = await Assert.ThrowsAsync<TranscriptionException>(() => client.TranscribeAsync(Request("", endpoint), default));
        Assert.Equal(TranscriptionErrorKind.Configuration, error.Kind);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task SharedHttpClientDoesNotLeakAuthenticationBetweenProviders()
    {
        var handler = Handler();
        using var http = new HttpClient(handler);
        var azure = TranscriptionClients.Create(ProviderKind.AzureOpenAi, http);
        var compatible = TranscriptionClients.Create(ProviderKind.OpenAiCompatible, http);
        await azure.TranscribeAsync(Request(), default);
        await compatible.TranscribeAsync(new(TestProfiles.Create(ProviderKind.OpenAiCompatible), "BEARER-KEY", Request().Wav), default);
        await azure.TranscribeAsync(Request("SECOND-AZURE-KEY"), default);
        Assert.False(handler.Calls[1].Request.Headers.Contains("api-key"));
        Assert.Equal("BEARER-KEY", handler.Calls[1].Request.Headers.Authorization!.Parameter);
        Assert.Equal("SECOND-AZURE-KEY", Assert.Single(handler.Calls[2].Request.Headers.GetValues("api-key")));
        Assert.Null(handler.Calls[2].Request.Headers.Authorization);
        Assert.Empty(http.DefaultRequestHeaders);
    }

    [Theory]
    [InlineData(401, TranscriptionErrorKind.Auth)]
    [InlineData(429, TranscriptionErrorKind.RateLimit)]
    [InlineData(302, TranscriptionErrorKind.Redirect)]
    public async Task AzureMapsErrorsWithoutExposingKey(int status, TranscriptionErrorKind expected)
    {
        var handler = Handler($"{{\"error\":{{\"message\":\"Invalid {Key}\"}}}}", (HttpStatusCode)status);
        using var http = new HttpClient(handler);
        var client = TranscriptionClients.Create(ProviderKind.AzureOpenAi, http);
        var error = await Assert.ThrowsAsync<TranscriptionException>(() => client.TranscribeAsync(Request(), default));
        Assert.Equal(expected, error.Kind);
        Assert.DoesNotContain(Key, error.Message);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task DeploymentNameIsEscapedAsSinglePathSegment()
    {
        var handler = Handler();
        using var http = new HttpClient(handler);
        var client = TranscriptionClients.Create(ProviderKind.AzureOpenAi, http);
        var request = Request();
        request.Profile.Model = "custom/model?#";
        await client.TranscribeAsync(request, default);
        Assert.Equal("https://example.openai.azure.com/openai/deployments/custom%2Fmodel%3F%23/audio/transcriptions?api-version=2025-03-01-preview", Assert.Single(handler.Calls).Request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public void AzureDefaultIsValidJapaneseProfile()
    {
        var p = Profile.CreateDefault(ProviderKind.AzureOpenAi);
        Assert.Equal("ja", p.Language);
        Assert.Equal("gpt-transcribe", p.Model);
        Assert.Equal("", p.Endpoint);
        Assert.Equal(ProviderKind.AzureOpenAi, p.Provider);
        Assert.Single(ProfileValidator.Validate(p));
        p.Endpoint = "https://example.openai.azure.com/";
        Assert.Empty(ProfileValidator.Validate(p));
        Assert.Equal(0, (int)ProviderKind.AzureMai);
        Assert.Equal(1, (int)ProviderKind.OpenAiCompatible);
    }
}