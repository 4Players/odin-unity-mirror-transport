using Mirror;
using OdinNative.Odin.Media;
using OdinNative.Odin.Peer;
using OdinNative.Odin.Room;
using UnityEngine;

public class OdinPositionalAudio : NetworkBehaviour
{
    [SerializeField] private string proximityRoomName = "proximity";

    [SyncVar(hook = nameof(OnPeerIdUpdated))]
    private int _peerId = -1;

    [Tooltip(
        "The audio source container which is used to play back the audio. Attach a Game Object that represents the mouth of the avatar or leave blank to use the root of the avatar.")]
    public GameObject playbackSource = null;

    private bool _isWaitingForNetworkJoin = false;

    public override void OnStartLocalPlayer()
    {
        base.OnStartLocalPlayer();
        OdinHandler.Instance.OnRoomJoined.AddListener(OnJoinedRoom);
        if(OdinHandler.Instance.Rooms.Contains(NetworkManager.singleton.networkAddress))
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
        if (_isWaitingForNetworkJoin && joinedRoomName == NetworkManager.singleton.networkAddress)
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
            GameObject target = playbackSource ? playbackSource : gameObject;
            string fullRoomName = GetFullProximityRoomName();
            Room proximityRoom = OdinHandler.Instance.Rooms[GetFullProximityRoomName()];
            if (null != proximityRoom)
            {
                ulong targetPeerId = (ulong)newPeerId;
                Peer targetPeer = proximityRoom.RemotePeers[targetPeerId];
                foreach (MediaStream targetPeerMedia in targetPeer.Medias)
                {
                    OdinHandler.Instance.AddPlaybackComponent(target, fullRoomName, targetPeerId,
                        targetPeerMedia.Id);
                }
            }
        }
    }

    private string GetFullProximityRoomName()
    {
        return proximityRoomName + NetworkManager.singleton.networkAddress;
    }
}