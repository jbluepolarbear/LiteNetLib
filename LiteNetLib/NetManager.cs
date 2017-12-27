#if DEBUG
#define STATS_ENABLED
#endif

using System;
using System.Collections.Generic;
using System.Threading;
using LiteNetLib.Utils;

namespace LiteNetLib
{
    /// <summary>
    /// Main class for all network operations. Can be used as client and/or server.
    /// </summary>
    public sealed class NetManager
    {
        internal delegate void OnMessageReceived(byte[] data, int length, int errorCode, NetEndPoint remoteEndPoint);

        private enum NetEventType
        {
            Connect,
            Disconnect,
            Receive,
            ReceiveUnconnected,
            Error,
            ConnectionLatencyUpdated,
            DiscoveryRequest,
            DiscoveryResponse,
            ConnectionRequest
        }

        private sealed class NetEvent
        {
            public NetPeer Peer;
            public readonly NetDataReader DataReader = new NetDataReader();
            public NetEventType Type;
            public NetEndPoint RemoteEndPoint;
            public int AdditionalData;
            public DisconnectReason DisconnectReason;
            public ConnectionRequest ConnectionRequest;
        }

#if DEBUG
        private struct IncomingData
        {
            public byte[] Data;
            public NetEndPoint EndPoint;
            public DateTime TimeWhenGet;
        }
        private readonly List<IncomingData> _pingSimulationList = new List<IncomingData>(); 
        private readonly Random _randomGenerator = new Random();
        private const int MinLatencyTreshold = 5;
#endif

        private readonly NetSocket _socket;
        private readonly NetThread _logicThread;

        private readonly Queue<NetEvent> _netEventsQueue;
        private readonly Stack<NetEvent> _netEventsPool;
        private readonly INetEventListener _netEventListener;

        private readonly NetPeerCollection _peers;
        private readonly Dictionary<NetEndPoint, NetPeer> _peersToShutdown;
        private readonly int _maxConnections;

        internal readonly NetPacketPool NetPacketPool;

        //config section
        /// <summary>
        /// Enable messages receiving without connection. (with SendUnconnectedMessage method)
        /// </summary>
        public bool UnconnectedMessagesEnabled = false;

        /// <summary>
        /// Enable nat punch messages
        /// </summary>
        public bool NatPunchEnabled = false;

        /// <summary>
        /// Library logic update and send period in milliseconds
        /// </summary>
        public int UpdateTime { get { return _logicThread.SleepTime; } set { _logicThread.SleepTime = value; } }

        /// <summary>
        /// Interval for latency detection and checking connection
        /// </summary>
        public int PingInterval = 1000;

        /// <summary>
        /// If NetManager doesn't receive any packet from remote peer during this time then connection will be closed
        /// (including library internal keepalive packets)
        /// </summary>
        public long DisconnectTimeout = 5000;

        /// <summary>
        /// Simulate packet loss by dropping random amout of packets. (Works only in DEBUG mode)
        /// </summary>
        public bool SimulatePacketLoss = false;

        /// <summary>
        /// Simulate latency by holding packets for random time. (Works only in DEBUG mode)
        /// </summary>
        public bool SimulateLatency = false;

        /// <summary>
        /// Chance of packet loss when simulation enabled. value in percents (1 - 100).
        /// </summary>
        public int SimulationPacketLossChance = 10;

        /// <summary>
        /// Minimum simulated latency
        /// </summary>
        public int SimulationMinLatency = 30;

        /// <summary>
        /// Maximum simulated latency
        /// </summary>
        public int SimulationMaxLatency = 100;

        /// <summary>
        /// Experimental feature. Events automatically will be called without PollEvents method from another thread
        /// </summary>
        public bool UnsyncedEvents = false;

        /// <summary>
        /// Allows receive DiscoveryRequests
        /// </summary>
        public bool DiscoveryEnabled = false;

        /// <summary>
        /// Merge small packets into one before sending to reduce outgoing packets count. (May increase a bit outgoing data size)
        /// </summary>
        public bool MergeEnabled = false;

        /// <summary>
        /// Delay betwen initial connection attempts
        /// </summary>
        public int ReconnectDelay = 500;

        /// <summary>
        /// Maximum connection attempts before client stops and call disconnect event.
        /// </summary>
        public int MaxConnectAttempts = 10;

        /// <summary>
        /// Enables socket option "ReuseAddress" for specific purposes
        /// </summary>
        public bool ReuseAddress = false;

