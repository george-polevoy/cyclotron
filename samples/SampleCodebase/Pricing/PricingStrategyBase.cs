using SampleCodebase.Domain;

namespace SampleCodebase.Pricing;

public abstract class PricingStrategyBase : IPricingStrategy
{
    protected decimal ApplyTierDiscount(decimal subtotal, CustomerTier tier) =>
        tier switch
        {
            CustomerTier.Preferred => subtotal * 0.95m,
            CustomerTier.Enterprise => subtotal * 0.90m,
            _ => subtotal,
        };

    public abstract decimal CalculatePrice(Order order, CustomerTier tier, bool peakSeason);
}
