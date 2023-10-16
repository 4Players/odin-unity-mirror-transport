using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Mirror;
using OdinNative.Odin.Peer;
using OdinNative.Odin.Room;
using UnityEngine;

namespace Odin.Networking.Mirror
{
    public struct HandleOdinMessagePayload
    {
        public int connectionId;
        public OdinTransportMessage odinMessage;
    }

    /// <summary>
    /// Implementation of the Odin Transport for Mirror. This transport uses the Odin Native SDK to connect to a ODIN
    /// room. The transport can be used as a server or a client. The transport will handle all Odin related events.
    /// The transport will connect to the ODIN gateway set in the OdinEditorConfig and uses the NetworkManager's
    /// networkAddress as the room id.
    /// </summary>
    public class OdinTransport : Transport
    {
        /// <summary>
        /// Sets the maximum size of a package that can be sent or received. This should be smaller that the MTU of the network.
        /// </summary>
        [Tooltip(
            "The maximum size of a package that can be sent or received. This should be smaller that the MTU of the network.")]
        public int maxPackageSize = 1024;

        /// <summary>
        /// If true, the transport will log all messages to the console
        /// </summary>
        [Tooltip("If true, the transport will log all messages to the console")]
        public OdinTransportDebugLevel debugLevel = OdinTransportDebugLevel.None;

        /// <summary>
        /// Set to true after the connection to the ODIN room has been established
        /// </summary>
        private bool _isConnected;

        /// <summary>
        /// The connected room
        /// </summary>
        private Room _connectedRoom;

        /// <summary>
        /// Stores if this peer is a server (i.e. ServerStart has been called). If false, the ClientConnect has been called
        /// </summary>
        private bool _isServer;

        /// <summary>
        /// The room id we are connected to. This is the room id that the client will connect to and the server will create.
        /// </summary>
        private string _roomId;

        /// <summary>
        /// Stores the peer id of the server peer in the room. After the client connects to the ODIN room OnPeerJoined event
        /// is called. The client unpacks the Peers User Data and searches for the server peer. Once the server peer is found
        /// the client stores the peer id in this variable. This is used to send messages to the server peer.
        /// </summary>
        private ulong _hostPeerId;

        /// <summary>
        /// Will be invoked, if the client joined an ODIN room, but the room didn't contain a hosting peer. ClientDisconnect()
        /// will be called immediately afterwards.
        /// </summary>
        public Action<string> onNoServerFound;

        /// <summary>
        /// The name of the ODIN room used for networking.
        /// </summary>
        public string NetworkingRoomId => _roomId;

        private delegate void HandleOdinMessageDelegate(HandleOdinMessagePayload payload);

        private readonly Dictionary<OdinMessageType, HandleOdinMessageDelegate> _messageHandlers =
            new Dictionary<OdinMessageType, HandleOdinMessageDelegate>();

        #region ODIN Helper Methods

        /// <summary>
        /// Sets up ODIN event handlers required for the transport
        /// </summary>
        private void SetupOdinEventHandlers()
        {
            OdinHandler.Instance.OnRoomJoined.AddListener(OnRoomJoined);
            OdinHandler.Instance.OnRoomLeft.AddListener(OnRoomLeft);
            OdinHandler.Instance.OnPeerJoined.AddListener(OnPeerJoined);
            OdinHandler.Instance.OnPeerLeft.AddListener(OnPeerLeft);
            OdinHandler.Instance.OnMessageReceived.AddListener(OnMessageReceived);
        }

        /// <summary>
        /// Removes all event handlers except the OnRoomLeft event which is removed later as the OnRoomLeft event is required
        /// for resetting the connection state
        /// </summary>
        private void RemoveOdinEventHandlers()
        {
            OdinHandler.Instance.OnRoomJoined.RemoveListener(OnRoomJoined);
            OdinHandler.Instance.OnPeerJoined.RemoveListener(OnPeerJoined);
            OdinHandler.Instance.OnPeerLeft.RemoveListener(OnPeerLeft);
            OdinHandler.Instance.OnMessageReceived.RemoveListener(OnMessageReceived);

            // We don't remove the OnRoomLeft event as we need it to reset the connection state, we'll clear that later
        }

        private void JoinOdinRoom(string roomId, OdinTransportPeerType peerType)
        {
            if (_isConnected)
                return;
            LogDefault($"ODIN Transport: Joining Odin Room ${roomId} as type {peerType}");
            // Set the room id
            _roomId = roomId;

            // Prepare user data to indicate that we are a client and connect to the room
            OdinTransportUserData userData = new OdinTransportUserData(peerType);
            OdinHandler.Instance.JoinRoom(roomId, userData);
        }

