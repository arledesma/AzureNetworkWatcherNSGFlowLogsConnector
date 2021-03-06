﻿namespace nsgFunc
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading.Tasks;

    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;

    public partial class Util
    {
        // ReSharper disable InconsistentNaming
       
        // If global setting for logging is enabled. Log Information for debugging. Will be helpful in investigation.
        private static readonly bool ENABLE_DEBUG_LOG = bool.TryParse(GetEnvironmentVariable("enableDebugLog"), out _);
       
        // ReSharper restore InconsistentNaming

        /// <summary>
        /// The payload that will be sent to Armor for ingestion into the event pipeline
        /// </summary>
        public class ArmorPayload
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="ArmorPayload"/> class.
            /// </summary>
            /// <param name="message">The message.</param>
            /// <param name="messageEncoded">IP FIX format encoded string for individual record from `flowTuples`.</param>
            /// <param name="tenantId">The tenant identifier.</param>
            public ArmorPayload(string message, string messageEncoded, int tenantId)
            {
                Message = message;
                MessageEncoded = messageEncoded;
                MessageType = "aws-vpc-flows"; //TODO: Need to add NSG Flow Log type once finalized. Right now routing through vpc log.
                Tags = new[] { "relayed" };
                TenantId = tenantId;
                ExternalId = Guid.Parse(tenantId.ToString("D32")).ToString("D");
            }

            [JsonProperty("tags")]
            public string[] Tags { get; }

            [JsonProperty("type")]
            public string MessageType { get; }

            /// <summary>
            /// Gets the external identifier.
            /// </summary>
            /// <value>
            /// The external identifier for an event that is a guid representation of an integer that represents the Customer AccountID.
            /// </value>
            [JsonProperty("external_id")]
            public string ExternalId { get; }

            /// <summary>
            /// Gets the message.
            /// </summary>
            /// <value>
            /// The message.
            /// </value>
            [JsonProperty("message")]
            public string Message { get; }

            /// <summary>
            /// Gets IPFIX converted format for individual record from `flowTuples`
            /// </summary>
            /// <value>
            /// The message.
            /// </value>
            [JsonProperty("message_encoded")]
            public string MessageEncoded { get; }

            /// <summary>
            /// Gets the payload.
            /// </summary>
            /// <value>
            /// The payload.
            /// </value>
            [JsonProperty("payload")]
            public string Payload { get; }

            /// <summary>
            /// Gets or sets the tenant identifier.
            /// </summary>
            /// <value>
            /// The tenant identifier.
            /// </value>
            [JsonProperty("tenant_id")]
            public int TenantId { get; set; }
        }

        /// <summary>
        /// Output Binding to Armor.
        /// </summary>
        /// <param name="newClientContent">New content of the client.</param>
        /// <param name="log">The log.</param>
        /// <returns></returns>

        public static async Task ObArmor(string newClientContent, ILogger log)
        {
            // TODO: Figure this out
            // Should this be creating a collection of ipfix packets
            // and sending that as a single event with the original newClientContent as the message?
            // It feels like we should send this all as a single event into the eventing platform
            // and let the processor handle the rest
            foreach (var content in ConvertToArmorPayload(newClientContent, log))
            {
                await obLogstash(content, log).ConfigureAwait(false);
            }
        }

        public static System.Collections.Generic.IEnumerable<string> ConvertToArmorPayload(string newClientContent, ILogger log)
        {
            var tenantId = GetTenantIdFromEnvironment(log);

            foreach (var armorRecord in DenormalizedRecord(newClientContent))
            {
                var ipFixEncodedLog = ConvertToIpFixFormat(armorRecord.Records, log);
                yield return JsonConvert.SerializeObject(
                    new ArmorPayload(armorRecord.Message, ipFixEncodedLog, tenantId),
                    Formatting.None);
            }
        }

        /// <summary>
        /// Convert to IPFIX format
        /// </summary>
        /// <param name="denormalizedRecords">Collection of flowTuples from each record.</param>
        /// <param name="log">ILogger for logging.</param>
        /// <returns>Base64 encoded string of byte array.</returns>
        private static string ConvertToIpFixFormat(IEnumerable<DenormalizedRecord> denormalizedRecords, ILogger log)
        {
            try
            {
                var records = denormalizedRecords.ToList();
            
                if (ENABLE_DEBUG_LOG)
                {
                    log.LogInformation(
                        $"Start of IP FIX conversion flowTuples count: {records.Count} and record: {JsonConvert.SerializeObject(records)}");
                }

                if (records.Count <= 0)
                {
                    log.LogWarning("Zero records passed to ConvertToIpFixFormat function.");
                    return string.Empty;
                }

                // Having issue in mapping OutputPackets and OutputBytes for direction egress. 
                // Hence mapping to Input Field. Same as with aws-vpc-flow log.
                var templateDef =
                    new TemplateFlow(555)
                        .Field(NetFlowInformationElement.IPV4SourceAddress, 4)
                        .Field(NetFlowInformationElement.L4SourcePort, 2)
                        .Field(NetFlowInformationElement.IPV4DestionationAddress, 4)
                        .Field(NetFlowInformationElement.L4DestionationPort, 2)
                        .Field(NetFlowInformationElement.Protocol, 1)
                        .Field(NetFlowInformationElement.InputPackets, 4) // Will be always mapping to InputPackets even for direction egress.
                        .Field(NetFlowInformationElement.InputBytes, 4) // Will be always mapping to InputBytes even for direction egress.
                        .Field(NetFlowInformationElement.FirstSwitched, 4)
                        .Field(NetFlowInformationElement.LastSwitched, 4)
                        .Field(NetFlowInformationElement.InterfaceName, 32);

                var templateData =
                    new TemplateData(templateDef);

                // Will be sending all tuple together.
                foreach (var record in records)
                {
                    var protocolIdentifier =
                        record.transportProtocol == "U" ? (byte) ProtocolType.Udp : (byte) ProtocolType.Tcp;

                    // Based on direction of device get packets and bytes count.
                    var packetDeltaCount = GetPacketCountFromFlowLog(record, log);
                    var octetDeltaCount = GetOctetCountFromFlowLog(record, log);

                    // Start and End time to be same.
                    var flowStartSeconds = Convert.ToUInt32(record.startTime);
                    var flowEndSeconds = Convert.ToUInt32(record.startTime);

                    templateData
                        .Data(
                            IPAddress.TryParse(record.sourceAddress, out var sourceAddress)
                                ? sourceAddress
                                : IPAddress.Any,
                            ushort.TryParse(record.sourcePort, out var sourcePort) ? sourcePort : (ushort) 0,
                            IPAddress.TryParse(record.destinationAddress, out var destinationAddress)
                                ? destinationAddress
                                : IPAddress.Any,
                            ushort.TryParse(record.destinationPort, out var destinationPort)
                                ? destinationPort
                                : (ushort) 0,
                            protocolIdentifier,
                            packetDeltaCount,
                            octetDeltaCount,
                            flowStartSeconds,
                            flowEndSeconds,
                            record.mac
                        );
                }

                var exportData =
                    new ExportPacket(0, 1234)
                        .Template(templateData)
                        .GetData();

                var base64Encoded = Convert.ToBase64String(exportData, Base64FormattingOptions.None);

                if (ENABLE_DEBUG_LOG)
                {
                    // https://stackoverflow.com/questions/5666413/ipfix-data-over-udp-to-c-sharp-can-i-decode-the-data
                    log.LogInformation(
                        $"End of IP FIX conversion ipFixEncodedLog: {base64Encoded}");
                }

                return base64Encoded;
            }
            catch (Exception ex)
            {
                log.LogError(
                    $"Exception occurred in ConvertToIpFixFormat records: {JsonConvert.SerializeObject(denormalizedRecords)} and exception: {ex}");
            }

            return string.Empty;
        }

        private static uint GetOctetCountFromFlowLog(DenormalizedRecord record, ILogger log)
        {
            if (!(record.version >= 2.0))
            {
                log.LogWarning("Only version 2 supported {version}", record.version);
                return 0;
            }

            if (record.flowState != "B")
            {
                return record.deviceDirection == "I"
                    ? Convert.ToUInt32(record.bytesStoD)
                    : Convert.ToUInt32(record.bytesDtoS);
            }

            return 0;
        }

        private static uint GetPacketCountFromFlowLog(DenormalizedRecord record, ILogger log)
        {
            if (!(record.version >= 2.0))
            {
                log.LogWarning("Only version 2 supported {version}", record.version);
                return 0;
            }

            if (record.flowState != "B")
            {
                return record.deviceDirection == "I"
                    ? Convert.ToUInt32(record.packetsStoD)
                    : Convert.ToUInt32(record.packetsDtoS);

            }

            return 0;
        }

        static IEnumerable<ArmorRecord> DenormalizedRecord(string newClientContent)
        {
            var logs = JsonConvert.DeserializeObject<NSGFlowLogRecords>(newClientContent);

            foreach (var record in logs.records)
            {
                var version = record.properties.Version;
                var message = JsonConvert.SerializeObject(record, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore
                });

                var armorRecord = new ArmorRecord(message);
                var denormalizedRecordList = new List<DenormalizedRecord>();

                foreach (var outerFlow in record.properties.flows)
                {
                    foreach (var innerFlow in outerFlow.flows)
                    {
                        foreach (var flowTuple in innerFlow.flowTuples)
                        {
                            var tuple = new NSGFlowLogTuple(flowTuple, version);

                            denormalizedRecordList.Add(new DenormalizedRecord(
                                record.properties.Version,
                                record.time,
                                record.category,
                                record.operationName,
                                record.resourceId,
                                outerFlow.rule,
                                innerFlow.mac,
                                tuple));
                        }
                    }
                }

                armorRecord.Records = denormalizedRecordList;
                yield return armorRecord;
            }
        }

        /// <summary>
        /// Gets the tenant identifier from the Armor Account Id environment variable.
        /// </summary>
        /// <returns></returns>
        private static int GetTenantIdFromEnvironment(ILogger log)
        {
            var accountIdEnvironmentVariable = Util.GetEnvironmentVariable("armorAccountId");
            if (int.TryParse(accountIdEnvironmentVariable, out var accountId))
            {
                return accountId;
            }

            log.LogError(string.IsNullOrWhiteSpace(accountIdEnvironmentVariable)
                ? "Value for armorAccountId is required."
                : "Value for armorAccountId must be a natural number.");
            throw new System.ArgumentNullException("armorAccountId", "Please provide your Armor Account ID as armorAccountId.");
        }
    }
}
