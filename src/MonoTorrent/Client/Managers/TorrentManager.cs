using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MonoTorrent.Client.Tracker;
using MonoTorrent.Common;

namespace MonoTorrent.Client
{
    public class TorrentManager : IDisposable, IEquatable<TorrentManager>
    {
        internal void HandlePeerConnected(PeerId id, Direction direction)
        {
            // The only message sent/received so far is the Handshake message.
            // The current mode decides what additional messages need to be sent.
            Mode.HandlePeerConnected(id, direction);
            RaisePeerConnected(new PeerConnectionEventArgs(this, id, direction));
        }

        #region Events

        public event EventHandler<PeerConnectionEventArgs> PeerConnected;

        public event EventHandler<PeerConnectionEventArgs> PeerDisconnected;

        internal event EventHandler<PeerConnectionFailedEventArgs> ConnectionAttemptFailed;

        public event EventHandler<PeersAddedEventArgs> PeersFound;

        public event EventHandler<PieceHashedEventArgs> PieceHashed;

        public event EventHandler<TorrentStateChangedEventArgs> TorrentStateChanged;

        internal event EventHandler<PeerAddedEventArgs> OnPeerFound;

        #endregion

        #region Member Variables

        private bool disposed;
        internal Queue<int> finishedPieces; // The list of pieces which we should send "have" messages for
        private int hashFails; // The total number of pieces receieved which failed the hashcheck
        internal bool isInEndGame = false; // Set true when the torrent enters end game processing
        private Mode mode;

        // Tracks all the piece requests we've made and decides what pieces we can request off each peer

        private readonly string torrentSave; // The path where the .torrent data will be saved when in metadata mode
        internal IUnchoker chokeUnchoker; // Used to choke and unchoke peers
        internal DateTime lastCalledInactivePeerManager = DateTime.Now;
#if !DISABLE_DHT
        private bool dhtInitialised;
#endif

        #endregion Member Variables

        #region Properties

        public BitField Bitfield { get; internal set; }

        public bool CanUseDht
        {
            get { return Settings.UseDht && (Torrent == null || !Torrent.IsPrivate); }
        }

        public bool Complete
        {
            get { return Bitfield.AllTrue; }
        }

        internal RateLimiterGroup DownloadLimiter { get; private set; }

        public ClientEngine Engine { get; internal set; }

        public Error Error { get; internal set; }

        internal Mode Mode
        {
            get { return mode; }
            set
            {
                var oldMode = mode;
                mode = value;
                if (oldMode != null)
                    RaiseTorrentStateChanged(new TorrentStateChangedEventArgs(this, oldMode.State, mode.State));
                mode.Tick(0);
            }
        }

        public int PeerReviewRoundsComplete
        {
            get
            {
                if (chokeUnchoker is ChokeUnchokeManager)
                    return ((ChokeUnchokeManager) chokeUnchoker).ReviewsExecuted;
                return 0;
            }
        }


        public bool HashChecked { get; internal set; }

        public int HashFails
        {
            get { return hashFails; }
        }

        public bool HasMetadata
        {
            get { return Torrent != null; }
        }

        /// <summary>
        ///     True if this torrent has activated special processing for the final few pieces
        /// </summary>
        public bool IsInEndGame
        {
            get { return State == TorrentState.Downloading && isInEndGame; }
        }

        public ConnectionMonitor Monitor { get; private set; }


        /// <summary>
        ///     The number of peers that this torrent instance is connected to
        /// </summary>
        public int OpenConnections
        {
            get { return Peers.ConnectedPeers.Count; }
        }


        /// <summary>
        /// </summary>
        public PeerManager Peers { get; private set; }


        /// <summary>
        ///     The piecemanager for this TorrentManager
        /// </summary>
        public PieceManager PieceManager { get; internal set; }


        /// <summary>
        ///     The inactive peer manager for this TorrentManager
        /// </summary>
        internal InactivePeerManager InactivePeerManager { get; private set; }