        private void LeaveOdinRoom()
        {
            _isConnected = false;
            OdinHandler.Instance.LeaveRoom(_roomId);
        }

        #endregion

        #region Mirror Transport Callbacks

        /// <summary>
        /// ODIN Transport is only available on non-webgl platforms
        /// </summary>
        /// <returns></returns>
        public override bool Available()
        {
            return Application.platform != RuntimePlatform.WebGLPlayer;
        }

        /// <summary>
        /// Returns true if the client is connected to the ODIN room
        /// </summary>
        /// <returns></returns>
        public override bool ClientConnected()
        {
            return _isConnected;
        }

        /// <summary>
        /// Connect the client to the ODIN room. Uses the NetworkManager's networkAddress as the room id
        /// </summary>
        /// <param name="room">Provide the room to connect as the network address</param>
        public override void ClientConnect(string room)
        {
            LogDefault($"ODIN Transport: ClientConnect to room id {room}");

            // If we are the host we don't need to connect again
            if (_isServer && _isConnected)
            {
                OnClientConnected?.Invoke();
                return;
            }

            if (_isConnected)
                return;

            _roomId = room;

            _messageHandlers.Add(OdinMessageType.Invalid, HandleInvalidOdinMessage);
            _messageHandlers.Add(OdinMessageType.DisconnectClient, ClientHandleClientDisconnect);
            _messageHandlers.Add(OdinMessageType.Default, ClientHandleDefaultOdinMessage);

            // Setup the ODIN event handlers
            SetupOdinEventHandlers();

            // Join the room
            JoinOdinRoom(room, OdinTransportPeerType.Client);
        }


        /// <summary>
        /// Send data to the server. Clients identify the server in the initial set of PeerJoined events by analyzing the
        /// user data and setting the _hostPeerId variable. This is then used to send messages to the server.
        /// </summary>
        /// <param name="segment"></param>
        /// <param name="channelId"></param>
        public override async void ClientSend(ArraySegment<byte> segment, int channelId = Channels.Reliable)
        {
            if (!OdinHandler.Instance || !OdinHandler.Instance.Rooms.Contains(_roomId))
            {
                LogVerbose($"ODIN Transport: refused ClientSend call, OdinHandler room list does not contain {_roomId}");
                OnClientError(TransportError.InvalidSend, $"OdinHandler room list does not contain {_roomId}");
                return;
            }

            if (!_isServer)
            {
                if (_hostPeerId <= 0)
                {
                    Debug.LogError("OdinTransport: Could not send message, host peer id is not set");
                    OnClientError(TransportError.InvalidSend, "Could not send message, host peer id is not set");
                    return;
                }

                if (_connectedRoom == null)
                {
                    Debug.LogError("OdinTransport: Could not send message, connected room is invalid");
                    OnClientError(TransportError.InvalidSend, $"Could not send message, connected room is invalid");
                    return;
                }

                await SendOdinMessage(_hostPeerId, segment);
                OnClientDataSent?.Invoke(segment, channelId);
            }
        }

        private Task<bool> SendOdinMessage(ulong targetId, ArraySegment<byte> segment,
            OdinMessageType type = OdinMessageType.Default)
        {
            OdinTransportMessage odinTransportMessage = new OdinTransportMessage(type, segment);

            ulong[] peerIdList = new ulong[1];
            peerIdList[0] = targetId;
            return _connectedRoom?.SendMessageAsync(peerIdList, odinTransportMessage.ToBytes());
        }

        /// <summary>
        /// Disconnect a client, removes ODIN event handlers and leaves the ODIN room
        /// </summary>
        public override void ClientDisconnect()
        {
            if (NetworkManager.singleton.mode == NetworkManagerMode.ServerOnly)
            {
                // We are host, although we close our client connection we still want to keep the server running
                LogDefault("ODIN Transport: Disconnecting client but leaving ODIN connection and server running");
                return;
            }

            LogDefault("ODIN Transport: Client Disconnect");

            // Remove all listeners except the room left listener (we need that event to clean up)
            Cleanup();

            // Leave the room
            LeaveOdinRoom();
        }


        /// <summary>
        /// Returns the server uri for the ODIN room in standard ODIN notation, which is odin://<gateway address>/<room id>
        /// </summary>
        /// <returns></returns>
        public override Uri ServerUri()
        {
            return new Uri($"odin://{OdinHandler.Config.Server}/{_connectedRoom.Config.Name}");
        }

        /// <summary>
        /// Returns if we are the server and connected to the ODIN room
        /// </summary>
        /// <returns></returns>
        public override bool ServerActive()
        {
            return _isServer && _isConnected;
        }

