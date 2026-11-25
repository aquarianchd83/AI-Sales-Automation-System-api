using WhatsAppSalesAutomation.Api.Controllers;
using WhatsAppSalesAutomation.Api.Filters;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

public class SkipFeatureCheckTests
{
    [Fact]
    public void The_studio_status_probe_is_exempt_from_the_feature_check_so_it_can_answer_off()
    {
        var status = typeof(ContentStudioQuestionnaireController).GetMethod(nameof(ContentStudioQuestionnaireController.GetStatus))!;
        Assert.NotNull(Attribute.GetCustomAttribute(status, typeof(SkipFeatureCheckAttribute)));

        var other = typeof(ContentStudioQuestionnaireController).GetMethod(nameof(ContentStudioQuestionnaireController.GetQuestionnaire))!;
        Assert.Null(Attribute.GetCustomAttribute(other, typeof(SkipFeatureCheckAttribute)));
    }
}
