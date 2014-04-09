﻿using System;
using System.Collections.Concurrent;
using System.Reactive.Subjects;
using WampSharp.Core.Listener;
using WampSharp.Core.Message;
using WampSharp.V2.Core.Contracts;
using WampSharp.V2.Core.Listener;

namespace WampSharp.V2.PubSub
{
    internal class RawWampTopic<TMessage> : IRawWampTopic<TMessage>, IWampTopicSubscriber, IDisposable
    {
        #region Data Members

        private readonly ConcurrentDictionary<long, Subscription> mSesssionIdToSubscription =
            new ConcurrentDictionary<long, Subscription>();

        private readonly IWampBinding<TMessage> mBinding; 
        private readonly IWampEventSerializer<TMessage> mSerializer;
        private readonly Subject<WampMessage<TMessage>> mSubject = new Subject<WampMessage<TMessage>>();
        private readonly string mTopicUri;

        #endregion

        #region Constructor

        public RawWampTopic(string topicUri, IWampEventSerializer<TMessage> serializer, IWampBinding<TMessage> binding)
        {
            mSerializer = serializer;
            mTopicUri = topicUri;
            mBinding = binding;
        }

        #endregion

        #region IRawWampTopic<TMessage> Members

        public void Event(long publicationId, object details)
        {
            WampMessage<TMessage> message =
                mSerializer.Event(SubscriptionId, publicationId, details);

            Publish(message);
        }

        public void Event(long publicationId, object details, object[] arguments)
        {
            WampMessage<TMessage> message =
                mSerializer.Event(SubscriptionId, publicationId, details, arguments);

            Publish(message);
        }

        public void Event(long publicationId, object details, object[] arguments, object argumentsKeywords)
        {
            WampMessage<TMessage> message =
                mSerializer.Event(SubscriptionId, publicationId, details, arguments, argumentsKeywords);

            Publish(message);
        }

        private void Publish(WampMessage<TMessage> message)
        {
            WampMessage<TMessage> raw = mBinding.GetRawMessage(message);
            mSubject.OnNext(raw);
        }

        public bool HasSubscribers
        {
            get
            {
                return mSubject.HasObservers;
            }
        }

        public long SubscriptionId
        {
            get; 
            set;
        }

        public string TopicUri
        {
            get
            {
                return mTopicUri;
            }
        }

        public IDisposable SubscriptionDisposable
        {
            get; 
            set;
        }

        public void Subscribe(ISubscribeRequest<TMessage> request, TMessage options)
        {
            RemoteWampTopicSubscriber remoteSubscriber =
                new RemoteWampTopicSubscriber(this.SubscriptionId,
                                              request.Client as IWampSubscriber);

            this.RaiseSubscriptionAdding(remoteSubscriber, options);

            IWampClient<TMessage> client = request.Client;

            RemoteObserver observer = new RemoteObserver(client);
            
            // TODO: race conition: events are allowed to be sent only after client
            // TODO: received the SUBSCRIBED message.
            IDisposable disposable = mSubject.Subscribe(observer);
            
            Subscription subscription = new Subscription(this, client, disposable);

            mSesssionIdToSubscription.TryAdd(client.Session, subscription);

            request.Subscribed(this.SubscriptionId);

            this.RaiseSubscriptionAdded(remoteSubscriber, options);
        }

        public void Unsubscribe(IUnsubscribeRequest<TMessage> request)
        {
            IWampClient<TMessage> client = request.Client;

            Subscription subscription;
            
            if (mSesssionIdToSubscription.TryRemove(client.Session, out subscription))
            {
                this.RaiseSubscriptionRemoving(client.Session);

                subscription.Dispose();

                request.Unsubscribed();

                this.RaiseSubscriptionRemoved(client.Session);

                if (!this.HasSubscribers)
                {
                    this.RaiseTopicEmpty();
                }
            }
        }

        public void Dispose()
        {
            SubscriptionDisposable.Dispose();
            SubscriptionDisposable = null;
        }

        #endregion

        #region ISubscriptionNotifier

