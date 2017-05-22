// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Sockets;
using Microsoft.AspNetCore.Sockets.Client;
using Microsoft.AspNetCore.Testing.xunit;
using Microsoft.Extensions.Logging;
using Xunit;

using ClientConnection = Microsoft.AspNetCore.Sockets.Client.Connection;
using Microsoft.AspNetCore.SignalR.Tests.Common;
using Xunit.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Microsoft.AspNetCore.SignalR.Tests
{
    [CollectionDefinition(Name)]
    public class EndToEndTestsCollection : ICollectionFixture<ServerFixture>
    {
        public const string Name = "EndToEndTests";
    }

    [Collection(EndToEndTestsCollection.Name)]
    public class EndToEndTests : LoggedTest
    {
        private readonly ServerFixture _serverFixture;

        public EndToEndTests(ServerFixture serverFixture, ITestOutputHelper output) : base(output)
        {
            if (serverFixture == null)
            {
                throw new ArgumentNullException(nameof(serverFixture));
            }

            _serverFixture = serverFixture;
        }

        [ConditionalFact]
        [OSSkipCondition(OperatingSystems.Windows, WindowsVersions.Win7, WindowsVersions.Win2008R2, SkipReason = "No WebSockets Client for this platform")]
        public async Task WebSocketsTest()
        {
            using (StartLog(out var loggerFactory))
            {
                var logger = loggerFactory.CreateLogger<EndToEndTests>();

                const string message = "Hello, World!";
                using (var ws = new ClientWebSocket())
                {
                    string socketUrl = _serverFixture.WebSocketsUrl + "/echo";

                    logger.LogInformation("Connecting WebSocket to {socketUrl}", socketUrl);
                    await ws.ConnectAsync(new Uri(socketUrl), CancellationToken.None).OrTimeout();

                    var bytes = Encoding.UTF8.GetBytes(message);
                    logger.LogInformation("Sending {length} byte frame", bytes.Length);
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Binary, true, CancellationToken.None).OrTimeout();

                    logger.LogInformation("Receiving frame");
                    var buffer = new ArraySegment<byte>(new byte[1024]);
                    var result = await ws.ReceiveAsync(buffer, CancellationToken.None).OrTimeout();
                    logger.LogInformation("Received {length} byte frame", result.Count);

                    Assert.Equal(bytes, buffer.Array.AsSpan().Slice(0, result.Count).ToArray());

                    logger.LogInformation("Closing socket");
                    await ws.CloseAsync(WebSocketCloseStatus.Empty, "", CancellationToken.None).OrTimeout();
                    logger.LogInformation("Closed socket");
                }
            }
        }

        [ConditionalTheory]
        [OSSkipCondition(OperatingSystems.Windows, WindowsVersions.Win7, WindowsVersions.Win2008R2, SkipReason = "No WebSockets Client for this platform")]
        [MemberData(nameof(TransportTypes))]
        public async Task ConnectionCanSendAndReceiveMessages(TransportType transportType)
        {
            using (StartLog(out var loggerFactory, testName: $"ConnectionCanSendAndReceiveMessages_{transportType.ToString()}"))
            {
                var logger = loggerFactory.CreateLogger<EndToEndTests>();

                const string message = "Major Key";
                var baseUrl = _serverFixture.BaseUrl;

                string url = baseUrl + "/echo";
                var connection = new ClientConnection(new Uri(url), loggerFactory);
                try
                {
                    var receiveTcs = new TaskCompletionSource<string>();
                    var closeTcs = new TaskCompletionSource<object>();
                    connection.Received += (data, format) =>
                    {
                        logger.LogInformation("Received {length} byte message", data.Length);
                        receiveTcs.TrySetResult(Encoding.UTF8.GetString(data));
                    };
                    connection.Closed += e =>
                    {
                        logger.LogInformation("Connection closed");
                        if (e != null)
                        {
                            receiveTcs.TrySetException(e);
                            closeTcs.TrySetException(e);
                        }
                        else
                        {
                            receiveTcs.TrySetResult(null);
                            closeTcs.TrySetResult(null);
                        }
                    };

                    logger.LogInformation("Starting connection to {url}", url);
                    await connection.StartAsync(transportType).OrTimeout();
                    logger.LogInformation("Started connection to {url}", url);

                    var bytes = Encoding.UTF8.GetBytes(message);
                    logger.LogInformation("Sending {length} byte message", bytes.Length);
                    await connection.SendAsync(bytes, MessageType.Text).OrTimeout();
                    logger.LogInformation("Sent message", bytes.Length);

                    var receiveData = new ReceiveData();

                    logger.LogInformation("Receiving message");
                    Assert.Equal(message, await receiveTcs.Task.OrTimeout());
                    logger.LogInformation("Completed receive");

                    await closeTcs.Task.OrTimeout();
                }
                finally
                {
                    logger.LogInformation("Disposing Connection");
                    await connection.DisposeAsync().OrTimeout();
                    logger.LogInformation("Disposed Connection");
                }
            }
        }

        public static IEnumerable<object[]> MessageSizesData
        {
            get
            {
                yield return new object[] { new string('A', 5 * 4096) };
                yield return new object[] { new string('A', 1000 * 4096 + 32) };
            }
        }

        [ConditionalTheory]
        [OSSkipCondition(OperatingSystems.Windows, WindowsVersions.Win7, WindowsVersions.Win2008R2, SkipReason = "No WebSockets Client for this platform")]
        [MemberData(nameof(MessageSizesData))]
        public async Task ConnectionCanSendAndReceiveDifferentMessageSizesWebSocketsTransport(string message)
        {
            using (StartLog(out var loggerFactory, testName: $"ConnectionCanSendAndReceiveDifferentMessageSizesWebSocketsTransport_{message.Length}"))
            {
                var logger = loggerFactory.CreateLogger<EndToEndTests>();

                var baseUrl = _serverFixture.BaseUrl;

                string url = baseUrl + "/echo";
                var connection = new ClientConnection(new Uri(url), loggerFactory);
                try
                {
                    var receiveTcs = new TaskCompletionSource<byte[]>();
                    connection.Received += (data, messageType) =>
                    {
                        logger.LogInformation("Received {length} byte message", data.Length);
                        receiveTcs.TrySetResult(data);
                    };

                    logger.LogInformation("Starting connection to {url}", url);
                    await connection.StartAsync(TransportType.WebSockets).OrTimeout();
                    logger.LogInformation("Started connection to {url}", url);

                    var bytes = Encoding.UTF8.GetBytes(message);
                    logger.LogInformation("Sending {length} byte message", bytes.Length);
                    await connection.SendAsync(bytes, MessageType.Text).OrTimeout();
                    logger.LogInformation("Sent message", bytes.Length);

                    var receiveData = new ReceiveData();

                    logger.LogInformation("Receiving message");
                    var receivedData = await receiveTcs.Task.OrTimeout();
                    Assert.Equal(message, Encoding.UTF8.GetString(receivedData));
                    logger.LogInformation("Completed receive");
                }
                finally
                {
                    logger.LogInformation("Disposing Connection");
                    await connection.DisposeAsync().OrTimeout();
                    logger.LogInformation("Disposed Connection");
                }
            }
        }

        public static IEnumerable<object[]> TransportTypes() =>
            new[]
            {
                new object[] { TransportType.WebSockets },
                new object[] { TransportType.ServerSentEvents },
                new object[] { TransportType.LongPolling }
            };
    }
}