        /// <summary>
        ///     The current progress of the torrent in percent
        /// </summary>
        public double Progress
        {
            get { return Bitfield.PercentComplete; }
        }


        /// <summary>
        ///     The directory to download the files to
        /// </summary>
        public string SavePath { get; private set; }


        /// <summary>
        ///     The settings for with this TorrentManager
        /// </summary>
        public TorrentSettings Settings { get; }


        /// <summary>
        ///     The current state of the TorrentManager
        /// </summary>
        public TorrentState State
        {
            get { return mode.State; }
        }


        /// <summary>
        ///     The time the torrent manager was started at
        /// </summary>
        public DateTime StartTime { get; private set; }


        /// <summary>
        ///     The tracker connection associated with this TorrentManager
        /// </summary>
        public TrackerManager TrackerManager { get; private set; }


        /// <summary>
        ///     The Torrent contained within this TorrentManager
        /// </summary>
        public Torrent Torrent { get; internal set; }


        /// <summary>
        ///     The number of peers that we are currently uploading to
        /// </summary>
        public int UploadingTo { get; internal set; }

        internal RateLimiterGroup UploadLimiter { get; private set; }

        public bool IsInitialSeeding
        {
            get { return Mode is InitialSeedingMode; }
        }

        /// <summary>
        ///     Number of peers we have inactivated for this torrent
        /// </summary>
        public int InactivePeers
        {
            get { return InactivePeerManager.InactivePeers; }
        }

        public InfoHash InfoHash { get; }

        /// <summary>
        ///     List of peers we have inactivated for this torrent
        /// </summary>
        public List<Uri> InactivePeerList
        {
            get { return InactivePeerManager.InactivePeerList; }
        }

        #endregion

        #region Constructors

        /// <summary>
        ///     Creates a new TorrentManager instance.
        /// </summary>
        /// <param name="torrent">The torrent to load in</param>
        /// <param name="savePath">The directory to save downloaded files to</param>
        /// <param name="settings">The settings to use for controlling connections</param>
        public TorrentManager(Torrent torrent, string savePath, TorrentSettings settings)
            : this(torrent, savePath, settings, torrent.Files.Length == 1 ? "" : torrent.Name)
        {
        }

        /// <summary>
        ///     Creates a new TorrentManager instance.
        /// </summary>
        /// <param name="torrent">The torrent to load in</param>
        /// <param name="savePath">The directory to save downloaded files to</param>
        /// <param name="settings">The settings to use for controlling connections</param>
        /// <param name="baseDirectory">
        ///     In the case of a multi-file torrent, the name of the base directory containing the files.
        ///     Defaults to Torrent.Name
        /// </param>
        public TorrentManager(Torrent torrent, string savePath, TorrentSettings settings, string baseDirectory)
        {
            Check.Torrent(torrent);
            Check.SavePath(savePath);
            Check.Settings(settings);
            Check.BaseDirectory(baseDirectory);

            Torrent = torrent;
            InfoHash = torrent.infoHash;
            Settings = settings;

            Initialise(savePath, baseDirectory, torrent.AnnounceUrls);
            ChangePicker(CreateStandardPicker());
        }


        public TorrentManager(InfoHash infoHash, string savePath, TorrentSettings settings, string torrentSave,
            IList<RawTrackerTier> announces)
        {
            Check.InfoHash(infoHash);
            Check.SavePath(savePath);
            Check.Settings(settings);
            Check.TorrentSave(torrentSave);
            Check.Announces(announces);

            InfoHash = infoHash;
            Settings = settings;
            this.torrentSave = torrentSave;

            Initialise(savePath, "", announces);
        }

