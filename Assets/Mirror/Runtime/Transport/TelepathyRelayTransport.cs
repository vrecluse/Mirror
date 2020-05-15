// wraps Telepathy for use as HLAPI TransportLayer
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net;
using UnityEngine;
using UnityEngine.Serialization;

namespace Mirror
{
    //[HelpURL("https://github.com/vis2k/Telepathy/blob/master/README.md")]
    public class TelepathyRelayTransport : Transport
    {
        // scheme used by this transport
        // "tcp4" means tcp with 4 bytes header, network byte order
        public const string Scheme = "tcp4";

        public string relayServerAddress = "localhost";
        public ushort port = 7777;

        [Tooltip("Nagle Algorithm can be disabled by enabling NoDelay")]
        public bool NoDelay = true;

        // Deprecated 04/08/2019
        [EditorBrowsable(EditorBrowsableState.Never), Obsolete("Use MaxMessageSizeFromClient or MaxMessageSizeFromServer instead.")]
        public int MaxMessageSize
        {
            get => serverMaxMessageSize;
            set => serverMaxMessageSize = clientMaxMessageSize = value;
        }

        [Header("Server")]
        [Tooltip("Protect against allocation attacks by keeping the max message size small. Otherwise an attacker might send multiple fake packets with 2GB headers, causing the server to run out of memory after allocating multiple large packets.")]
        [FormerlySerializedAs("MaxMessageSize")] public int serverMaxMessageSize = 16 * 1024;

        [Tooltip("Server processes a limit amount of messages per tick to avoid a deadlock where it might end up processing forever if messages come in faster than we can process them.")]
        public int serverMaxReceivesPerTick = 10000;

        [Header("Client")]
        [Tooltip("Protect against allocation attacks by keeping the max message size small. Otherwise an attacker host might send multiple fake packets with 2GB headers, causing the connected clients to run out of memory after allocating multiple large packets.")]
        [FormerlySerializedAs("MaxMessageSize")] public int clientMaxMessageSize = 16 * 1024;

        [Tooltip("Client processes a limit amount of messages per tick to avoid a deadlock where it might end up processing forever if messages come in faster than we can process them.")]
        public int clientMaxReceivesPerTick = 1000;


        protected Telepathy.Client client = new Telepathy.Client();

        protected bool IsHost;

        void Awake()
        {
            // tell Telepathy to use Unity's Debug.Log
            Telepathy.Logger.Log = Debug.Log;
            Telepathy.Logger.LogWarning = Debug.LogWarning;
            Telepathy.Logger.LogError = Debug.LogError;

            // configure
            client.NoDelay = NoDelay;
            client.MaxMessageSize = clientMaxMessageSize;

            Debug.Log("TelepathyTransport initialized!");
        }

        public override bool Available()
        {
            // C#'s built in TCP sockets run everywhere except on WebGL
            return Application.platform != RuntimePlatform.WebGLPlayer;
        }

        // client
        public override bool ClientConnected() => client.Connected;
        public override void ClientConnect(string address)
        {
            IsHost = false;
            client.Connect(address, port);
        }

        public override void ClientConnect(Uri uri)
        {
            if (uri.Scheme != Scheme)
                throw new ArgumentException($"Invalid url {uri}, use {Scheme}://host:port instead", nameof(uri));

            int serverPort = uri.IsDefaultPort ? port : uri.Port;
            client.Connect(uri.Host, serverPort);
        }
        public override bool ClientSend(int channelId, ArraySegment<byte> segment)
        {
            // telepathy doesn't support allocation-free sends yet.
            // previously we allocated in Mirror. now we do it here.
            byte[] data = new byte[segment.Count];
            Array.Copy(segment.Array, segment.Offset, data, 0, segment.Count);
            return client.Send(data);
        }

        bool ProcessClientMessage()
        {
            if (IsHost) { return false; }
            if (client.GetNextMessage(out Telepathy.Message message))
            {
                switch (message.eventType)
                {
                    case Telepathy.EventType.Connected:
                        OnClientConnected.Invoke();
                        break;
                    case Telepathy.EventType.Data:
                        ClientHandleRelayData(message.data);
                        break;
                    case Telepathy.EventType.Disconnected:
                        OnClientDisconnected.Invoke();
                        break;
                    default:
                        // TODO:  Telepathy does not report errors at all
                        // it just disconnects,  should be fixed
                        OnClientDisconnected.Invoke();
                        break;
                }
                return true;
            }
            return false;
        }

        private void ClientHandleRelayData(byte[] data)
        {
            // ignore first byte, it is connection Id
            OnClientDataReceived.Invoke(new ArraySegment<byte>(data, 1, data.Length - 1), Channels.DefaultReliable);
        }

        public override void ClientDisconnect() => client.Disconnect();

        // IMPORTANT: set script execution order to >1000 to call Transport's
        //            LateUpdate after all others. Fixes race condition where
        //            e.g. in uSurvival Transport would apply Cmds before
        //            ShoulderRotation.LateUpdate, resulting in projectile
        //            spawns at the point before shoulder rotation.
        public void LateUpdate()
        {
            // note: we need to check enabled in case we set it to false
            // when LateUpdate already started.
            // (https://github.com/vis2k/Mirror/pull/379)
            if (!enabled)
                return;

            // process a maximum amount of client messages per tick
            for (int i = 0; i < clientMaxReceivesPerTick; ++i)
            {
                // stop when there is no more message
                if (!ProcessClientMessage())
                {
                    break;
                }

                // Some messages can disable transport
                // If this is disabled stop processing message in queue
                if (!enabled)
                {
                    break;
                }
            }

            // process a maximum amount of server messages per tick
            for (int i = 0; i < serverMaxReceivesPerTick; ++i)
            {
                // stop when there is no more message
                if (!ProcessServerMessage())
                {
                    break;
                }

                // Some messages can disable transport
                // If this is disabled stop processing message in queue
                if (!enabled)
                {
                    break;
                }
            }
        }

