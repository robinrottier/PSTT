using PSTT.Data;
using System;

namespace PSTT.Data
{
    /// <summary>
    /// MQTT-style pattern matcher supporting '+' (single-level wildcard) and '#' (multi-level wildcard)
    /// on '/'-separated topic strings.
    /// </summary>
    /// <remarks>
    /// Follows MQTT 3.1.1 topic filter semantics:
    /// <list type="bullet">
    ///   <item><description><c>sensors/+/temp</c> matches <c>sensors/living/temp</c> but not <c>sensors/living/room/temp</c>.</description></item>
    ///   <item><description><c>sensors/#</c> matches any topic starting with <c>sensors/</c> (and the parent <c>sensors</c> itself).</description></item>
    ///   <item><description><c>#</c> matches every topic.</description></item>
    /// </list>
    /// </remarks>
    public sealed class MqttWildcardMatcher : IWildcardMatcher<string>
    {
        /// <summary>
        /// Returns true if <paramref name="key"/> contains MQTT wildcard characters ('+' or '#').
        /// </summary>
        public bool IsPattern(string key) =>
            key != null && (key.Contains('+') || key.Contains('#'));

        /// <summary>
        /// Returns true if <paramref name="candidate"/> matches the MQTT topic filter <paramref name="pattern"/>.
        /// </summary>
        public bool Matches(string pattern, string candidate)
        {
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));

            string[] patternParts = pattern.Split('/');
            string[] candidateParts = candidate.Split('/');

            for (int i = 0; i < patternParts.Length; i++)
            {
                string p = patternParts[i];

                if (p == "#")
                {
                    // '#' matches the parent level and everything below — must match at least 0 more levels.
                    // "a/#" also matches "a" (the parent itself).
                    return true;
                }

                if (i >= candidateParts.Length)
                    return false;

                if (p != "+" && p != candidateParts[i])
                    return false;
            }

            // All pattern parts matched — candidate must have the same depth.
            return candidateParts.Length == patternParts.Length;
        }
    }
}