        public TorrentManager(MagnetLink magnetLink, string savePath, TorrentSettings settings, string torrentSave)
        {
            Check.MagnetLink(magnetLink);
            Check.InfoHash(magnetLink.InfoHash);
            Check.SavePath(savePath);
            Check.Settings(settings);
            Check.TorrentSave(torrentSave);

            InfoHash = magnetLink.InfoHash;
            Settings = settings;
            this.torrentSave = torrentSave;
            IList<RawTrackerTier> announces = new RawTrackerTiers();
            if (magnetLink.AnnounceUrls != null)
                announces.Add(magnetLink.AnnounceUrls);
            Initialise(savePath, "", announces);
        }

        private void Initialise(string savePath, string baseDirectory, IList<RawTrackerTier> announces)
        {
            Bitfield = new BitField(HasMetadata ? Torrent.Pieces.Count : 1);
            SavePath = Path.Combine(savePath, baseDirectory);
            finishedPieces = new Queue<int>();
            Monitor = new ConnectionMonitor();
            InactivePeerManager = new InactivePeerManager(this);
            Peers = new PeerManager();
            PieceManager = new PieceManager();
            TrackerManager = new TrackerManager(this, InfoHash, announces);

            Mode = new StoppedMode(this);
            CreateRateLimiters();

            PieceHashed +=
                delegate(object o, PieceHashedEventArgs e) { PieceManager.UnhashedPieces[e.PieceIndex] = false; };

            if (HasMetadata)
            {
                foreach (var file in Torrent.Files)
                    file.FullPath = Path.Combine(SavePath, file.Path);
            }
        }

        private void CreateRateLimiters()
        {
            var downloader = new RateLimiter();
            DownloadLimiter = new RateLimiterGroup();
            DownloadLimiter.Add(new PauseLimiter(this));
            DownloadLimiter.Add(downloader);

            var uploader = new RateLimiter();
            UploadLimiter = new RateLimiterGroup();
            UploadLimiter.Add(new PauseLimiter(this));
            UploadLimiter.Add(uploader);
        }

        #endregion

        #region Public Methods

        public void ChangePicker(PiecePicker picker)
        {
            Check.Picker(picker);

            ClientEngine.MainLoop.QueueWait(
                delegate { PieceManager.ChangePicker(picker, Bitfield, Torrent.Files); });
        }

        public void Dispose()
        {
            disposed = true;
        }


        /// <summary>
        ///     Overrridden. Returns the name of the torrent.
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return Torrent == null ? "<Metadata Mode>" : Torrent.Name;
        }


        /// <summary>
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        public override bool Equals(object obj)
        {
            var m = obj as TorrentManager;
            return m == null ? false : Equals(m);
        }


        /// <summary>
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public bool Equals(TorrentManager other)
        {
            return other == null ? false : InfoHash == other.InfoHash;
        }

        public List<Piece> GetActiveRequests()
        {
            return
                (List<Piece>)
                    ClientEngine.MainLoop.QueueWait(
                        delegate { return PieceManager.Picker.ExportActiveRequests(); });
        }

        /// <summary>
        /// </summary>
        /// <returns></returns>
        public override int GetHashCode()
        {
            return InfoHash.GetHashCode();
        }

        public List<PeerId> GetPeers()
        {
            return
                (List<PeerId>)
                    ClientEngine.MainLoop.QueueWait(
                        delegate { return new List<PeerId>(Peers.ConnectedPeers); });
        }

        /// <summary>
        ///     Starts a hashcheck. If forceFullScan is false, the library will attempt to load fastresume data
        ///     before performing a full scan, otherwise fast resume data will be ignored and a full scan will be started
        /// </summary>
        /// <param name="forceFullScan">True if a full hash check should be performed ignoring fast resume data</param>
        public void HashCheck(bool autoStart)
        {
            ClientEngine.MainLoop.QueueWait(delegate
            {
                if (!Mode.CanHashCheck)
                    throw new TorrentException(
                        string.Format("A hashcheck can only be performed when the manager is stopped. State is: {0}",
                            State));

                CheckRegisteredAndDisposed();
                StartTime = DateTime.Now;
                Mode = new HashingMode(this, autoStart);
                Engine.Start();
            });
        }