        private const int DefaultUpdateTime = 15;

        /// <summary>
        /// Statistics of all connections
        /// </summary>
        public readonly NetStatistics Statistics;

        //modules
        /// <summary>
        /// NatPunchModule for NAT hole punching operations
        /// </summary>
        public readonly NatPunchModule NatPunchModule;

        /// <summary>
        /// Returns true if socket listening and update thread is running
        /// </summary>
        public bool IsRunning
        {
            get { return _logicThread.IsRunning; }
        }

        /// <summary>
        /// Local EndPoint (host and port)
        /// </summary>
        public int LocalPort
        {
            get { return _socket.LocalPort; }
        }

        /// <summary>
        /// Connected peers count
        /// </summary>
        public int PeersCount
        {
            get { return _peers.Count; }
        }

        /// <summary>
        /// NetManager constructor with maxConnections = 1 (usable for client)
        /// </summary>
        /// <param name="listener">Network events listener</param>
        public NetManager(INetEventListener listener) : this(listener, 1)
        {
            
        }

        /// <summary>
        /// NetManager constructor
        /// </summary>
        /// <param name="listener">Network events listener</param>
        /// <param name="maxConnections">Maximum connections (incoming and outcoming)</param>
        public NetManager(INetEventListener listener, int maxConnections)
        {
            _logicThread = new NetThread("LogicThread", DefaultUpdateTime, UpdateLogic);
            _socket = new NetSocket(ReceiveLogic);
            _netEventListener = listener;
            _netEventsQueue = new Queue<NetEvent>();
            _netEventsPool = new Stack<NetEvent>();
            NetPacketPool = new NetPacketPool();
            NatPunchModule = new NatPunchModule(this);
            Statistics = new NetStatistics();
            _peers = new NetPeerCollection(maxConnections);
            _peersToShutdown = new Dictionary<NetEndPoint, NetPeer>();
            _maxConnections = maxConnections;
        }

        internal void ConnectionLatencyUpdated(NetPeer fromPeer, int latency)
        {
            var evt = CreateEvent(NetEventType.ConnectionLatencyUpdated);
            evt.Peer = fromPeer;
            evt.AdditionalData = latency;
            EnqueueEvent(evt);
        }

        internal bool SendRawAndRecycle(NetPacket packet, NetEndPoint remoteEndPoint)
        {
            var result = SendRaw(packet.RawData, 0, packet.Size, remoteEndPoint);
            NetPacketPool.Recycle(packet);
            return result;
        }

        internal bool SendRaw(byte[] message, int start, int length, NetEndPoint remoteEndPoint)
        {
            if (!IsRunning)
                return false;

            int errorCode = 0;
            if (_socket.SendTo(message, start, length, remoteEndPoint, ref errorCode) <= 0)
            {
                return false;
            }

            //10040 message to long... need to check
            //10065 no route to host
            if (errorCode == 10040)
            {
                NetUtils.DebugWrite(ConsoleColor.Red, "[SRD] 10040, datalen: {0}", length);
                return false;
            }
            if (errorCode != 0 && errorCode != 10065)
            {
                //Send error
                NetPeer fromPeer;
                if (_peers.TryGetValue(remoteEndPoint, out fromPeer))
                {
                    DisconnectPeer(fromPeer, DisconnectReason.SocketSendError, errorCode, true, null, 0, 0);
                }
                var netEvent = CreateEvent(NetEventType.Error);
                netEvent.RemoteEndPoint = remoteEndPoint;
                netEvent.AdditionalData = errorCode;
                EnqueueEvent(netEvent);
                return false;
            }
#if STATS_ENABLED
            Statistics.PacketsSent++;
            Statistics.BytesSent += (uint)length;
#endif

            return true;
        }

