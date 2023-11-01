using System;
using OdinNative.Odin;
using UnityEngine;

namespace Odin.Networking.Mirror
{
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
}