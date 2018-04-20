﻿using System;
using System.Fabric;
using System.Threading;
using System.Threading.Tasks;
using Contracts;
using Microsoft.ServiceFabric.Data;
using Microsoft.ServiceFabric.Data.Collections;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using NServiceBus;
using NServiceBus.Persistence.ServiceFabric;
using Shared;

namespace ChocolateOrder
{
    public class EndpointCommunicationListener :
        ICommunicationListener
    {
        StatefulServiceContext context;
        IReliableStateManager stateManager;
        IEndpointInstance endpointInstance;
        EndpointConfiguration endpointConfiguration;
        private ServicePartitionInformation servicePartitionInformation;

        public EndpointCommunicationListener(StatefulServiceContext context, IReliableStateManager stateManager, ServicePartitionInformation partitionPartitionInfo)
        {
            servicePartitionInformation = partitionPartitionInfo;
            this.context = context;
            this.stateManager = stateManager;
        }

        public async Task<string> OpenAsync(CancellationToken cancellationToken)
        {
            Logger.Log = m => ServiceEventSource.Current.ServiceMessage(context, m);

            var partitionInfo = await ServicePartitionQueryHelper
                .QueryServicePartitions(context.ServiceName, context.PartitionId)
                .ConfigureAwait(false);

            endpointConfiguration = new EndpointConfiguration("chocolateorder");

            #region Monitoring

            endpointConfiguration.SendHeartbeatTo(serviceControlQueue: "Particular.ServiceControl.RabbitMQ", frequency: TimeSpan.FromSeconds(5), timeToLive: TimeSpan.FromSeconds(15));
            var hostInfo = endpointConfiguration.UniquelyIdentifyRunningInstance();
            var instanceId = partitionInfo.LocalPartitionKey != null ? $"chocolateorder-{partitionInfo.LocalPartitionKey}" : "chocolateorder";
            hostInfo.UsingCustomDisplayName(instanceId);
            hostInfo.UsingCustomIdentifier(DeterministicIdBuilder.ToGuid(instanceId));

            var metrics = endpointConfiguration.EnableMetrics();
            metrics.SendMetricDataToServiceControl(serviceControlMetricsAddress: "Particular.Monitoring.RabbitMQ", interval: TimeSpan.FromSeconds(5));

            #endregion

            var transport = endpointConfiguration.ApplyCommonConfiguration(stateManager, servicePartitionInformation, context);

            var routing = transport.Routing();
            routing.RouteToEndpoint(typeof(ShipOrder), "chocolateshipping");

            ConfigureLocalPartitionsChocolateOrder(endpointConfiguration, partitionInfo);
            ConfigureReceiverSideDistributionOrderShipped(transport, partitionInfo);

            return null;
        }

        static void ConfigureLocalPartitionsChocolateOrder(EndpointConfiguration endpointConfiguration,
            PartitionsInformation partitionInfo)
        {
            endpointConfiguration.RegisterPartitionsForThisEndpoint(
                localPartitionKey: partitionInfo.LocalPartitionKey,
                allPartitionKeys: partitionInfo.Partitions);
        }

        static void ConfigureReceiverSideDistributionOrderShipped(TransportExtensions transportConfig, PartitionsInformation partitionInfo)
        {
            // TODO 10
            var routing = transportConfig.Routing();
            var receiverSideDistribution = routing.EnableReceiverSideDistribution(partitionInfo.Partitions);
            receiverSideDistribution.AddPartitionMappingForMessageType<OrderShipped>(
                mapMessageToPartitionKey: orderShipped =>
                {
                    var chocolateType = orderShipped.OrderId.Split(new[]{ ";" }, StringSplitOptions.RemoveEmptyEntries)[0];
                    return chocolateType;
                });
        }
        #region NotImportant
        public async Task Run()
        {
            if (endpointConfiguration == null)
            {
                var message =
                    $"{nameof(EndpointCommunicationListener)} Run() method should be invoked after communication listener has been opened and not before.";

                Logger.Log(message);
                throw new Exception(message);
            }

            var orderProcesses = await stateManager.TryGetAsync<IReliableDictionary<Guid, SagaEntry>>("orders")
                .ConfigureAwait(false);
            if (orderProcesses.HasValue)
            {
                await orderProcesses.Value.ClearAsync()
                    .ConfigureAwait(false);
            }

            endpointInstance = await Endpoint.Start(endpointConfiguration)
                .ConfigureAwait(false);
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            return endpointInstance != null ? endpointInstance.Stop() : Task.CompletedTask;
        }

        public void Abort()
        {
            // Fire & Forget Close
            CloseAsync(CancellationToken.None);
        }
        #endregion
    }
}