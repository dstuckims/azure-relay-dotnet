using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace Microsoft.Azure.Relay.UnitTests
{
    sealed class HybridRequest : IDisposable
    {
        public static readonly TimeSpan ConnectOperationTimeout = TimeSpan.FromSeconds(10);
        const int MaxBodySizeWithoutRendezvous = 64 * 1024;
        const int ReadBufferSize = 32 * 1024;
        static readonly TimeSpan ListenerResponseTimeout = TimeSpan.FromSeconds(60);
        readonly HttpListenerContext listenerContext;
        readonly Uri sbRequestUri;
        readonly TaskCompletionSource<bool> sendRequestCompletionSource;
        readonly TaskCompletionSource<WebSocket> rendezvousCompletionSource;
        readonly TaskCompletionSource<ResponseCommandAndStream> responseReadyCompletionSource;
        readonly int? contentLength;
        readonly DateTime startTimestamp;
        NameValueCollection queryString;
        SecurityToken securityToken;
        string queryStringToken;
        string queryStringSasKeyName;
        string queryStringSasKey;
        bool disposed;

        public HybridRequest(
            HttpListenerContext listenerContext, 
            RelayEmulator relayService)
        {
            this.startTimestamp = DateTime.UtcNow;
            this.listenerContext = listenerContext;
            this.sbRequestUri = new UriBuilder(this.listenerContext.Request.Url) { Scheme = "sb", Port = -1 }.Uri;
            this.TrackingContext = TrackingContext.Create(Guid.NewGuid() + "_G0", this.sbRequestUri.GetLeftPart(UriPartial.Path));
            this.RelayService = relayService;
            this.sendRequestCompletionSource = new TaskCompletionSource<bool>();
            this.responseReadyCompletionSource = new TaskCompletionSource<ResponseCommandAndStream>();
            this.rendezvousCompletionSource = new TaskCompletionSource<WebSocket>();
            this.contentLength = this.GetContentLength();
            this.RemoteEndpoint = this.GetRemoteEndpoint();

            //this.operationContext.OperationCompleted += (s, e) => this.Dispose();
        }
       
        public IPEndPoint RemoteEndpoint { get; }

        public TrackingContext TrackingContext { get; }

        public Task ResponseReadyTask
        {
            get { return this.responseReadyCompletionSource.Task; }
        }

        bool IsRendezvousRequired
        {
            get
            {
                if (this.IncomingRequestStream == null ||
                    (this.contentLength.HasValue && this.contentLength.Value <= MaxBodySizeWithoutRendezvous))
                {
                    return false;
                }

                return true;
            }
        }

        string HttpMethod => this.listenerContext.Request.HttpMethod;

        RelayEmulator RelayService { get; }

        Stream IncomingRequestStream { get; set; }

        object ThisLock => this.responseReadyCompletionSource;

        public async Task ProcessAsync()
        {
            try
            {
                Log.Info(this, $"RelayEmulator: HybridHttp request received: {this.HttpMethod}");

                this.ParseAndFilterQueryString();
                this.securityToken = this.GetToken();
                this.FilterConnectionHeaders();
                this.AddViaHeader(this.listenerContext.Request.Headers);

                //this.RelayService.AuthorizeUser(this.NamePolicy, this.requestUri, this.securityToken, true, this.TrackingContext);

                if (this.listenerContext.Request.ContentLength64 != 0)
                {
                    this.IncomingRequestStream = this.listenerContext.Request.InputStream;
                }

                await this.SendRequestAsync();

                await this.WaitForResponse();
            }
            catch (Exception e)
            {
                this.AbortRendezvousConnection();
                this.PrepareErrorResponseMessage(e);
            }
        }

        public async Task AcceptRendezvousAsync(HttpListenerContext listenerContext, TrackingContext trackingContext)
        {
            try
            {
                HttpListenerWebSocketContext webSocketContext = await listenerContext.AcceptWebSocketAsync(null);
                var rendezvousWebSocket = webSocketContext.WebSocket;
                this.rendezvousCompletionSource.SetResult(rendezvousWebSocket);

                // Wait until the request is sent (large requests will be sent over this rendezvous connection at this point)
                // No timeout is necessary here because sendRequestCompletionSource will be completed in case of error or success.
                await this.sendRequestCompletionSource.Task;

                ListenerCommand.ResponseCommand responseCommand;
                byte[] array = null;
                Stream stream = null;
                var cancelSource = new CancellationTokenSource(ListenerResponseTimeout);
                try
                {
                    array = new byte[ReadBufferSize];
                    stream = new MemoryStream();
                    WebSocketReadMessageResult readResult = await WebSocketUtility.ReadMessageAsync(rendezvousWebSocket, new ArraySegment<byte>(array), stream, cancelSource.Token);
                    if (readResult.MessageType == WebSocketMessageType.Close)
                    {
                        await this.CloseRendezvousConnectionAsync(WebSocketCloseStatus.ProtocolError, "Premature shutdown: WebSocket closed.");
                        return;
                    }
                    else if (readResult.MessageType != WebSocketMessageType.Text)
                    {
                        await this.CloseRendezvousConnectionAsync(WebSocketCloseStatus.ProtocolError, $"Command messages must be MessageType=Text, actual={readResult.MessageType}.");
                        return;
                    }

                    stream.Position = 0;
                    var listenerCommand = ListenerCommand.ReadObject(stream);
                    responseCommand = listenerCommand.Response;
                    if (responseCommand == null)
                    {
                        await this.CloseRendezvousConnectionAsync(WebSocketCloseStatus.ProtocolError, "'response' command was expected.");
                        return;
                    }
                    else if (string.IsNullOrEmpty(responseCommand.RequestId))
                    {
                        await this.CloseRendezvousConnectionAsync(WebSocketCloseStatus.ProtocolError, "'response.requestId' is required.");
                        return;
                    }
                }
                finally
                {
                    stream?.Dispose();
                    cancelSource.Dispose();
                }

                Stream responseStream = null;
                if (responseCommand.Body)
                {
                    responseStream = new WebSocketMessageStream(rendezvousWebSocket, ConnectOperationTimeout);
                }

                this.SetResponseCommandAndStream(responseCommand, responseStream);
            }
            catch (Exception e)
            {
                Log.Warning(this, $"RelayEmulator: HybridHttp Accepting of the Request failed. {e}");
                this.responseReadyCompletionSource.TrySetException(e);
                await this.CloseRendezvousConnectionAsync(
                    WebSocketCloseStatus.InternalServerError,
                    $"Unexpected error. If the problem persists contact support and provide the the following tracking details: {trackingContext}");
            }
        }

        public void SetResponseCommandAndStream(ListenerCommand.ResponseCommand responseCommand, Stream bodyStream)
        {
            this.responseReadyCompletionSource.TrySetResult(new ResponseCommandAndStream(responseCommand, bodyStream));
        }

        public void Dispose()
        {
            lock (this.ThisLock)
            {
                if (this.disposed)
                {
                    return;
                }

                this.disposed = true;
            }

            var ignore = this.CleanupAsync();
        }

        public override string ToString()
        {
            // Used in tracing
            return nameof(HybridRequest) + "(" + this.TrackingContext + ")";
        }

        async Task CleanupAsync()
        {
            try
            {
                await this.CloseRendezvousConnectionAsync(WebSocketCloseStatus.NormalClosure, "Normal Closure");
            }
            catch (Exception e)
            {
                Log.HandledException(e, this);
            }
        }

        async Task SendRequestAsync()
        {
            byte[] readBuffer = null;
            try
            {
                // Send the initial command through the listener's Gateway and control connection
                ListenerCommand.RequestCommand requestCommand = this.CreateRequestCommand(isControlConnection: true);
                await this.SendRequestToListenerGatewayAsync(requestCommand);

                if (this.IsRendezvousRequired)
                {
                    requestCommand = this.CreateRequestCommand(isControlConnection: false);

                    WebSocket rendezvousWebSocket = await WaitForListenerRendezvousAsync();

                    readBuffer = new byte[ReadBufferSize];
                    using (var cancelSource = new CancellationTokenSource(ConnectOperationTimeout))
                    using (var stream = new MemoryStream(readBuffer, writable: true))
                    {
                        stream.SetLength(0);
                        var listenerCommand = new ListenerCommand { Request = requestCommand };
                        listenerCommand.WriteObject(stream);
                        var buffer = new ArraySegment<byte>(readBuffer, 0, (int)stream.Length);
                        await rendezvousWebSocket.SendAsync(buffer, WebSocketMessageType.Text, true, cancelSource.Token);
                    }

                    int bytesRead;
                    do
                    {
                        using (var cancelSource = new CancellationTokenSource(ConnectOperationTimeout))
                        {
                            bytesRead = await this.IncomingRequestStream.ReadAsync(readBuffer, 0, readBuffer.Length, cancelSource.Token);
                            bool endOfMessage = bytesRead == 0;
                            var bodyBuffer = new ArraySegment<byte>(readBuffer, 0, bytesRead);
                            await rendezvousWebSocket.SendAsync(bodyBuffer, WebSocketMessageType.Binary, endOfMessage, cancelSource.Token);
                        }
                    }
                    while (bytesRead > 0);
                }

                this.sendRequestCompletionSource.SetResult(true);
            }
            catch (Exception e)
            {
                Log.ThrowingException(e, this);
                this.sendRequestCompletionSource.SetException(e);
                throw;
            }
        }

        async Task WaitForResponse()
        {
            // Wait for Listener to provide Response over control channel or via rendezvous (or timeout)
            try
            {
                ResponseCommandAndStream responseAndStream = await this.responseReadyCompletionSource.Task;
                await ProcessResponseAsync(responseAndStream);
            }
            catch (Exception e) when (e is TimeoutException || e is OperationCanceledException)
            {
                var exception = Log.ThrowingException(new TimeoutException("The request was routed to the listener but the listener did not respond in the required time.", e), this);
                throw exception;
            }
        }

        ListenerCommand.RequestCommand CreateRequestCommand(bool isControlConnection)
        {
            var queryParameters = new StringBuilder();
            foreach (var paramName in this.queryString.AllKeys)
            {
                if (queryParameters.Length > 0)
                {
                    queryParameters.Append("&");
                }

                queryParameters.Append($"{paramName}={this.queryString[paramName]}");
            }

            Uri rendezvousUri = HybridConnectionUtility.BuildUri(
                this.listenerContext.Request.Url.Host,
                this.listenerContext.Request.Url.Port,
                this.listenerContext.Request.Url.AbsolutePath,
                queryParameters.ToString(),
                HybridConnectionConstants.Actions.Request,
                this.TrackingContext.TrackingId);

            var requestCommand = new ListenerCommand.RequestCommand { Address = rendezvousUri.AbsoluteUri };
            if (!isControlConnection || !this.IsRendezvousRequired)
            {
                requestCommand.Id = this.TrackingContext.TrackingId;
                requestCommand.Body = this.IncomingRequestStream != null;
                requestCommand.Method = this.HttpMethod;
                requestCommand.RequestTarget = this.sbRequestUri.GetComponents(UriComponents.Path, UriFormat.Unescaped);
                if (!requestCommand.RequestTarget.StartsWith("/"))
                {
                    requestCommand.RequestTarget = "/" + requestCommand.RequestTarget;
                }

                if (queryParameters.Length > 0)
                {
                    requestCommand.RequestTarget += "?" + queryParameters;
                }

                foreach (string headerName in this.listenerContext.Request.Headers.AllKeys)
                {
                    requestCommand.RequestHeaders[headerName] = this.listenerContext.Request.Headers[headerName];
                }

                // Add the RemoteEndpoint details
                if (this.RemoteEndpoint?.Address != null)
                {
                    requestCommand.RemoteEndpoint.Address = this.RemoteEndpoint.Address.ToString();
                    requestCommand.RemoteEndpoint.Port = this.RemoteEndpoint.Port;
                }
            }

            return requestCommand;
        }

        async Task SendRequestToListenerGatewayAsync(ListenerCommand.RequestCommand requestCommand)
        {
            ArraySegment<byte> buffer = default;
            var listenerCommand = new ListenerCommand { Request = requestCommand };
            if (!this.IsRendezvousRequired && this.IncomingRequestStream != null)
            {
                buffer = this.BufferRequestBody();
            }

            await this.RelayService.SendToListenerClientAsync(this.sbRequestUri.AbsoluteUri, listenerCommand, this.TrackingContext, buffer);
        }

        ArraySegment<byte> BufferRequestBody()
        {
            Debug.Assert(!this.IsRendezvousRequired, "this should only be called for no-rendezvous requests");
            Debug.Assert(this.IncomingRequestStream != null, "this should only be called when IncomingRequestStream is present");
            Debug.Assert(this.contentLength.HasValue, "If no rendezvous is required and IncomingRequestStream is present then contentLength must be present");

            using (var memStream = new MemoryStream(this.contentLength.Value))
            {
                this.IncomingRequestStream.CopyTo(memStream);
                memStream.Position = 0;
                if (!memStream.TryGetBuffer(out ArraySegment<byte> buffer))
                {
                    buffer = new ArraySegment<byte>(memStream.GetBuffer());
                }

                return buffer;
            }
        }

        void AbortRendezvousConnection()
        {
            if (this.rendezvousCompletionSource.Task.Status == TaskStatus.RanToCompletion)
            {
                var rendezvousWebSocket = this.rendezvousCompletionSource.Task.Result;
                rendezvousWebSocket.Abort();
                rendezvousWebSocket.Dispose();
            }
        }

        async Task CloseRendezvousConnectionAsync(WebSocketCloseStatus closeStatus, string closeDescription)
        {
            if (this.rendezvousCompletionSource.Task.Status == TaskStatus.RanToCompletion)
            {
                var rendezvousWebSocket = await this.rendezvousCompletionSource.Task;
                if (rendezvousWebSocket.State != WebSocketState.Aborted && rendezvousWebSocket.State != WebSocketState.Closed)
                {
                    Log.Info(this, $"RelayEmulator: HybridHttp rendezvous connection closing ({closeStatus}:{closeDescription})");
                    using (var cts = new CancellationTokenSource(ConnectOperationTimeout))
                    {
                        await rendezvousWebSocket.CloseAsync(closeStatus, closeDescription, cts.Token);
                    }

                    Log.Info(this, "RelayEmulator: HybridHttp rendezvous connection closed");
                }
            }
        }

        async Task ProcessResponseAsync(ResponseCommandAndStream responseAndStream)
        {
            ListenerCommand.ResponseCommand responseCommand = responseAndStream.ResponseCommand;
            Log.Info(this, $"RelayEmulator: HybridHttp returning response: {responseCommand.StatusCode} HasBody={responseCommand.Body}");
            this.AddViaHeader(this.listenerContext.Response.Headers);
            this.listenerContext.Response.StatusCode = responseCommand.StatusCode;
            this.listenerContext.Response.StatusDescription = responseCommand.StatusDescription;
            foreach (var kvp in responseCommand.ResponseHeaders)
            {
                this.listenerContext.Response.Headers.Add(kvp.Key, kvp.Value);
            }

            if (responseCommand.Body)
            {
                Stream responseStream = responseAndStream.Stream;
                Debug.Assert(responseStream != null, "ResponseStream must not be null if ResponseCommand.Body == true");
                await responseStream.CopyToAsync(this.listenerContext.Response.OutputStream);
            }

            this.listenerContext.Response.Close();
        }

        void PrepareErrorResponseMessage(Exception e)
        {
            // Code Error           Description
            // 404  Not Found       The Hybrid Connection path is invalid or the base URL is malformed.
            // 401  Unauthorized    The security token is missing or malformed or invalid.
            // 403  Forbidden       The security token is not valid for this path and for this action.
            // 500  Internal Error  Something went wrong in the service.
            // 502  Bad Gateway     The notification could not be routed to any listener.
            // 504  Gateway Timeout The notification was routed to the listener, but the listener did not acknowledge receipt in the required time.

            var errorDetail = new ErrorDetail { Code = ErrorCode.InternalServerError };
            HttpStatusCode statusCode = HttpStatusCode.InternalServerError;
            string statusDescription = "InternalServerError";
            if (e is TimeoutException)
            {
                statusCode = HttpStatusCode.GatewayTimeout;
                errorDetail.Code = ErrorCode.GatewayTimeout;
                statusDescription = e.Message;
            }
            else if (e is AuthorizationFailedException)
            {
                statusDescription = e.Message;
                var authFailedException = (AuthorizationFailedException)e;
                statusCode = HttpStatusCode.Unauthorized;
                errorDetail.Code = ErrorCode.TokenMissingOrInvalid;
            }
            else if (e is EndpointNotFoundException)
            {
                statusCode = HttpStatusCode.NotFound;
                errorDetail.Code = ErrorCode.EndpointNotFound;
                statusDescription = e.Message;
            }

            statusDescription = statusDescription + ". " + this.TrackingContext;
            errorDetail.Message = statusDescription;

            Log.Warning(this, $"RelayEmulator: HybridHttp Returning Failure Response: {statusCode} due to {e}");

            this.listenerContext.Response.StatusCode = (int)statusCode;
            this.listenerContext.Response.StatusDescription = statusDescription;
            this.listenerContext.Response.ContentType = "application/json";

            byte[] jsonBytes = Encoding.UTF8.GetBytes(errorDetail.ToJson());
            this.listenerContext.Response.OutputStream.Write(jsonBytes, 0, jsonBytes.Length);
        }

        SecurityToken GetToken()
        {
            SecurityToken token = null;
            try
            {
                if (!string.IsNullOrEmpty(this.queryStringToken))
                {
                    token = new SharedAccessSignatureToken(this.queryStringToken);
                }

                string tokenString = this.listenerContext.Request.Headers["ServiceBusAuthorization"];
                if (!string.IsNullOrEmpty(tokenString))
                {
                    // Always strip the ServiceBusAuthorizationHeader
                    this.listenerContext.Request.Headers.Remove("ServiceBusAuthorization");
                    if (token == null)
                    {
                        token = new SharedAccessSignatureToken(tokenString);
                    }
                }

                if (token == null && (!string.IsNullOrEmpty(this.queryStringSasKeyName) || !string.IsNullOrEmpty(this.queryStringSasKey)))
                {
                    if (string.IsNullOrEmpty(this.queryStringSasKeyName) || string.IsNullOrEmpty(this.queryStringSasKey))
                    {
                        throw Log.ThrowingException(
                            new AuthorizationFailedException("Malformed Token"),
                            this);
                    }

                    var tokenProvider = TokenProvider.CreateSharedAccessSignatureTokenProvider(this.queryStringSasKeyName, this.queryStringSasKey);
                    token = (SecurityToken)tokenProvider.GetTokenAsync(this.sbRequestUri.AbsoluteUri, TimeSpan.FromMinutes(60)).GetAwaiter().GetResult();
                }

                if (token == null) // && this.HybridConnectionDescription.RequiresClientAuthorization)
                {
                    // Only strip Authorization header if it's used for Authorizing with Azure Relay
                    tokenString = this.listenerContext.Request.Headers["Authorization"];
                    if (!string.IsNullOrEmpty(tokenString))
                    {
                        this.listenerContext.Request.Headers.Remove("Authorization");
                        token = new SharedAccessSignatureToken(tokenString);
                    }
                }

                return token;
            }
            catch (FormatException formatException)
            {
                throw Log.ThrowingException(
                    new AuthorizationFailedException($"Authorization failed. {formatException.GetType()}: {formatException.Message}."), this);
            }
        }

        void AddViaHeader(NameValueCollection headers)
        {
            headers.Add("Via", "1.1 " + this.sbRequestUri.Host);
        }

        void ParseAndFilterQueryString()
        {
            this.queryString = this.listenerContext.Request.QueryString;

            foreach (string key in this.queryString.AllKeys)
            {
                if (!string.IsNullOrEmpty(key) && key.StartsWith(HybridConnectionConstants.QueryStringKeyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    // Read any sb-hc- Token related parameters
                    if (key.Equals(HybridConnectionConstants.Token, StringComparison.OrdinalIgnoreCase))
                    {
                        this.queryStringToken = this.queryString[key];
                    }
                    else if (key.Equals(HybridConnectionConstants.SasKeyName, StringComparison.OrdinalIgnoreCase))
                    {
                        this.queryStringSasKeyName = this.queryString[key];
                    }
                    else if (key.Equals(HybridConnectionConstants.SasKey, StringComparison.OrdinalIgnoreCase))
                    {
                        this.queryStringSasKey = this.queryString[key];
                    }

                    this.queryString.Remove(key);
                }
            }
        }

        void FilterConnectionHeaders()
        {
            // Remove headers related to auth and headers that strictly relate to the connection with the gateway.
            // Specifically, ALL headers defined or reserved in RFC7230, except Via, are stripped
            NameValueCollection headers = this.listenerContext.Request.Headers;
            headers.Remove("Connection");
            headers.Remove("Content-Length");
            headers.Remove("Te");
            headers.Remove("Trailer");
            headers.Remove("Transfer-Encoding");
            headers.Remove("Upgrade");
            headers.Remove("Close");
        }

        int? GetContentLength()
        {
            NameValueCollection headers = this.listenerContext.Request.Headers;
            string contentLengthString = headers["Content-Length"];
            if (!string.IsNullOrEmpty(contentLengthString))
            {
                int contentLengthValue;
                if (int.TryParse(contentLengthString, out contentLengthValue) && contentLengthValue >= 0)
                {
                    return contentLengthValue;
                }
            }

            return null;
        }

        async Task<WebSocket> WaitForListenerRendezvousAsync()
        {
            try
            {
                // Wait for the listener to rendezvous with a WebSocket or the overall operation times out
                var completedTask = await Task.WhenAny(this.responseReadyCompletionSource.Task, this.rendezvousCompletionSource.Task);

                // Check to see if the completed Task was cancelled or faulted (Exception)
                await completedTask;

                WebSocket webSocket = await this.rendezvousCompletionSource.Task;
                return webSocket;
            }
            catch (TimeoutException timeoutException)
            {
                throw Log.ThrowingException(
                    new TimeoutException($"The listener did not rendezvous to accept the request within the allotted time {ListenerResponseTimeout}.", timeoutException),
                    this);
            }
        }

        IPEndPoint GetRemoteEndpoint()
        {
            return this.listenerContext.Request.RemoteEndPoint;
        }

        struct ResponseCommandAndStream
        {
            public ResponseCommandAndStream(ListenerCommand.ResponseCommand responseCommand, Stream stream)
            {
                this.ResponseCommand = responseCommand;
                this.Stream = stream;
            }

            public ListenerCommand.ResponseCommand ResponseCommand;
            public Stream Stream;
        }
    }
}