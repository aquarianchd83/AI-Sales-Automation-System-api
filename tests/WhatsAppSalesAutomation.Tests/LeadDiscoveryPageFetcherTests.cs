using System.Net;
using WhatsAppSalesAutomation.Infrastructure.Ai;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

public class LeadDiscoveryPageFetcherTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")] // cloud metadata
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("::ffff:10.0.0.1")] // IPv4 mapped
    [InlineData("::ffff:169.254.169.254")]
    public void Private_and_internal_addresses_are_not_public(string address) =>
        Assert.False(LeadDiscoveryPageFetcher.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    public void Public_addresses_are_allowed(string address) =>
        Assert.True(LeadDiscoveryPageFetcher.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public void Html_to_text_keeps_tel_and_mailto_links_ahead_of_the_body_and_drops_scripts()
    {
        const string html = """
            <html><head><style>.a{color:red}</style><script>var phone="+1 000 000 0000";</script></head>
            <body><h1>Acme Dental</h1><p>Call us</p>
            <a href="tel:+91-98200-98200">Call</a> <a href='mailto:hello@acme.test?subject=Hi'>Mail</a>
            <address>12 Main Road<br>Pune</address></body></html>
            """;

        var text = LeadDiscoveryPageFetcher.HtmlToText(html);

        Assert.StartsWith("tel: +91-98200-98200", text);
        Assert.Contains("mailto: hello@acme.test", text);
        Assert.DoesNotContain("subject", text);
        Assert.DoesNotContain("+1 000 000 0000", text);
        Assert.DoesNotContain("color:red", text);
        Assert.Contains("Acme Dental", text);
        Assert.Contains("12 Main Road", text);
    }

    [Fact]
    public void Html_to_text_includes_json_ld_contact_details()
    {
        const string html = """<script type="application/ld+json">{"@type":"Dentist","telephone":"+91 22 5555 1212"}</script><p>Hi</p>""";

        Assert.Contains("+91 22 5555 1212", LeadDiscoveryPageFetcher.HtmlToText(html));
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/x")]
    [InlineData("not a url")]
    [InlineData("http://example.com:8080/")]
    public async Task Fetch_refuses_non_http_urls_and_unusual_ports(string url)
    {
        var fetcher = new LeadDiscoveryPageFetcher(new HttpClient(LeadDiscoveryPageFetcher.CreateHandler()));

        var result = await fetcher.FetchAsync(url, 1000, CancellationToken.None);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Fetch_never_connects_to_loopback()
    {
        var fetcher = new LeadDiscoveryPageFetcher(new HttpClient(LeadDiscoveryPageFetcher.CreateHandler()));

        var result = await fetcher.FetchAsync("http://127.0.0.1/", 1000, CancellationToken.None);

        Assert.False(result.Ok);
    }
}