        public void MoveFile(TorrentFile file, string path)
        {
            Check.File(file);
            Check.PathNotEmpty(path);
            CheckRegisteredAndDisposed();
            CheckMetadata();

            if (State != TorrentState.Stopped)
                throw new TorrentException("Cannot move files when the torrent is active");

            Engine.DiskManager.MoveFile(this, file, path);
        }

        public void MoveFiles(string newRoot, bool overWriteExisting)
        {
            CheckRegisteredAndDisposed();
            CheckMetadata();

            if (State != TorrentState.Stopped)
                throw new TorrentException("Cannot move files when the torrent is active");

            Engine.DiskManager.MoveFiles(this, newRoot, overWriteExisting);
            SavePath = newRoot;
        }

        /// <summary>
        ///     Pauses the TorrentManager
        /// </summary>
        public void Pause()
        {
            ClientEngine.MainLoop.QueueWait(delegate
            {
                CheckRegisteredAndDisposed();
                if (State != TorrentState.Downloading && State != TorrentState.Seeding)
                    return;

                // By setting the state to "paused", peers will not be dequeued from the either the
                // sending or receiving queues, so no traffic will be allowed.
                Mode = new PausedMode(this);
                SaveFastResume();
            });
        }


        /// <summary>
        ///     Starts the TorrentManager
        /// </summary>
        public void Start()
        {
            ClientEngine.MainLoop.QueueWait(delegate
            {
                CheckRegisteredAndDisposed();

                Engine.Start();
                // If the torrent was "paused", then just update the state to Downloading and forcefully
                // make sure the peers begin sending/receiving again
                if (State == TorrentState.Paused)
                {
                    Mode = new DownloadMode(this);
                    return;
                }

                if (!HasMetadata)
                {
                    Mode = new MetadataMode(this, torrentSave);
#if !DISABLE_DHT
                    StartDHT();
#endif
                    return;
                }

                VerifyHashState();
                // If the torrent has not been hashed, we start the hashing process then we wait for it to finish
                // before attempting to start again
                if (!HashChecked)
                {
                    if (State != TorrentState.Hashing)
                        HashCheck(true);
                    return;
                }

                if (State == TorrentState.Seeding || State == TorrentState.Downloading)
                    return;

                if (TrackerManager.CurrentTracker != null)
                {
                    if (TrackerManager.CurrentTracker.CanScrape)
                        TrackerManager.Scrape();
                    TrackerManager.Announce(TorrentEvent.Started); // Tell server we're starting
                }

                if (Complete && Settings.InitialSeedingEnabled && ClientEngine.SupportsInitialSeed)
                {
                    Mode = new InitialSeedingMode(this);
                }
                else
                {
                    Mode = new DownloadMode(this);
                }
                Engine.Broadcast(this);

#if !DISABLE_DHT
                StartDHT();
#endif
                StartTime = DateTime.Now;
                PieceManager.Reset();

                ClientEngine.MainLoop.QueueTimeout(TimeSpan.FromSeconds(2), delegate
                {
                    if (State != TorrentState.Downloading && State != TorrentState.Seeding)
                        return false;
                    PieceManager.Picker.CancelTimedOutRequests();
                    return true;
                });
            });
        }

#if !DISABLE_DHT
        private void StartDHT()
        {
            if (dhtInitialised)
                return;
            dhtInitialised = true;
            Engine.DhtEngine.PeersFound += delegate(object o, PeersFoundEventArgs e) { DhtPeersFound(o, e); };

            // First get some peers
            Engine.DhtEngine.GetPeers(InfoHash);

            // Second, get peers every 10 minutes (if we need them)
            ClientEngine.MainLoop.QueueTimeout(TimeSpan.FromMinutes(10), delegate
            {
                // Torrent is no longer active
                if (!Mode.CanAcceptConnections)
                    return false;

                // Only use DHT if it hasn't been (temporarily?) disabled in settings
                if (CanUseDht && Peers.AvailablePeers.Count < Settings.MaxConnections)
                {
                    Engine.DhtEngine.Announce(InfoHash, Engine.Settings.ListenPort);
                    //announce ever done a get peers task
                    //engine.DhtEngine.GetPeers(InfoHash);
                }
                return true;
            });
        }
#endif