        private void DisconnectPeer(
            NetPeer peer, 
            DisconnectReason reason, 
            int socketErrorCode, 
            bool force,
            byte[] data,
            int start,
            int count)
        {
            if (peer == null || !_peers.ContainsAddress(peer.EndPoint))
            {
                return;
            }

            if (count + 8 >= peer.Mtu)
            {
                //Drop additional data
                data = null;
                count = 0;
                NetUtils.DebugWriteError("[NM] Disconnect additional data size more than MTU - 8!");
            }
            var disconnectPacket = NetPacketPool.Get(PacketProperty.Disconnect, 8 + count);
            FastBitConverter.GetBytes(disconnectPacket.RawData, 1, peer.ConnectId);
            if (data != null && count > 0)
            {
                Buffer.BlockCopy(data, start, disconnectPacket.RawData, 9, count);
            }

            if (force)
            {
                SendRawAndRecycle(disconnectPacket, peer.EndPoint);
            }
            else
            {
                //reliable disconnect
                peer.Shutdown(disconnectPacket);
                lock (_peersToShutdown)
                {
                    _peersToShutdown.Add(peer.EndPoint, peer);
                }
            }
            var netEvent = CreateEvent(NetEventType.Disconnect);
            netEvent.Peer = peer;
            netEvent.AdditionalData = socketErrorCode;
            netEvent.DisconnectReason = reason;
            EnqueueEvent(netEvent);
            lock (_peers)
            {
                _peers.Remove(peer.EndPoint);
            }
        }

        private void ClearPeers()
        {
            lock (_peers)
            {
                _peers.Clear();
            }
        }

        private NetEvent CreateEvent(NetEventType type)
        {
            NetEvent evt = null;
            lock (_netEventsPool)
            {
                if (_netEventsPool.Count > 0)
                {
                    evt = _netEventsPool.Pop();
                }
            }
            if(evt == null)
            {
                evt = new NetEvent();
            }
            evt.Type = type;
            return evt;
        }

        private void EnqueueEvent(NetEvent evt)
        {
            if (UnsyncedEvents)
            {
                ProcessEvent(evt);
            }
            else
            {
                lock (_netEventsQueue)
                {
                    _netEventsQueue.Enqueue(evt);
                }
            }
        }

        private void ProcessEvent(NetEvent evt)
        {
            switch (evt.Type)
            {
                case NetEventType.Connect:
                    _netEventListener.OnPeerConnected(evt.Peer);
                    break;
                case NetEventType.Disconnect:
                    var info = new DisconnectInfo
                    {
                        Reason = evt.DisconnectReason,
                        AdditionalData = evt.DataReader,
                        SocketErrorCode = evt.AdditionalData
                    };
                    _netEventListener.OnPeerDisconnected(evt.Peer, info);
                    break;
                case NetEventType.Receive:
                    _netEventListener.OnNetworkReceive(evt.Peer, evt.DataReader);
                    break;
                case NetEventType.ReceiveUnconnected:
                    _netEventListener.OnNetworkReceiveUnconnected(evt.RemoteEndPoint, evt.DataReader, UnconnectedMessageType.BasicMessage);
                    break;
                case NetEventType.DiscoveryRequest:
                    _netEventListener.OnNetworkReceiveUnconnected(evt.RemoteEndPoint, evt.DataReader, UnconnectedMessageType.DiscoveryRequest);
                    break;
                case NetEventType.DiscoveryResponse:
                    _netEventListener.OnNetworkReceiveUnconnected(evt.RemoteEndPoint, evt.DataReader, UnconnectedMessageType.DiscoveryResponse);
                    break;
                case NetEventType.Error:
                    _netEventListener.OnNetworkError(evt.RemoteEndPoint, evt.AdditionalData);
                    break;
                case NetEventType.ConnectionLatencyUpdated:
                    _netEventListener.OnNetworkLatencyUpdate(evt.Peer, evt.AdditionalData);
                    break;
                case NetEventType.ConnectionRequest:
                    _netEventListener.OnConnectionRequest(evt.ConnectionRequest);
                    break;
            }

            //Recycle
            evt.DataReader.Clear();
            evt.Peer = null;
            evt.AdditionalData = 0;
            evt.RemoteEndPoint = null;
            evt.ConnectionRequest = null;

            lock (_netEventsPool)
            {
                _netEventsPool.Push(evt);
            }
        }