        public event EventHandler<SubscriptionAddEventArgs> SubscriptionAdding;
        public event EventHandler<SubscriptionAddEventArgs> SubscriptionAdded;
        public event EventHandler<SubscriptionRemoveEventArgs> SubscriptionRemoving;
        public event EventHandler<SubscriptionRemoveEventArgs> SubscriptionRemoved;
        public event EventHandler TopicEmpty;

        protected virtual void RaiseSubscriptionAdding(RemoteWampTopicSubscriber subscriber, TMessage options)
        {
            EventHandler<SubscriptionAddEventArgs> handler = SubscriptionAdding;

            if (handler != null)
            {
                SubscriptionAddEventArgs args = GetAddEventArgs(subscriber, options);

                handler(this, args);
            }
        }

        protected virtual void RaiseSubscriptionAdded(RemoteWampTopicSubscriber subscriber, TMessage options)
        {
            EventHandler<SubscriptionAddEventArgs> handler = SubscriptionAdded;

            if (handler != null)
            {
                SubscriptionAddEventArgs args = GetAddEventArgs(subscriber, options);

                handler(this, args);
            }
        }

        protected virtual void RaiseSubscriptionRemoving(long sessionId)
        {
            EventHandler<SubscriptionRemoveEventArgs> handler = SubscriptionRemoving;

            if (handler != null)
            {
                SubscriptionRemoveEventArgs args = GetRemoveEventArgs(sessionId);
                handler(this, args);
            }
        }

        protected virtual void RaiseSubscriptionRemoved(long sessionId)
        {
            EventHandler<SubscriptionRemoveEventArgs> handler = SubscriptionRemoved;

            if (handler != null)
            {
                SubscriptionRemoveEventArgs args = GetRemoveEventArgs(sessionId);
                handler(this, args);
            }
        }

        protected virtual void RaiseTopicEmpty()
        {
            EventHandler handler = TopicEmpty;

            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private static SubscriptionAddEventArgs GetAddEventArgs(RemoteWampTopicSubscriber subscriber, TMessage options)
        {
            return new RemoteSubscriptionAddEventArgs(subscriber, options);
        }

        private static SubscriptionRemoveEventArgs GetRemoveEventArgs(long sessionId)
        {
            return new RemoteSubscriptionRemoveEventArgs(sessionId);
        }

        #endregion

        #region Nested Types

        private class Subscription : IDisposable
        {
            private readonly RawWampTopic<TMessage> mParent;
            private readonly IWampClient<TMessage> mClient;
            private readonly IDisposable mDisposable;

            public Subscription(RawWampTopic<TMessage> parent, IWampClient<TMessage> client, IDisposable disposable)
            {
                mParent = parent;
                mClient = client;
                mDisposable = disposable;

                IWampConnectionMonitor monitor = mClient as IWampConnectionMonitor;
                monitor.ConnectionClosed += OnConnectionClosed;
            }

            private void OnConnectionClosed(object sender, EventArgs e)
            {
                mParent.Unsubscribe(new DisconnectUnsubscribeRequest(mClient));
                IWampConnectionMonitor monitor = sender as IWampConnectionMonitor;
                monitor.ConnectionClosed -= OnConnectionClosed;
            }

            public void Dispose()
            {
                mDisposable.Dispose();
            }

            private class DisconnectUnsubscribeRequest : IUnsubscribeRequest<TMessage>
            {
                private readonly IWampClient<TMessage> mClient;

                public DisconnectUnsubscribeRequest(IWampClient<TMessage> client)
                {
                    mClient = client;
                }

                public IWampClient<TMessage> Client
                {
                    get
                    {
                        return mClient;
                    }
                }

                public void Unsubscribed()
                {
                }
            }
        }

        private class RemoteObserver : IObserver<WampMessage<TMessage>>
        {
            private readonly IWampRawClient<TMessage> mClient;

            public RemoteObserver(IWampRawClient<TMessage> client)
            {
                mClient = client;
            }

            public void OnNext(WampMessage<TMessage> value)
            {
                mClient.Message(value);
            }

            public void OnError(Exception error)
            {
            }

            public void OnCompleted()
            {
            }
        }

        #endregion
    }
}