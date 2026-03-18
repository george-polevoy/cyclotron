using SampleCodebase.Domain;

namespace SampleCodebase.Pricing;

public interface IPricingStrategy
{
    decimal CalculatePrice(Order order, CustomerTier tier, bool peakSeason);
}
