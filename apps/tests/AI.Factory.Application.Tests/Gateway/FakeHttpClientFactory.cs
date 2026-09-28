using System.Net.Http;

namespace AI.Factory.Application.Tests.Gateway;

public sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly Func<string, HttpClient> _factory;

    public FakeHttpClientFactory(HttpClient defaultClient)
    {
        _factory = _ => defaultClient;
    }

    public FakeHttpClientFactory(Func<string, HttpClient> factory)
    {
        _factory = factory;
    }

    public HttpClient CreateClient(string name) => _factory(name);
}
