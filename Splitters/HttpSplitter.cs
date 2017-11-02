using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using log4net;

namespace NetSplitter
{
    public class HttpSplitter : Splitter
    {
        private const int bufferSize = 32 * 1024;

        private static readonly ILog logger = new DefaultLogger(); // LogManager.GetLogger(typeof(HttpSplitter));

        public ushort Port { get; }

        public override event EventHandler<HostInfo> HostConnected;
        public override event EventHandler<HostInfo> HostDisconnected;

        private HttpListener httpListener;
        private bool running = false;
        private List<ClientConnection> activeConnections = new List<ClientConnection>();

        public HttpSplitter(ushort port, Func<HostInfo, HostInfo> targetBalancer, Func<HostInfo, IEnumerable<HostInfo>> targetCloner) : base(targetBalancer, targetCloner)
        {
            throw new NotImplementedException();

            Port = port;

            httpListener = new HttpListener();
            httpListener.Prefixes.Add($"http://+:{port}/");
        }

        public override void Start()
        {
            httpListener.Start();

            running = true;
            httpListener.BeginGetContext(OnHttpConnection, null);
        }
        public override void Stop()
        {
            running = false;
            httpListener.Stop();
        }

        private void OnHttpConnection(IAsyncResult asyncResult)
        {
            if (!running)
                return;

            try
            {
                // Accept client
                HttpListenerContext context = httpListener.EndGetContext(asyncResult);

                IPEndPoint clientEndPoint = context.Request.RemoteEndPoint;
                HostInfo clientInfo = new HostInfo(clientEndPoint.Address.ToString(), (ushort)clientEndPoint.Port);

                new Thread(() =>
                {
                    // Connect to main server
                    HostInfo mainTargetInfo = targetBalancer(clientInfo);
                    if (mainTargetInfo == null)
                        return;

                    HostConnected?.Invoke(this, clientInfo);

                    // Create a HttpWebRequest
                    Func<HostInfo, HttpWebRequest> requestBuilder = (HostInfo target) =>
                    {
                        Uri uri = new Uri(new Uri($"http://{target.Hostname}:{target.Port}"), context.Request.Url);
                        HttpWebRequest request = WebRequest.CreateHttp(uri);

                        foreach (string key in context.Request.Headers.Keys)
                            request.Headers.Add(key, context.Request.Headers[key]);

                        return request;
                    };

                    requestBuilder(mainTargetInfo);
                });
            }
            catch { }
        }
    }
}
