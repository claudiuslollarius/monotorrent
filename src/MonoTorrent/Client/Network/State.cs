using MonoTorrent.Common;

namespace MonoTorrent.Client.Network
{
    internal class SendMessageState : ICacheable
    {
        public byte[] Buffer { get; private set; }

        public AsyncIOCallback Callback { get; private set; }

        public object State { get; set; }

        public void Initialise()
        {
            Initialise(null, null, null);
        }

        public SendMessageState Initialise(byte[] buffer, AsyncIOCallback callback, object state)
        {
            Buffer = buffer;
            Callback = callback;
            State = state;
            return this;
        }
    }
}