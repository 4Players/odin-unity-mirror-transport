using System;
using Mirror;
using OdinNative.Odin.Media;
using OdinNative.Odin.Peer;
using OdinNative.Odin.Room;
using OdinNative.Unity.Audio;
using UnityEngine;

/// <summary>
/// </summary>
public class OdinPositionalAudio : NetworkBehaviour
{
    [SerializeField] private string proximityRoomName = "proximity";

    [Tooltip(
        "The audio source container which is used to play back the audio. Attach a Game Object that represents the mouth of the avatar or leave blank to use the root of the avatar.")]
    [SerializeField]
    private Transform playbackSource;

    [SerializeField] private PlaybackComponent playbackPrefab;


    [SyncVar(hook = nameof(OnPeerIdUpdated))]
    private int _peerId = -1;

    private bool _isWaitingForNetworkJoin = false;

    private void Awake()
    {
        if (!playbackPrefab)
        {
            Debug.LogWarning(
                $"OdinPositionalAudio: playbackPrefab not set on object {gameObject.name}, will spawn PlaybackComponent with default settings.");
        }
    }

    public override void OnStartLocalPlayer()
    {
        base.OnStartLocalPlayer();
        OdinHandler.Instance.OnRoomJoined.AddListener(OnJoinedRoom);
        if (OdinHandler.Instance.Rooms.Contains(NetworkManager.singleton.networkAddress))
        {
            OdinHandler.Instance.JoinRoom(GetFullProximityRoomName());
        }
        else
        {
            _isWaitingForNetworkJoin = true;
        }
    }

    public override void OnStopLocalPlayer()
    {
        base.OnStopLocalPlayer();
        OdinHandler.Instance.OnRoomJoined.RemoveListener(OnJoinedRoom);
    }

    private void OnJoinedRoom(RoomJoinedEventArgs joinRoomArgs)
    {
        string joinedRoomName = joinRoomArgs.Room.Config.Name;
        if (_isWaitingForNetworkJoin && joinedRoomName ==  GetNetworkingRoomName())
        {
            OdinHandler.Instance.JoinRoom(GetFullProximityRoomName());
            _isWaitingForNetworkJoin = false;
        }

        if (joinedRoomName == GetFullProximityRoomName())
        {
            ulong selfPeerId = joinRoomArgs.Room.Self.Id;
            CmdUpdatePeerId((int)selfPeerId);
        }
    }


    [Command]
    private void CmdUpdatePeerId(int newPeerId)
    {
        _peerId = newPeerId;
    }

    private void OnPeerIdUpdated(int oldPeerId, int newPeerId)
    {
        if (!isLocalPlayer)
        {
            Transform target = playbackSource ? playbackSource : transform;
            string fullRoomName = GetFullProximityRoomName();
            Room proximityRoom = OdinHandler.Instance.Rooms[GetFullProximityRoomName()];
            if (null != proximityRoom)
            {
                ulong targetPeerId = (ulong)newPeerId;
                Peer targetPeer = proximityRoom.RemotePeers[targetPeerId];
                foreach (MediaStream targetPeerMedia in targetPeer.Medias)
                {
                    if (playbackPrefab)
                    {
                        SpawnPlaybackPrefab(target, fullRoomName, targetPeerId, targetPeerMedia.Id);
                    }
                    else
                    {
                        ManualPlaybackSetup(target, fullRoomName, targetPeerId, targetPeerMedia.Id);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Creates a new GameObject, adds an Audio Source + PlaybackComponent and sets the Behaviors up from scratch.
    /// Only use this, if there is no custom Playback Prefab provided.
    /// </summary>
    /// <param name="parent">Parent transform of the Playback, e.g. an avatar's mouth</param>
    /// <param name="roomName">The ODIN room name</param>
    /// <param name="peerId">The peer Id</param>
    /// <param name="mediaId">The media stream id</param>
    private void ManualPlaybackSetup(Transform parent, string roomName, ulong peerId,
        long mediaId)
    {
        GameObject playbackObject = new GameObject("ProximityVoicePlayback");
        playbackObject.transform.parent = parent;
        playbackObject.transform.localPosition = Vector3.zero;
        playbackObject.transform.localRotation = Quaternion.identity;

        AudioSource audioSource = playbackObject.AddComponent<AudioSource>();
        audioSource.loop = true;
        audioSource.spatialBlend = 1.0f;
        audioSource.minDistance = 2.0f;
        audioSource.maxDistance = 100.0f;
        
        PlaybackComponent playback = playbackObject.AddComponent<PlaybackComponent>();
        playback.PlaybackSource = audioSource;
        playback.RoomName = roomName;
        playback.PeerId = peerId;
        playback.MediaStreamId = mediaId;
    }

    /// <summary>
    /// Creates a new instance of the playback Prefab and sets up the PlaybackComponent to connect to the media stream.
    /// </summary>
    /// <param name="parent">Parent transform of the Playback, e.g. an avatar's mouth</param>
    /// <param name="roomName">The ODIN room name</param>
    /// <param name="peerId">The peer Id</param>
    /// <param name="mediaId">The media stream id</param>
    private void SpawnPlaybackPrefab(Transform parent, string roomName, ulong peerId, long mediaId)
    {
        GameObject playbackInstanceObject = Instantiate(playbackPrefab.gameObject, parent);
        playbackInstanceObject.transform.localPosition = Vector3.zero;
        playbackInstanceObject.transform.localRotation = Quaternion.identity;

        PlaybackComponent playback = playbackInstanceObject.GetComponent<PlaybackComponent>();
        playback.RoomName = roomName;
        playback.PeerId = peerId;
        playback.MediaStreamId = mediaId;
    }

    /// <summary>
    /// Return the proximity room name consisting of a prefix (proximityRoomName) and the networking room name as a suffix.
    /// </summary>
    /// <returns></returns>
    private string GetFullProximityRoomName()
    {
        
        return $"{proximityRoomName}-{GetNetworkingRoomName()}";
    }

    /// <summary>
    /// Retrieves the networking room name from the Mirror Server Uri.
    /// </summary>
    /// <returns>The networking room name.</returns>
    private string GetNetworkingRoomName()
    {
        Uri serverUri = Transport.active.ServerUri();
        return serverUri.Segments[^1]; // last segment contains networking room name
    }
}