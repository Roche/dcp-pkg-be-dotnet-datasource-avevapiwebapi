using System;
using System.Collections.Generic;

namespace OpenDCP.Framework.Datasource.AVEVAPIwebAPI.Helpers
{
    internal static class WebIdFormatConverter
    {
        public static string ConvertFullWebIdToIdOnly(string fullWebId)
        {
            //webId is already IdOnly type
            if (string.IsNullOrEmpty(fullWebId) || fullWebId.Length < 50)
                return fullWebId;

            //replace F -> I
            char[] chars = fullWebId.ToCharArray();
            if (chars[0] == 'F')
                chars[0] = 'I';
            string modified = new string(chars);

            string marker = modified.Substring(2, 2);

            int prefixLength;
            if (ThreeCharMarkers.Contains(marker))
                prefixLength = 3;
            else if (TwoCharMarkers.Contains(marker))
                prefixLength = 2;
            else
                return fullWebId;

            int totalLength = prefixLength + 46;

            return modified.Substring(0, totalLength);
        }

        private static readonly HashSet<string> TwoCharMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Xs", "XC", "XT", "XP", "AC", "RD", "Em", "EC", "Fm", "Nf", "NT", "TP", "SM", "DS", "RS", "Ut", "UC"
        };

        private static readonly HashSet<string> ThreeCharMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "XR", "Ab", "AT", "MS", "MV", "TR"
        };
    }
}
