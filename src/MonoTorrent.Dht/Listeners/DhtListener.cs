#if !DISABLE_DHT
using System.Net;

namespace MonoTorrent.Dht.Listeners
{
    public class DhtListener : UdpListener
    {
        public DhtListener(IPEndPoint endpoint)
            : base(endpoint)
        {
        }

        public event MessageReceived MessageReceived;

        protected override void OnMessageReceived(byte[] buffer, IPEndPoint endpoint)
        {
            var h = MessageReceived;
            if (h != null)
                h(buffer, endpoint);
        }
    }
}

#endif