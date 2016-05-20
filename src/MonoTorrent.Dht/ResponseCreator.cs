using MonoTorrent.BEncoding;

namespace MonoTorrent.Dht.Messages
{
    internal delegate Message ResponseCreator(BEncodedDictionary dictionary, QueryMessage message);
}