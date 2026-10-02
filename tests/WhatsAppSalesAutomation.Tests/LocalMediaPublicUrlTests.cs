using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Infrastructure.Storage;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

public class LocalMediaPublicUrlTests
{
    private static LocalFileMediaStorageService Storage(string baseUrl)
    {
        var root = Path.Combine(Path.GetTempPath(), "media-url-" + Guid.NewGuid().ToString("N"));
        var env = Fake.Of<IWebHostEnvironment>((_, _) => null);
        return new LocalFileMediaStorageService(env, Options.Create(new LocalMediaStorageSettings { RootPath = root, PublicBaseUrl = baseUrl }));
    }

    [Fact]
    public void Url_is_built_from_the_configured_base_url_and_ignores_a_trailing_slash()
    {
        var storage = Storage("https://api.example.com/");

        var url = storage.GetPublicUrl("2026/09/a.png");

        Assert.Equal("https://api.example.com/media/2026/09/a.png", url);
        Assert.True(storage.IsPublicUrl(url));
    }

    [Fact]
    public void Without_a_base_url_the_link_is_relative_and_not_public()
    {
        var storage = Storage("");

        var url = storage.GetPublicUrl("2026/09/a.png");

        Assert.Equal("/media/2026/09/a.png", url);
        Assert.False(storage.IsPublicUrl(url));
    }

    [Fact]
    public void A_localhost_base_url_is_not_public()
    {
        var storage = Storage("http://localhost:5000");

        Assert.False(storage.IsPublicUrl(storage.GetPublicUrl("a.png")));
    }
}