        /// <summary>
        ///     Stops the TorrentManager
        /// </summary>
        public void Stop()
        {
            if (State == TorrentState.Error)
            {
                Error = null;
                Mode = new StoppedMode(this);
                return;
            }

            if (Mode is StoppingMode)
                return;

            ClientEngine.MainLoop.QueueWait(delegate
            {
                if (State != TorrentState.Stopped)
                {
#if !DISABLE_DHT
                    Engine.DhtEngine.PeersFound -= DhtPeersFound;
#endif
                    Mode = new StoppingMode(this);
                }
            });
        }

        #endregion

        #region Internal Methods

        public void AddPeers(Peer peer)
        {
            Check.Peer(peer);
            if (HasMetadata && Torrent.IsPrivate)
                throw new InvalidOperationException("You cannot add external peers to a private torrent");

            ClientEngine.MainLoop.QueueWait(() => { AddPeersCore(peer); });
        }

        public void AddPeers(IEnumerable<Peer> peers)
        {
            Check.Peers(peers);
            if (HasMetadata && Torrent.IsPrivate)
                throw new InvalidOperationException("You cannot add external peers to a private torrent");

            ClientEngine.MainLoop.QueueWait(() => { AddPeersCore(peers); });
        }

        internal int AddPeersCore(Peer peer)
        {
            if (Peers.Contains(peer))
                return 0;

            // Ignore peers in the inactive list
            if (InactivePeerManager.InactivePeerList.Contains(peer.ConnectionUri))
                return 0;

            Peers.AvailablePeers.Add(peer);
            if (OnPeerFound != null)
                OnPeerFound(this, new PeerAddedEventArgs(this, peer));
            // When we successfully add a peer we try to connect to the next available peer
            return 1;
        }

        internal int AddPeersCore(IEnumerable<Peer> peers)
        {
            var count = 0;
            foreach (var p in peers)
                count += AddPeersCore(p);
            return count;
        }

        internal void HashedPiece(PieceHashedEventArgs pieceHashedEventArgs)
        {
            if (!pieceHashedEventArgs.HashPassed)
                Interlocked.Increment(ref hashFails);

            RaisePieceHashed(pieceHashedEventArgs);
        }

        internal void RaisePeerConnected(PeerConnectionEventArgs args)
        {
            Toolbox.RaiseAsyncEvent(PeerConnected, this, args);
        }

        internal void RaisePeerDisconnected(PeerConnectionEventArgs args)
        {
            Mode.HandlePeerDisconnected(args.PeerID);
            Toolbox.RaiseAsyncEvent(PeerDisconnected, this, args);
        }

        internal void RaisePeersFound(PeersAddedEventArgs args)
        {
            Toolbox.RaiseAsyncEvent(PeersFound, this, args);
        }

        internal void RaisePieceHashed(PieceHashedEventArgs args)
        {
            var index = args.PieceIndex;
            var files = Torrent.Files;

            for (var i = 0; i < files.Length; i++)
                if (index >= files[i].StartPieceIndex && index <= files[i].EndPieceIndex)
                    files[i].BitField[index - files[i].StartPieceIndex] = args.HashPassed;

            if (args.HashPassed)
            {
                var connected = Peers.ConnectedPeers;
                for (var i = 0; i < connected.Count; i++)
                    connected[i].IsAllowedFastPieces.Remove(index);
            }

            Toolbox.RaiseAsyncEvent(PieceHashed, this, args);
        }

