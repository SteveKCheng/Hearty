using System;
using System.Xml;

namespace JobBank.Server
{
    /// <summary>
    /// Wraps <see cref="TimeSpan"/> to allow it to be automatically bound from/to
    /// its text representation in Web-based APIs.
    /// </summary>
    internal readonly struct ExpiryTimeSpan
    {
        public TimeSpan Value { get; }

        public ExpiryTimeSpan(TimeSpan timeSpan)
        {
            Value = timeSpan;
        }

        public static bool TryParse(string value, out TimeSpan result)
        {
            try
            {
                if (!string.IsNullOrEmpty(value))
                {
                    result = XmlConvert.ToTimeSpan(value);
                    return true;
                }
            }
            catch (FormatException)
            {
            }

            result = TimeSpan.Zero;
            return false;
        }

        public static bool TryParse(string value, out ExpiryTimeSpan result)
        {
            bool success = TryParse(value, out TimeSpan timeSpan);
            result = new ExpiryTimeSpan(timeSpan);
            return success;
        }
    }
}
