using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Azure.Relay.UnitTests
{
    public class RelayEmulator : IDisposable
    {
        const string ServiceBusAuthorizationHeaderName = "ServiceBusAuthorization";
        readonly int port;
        readonly HttpListener listener;
        bool disposed;

        public RelayEmulator(int port)
        {
            this.port = port;
            this.listener = new HttpListener();
            this.Authorization = new AuthorizationRules();
            this.HybridConnectionDescriptions = new Dictionary<string, HybridConnectionDescription>(StringComparer.OrdinalIgnoreCase);
            this.ConnectedListeners = new Dictionary<Uri, HybridConnectionListenerProxy>();
            this.PendingClients = new Dictionary<Guid, HybridRelayedConnection>();
            this.PendingRequests = new Dictionary<Guid, HybridRequest>();
        }

        public AuthorizationRules Authorization { get; }

        Dictionary<Uri, HybridConnectionListenerProxy> ConnectedListeners { get; }

        Dictionary<string, HybridConnectionDescription> HybridConnectionDescriptions { get; }

        Dictionary<Guid, HybridRelayedConnection> PendingClients { get; }

        Dictionary<Guid, HybridRequest> PendingRequests { get; }

        object ThisLock => this.HybridConnectionDescriptions;

        public void Start()
        {
            listener.Prefixes.Add("https://+:" + this.port + "/");
            listener.Start();

            this.ListenLoopAsyncVoid();
        }

        public void Stop()
        {
            this.Dispose();
        }

        public void CreateHybridConnection(string path)
        {
            this.CreateHybridConnection(new HybridConnectionDescription(path));
        }

        public void CreateHybridConnection(HybridConnectionDescription hybridConnection)
        {
            lock (this.ThisLock)
            {
                this.HybridConnectionDescriptions.Add("/" + hybridConnection.Path + "/", hybridConnection);
            }
        }

        public void Dispose()
        {
            this.Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing && !this.disposed)
            {
                this.listener.Stop();
                this.listener.Close();
                this.disposed = true;
                GC.SuppressFinalize(this);
            }
        }

        async void ListenLoopAsyncVoid()
        {
            while (listener.IsListening)
            {
                try
                {
                    HttpListenerContext context = await listener.GetContextAsync().ConfigureAwait(false);
                    if (!context.Request.IsWebSocketRequest)
                    {
                        this.ProcessHttpRequest(context);
                    }
                    else
                    {
                        this.ProcessWebSocketRequest(context);
                    }
                }
                catch (Exception e)
                {
                    if (listener.IsListening)
                    {
                        Trace.TraceWarning("RelayEmulator: {0}:{1}", e.GetType().Name, e.Message);
                    }
                }
            }

            try
            {
                this.listener.Close();
            }
            catch (Exception e)
            {
                Trace.TraceWarning("RelayEmulator: Error Closing Listener: {0}:{1}", e.GetType().Name, e.Message);
            }
        }

        async void ProcessHttpRequest(HttpListenerContext listenerContext)
        {
            var hybridRequest = new HybridRequest(listenerContext, this);
            try
            {
                lock (this.PendingRequests)
                {
                    this.PendingRequests.Add(hybridRequest.TrackingContext.ActivityId, hybridRequest);
                }

                await hybridRequest.ProcessAsync();
            }
            finally
            {
                lock (this.PendingRequests)
                {
                    this.PendingRequests.Remove(hybridRequest.TrackingContext.ActivityId);
                }
            }
        }

        async void ProcessWebSocketRequest(HttpListenerContext listenerContext)
        {
            string action = string.Empty;
            Uri endpointUri;
            var trackingContext = GetTrackingContextAndAddress(listenerContext, out endpointUri);
            try
            {
                action = GetAction(listenerContext);
                TryGetHybridConnectionDescription(endpointUri, out HybridConnectionDescription description);
                switch (action)
                {
                    case HybridConnectionConstants.Actions.Accept:
                        await this.AcceptHybridConnectionAsync(listenerContext, endpointUri, trackingContext);
                        break;
                    case HybridConnectionConstants.Actions.Connect:
                        await this.InitiateConnectionAsync(listenerContext, description, endpointUri, trackingContext);
                        break;
                    case HybridConnectionConstants.Actions.Listen:
                        await this.RegisterListenerAsync(listenerContext, description, endpointUri, trackingContext);
                        break;
                    case HybridConnectionConstants.Actions.Request:
                        await this.AcceptHybridRequestAsync(listenerContext, description, endpointUri, trackingContext);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported action '{action}'");
                }
            }
            catch (Exception e)
            {
                Log.HandledException(e, this);
                SendHttpErrorResponse(listenerContext, e, trackingContext);
            }
        }

        bool TryGetHybridConnectionDescription(Uri endpointUri, out HybridConnectionDescription description)
        {
            string endpointPath = endpointUri.AbsolutePath;
            if (!endpointPath.EndsWith("/", StringComparison.OrdinalIgnoreCase))
            {
                endpointPath += "/";
            }

            lock (this.ThisLock)
            {
                foreach (KeyValuePair<string, HybridConnectionDescription> kvp in this.HybridConnectionDescriptions)
                {
                    if (endpointPath.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        description = kvp.Value;
                        return true;
                    }
                }
            }

            description = null;
            return false;
        }

        void SendHttpErrorResponse(
            HttpListenerContext listenerContext,
            Exception e,
            TrackingContext trackingContext)
        {
            HttpStatusCode statusCode;
            string description;

            if (e is AuthorizationFailedException)
            {
                statusCode = HttpStatusCode.Unauthorized;
                description = e.Message;
            }
            else if (e is EndpointNotFoundException)
            {
                statusCode = HttpStatusCode.NotFound;
                description = e.Message;
            }
            else if (e is InvalidOperationException)
            {
                statusCode = HttpStatusCode.BadRequest;
                description = e.Message;
            }
            else if (e is TimeoutException)
            {
                statusCode = HttpStatusCode.RequestTimeout;
                description = e.Message;
            }
            else
            {
                statusCode = HttpStatusCode.InternalServerError;
                description = HttpStatusCode.InternalServerError.ToString();
            }

            try
            {
                listenerContext.Response.StatusCode = (int)statusCode;
                listenerContext.Response.StatusDescription = description;
                listenerContext.Response.Close();
            }
            catch (Exception ex)
            {
                Log.HandledException(ex, this);
            }
        }


        static TrackingContext GetTrackingContextAndAddress(HttpListenerContext listenerContext, out Uri endpointUri)
        {
            string id = listenerContext.Request.QueryString[HybridConnectionConstants.Id];
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString();
            }

            string path = listenerContext.Request.Url.AbsolutePath;
            if (path.StartsWith("/$hc", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(4);
            }

            string query = FilterQueryString(listenerContext.Request.QueryString);
            string host = listenerContext.Request.Url.Host;
            endpointUri = new UriBuilder { Scheme = "sb", Host = host, Path = path, Query = query }.Uri;
            var trackingContext = TrackingContext.Create(id + "_G0", endpointUri.GetLeftPart(UriPartial.Path));

            // Flow the TrackingId (including "_GX" suffix) back to the client.
            listenerContext.Response.Headers.Add("TrackingId", trackingContext.TrackingId);

            return trackingContext;
        }

        internal void HandleCommand(ListenerCommand listenerCommand, Stream bodyStream, TrackingContext trackingContext)
        {
            try
            {
                // Find the CloudHybridRequest instance, hand it this websocket.
                HybridRequest hybridRequest;
                lock (this.PendingRequests)
                {
                    if (!this.PendingRequests.TryGetValue(trackingContext.ActivityId, out hybridRequest))
                    {
                        throw Log.ThrowingException(
                            new InvalidOperationException($"Unknown Connection ID: {trackingContext}. Is the listener accepting the request too late?"),
                            this);
                    }

                    this.PendingRequests.Remove(trackingContext.ActivityId);
                }

                hybridRequest.SetResponseCommandAndStream(listenerCommand.Response, bodyStream);
            }
            catch (Exception e)
            {
                Log.Warning(this, $"Sender Gateway: Accepting of the HybridRequest failed. {e}");
            }
        }

        /// <summary>
        /// Filters out any query string values which start with the 'sb-hc-' prefix.  The returned string never
        /// has a '?' character at the start.
        /// </summary>
        static string FilterQueryString(NameValueCollection queryString)
        {
            var sb = new StringBuilder(256);
            foreach (string key in queryString.AllKeys)
            {
                if (key == null || key.StartsWith(HybridConnectionConstants.QueryStringKeyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append('&');
                }

                sb.Append(WebUtility.UrlEncode(key)).Append('=').Append(WebUtility.UrlEncode(queryString[key]));
            }

            return sb.ToString();
        }

        static string GetAction(HttpListenerContext listenerContext)
        {
            string action = listenerContext.Request.QueryString[HybridConnectionConstants.Action];
            if (string.IsNullOrEmpty(action))
            {
                action = HybridConnectionConstants.Actions.Connect;
            }

            return action;
        }

        async Task RegisterListenerAsync(HttpListenerContext listenerContext, HybridConnectionDescription description, Uri endpointUri, TrackingContext trackingContext)
        {
            Log.Info(this, $"RelayEmulator: RegisterListener - Start. Endpoint: {endpointUri}");

            // Get Token and authorize
            var token = GetToken(listenerContext.Request.Headers, listenerContext.Request.QueryString);
            AuthorizeRequest(endpointUri, token, AccessRights.Listen, description?.Authorization, trackingContext);

            if (description == null)
            {
                throw Log.ThrowingException(new EndpointNotFoundException($"Endpoint does not exist. {trackingContext}"), this);
            }

            // Register this listener
            var listener = new HybridConnectionListenerProxy(this, listenerContext, endpointUri, trackingContext);

            listener.Closing += (s, e) =>
            {
                lock (this.ConnectedListeners)
                {
                    this.ConnectedListeners.Remove(listener.ListenerUniqueAddress);
                }
            };

            lock (this.ConnectedListeners)
            {
                this.ConnectedListeners.Add(listener.ListenerUniqueAddress, listener);
            }

            await listener.OpenAsync();

            Log.Info(this, $"RelayEmulator: RegisterListener - Succeeded. Endpoint: {listener.ListenerUniqueAddress}");
        }

        async Task AcceptHybridConnectionAsync(HttpListenerContext listenerContext, Uri endpointUri, TrackingContext trackingContext)
        {
            Log.Info(this, $"RelayEmulator: Listener accepted connection.  Uri: {trackingContext.Address}, ConnectionId: {trackingContext.TrackingId}.");

            var acceptHeaders = listenerContext.Request.Headers;
            FlowSecWebSocketExtension(acceptHeaders, listenerContext.Response.Headers);

            HybridRelayedConnection hybridRelayedConnection;
            lock (this.PendingClients)
            {
                if (!this.PendingClients.TryGetValue(trackingContext.ActivityId, out hybridRelayedConnection))
                {
                    throw Log.ThrowingException(
                        new InvalidOperationException($"Unknown Connection ID: {trackingContext}. Is the listener accepting the connection too late?"),
                        this);
                }

                this.PendingClients.Remove(trackingContext.ActivityId);
            }

            string statusCodeString = listenerContext.Request.QueryString[HybridConnectionConstants.StatusCode];
            if (string.IsNullOrEmpty(statusCodeString))
            {
                await hybridRelayedConnection.AcceptAsync(listenerContext);
            }
            else
            {
                // Listener has rejected the websocket upgrade.
                int statusCode;
                if (!int.TryParse(statusCodeString, out statusCode) || statusCode < 100 || statusCode > 999)
                {
                    throw Log.ThrowingException(
                        new InvalidOperationException(HybridConnectionConstants.StatusCode + " must be an integer between 100 and 999."),
                        this);
                }

                string statusDescription = listenerContext.Request.QueryString[HybridConnectionConstants.StatusDescription];
                hybridRelayedConnection.Reject(statusCode, statusDescription, acceptHeaders);

                // When the listener rejects a client rendezvous we always close the listener data channel with "410 Gone"
                listenerContext.Response.StatusCode = (int)HttpStatusCode.Gone;
                listenerContext.Response.StatusDescription = "Gone";
                listenerContext.Response.Close();
            }
        }

        async Task InitiateConnectionAsync(HttpListenerContext clientContext, HybridConnectionDescription description, Uri endpointUri, TrackingContext trackingContext)
        {
            Log.Info(this, $"RelayEmulator: A new sender is connecting: {trackingContext}.");

            if (description == null || description.RequiresClientAuthorization)
            {
                var token = GetToken(clientContext.Request.Headers, clientContext.Request.QueryString);
                AuthorizeRequest(endpointUri, token, AccessRights.Send, description?.Authorization, trackingContext);
            }

            if (description == null)
            {
                throw Log.ThrowingException(new EndpointNotFoundException($"Endpoint does not exist. {trackingContext}"), this);
            }

            var hybridRelayedConnection = new HybridRelayedConnection(clientContext, endpointUri, trackingContext);
            try
            {
                lock (this.PendingClients)
                {
                    this.PendingClients.Add(trackingContext.ActivityId, hybridRelayedConnection);
                }

                var acceptCommand = CreateAcceptCommand(hybridRelayedConnection);
                await this.SendToListenerClientAsync(hybridRelayedConnection.TrackingContext.Address, acceptCommand, trackingContext);
                await hybridRelayedConnection.WaitForAcceptAsync();

                if (hybridRelayedConnection.RejectedStatusCode == null)
                {
                    await hybridRelayedConnection.CompleteConnectAsync();
                    Log.Info(this, $"RelayEmulator: Succeeded adding sender: Endpoint: {trackingContext.Address}");
                }
                else
                {
                    // Listener has explicitly rejected this connection attempt.
                    FlowSecWebSocketExtension(hybridRelayedConnection.AcceptHeaders, clientContext.Response.Headers);
                    int statusCode = hybridRelayedConnection.RejectedStatusCode.Value;
                    string statusDescription = hybridRelayedConnection.RejectedStatusDescription;
                    Log.Warning(this, $"RelayEmulator: sender was rejected: {statusCode}: {statusDescription}; {trackingContext}.");

                    clientContext.Response.StatusCode = statusCode;
                    if (!string.IsNullOrEmpty(hybridRelayedConnection.RejectedStatusDescription))
                    {
                        clientContext.Response.StatusDescription = hybridRelayedConnection.RejectedStatusDescription;
                    }

                    clientContext.Response.Close();
                }
            }
            finally
            {
                lock (this.PendingClients)
                {
                    this.PendingClients.Remove(trackingContext.ActivityId);
                }
            }
        }

        async Task AcceptHybridRequestAsync(HttpListenerContext listenerContext, HybridConnectionDescription description, Uri endpointUri, TrackingContext trackingContext)
        {
            Log.Info(this, $"Sender Gateway: Listener accepting hybrid request.  Uri: {trackingContext.Address}, ConnectionId: {trackingContext.TrackingId}.");
            try
            {
                // Find the CloudHybridRequest instance, hand it this websocket.
                HybridRequest hybridRequest;
                lock (this.PendingRequests)
                {
                    if (!this.PendingRequests.TryGetValue(trackingContext.ActivityId, out hybridRequest))
                    {
                        throw Log.ThrowingException(
                            new InvalidOperationException($"Unknown Connection ID: {trackingContext}. Is the listener accepting the request too late?"),
                            this);
                    }

                    this.PendingRequests.Remove(trackingContext.ActivityId);
                }

                await hybridRequest.AcceptRendezvousAsync(listenerContext, trackingContext);
            }
            catch (Exception e)
            {
                Log.Warning(this, $"Sender Gateway: Accepting of the HybridRequest failed. {e}");
            }
        }

        internal async Task SendToListenerClientAsync(string address, ListenerCommand listenerCommand, TrackingContext trackingContext, ArraySegment<byte> buffer = default)
        {
            var endpointListeners = new List<HybridConnectionListenerProxy>();
            lock (this.ConnectedListeners)
            {
                foreach (var kvp in this.ConnectedListeners)
                {
                    if (address.Contains(kvp.Value.ListenAddress.AbsoluteUri))
                    {
                        endpointListeners.Add(kvp.Value);
                    }
                }
            }

            if (endpointListeners.Count == 0)
            {
                // TODO: No listeners connected
                throw Log.ThrowingException(new EndpointNotFoundException($"Endpoint not found. {trackingContext}"), this);
            }

            int randomIndex = new Random().Next(0, endpointListeners.Count);            
            await endpointListeners[randomIndex].SendCommandAsync(listenerCommand, buffer);
        }

        void AuthorizeRequest(Uri uri, SecurityToken token, AccessRights requiredRight, AuthorizationRules entityAuthorizationRules, TrackingContext trackingContext)
        {
            //    // Check the Namespace Level rules first
            //    foreach (AuthorizationRule rule in this.Authorization)
            //    {
            //        if (rule is SharedAccessAuthorizationRule sasRule &&
            //            string.Equals(sasRule.KeyName, sasToken.KeyName, StringComparison.OrdinalIgnoreCase))
            //        {
            //            if (!sasRule.Rights.Contains(requiredRight))
            //            {
            //                throw Log.ThrowingException(new AuthorizationFailedException($"{requiredRight} claim is required. {trackingContext}"), this);
            //            }

            //        }
            //    }

            //    // TODO: Check the endpoint level rules
            //    ////if (endpointRules != null) {
            //    ////    foreach (AuthorizationRule rule in endpointRules)


            if (token == null)
            {
                throw Log.ThrowingException(new AuthorizationFailedException($"RelaySecurityTokenRequired. {trackingContext}"), this);
            }

            IEnumerable<AccessRights> rightsAllowed = this.ValidateUserToken(token, uri, entityAuthorizationRules, trackingContext);
            if (!rightsAllowed.Contains(requiredRight))
            {
                throw Log.ThrowingException(new AuthorizationFailedException($"Authorization failed for specified action: {requiredRight}. {trackingContext}"), this);
            }
        }

        IEnumerable<AccessRights> ValidateUserToken(SecurityToken securityToken, Uri uri, AuthorizationRules entityAuthorizationRules, TrackingContext trackingContext)
        {
            uri = uri ?? throw new ArgumentNullException("uri");

            if (!(securityToken is SharedAccessSignatureToken sasToken))
            {
                throw Log.ThrowingException(new AuthorizationFailedException($"Unable to process security token. {trackingContext}"), this);
            }
            else
            {
                string audience = TokenProvider.NormalizeAudience(uri.AbsoluteUri);
                IEnumerable<AccessRights> actions = this.ValidateSharedAccessKeySignatureAndAudience(sasToken, audience, entityAuthorizationRules, trackingContext);
                return actions;
            }
        }

        IEnumerable<AccessRights> ValidateSharedAccessKeySignatureAndAudience(
            SharedAccessSignatureToken sasToken,
            string audienceUri,
            AuthorizationRules entityAuthorizationRules,
            TrackingContext trackingContext)
        {
            //SharedAccessSignature signature;
            //signature = SharedAccessSignature.Parse(sasToken.Signature);
            IEnumerable<AccessRights> actions = null;
            AuthorizationFailedException authorizationFailedException = null;

            // TODO: First try using the entity level SAS rule to validate the incoming signature.
            ////try
            ////{
            ////    if (entityAuthorizationRules != null)
            ////        entityAuthorizationRules.TryGetSharedAccessAuthorizationRule(signature.KeyName, out sharedAccessAuthorizationRule))
            ////    {
            ////        actions = this.ValidateSharedAccessKeySignatureAndTransformToClaimCollectionHelper(
            ////            signature,
            ////            key,
            ////            audienceUri,
            ////            sharedAccessAuthorizationRule);
            ////    }
            ////}
            ////catch (AuthorizationFailedException ex)
            ////{
            ////    // If there is a validation failure, store the exception as we may potentially return this to the user.
            ////    authorizationFailedException = ex;
            ////}

            // A null collection means that either: 
            //     1) The entity level SAS rule doesn't exist,
            //     2) It exists, but the incorrect key was used to generate the signature.
            if (actions == null)
            {
                // Now try using the namespace level SAS rule to validate the incoming signature.
                foreach (AuthorizationRule rule in this.Authorization)
                {
                    if (rule is SharedAccessAuthorizationRule sasRule &&
                        string.Equals(rule.KeyName, sasToken.KeyName, StringComparison.OrdinalIgnoreCase))
                    {
                        ValidateSharedAccessSignature(sasToken, sasRule, trackingContext);
                        //signature.ValidateAccessTo(audienceUri);
                        actions = sasRule.Rights;
                        break;
                    }
                }
            }

            // A collection that is still null means that the SAS rule doesn't exist at the namespace level either.
            if (actions == null)
            {
                // If there was an error validating against the entity-level SAS rule, we might as well
                // return the original exception we encountered. Otherwise, we will fall through to the
                // bottom of this method and return a generic AuthorizationFailedException
                if (authorizationFailedException != null)
                {
                    throw Log.ThrowingException(authorizationFailedException, this);
                }

                throw Log.ThrowingException(new AuthorizationFailedException($"The token has an invalid signature. {trackingContext}"), this);
            }

            return actions;
        }

        static void ValidateSharedAccessSignature(SharedAccessSignatureToken token, SharedAccessAuthorizationRule authorizationRule, TrackingContext trackingContext)
        {
            if (token == null)
            {
                throw new AuthorizationFailedException($"The token is missing. {trackingContext}");
            }
            else if (token.ExpiresAtUtc < DateTime.UtcNow.Add(TimeSpan.FromMinutes(1)))
            {
                throw new AuthorizationFailedException($"The token is expired. Expiration time: '{token.ExpiresAtUtc.ToString("u", CultureInfo.InvariantCulture)}'. {trackingContext}");
            }

            if (token.Signature != SharedAccessSignatureToken.ComputeSignature(token.RawExpiry, token.Audience, authorizationRule.PrimaryKey))
            {
                if (authorizationRule.SecondaryKey == null ||
                    token.Signature != SharedAccessSignatureToken.ComputeSignature(token.RawExpiry, token.Audience, authorizationRule.SecondaryKey))
                {
                    throw new AuthorizationFailedException($"The token has an invalid signature. {trackingContext}");
                }
            }
        }

        ListenerCommand CreateAcceptCommand(HybridRelayedConnection relayedConnection)
        {
            // Create JSON payload
            var listenerCommand = new ListenerCommand { Accept = new ListenerCommand.AcceptCommand() };
            var tempUri = new Uri($"wss://localhost:{this.port}/{relayedConnection.Address.PathAndQuery}");
            var acceptUri = BuildUri(
                tempUri.Host,
                tempUri.Port,
                tempUri.AbsolutePath,
                tempUri.Query,
                HybridConnectionConstants.Actions.Accept,
                relayedConnection.TrackingContext.TrackingId);
            listenerCommand.Accept.Address = acceptUri.AbsoluteUri;
            listenerCommand.Accept.Id = relayedConnection.TrackingContext.TrackingId;

            // Process any HTTP Headers from the initial connect message
            foreach (var headerName in relayedConnection.ConnectHeaders.AllKeys)
            {
                listenerCommand.Accept.ConnectHeaders.Add(headerName, relayedConnection.ConnectHeaders[headerName]);
            }

            listenerCommand.Accept.RemoteEndpoint.Address = relayedConnection.ConnectRemoteEndPoint.Address.ToString();
            listenerCommand.Accept.RemoteEndpoint.Port = relayedConnection.ConnectRemoteEndPoint.Port;

            return listenerCommand;
        }

        /// <summary>
        /// Build the websocket uri for use with HybridConnection WebSockets.
        /// Results in a Uri such as "wss://HOST:PORT/$hc/PATH?QUERY&amp;sb-hc-action=listen&amp;sb-hc-id=ID"
        /// </summary>
        /// <param name="host">The host name (required).</param>
        /// <param name="port">The port (-1 is allowed).</param>
        /// <param name="path">The hybridConnection path.</param>
        /// <param name="query">An optional query string.</param>
        /// <param name="action">The action (listen|connect|accept).</param>
        /// <param name="id">The tracking id.</param>
        public static Uri BuildUri(string host, int port, string path, string query, string action, string id)
        {
            if (!path.StartsWith("/", StringComparison.Ordinal))
            {
                path = "/" + path;
            }

            query = BuildQueryString(query, action, id);

            return new UriBuilder
            {
                Scheme = HybridConnectionConstants.SecureWebSocketScheme,
                Host = host,
                Port = port,
                Path = HybridConnectionConstants.HybridConnectionRequestUri + path,
                Query = query
            }.Uri;
        }

        /// <summary>
        /// Builds a query string, e.g. "existing=stuff_here&amp;sb-hc-action=listen&amp;sb-hc-id=TRACKING_ID"
        /// </summary>
        static string BuildQueryString(string existingQueryString, string action, string id)
        {
            // Add enough extra buffer for our &sb-hc-action=connect&sb-hc-id=00000000-0000-0000-0000-000000000000_GXX_GYY
            const int RequiredLength = 80;
            var buffer = new StringBuilder(existingQueryString.Length + RequiredLength);

            if (!string.IsNullOrEmpty(existingQueryString))
            {
                existingQueryString = existingQueryString.TrimStart('?');
                buffer.Append(existingQueryString);
                if (buffer.Length > 0)
                {
                    buffer.Append("&");
                }
            }

            buffer.Append(HybridConnectionConstants.Action).Append('=').Append(action).Append('&').Append(HybridConnectionConstants.Id).Append('=').Append(id);
            return buffer.ToString();
        }

        internal static void FlowSecWebSocketExtension(NameValueCollection sourceHeaders, NameValueCollection destinationHeaders)
        {
            string secWebSocketExtensions = sourceHeaders[HybridConnectionConstants.Headers.SecWebSocketExtensions];
            if (!string.IsNullOrEmpty(secWebSocketExtensions))
            {
                destinationHeaders[HybridConnectionConstants.Headers.SecWebSocketExtensions] = secWebSocketExtensions;
            }
        }

        static SecurityToken GetToken(NameValueCollection headers, NameValueCollection queryString)
        {
            SecurityToken token = null;
            string tokenString = headers[ServiceBusAuthorizationHeaderName];
            if (!string.IsNullOrEmpty(tokenString))
            {
                token = new SharedAccessSignatureToken(tokenString);
                headers.Remove(ServiceBusAuthorizationHeaderName);
            }
            else
            {
                tokenString = queryString[HybridConnectionConstants.Token];
                if (!string.IsNullOrEmpty(tokenString))
                {
                    token = new SharedAccessSignatureToken(tokenString);
                }
            }

            return token;
        }
    }
}