        internal void RaiseTorrentStateChanged(TorrentStateChangedEventArgs e)
        {
            // Whenever we have a state change, we need to make sure that we flush the buffers.
            // For example, Started->Paused, Started->Stopped, Downloading->Seeding etc should all
            // flush to disk.
            Toolbox.RaiseAsyncEvent(TorrentStateChanged, this, e);
        }

        /// <summary>
        ///     Raise the connection attempt failed event
        /// </summary>
        /// <param name="args"></param>
        internal void RaiseConnectionAttemptFailed(PeerConnectionFailedEventArgs args)
        {
            Toolbox.RaiseAsyncEvent(ConnectionAttemptFailed, this, args);
        }

        internal void UpdateLimiters()
        {
            DownloadLimiter.UpdateChunks(Settings.MaxDownloadSpeed, Monitor.DownloadSpeed);
            UploadLimiter.UpdateChunks(Settings.MaxUploadSpeed, Monitor.UploadSpeed);
        }

        #endregion Internal Methods

        #region Private Methods

        private void CheckMetadata()
        {
            if (!HasMetadata)
                throw new InvalidOperationException("This action cannot be performed until metadata has been retrieved");
        }

        private void CheckRegisteredAndDisposed()
        {
            if (Engine == null)
                throw new TorrentException("This manager has not been registed with an Engine");
            if (Engine.Disposed)
                throw new InvalidOperationException("The registered engine has been disposed");
        }

        internal PiecePicker CreateStandardPicker()
        {
            PiecePicker picker;
            if (ClientEngine.SupportsEndgameMode)
                picker = new EndGameSwitcher(new StandardPicker(), new EndGamePicker(),
                    Torrent.PieceLength/Piece.BlockSize, this);
            else
                picker = new StandardPicker();
            picker = new RandomisedPicker(picker);
            picker = new RarestFirstPicker(picker);
            picker = new PriorityPicker(picker);
            return picker;
        }

#if !DISABLE_DHT
        private void DhtPeersFound(object o, PeersFoundEventArgs e)
        {
            if (InfoHash != e.InfoHash)
                return;

            ClientEngine.MainLoop.Queue(delegate
            {
                var count = AddPeersCore(e.Peers);
                RaisePeersFound(new DhtPeersAdded(this, count, e.Peers.Count));
            });
        }
#endif

        public void LoadFastResume(FastResume data)
        {
            Check.Data(data);
            CheckMetadata();
            if (State != TorrentState.Stopped)
                throw new InvalidOperationException("Can only load FastResume when the torrent is stopped");
            if (InfoHash != data.Infohash || Torrent.Pieces.Count != data.Bitfield.Length)
                throw new ArgumentException("The fast resume data does not match this torrent", "fastResumeData");

            Bitfield.From(data.Bitfield);
            for (var i = 0; i < Torrent.Pieces.Count; i++)
                RaisePieceHashed(new PieceHashedEventArgs(this, i, Bitfield[i]));

            HashChecked = true;
        }

        public FastResume SaveFastResume()
        {
            CheckMetadata();
            if (!HashChecked)
                throw new InvalidOperationException(
                    "Fast resume data cannot be created when the TorrentManager has not been hash checked");
            return new FastResume(InfoHash, Bitfield);
        }

        private void VerifyHashState()
        {
            // FIXME: I should really just ensure that zero length files always exist on disk. If the first file is
            // a zero length file and someone deletes it after the first piece has been written to disk, it will
            // never be recreated. If the downloaded data requires this file to exist, we have an issue.
            if (HasMetadata)
            {
                foreach (var file in Torrent.Files)
                    if (!file.BitField.AllFalse && HashChecked && file.Length > 0)
                        HashChecked &= Engine.DiskManager.CheckFileExists(this, file);
            }
        }

        #endregion Private Methods
    }
}