        /// <summary>
        /// Start the server, connects the ODIN rooms and sets up event handlers. Uses the NetworkManager's networkAddress
        /// as the room id
        /// </summary>
        public override void ServerStart()
        {
            LogDefault("ODIN Transport: ServerStart");
            _isServer = true;

            _messageHandlers.Add(OdinMessageType.Invalid, HandleInvalidOdinMessage);
            _messageHandlers.Add(OdinMessageType.Default, ServerHandleDefaultOdinMessage);
            _messageHandlers.Add(OdinMessageType.DisconnectClient, ServerHandleDisconnectClientMessage);

            // Setup the ODIN event handlers
            SetupOdinEventHandlers();

            // Modify ODIN Config if this is a standalone server
            if (NetworkManager.singleton.mode == NetworkManagerMode.ServerOnly)
            {
                // Disable automatic playback of audio (does not make sense on server side
                OdinHandler.Instance.CreatePlayback = false;

                // Disable MicrophoneReader (does not make sense on server side)
                OdinHandler.Instance.Microphone.AutostartListen = false;
            }

            var room = NetworkManager.singleton.networkAddress;
            JoinOdinRoom(room, OdinTransportPeerType.Server);
        }


        /// <summary>
        /// Send data to the specified connection. Uses Odins SendMessage to send the data to the specified peer.
        /// </summary>
        /// <param name="connectionId">Connection id is equal to target peer id</param>
        /// <param name="segment"></param>
        /// <param name="channelId"></param>
        public override async void ServerSend(int connectionId, ArraySegment<byte> segment,
            int channelId = Channels.Reliable)
        {
            if (!_isServer) return;
            if (_connectedRoom == null)
            {
                LogVerbose("ODIN Transport: Could not send message, no room connected");
                OnServerError(connectionId, TransportError.InvalidSend, "Could not send message, no room connected");
                return;
            }

            await SendOdinMessage((ulong)connectionId, segment);
            OnServerDataSent?.Invoke(connectionId, segment, channelId);
        }

        /// <summary>
        /// This function does nothing as Odin does not support kicking peers from the server
        /// </summary>
        /// <param name="connectionId"></param>
        public override void ServerDisconnect(int connectionId)
        {
            LogDefault($"Odin Transport: Server Disconnect for connection {connectionId}");
            SendOdinMessage((ulong)connectionId, null, OdinMessageType.DisconnectClient);
            // Do nothing - we cannot kick peers from the server
        }

        /// <summary>
        /// Returns the user id of the Odin Peer
        /// </summary>
        /// <param name="connectionId"></param>
        /// <returns></returns>
        public override string ServerGetClientAddress(int connectionId)
        {
            //if (debug) Debug.Log("ODIN Transport: ServerGetClientAddress");
            return _connectedRoom.RemotePeers[(ulong)connectionId].UserId;
        }

        /// <summary>
        /// Stops the server. Cleans up Odin handlers and leaves the room
        /// </summary>
        public override void ServerStop()
        {
            Debug.Log("Server Stop");

            if (_isServer)
            {
                LogDefault("ODIN Transport: ServerStop");
                // Remove all listeners except the room left listener (we need that event to clean up)
                Cleanup();

                // Leave the room
                OdinHandler.Instance.LeaveRoom(_roomId);
                _connectedRoom = null;
                _isConnected = false;
                _isServer = false;
            }
        }

        /// <summary>
        /// Returns the maximum packet size for the given channel - this value can be set in the transport settings
        /// </summary>
        /// <param name="channelId"></param>
        /// <returns></returns>
        public override int GetMaxPacketSize(int channelId = Channels.Reliable)
        {
            return maxPackageSize;
        }

        /// <summary>
        /// Shutdown the transport
        /// </summary>
        public override void Shutdown()
        {
            LogDefault("ODIN Transport: Shutdown");

            ServerStop();
        }

        #endregion

        #region Odin Callbacks

