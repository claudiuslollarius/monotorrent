using MonoTorrent.BEncoding;

namespace MonoTorrent.Dht.Messages
{
    internal delegate Message Creator(BEncodedDictionary dictionary);
}