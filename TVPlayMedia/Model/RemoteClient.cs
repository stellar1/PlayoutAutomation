﻿#undef DEBUG

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using TAS.Common;
using TAS.Server.Common;
using TAS.Server.Interfaces;
using TAS.Server.Remoting;
using WebSocketSharp;
using Newtonsoft.Json;

namespace TAS.Client.Model
{
    public class RemoteClient: IRemoteClient
    {
        readonly string _address;
        WebSocket _clientSocket;
        AutoResetEvent _messageHandler = new AutoResetEvent(false);
        readonly JsonSerializer _serializer;
        ConcurrentDictionary<Guid, WebSocketMessage> _receivedMessages = new ConcurrentDictionary<Guid, WebSocketMessage>();
        ConcurrentDictionary<Guid, IDto> _knownObjects = new ConcurrentDictionary<Guid, IDto>();
        const int query_timeout = 1000;

        public event EventHandler<WebSocketMessageEventArgs> EventNotification;
        public event EventHandler OnOpen;
        public event EventHandler<CloseEventArgs> OnClose;
        
        public RemoteClient(string host)
        {
            _address = host;
            _serializer = JsonSerializer.CreateDefault();
        }
        
        public JsonConverter[] CreationConverters
        {
            set
            {
                _serializer.Converters.Clear();
                foreach (JsonConverter c in value)
                    _serializer.Converters.Add(c);

            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        public void Initialize()
        {
            try
            {
                if (_clientSocket != null)
                {
                    _clientSocket.OnOpen -= _clientSocket_OnOpen;
                    _clientSocket.OnClose -= _clientSocket_OnClose;  
                    _clientSocket.OnMessage -= _clientSocket_OnMessage;
                }
                _clientSocket = new WebSocket(string.Format("ws://{0}/MediaManager", _address));
                _clientSocket.OnOpen += _clientSocket_OnOpen;
                _clientSocket.OnClose += _clientSocket_OnClose;
                _clientSocket.OnMessage += _clientSocket_OnMessage;
                _clientSocket.OnError += _clientSocket_OnError;
                _clientSocket.Connect();
            }
            catch (Exception e)
            {
                Debug.WriteLine(e, "Error initializing MediaManager remote interface");
            }
        }

        private void _clientSocket_OnError(object sender, WebSocketSharp.ErrorEventArgs e)
        {
            Debug.WriteLine(e.Exception);
        }

        private void _clientSocket_OnClose(object sender, CloseEventArgs e)
        {
            var h = OnClose;
            if (h != null)
                h(this, e);
        }

        private void _clientSocket_OnMessage(object sender, MessageEventArgs e)
        {
            WebSocketMessage message = JsonConvert.DeserializeObject<WebSocketMessage>(e.Data);
            if (message.MessageType == WebSocketMessage.WebSocketMessageType.EventNotification)
            {
                var h = EventNotification;
                if (h != null)
                    h(this, new WebSocketMessageEventArgs(message));
            }
            else
            {
                _receivedMessages[message.MessageGuid] = message;
                _messageHandler.Set();
                Debug.WriteLine(message, "Received");
            }
        }

        private void _clientSocket_OnOpen(object sender, EventArgs e)
        {
            var h = OnOpen;
            if (h != null)
                h(this, EventArgs.Empty);
        }

        private WebSocketMessage WaitForResponse(WebSocketMessage sendedMessage)
        {
            Func<WebSocketMessage> resultFunc = new Func<WebSocketMessage>(() =>
           {
               WebSocketMessage response;
               Stopwatch timeout = Stopwatch.StartNew();
               do
               {
                   _messageHandler.WaitOne(query_timeout);
                   if (_receivedMessages.TryRemove(sendedMessage.MessageGuid, out response))
                       return response;
               }
               while (timeout.ElapsedMilliseconds < query_timeout);
               throw new TimeoutException(string.Format("Didn't received response from server within {0} milliseconds.", query_timeout));
           });
            IAsyncResult funcAsyncResult = resultFunc.BeginInvoke(null, null);
            funcAsyncResult.AsyncWaitHandle.WaitOne();
            return resultFunc.EndInvoke(funcAsyncResult);
        }

        public T Deserialize<T>(object o)
        {
            if (o is Newtonsoft.Json.Linq.JContainer)
                using (StringReader stringReader = new StringReader(o.ToString()))
                    using (JsonTextReader jsonReader = new JsonTextReader(stringReader))
                        return _serializer.Deserialize<T>(jsonReader);
            //Type resultType = typeof(T);
            T result = default(T);
            if (result is Enum)
                result = (T)Enum.Parse(typeof(T), o.ToString());
            else
            if (result is Guid)
                result = (T)(object)(new Guid((string)o));
            else
            if (typeof(T) == typeof(TimeSpan))
                result = (T)(object)TimeSpan.Parse((string)o, System.Globalization.CultureInfo.InvariantCulture);
            else
            result = (T)Convert.ChangeType(o, typeof(T));
            return result;
        }

        public void Update(object serialized, object target)
        {
            if (serialized is Newtonsoft.Json.Linq.JContainer)
                using (StringReader stringReader = new StringReader(serialized.ToString()))
                    _serializer.Populate(stringReader, target);
        }

        public T GetInitalObject<T>()
        {
            WebSocketMessage query = new WebSocketMessage() { MessageType = WebSocketMessage.WebSocketMessageType.RootQuery };
            _clientSocket.Send(JsonConvert.SerializeObject(query));
            return Deserialize<T>(WaitForResponse(query).Response);
        }

        public T Query<T>(ProxyBase dto, string methodName, params object[] parameters)
        {
            WebSocketMessage query = new WebSocketMessage() { DtoGuid = dto.DtoGuid, MessageType = WebSocketMessage.WebSocketMessageType.Query, MemberName = methodName, Parameters = parameters };
            Debug.WriteLine(query, "Query");
            _clientSocket.Send(JsonConvert.SerializeObject(query));
            return Deserialize<T>(WaitForResponse(query).Response);
        }

        public T Get<T>(ProxyBase dto, string propertyName)
        {
            WebSocketMessage query = new WebSocketMessage() { DtoGuid = dto.DtoGuid, MessageType = WebSocketMessage.WebSocketMessageType.Get, MemberName = propertyName};
            Debug.WriteLine(query, "Get");
            _clientSocket.Send(JsonConvert.SerializeObject(query));
            return Deserialize<T>(WaitForResponse(query).Response);
        }

        public void Invoke(ProxyBase dto, string methodName, params object[] parameters)
        {
            WebSocketMessage query = new WebSocketMessage() { DtoGuid = dto.DtoGuid, MessageType = WebSocketMessage.WebSocketMessageType.Invoke, MemberName = methodName, Parameters = parameters };
            Debug.WriteLine(query, "Invoke");
            _clientSocket.Send(JsonConvert.SerializeObject(query));
        }

        public void Set(ProxyBase dto, object value, string propertyName)
        {
            WebSocketMessage query = new WebSocketMessage() { DtoGuid = dto.DtoGuid, MessageType = WebSocketMessage.WebSocketMessageType.Set, MemberName = propertyName, Parameters = new object[] { value} };
            Debug.WriteLine(query, "Set");
            _clientSocket.Send(JsonConvert.SerializeObject(query));
        }

        public void EventAdd(ProxyBase dto, string eventName)
        {
            WebSocketMessage query = new WebSocketMessage() { DtoGuid = dto.DtoGuid, MessageType = WebSocketMessage.WebSocketMessageType.EventAdd, MemberName = eventName };
            Debug.WriteLine(query, "EventAdd");
            _clientSocket.Send(JsonConvert.SerializeObject(query));
            if (eventName == "PropertyChanged")
                Update(WaitForResponse(query).Response, dto);
        }

        public void EventRemove(ProxyBase dto, string eventName)
        {
            WebSocketMessage query = new WebSocketMessage() { DtoGuid = dto.DtoGuid, MessageType = WebSocketMessage.WebSocketMessageType.EventRemove, MemberName = eventName };
            Debug.WriteLine(query, "EventRemove");
            _clientSocket.Send(JsonConvert.SerializeObject(query));
        }

    }


}
