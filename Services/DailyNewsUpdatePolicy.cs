using AIFrontier.Models;

namespace AIFrontier.Services;

public enum DailyNewsUpdateRoute
{
    UseCloud,
    WaitForCloud,
    UseLocalCodex
}

public static class DailyNewsUpdatePolicy
{
    public static DailyNewsUpdateRoute Decide(
        NewsUpdateMode mode,
        bool localFallbackEnabled,
        int failuresIncludingCurrent,
        CloudFeedHealth cloud)
    {
        if (mode == NewsUpdateMode.LocalCodexOnly)
        {
            return DailyNewsUpdateRoute.UseLocalCodex;
        }
        if (cloud.Reachable && cloud.Qualified && cloud.Fresh)
        {
            return DailyNewsUpdateRoute.UseCloud;
        }
        if (localFallbackEnabled && (!cloud.Reachable || failuresIncludingCurrent >= 2))
        {
            return DailyNewsUpdateRoute.UseLocalCodex;
        }
        return DailyNewsUpdateRoute.WaitForCloud;
    }
}
