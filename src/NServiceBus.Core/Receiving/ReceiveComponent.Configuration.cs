namespace NServiceBus
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Transport;
    using Unicast;

    partial class ReceiveComponent
    {
        public static Configuration PrepareConfiguration(Settings settings, TransportSeam transportSeam)
        {
            var isSendOnlyEndpoint = settings.IsSendOnlyEndpoint;

            if (isSendOnlyEndpoint && settings.CustomLocalAddressProvided)
            {
                throw new Exception($"Specifying a base name for the input queue using `{nameof(ReceiveSettingsExtensions.OverrideLocalAddress)}(baseInputQueueName)` is not supported for send-only endpoints.");
            }

            var endpointName = settings.EndpointName;
            var discriminator = settings.EndpointInstanceDiscriminator;
            var queueNameBase = settings.CustomLocalAddress ?? endpointName;
            var purgeOnStartup = settings.PurgeOnStartup;

            var transportDefinition = transportSeam.TransportDefinition;

            LogicalAddress logicalAddress = LogicalAddress.CreateLocalAddress(queueNameBase, new Dictionary<string, string>());

            var localAddress = transportDefinition.ToTransportAddress(logicalAddress.ToEndpointAddress());

            string instanceSpecificQueue = null;
            if (discriminator != null)
            {
                instanceSpecificQueue = transportDefinition.ToTransportAddress(logicalAddress.CreateIndividualizedAddress(discriminator).ToEndpointAddress());
            }

            var transactionMode = GetRequiredTransactionMode(settings, transportSeam.TransportDefinition.SupportedTransactionModes);

            var pushRuntimeSettings = settings.PushRuntimeSettings;

            var receiveConfiguration = new Configuration(
                logicalAddress,
                queueNameBase,
                localAddress,
                instanceSpecificQueue,
                transactionMode,
                pushRuntimeSettings,
                purgeOnStartup,
                settings.PipelineCompletedSubscribers ?? new Notification<ReceivePipelineCompleted>(),
                isSendOnlyEndpoint,
                settings.ExecuteTheseHandlersFirst,
                settings.MessageHandlerRegistry,
                settings.ShouldCreateQueues,
                transportSeam);

            settings.RegisterReceiveConfigurationForBackwardsCompatibility(receiveConfiguration);

            return receiveConfiguration;
        }

        public class Configuration
        {
            public Configuration(LogicalAddress logicalAddress,
                string queueNameBase,
                string localAddress,
                string instanceSpecificQueue,
                TransportTransactionMode transactionMode,
                PushRuntimeSettings pushRuntimeSettings,
                bool purgeOnStartup,
                Notification<ReceivePipelineCompleted> pipelineCompletedSubscribers,
                bool isSendOnlyEndpoint,
                List<Type> executeTheseHandlersFirst,
                MessageHandlerRegistry messageHandlerRegistry,
                bool createQueues, TransportSeam transportSeam)
            {
                LogicalAddress = logicalAddress;
                QueueNameBase = queueNameBase;
                LocalAddress = localAddress;
                InstanceSpecificQueue = instanceSpecificQueue;
                TransactionMode = transactionMode;
                PushRuntimeSettings = pushRuntimeSettings;
                PurgeOnStartup = purgeOnStartup;
                IsSendOnlyEndpoint = isSendOnlyEndpoint;
                PipelineCompletedSubscribers = pipelineCompletedSubscribers;
                ExecuteTheseHandlersFirst = executeTheseHandlersFirst;
                satelliteDefinitions = new List<SatelliteDefinition>();
                this.messageHandlerRegistry = messageHandlerRegistry;
                CreateQueues = createQueues;
                this.transportSeam = transportSeam;
            }

            public LogicalAddress LogicalAddress { get; }

            public string LocalAddress { get; }

            public string InstanceSpecificQueue { get; }

            public TransportTransactionMode TransactionMode { get; }

            public PushRuntimeSettings PushRuntimeSettings { get; }

            public string QueueNameBase { get; }

            public IReadOnlyList<SatelliteDefinition> SatelliteDefinitions => satelliteDefinitions;

            public bool PurgeOnStartup { get; }

            public bool IsSendOnlyEndpoint { get; }

            public List<Type> ExecuteTheseHandlersFirst { get; }

            public bool CreateQueues { get; }

            public void AddSatelliteReceiver(string name, string transportAddress, PushRuntimeSettings runtimeSettings, Func<RecoverabilityConfig, ErrorContext, RecoverabilityAction> recoverabilityPolicy, Func<IServiceProvider, MessageContext, Task> onMessage)
            {
                var satelliteDefinition = new SatelliteDefinition(name, transportAddress, TransactionMode, runtimeSettings, recoverabilityPolicy, onMessage);

                satelliteDefinitions.Add(satelliteDefinition);
            }

            public Notification<ReceivePipelineCompleted> PipelineCompletedSubscribers;

            //This should only be used by the receive component it self
            internal readonly MessageHandlerRegistry messageHandlerRegistry;
            internal readonly TransportSeam transportSeam;

            List<SatelliteDefinition> satelliteDefinitions;
        }
    }
}