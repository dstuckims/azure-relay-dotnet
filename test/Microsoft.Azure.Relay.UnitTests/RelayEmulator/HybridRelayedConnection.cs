using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Azure.Relay.UnitTests
{
    class HybridRelayedConnection
    {
        const int ReceiveBufferSize = 32 * 1024;
        static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(60);
        static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(60);
        readonly HttpListenerContext clientContext;
        readonly TaskCompletionSource<object> acceptedTcs;
        WebSocket initiatorWebSocket;
        WebSocket acceptorWebSocket;

        public HybridRelayedConnection(HttpListenerContext clientContext, Uri address, TrackingContext trackingContext)
        {
            this.clientContext = clientContext;
            this.acceptedTcs = new TaskCompletionSource<object>(this);
            this.Address = address;
            this.TrackingContext = trackingContext;
        }

        public Uri Address { get; }

        public NameValueCollection AcceptHeaders { get; private set; }

        public NameValueCollection ConnectHeaders => this.clientContext.Request.Headers;

        public IPEndPoint ConnectRemoteEndPoint => this.clientContext.Request.RemoteEndPoint;

        public int? RejectedStatusCode { get; private set; }

        public string RejectedStatusDescription { get; private set; }

        public TrackingContext TrackingContext { get; }

        public void Reject(int statusCode, string statusDescription, NameValueCollection acceptHeaders)
        {
            this.RejectedStatusCode = statusCode;
            this.RejectedStatusDescription = statusDescription;
            this.AcceptHeaders = acceptHeaders;

            this.acceptedTcs.SetResult(null);
        }

        public override string ToString()
        {
            return $"{this.GetType().Name}({this.TrackingContext})";
        }

        internal Task WaitForAcceptAsync()
        {
            return this.acceptedTcs.Task;
        }

        public async Task AcceptAsync(HttpListenerContext listenerContext)
        {
            this.AcceptHeaders = listenerContext.Request.Headers;
            string subProtocol = this.AcceptHeaders[HybridConnectionConstants.Headers.SecWebSocketProtocol];
            var webSocketContext = await listenerContext.AcceptWebSocketAsync(subProtocol);

            acceptedTcs.SetResult(null);

            // Once both initiator and acceptor are set this will start the pumping from initiatingWebSocket <-> acceptingWebSocket
            var acceptingWebSocket = webSocketContext.WebSocket;
            this.TryCompleteOpen(null, acceptingWebSocket);
        }

        public async Task CompleteConnectAsync()
        {
            RelayEmulator.FlowSecWebSocketExtension(this.AcceptHeaders, clientContext.Response.Headers);
            string subProtocol = this.AcceptHeaders[HybridConnectionConstants.Headers.SecWebSocketProtocol];
            var webSocketContext = await clientContext.AcceptWebSocketAsync(subProtocol);

            // Once both initiator and acceptor are set this will start the pumping from initiatingWebSocket <-> acceptingWebSocket
            var initiatingWebSocket = webSocketContext.WebSocket;
            this.TryCompleteOpen(initiatingWebSocket, null);
        }

        async void TryCompleteOpen(WebSocket initiatingWebSocket, WebSocket acceptingWebSocket)
        {
            lock (this)
            {
                if (initiatingWebSocket != null)
                {
                    Debug.Assert(this.initiatorWebSocket == null);
                    this.initiatorWebSocket = initiatingWebSocket;
                }
                else if (acceptingWebSocket != null)
                {
                    Debug.Assert(this.acceptorWebSocket == null);
                    this.acceptorWebSocket = acceptingWebSocket;
                }

                if (this.acceptorWebSocket == null || this.initiatorWebSocket == null)
                {
                    // Not done yet.
                    return;
                }
            }

            Log.Info(this, $"Ready");

            try
            {
                // Kick off pumping in both directions, then block this state machine until both have stopped receiving more data
                await Task.WhenAll(
                    this.WebSocketPumpAsync("Upstream", this.initiatorWebSocket, this.acceptorWebSocket),
                    this.WebSocketPumpAsync("Downstream", this.acceptorWebSocket, this.initiatorWebSocket));

                // Now that both sides are done receiving data Close both WebSockets
                await this.CloseWebSocketsAsync();
            }
            catch (Exception exception)
            {
                Log.HandledException(exception, this);
                this.AbortWebSockets();
            }
            finally
            {
                Log.Info(this, $"Closed");
            }
        }

        async Task WebSocketPumpAsync(string direction, WebSocket sourceWebSocket, WebSocket destinationWebSocket)
        {
            try
            {
                byte[] buffer = new byte[ReceiveBufferSize];

                // Pump from source to destination while no exceptions occur
                while (true)
                {
                    WebSocketReceiveResult receiveResult;
                    try
                    {
                        receiveResult = await sourceWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    }
                    catch (Exception receiveException)
                    {
                        // Receive Exception means the source websocket threw an exception therefore we need to notify the destination
                        Log.HandledException(receiveException, $"{this}.{nameof(WebSocketPumpAsync)}(receive {direction})");
                        destinationWebSocket.Abort();
                        break;
                    }

                    try
                    {
                        using (var cancelTokenSource = new CancellationTokenSource(SendTimeout))
                        {
                            if (receiveResult.MessageType != WebSocketMessageType.Close)
                            {
                                await destinationWebSocket.SendAsync(
                                    new ArraySegment<byte>(buffer, 0, receiveResult.Count),
                                    receiveResult.MessageType,
                                    receiveResult.EndOfMessage,
                                    cancelTokenSource.Token);
                            }
                            else
                            {
                                // TODO: Start a timer for the case where the remote side doesn't continue the graceful shutdown process
                                ////this.shutdownCancelTokenSource.CancelAfter(CloseTimeout);

                                await destinationWebSocket.CloseOutputAsync(
                                    receiveResult.CloseStatus ?? WebSocketCloseStatus.Empty,
                                    receiveResult.CloseStatusDescription,
                                    cancelTokenSource.Token);
                                break;
                            }
                        }
                    }
                    catch (Exception sendException)
                    {
                        // Send Exception means the destination WebSocket threw an exception, therefore we need to notify the source
                        Log.HandledException(sendException, $"{this}.{nameof(WebSocketPumpAsync)}(send {direction})");
                        sourceWebSocket.Abort();
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                Log.HandledException(exception, $"{this}.{nameof(WebSocketPumpAsync)}(generic {direction})");
            }
        }
        async Task CloseWebSocketsAsync()
        {
            try
            {
                var closeStatus = this.initiatorWebSocket.CloseStatus ?? WebSocketCloseStatus.NormalClosure;
                var closeDescription = this.initiatorWebSocket.CloseStatusDescription ?? "Normal Closure";
                using (var cancelTokenSource = new CancellationTokenSource(CloseTimeout))
                {
                    await Task.WhenAll(
                        this.initiatorWebSocket.CloseAsync(closeStatus, closeDescription, cancelTokenSource.Token),
                        this.acceptorWebSocket.CloseAsync(closeStatus, closeDescription, cancelTokenSource.Token));

                    this.Cleanup();
                }
            }
            catch (Exception exception)
            {
                Log.HandledException(exception, this);
                this.AbortWebSockets();
            }
        }

        void AbortWebSockets()
        {
            this.initiatorWebSocket?.Abort();
            this.acceptorWebSocket?.Abort();
            this.Cleanup();
        }

        void Cleanup()
        {
            if (this.initiatorWebSocket != null)
            {
                this.initiatorWebSocket.Dispose();
                this.initiatorWebSocket = null;
            }

            if (this.acceptorWebSocket != null)
            {
                this.acceptorWebSocket.Dispose();
                this.acceptorWebSocket = null;
            }
        }
    }
}