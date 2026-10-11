namespace MockGraph.ContractTests;

[Collection(ContractCollection.Name)]
public sealed class ErrorContractTests(ContractTarget target)
{
    [SkippableFact]
    public async Task MissingBearerTokenIsUnauthorizedWithAGraphErrorBody()
    {
        var (status, body) = await target.RawGetAsync("/users?$top=1", authenticated: false);
        Assert.Equal(401, status);
        var error = body!["error"]!;
        Assert.Equal("InvalidAuthenticationToken", error["code"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(error["message"]!.GetValue<string>()));
        // Loosened: real Graph also adds client-request-id; only request-id is required here.
        Assert.False(string.IsNullOrEmpty(error["innerError"]!["request-id"]!.GetValue<string>()));
    }

    [SkippableFact]
    public async Task UnknownUserErrorBodyHasInnerErrorRequestId()
    {
        var (status, body) = await target.RawGetAsync($"/users/{Guid.NewGuid()}");
        Assert.Equal(404, status);
        Assert.Equal("Request_ResourceNotFound", body!["error"]!["code"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(body["error"]!["innerError"]!["request-id"]!.GetValue<string>()));
    }
}