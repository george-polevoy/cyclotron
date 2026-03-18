using SampleCodebase.Domain;

namespace SampleCodebase.Pricing;

public sealed class SeasonalPricingStrategy : PricingStrategyBase
{
    public override decimal CalculatePrice(Order order, CustomerTier tier, bool peakSeason)
    {
        var subtotal = order.Lines.Sum(line => line.Quantity * line.UnitPrice);
        subtotal = ApplyTierDiscount(subtotal, tier);

        if (peakSeason && subtotal > 250m)
        {
            subtotal *= 1.08m;
        }
        else if (order.IsExpedited)
        {
            subtotal += 25m;
        }

        return subtotal;
    }
}
