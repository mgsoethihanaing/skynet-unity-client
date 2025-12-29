// TaskExtensions.cs
// Helper extensions for safe async patterns in Unity

using System;
using System.Threading.Tasks;

namespace SkynetUnity
{
    /// <summary>
    /// Extensions for safe fire-and-forget async operations in Unity.
    /// </summary>
    public static class TaskExtensions
    {
        /// <summary>
        /// Safely fire-and-forget a task without causing unobserved exceptions.
        /// Swallows OperationCanceledException (normal during shutdown).
        /// Reports other exceptions via callback.
        /// </summary>
        /// <param name="task">Task to execute</param>
        /// <param name="onException">Optional exception handler</param>
        public static async void SafeFireAndForget(
            this Task task, 
            Action<Exception> onException = null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown - don't report
            }
            catch (Exception ex)
            {
                onException?.Invoke(ex);
            }
        }
    }
}