using System.Globalization;
using System.Reflection;
using SimpleL7Proxy.Extensions;

namespace SimpleL7Proxy.Tokenomics;

public class TokenomicsLimits
{
    public int MonthlyTokenLimit { get; set; }=10000;
    public int DailyTokenLimit { get; set; }=1000;
    public int HourlyTokenLimit { get; set; }=1000;

    public decimal HourlyBudgetUsd { get; set; } = 10m;
    public decimal DailyBudgetUsd { get; set; } = 50m;
    public decimal MonthlyBudgetUsd { get; set; }=1000m;

}
