#if !DISABLE_DHT
//
// MessageLoop.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2008 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using MonoTorrent.BEncoding;
using MonoTorrent.Common;
using MonoTorrent.Dht.Listeners;
using MonoTorrent.Dht.Messages;

namespace MonoTorrent.Dht
{
    internal class MessageLoop
    {
        private readonly List<IAsyncResult> activeSends = new List<IAsyncResult>();
        private readonly DhtEngine engine;
        private DateTime lastSent;
        private readonly DhtListener listener;
        private readonly object locker = new object();

        private readonly Queue<KeyValuePair<IPEndPoint, Message>> receiveQueue =
            new Queue<KeyValuePair<IPEndPoint, Message>>();

        private readonly Queue<SendDetails> sendQueue = new Queue<SendDetails>();
        private readonly MonoTorrentCollection<SendDetails> waitingResponse = new MonoTorrentCollection<SendDetails>();

        public MessageLoop(DhtEngine engine, DhtListener listener)
        {
            this.engine = engine;
            this.listener = listener;
            listener.MessageReceived += MessageReceived;
            DhtEngine.MainLoop.QueueTimeout(TimeSpan.FromMilliseconds(5), delegate
            {
                if (engine.Disposed)
                    return false;
                try
                {
                    SendMessage();
                    ReceiveMessage();
                    TimeoutMessage();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Error in DHT main loop:");
                    Debug.WriteLine(ex);
                }

                return !engine.Disposed;
            });
        }

        private bool CanSend
        {
            get
            {
                return activeSends.Count < 5 && sendQueue.Count > 0 &&
                       DateTime.Now - lastSent > TimeSpan.FromMilliseconds(5);
            }
        }

        internal event EventHandler<SendQueryEventArgs> QuerySent;

        private void MessageReceived(byte[] buffer, IPEndPoint endpoint)
        {
            lock (locker)
            {
                // I should check the IP address matches as well as the transaction id
                // FIXME: This should throw an exception if the message doesn't exist, we need to handle this
                // and return an error message (if that's what the spec allows)
                try
                {
                    Message message;
                    if (
                        MessageFactory.TryDecodeMessage(
                            (BEncodedDictionary) BEncodedValue.Decode(buffer, 0, buffer.Length, false), out message))
                        receiveQueue.Enqueue(new KeyValuePair<IPEndPoint, Message>(endpoint, message));
                }
                catch (MessageException ex)
                {
                    Console.WriteLine("Message Exception: {0}", ex);
                    // Caused by bad transaction id usually - ignore
                }
                catch (Exception ex)
                {
                    Console.WriteLine("OMGZERS! {0}", ex);
                    //throw new Exception("IP:" + endpoint.Address.ToString() + "bad transaction:" + e.Message);
                }
            }
        }

        private void RaiseMessageSent(IPEndPoint endpoint, QueryMessage query, ResponseMessage response)
        {
            var h = QuerySent;
            if (h != null)
                h(this, new SendQueryEventArgs(endpoint, query, response));
        }

        private void SendMessage()
        {
            SendDetails? send = null;
            if (CanSend)
                send = sendQueue.Dequeue();

            if (send != null)
            {
                SendMessage(send.Value.Message, send.Value.Destination);
                var details = send.Value;
                details.SentAt = DateTime.UtcNow;
                if (details.Message is QueryMessage)
                    waitingResponse.Add(details);
            }
        }

        internal void Start()
        {
            if (listener.Status != ListenerStatus.Listening)
                listener.Start();
        }

        internal void Stop()
        {
            if (listener.Status != ListenerStatus.NotListening)
                listener.Stop();
        }

        private void TimeoutMessage()
        {
            if (waitingResponse.Count > 0)
            {
                if (DateTime.UtcNow - waitingResponse[0].SentAt > engine.TimeOut)
                {
                    var details = waitingResponse.Dequeue();
                    MessageFactory.UnregisterSend((QueryMessage) details.Message);
                    RaiseMessageSent(details.Destination, (QueryMessage) details.Message, null);
                }
            }
        }

        private void ReceiveMessage()
        {
            if (receiveQueue.Count == 0)
                return;

            var receive = receiveQueue.Dequeue();
            var m = receive.Value;
            var source = receive.Key;
            for (var i = 0; i < waitingResponse.Count; i++)
                if (waitingResponse[i].Message.TransactionId.Equals(m.TransactionId))
                    waitingResponse.RemoveAt(i--);

            try
            {
                var node = engine.RoutingTable.FindNode(m.Id);

                // What do i do with a null node?
                if (node == null)
                {
                    node = new Node(m.Id, source);
                    engine.RoutingTable.Add(node);
                }
                node.Seen();
                m.Handle(engine, node);
                var response = m as ResponseMessage;
                if (response != null)
                {
                    RaiseMessageSent(node.EndPoint, response.Query, response);
                }
            }
            catch (MessageException ex)
            {
                Console.WriteLine("Incoming message barfed: {0}", ex);
                // Normal operation (FIXME: do i need to send a response error message?) 
            }
            catch (Exception ex)
            {
                Console.WriteLine("Handle Error for message: {0}", ex);
                EnqueueSend(new ErrorMessage(ErrorCode.GenericError, "Misshandle received message!"), source);
            }
        }

        private void SendMessage(Message message, IPEndPoint endpoint)
        {
            lastSent = DateTime.Now;
            var buffer = message.Encode();
            listener.Send(buffer, endpoint);
        }

        internal void EnqueueSend(Message message, IPEndPoint endpoint)
        {
            lock (locker)
            {
                if (message.TransactionId == null)
                {
                    if (message is ResponseMessage)
                        throw new ArgumentException("Message must have a transaction id");
                    do
                    {
                        message.TransactionId = TransactionId.NextId();
                    } while (MessageFactory.IsRegistered(message.TransactionId));
                }

                // We need to be able to cancel a query message if we time out waiting for a response
                if (message is QueryMessage)
                    MessageFactory.RegisterSend((QueryMessage) message);

                sendQueue.Enqueue(new SendDetails(endpoint, message));
            }
        }

        internal void EnqueueSend(Message message, Node node)
        {
            EnqueueSend(message, node.EndPoint);
        }

        private struct SendDetails
        {
            public SendDetails(IPEndPoint destination, Message message)
            {
                Destination = destination;
                Message = message;
                SentAt = DateTime.MinValue;
            }

            public readonly IPEndPoint Destination;
            public readonly Message Message;
            public DateTime SentAt;
        }
    }
}

#endif