namespace PSTT.Data
{
    /// <summary>
    /// Strategy interface for pattern-based key matching.
    /// Implementations define what constitutes a pattern key and how patterns match candidate keys.
    /// </summary>
    /// <typeparam name="TKey">The key type (typically <see cref="string"/>).</typeparam>
    /// <example>
    /// Built-in implementation: <see cref="PSTT.MqttPatternMatcher"/> supports MQTT-style wildcards
    /// ('+' for single level, '#' for multi-level).
    /// </example>
    public interface IWildcardMatcher<TKey>
    {
        /// <summary>
        /// Returns true if <paramref name="key"/> contains pattern syntax and should be treated
        /// as a wildcard subscription rather than an exact-match key.
        /// </summary>
        bool IsPattern(TKey key);

        /// <summary>
        /// Returns true if <paramref name="candidate"/> matches the given <paramref name="pattern"/>.
        /// </summary>
        /// <param name="pattern">A pattern key (for which <see cref="IsPattern"/> returns true).</param>
        /// <param name="candidate">An exact key to test against the pattern.</param>
        bool Matches(TKey pattern, TKey candidate);
    }
}
