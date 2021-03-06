#if !DISABLE_DHT
using System.Collections.Generic;
using MonoTorrent.Client;
using MonoTorrent.Dht.Messages;

namespace MonoTorrent.Dht.Tasks
{
    internal class GetPeersTask : Task
    {
        private readonly SortedList<NodeId, NodeId> closestNodes;
        private readonly DhtEngine engine;
        private readonly NodeId infoHash;
        private int activeQueries;

        public GetPeersTask(DhtEngine engine, InfoHash infohash)
            : this(engine, new NodeId(infohash))
        {
        }

        public GetPeersTask(DhtEngine engine, NodeId infohash)
        {
            this.engine = engine;
            infoHash = infohash;
            closestNodes = new SortedList<NodeId, NodeId>(Bucket.MaxCapacity);
            ClosestActiveNodes = new SortedList<NodeId, Node>(Bucket.MaxCapacity*2);
        }

        internal SortedList<NodeId, Node> ClosestActiveNodes { get; }

        public override void Execute()
        {
            if (Active)
                return;

            Active = true;
            DhtEngine.MainLoop.Queue(delegate
            {
                IEnumerable<Node> newNodes = engine.RoutingTable.GetClosest(infoHash);
                foreach (var n in Node.CloserNodes(infoHash, closestNodes, newNodes, Bucket.MaxCapacity))
                    SendGetPeers(n);
            });
        }

        private void SendGetPeers(Node n)
        {
            var distance = n.Id.Xor(infoHash);
            ClosestActiveNodes.Add(distance, n);

            activeQueries++;
            var m = new GetPeers(engine.LocalId, infoHash);
            var task = new SendQueryTask(engine, m, n);
            task.Completed += GetPeersCompleted;
            task.Execute();
        }

        private void GetPeersCompleted(object o, TaskCompleteEventArgs e)
        {
            try
            {
                activeQueries--;
                e.Task.Completed -= GetPeersCompleted;

                var args = (SendQueryEventArgs) e;

                // We want to keep a list of the top (K) closest nodes which have responded
                var target = ((SendQueryTask) args.Task).Target;
                var index = ClosestActiveNodes.Values.IndexOf(target);
                if (index >= Bucket.MaxCapacity || args.TimedOut)
                    ClosestActiveNodes.RemoveAt(index);

                if (args.TimedOut)
                    return;

                var response = (GetPeersResponse) args.Response;

                // Ensure that the local Node object has the token. There may/may not be
                // an additional copy in the routing table depending on whether or not
                // it was able to fit into the table.
                target.Token = response.Token;
                if (response.Values != null)
                {
                    // We have actual peers!
                    engine.RaisePeersFound(infoHash, Peer.Decode(response.Values));
                }
                else if (response.Nodes != null)
                {
                    if (!Active)
                        return;

                    // We got a list of nodes which are closer
                    var newNodes = Node.FromCompactNode(response.Nodes);
                    foreach (var n in Node.CloserNodes(infoHash, closestNodes, newNodes, Bucket.MaxCapacity))
                        SendGetPeers(n);
                }
            }
            finally
            {
                if (activeQueries == 0)
                    RaiseComplete(new TaskCompleteEventArgs(this));
            }
        }

        protected override void RaiseComplete(TaskCompleteEventArgs e)
        {
            if (!Active)
                return;

            Active = false;
            base.RaiseComplete(e);
        }
    }
}

#endif