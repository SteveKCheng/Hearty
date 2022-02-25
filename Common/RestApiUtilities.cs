using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace Hearty.Common
{
    public static class RestApiUtilities
    {
        public static bool TryParseTimespan(string input, out TimeSpan result)
        {
            try
            {
                if (!string.IsNullOrEmpty(input))
                {
                    result = XmlConvert.ToTimeSpan(input);
                    return true;
                }
            }
            catch (FormatException)
            {
            }

            result = TimeSpan.Zero;
            return false;
        }

        public static string FormatTimeSpan(TimeSpan value)
        {
            return $"P{value.Days}DT{value.Hours}H{value.Minutes}M{value.Seconds}.{value.Milliseconds:D3}S";
        }
    }
}
