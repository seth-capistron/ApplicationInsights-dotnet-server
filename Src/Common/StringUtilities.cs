namespace Microsoft.ApplicationInsights.Common
{
    using System.Diagnostics;

    /// <summary>
    /// Generic functions to perform common operations on a string.
    /// </summary>
    public static class StringUtilities
    {
        /// <summary>
        /// Check a strings length and trim to a max length if needed.
        /// </summary>
        public static string EnforceMaxLength(string input, int maxLength)
        {
            if (input != null && input.Length > maxLength)
            {
                input = input.Substring(0, maxLength);
            }

            return input;
        }
    }
}
