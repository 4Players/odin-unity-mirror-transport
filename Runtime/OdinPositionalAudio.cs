using Mirror;
using UnityEngine;

public class OdinPositionalAudio : NetworkBehaviour
{
    [SyncVar(hook = nameof(OnConnectionIdChanged))]
    public int connectionId = 0;

    [Tooltip("The audio source container which is used to play back the audio. Attach a Game Object that represents the mouth of the avatar or leave blank to use the root of the avatar.")]
    public GameObject PlaybackSource = null;
    
    public override void OnStartServer()
    {
        var connId = connectionToClient?.connectionId ?? 0;
        this.connectionId = connId;
    }

    private void OnConnectionIdChanged(int oldConnectionId, int newConnectionId)
    {
        Debug.Log($"Received connection id from Server: {newConnectionId} with netId {netId}");
    }
}
