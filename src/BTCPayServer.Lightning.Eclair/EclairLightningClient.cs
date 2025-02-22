﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning.Eclair.Models;
using NBitcoin;
using NBitcoin.RPC;

namespace BTCPayServer.Lightning.Eclair
{
    public class EclairLightningClient : ILightningClient
    {
        private readonly Uri _address;
        private readonly string _password;
        private readonly Network _network;
        private readonly RPCClient _rpcClient;
        private readonly EclairClient _eclairClient;

        public EclairLightningClient(Uri address, string password, Network network, RPCClient rpcClient,
            HttpClient httpClient = null)
        {
            if (address == null)
                throw new ArgumentNullException(nameof(address));
            if (network == null)
                throw new ArgumentNullException(nameof(network));
            _address = address;
            _password = password;
            _network = network;
            _rpcClient = rpcClient;
            _eclairClient = new EclairClient(address, password, httpClient);
        }


        public async Task<LightningInvoice> GetInvoice(string invoiceId,
            CancellationToken cancellation = default(CancellationToken))
        {
            var result = await _eclairClient.GetInvoice(invoiceId, cancellation);
            GetReceivedInfoResponse info;
            try
            {
                info = await _eclairClient.GetReceivedInfo(invoiceId, null, cancellation);
            }
            catch (EclairClient.EclairApiException)
            {
                info = new GetReceivedInfoResponse()
                {
                    AmountMsat = 0,
                    ReceivedAt = 0,
                    PaymentHash = invoiceId
                };
            }

            var parsed = BOLT11PaymentRequest.Parse(result.Serialized, _network);
            return new LightningInvoice()
            {
                Id = result.PaymentHash,
                Amount = parsed.MinimumAmount,
                ExpiresAt = parsed.ExpiryDate,
                BOLT11 = result.Serialized,
                AmountReceived = info.AmountMsat,
                Status = info.AmountMsat >= parsed.MinimumAmount ? LightningInvoiceStatus.Paid :
                    DateTime.Now >= parsed.ExpiryDate ? LightningInvoiceStatus.Expired : LightningInvoiceStatus.Unpaid,
                PaidAt = info.ReceivedAt == 0
                    ? (DateTimeOffset?) null
                    : DateTimeOffset.FromUnixTimeMilliseconds(info.ReceivedAt)
            };
        }

        public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
            CancellationToken cancellation = default(CancellationToken))
        {
            var result = await _eclairClient.CreateInvoice(
                description,
                amount.MilliSatoshi,
                Convert.ToInt32(expiry.TotalSeconds), null, cancellation);

            var parsed = BOLT11PaymentRequest.Parse(result.Serialized, _network);
            var invoice = new LightningInvoice()
            {
                BOLT11 = result.Serialized,
                Amount = amount,
                Id = result.PaymentHash,
                Status = LightningInvoiceStatus.Unpaid,
                ExpiresAt = parsed.ExpiryDate
            };
            return invoice;
        }

