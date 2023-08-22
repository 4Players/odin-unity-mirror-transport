using System;
using System.Collections.Generic;
using Mirror;
using OdinNative.Core.Imports;
using OdinNative.Odin;
using OdinNative.Odin.Media;
using OdinNative.Odin.Peer;
using OdinNative.Odin.Room;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Struct that holds the information about a pending media stream. If 3D audio is enabled we need to add the PlaybackComponent
/// (ODINs live AudioSource connected to that peers microphone) to the peer's GameObject. However, sometimes the peer's GameObject
/// does not exist yet, as the OnMediaAdded event comes before the Server has sent the AddPlayer message to the client.
/// In these cases, we store all relevant information in this struct in an array and in each frame we check if the peer's GameObject
/// is available. If it is, we add the PlaybackComponent to the GameObject and remove the entry from the array. 
/// </summary>
public class OdinPendingMediaStream
{
    public MediaStream mediaStream;
    public int connectionId;
    public ulong peerId;
}

/// <summary>
/// The peer type identifies if the peer is a server or a client
/// </summary>
public enum OdinTransportPeerType
{
    Unknown = 0,
    Client = 1,
    Server = 2,
    Bot = 3
}

/// <summary>
/// Implementation of the User Data used by the Odin Transport. It's a simple flag that indicates if the peer is a server
/// or a client. As Mirror already provides tools to store user data like SyncVars, we don't need to use the Odin User Data
/// interface for that. However, if required, you can override this class and implement the IUserData interface to store
/// your own, additional data.
/// </summary>
public class OdinTransportUserData : IUserData
{
    /// <summary>
    /// The peer type, available is unknown, client, server and bot. Bots are scripted clients (i.e. with the NodeJS)
    /// and might be useful for advanced features. Unknown clients within the room (i.e. recorders or spectators) will
    /// be ignored and they will not become part of the mirror network. 
    /// </summary>
    public readonly OdinTransportPeerType PeerType;
    
    /// <summary>
    /// Returns true if the peer is a server
    /// </summary>
    public bool IsServer => PeerType == OdinTransportPeerType.Server;

    /// <summary>
    /// Creates a new instance with the given peer type
    /// </summary>
    /// <param name="type"></param>
    public OdinTransportUserData(OdinTransportPeerType type)
    {
        PeerType = type;
    }

    /// <summary>
    /// Always returns false as the constructor does not allow to create an empty instance
    /// </summary>
    /// <returns></returns>
    public bool IsEmpty()
    {
        return false;
    }

    /// <summary>
    /// Returns the byte array required by ODIN to store the user data
    /// </summary>
    /// <returns></returns>
    public byte[] ToBytes()
    {
        return BitConverter.GetBytes((short)PeerType);
    }
    
    /// <summary>
    /// Creates a new instance of this class by bytes received from ODIN. If the user data is invalid, the peer type
    /// will be set to unknown and the peer will be ignored.
    /// </summary>
    /// <param name="bytes"></param>
    /// <returns></returns>
    public static OdinTransportUserData FromBytes(byte[] bytes)
    {
        if (bytes == null || bytes.Length != sizeof(short))
        {
            Debug.LogWarning("Invalid OdinTransportUserData bytes, an unknown peer is in the room - ignoring");
            return new OdinTransportUserData(OdinTransportPeerType.Unknown);
        }

        OdinTransportPeerType value = (OdinTransportPeerType)BitConverter.ToInt16(bytes, 0);
        return new OdinTransportUserData(value);
    }
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
    [Tooltip("The maximum size of a package that can be sent or received. This should be smaller that the MTU of the network.")]
    public int maxPackageSize = 1024;
    
    /// <summary>
    /// If true, the transport will log all messages to the console
    /// </summary>
    [Tooltip("If true, the transport will log all messages to the console")]
    public bool debug = false;
    
    [Header("3D Audio")]
    [Tooltip("If true will use positional audio for the voice chat, i.e. the voice will be located at the position of the player")]
    public bool positionalAudio = false;
    
    [Tooltip("Defines the spatial blend of the audio source. 0.0 is 2D, 1.0 is 3D")]
    [Range(0.0f, 1.0f)]
    public float spatialBlend = 1.0f;

    /// <summary>
    /// Set to true after the connection to the ODIN room has been established
    /// </summary>
    private bool _isConnected = false;
    
