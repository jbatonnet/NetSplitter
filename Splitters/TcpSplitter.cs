using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NetSplitter
{
    public class TargetConnection
    {
        public HostInfo Target { get; }
        public TcpClient Connection { get; }
        public NetworkStream Stream { get; }

        public ConcurrentQueue<ArraySegment<byte>> Buffers { get; } = new ConcurrentQueue<ArraySegment<byte>>();

        public TargetConnection(HostInfo target, TcpClient connection, NetworkStream stream)
        {
            Target = target;
            Connection = connection;
            Stream = stream;
        }
    }
    public class ClientConnection
    {
        public HostInfo ClientInfo { get; }
        public TcpClient Connection { get; }
        public NetworkStream Stream { get; }

        public TargetConnection MainTarget { get; }
        public Dictionary<HostInfo, TargetConnection> OutputTargets { get; }

        public ClientConnection(HostInfo clientInfo, TcpClient connection, NetworkStream stream, TargetConnection mainTarget, Dictionary<HostInfo, TargetConnection> outputTargets)
        {
            ClientInfo = clientInfo;
            Connection = connection;
            Stream = stream;
            MainTarget = mainTarget;
            OutputTargets = outputTargets;
        }
    }

    public class TcpSplitter : Splitter
    {
        private const int bufferSize = 32 * 1024;

        private static readonly DefaultLogger logger = new DefaultLogger();

        public ushort Port { get; }

        public override event EventHandler<HostInfo> HostConnected;
        public override event EventHandler<HostInfo> HostDisconnected;

        private TcpListener tcpListener;
        private bool running = false;
        private List<ClientConnection> activeConnections = new List<ClientConnection>();

        public TcpSplitter(ushort port, Func<HostInfo, HostInfo> targetBalancer, Func<HostInfo, IEnumerable<HostInfo>> targetCloner) : base(targetBalancer, targetCloner)
        {
            Port = port;

            tcpListener = new TcpListener(IPAddress.Any, port);
        }

        public override void Start()
        {
            tcpListener.Start();

            running = true;
            tcpListener.BeginAcceptTcpClient(OnTcpConnection, null);
        }
        public override void Stop()
        {
            running = false;
            tcpListener.Stop();
        }

        private void OnTcpConnection(IAsyncResult asyncResult)
        {
            if (!running)
                return;

            try
            {
                // Accept client
                TcpClient client = tcpListener.EndAcceptTcpClient(asyncResult);

                IPEndPoint clientEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
                HostInfo clientInfo = new HostInfo(clientEndPoint.Address.ToString(), (ushort)clientEndPoint.Port);

                new Thread(() =>
                {
                    // Connect to main server
                    HostInfo mainTargetInfo = targetBalancer(clientInfo);
                    if (mainTargetInfo == null)
                        return;

                    TargetConnection mainTarget;

                    HostConnected?.Invoke(this, clientInfo);

                    try
                    {
                        TcpClient mainTargetConnection = new TcpClient(mainTargetInfo.Hostname, mainTargetInfo.Port);
                        mainTarget = new TargetConnection(mainTargetInfo, mainTargetConnection, mainTargetConnection.GetStream());
                    }
                    catch (Exception e)
                    {
                        logger.Warn($"Could not connect to output {mainTargetInfo.Hostname}:{mainTargetInfo.Port}. Skipping connection. {e}");

                        HostDisconnected?.Invoke(this, clientInfo);
                        return;
                    }

                    Dictionary<HostInfo, TargetConnection> outputTargets = targetCloner(clientInfo).ToDictionary<HostInfo, HostInfo, TargetConnection>(t => t, t => null);

                    ClientConnection clientConnection = new ClientConnection(clientInfo, client, client.GetStream(), mainTarget, outputTargets);
                    activeConnections.Add(clientConnection);

                    // Connect to output targets
                    foreach (var outputInfo in clientConnection.OutputTargets.ToArray())
                    {
                        try
                        {
                            TcpClient outputClient = new TcpClient();

                            Task connectTask = outputClient.ConnectAsync(outputInfo.Key.Hostname, outputInfo.Key.Port);
                            if (!connectTask.Wait(Program.Timeout))
                            {
                                logger.Warn($"Could not connect to output {outputInfo.Key.Hostname}:{outputInfo.Key.Port} after {Program.Timeout}. Output will be skipped");
                                continue;
                            }

                            TargetConnection outputConnection = new TargetConnection(outputInfo.Key, outputClient, outputClient.GetStream());
                            clientConnection.OutputTargets[outputInfo.Key] = outputConnection;
                        }
                        catch (Exception e)
                        {
                            logger.Warn($"Could not connect to output {outputInfo.Key.Hostname}:{outputInfo.Key.Port}. Output will be skipped. {e}");
                        }
                    }

                    // Client > Target
                    new Thread(() =>
                    {
                        Exception exception = null;

                        try
                        {
                            byte[] buffer = new byte[bufferSize];

                            while (true)
                            {
                                int read = clientConnection.Stream.Read(buffer, 0, buffer.Length);
                                if (read == 0)
                                    break;

                                foreach (var targetInfo in outputTargets.ToArray())
                                {
                                    if (targetInfo.Value == null)
                                        continue;

                                    ulong totalSize = (ulong)targetInfo.Value.Buffers.Count * bufferSize + bufferSize;
                                    if (totalSize > Program.BufferSize)
                                    {
                                        logger.Warn($"Output target {targetInfo.Key.Hostname}:{targetInfo.Key.Port} has reach its maximum buffer size. Output will be skipped");

                                        outputTargets[targetInfo.Key] = null;
                                        Try(targetInfo.Value.Connection.Close);

                                        continue;
                                    }

                                    targetInfo.Value.Buffers.Enqueue(new ArraySegment<byte>(buffer, 0, read));
                                }

                                mainTarget.Stream.Write(buffer, 0, read);
                                mainTarget.Stream.Flush();

                                buffer = new byte[bufferSize];
                            }
                        }
                        catch (Exception e)
                        {
                            exception = e;
                        }
                        finally
                        {
                            Try(mainTarget.Connection.Close);

                            foreach (TargetConnection targetConnection in outputTargets.Values)
                                if (targetConnection != null)
                                    Try(targetConnection.Connection.Close);

                            lock (activeConnections)
                            {
                                if (activeConnections.Remove(clientConnection))
                                {
                                    if (exception != null)
                                    {
                                        if (!(exception is IOException) || !(exception.InnerException is SocketException))
                                            logger.Warn("Exception while reading from client. " + exception);
                                    }

                                    HostDisconnected?.Invoke(this, clientConnection.ClientInfo);
                                }
                            }
                        }
                    }).Start();

                    // Target > Client
                    new Thread(() =>
                    {
                        Exception exception = null;

                        try
                        {
                            byte[] buffer = new byte[bufferSize];

                            while (true)
                            {
                                int read = mainTarget.Stream.Read(buffer, 0, buffer.Length);
                                if (read == 0)
                                    break;

                                clientConnection.Stream.Write(buffer, 0, read);
                                clientConnection.Stream.Flush();
                            }
                        }
                        catch (Exception e)
                        {
                            exception = e;
                        }
                        finally
                        {
                            Try(client.Close);

                            foreach (TargetConnection targetConnection in outputTargets.Values)
                                if (targetConnection != null)
                                    Try(targetConnection.Connection.Close);

                            lock (activeConnections)
                            {
                                if (activeConnections.Remove(clientConnection))
                                {
                                    if (exception != null)
                                    {
                                        if (!(exception is IOException) || !(exception.InnerException is SocketException))
                                            logger.Warn("Exception while reading from target. " + exception);
                                    }

                                    HostDisconnected?.Invoke(this, clientConnection.ClientInfo);
                                }
                            }
                        }
                    }).Start();

                    // Flush output target connections
                    byte[] readBuffer = new byte[bufferSize];

                    while (true)
                    {
                        int outputTargetCount = 0;
                        int dequeuedBuffers = 0;

                        foreach (var outputInfo in clientConnection.OutputTargets.ToArray())
                        {
                            if (outputInfo.Value == null)
                                continue;

                            outputTargetCount++;

                            try
                            {
                                ArraySegment<byte> buffer;
                                while (outputInfo.Value.Buffers.TryDequeue(out buffer))
                                {
                                    dequeuedBuffers++;

                                    outputInfo.Value.Stream.Write(buffer.Array, buffer.Offset, buffer.Count);
                                    outputInfo.Value.Stream.Flush();
                                }

                                while (outputInfo.Value.Stream.DataAvailable)
                                    outputInfo.Value.Stream.Read(readBuffer, 0, readBuffer.Length);
                            }
                            catch
                            {
                                logger.Warn($"Could not send buffer to output target {outputInfo.Key.Hostname}:{outputInfo.Key.Port}. Output will be skipped");

                                outputTargets[outputInfo.Key] = null;
                                Try(outputInfo.Value.Connection.Close);

                                continue;
                            }
                        }

                        if (outputTargetCount == 0)
                            break;

                        if (dequeuedBuffers == 0)
                            Thread.Sleep(10);
                    }
                }).Start();
            }
            catch (Exception e)
            {
                logger.Warn($"Error while processing TCP client. " + e);
            }

            tcpListener.BeginAcceptTcpClient(OnTcpConnection, null);
        }

        private static bool Try(Action action)
        {
            try
            {
                action();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
