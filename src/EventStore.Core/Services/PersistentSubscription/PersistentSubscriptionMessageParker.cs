using System;
using System.Text;
using EventStore.Common.Log;
using EventStore.Common.Utils;
using EventStore.Core.Data;
using EventStore.Core.Helpers;
using EventStore.Core.Messages;
using EventStore.Core.Services.UserManagement;

namespace EventStore.Core.Services.PersistentSubscription
{
    public class PersistentSubscriptionMessageParker : IPersistentSubscriptionMessageParker
    {
        private readonly IODispatcher _ioDispatcher;
        private readonly string _parkedStreamId;
        private static readonly ILogger Log = LogManager.GetLoggerFor<PersistentSubscriptionCheckpointWriter>();

        public PersistentSubscriptionMessageParker(string subscriptionId, IODispatcher ioDispatcher)
        {
            _parkedStreamId = "$persistentsubscription-" + subscriptionId + "-parked";
            _ioDispatcher = ioDispatcher;
        }

        private Event CreateStreamMetadataEvent(int? tb)
        {
            var eventId = Guid.NewGuid();
            var acl = new StreamAcl(
                readRole: SystemRoles.Admins, writeRole: SystemRoles.Admins,
                deleteRole: SystemRoles.Admins, metaReadRole: SystemRoles.Admins,
                metaWriteRole: SystemRoles.Admins);
            var metadata = new StreamMetadata(cacheControl: null, 
                                              truncateBefore: tb, 
                                              acl: acl);
            var dataBytes = metadata.ToJsonBytes();
            return new Event(eventId, SystemEventTypes.StreamMetadata, isJson: true, data: dataBytes, metadata: null);
        }

        private void WriteStateCompleted(Action<ResolvedEvent, OperationResult> completed, ResolvedEvent ev, ClientMessage.WriteEventsCompleted msg)
        {
            if(completed != null)
                completed(ev, msg.Result);
        }

        public void BeginParkMessage(ResolvedEvent ev,string reason, Action<ResolvedEvent, OperationResult> completed)
        {
            var metadata = new ParkedMessageMetadata {Added = DateTime.Now, Reason = reason, SubscriptionEventNumber = ev.OriginalEventNumber};
            
            string data = GetLinkToFor(ev);

            var parkedEvent = new Event(Guid.NewGuid(), SystemEventTypes.LinkTo, false, data, metadata.ToJson());

            _ioDispatcher.WriteEvent(_parkedStreamId, ExpectedVersion.Any, parkedEvent, SystemAccount.Principal, x => WriteStateCompleted(completed, ev, x));
        }

        private string GetLinkToFor(ResolvedEvent ev)
        {
            return string.Format("{0}@{1}", ev.Event.EventNumber, ev.Event.EventStreamId);
        }


        public void BeginDelete(Action<IPersistentSubscriptionMessageParker> completed)
        {
            _ioDispatcher.DeleteStream(_parkedStreamId, ExpectedVersion.Any, false, SystemAccount.Principal,
                x => completed(this));
        }

        public void BeginReadEndSequence(Action<int?> completed)
        {
            _ioDispatcher.ReadBackward(_parkedStreamId,
                int.MaxValue,
                1,
                false,
                SystemAccount.Principal, comp =>
                {
                    switch (comp.Result)
                    {
                        case ReadStreamResult.Success :
                            completed(comp.LastEventNumber);
                            break;
                        case ReadStreamResult.NoStream :
                            completed(null);
                            break;
                        default:
                            Log.Error("An error occured reading the last event in the parked message stream {0} due to {1}.\n" +
                                  "Messages were not removed on retry",
                                  _parkedStreamId,
                                  comp.Result);
                            break;
                    }
                });
        }

        public void BeginMarkParkedMessagesReprocessed(int sequence)
        {
            var metaStreamId = SystemStreams.MetastreamOf(_parkedStreamId);
            _ioDispatcher.WriteEvent(
                metaStreamId, ExpectedVersion.Any, CreateStreamMetadataEvent(sequence), SystemAccount.Principal, msg =>
                {
                    switch (msg.Result)
                    {
                        case OperationResult.Success:
                            //nothing
                            break;
                        default:
                            Log.Error("An error occured truncating the parked message stream {0} due to {1}.\n" + 
                                      "Messages were not removed on retry", 
                                      _parkedStreamId, 
                                      msg.Result);
                            break;
                    }
                });
        }

        class ParkedMessageMetadata
        {
            public DateTime Added { get; set; }
            public string Reason { get; set; }
            public int SubscriptionEventNumber { get; set; }
        }
    }
}