        //Update function
        private void UpdateLogic()
        {
#if DEBUG
            if (SimulateLatency)
            {
                var time = DateTime.UtcNow;
                lock (_pingSimulationList)
                {
                    for (int i = 0; i < _pingSimulationList.Count; i++)
                    {
                        var incomingData = _pingSimulationList[i];
                        if (incomingData.TimeWhenGet <= time)
                        {
                            DataReceived(incomingData.Data, incomingData.Data.Length, incomingData.EndPoint);
                            _pingSimulationList.RemoveAt(i);
                            i--;
                        }
                    }
                }
            }
#endif

#if STATS_ENABLED
            ulong totalPacketLoss = 0;
#endif
            int delta = _logicThread.SleepTime;
            //Process acks
            lock (_peers)
            {
                for(int i = 0; i < _peers.Count; i++)
                {
                    var netPeer = _peers[i];
                    bool remove = false;
                    if (netPeer.ConnectionState == ConnectionState.Connected && netPeer.TimeSinceLastPacket > DisconnectTimeout)
                    {
                        NetUtils.DebugWrite("[NM] Disconnect by timeout: {0} > {1}", netPeer.TimeSinceLastPacket, DisconnectTimeout);
                        var netEvent = CreateEvent(NetEventType.Disconnect);
                        netEvent.Peer = netPeer;
                        netEvent.DisconnectReason = DisconnectReason.Timeout;
                        EnqueueEvent(netEvent);
                        remove = true;
                    }
                    else if(netPeer.ConnectionState == ConnectionState.Disconnected)
                    {
                        var netEvent = CreateEvent(NetEventType.Disconnect);
                        netEvent.Peer = netPeer;
                        netEvent.DisconnectReason = DisconnectReason.ConnectionFailed;
                        EnqueueEvent(netEvent);
                        remove = true;
                    }

                    if (remove)
                    {
                        _peers.RemoveAt(i);
                        i--;
                    }
                    else
                    {
                        netPeer.Update(delta);
#if STATS_ENABLED
                        totalPacketLoss += netPeer.Statistics.PacketLoss;
#endif
                    }
                }
            }
  			//Process shutdowned peers
            lock (_peersToShutdown)
            {
                foreach (var netPeer in _peersToShutdown)
                {
                    netPeer.Value.Update(delta);
                }
            }
            
#if STATS_ENABLED
            Statistics.PacketLoss = totalPacketLoss;
#endif
        }
        
        private void ReceiveLogic(byte[] data, int length, int errorCode, NetEndPoint remoteEndPoint)
        {
            //Receive some info
            if (errorCode == 0)
            {
#if DEBUG
                if (SimulatePacketLoss && _randomGenerator.NextDouble() * 100 < SimulationPacketLossChance)
                {
                    //drop packet
                    return;
                }
                if (SimulateLatency)
                {
                    int latency = _randomGenerator.Next(SimulationMinLatency, SimulationMaxLatency);
                    if (latency > MinLatencyTreshold)
                    {
                        byte[] holdedData = new byte[length];
                        Buffer.BlockCopy(data, 0, holdedData, 0, length);

                        lock (_pingSimulationList)
                        {
                            _pingSimulationList.Add(new IncomingData
                            {
                                Data = holdedData,
                                EndPoint = remoteEndPoint,
                                TimeWhenGet = DateTime.UtcNow.AddMilliseconds(latency)
                            });
                        }

                        //hold packet
                        return;
                    }
                }
#endif
                try
                {
                    //ProcessEvents
                    DataReceived(data, length, remoteEndPoint);
                }
                catch(Exception e)
                {
                    //protects socket receive thread
                    NetUtils.DebugWriteError("[NM] SocketReceiveThread error: " + e );
                }
            }
            else //Error on receive
            {
                //TODO: strange?
                ClearPeers();
                var netEvent = CreateEvent(NetEventType.Error);
                netEvent.AdditionalData = errorCode;
                EnqueueEvent(netEvent);
                NetUtils.DebugWriteError("[NM] Receive error: {0}" + errorCode);
            }
        }

        private void OnConnectionSolved(ConnectionRequest request)
        {
            lock (_peers)
            {
                if (_peers.ContainsAddress(request.RemoteEndPoint))
                {
                    NetUtils.DebugWrite(ConsoleColor.Yellow, "[NM] Peer already connected: {0}", request.RemoteEndPoint);
                    return;
                }

                if (request.Result == ConnectionRequestResult.Reject)
                {
                    NetUtils.DebugWrite(ConsoleColor.Cyan, "[NM] Peer connect reject.");
                }
                else
                {
                    //response with id
                    var netPeer = new NetPeer(this, request.RemoteEndPoint, request.ConnectionId);
                    NetUtils.DebugWrite(ConsoleColor.Cyan, "[NM] Received peer connection Id: {0}, EP: {1}",
                        netPeer.ConnectId, netPeer.EndPoint);

                    //add peer to list
                    _peers.Add(request.RemoteEndPoint, netPeer);

                    var netEvent = CreateEvent(NetEventType.Connect);
                    netEvent.Peer = netPeer;
                    EnqueueEvent(netEvent);
                }
            }
        }

