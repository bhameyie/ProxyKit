﻿using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProxyKit
{
    public class WebSocketProxyMiddleware
    {
        private readonly string _urlPath;
        private readonly Func<HttpContext, Uri> _chooseUri;

        private static readonly HashSet<string> NotForwardedWebSocketHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Connection", "Host", "Upgrade", "Sec-WebSocket-Accept",
            "Sec-WebSocket-Protocol", "Sec-WebSocket-Key", "Sec-WebSocket-Version",
            "Sec-WebSocket-Extensions"
        };
        private const int DefaultWebSocketBufferSize = 4096;
        private readonly RequestDelegate _next;
        private readonly ProxyOptions _options;
        private readonly Uri _destinationUri;
        private readonly ILogger<WebSocketProxyMiddleware> _logger;

        private WebSocketProxyMiddleware(
            RequestDelegate next,
            IOptionsMonitor<ProxyOptions> options,
            ILogger<WebSocketProxyMiddleware> logger)
        {
            _next = next;
            _options = options.CurrentValue;
            _logger = logger;
        }

        public WebSocketProxyMiddleware(
            RequestDelegate next,
            IOptionsMonitor<ProxyOptions> options,
            Uri destinationUri,
            ILogger<WebSocketProxyMiddleware> logger) : this(next, options, logger)
        {
            _destinationUri = destinationUri;
        }

        public WebSocketProxyMiddleware(
                   RequestDelegate next,
                   IOptionsMonitor<ProxyOptions> options,
                   string urlPath,
                   Func<HttpContext, Uri> chooseUri,
                   ILogger<WebSocketProxyMiddleware> logger) : this(next, options, logger)
        {
            _urlPath = urlPath;
            _chooseUri = chooseUri;
        }

        public async Task Invoke(HttpContext context)
        {
			if (context.WebSockets.IsWebSocketRequest)
			{
				await ProxyOutToWebSocket(context).ConfigureAwait(false);
			}
            else
            {
                await _next(context).ConfigureAwait(false);
            }
        }

		private Task ProxyOutToWebSocket(HttpContext context)
		{
			if (_urlPath == null) return AcceptProxyWebSocketRequest(context, _destinationUri);

			if (!context.Request.Path.StartsWithSegments(_urlPath)) return _next(context);

			var relativePath = context.Request.Path.ToString();
			var uri = new Uri(_chooseUri(context), 
							relativePath.Length >= _urlPath.Length ? relativePath.Substring(_urlPath.Length) : "");
			return AcceptProxyWebSocketRequest(context, uri);
		}

		private async Task AcceptProxyWebSocketRequest(HttpContext context, Uri destinationUri)
        {
            using (var client = new ClientWebSocket())
            {
                foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols)
                {
                    client.Options.AddSubProtocol(protocol);
                }

                foreach (var headerEntry in context.Request.Headers)
                {
                    if (!NotForwardedWebSocketHeaders.Contains(headerEntry.Key))
                    {
                        client.Options.SetRequestHeader(headerEntry.Key, headerEntry.Value);
                    }
                }

                if (_options.WebSocketKeepAliveInterval.HasValue)
                {
                    client.Options.KeepAliveInterval = _options.WebSocketKeepAliveInterval.Value;
                }

                try
                {
                    await client.ConnectAsync(destinationUri, context.RequestAborted).ConfigureAwait(false);
                }
                catch (WebSocketException ex)
                {
                    context.Response.StatusCode = 400;
                    _logger.LogError(ex, "Error connecting to server");
                    return;
                }

                using (var server = await context.WebSockets.AcceptWebSocketAsync(client.SubProtocol).ConfigureAwait(false))
                {
                    var bufferSize = _options.WebSocketBufferSize ?? DefaultWebSocketBufferSize;
                    await Task.WhenAll(
                        PumpWebSocket(client, server, bufferSize, context.RequestAborted),
                        PumpWebSocket(server, client, bufferSize, context.RequestAborted)).ConfigureAwait(false);
                }
            }
        }

        private static async Task PumpWebSocket(WebSocket source, WebSocket destination, int bufferSize, CancellationToken cancellationToken)
        {
            if (bufferSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferSize));
            }

            var buffer = new byte[bufferSize];
            while (true)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await source.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await destination.CloseOutputAsync(
                        WebSocketCloseStatus.EndpointUnavailable,
                        "Endpoind unavailable",
                        cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await destination
                        .CloseOutputAsync(source.CloseStatus.Value, source.CloseStatusDescription, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                await destination.SendAsync(
                    new ArraySegment<byte>(buffer, 0, result.Count),
                    result.MessageType,
                    result.EndOfMessage,
                    cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}