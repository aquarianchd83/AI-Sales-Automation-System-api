using System.Text.Json;
using WhatsAppSalesAutomation.Application.Packages;
using WhatsAppSalesAutomation.Domain.Enums;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>What the Packages screen and the API say to each other over HTTP. The service tests never serialise,
/// which is how a request the screen sends every time (<c>"durationUnit":"Months"</c>) came to be refused with a 400.</summary>
public sealed class PackageJsonTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_save_request_with_the_unit_as_a_word_is_understood()
    {
        const string body = """{"name":"Gold","description":null,"price":5000,"durationValue":3,"durationUnit":"Months","features":["SEO"],"expectedSales":4,"isActive":true}""";

        var request = JsonSerializer.Deserialize<SavePackageRequest>(body, Web)!;

        Assert.Equal(PackageDurationUnit.Months, request.DurationUnit);
    }

    [Fact]
    public void A_numeric_unit_from_an_older_client_is_still_understood()
    {
        var request = JsonSerializer.Deserialize<SavePackageRequest>("""{"name":"Gold","price":1,"durationValue":1,"durationUnit":2,"expectedSales":0,"isActive":true}""", Web)!;

        Assert.Equal(PackageDurationUnit.Months, request.DurationUnit);
    }

    [Fact]
    public void A_package_is_returned_with_the_unit_as_a_word_for_the_screen()
    {
        var dto = new PackageDto(Guid.NewGuid(), "Gold", null, 5000m, 3, PackageDurationUnit.Years, Array.Empty<string>(), 4, 20000m, true, DateTime.UtcNow);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, Web));

        Assert.Equal("Years", json.RootElement.GetProperty("durationUnit").GetString());
    }
}
