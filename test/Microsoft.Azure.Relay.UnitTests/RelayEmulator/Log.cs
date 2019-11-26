using System;
using System.Diagnostics;

namespace Microsoft.Azure.Relay.UnitTests
{
    static class Log
    {
        internal static void Info(object source, string message)
        {
            Trace.TraceInformation($"[{source}] Info: {message}");
        }

        internal static void HandledException(Exception e, object source)
        {
            Trace.TraceWarning($"[{source}] An exception was handled. {e}");
        }

        internal static void Warning(object source, string message)
        {
            Trace.TraceWarning($"[{source}] Warn: {message}");
        }

        internal static Exception Argument(string paramName, string message)
        {
            return new ArgumentException(message, paramName);
        }

        internal static Exception ArgumentNull(string paramName)
        {
            return new ArgumentNullException(paramName);
        }

        internal static Exception ThrowingException(Exception exception, object source)
        {
            Warning(source, $"Throwing an exception: {exception}");
            return exception;
        }
    }
}