        /// <summary>
        /// Odin event handler for the room joined event. It's used to send the OnClientConnected event to the network manager
        /// and to set the _isConnected flag to true
        /// </summary>
        /// <param name="args"></param>
        private void OnRoomJoined(RoomJoinedEventArgs args)
        {
            string roomName = args.Room.Config.Name;
            // Check if the room id matches the room id we are connected to for networking
            if (!IsNetworkingRoom(roomName))
            {
                LogVerbose($"ODIN Transport: OnRoomJoined refused for {roomName} , not in networking room {_roomId}.");
                return;
            }

            if (_isConnected)
            {
                LogVerbose("ODIN Transport: Already connected, OnRoomJoined refused.");
                return;
            }


            _isConnected = true;
            _connectedRoom = args.Room;
            if (!_isServer)
            {
                foreach (var remotePeer in _connectedRoom.RemotePeers)
                {
                    if (IsPeerServer(remotePeer))
                    {
                        LogDefault("ODIN Transport: Found host, OnClientConnected called");

                        _hostPeerId = remotePeer.Id;
                        OnClientConnected?.Invoke();
                        return;
                    }
                }
            
                Debug.LogError($"ODIN Transport: Could not find host in networking room {NetworkingRoomId}");
                // Call the OnNoServerFound event
                onNoServerFound?.Invoke(_connectedRoom.Config.Name);
                // If we are here we did not find a server
                ClientDisconnect();
            }
            else
            {
                OnClientConnected?.Invoke();
            }
        }

        /// <summary>
        /// Odin event handler for the room left event. It's used to send the OnClientDisconnected event to the network manager
        /// Also removes the listener for the room left event
        /// </summary>
        /// <param name="roomLeftEventArgs"></param>
        private void OnRoomLeft(RoomLeftEventArgs roomLeftEventArgs)
        {
            // Check if the room id matches the room id we are connected to for networking
            if (roomLeftEventArgs.RoomName != _roomId)
            {
                return;
            }

            _connectedRoom = null;
            _isConnected = false;

            // Remote the listener for the room left event - now we are finished
            OdinHandler.Instance.OnRoomLeft.RemoveListener(OnRoomLeft);

            if (!_isServer)
            {
                LogDefault("ODIN Transport: OnClientDisconnected called.");
                OnClientDisconnected?.Invoke();
            }
        }

        /// <summary>
        /// Unpack the peers user data and check if this is a server
        /// </summary>
        /// <param name="peer"></param>
        /// <returns></returns>
        private bool IsPeerServer(Peer peer)
        {
            OdinTransportUserData userData = OdinTransportUserData.FromBytes(peer.UserData.ToBytes());
            return userData.IsServer;
        }

        /// <summary>
        /// Handles OnPeerJoined events sent from ODIN. This is called when a new peer joins the room. If this peer is the
        /// server, we check if the peer is another server, then we block him from joining. If this is a client, we check if
        /// this peer is the server and if so, we store the server peer id as we need that later when sending messages to the
        /// server.
        /// </summary>
        /// <param name="roomObject"></param>
        /// <param name="peerJoinedEventArgs"></param>
        private void OnPeerJoined(object roomObject, PeerJoinedEventArgs peerJoinedEventArgs)
        {
            if (!IsNetworkingRoom(roomObject))
            {
                LogVerbose("ODIN Transport: OnPeerJoined refused, not in networking room.");
                return;
            }

            if (_isServer)
            {
                // Make sure this is not another server
                if (IsPeerServer(peerJoinedEventArgs.Peer))
                {
                    // We don't let this peer in - he will time out as he does not receive any answer from us
                    Debug.LogWarning("ODIN Transport: Another server tried to connect to room, refusing connection.");
                }
                else
                {
                    LogDefault(
                        $"ODIN Transport: OnServerConnected called with connectionId: {peerJoinedEventArgs.Peer.Id}");
                    OnServerConnected?.Invoke((int)peerJoinedEventArgs.Peer.Id);
                }
            }
            else
            {
                // This is a client and we got info that another client connected. We try to find the host and store the hosts
                // peer id so we can use it to send messages to the host.
                if (IsPeerServer(peerJoinedEventArgs.Peer))
                {
                    _hostPeerId = peerJoinedEventArgs.Peer.Id;
                    LogDefault($"ODIN Transport: Server found in room with peer id: {_hostPeerId}");
                }
            }
        }

        private bool IsNetworkingRoom(object roomObject)
        {
            bool isNetworkingRoom = false;
            if (roomObject is Room room)
            {
                isNetworkingRoom = room.Config.Name == NetworkingRoomId;
            }

            return isNetworkingRoom;
        }

        /// <summary>
        /// Handles the case when a peer leaves the room. If this is the server and 
        /// </summary>
        /// <param name="roomObject"></param>
        /// <param name="arg1"></param>
        private void OnPeerLeft(object roomObject, PeerLeftEventArgs arg1)
        {
            if (!IsNetworkingRoom(roomObject))
                return;

            if (_isServer)
            {
                LogDefault($"ODIN Transport: OnServerDisconnected called with connectionId: {arg1.PeerId}");
                OnServerDisconnected?.Invoke((int)arg1.PeerId);
            }
            else
            {
                // We are the client, check if the server has left the room - then we disconnect
                if (arg1.PeerId == _hostPeerId)
                {
                    OnClientError(TransportError.Unexpected, "The server has left the room.");
                    LogDefault($"ODIN Transport: The server has left the room, peer Id: {arg1.PeerId}");
                    LeaveOdinRoom();
                }
            }
        }