    /// <summary>
    /// The connected room
    /// </summary>
    private Room _connectedRoom = null;
    
    /// <summary>
    /// Stores if this peer is a server (i.e. ServerStart has been called). If false, the ClientConnect has been called
    /// </summary>
    private bool _isServer = false;
    
    /// <summary>
    /// The room id we are connected to. This is the room id that the client will connect to and the server will create.
    /// </summary>
    private string _roomId;

    /// <summary>
    /// Stores the peer id of the server peer in the room. After the client connects to the ODIN room OnPeerJoined event
    /// is called. The client unpacks the Peers User Data and searches for the server peer. Once the server peer is found
    /// the client stores the peer id in this variable. This is used to send messages to the server peer.
    /// </summary>
    private ulong _hostPeerId = 0;

    /// <summary>
    /// Stores all pending media streams. This is required as the OnMediaAdded event is called before the player object
    /// has been created and 3D positional audio is enabled. In this case we have to wait until the player object is
    /// spawned.
    /// </summary>
    private List<OdinPendingMediaStream> _pendingMediaStreams = new List<OdinPendingMediaStream>();

    public Action<string> OnNoServerFound;
    

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
        OdinHandler.Instance.OnConnectionStateChanged.AddListener(OnConnectionStateChanged);

        if (positionalAudio)
        {
            OdinHandler.Instance.OnMediaAdded.AddListener(OnMediaAdded);
            OdinHandler.Instance.OnMediaRemoved.AddListener(OnMediaRemoved);   
        }
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
        OdinHandler.Instance.OnConnectionStateChanged.RemoveListener(OnConnectionStateChanged);

        // Remove media management events if we are using positional audio
        if (positionalAudio)
        {
            OdinHandler.Instance.OnMediaAdded.RemoveListener(OnMediaAdded);
            OdinHandler.Instance.OnMediaRemoved.RemoveListener(OnMediaRemoved);   
        }

