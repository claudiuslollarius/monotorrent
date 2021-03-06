using System;
using System.Collections.Generic;
using MonoTorrent.Client.Messages;
using MonoTorrent.Client.Messages.Standard;
using MonoTorrent.Common;

namespace MonoTorrent.Client
{
    // Keep a list of all the pieces which have not yet being fully downloaded
    // From this list we will make requests for all the blocks until the piece is complete.
    public class EndGamePicker : PiecePicker
    {
        private static readonly Predicate<Request> TimedOut = delegate(Request r) { return r.Block.RequestTimedOut; };
        private static Predicate<Request> NotRequested = delegate(Request r) { return r.Block.RequestedOff == null; };

        // These are all the requests for the individual blocks
        private readonly List<Request> requests;

        // This list stores all the pieces which have not yet been completed. If a piece is *not* in this list
        // we don't need to download it.
        private List<Piece> pieces;

        public EndGamePicker()
            : base(null)
        {
            requests = new List<Request>();
        }

        // Cancels a pending request when the predicate returns 'true'
        private void CancelWhere(Predicate<Request> predicate, bool sendCancel)
        {
            for (var i = 0; i < requests.Count; i++)
            {
                var r = requests[i];
                if (predicate(r))
                {
                    r.Peer.AmRequestingPiecesCount--;
                    if (sendCancel)
                        r.Peer.Enqueue(new CancelMessage(r.Block.PieceIndex, r.Block.StartOffset, r.Block.RequestLength));
                }
            }
            requests.RemoveAll(predicate);
        }

        public override void CancelTimedOutRequests()
        {
            CancelWhere(TimedOut, false);
        }

        public override RequestMessage ContinueExistingRequest(PeerId peer)
        {
            return null;
        }

        public override int CurrentRequestCount()
        {
            return requests.Count;
        }

        public override List<Piece> ExportActiveRequests()
        {
            return new List<Piece>(pieces);
        }

        public override void Initialise(BitField bitfield, TorrentFile[] files, IEnumerable<Piece> requests)
        {
            // 'Requests' should contain a list of all the pieces we need to complete
            pieces = new List<Piece>(requests);
            foreach (var piece in pieces)
            {
                for (var i = 0; i < piece.BlockCount; i++)
                    if (piece.Blocks[i].RequestedOff != null)
                        this.requests.Add(new Request(piece.Blocks[i].RequestedOff, piece.Blocks[i]));
            }
        }

        public override bool IsInteresting(BitField bitfield)
        {
            return !bitfield.AllFalse;
        }

        public override MessageBundle PickPiece(PeerId id, BitField peerBitfield, List<PeerId> otherPeers, int count,
            int startIndex, int endIndex)
        {
            // Only request 2 pieces at a time in endgame mode
            // to prevent a *massive* overshoot
            if (id.IsChoking || id.AmRequestingPiecesCount > 2)
                return null;

            LoadPieces(id, peerBitfield);

            // 1) See if there are any blocks which have not been requested at all. Request the block if the peer has it
            foreach (var p in pieces)
            {
                if (!peerBitfield[p.Index] || p.AllBlocksRequested)
                    continue;

                for (var i = 0; i < p.BlockCount; i++)
                {
                    if (p.Blocks[i].Requested)
                        continue;
                    p.Blocks[i].Requested = true;
                    var request = new Request(id, p.Blocks[i]);
                    requests.Add(request);
                    return new MessageBundle(request.Block.CreateRequest(id));
                }
            }

            // 2) For each block with an existing request, add another request. We do a search from the start
            //    of the list to the end. So when we add a duplicate request, move both requests to the end of the list
            foreach (var p in pieces)
            {
                if (!peerBitfield[p.Index])
                    continue;

                for (var i = 0; i < p.BlockCount; i++)
                {
                    if (p.Blocks[i].Received || AlreadyRequested(p.Blocks[i], id))
                        continue;

                    var c = requests.Count;
                    for (var j = 0; j < requests.Count - 1 && (c-- > 0); j++)
                    {
                        if (requests[j].Block.PieceIndex == p.Index &&
                            requests[j].Block.StartOffset == p.Blocks[i].StartOffset)
                        {
                            var r = requests[j];
                            requests.RemoveAt(j);
                            requests.Add(r);
                            j--;
                        }
                    }
                    p.Blocks[i].Requested = true;
                    var request = new Request(id, p.Blocks[i]);
                    requests.Add(request);
                    return new MessageBundle(request.Block.CreateRequest(id));
                }
            }

            return null;
        }

        private void LoadPieces(PeerId id, BitField b)
        {
            var length = b.Length;
            for (var i = b.FirstTrue(0, length); i != -1; i = b.FirstTrue(i + 1, length))
                if (!pieces.Exists(delegate(Piece p) { return p.Index == i; }))
                    pieces.Add(new Piece(i, id.TorrentManager.Torrent.PieceLength, id.TorrentManager.Torrent.Size));
        }

        private bool AlreadyRequested(Block block, PeerId id)
        {
            var b = requests.Exists(delegate(Request r)
            {
                return r.Block.PieceIndex == block.PieceIndex &&
                       r.Block.StartOffset == block.StartOffset &&
                       r.Peer == id;
            });
            return b;
        }

        public override void Reset()
        {
            // Though if you reset an EndGamePicker it really means that you should be using a regular picker now
            requests.Clear();
        }

        public override void CancelRequest(PeerId peer, int piece, int startOffset, int length)
        {
            CancelWhere(delegate(Request r)
            {
                return r.Block.PieceIndex == piece &&
                       r.Block.StartOffset == startOffset &&
                       r.Block.RequestLength == length &&
                       peer.Equals(r.Peer);
            }, false);
        }

        public override void CancelRequests(PeerId peer)
        {
            CancelWhere(delegate(Request r) { return r.Peer == peer; }, false);
        }

        public override bool ValidatePiece(PeerId peer, int pieceIndex, int startOffset, int length, out Piece piece)
        {
            foreach (var r in requests)
            {
                // When we get past this block, it means we've found a valid request for this piece
                if (r.Block.PieceIndex != pieceIndex || r.Block.StartOffset != startOffset ||
                    r.Block.RequestLength != length || r.Peer != peer)
                    continue;

                // All the other requests for this block need to be cancelled.
                foreach (var p in pieces)
                {
                    if (p.Index != pieceIndex)
                        continue;

                    CancelWhere(delegate(Request req)
                    {
                        return req.Block.PieceIndex == pieceIndex &&
                               req.Block.StartOffset == startOffset &&
                               req.Block.RequestLength == length &&
                               req.Peer != peer;
                    }, true);

                    // Mark the block as received
                    p.Blocks[startOffset/Piece.BlockSize].Received = true;

                    // Once a piece is completely received, remove it from our list.
                    // If a piece *fails* the hashcheck, we need to add it back into the list so
                    // we download it again.
                    if (p.AllBlocksReceived)
                        pieces.Remove(p);

                    requests.Remove(r);
                    piece = p;
                    peer.AmRequestingPiecesCount--;
                    return true;
                }
            }

            // The request was not valid
            piece = null;
            return false;
        }

        // Struct to link a request for a block to a peer
        // This way we can have multiple requests for the same block
        private class Request
        {
            public readonly PeerId Peer;
            public Block Block;

            public Request(PeerId peer, Block block)
            {
                Peer = peer;
                Block = block;
            }
        }
    }
}