        /// <summary>
        /// This is called when a message is received from a peer, depending on the role of this peer, we call either
        /// client message received or server message received.
        /// </summary>
        /// <param name="roomObject"></param>
        /// <param name="messageReceivedEventArgs"></param>
        private void OnMessageReceived(object roomObject, MessageReceivedEventArgs messageReceivedEventArgs)
        {
            LogVerbose($"ODIN Transport: OnMessageReceived");

            if (!IsNetworkingRoom(roomObject))
            {
                LogVerbose(
                    $"Refusing message, not in networking room. Networking Room: {_roomId}, message sent on: {(roomObject as Room)?.Config.Name}");
                return;
            }

            if (!_isConnected && !_isServer)
            {
                // ODIN SDK sends RoomJoined message at the very last moment, sometimes, a message is received before the RoomJoined event is fired
                // Mirror expects OnClientConnected to be called first, so we call it here if it hasn't been called yet
                _isConnected = true;
                _hostPeerId = messageReceivedEventArgs.PeerId;
                _connectedRoom = roomObject as Room;
                OnClientConnected?.Invoke();
            }

            OdinTransportMessage transportMessage = OdinTransportMessage.FromBytes(messageReceivedEventArgs.Data);
            HandleOdinMessagePayload messagePayload = new HandleOdinMessagePayload()
            {
                odinMessage = transportMessage,
                connectionId = (int)messageReceivedEventArgs.PeerId
            };
            // handle message based on message type
            _messageHandlers[transportMessage.messageType](messagePayload);
        }

        #endregion


        #region HandleOdinMessages

        private void ServerHandleDisconnectClientMessage(HandleOdinMessagePayload payload)
        {
            Debug.LogError(
                "Odin Transport: Server received a 'Disconnect Client' message. Ignoring, because Server can not be kicked from Server.");
        }

        private void ServerHandleDefaultOdinMessage(HandleOdinMessagePayload payload)
        {
            OdinTransportMessage odinTransportMessage = payload.odinMessage;
            LogDefault(
                $"ODIN Transport: OnServerDataReceived called with connectionId: {payload.connectionId}, {odinTransportMessage.content.Count} bytes");

            OnServerDataReceived?.Invoke(payload.connectionId,
                odinTransportMessage.content, Channels.Reliable);
        }

        private void ClientHandleDefaultOdinMessage(HandleOdinMessagePayload payload)
        {
            OdinTransportMessage odinTransportMessage = payload.odinMessage;
            LogVerbose($"ODIN Transport: OnClientDataReceived called, {odinTransportMessage.content.Count} bytes");
            OnClientDataReceived?.Invoke(odinTransportMessage.content, Channels.Reliable);
        }

        private void ClientHandleClientDisconnect(HandleOdinMessagePayload payload)
        {
            LogDefault($"Odin Transport: Client received ClientDisonnect message, disconnecting.");
            ClientDisconnect();
        }

        private void HandleInvalidOdinMessage(HandleOdinMessagePayload payload)
        {
            Debug.LogError("Odin Transport: Received an invalid odin message payload.");
        }

        #endregion

        /// <summary>
        /// Removes event handlers.
        /// </summary>
        private void Cleanup()
        {
            _messageHandlers.Clear();
            RemoveOdinEventHandlers();
        }

        #region Debug Logging

        /// <summary>
        /// Used to allow users to set the debug level. 
        /// </summary>
        public enum OdinTransportDebugLevel
        {
            /// <summary>
            /// Disable Logging
            /// </summary>
            None = 0,

            /// <summary>
            /// Enable logging for important steps
            /// </summary>
            Log = 2,

            /// <summary>
            /// Log everything.
            /// </summary>
            Verbose = 3
        }

        private void LogVerbose(string message)
        {
            if (ShouldLogVerbose()) Debug.Log(message);
        }

        private void LogDefault(string message)
        {
            if (ShouldLogDefault()) Debug.Log(message);
        }

        private bool ShouldLogDefault()
        {
            return ShouldLog(OdinTransportDebugLevel.Log);
        }

        private bool ShouldLogVerbose()
        {
            return ShouldLog(OdinTransportDebugLevel.Verbose);
        }

        private bool ShouldLog(OdinTransportDebugLevel startLogLevel)
        {
            return debugLevel >= startLogLevel;
        }

        #endregion
    }
}