        private void DataReceived(byte[] reusableBuffer, int count, NetEndPoint remoteEndPoint)
        {
#if STATS_ENABLED
            Statistics.PacketsReceived++;
            Statistics.BytesReceived += (uint) count;
#endif

            //Try read packet
            NetPacket packet = NetPacketPool.GetAndRead(reusableBuffer, 0, count);
            if (packet == null)
            {
                NetUtils.DebugWriteError("[NM] DataReceived: bad!");
                return;
            }

            //Check unconnected
            switch (packet.Property)
            {
                case PacketProperty.DiscoveryRequest:
                    if(DiscoveryEnabled)
                    {
                        var netEvent = CreateEvent(NetEventType.DiscoveryRequest);
                        netEvent.RemoteEndPoint = remoteEndPoint;
                        netEvent.DataReader.SetSource(packet.RawData, NetConstants.HeaderSize, count);
                        EnqueueEvent(netEvent);
                    }
                    return;
                case PacketProperty.DiscoveryResponse:
                    {
                        var netEvent = CreateEvent(NetEventType.DiscoveryResponse);
                        netEvent.RemoteEndPoint = remoteEndPoint;
                        netEvent.DataReader.SetSource(packet.RawData, NetConstants.HeaderSize, count);
                        EnqueueEvent(netEvent);
                    }
                    return;
                case PacketProperty.UnconnectedMessage:
                    if (UnconnectedMessagesEnabled)
                    {
                        var netEvent = CreateEvent(NetEventType.ReceiveUnconnected);
                        netEvent.RemoteEndPoint = remoteEndPoint;
                        netEvent.DataReader.SetSource(packet.RawData, NetConstants.HeaderSize, count);
                        EnqueueEvent(netEvent);
                    }
                    return;
                case PacketProperty.NatIntroduction:
                case PacketProperty.NatIntroductionRequest:
                case PacketProperty.NatPunchMessage:
                    {
                        if (NatPunchEnabled)
                            NatPunchModule.ProcessMessage(remoteEndPoint, packet);
                        return;
                    }
            }

            //Check normal packets
            NetPeer netPeer;
            //Check peers
            Monitor.Enter(_peers);
            int peersCount = _peers.Count;
            if (_peers.TryGetValue(remoteEndPoint, out netPeer))
            {
                Monitor.Exit(_peers);
                //Send
                if (packet.Property == PacketProperty.Disconnect)
                {
                    if (BitConverter.ToInt64(packet.RawData, 1) != netPeer.ConnectId)
                    {
                        //Old or incorrect disconnect
                        NetPacketPool.Recycle(packet);
                        return;
                    }

                    var netEvent = CreateEvent(NetEventType.Disconnect);
                    netEvent.Peer = netPeer;
                    netEvent.DataReader.SetSource(packet.RawData, 9, packet.Size);
                    netEvent.DisconnectReason = DisconnectReason.RemoteConnectionClose;
                    EnqueueEvent(netEvent);

                    Monitor.Enter(_peers);
                    _peers.Remove(netPeer.EndPoint);
                    Monitor.Exit(_peers);
                    //do not recycle because no sense)
                }
                else if (packet.Property == PacketProperty.ConnectAccept)
                {
                    if (netPeer.ProcessConnectAccept(packet))
                    {
                        var connectEvent = CreateEvent(NetEventType.Connect);
                        connectEvent.Peer = netPeer;
                        EnqueueEvent(connectEvent);
                    }
                    NetPacketPool.Recycle(packet);
                }
                else
                {
                    netPeer.ProcessPacket(packet);
                }
                return;
            }
            Monitor.Exit(_peers);

            //Unacked shutdown
            if (packet.Property == PacketProperty.Disconnect)
            {
                byte[] data = { (byte)PacketProperty.AlreadyDisconnected };
                SendRaw(data, 0, 1, remoteEndPoint);
                return;
            }

            //Search shutdowned peer and remove
            if (packet.Property == PacketProperty.AlreadyDisconnected)
            {
                lock (_peersToShutdown)
                {
                    bool removed = _peersToShutdown.Remove(remoteEndPoint);
                    NetUtils.DebugWriteForce(ConsoleColor.Cyan, "[NM] Peer shutdowned: " + removed);
                }
                return;
            }

            if (peersCount < _maxConnections && packet.Property == PacketProperty.ConnectRequest && packet.Size >= 12)
            {
                int protoId = BitConverter.ToInt32(packet.RawData, 1);
                if (protoId != NetConstants.ProtocolId)
                {
                    NetUtils.DebugWrite(ConsoleColor.Cyan, "[NM] Peer connect reject. Invalid protocol ID: " + protoId);
                    return;
                }
                //Getting new id for peer
                long connectionId = BitConverter.ToInt64(packet.RawData, 5);

                // Read data and create request
                var reader = new NetDataReader(null, 0, 0);
                if (packet.Size > 12)
                {
                    reader.SetSource(packet.RawData, 13, packet.Size);
                }
                var netEvent = CreateEvent(NetEventType.ConnectionRequest);
                netEvent.ConnectionRequest = new ConnectionRequest(connectionId, remoteEndPoint, reader, OnConnectionSolved);
                EnqueueEvent(netEvent);
            }
        }