        public override Uri ServerUri()
        {
            UriBuilder builder = new UriBuilder();
            builder.Scheme = Scheme;
            builder.Host = Dns.GetHostName();
            builder.Port = port;
            return builder.Uri;
        }

        // server
        public override bool ServerActive() => IsHost && client.Connected;
        public override void ServerStart()
        {
            IsHost = true;
            client.Connect(relayServerAddress, port);
        }
        public override bool ServerSend(List<int> connectionIds, int channelId, ArraySegment<byte> segment)
        {
            if (segment.Count == 0)
            {
                // Currently empty message acts as disconnect message
                Debug.LogError("Cant send empty message with relay");
                return false;
            }

            // telepathy doesn't support allocation-free sends yet.
            // previously we allocated in Mirror. now we do it here.
            // +1 so that we can write connectionId
            byte[] data = new byte[segment.Count + 1];
            Array.Copy(segment.Array, segment.Offset, data, 1, segment.Count);

            // send to all
            bool result = true;
            foreach (int connectionId in connectionIds)
            {
                data[0] = (byte)connectionId;
                result &= client.Send(data);
            }

            return result;
        }
        public bool ProcessServerMessage()
        {
            if (!IsHost) { return false; }
            if (client.GetNextMessage(out Telepathy.Message message))
            {
                switch (message.eventType)
                {
                    case Telepathy.EventType.Connected:
                        // host connected to relay server
                        // do nothing we don't care
                        break;
                    case Telepathy.EventType.Data:
                        HostHandleRelayData(message.data);
                        break;
                    case Telepathy.EventType.Disconnected:
                        // host disonnected
                        // this is critical fail
                        throw new Exception("Host Disconneceted from relay server");
                    default:
                        // TODO handle errors from Telepathy when telepathy can report errors
                        OnServerDisconnected.Invoke(message.connectionId);
                        break;
                }
                return true;
            }
            return false;
        }

        private void HostHandleRelayData(byte[] data)
        {
            Debug.Assert(data.Length >= 2, "Message from relay must have connection Id and message type");
            byte senderId = data[0];
            Telepathy.EventType messageType = (Telepathy.EventType)data[1];

            switch (messageType)
            {
                case Telepathy.EventType.Connected:
                    OnServerConnected.Invoke(senderId);

                    break;
                case Telepathy.EventType.Data:
                    OnServerDataReceived.Invoke(senderId, new ArraySegment<byte>(data, 2, data.Length - 2), Channels.DefaultReliable);
                    break;
                case Telepathy.EventType.Disconnected:
                    OnServerDisconnected.Invoke(senderId);
                    break;
                default:
                    // TODO handle errors from Telepathy when telepathy can report errors
                    OnServerDisconnected.Invoke(senderId);
                    break;
            }
        }

        public override bool ServerDisconnect(int connectionId)
        {
            return client.Send(new byte[1] { (byte)connectionId });
        }

        public override string ServerGetClientAddress(int connectionId)
        {
            return "unknown";

            //try
            //{
            //    return server.GetClientAddress(connectionId);
            //}
            //catch (SocketException)
            //{
            //    // using server.listener.LocalEndpoint causes an Exception
            //    // in UWP + Unity 2019:
            //    //   Exception thrown at 0x00007FF9755DA388 in UWF.exe:
            //    //   Microsoft C++ exception: Il2CppExceptionWrapper at memory
            //    //   location 0x000000E15A0FCDD0. SocketException: An address
            //    //   incompatible with the requested protocol was used at
            //    //   System.Net.Sockets.Socket.get_LocalEndPoint ()
            //    // so let's at least catch it and recover
            //    return "unknown";
            //}
        }
        public override void ServerStop()
        {
            if (IsHost)
            {
                client.Disconnect();
            }
        }

        // common
        public override void Shutdown()
        {
            Debug.Log("TelepathyTransport Shutdown()");
            client.Disconnect();
        }

        public override int GetMaxPacketSize(int channelId)
        {
            return serverMaxMessageSize;
        }

        public override string ToString()
        {
            if (IsHost && client.Connected)
            {
                // printing server.listener.LocalEndpoint causes an Exception
                // in UWP + Unity 2019:
                //   Exception thrown at 0x00007FF9755DA388 in UWF.exe:
                //   Microsoft C++ exception: Il2CppExceptionWrapper at memory
                //   location 0x000000E15A0FCDD0. SocketException: An address
                //   incompatible with the requested protocol was used at
                //   System.Net.Sockets.Socket.get_LocalEndPoint ()
                // so let's use the regular port instead.
                return "TelepathyRelay Host port: " + port;
            }
            else if (!IsHost && client.Connected)
            {
                return "TelepathyRelay Client ip: " + client.client.Client.RemoteEndPoint;
            }
            return "Telepathy (inactive/disconnected)";
        }
    }
}
