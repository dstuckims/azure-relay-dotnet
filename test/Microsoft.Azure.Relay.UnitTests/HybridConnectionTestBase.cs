// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.Relay.UnitTests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.WebSockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class HybridConnectionTestBase : IDisposable
    {
        readonly RelayEmulator relayEmulator;

        protected HybridConnectionTestBase()
        {
            var envConnectionString = Environment.GetEnvironmentVariable(Constants.ConnectionStringEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(envConnectionString))
            {
                throw new InvalidOperationException($"'{Constants.ConnectionStringEnvironmentVariable}' environment variable was not found!");
            }

            // Validate the connection string
            var connectionStringBuilder = new RelayConnectionStringBuilder(envConnectionString);
            this.ConnectionString = connectionStringBuilder.ToString();

            if (string.Equals(connectionStringBuilder.Endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                this.relayEmulator = new RelayEmulator(connectionStringBuilder.Endpoint.Port);
                this.relayEmulator.Authorization.Add(new SharedAccessAuthorizationRule(connectionStringBuilder.SharedAccessKeyName, connectionStringBuilder.SharedAccessKey, new[] { AccessRights.Manage, AccessRights.Listen, AccessRights.Send }));
                this.relayEmulator.Start();
                var hybridConnectionAuth = new HybridConnectionDescription(Constants.AuthenticatedEntityPath) { RequiresClientAuthorization = true };
                this.relayEmulator.CreateHybridConnection(hybridConnectionAuth);

                var hybridConnectionUnauth = new HybridConnectionDescription(Constants.UnauthenticatedEntityPath) { RequiresClientAuthorization = false };
                this.relayEmulator.CreateHybridConnection(hybridConnectionUnauth);
            }
        }

        protected string ConnectionString { get; private set; }

        protected enum EndpointTestType
        {
            Authenticated,
            Unauthenticated
        }

        public static IEnumerable<object[]> AuthenticationTestPermutations => new object[][]
        {
            new object[] { EndpointTestType.Authenticated },
            new object[] { EndpointTestType.Unauthenticated }
        };

        public static IEnumerable<object[]> AuthenticationAndBuiltInClientWebSocketTestPermutations => new List<object[]>
        {
            new object[] { EndpointTestType.Authenticated, false },
            new object[] { EndpointTestType.Unauthenticated, false },
            new object[] { EndpointTestType.Authenticated, true },
            new object[] { EndpointTestType.Unauthenticated, true },
        };

        public void Dispose()
        {
            this.Dispose(true);
                envConnectionString = Environment.GetEnvironmentVariable(Constants.LegacyConnectionStringEnvironmentVariable);
                if (string.IsNullOrWhiteSpace(envConnectionString))
                {
                    throw new InvalidOperationException($"'{Constants.ConnectionStringEnvironmentVariable}' environment variable was not found!");
                }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.relayEmulator?.Dispose();
                GC.SuppressFinalize(this);
            }
        }

        /// <summary>
        /// Returns a HybridConnectionClient based on the EndpointTestType (authenticated/unauthenticated).
        /// </summary>
        protected HybridConnectionClient GetHybridConnectionClient(EndpointTestType endpointTestType)
        {
            var connectionStringBuilder = GetConnectionString(endpointTestType, isListener: false);
            return new HybridConnectionClient(connectionStringBuilder.ToString());
        }

        /// <summary>
        /// Returns a HybridConnectionListener based on the EndpointTestType (authenticated/unauthenticated).
        /// </summary>
        protected HybridConnectionListener GetHybridConnectionListener(EndpointTestType endpointTestType)
        {
            // Even if the endpoint is unauthenticated, the *listener* still needs to be authenticated
            return new HybridConnectionListener(GetConnectionString(endpointTestType, isListener: true).ToString());
        }

        public static void LogRequestLine(HttpRequestMessage httpRequest, HttpClient httpClient)
        {
            string requestUri = $"{httpClient?.BaseAddress}{httpRequest.RequestUri}";
            TestUtility.Log($"Request: {httpRequest.Method} {requestUri} HTTP/{httpRequest.Version}");
        }

        public static void LogRequest(HttpRequestMessage httpRequest, HttpClient httpClient, bool showBody = true)
        {
            TestUtility.Log("Sending Request:");
            string requestUri = $"{httpClient?.BaseAddress}{httpRequest.RequestUri}";
            TestUtility.Log($"{httpRequest.Method} {requestUri} HTTP/{httpRequest.Version}");
            httpClient?.DefaultRequestHeaders.ToList().ForEach((kvp) => LogHttpHeader(kvp.Key, kvp.Value));
            httpRequest.Headers.ToList().ForEach((kvp) => LogHttpHeader(kvp.Key, kvp.Value));
            httpRequest.Content?.Headers.ToList().ForEach((kvp) => LogHttpHeader(kvp.Key, kvp.Value));

            TestUtility.Log(string.Empty);
            if (httpRequest.Content != null)
            {
                if (showBody)
                {
                    TestUtility.Log(httpRequest.Content?.ReadAsStringAsync().Result);
                }
                else
                {
                    TestUtility.Log($"(Body stream is {httpRequest.Content?.ReadAsStreamAsync().Result.Length ?? 0} bytes)");
                }
            }
        }

        public static void LogResponseLine(HttpResponseMessage httpResponse)
        {
            TestUtility.Log($"Response: HTTP/{httpResponse.Version} {(int)httpResponse.StatusCode} {httpResponse.ReasonPhrase}");
        }

        public static void LogResponse(HttpResponseMessage httpResponse, bool? showBody = true)
        {
            TestUtility.Log("Received Response:");
            TestUtility.Log($"HTTP/{httpResponse.Version} {(int)httpResponse.StatusCode} {httpResponse.ReasonPhrase}");
            httpResponse.Headers.ToList().ForEach((kvp) => LogHttpHeader(kvp.Key, kvp.Value));

            TestUtility.Log(string.Empty);
            if (showBody.HasValue && httpResponse.Content != null)
            {
                if (showBody.Value)
                {
                    TestUtility.Log(httpResponse.Content?.ReadAsStringAsync().Result);
                }
                else
                {
                    TestUtility.Log($"(Body stream is {httpResponse.Content?.ReadAsStreamAsync().Result.Length ?? 0} bytes)");
                }
            }
        }

        public static void LogHttpHeader(string headerName, IEnumerable<string> headerValues)
        {
            TestUtility.Log($"{headerName}: {string.Join(",", headerValues)}");
        }

        protected async Task SafeCloseAsync(HybridConnectionListener listener)
        {
            if (listener != null)
            {
                try
                {
                    TestUtility.Log($"Closing {listener}");
                    await listener.CloseAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception e)
                {
                    TestUtility.Log($"Error closing HybridConnectionListener {e.GetType()}: {e.Message}");
                }
            }
        }

        protected static string StreamToString(Stream stream)
        {
            using (var streamReader = new StreamReader(stream, Encoding.UTF8))
            {
                return streamReader.ReadToEnd();
            }
        }

        /// <summary>
        /// Call HybridConnectionListener.AcceptConnectionAsync, once/if a listener is accepted
        /// read from its stream and echo the bytes until a 0-byte read occurs, then close.
        /// </summary>
        protected async void AcceptEchoListener(HybridConnectionListener listener)
        {
            try
            {
                var listenerStream = await listener.AcceptConnectionAsync();
                if (listenerStream != null)
                {
                    byte[] buffer = new byte[4 * 1024];
                    do
                    {
                        int bytesRead;
                        try
                        {
                            bytesRead = await listenerStream.ReadAsync(buffer, 0, buffer.Length);
                        }
                        catch (Exception readException)
                        {
                            TestUtility.Log($"AcceptEchoListener {readException.GetType().Name}: {readException.Message}");
                            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                            {
                                await listenerStream.CloseAsync(cts.Token);
                            }

                            return;
                        }

                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                        {
                            if (bytesRead == 0)
                            {
                                await listenerStream.CloseAsync(cts.Token);
                                return;
                            }
                            else
                            {
                                await listenerStream.WriteAsync(buffer, 0, bytesRead, cts.Token);
                            }
                        }
                    }
                    while (true);
                }
            }
            catch (Exception e)
            {
                TestUtility.Log($"AcceptEchoListener {e.GetType().Name}: {e.Message}");
            }
        }

        protected async Task ReadCountBytesAsync(Stream stream, byte[] buffer, int offset, int bytesToRead, TimeSpan timeout)
        {
            DateTime timeoutInstant = DateTime.Now.Add(timeout);
            int? originalReadTimeout = stream.CanTimeout ? stream.ReadTimeout : (int?)null;
            try
            {
                int totalBytesRead = 0;
                do
                {
                    TimeSpan remainingTimeout = timeoutInstant.Subtract(DateTime.Now);
                    if (remainingTimeout <= TimeSpan.Zero)
                    {
                        break;
                    }

                    stream.ReadTimeout = (int)remainingTimeout.TotalMilliseconds;
                    int bytesRead = await stream.ReadAsync(buffer, offset + totalBytesRead, bytesToRead - totalBytesRead);
                    TestUtility.Log($"Stream read {bytesRead} bytes");
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    totalBytesRead += bytesRead;
                }
                while (totalBytesRead < bytesToRead);

                if (totalBytesRead < bytesToRead)
                {
                    throw new TimeoutException("The requested number of bytes could not be received.  ReadAsync returned 0 bytes");
                }
            }
            finally
            {
                if (originalReadTimeout.HasValue)
                {
                    stream.ReadTimeout = originalReadTimeout.Value;
                }
            }
        }

        protected byte[] CreateBuffer(int length, byte[] fillPattern)
        {
            byte[] buffer = new byte[length];

            int offset = 0;
            do
            {
                int bytesToCopy = Math.Min(length - offset, fillPattern.Length);
                Array.Copy(fillPattern, 0, buffer, offset, bytesToCopy);
                offset += bytesToCopy;
            }
            while (offset < length);

            return buffer;
        }

        /// <summary>
        /// Calls Stream.ReadAsync with Exception handling. If an IOException occurs a zero byte read is returned.
        /// </summary>
        protected async Task<int> SafeReadAsync(Stream stream, byte[] buffer, int offset, int bytesToRead)
        {
            int bytesRead = 0;
            try
            {
                bytesRead = await stream.ReadAsync(buffer, offset, bytesToRead);
                TestUtility.Log($"Stream read {bytesRead} bytes");
            }
            catch (IOException ex)
            {
                TestUtility.Log($"Stream.ReadAsync error {ex}");
            }

            return bytesRead;
        }

        /// <summary>
        /// Since these tests all share a common connection string, this method will modify the 
        /// endpoint / shared access keys as needed based on the EndpointTestType.
        /// </summary>
        protected RelayConnectionStringBuilder GetConnectionString(EndpointTestType endpointTestType, bool isListener)
        {
            var connectionStringBuilder = new RelayConnectionStringBuilder(this.ConnectionString);
            if (endpointTestType == EndpointTestType.Unauthenticated)
            {
                connectionStringBuilder.EntityPath = Constants.UnauthenticatedEntityPath;
                if (!isListener)
                {
                    connectionStringBuilder.SharedAccessKey = string.Empty;
                    connectionStringBuilder.SharedAccessKeyName = string.Empty;
                    connectionStringBuilder.SharedAccessSignature = string.Empty;
                }
            }
            else
            {
                connectionStringBuilder.EntityPath = Constants.AuthenticatedEntityPath;
            }

            return connectionStringBuilder;
        }

        protected void VerifyHttpStatusCodeAndDescription(
            WebSocketException webSocketException,
            HttpStatusCode expectedStatusCode,
            string expectedStatusDescription,
            bool exactMatchDescription = true)
        {
            // TODO: Error details aren't available in .NET Core due to issue:
            // https://github.com/dotnet/corefx/issues/13773
#if NET46
            Assert.NotNull(webSocketException.InnerException);

            Assert.IsAssignableFrom<WebException>(webSocketException.InnerException);
            var webException = (WebException)webSocketException.InnerException;
            Assert.NotNull(webException.Response);
            Assert.IsAssignableFrom<HttpWebResponse>(webException.Response);
            var httpWebResponse = (HttpWebResponse)webException.Response;
            TestUtility.Log($"Actual HTTP Status: {(int)httpWebResponse.StatusCode}: {httpWebResponse.StatusDescription}");
            Assert.Equal(expectedStatusCode, httpWebResponse.StatusCode);
            if (exactMatchDescription)
            {
                Assert.Equal(expectedStatusDescription, httpWebResponse.StatusDescription);
            }
            else
            {
                Assert.Contains(expectedStatusDescription, httpWebResponse.StatusDescription);
            }
#endif // NET46
        }
    }
}
