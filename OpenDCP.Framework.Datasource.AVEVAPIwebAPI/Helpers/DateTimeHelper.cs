using System;

namespace OpenDCP.Framework.Datasource.AVEVAPIwebAPI.Helpers
{
    public static class DateTimeHelper
    {
        public static string ToIso8601String(this DateTime dateTime)
        {
            return dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        }
    }
}
