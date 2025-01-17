using Lagrange.Core.Internal.Context.Attributes;
using Lagrange.Core.Internal.Event;
using Lagrange.Core.Internal.Event.EventArg;
using Lagrange.Core.Internal.Event.Protocol;
using Lagrange.Core.Internal.Event.Protocol.Message;
using Lagrange.Core.Internal.Event.Protocol.Notify;
using Lagrange.Core.Internal.Service;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entity;
using Lagrange.Core.Utility.Extension;

namespace Lagrange.Core.Internal.Context.Logic.Implementation;

[EventSubscribe(typeof(PushMessageEvent))]
[EventSubscribe(typeof(SendMessageEvent))]
[EventSubscribe(typeof(GroupSysInviteEvent))]
[EventSubscribe(typeof(GroupSysAdminEvent))]
[EventSubscribe(typeof(GroupSysIncreaseEvent))]
[EventSubscribe(typeof(GroupSysDecreaseEvent))]
[BusinessLogic("MessagingLogic", "Manage the receiving and sending of messages and notifications")]
internal class MessagingLogic : LogicBase
{
    private const string Tag = nameof(MessagingLogic);

    internal MessagingLogic(ContextCollection collection) : base(collection) { }

    public override async Task Incoming(ProtocolEvent e)
    {
        switch (e)
        {
            case PushMessageEvent push:
            {
                if (push.Chain.Count == 0) return;
                await ResolveAdditional(push.Chain);
                await ResolveChainMetadata(push.Chain);

                var chain = push.Chain;
                Collection.Log.LogVerbose(Tag, chain.ToPreviewString());

                EventBase args = push.Chain.GroupUin != null
                        ? new GroupMessageEvent(push.Chain)
                        : new FriendMessageEvent(push.Chain);
                Collection.Invoker.PostEvent(args);

                break;
            }
            case GroupSysInviteEvent invite:
            {
                uint invitorUin = await Collection.Business.CachingLogic.ResolveUin(null, invite.InvitorUid) ?? 0;
                var inviteArgs = new GroupInvitationEvent(invite.GroupUin, invitorUin);
                Collection.Invoker.PostEvent(inviteArgs);
                break;
            }
            case GroupSysAdminEvent admin:
            {
                uint adminUin = await Collection.Business.CachingLogic.ResolveUin(admin.GroupUin, admin.Uid) ?? 0;
                var adminArgs = new GroupAdminChangedEvent(admin.GroupUin, adminUin, admin.IsPromoted);
                Collection.Invoker.PostEvent(adminArgs);
                break;
            }
            case GroupSysIncreaseEvent increase:
            {
                uint memberUin = await Collection.Business.CachingLogic.ResolveUin(increase.GroupUin, increase.MemberUid, true) ?? 0;
                uint? invitorUin = null;
                if (increase.InvitorUid != null) invitorUin = await Collection.Business.CachingLogic.ResolveUin(increase.GroupUin, increase.InvitorUid);
                var increaseArgs = new GroupMemberIncreaseEvent(increase.GroupUin, memberUin, invitorUin);
                Collection.Invoker.PostEvent(increaseArgs);
                break;
            }
            case GroupSysDecreaseEvent decrease:
            {
                uint memberUin = await Collection.Business.CachingLogic.ResolveUin(decrease.GroupUin, decrease.MemberUid) ?? 0;
                uint? operatorUin = null;
                if (decrease.OperatorUid != null) operatorUin = await Collection.Business.CachingLogic.ResolveUin(decrease.GroupUin, decrease.OperatorUid);
                var decreaseArgs = new GroupMemberDecreaseEvent(decrease.GroupUin, memberUin, operatorUin);
                Collection.Invoker.PostEvent(decreaseArgs);
                break;
            }
        }
    }

    public override async Task Outgoing(ProtocolEvent e)
    {
        switch (e)
        {
            case SendMessageEvent send: // resolve Uin to Uid
                await ResolveChainMetadata(send.Chain);
                await ResolveChainUid(send.Chain);
                await UploadResources(send.Chain);
                break;
        }
    }

