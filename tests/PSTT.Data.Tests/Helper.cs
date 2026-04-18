using PSTT.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace PSTT.Data.Tests
{
    /// <summary>
    /// Shared test helpers for all DataSource test classes
    /// </summary>
    public static class TestHelper
    {
        /// <summary>
        /// Helper to wait for a value to match expected
        /// </summary>
        public static async Task<bool> WaitForValue<T>(T expected, Func<T> actual, int maxRetries = 10)
        {
            var comparer = System.Collections.Generic.EqualityComparer<T>.Default;
            for (int i = 0; i < maxRetries; i++)
            {
                await Task.Delay(1);
                await Task.Yield();
                if (comparer.Equals(expected, actual()))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Asserts that a value matches expected after waiting with retries
        /// </summary>
        public static async Task AssertValueAfterWait<T>(T expected, Func<T> actual, int maxRetries = 10, string? message = null)
        {
            var result = await WaitForValue(expected, actual, maxRetries);
            var actualValue = actual();
            Assert.True(result, message ?? $"Expected value '{expected}' but got '{actualValue}' after {maxRetries} retries.");
        }

        /// <summary>
        /// Waits for a condition to be true
        /// </summary>
        public static async Task<bool> WaitForCondition(Func<bool> condition, int maxRetries = 10)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                await Task.Delay(1);
                await Task.Yield();
                if (condition())
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Asserts that a condition is true after waiting with retries
        /// </summary>
        public static async Task AssertConditionAfterWait(Func<bool> condition, int maxRetries = 10, string? message = null)
        {
            var result = await WaitForCondition(condition, maxRetries);
            Assert.True(result, message ?? $"Condition was not met after {maxRetries} retries.");
        }

        /// <summary>
        /// Creates a callback that will fail if invoked (useful for testing subscription limits)
        /// </summary>
        public static async Task<Func<ISubscription<TKey, TValue>, Task>> CallbackThatFailsIfInvoked<TKey, TValue>()
            where TKey : notnull
        {
            Func<ISubscription<TKey, TValue>, Task> callback = async s => 
            { 
                throw new Exception("Callback should not have been called!"); 
            };

            // Test it fails (also helps with code coverage!)
            await Assert.ThrowsAsync<Exception>(() => callback(null!));
            return callback;
        }
    }

    /// <summary>
    /// DataSource utility class for test suite with common helper methods
    /// </summary>
    public class DataSourceTestHelper<TKey, TValue> : Cache<TKey, TValue>
        where TKey : notnull
    {
        public DataSourceTestHelper() : base() { }

        public DataSourceTestHelper(CacheConfig<TKey, TValue> config) : base(config) { }

        public bool IsEmpty() => Count == 0 && SubscribeCount == 0;

        public async Task<(int retries, bool success)> WaitForValue<T>(T expected, Func<T> actual, int maxRetries = 10)
        {
            var comparer = System.Collections.Generic.EqualityComparer<T>.Default;
            int retry;
            for (retry = 0; retry < maxRetries; retry++)
            {
                await DoEventsAsync();
                if (comparer.Equals(expected, actual()))
                    return (retry, true);
            }
            return (retry, false);
        }

        public async Task<int> AssertValueAfterWait<T>(T expected, Func<T> actual, int maxRetries = 10)
        {
            var (retry, isEqual) = await WaitForValue(expected, actual, maxRetries);
            Assert.True(isEqual, $"Expected value '{expected}' but got '{actual()}' after {retry} retries.");
            return retry;
        }

        public async Task<int> AssertEmptyAfterWait(int maxRetries = 10)
        {
            var (retry, isEqual) = await WaitForValue(true, () => IsEmpty(), maxRetries);
            Assert.True(isEqual, $"DataSource should be empty but Count={Count}, SubscribeCount={SubscribeCount}");
            return retry;
        }

        public async Task<int> AssertConditionAfterWait(Func<bool> condition, int maxRetries = 10, string? message = null)
        {
            int retry;
            for (retry = 0; retry < maxRetries; retry++)
            {
                await DoEventsAsync();
                if (condition())
                    return retry;
            }
            Assert.True(false, message ?? $"Condition was not met after {retry} retries.");
            return retry;
        }
    }
}