        // We don't remove the OnRoomLeft event as we need it to reset the connection state, we'll clear that later
    }

    private void JoinOdinRoom(string roomId, OdinTransportPeerType peerType)
    {
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
        if (debug) Debug.Log($"ODIN Transport: ClientConnect to room id {room}");
        
        // If we are the host we don't need to connect again
        if (_isServer && _isConnected)
        {
            // Directly call the callback as we are already connected to the server
            OnClientConnected?.Invoke();
            return;
        }
        
        // If positional audio is enabled, we disable automatic media management of the Odin Handler and handle media
        // management ourselves
        if (positionalAudio)
        {
            OdinHandler.Instance.CreatePlayback = false;
        }
        
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
    public override void ClientSend(ArraySegment<byte> segment, int channelId = Channels.Reliable)
    {
        if (!_isServer && OdinHandler.Instance.Rooms.Contains(_roomId))
        {
            if (_hostPeerId <= 0)
            {
                Debug.LogError("Could not send message, host peer id is not set");
                OnClientError(TransportError.InvalidSend, "Could not send message, host peer id is not set");
                return;
            }

            if (_connectedRoom == null)
            {
                Debug.LogError("Could not send message, no room connected");
                return;
            } 
            
            if (debug) Debug.Log($"ODIN Transport: ClientSend, isServer: {_isServer}");
        
            byte[] data = new byte[segment.Count];
            Array.Copy(segment.Array, segment.Offset, data, 0, segment.Count);
        
            // If this is not the server we need to send the message to the server
            ulong[] peerIdList = new ulong[1];
            peerIdList[0] = (ulong)_hostPeerId;
            _connectedRoom?.SendMessageAsync(peerIdList, data);    
        
            OnClientDataSent?.Invoke(segment, channelId);   
        }
    }

    /// <summary>
    /// Disconnect a client, removes ODIN event handlers and leaves the ODIN room
    /// </summary>
    public override void ClientDisconnect()
    {
        if (NetworkManager.singleton.mode == NetworkManagerMode.ServerOnly)
        {
            // We are host, although we close our client connection we still want to keep the server running
            if (debug) Debug.Log($"ODIN Transport: Disconnecting client but leaving ODIN connection and server running");
            return;
        }
        
        if (debug) Debug.Log($"ODIN Transport: Client Disconnect");
        
        // Remove all listeners except the room left listener (we need that event to clean up)
        RemoveOdinEventHandlers();
        
        // Leave the room
        LeaveOdinRoom();
    }

    /// <summary>
    /// Returns the server uri for the ODIN room in standard ODIN notation, which is odin://<gateway address>/<room id>
    /// </summary>
    /// <returns></returns>
    public override Uri ServerUri()
    {
        return new Uri($"odin://{OdinHandler.Config.Server}/{_connectedRoom.GetRoomId()}");
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
        if (debug) Debug.Log("ODIN Transport: ServerStart");
        _isServer = true;
        
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
    /// Send data to the specified connection. Uses Odins SendMessage to send the data to the specified peer
    /// </summary>
    /// <param name="connectionId"></param>
    /// <param name="segment"></param>
    /// <param name="channelId"></param>
    public override void ServerSend(int connectionId, ArraySegment<byte> segment, int channelId = Channels.Reliable)
    {
        if (_isServer)
        {
            if (_connectedRoom == null)
            {
                Debug.LogError("Could not send message, no room connected");
                return;
            } 
            
            if (debug) Debug.Log($"ODIN Transport: ServerSend, {segment.Count} bytes to {connectionId}");
            ulong[] peerIdList = new ulong[1];
            peerIdList[0] = (ulong)connectionId;
        
            byte[] data = new byte[segment.Count];
            Array.Copy(segment.Array, segment.Offset, data, 0, segment.Count);

            _connectedRoom?.SendMessageAsync(peerIdList, data);
            OnServerDataSent?.Invoke(connectionId, segment, channelId);   
        }
    }

    /// <summary>
    /// This function does nothing as Odin does not support kicking peers from the server
    /// </summary>
    /// <param name="connectionId"></param>
    public override void ServerDisconnect(int connectionId)
    {
        // Do nothing - we cannot kick peers from the server
        Debug.LogWarning("ODIN Transport does not support kicking peers from the server.");
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
        if (_isServer)
        {
            if (debug) Debug.Log("ODIN Transport: ServerStop");
            // Remove all listeners except the room left listener (we need that event to clean up)
            RemoveOdinEventHandlers();

            _isServer = false;            
            
            // Leave the room
            OdinHandler.Instance.LeaveRoom(_roomId);
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
        if (debug) Debug.Log("ODIN Transport: Shutdown");
        ServerStop();
    }

    /// <summary>
    /// Check if we have any pending media streams and try to add a playback component to the player object
    /// </summary>
    public override void ClientLateUpdate()
    {
        base.ClientLateUpdate();
        
        if (!NetworkClient.active) return;
        
        if (_pendingMediaStreams.Count > 0)
        {
            for (int i=0;i<_pendingMediaStreams.Count;i++)
            {
                OdinPendingMediaStream mediaStream = _pendingMediaStreams[i];
                // Try to find a player object for the media stream, if found, remove from the list
                var playerIdentity = TryAddPlaybackComponent(mediaStream);
                if (playerIdentity != null)
                {
                    _pendingMediaStreams.RemoveAt(i);    
                }
            }
        }
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
        // Check if the room id matches the room id we are connected to for networking
        if (args.Room.GetRoomId() != _roomId)
        {
            return;
        }
        
        if (_isConnected) return;
        
        _isConnected = true;
        _connectedRoom = args.Room;

        if (!_isServer)
        {
            Debug.Log($"ODIN Transport: Connected to room with peers: {_connectedRoom.RemotePeers.Count}");
            foreach (var remotePeer in _connectedRoom.RemotePeers)
            {
                if (IsPeerServer(remotePeer))
                {
                    if (debug) Debug.Log("ODIN Transport: OnClientConnected called");
                    OnClientConnected?.Invoke();
                    return;
                }    
            }
            
            // Call the OnNoServerFound event
            OnNoServerFound?.Invoke(_connectedRoom.GetRoomId());
            
            // If we are here we did not find a server
            ClientDisconnect();
        }
    }
    
    /// <summary>
    /// Odin event handler for the room left event. It's used to send the OnClientDisconnected event to the network manager
    /// Also removes the listener for the room left event
    /// </summary>
    /// <param name="arg0"></param>
    private void OnRoomLeft(RoomLeftEventArgs arg0)
    {
        // Check if the room id matches the room id we are connected to for networking
        if (arg0.RoomName != _roomId)
        {
            return;
        }
        
        _connectedRoom = null;
        _isConnected = false;
        
        // Remote the listener for the room left event - now we are finished
        OdinHandler.Instance.OnRoomLeft.RemoveListener(OnRoomLeft);

        if (!_isServer)
        {
            if (debug) Debug.Log("ODIN Transport: OnClientDisconnected called");
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
    /// <param name="arg0"></param>
    /// <param name="arg1"></param>
    private void OnPeerJoined(object arg0, PeerJoinedEventArgs arg1)
    {
        if (_isServer)
        {
            // Make sure this is not another server
            if (IsPeerServer(arg1.Peer))
            {
                // We don't let this guy in - he will time out as he does not receive any answer from us
                Debug.LogWarning("Another server tried to connect, we are not letting him in.");
                ServerStop();
            }
            else
            {
                if (debug) Debug.Log($"ODIN Transport: OnServerConnected called with connectionId: {arg1.Peer.Id}");
                OnServerConnected?.Invoke((int)arg1.Peer.Id);   
            }
        }
        else
        {
            // This is a client and we got info that another client connected. We try to find the host and store the hosts
            // peer id so we can use it to send messages to the host.
            if (IsPeerServer(arg1.Peer))
            {
                _hostPeerId = arg1.Peer.Id;
                if (debug) Debug.Log($"ODIN Transport: Server found in room with peer id: {_hostPeerId}");
            }
        }
    }
    
    /// <summary>
    /// Handles the case when a peer leaves the room. If this is the server, we call the OnServerDisconnected event. If
    /// this peer is a client, we do nothing.
    /// </summary>
    /// <param name="arg0"></param>
    /// <param name="arg1"></param>
    private void OnPeerLeft(object arg0, PeerLeftEventArgs arg1)
    {
        if (_isServer)
        {
            if (debug) Debug.Log($"ODIN Transport: OnServerDisconnected called with connectionId: {arg1.PeerId}");
            OnServerDisconnected?.Invoke((int)arg1.PeerId);            
        }
        else
        {
            // We are the client, check if the server has left the room - then we disconnect
            if (arg1.PeerId == _hostPeerId)
            {
                OnClientError(TransportError.Unexpected, "The server has left the room.");
                LeaveOdinRoom();
            }
        }
    }
    
    /// <summary>
    /// This is called when a message is received from a peer, depending on the role of this peer, we call either trigger
    /// client message received or server message received.
    /// </summary>
    /// <param name="arg0"></param>
    /// <param name="arg1"></param>
    private void OnMessageReceived(object arg0, MessageReceivedEventArgs arg1)
    {
        if (_isServer)
        {
            //if (debug) Debug.Log($"ODIN Transport: OnServerDataReceived called with connectionId: {arg1.PeerId}, {arg1.Data.Length} bytes");
            OnServerDataReceived?.Invoke((int)arg1.PeerId, new ArraySegment<byte>(arg1.Data), Channels.Reliable);
        }
        else
        {
            // ODIN SDK sends RoomJoined message at the very last moment, sometimes, a message is received before the RoomJoined event is fired
            // Mirror expects OnClientConnected to be called first, so we call it here if it hasn't been called yet
            if (!_isConnected)
            {
                _isConnected = true;
                _connectedRoom = arg0 as Room;

                if (debug) Debug.Log("ODIN Transport: OnClientConnected called in OnMessageReceived");
                OnClientConnected?.Invoke();
            }
            
            //if (debug) Debug.Log($"ODIN Transport: OnClientDataReceived called, {arg1.Data.Length} bytes");
            OnClientDataReceived?.Invoke(new ArraySegment<byte>(arg1.Data), Channels.Reliable);   
        }
    }

    /// <summary>
    /// Handles Odin connection changes. We use this to disconnect the client
    /// </summary>
    /// <param name="arg0"></param>
    /// <param name="arg1"></param>
    private void OnConnectionStateChanged(object arg0, ConnectionStateChangedEventArgs arg1)
    {
        // Check if the room id matches the room id we are connected to for networking
        var room = arg0 as Room;
        if (room?.GetRoomId() != _roomId)
        {
            return;
        }
        
        if (_isServer)
        {
            if (arg1.ConnectionState == NativeBindings.OdinRoomConnectionState.Disconnected)
            {
                // We are the server and the connection to ODIN disconnected. We just clean up
            }
        }

        Debug.Log($"ODIN Transport: OnConnectionStateChanged: {arg1.ConnectionState}, {arg1.ChangeReason}");
    }

    /// <summary>
    /// Handles OnMediaAdded events sent from ODIN. This is called when a new media stream is added to the room. If
    /// positional audio is activated we need to add a playback component to the player object. If this is a server, we
    /// need to set the connectionId to 0 as the server is always connection 0 (although it has a different PeerId, the
    /// host is a special case). If the players game object has not been spawned yet, we put the media stream and other
    /// info in the OdinPendingMediaStream struct and wait for the player to be spawned (checked in ClientLateUpdate).
    /// </summary>
    /// <param name="arg0"></param>
    /// <param name="arg1"></param>
    private void OnMediaAdded(object arg0, MediaAddedEventArgs arg1)
    {
        // Don't handle media if this is just a server
        if (!NetworkClient.active) return;

        var odinPendingMediaStream = new OdinPendingMediaStream();
        odinPendingMediaStream.mediaStream = arg1.Media;
        odinPendingMediaStream.peerId = arg1.Peer.Id;
        odinPendingMediaStream.connectionId = (int)arg1.Media.GetPeerId();
        if (IsPeerServer(arg1.Peer))
        {
            // For hosts, the connection id is 0, so we need to use that as the peer id
            odinPendingMediaStream.connectionId = 0;
        }
        
        var playerObject = TryAddPlaybackComponent(odinPendingMediaStream);
        if (playerObject == null)
        {
            _pendingMediaStreams.Add(odinPendingMediaStream);
        }
    }
    
    /// <summary>
    /// This does nothing as the PlaybackComponent destroys itself when the media stream is removed
    /// </summary>
    /// <param name="arg0"></param>
    /// <param name="arg1"></param>
    private void OnMediaRemoved(object arg0, MediaRemovedEventArgs arg1)
    {
        // Nothing to do as the playback component is auto destroyed
    }

    #endregion
    
    #region Odin Positional Audio
    
    /// <summary>
    /// Helper function to find the players game object for a given connection id. The connection id is the same as the
    /// the peer id in ODIN - with the exception of the host, which is always 0 (even though it has a different peer id).
    /// This is how mirror handles it, and we need to adept accordingly.
    /// </summary>
    /// <param name="connectionId"></param>
    /// <returns></returns>
    private OdinPositionalAudio GetPlayerForConnectionId(int connectionId)
    {
        // Find in spawned
        foreach (var identity in NetworkClient.spawned.Values)
        {
            if (identity.TryGetComponent<OdinPositionalAudio>(out OdinPositionalAudio positionalAudio))
            {
                if (positionalAudio.connectionId == connectionId)
                {
                    Debug.Log($"Found the player with net id {positionalAudio.netId} for the connection id {connectionId}");
                    return positionalAudio;
                }
            }
        }

        return null;
    }
    
    /// <summary>
    /// Try to add a playback component to the player object. If the player object has not been spawned yet, we do nothing
    /// The `spatialBlend` value is set to the `spatialBlend` value set globally in this object. 
    /// </summary>
    /// <param name="pendingMediaStream"></param>
    /// <returns></returns>
    private NetworkIdentity TryAddPlaybackComponent(OdinPendingMediaStream pendingMediaStream)
    {
        var connectionId = (int)pendingMediaStream.connectionId;
        //Debug.Log(connectionId);
        OdinPositionalAudio positionalAudio = GetPlayerForConnectionId(connectionId);
        if (positionalAudio)
        {
            var playbackComponent = OdinHandler.Instance.AddPlaybackComponent(positionalAudio.PlaybackSource ? positionalAudio.PlaybackSource : positionalAudio.gameObject, NetworkManager.singleton.networkAddress, pendingMediaStream.peerId, pendingMediaStream.mediaStream.Id);
                        
            // Activate full 3D audio
            playbackComponent.PlaybackSource.spatialBlend = spatialBlend;
            playbackComponent.AutoDestroyAudioSource = true;
            playbackComponent.AutoDestroyMediaStream = true;
                
            Debug.Log("ODIN Transport: OnMediaAdded, found Player object: " + positionalAudio.gameObject.name);
            return positionalAudio.netIdentity;
        }

        return null;
    }
    
    #endregion
}
