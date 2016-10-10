// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Globalization;

namespace Microsoft.AspNetCore.ResponseCaching.Internal
{
    internal static class ParsingHelpers
    {
        private static readonly string[] DateFormats = new string[] {
            // "r", // RFC 1123, required output format but too strict for input
            "ddd, d MMM yyyy H:m:s 'GMT'", // RFC 1123 (r, except it allows both 1 and 01 for date and time)
            "ddd, d MMM yyyy H:m:s", // RFC 1123, no zone - assume GMT
            "d MMM yyyy H:m:s 'GMT'", // RFC 1123, no day-of-week
            "d MMM yyyy H:m:s", // RFC 1123, no day-of-week, no zone
            "ddd, d MMM yy H:m:s 'GMT'", // RFC 1123, short year
            "ddd, d MMM yy H:m:s", // RFC 1123, short year, no zone
            "d MMM yy H:m:s 'GMT'", // RFC 1123, no day-of-week, short year
            "d MMM yy H:m:s", // RFC 1123, no day-of-week, short year, no zone

            "dddd, d'-'MMM'-'yy H:m:s 'GMT'", // RFC 850
            "dddd, d'-'MMM'-'yy H:m:s", // RFC 850 no zone
            "ddd MMM d H:m:s yyyy", // ANSI C's asctime() format

            "ddd, d MMM yyyy H:m:s zzz", // RFC 5322
            "ddd, d MMM yyyy H:m:s", // RFC 5322 no zone
            "d MMM yyyy H:m:s zzz", // RFC 5322 no day-of-week
            "d MMM yyyy H:m:s", // RFC 5322 no day-of-week, no zone
        };

        internal static bool TryStringToDate(string input, out DateTimeOffset result)
        {
            // Try the various date formats in the order listed above.
            // We should accept a wide verity of common formats, but only output RFC 1123 style dates.
            if (DateTimeOffset.TryParseExact(input, DateFormats, DateTimeFormatInfo.InvariantInfo,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out result))
            {
                return true;
            }

            return false;
        }

        internal static bool TryParseHeaderValue(int startIndex, string header, out int value)
        {
            while (startIndex != header.Length)
            {
                if (header[startIndex] == '=')
                {
                    break;
                }
                ++startIndex;
            }
            if (startIndex != header.Length)
            {
                var endIndex = startIndex + 1;
                while (endIndex < header.Length)
                {
                    var c = header[endIndex];
                    if ((c >= '0') && (c <= '9'))
                    {
                        endIndex++;
                    }
                    else
                    {
                        break;
                    }
                }
                value = int.Parse(header.Substring(startIndex + 1, endIndex - (startIndex + 1)), NumberStyles.None, NumberFormatInfo.InvariantInfo);
                return true;
            }
            value = 0;
            return false;
        }
    }
}
