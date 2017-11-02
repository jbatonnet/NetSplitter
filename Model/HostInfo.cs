using System;

namespace NetSplitter
{
    public class HostInfo : IEquatable<HostInfo>
    {
        public string Hostname { get; }
        public ushort Port { get; }

        public HostInfo(string hostname, ushort port)
        {
            Hostname = hostname;
            Port = port;
        }

        public bool Equals(HostInfo other)
        {
            return Equals((object)other);
        }
        public override bool Equals(object obj)
        {
            HostInfo other = obj as HostInfo;
            return other != null && other.Hostname == Hostname && other.Port == Port;
        }
        public override int GetHashCode()
        {
            return Hostname.GetHashCode() ^ Port.GetHashCode();
        }

        public override string ToString() => $"{Hostname}:{Port}";
    }
}