        public async Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default(CancellationToken))
        {
            return new EclairSession(
               await WebsocketHelper.CreateClientWebSocket(_address.AbsoluteUri,
                  new AuthenticationHeaderValue("Basic",
                        Convert.ToBase64String(Encoding.Default.GetBytes($":{_password}"))).ToString(), cancellation), this);
        }

        public async Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default(CancellationToken))
        {
            var info = await _eclairClient.GetInfo(cancellation);
            return new LightningNodeInformation()
            {
                NodeInfoList = info.PublicAddresses.Select(s =>
                {
                    var split = s.Split(':');
                    return new NodeInfo(new PubKey(info.NodeId), split[0], int.Parse(split[1]));
                }).ToList(),
                BlockHeight = info.BlockHeight
            };
        }

        public async Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default(CancellationToken))
        {
            try
            {
                var uuid = await _eclairClient.PayInvoice(bolt11, null, null, cancellation);
                while (!cancellation.IsCancellationRequested)
                {
                    var status = await _eclairClient.GetSentInfo(null, uuid, cancellation);
                    if (!status.Any())
                    {
                        continue;
                    }

                    switch (status.First().Status)
                    {
                        case "SUCCEEDED":
                            return new PayResponse(PayResult.Ok);
                        case "FAILED":
                            return new PayResponse(PayResult.CouldNotFindRoute);
                        case "PENDING":
                            await Task.Delay(200, cancellation);
                            break;
                    }
                }
            }
            catch (EclairClient.EclairApiException)
            {
            }

            return new PayResponse(PayResult.CouldNotFindRoute);
        }

        public async Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest,
            CancellationToken cancellation = default(CancellationToken))
        {
            try
            {
                var result = await _eclairClient.Open(openChannelRequest.NodeInfo.NodeId,
                    openChannelRequest.ChannelAmount.Satoshi
                    , null,
                    Convert.ToInt64(openChannelRequest.FeeRate.SatoshiPerByte), null, cancellation);

                if (result.Contains("created channel"))
                {
                    var channelId = result.Replace("created channel", "").Trim();
                    var channel = await _eclairClient.Channel(channelId, cancellation);
                    switch (channel.State)
                    {
                        case "WAIT_FOR_OPEN_CHANNEL":
                        case "WAIT_FOR_ACCEPT_CHANNEL":
                        case "WAIT_FOR_FUNDING_CREATED":
                        case "WAIT_FOR_FUNDING_SIGNED":
                        case "WAIT_FOR_FUNDING_LOCKED":
                        case "WAIT_FOR_FUNDING_CONFIRMED":
                            return new OpenChannelResponse(OpenChannelResult.NeedMoreConf);
                    }
                }

                if (result.Contains("couldn't publish funding tx"))
                {
                    return new OpenChannelResponse(OpenChannelResult.CannotAffordFunding);
                }

                return new OpenChannelResponse(OpenChannelResult.Ok);
            }
            catch (Exception e)
            {
                if (e.Message.Contains("not connected") || e.Message.Contains("no connection to peer"))
                {
                    return new OpenChannelResponse(OpenChannelResult.PeerNotConnected);
                }

                if (e.Message.Contains("insufficient funds"))
                {
                    return new OpenChannelResponse(OpenChannelResult.CannotAffordFunding);
                }
                if (e.Message.Contains("peer sent error: 'Multiple channels unsupported'"))
                {
                    return new OpenChannelResponse(OpenChannelResult.AlreadyExists);
                }
                

                return new OpenChannelResponse(OpenChannelResult.AlreadyExists);
            }
        }

        public async Task<BitcoinAddress> GetDepositAddress()
        {
            if (_rpcClient == null)
            {
                throw new NotSupportedException("The bitcoind connection details were not provided.");
            }
            return await _rpcClient.GetNewAddressAsync();
        }

        public async Task ConnectTo(NodeInfo nodeInfo)
        {
            await _eclairClient.Connect(nodeInfo.NodeId, nodeInfo.Host, nodeInfo.Port);
        }

        public async Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default(CancellationToken))
        {
            var channels = await _eclairClient.Channels(null, cancellation);
            return channels.Select(response =>
            {
                OutPoint.TryParse(response.Data.Commitments.CommitInput.OutPoint.Replace(":", "-"),
                    out var outPoint);

                return new LightningChannel()
                {
                    IsPublic = ((ChannelFlags) response.Data.Commitments.ChannelFlags) == ChannelFlags.Public,
                    RemoteNode = new PubKey(response.NodeId),
                    IsActive = response.State == "NORMAL",
                    LocalBalance = new LightMoney(response.Data.Commitments.LocalCommit.Spec.ToLocalMsat),
                    Capacity = new LightMoney(response.Data.Commitments.CommitInput.AmountSatoshis),
                    ChannelPoint = outPoint,
                };
            }).ToArray();
        }
    }
}