        internal void ReceiveFromPeer(NetPacket packet, NetEndPoint remoteEndPoint)
        {
            NetPeer fromPeer;
            if (_peers.TryGetValue(remoteEndPoint, out fromPeer))
            {
                NetUtils.DebugWrite(ConsoleColor.Cyan, "[NM] Received message");
                var netEvent = CreateEvent(NetEventType.Receive);
                netEvent.Peer = fromPeer;
                netEvent.RemoteEndPoint = fromPeer.EndPoint;
                netEvent.DataReader.SetSource(packet.CopyPacketData());
                EnqueueEvent(netEvent);
            }
        }

        /// <summary>
        /// Send data to all connected peers
        /// </summary>
        /// <param name="writer">DataWriter with data</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        public void SendToAll(NetDataWriter writer, SendOptions options)
        {
            SendToAll(writer.Data, 0, writer.Length, options);
        }

        /// <summary>
        /// Send data to all connected peers
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        public void SendToAll(byte[] data, SendOptions options)
        {
            SendToAll(data, 0, data.Length, options);
        }

        /// <summary>
        /// Send data to all connected peers
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="start">Start of data</param>
        /// <param name="length">Length of data</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        public void SendToAll(byte[] data, int start, int length, SendOptions options)
        {
            lock (_peers)
            {
                for(int i = 0; i < _peers.Count; i++)
                {
                    _peers[i].Send(data, start, length, options);
                }
            }
        }

        /// <summary>
        /// Send data to all connected peers
        /// </summary>
        /// <param name="writer">DataWriter with data</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        /// <param name="excludePeer">Excluded peer</param>
        public void SendToAll(NetDataWriter writer, SendOptions options, NetPeer excludePeer)
        {
            SendToAll(writer.Data, 0, writer.Length, options, excludePeer);
        }

        /// <summary>
        /// Send data to all connected peers
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        /// <param name="excludePeer">Excluded peer</param>
        public void SendToAll(byte[] data, SendOptions options, NetPeer excludePeer)
        {
            SendToAll(data, 0, data.Length, options, excludePeer);
        }

        /// <summary>
        /// Send data to all connected peers
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="start">Start of data</param>
        /// <param name="length">Length of data</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        /// <param name="excludePeer">Excluded peer</param>
        public void SendToAll(byte[] data, int start, int length, SendOptions options, NetPeer excludePeer)
        {
            lock (_peers)
            {
                for (int i = 0; i < _peers.Count; i++)
                {
                    var netPeer = _peers[i];
                    if (netPeer != excludePeer)
                    {
                        netPeer.Send(data, start, length, options);
                    }
                }
            }
        }

        /// <summary>
        /// Start logic thread and listening on available port
        /// </summary>
        public bool Start()
        {
            return Start(0);
        }

        /// <summary>
        /// Start logic thread and listening on selected port
        /// </summary>
        /// <param name="port">port to listen</param>
        public bool Start(int port)
        {
            if (IsRunning)
            {
                return false;
            }

            _netEventsQueue.Clear();
            if (!_socket.Bind(port, ReuseAddress))
                return false;

            _logicThread.Start();
            return true;
        }

        /// <summary>
        /// Send message without connection
        /// </summary>
        /// <param name="message">Raw data</param>
        /// <param name="remoteEndPoint">Packet destination</param>
        /// <returns>Operation result</returns>
        public bool SendUnconnectedMessage(byte[] message, NetEndPoint remoteEndPoint)
        {
            return SendUnconnectedMessage(message, 0, message.Length, remoteEndPoint);
        }

        /// <summary>
        /// Send message without connection
        /// </summary>
        /// <param name="writer">Data serializer</param>
        /// <param name="remoteEndPoint">Packet destination</param>
        /// <returns>Operation result</returns>
        public bool SendUnconnectedMessage(NetDataWriter writer, NetEndPoint remoteEndPoint)
        {
            return SendUnconnectedMessage(writer.Data, 0, writer.Length, remoteEndPoint);
        }

