using System;

namespace Odin.Networking.Mirror
{
    /// <summary>
    /// Type prefix that's put in front of messages to identify their type.
    /// </summary>
    public enum OdinMessageType
    {
        /// <summary>
        /// Invalid message type, discard.
        /// </summary>
        Invalid,

        /// <summary>
        /// The default message type for mirror, containing data from the networking solution.
        /// </summary>
        Default,

        /// <summary>
        /// Does not contain solution specific networking data. Signals client to disconnect from server.
        /// </summary>
        DisconnectClient
    }

    /// <summary>
    /// Struct for holding the data sent with each network message.
    /// </summary>
    public readonly struct OdinTransportMessage
    {
        /// <summary>
        /// The message type. Used by the transport layer to determine how to handle the content.
        /// </summary>
        public readonly OdinMessageType messageType;

        /// <summary>
        /// The networking message content
        /// </summary>
        public readonly ArraySegment<byte> content;

        /// <summary>
        /// Create an empty transport message
        /// </summary>
        /// <param name="type"></param>
        public OdinTransportMessage(OdinMessageType type) : this(type, ArraySegment<byte>.Empty)
        {
        }

        public OdinTransportMessage(OdinMessageType type, ArraySegment<byte> content)
        {
            this.messageType = type;
            this.content = content;
        }

        public byte[] ToBytes()
        {
            byte[] data;
            int messageTypeSize = sizeof(short);
            if (null != content && null != content.Array && content.Count > 0)
            {
                data = new byte[content.Count + messageTypeSize];
                // copy message content into byte array
                Array.Copy(content.Array, content.Offset, data, messageTypeSize, content.Count);
            }
            else
            {
                data = new byte[messageTypeSize];
            }

            // message type enum is cast to short
            byte[] typeBytes = BitConverter.GetBytes((short)messageType);

            // copy message type bytes into byte array;
            Array.Copy(typeBytes, 0, data, 0, messageTypeSize);

            return data;
        }

        private static int GetMessageTypeSize()
        {
            return sizeof(short);
        }

        public int GetByteSize()
        {
            return GetMessageTypeSize() + content.Count;
        }

        public static OdinTransportMessage FromBytes(byte[] data)
        {
            if (null == data || data.Length < GetMessageTypeSize())
                return new OdinTransportMessage(OdinMessageType.Invalid);

            // Retrieve Odin Transport Message prefix
            byte[] odinMessageTypeBytes = new ArraySegment<byte>(data, 0, GetMessageTypeSize()).ToArray();
            var odinMessageType = (OdinMessageType)BitConverter.ToInt16(odinMessageTypeBytes);

            // Extract the mirror data segment
            ArraySegment<byte> mirrorDataSegment =
                new ArraySegment<byte>(data, GetMessageTypeSize(), data.Length - GetMessageTypeSize());

            return new OdinTransportMessage(odinMessageType, mirrorDataSegment);
        }
    }
}