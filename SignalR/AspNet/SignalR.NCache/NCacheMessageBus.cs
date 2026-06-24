using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR;
using Microsoft.AspNet.SignalR.Messaging;
using Microsoft.AspNet.SignalR.Tracing;

namespace Alachisoft.NCache.AspNet.SignalR
{
    public class NCacheMessageBus : ScaleoutMessageBus
    {
        private const int DefaultBufferSize = 1000;

        private readonly string _eventKey;
        private readonly TraceSource _trace;
        private readonly ITraceManager _traceManager;

        private ICacheProvider _connection;
        private string _cacheName;
        private int _state;
        private readonly object _callbackLock = new object();     

        public NCacheMessageBus(IDependencyResolver resolver, NCacheScaleoutConfiguration configuration, ICacheProvider connection)
            : this(resolver, configuration, connection, true)
        {
        }
        
        internal NCacheMessageBus(IDependencyResolver resolver, NCacheScaleoutConfiguration configuration, ICacheProvider connection, bool connectAutomatically)
            : base(resolver, configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException("configuration");
            }

            _connection = connection;

            _cacheName = configuration.CacheName;
            _eventKey = configuration.EventKey;

            _traceManager = resolver.Resolve<ITraceManager>();

            _trace = _traceManager["SignalR." + "NCacheMessageBus"];

            ReconnectDelay = TimeSpan.FromSeconds(2);

            if (connectAutomatically)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    var ignore = ConnectWithRetry();
                });
            }
        }

        public TimeSpan ReconnectDelay { get; set; }       

        public virtual void OpenStream(int streamIndex)
        {
            Open(streamIndex);
        }

        protected override Task Send(int streamIndex, IList<Message> messages)
        {
            return _connection.PublishAsync(               
                _eventKey,
                NCacheMessage.ToBytes(new NCacheMessage(_connection.GetUniqueID(), new ScaleoutMessage(messages))));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var oldState = Interlocked.Exchange(ref _state, State.Disposing);

                switch (oldState)
                {
                    case State.Connected:
                        Shutdown();
                        break;
                    case State.Closed:
                    case State.Disposing:
                        // No-op
                        break;
                    case State.Disposed:
                        Interlocked.Exchange(ref _state, State.Disposed);
                        break;
                    default:
                        break;
                }
                
                this._connection.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Shutdown()
        {
            _trace.TraceInformation("Shutdown()");

            if (_connection != null)
            {
                _connection.Close();
            }

            Interlocked.Exchange(ref _state, State.Disposed);
        }

        internal async Task ConnectWithRetry()
        {
                try
                {
                    await ConnectToNCacheAsync();

                    var oldState = Interlocked.CompareExchange(ref _state,
                                               State.Connected,
                                               State.Closed);

                    if (oldState == State.Closed)
                    {
                        OpenStream(0);
                    }
                    else
                    {
                        Debug.Assert(oldState == State.Disposing, "unexpected state");

                        Shutdown();
                    }

                }

                catch (Exception ex)
                {
                    _trace.TraceError("Error connecting to NCache - " + ex.GetBaseException());
                }

                if (_state == State.Disposing)
                {
                    Shutdown();
                }

                await Task.Delay(ReconnectDelay);
        }

        private async Task ConnectToNCacheAsync()
        {
            if (_connection != null)
            {
                _trace.TraceInformation("Connecting...");
                await _connection.ConnectAsync(_cacheName, _traceManager["SignalR.NCache Connection"]);
                
                _trace.TraceInformation("Connection opened");            
            }

            _connection.CacheStopped += OnCacheStopped;            

            await _connection.SubscribeAsync(_eventKey, OnMessage);

            _trace.TraceVerbose("Subscribed to event " + _eventKey);
        }

        private void OnCacheStopped(Exception ex)
        {
            try
            {
                var oldState = Interlocked.CompareExchange(ref _state,State.Closed,State.Connected);
                OnError(0, ex);
                _trace.TraceError("OnCacheStopped - " + ex.Message);
                
                if (oldState == State.Connected)
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        var ignore = ConnectWithRetry();
                    });
                }
            }
            catch (Exception e)
            {
                _trace.TraceError("OnCacheStopped.Catch - " + e.Message);
            }
        }

        private void OnMessage(int streamIndex, NCacheMessage message)
        {           
            lock (_callbackLock)
            {
                OnReceived(streamIndex, message.Id, message.ScaleoutMessage);
            }
        }
        
        internal static class State
        {
            public const int Closed = 0;
            public const int Connected = 1;
            public const int Disposing = 2;
            public const int Disposed = 3;
        }
    }
}
