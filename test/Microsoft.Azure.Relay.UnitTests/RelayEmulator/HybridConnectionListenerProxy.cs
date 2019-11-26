using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Azure.Relay.UnitTests
{
    class HybridConnectionListenerProxy : OpenCloseAsyncObject
    {
        public static readonly TimeSpan ConnectOperationTimeout = TimeSpan.FromSeconds(10);
        readonly HttpListenerContext listenerContext;
        WebSocket webSocket;

        public HybridConnectionListenerProxy(RelayEmulator relayService, HttpListenerContext listenerContext, Uri listenAddress, TrackingContext trackingContext)
            : base(trackingContext)
        {
            this.RelayService = relayService;
            this.listenerContext = listenerContext;
            this.ListenAddress = listenAddress;
            this.ListenerUniqueAddress = new Uri(this.ListenAddress + "/" + this.TrackingContext.TrackingId);
        }

        public Uri ListenAddress { get; }

        public Uri ListenerUniqueAddress { get; }

        RelayEmulator RelayService { get; }

        WebSocketCloseStatus? CloseStatus { get; set; }

        string CloseStatusDescription { get; set; }

        protected override async Task OnOpenAsync()
        {
            var webSocketContext = await this.listenerContext.AcceptWebSocketAsync(null);
            this.webSocket = webSocketContext.WebSocket;
        }

        protected override void OnOpened()
        {
            base.OnOpened();
            var receiveTask = Task.Run(() => this.ReceivePumpAsync(CancellationToken.None));
        }

        protected override async Task OnCloseAsync()
        {
            await this.webSocket.CloseAsync(this.CloseStatus ?? WebSocketCloseStatus.Empty, this.CloseStatusDescription, CancellationToken.None);
            this.webSocket.Dispose();
        }

        internal Task SendCommandAsync(ListenerCommand listenerCommand, ArraySegment<byte> buffer)
        {
            using (var commandStream = new MemoryStream())
            {
                listenerCommand.WriteObject(commandStream);
                commandStream.Position = 0;
                var commandBuffer = new ArraySegment<byte>(commandStream.GetBuffer(), 0, (int)commandStream.Length);

                // TODO: use AsyncLock
                lock (this)
                {
                    this.webSocket.SendAsync(commandBuffer, WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
                    if (buffer.Array != null)
                    {
                        this.webSocket.SendAsync(buffer, WebSocketMessageType.Binary, true, CancellationToken.None).GetAwaiter().GetResult();
                    }
                }
            }

            return Task.CompletedTask;
        }

        async Task ReceivePumpAsync(CancellationToken shutdownToken)
        {
            Exception closeException = null;
            try
            {
                using (var commandStream = new MemoryStream())
                {
                    byte[] array = new byte[8 * 1024];
                    bool keepGoing;
                    do
                    {
                        keepGoing = await this.ReceivePumpCoreAsync(shutdownToken, array, commandStream);
                        commandStream.SetLength(0);
                    }
                    while (keepGoing);
                }
            }
            catch (Exception e)
            {
                Log.HandledException(e, this);
                closeException = e;
            }

            await this.CloseAsync(closeException);
        }

        /// <summary>
        /// Listener for and dispatch commands from the on-premises listener.  Any exceptions propagating from this method
        /// result in the listener being disconnected.
        /// </summary>
        /// <returns>A bool indicating whether to continue pumping, true means continue, false means stop.</returns>
        async Task<bool> ReceivePumpCoreAsync(CancellationToken shutdownToken, byte[] array, Stream destinationStream)
        {
            var buffer = new ArraySegment<byte>(array);
            var readResult = await WebSocketUtility.ReadMessageAsync(this.webSocket, buffer, destinationStream, shutdownToken);
            if (readResult.MessageType == WebSocketMessageType.Close)
            {
                // User is closing their listener
                lock (this.ThisLock)
                {
                    if (this.CloseStatus == null)
                    {
                        this.CloseStatus = readResult.CloseStatus;
                        this.CloseStatusDescription = readResult.CloseStatusDescription;
                    }
                }

                await this.CloseAsync(null);
                return false;
            }

            destinationStream.Position = 0;
            Debug.Assert(destinationStream.Length > 0, "Received an empty command.");

            await this.ProcessCommandFromListenerAsync(destinationStream);
            return true;
        }

        async Task ProcessCommandFromListenerAsync(Stream commandStream)
        {
            try
            {
                var listenerCommand = ListenerCommand.ReadObject(commandStream);
                if (listenerCommand != null)
                {
                    if (listenerCommand.RenewToken != null)
                    {
                        // TODO: await this.ProcessRenewTokenAsync(listenerCommand.RenewToken);
                        return;
                    }
                    else if (listenerCommand.Response != null)
                    {
                        await this.ProcessListenerResponseAsync(listenerCommand);
                        return;
                    }
#if DEBUG
                    //else if (listenerCommand.InjectFault != null)
                    //{
                    //    this.ProcessInjectFault(listenerCommand.InjectFault);
                    //    return;
                    //}
#endif // DEBUG
                }

                // If we make it here this is an unrecognized command
                string json;
                commandStream.Position = 0;
                using (var streamReader = new StreamReader(commandStream, Encoding.UTF8, false, HybridConnectionConstants.MaxUnrecognizedJson, true))
                {
                    json = (await streamReader.ReadToEndAsync());
                    if (json.Length > HybridConnectionConstants.MaxUnrecognizedJson)
                    {
                        json = json.Substring(0, HybridConnectionConstants.MaxUnrecognizedJson);
                    }
                }

                Log.Warning(this, $"RelayEmulator: Unknown command from listener. {this.TrackingContext}. Command: '{json}'");
            }
            catch (Exception e)
            {
                Log.HandledException(e, this);
            }
        }

        async Task ProcessListenerResponseAsync(ListenerCommand listenerCommand)
        {
            var trackingContext = TrackingContext.Create(listenerCommand.Response.RequestId + "_G0", this.ListenAddress.AbsolutePath);
            Stream bodyStream = Stream.Null;
            try
            {
                if (listenerCommand.Response.Body)
                {
                    using (var wsStream = new WebSocketMessageStream(this.webSocket, ConnectOperationTimeout))
                    {
                        bodyStream = new MemoryStream();
                        await wsStream.CopyToAsync(bodyStream);
                        bodyStream.Position = 0;
                    }
                }

                this.RelayService.HandleCommand(listenerCommand, bodyStream, trackingContext);
            }
            finally
            {
                bodyStream?.Dispose();
            }
        }
    }
}