        /// <summary>
        /// Send message without connection
        /// </summary>
        /// <param name="message">Raw data</param>
        /// <param name="start">data start</param>
        /// <param name="length">data length</param>
        /// <param name="remoteEndPoint">Packet destination</param>
        /// <returns>Operation result</returns>
        public bool SendUnconnectedMessage(byte[] message, int start, int length, NetEndPoint remoteEndPoint)
        {
            if (!IsRunning)
                return false;
            var packet = NetPacketPool.GetWithData(PacketProperty.UnconnectedMessage, message, start, length);
            bool result = SendRawAndRecycle(packet, remoteEndPoint);
            return result;
        }

        public bool SendDiscoveryRequest(NetDataWriter writer, int port)
        {
            return SendDiscoveryRequest(writer.Data, 0, writer.Length, port);
        }

        public bool SendDiscoveryRequest(byte[] data, int port)
        {
            return SendDiscoveryRequest(data, 0, data.Length, port);
        }

        public bool SendDiscoveryRequest(byte[] data, int start, int length, int port)
        {
            if (!IsRunning)
                return false;
            var packet = NetPacketPool.GetWithData(PacketProperty.DiscoveryRequest, data, start, length);
            bool result = _socket.SendBroadcast(packet.RawData, 0, packet.Size, port);
            NetPacketPool.Recycle(packet);
            return result;
        }

        public bool SendDiscoveryResponse(NetDataWriter writer, NetEndPoint remoteEndPoint)
        {
            return SendDiscoveryResponse(writer.Data, 0, writer.Length, remoteEndPoint);
        }

        public bool SendDiscoveryResponse(byte[] data, NetEndPoint remoteEndPoint)
        {
            return SendDiscoveryResponse(data, 0, data.Length, remoteEndPoint);
        }

        public bool SendDiscoveryResponse(byte[] data, int start, int length, NetEndPoint remoteEndPoint)
        {
            if (!IsRunning)
                return false;
            var packet = NetPacketPool.GetWithData(PacketProperty.DiscoveryResponse, data, start, length);
            bool result = SendRawAndRecycle(packet, remoteEndPoint);
            return result;
        }

        /// <summary>
        /// Flush all queued packets of all peers
        /// </summary>
        public void Flush()
        {
            lock (_peers)
            {
                for (int i = 0; i < _peers.Count; i++)
                {
                    _peers[i].Flush();
                }
            }
        }

        /// <summary>
        /// Receive all pending events. Call this in game update code
        /// </summary>
        public void PollEvents()
        {
            if (UnsyncedEvents)
                return;

            while (_netEventsQueue.Count > 0)
            {
                NetEvent evt;
                lock (_netEventsQueue)
                {
                    evt = _netEventsQueue.Dequeue();
                }
                ProcessEvent(evt);
            }
        }

        /// <summary>
        /// Connect to remote host
        /// </summary>
        /// <param name="address">Server IP or hostname</param>
        /// <param name="port">Server Port</param>
        /// <param name="key">Connection key</param>
        /// <returns>Null if connections limit reached, New NetPeer if new connection, Old NetPeer if already connected</returns>
        public NetPeer Connect(string address, int port, string key)
        {
            var ep = new NetEndPoint(address, port);
            return Connect(ep, key);
        }

        /// <summary>
        /// Connect to remote host
        /// </summary>
        /// <param name="address">Server IP or hostname</param>
        /// <param name="port">Server Port</param>
        /// <param name="connectionData">Additional data for remote peer</param>
        /// <returns>Null if connections limit reached, New NetPeer if new connection, Old NetPeer if already connected</returns>
        public NetPeer Connect(string address, int port, NetDataWriter connectionData)
        {
            var ep = new NetEndPoint(address, port);
            return Connect(ep, connectionData);
        }


        /// <summary>
        /// Connect to remote host
        /// </summary>
        /// <param name="target">Server end point (ip and port)</param>
        /// <param name="key">Connection key</param>
        /// <returns>Null if connections limit reached, New NetPeer if new connection, Old NetPeer if already connected</returns>
        public NetPeer Connect(NetEndPoint target, string key)
        {
            return Connect(target, NetDataWriter.FromString(key));
        }

