﻿using KafkaNet.Model;
using KafkaNet.Protocol;
using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace KafkaNet
{
    public class ProtocolGateway : IDisposable
    {
        private readonly IBrokerRouter _brokerRouter;

        //Add Loger
        public ProtocolGateway(params Uri[] brokerUrl)
        {
            var kafkaOptions = new KafkaOptions(brokerUrl) { MaximumReconnectionTimeout = TimeSpan.FromSeconds(60), ResponseTimeoutMs = TimeSpan.FromSeconds(60) };
            _brokerRouter = new BrokerRouter(kafkaOptions);
        }

        public ProtocolGateway(IBrokerRouter brokerRouter)
        {
            _brokerRouter = brokerRouter;
        }

        public ProtocolGateway(KafkaOptions kafkaOptions)
        {
            _brokerRouter = new BrokerRouter(kafkaOptions);
        }

        private readonly int _maxRetry = 3;

        /// <exception cref="InvalidTopicMetadataException">Thrown if the returned metadata for the given topic is invalid or missing</exception>
        /// <exception cref="InvalidPartitionException">Thrown if the give partitionId does not exist for the given topic.</exception>
        /// <exception cref="ServerUnreachableException">Thrown if none of the default brokers can be contacted</exception>
        /// <exception cref="ResponseTimeoutException">Thrown if there request times out</exception>
        /// <exception cref="BrokerConnectionException">Thrown in case of network error contacting broker (after retries)</exception>
        /// <exception cref="KafkaApplicationException">Thrown in case of an unexpected error in the request</exception>
        /// <exception cref="FormatException">Thrown in case the topic name is invalid</exception>
        public async Task<T> SendProtocolRequest<T>(IKafkaRequest<T> request, string topic, int partition) where T : class, IBaseResponse
        {
            ValidateTopic(topic);
            T response = null;
            int retryTime = 0;
            while (retryTime < _maxRetry)
            {
                bool needToRefreshTopicMetadata;
                ExceptionDispatchInfo exception = null;
                string errorDetails;

                try
                {
                    await _brokerRouter.RefreshMissingTopicMetadata(topic);

                    //find route it can chage after Metadata Refresh
                    var route = _brokerRouter.SelectBrokerRouteFromLocalCache(topic, partition);
                    var responses = await route.Connection.SendAsync(request).ConfigureAwait(false);
                    response = responses.FirstOrDefault();

                    //this can happened if you send ProduceRequest with ack level=0
                    if (response == null)
                    {
                        return null;
                    }

                    var error = (ErrorResponseCode)response.Error;
                    if (error == ErrorResponseCode.NoError)
                    {
                        return response;
                    }

                    //It means we had an error
                    errorDetails = error.ToString();
                    needToRefreshTopicMetadata = CanRecoverByRefreshMetadata(error);
                }
                catch (ResponseTimeoutException ex)
                {
                    exception = ExceptionDispatchInfo.Capture(ex);
                    needToRefreshTopicMetadata = true;
                    errorDetails = ex.GetType().Name;
                }
                catch (BrokerConnectionException ex)
                {
                    exception = ExceptionDispatchInfo.Capture(ex);
                    needToRefreshTopicMetadata = true;
                    errorDetails = ex.GetType().Name;
                }

                retryTime++;
                bool hasMoreRetry = retryTime < _maxRetry;

                _brokerRouter.Log.WarnFormat("ProtocolGateway error sending request, retrying (attempt number {0}): {1}", retryTime, errorDetails);
                if (needToRefreshTopicMetadata && hasMoreRetry)
                {
                    await _brokerRouter.RefreshTopicMetadata(topic).ConfigureAwait(false);
                }
                else
                {
                    _brokerRouter.Log.ErrorFormat("ProtocolGateway sending request failed");

                    // If an exception was thrown, we want to propagate it
                    if (exception != null)
                    {
                        exception.Throw();
                    }
                    
                    // Otherwise, the error was from Kafka, throwing application exception
                    throw new KafkaApplicationException("FetchResponse received an error from Kafka: {0}", errorDetails) { ErrorCode = response.Error };
                }
            }

            return response;
        }

        private static bool CanRecoverByRefreshMetadata(ErrorResponseCode error)
        {
            return error == ErrorResponseCode.BrokerNotAvailable ||
                                         error == ErrorResponseCode.ConsumerCoordinatorNotAvailableCode ||
                                         error == ErrorResponseCode.LeaderNotAvailable ||
                                         error == ErrorResponseCode.NotLeaderForPartition;
        }

        public void Dispose()
        {
            _brokerRouter.Dispose();
        }

        private void ValidateTopic(string topic)
        {
            if (topic.Contains(" "))
            {
                throw new FormatException("topic name is invalid");
            }
        }
    }
}