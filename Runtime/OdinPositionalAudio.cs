using System;
using Mirror;
using OdinNative.Odin.Media;
using OdinNative.Odin.Peer;
using OdinNative.Odin.Room;
using OdinNative.Unity.Audio;
using UnityEngine;
using UnityEngine.Serialization;

namespace Odin.Networking.Mirror
{
    /// <summary>
    /// Automatically spawns a PlaybackComponent for the NetworkIdentity this behaviour is connected to. Will either use the
    /// provided playbackPrefab or setup a GameObject for playback containing a PlaybackComponent and AudioSource with 3d blending
    /// enabled.
    /// 
    /// This behaviour will automatically determine the Media Stream the connected NetworkIdentity belongs to.
    /// </summary>
    public class OdinPositionalAudio : NetworkBehaviour
    {
        /// <summary>
        /// Prefix for the positional ODIN room name. 
        /// </summary>
        [FormerlySerializedAs("proximityRoomName")] [SerializeField]
        private string positionalRoomName = "proximity";

        [Tooltip(
            "The audio source container which is used to play back the audio. Attach a Game Object that represents the mouth of the avatar or leave blank to use the root of the avatar.")]
        [SerializeField]
        private Transform playbackSource;

        /// <summary>
        /// Will be spawned on playbackSource and setup to the correct Media Stream.
        /// </summary>
        [SerializeField] private PlaybackComponent playbackPrefab;

        /// <summary>
        /// The ODIN peer id of the owning NetworkIdentity
        /// </summary>
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
            // If we're already connected to the networking room, directly join the proximity room name, otherwise wait until connected.
            if (OdinHandler.Instance.Rooms.Contains(GetNetworkingRoomName()))
            {
                OdinHandler.Instance.JoinRoom(GetFullPositionalRoomName());
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
            // if we were waiting to join the networking room, join the positional audio room next
            if (_isWaitingForNetworkJoin && joinedRoomName == GetNetworkingRoomName())
            {
                OdinHandler.Instance.JoinRoom(GetFullPositionalRoomName());
                _isWaitingForNetworkJoin = false;
            }

            // if instead we were waiting to join the positional room name, extract the peer id and sync to remote
            // representations of the network identity
            if (joinedRoomName == GetFullPositionalRoomName())
            {
                ulong selfPeerId = joinRoomArgs.Room.Self.Id;
                CmdUpdatePeerId((int)selfPeerId);
            }
        }


        /// <summary>
        /// Notify the server, that ODIN peer id of network identity is known and let server replicate change to other clients.
        /// </summary>
        /// <param name="newPeerId">New ODIN peer id.</param>
        [Command]
        private void CmdUpdatePeerId(int newPeerId)
        {
            _peerId = newPeerId;
        }

        /// <summary>
        /// Handle peer id being updated by spawning a PlaybackComponent for the peer.
        /// </summary>
        /// <param name="oldPeerId"></param>
        /// <param name="newPeerId"></param>
        private void OnPeerIdUpdated(int oldPeerId, int newPeerId)
        {
            // only start playback on remote representations.
            if (isLocalPlayer)
                return;

            Transform target = playbackSource ? playbackSource : transform;
            string fullRoomName = GetFullPositionalRoomName();
            if (!OdinHandler.Instance.Rooms.Contains(GetFullPositionalRoomName()))
                return;

            Room proximityRoom = OdinHandler.Instance.Rooms[GetFullPositionalRoomName()];
            if (null == proximityRoom)
                return;

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
        public string GetFullPositionalRoomName()
        {
            return $"{positionalRoomName}-{GetNetworkingRoomName()}";
        }

        /// <summary>
        /// Retrieves the networking room name from the Mirror Server Uri.
        /// </summary>
        /// <returns>The networking room name.</returns>
        public string GetNetworkingRoomName()
        {
            Uri serverUri = Transport.active.ServerUri();
            return serverUri.Segments[^1]; // last segment contains networking room name
        }
    }
}