        /// <summary>
        /// Connect to remote host
        /// </summary>
        /// <param name="target">Server end point (ip and port)</param>
        /// <param name="connectionData">Additional data for remote peer</param>
        /// <returns>Null if connections limit reached, New NetPeer if new connection, Old NetPeer if already connected</returns>
        public NetPeer Connect(NetEndPoint target, NetDataWriter connectionData)
        {
            if (!IsRunning)
            {
                throw new Exception("Client is not running");
            }
            lock (_peers)
            {
                NetPeer peer;
                if (_peers.Count >= _maxConnections)
                {
                    return null;
                }
                if (_peers.TryGetValue(target, out peer))
                {
                    //Already connected
                    return peer;
                }

                //Create reliable connection
                //And send connection request
                peer = new NetPeer(this, target, connectionData);
                _peers.Add(target, peer);
                return peer;
            }
        }

        /// <summary>
        /// Force closes connection and stop all threads.
        /// </summary>
        public void Stop()
        {
            //Send disconnect packets
            lock (_peers)
            {
                for (int i = 0; i < _peers.Count; i++)
                {
                    var disconnectPacket = NetPacketPool.Get(PacketProperty.Disconnect, 8);
                    FastBitConverter.GetBytes(disconnectPacket.RawData, 1, _peers[i].ConnectId);
                    SendRawAndRecycle(disconnectPacket, _peers[i].EndPoint);
                }
            }

            //Clear
            ClearPeers();

            //Stop
            if (IsRunning)
            {
                _logicThread.Stop();
                _socket.Close();
            }
        }

        /// <summary>
        /// Get first peer. Usefull for Client mode
        /// </summary>
        /// <returns></returns>
        public NetPeer GetFirstPeer()
        {
            lock (_peers)
            {
                if (_peers.Count > 0)
                {
                    return _peers[0];
                }
            }
            return null;
        }

        /// <summary>
        /// Get copy of current connected peers
        /// </summary>
        /// <returns>Array with connected peers</returns>
        public NetPeer[] GetPeers()
        {
            NetPeer[] peers;
            lock (_peers)
            {
                peers = _peers.ToArray();
            }
            return peers;
        }

        /// <summary>
        /// Get copy of current connected peers (without allocations)
        /// </summary>
        /// <param name="peers">List that will contain result</param>
        public void GetPeersNonAlloc(List<NetPeer> peers)
        {
            peers.Clear();
            lock (_peers)
            {
                for(int i = 0; i < _peers.Count; i++)
                {
                    peers.Add(_peers[i]);
                }
            }
        }

        public void Disconnect()
        {
            //TODO
        }

        /// <summary>
        /// Immediately disconnect peer from server without additional data
        /// </summary>
        /// <param name="peer">peer to disconnect</param>
        public void DisconnectPeerForce(NetPeer peer)
        {
            DisconnectPeer(peer, DisconnectReason.DisconnectPeerCalled, 0, true, null, 0, 0);
        }

        /// <summary>
        /// Disconnect peer from server
        /// </summary>
        /// <param name="peer">peer to disconnect</param>
        public void DisconnectPeer(NetPeer peer)
        {
            DisconnectPeer(peer, null, 0, 0);
        }

        /// <summary>
        /// Disconnect peer from server and send additional data (Size must be less or equal MTU - 8)
        /// </summary>
        /// <param name="peer">peer to disconnect</param>
        /// <param name="data">additional data</param>
        public void DisconnectPeer(NetPeer peer, byte[] data)
        {
            DisconnectPeer(peer, data, 0, data.Length);
        }

        /// <summary>
        /// Disconnect peer from server and send additional data (Size must be less or equal MTU - 8)
        /// </summary>
        /// <param name="peer">peer to disconnect</param>
        /// <param name="writer">additional data</param>
        public void DisconnectPeer(NetPeer peer, NetDataWriter writer)
        {
            DisconnectPeer(peer, writer.Data, 0, writer.Length);
        }

        /// <summary>
        /// Disconnect peer from server and send additional data (Size must be less or equal MTU - 8)
        /// </summary>
        /// <param name="peer">peer to disconnect</param>
        /// <param name="data">additional data</param>
        /// <param name="start">data start</param>
        /// <param name="count">data length</param>
        public void DisconnectPeer(NetPeer peer, byte[] data, int start, int count)
        {
            DisconnectPeer(
                peer, 
                DisconnectReason.DisconnectPeerCalled, 
                0, 
                false,
                data, 
                start, 
                count);
            }
    }
}
