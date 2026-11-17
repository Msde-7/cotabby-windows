using Cotabby.Core.Models;

namespace Cotabby.Core.Tests.Models;

public sealed class OnboardingTemplateRecommenderTests
{
    private const ulong GiB = 1UL << 30;

    [Theory]
    [InlineData(OnboardingTemplate.Quick, "tabby-2-nano", 4, 7, false)]
    [InlineData(OnboardingTemplate.Everyday, "tabby-2-base", 7, 12, true)]
    [InlineData(OnboardingTemplate.Powerful, "tabby-2-pro", 12, 20, true)]
    public void Plan_MatchesUpstreamTiers(OnboardingTemplate template, string modelId, int low, int high, bool clipboard)
    {
        var plan = OnboardingTemplateRecommender.Plan(template);

        Assert.Equal(modelId, plan.Model.Id);
        Assert.Equal((low, high), (plan.LowWords, plan.HighWords));
        Assert.False(plan.AllowMultiLine);
        Assert.Equal(clipboard, plan.ClipboardContext);
    }

    [Theory]
    [InlineData(4, OnboardingTemplate.Quick)]
    [InlineData(7.9, OnboardingTemplate.Quick)]
    [InlineData(8, OnboardingTemplate.Everyday)]
    [InlineData(32, OnboardingTemplate.Everyday)]
    public void Recommended_KeepsLowMemoryPcsOnQuick(double gigabytes, OnboardingTemplate expected) =>
        Assert.Equal(expected, OnboardingTemplateRecommender.Recommended((ulong)(gigabytes * GiB)));

    [Fact]
    public void Availability_DisablesPowerfulBelowEightGigabytes()
    {
        var powerful = OnboardingTemplateRecommender.Availability(OnboardingTemplate.Powerful, 6 * GiB);

        Assert.True(powerful.IsDisabled);
        Assert.False(powerful.IsRecommended);
        Assert.StartsWith("Needs more memory", powerful.Warning);
    }

    [Fact]
    public void Availability_WarnsAboutPowerfulBelowSixteenGigabytes()
    {
        var powerful = OnboardingTemplateRecommender.Availability(OnboardingTemplate.Powerful, 8 * GiB);

        Assert.False(powerful.IsDisabled);
        Assert.Contains("less than 16 GB", powerful.Warning);
        Assert.Contains(OnboardingTemplateRecommender.SizeLabel(OnboardingTemplateRecommender.Plan(OnboardingTemplate.Powerful).Model), powerful.Warning);
    }

    [Fact]
    public void Availability_WarnsAboutEverydayBelowEightGigabytes()
    {
        var everyday = OnboardingTemplateRecommender.Availability(OnboardingTemplate.Everyday, 4 * GiB);

        Assert.False(everyday.IsDisabled);
        Assert.False(everyday.IsRecommended);
        Assert.NotNull(everyday.Warning);
        Assert.True(OnboardingTemplateRecommender.Availability(OnboardingTemplate.Quick, 4 * GiB).IsRecommended);
    }

    [Fact]
    public void Availability_OffersEveryTierWithoutWarningsAtSixteenGigabytes()
    {
        foreach (var template in Enum.GetValues<OnboardingTemplate>())
        {
            var availability = OnboardingTemplateRecommender.Availability(template, 16 * GiB);

            Assert.False(availability.IsDisabled);
            Assert.Null(availability.Warning);
            Assert.Equal(template == OnboardingTemplate.Everyday, availability.IsRecommended);
        }
    }

    [Fact]
    public void Availability_NeverWarnsAboutQuick() =>
        Assert.Null(OnboardingTemplateRecommender.Availability(OnboardingTemplate.Quick, 2 * GiB).Warning);
}
