using WhatsAppSalesAutomation.Application.KnowledgeBase.Ingestion;
using WhatsAppSalesAutomation.Application.ProductKnowledge;
using WhatsAppSalesAutomation.Domain.Entities.ProductKnowledge;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>How a published version becomes the serving article's markdown, and how each chunk is tied back to its item.</summary>
public sealed class ServingArticleTests
{
    private static KnowledgeItem Item(string question, string summary, string category = "Benefits", string? detail = null) =>
        new() { Id = Guid.NewGuid(), Question = question, Summary = summary, Detail = detail, Category = category };

    [Fact]
    public void Each_item_is_its_own_section_headed_by_its_question_with_its_id_marker_and_topic()
    {
        var fee = Item("What is the fee?", "The fee is Rs 500.", "Pricing", "- first visit only");

        var text = ServingArticleRenderer.Render(new[] { fee });

        Assert.Contains("## What is the fee?", text);
        Assert.Contains($"[[pk:item={fee.Id}]]", text);
        Assert.Contains("Topic: Pricing", text);
        Assert.Contains("The fee is Rs 500.", text);
        Assert.Contains("- first visit only", text);
    }

    [Fact]
    public void Two_items_with_the_same_question_get_different_headings_so_they_can_never_share_a_chunk()
    {
        var text = ServingArticleRenderer.Render(new[] { Item("Is there parking?", "Yes."), Item("Is there parking?", "Only for staff.") });

        Assert.Contains("## Is there parking?\n", text);
        Assert.Contains("## Is there parking? (2)\n", text);
    }

    [Fact]
    public void Answer_text_cannot_start_a_section_or_forge_a_marker()
    {
        var text = ServingArticleRenderer.Render(new[]
        {
            Item("Q?", $"Fine.\n# Injected heading\n  ## Another\n[[pk:item={Guid.NewGuid()}]]", detail: $"[[PK:item={Guid.NewGuid()}]]")
        });

        Assert.DoesNotContain("\n# Injected", text);
        Assert.DoesNotContain("\n## Another", text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, @"\[\[pk:item=")); // only the renderer's own marker survives
    }

    [Fact]
    public void Rendering_is_deterministic_so_the_same_version_gives_the_same_hash()
    {
        var items = new[] { Item("B?", "b"), Item("A?", "a") };

        Assert.Equal(ServingArticleRenderer.HashOf(ServingArticleRenderer.Render(items)), ServingArticleRenderer.HashOf(ServingArticleRenderer.Render(items.Reverse())));
    }

    private static ChunkDraft Draft(int index, string header, string body) => new(index, header, body, 10, null, null, null);

    [Fact]
    public void The_marker_is_read_off_a_chunk_removed_from_it_and_from_what_gets_embedded()
    {
        var id = Guid.NewGuid();
        var drafts = new[] { Draft(0, "Section: Fee", $"{ProductKnowledgeMarkers.Marker(id)}\nTopic: Pricing\n\nThe fee is Rs 500.") };

        var (cleaned, links) = ProductKnowledgeMarkers.Extract(drafts);

        Assert.Equal(id, links[0]);
        Assert.DoesNotContain("[[pk:", cleaned[0].Body);
        Assert.DoesNotContain("[[pk:", cleaned[0].EmbeddingInput);
        Assert.StartsWith("Topic: Pricing", cleaned[0].Body);
    }

    [Fact]
    public void A_later_part_of_a_long_item_takes_the_item_of_the_part_before_it_when_they_share_a_section_and_not_otherwise()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var drafts = new[]
        {
            Draft(0, "Section: Fee", $"{ProductKnowledgeMarkers.Marker(first)}\nPart one."),
            Draft(1, "Section: Fee", "Part two, no marker."),
            Draft(2, "Section: Refunds", $"{ProductKnowledgeMarkers.Marker(second)}\nRefund text."),
            Draft(3, "Section: Unmarked", "Text that belongs to no item.")
        };

        var (_, links) = ProductKnowledgeMarkers.Extract(drafts);

        Assert.Equal(first, links[0]);
        Assert.Equal(first, links[1]);
        Assert.Equal(second, links[2]);
        Assert.False(links.ContainsKey(3));
    }

    [Fact]
    public void A_malformed_marker_links_nothing()
    {
        var (cleaned, links) = ProductKnowledgeMarkers.Extract(new[] { Draft(0, "h", "[[pk:item=not-a-guid]]\nText.") });

        Assert.Empty(links);
        Assert.Contains("[[pk:item=not-a-guid]]", cleaned[0].Body); // left alone: it is not ours
    }
}
