using System;
using System.Collections.Generic;

namespace NetSplitter
{
    public abstract class Splitter
    {
        public abstract event EventHandler<HostInfo> HostConnected;
        public abstract event EventHandler<HostInfo> HostDisconnected;

        protected Func<HostInfo, HostInfo> targetBalancer;
        protected Func<HostInfo, IEnumerable<HostInfo>> targetCloner;

        protected Splitter(Func<HostInfo, HostInfo> targetBalancer, Func<HostInfo, IEnumerable<HostInfo>> targetCloner)
        {
            this.targetBalancer = targetBalancer;
            this.targetCloner = targetCloner;

            if (targetBalancer == null || targetCloner == null)
                throw new ArgumentNullException();
        }

        public abstract void Stop();
        public abstract void Start();
    }
}