    private async Task ResolveAdditional(MessageChain chain)
    {
        if (chain.HasTypeOf<FileEntity>())
        {
            var file = chain.GetEntity<FileEntity>();
            if (file?.IsGroup != false || file.FileHash == null || file.FileUuid == null) return;
            
            var @event = FileDownloadEvent.Create(file.FileUuid, file.FileHash, chain.Uid, chain.SelfUid);
            var results = await Collection.Business.SendEvent(@event);
            if (results.Count != 0)
            {
                var result = (FileDownloadEvent)results[0];
                file.FileUrl = result.FileUrl;
            }
        }
        if (chain.HasTypeOf<MultiMsgEntity>())
        {
            var multi = chain.GetEntity<MultiMsgEntity>();
            if (multi?.ResId == null) return;
            
            var @event = MultiMsgDownloadEvent.Create(chain.Uid ?? "", multi.ResId);
            var results = await Collection.Business.SendEvent(@event);
            if (results.Count != 0)
            {
                var result = (MultiMsgDownloadEvent)results[0];
                multi.Chains.AddRange((IEnumerable<MessageChain>?)result.Chains ?? Array.Empty<MessageChain>());
            }
        }
    }
    
    private async Task ResolveChainUid(MessageChain chain)
    {
        foreach (var entity in chain)
        {
            switch (entity)
            {
                case MentionEntity mention:
                {
                    mention.Uid = await Collection.Business.CachingLogic.ResolveUid(chain.GroupUin, mention.Uin) ?? throw new Exception($"Failed to resolve Uid for Uin {mention.Uin}");
                    break;
                }
                case MultiMsgEntity multiMsg:
                {
                    foreach (var multi in multiMsg.Chains)
                    {
                        await ResolveChainMetadata(multi);
                        await UploadResources(multi);
                    }

                    var multiMsgEvent = MultiMsgUploadEvent.Create(multiMsg.GroupUin, multiMsg.Chains);
                    var results = await Collection.Business.SendEvent(multiMsgEvent);
                    if (results.Count != 0)
                    {
                        var result = (MultiMsgUploadEvent)results[0];
                        multiMsg.ResId = result.ResId;
                    }
                    break;
                }
            }
        }
    }

    private async Task ResolveChainMetadata(MessageChain chain)
    {
        if (chain is { IsGroup: true, GroupUin: not null })
        {
            var groups = await Collection.Business.CachingLogic.GetCachedMembers(chain.GroupUin.Value, false);
            chain.GroupMemberInfo = groups.FirstOrDefault(x => x.Uin == chain.FriendUin);
        }
        else
        {
            var friends = await Collection.Business.CachingLogic.GetCachedFriends(false);
            chain.FriendInfo = friends.FirstOrDefault(x => x.Uin == chain.FriendUin);
        }
    }

    private async Task UploadResources(MessageChain chain)
    {
        foreach (var entity in chain)
        {
            switch (entity)
            {
                case ImageEntity image:
                {
                    if (image.ImageStream == null) continue;
                    
                    if (!chain.IsGroup)
                    {
                        string uid = await Collection.Business.CachingLogic.ResolveUid(chain.GroupUin, chain.FriendUin) ?? throw new Exception($"Failed to resolve Uid for Uin {chain.FriendUin}");
                        var @event = ImageUploadEvent.Create(image.ImageStream, uid);
                        var results = await Collection.Business.SendEvent(@event);
                        if (results.Count != 0)
                        {
                            var ticketResult = (ImageUploadEvent)results[0];
                            if (!ticketResult.IsExist)
                            {
                                bool hwSuccess = await Collection.Highway.UploadSrcByStreamAsync(1, Collection.Keystore.Uin, image.ImageStream, ticketResult.Ticket, @event.FileMd5.UnHex());
                                if (!hwSuccess)
                                {
                                    Collection.Log.LogFatal(Tag, "Failed to upload image to highway");
                                    continue;
                                }
                            }

                            image.ImageStream = @event.Stream;
                            image.Path = ticketResult.ServerPath;
                        }
                    }
                    else
                    {
                        var @event = ImageGroupUploadEvent.Create(image.ImageStream, chain.GroupUin ?? throw new Exception());
                        var results = await Collection.Business.SendEvent(@event);
                        if (results.Count != 0)
                        {
                            var ticketResult = (ImageGroupUploadEvent)results[0];
                            if (!ticketResult.IsExist)
                            {
                                bool hwSuccess = await Collection.Highway.UploadSrcByStreamAsync(2, Collection.Keystore.Uin, image.ImageStream, ticketResult.Ticket, @event.FileMd5.UnHex());
                                if (!hwSuccess)
                                {
                                    Collection.Log.LogFatal(Tag, "Failed to upload image to highway");
                                    continue;
                                }
                            }

                            image.ImageStream = @event.Stream;
                            image.FileId = ticketResult.FileId;
                        }
                    }
                    break;
                }
            }
        }
    }
}