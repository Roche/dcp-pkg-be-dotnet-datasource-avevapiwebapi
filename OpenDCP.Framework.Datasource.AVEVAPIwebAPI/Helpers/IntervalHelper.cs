using System;

namespace OpenDCP.Framework.Datasource.AVEVAPIwebAPI.Helpers
{
    public static class IntervalHelper
    {
        public static TimeSpan ParseIntervalToTimeSpan(string interval)
        {
            return interval switch
            {
                string s when s.EndsWith("s") => TimeSpan.FromSeconds(double.Parse(s[..^1])),
                string s when s.EndsWith("m") => TimeSpan.FromMinutes(double.Parse(s[..^1])),
                string s when s.EndsWith("h") => TimeSpan.FromHours(double.Parse(s[..^1])),
                string s when s.EndsWith("d") => TimeSpan.FromDays(double.Parse(s[..^1])),
                _ => throw new ArgumentException($"Invalid interval format: {interval}")
            };
        }
    }
}
