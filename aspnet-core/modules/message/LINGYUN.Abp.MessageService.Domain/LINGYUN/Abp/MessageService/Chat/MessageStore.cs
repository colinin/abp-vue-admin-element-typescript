﻿using LINGYUN.Abp.IM.Contract;
using LINGYUN.Abp.IM.Messages;
using LINGYUN.Abp.MessageService.Group;
using LINGYUN.Abp.MessageService.Utils;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Domain.Services;
using Volo.Abp.ObjectMapping;
using Volo.Abp.Uow;

namespace LINGYUN.Abp.MessageService.Chat
{
    public class MessageStore : DomainService, IMessageStore
    {
        private IFriendStore _friendStore;
        protected IFriendStore FriendStore => LazyGetRequiredService(ref _friendStore);

        private IObjectMapper _objectMapper;
        protected IObjectMapper ObjectMapper => LazyGetRequiredService(ref _objectMapper);

        private IUnitOfWorkManager _unitOfWorkManager;
        protected IUnitOfWorkManager UnitOfWorkManager => LazyGetRequiredService(ref _unitOfWorkManager);
        protected IUserChatSettingRepository UserChatSettingRepository { get; }
        protected IMessageRepository MessageRepository { get; }
        protected IGroupRepository GroupRepository { get; }
        protected ISnowflakeIdGenerator SnowflakeIdGenerator { get; }
        public MessageStore(
            IGroupRepository groupRepository,
            IMessageRepository messageRepository,
            ISnowflakeIdGenerator snowflakeIdGenerator,
            IUserChatSettingRepository userChatSettingRepository)
        {
            GroupRepository = groupRepository;
            MessageRepository = messageRepository;
            SnowflakeIdGenerator = snowflakeIdGenerator;
            UserChatSettingRepository = userChatSettingRepository;
        }

        [UnitOfWork]
        public virtual async Task StoreMessageAsync(ChatMessage chatMessage)
        {
            using (var unitOfWork = UnitOfWorkManager.Begin())
            {
                using (CurrentTenant.Change(chatMessage.TenantId))
                {
                    if (!chatMessage.GroupId.IsNullOrWhiteSpace())
                    {
                        long groupId = long.Parse(chatMessage.GroupId);
                        await StoreGroupMessageAsync(chatMessage, groupId);
                    }
                    else
                    {
                        await StoreUserMessageAsync(chatMessage);
                    }
                    await unitOfWork.SaveChangesAsync();
                }
            }
        }

        public virtual async Task<List<ChatMessage>> GetGroupMessageAsync(
            Guid? tenantId, 
            long groupId,
            string filter = "",
            string sorting = nameof(ChatMessage.MessageId),
            bool reverse = true, 
            MessageType? type = null, 
            int skipCount = 0, 
            int maxResultCount = 10)
        {
            using (CurrentTenant.Change(tenantId))
            {
                var groupMessages = await MessageRepository
                    .GetGroupMessagesAsync(groupId, filter, sorting, reverse, type, skipCount, maxResultCount);
                var chatMessages = ObjectMapper.Map<List<GroupMessage>, List<ChatMessage>>(groupMessages);

                return chatMessages;
            }
        }

        public virtual async Task<List<ChatMessage>> GetChatMessageAsync(
            Guid? tenantId, 
            Guid sendUserId, 
            Guid receiveUserId, 
            string filter = "",
            string sorting = nameof(ChatMessage.MessageId),
            bool reverse = true, 
            MessageType? type = null, 
            int skipCount = 0, 
            int maxResultCount = 10)
        {
            using (CurrentTenant.Change(tenantId))
            {
                var userMessages = await MessageRepository
                    .GetUserMessagesAsync(sendUserId, receiveUserId, filter, sorting, reverse, type, skipCount, maxResultCount);
                var chatMessages = ObjectMapper.Map<List<UserMessage>, List<ChatMessage>>(userMessages);

                return chatMessages;
            }
        }

        public virtual async Task<List<LastChatMessage>> GetLastChatMessagesAsync(
            Guid? tenantId,
            Guid userId,
            string sorting = nameof(LastChatMessage.SendTime),
            bool reverse = true,
            int maxResultCount = 10
            )
        {
            using (CurrentTenant.Change(tenantId))
            {
                return await MessageRepository
                    .GetLastMessagesByOneFriendAsync(userId, sorting, reverse, maxResultCount);
            }
        }

        public virtual async Task<long> GetGroupMessageCountAsync(
            Guid? tenantId, 
            long groupId, 
            string filter = "",
            MessageType? type = null)
        {
            using (CurrentTenant.Change(tenantId))
            {
                return await MessageRepository.GetCountAsync(groupId, filter, type);
            }
        }

        public virtual async Task<long> GetChatMessageCountAsync(
            Guid? tenantId,
            Guid sendUserId, 
            Guid receiveUserId, 
            string filter = "", 
            MessageType? type = null)
        {
            using (CurrentTenant.Change(tenantId))
            {
                return await MessageRepository.GetCountAsync(sendUserId, receiveUserId, filter, type);
            }
        }

        protected virtual async Task StoreUserMessageAsync(ChatMessage chatMessage)
        {
            // 检查接收用户
            if (!chatMessage.ToUserId.HasValue)
            {
                throw new BusinessException(MessageServiceErrorCodes.UseNotFount);
            }

            var myFriend = await FriendStore.GetMemberAsync(chatMessage.TenantId, chatMessage.ToUserId.Value, chatMessage.FormUserId);

            var userChatSetting = await UserChatSettingRepository.FindByUserIdAsync(chatMessage.ToUserId.Value);
            if (userChatSetting != null)
            {
                if (!userChatSetting.AllowReceiveMessage)
                {
                    // 当前发送的用户不接收消息
                    throw new BusinessException(MessageServiceErrorCodes.UserHasRejectAllMessage);
                }

                if (myFriend == null && !chatMessage.IsAnonymous)
                {
                    throw new BusinessException(MessageServiceErrorCodes.UserHasRejectNotFriendMessage);
                }

                if (chatMessage.IsAnonymous && !userChatSetting.AllowAnonymous)
                {
                    // 当前用户不允许匿名发言
                    throw new BusinessException(MessageServiceErrorCodes.UserNotAllowedToSpeakAnonymously);
                }
            }
            else
            {
                if (myFriend == null)
                {
                    throw new BusinessException(MessageServiceErrorCodes.UserHasRejectNotFriendMessage);
                }
            }
            if (myFriend?.Black == true)
            {
                throw new BusinessException(MessageServiceErrorCodes.UserHasBlack);
            }
            var messageId = SnowflakeIdGenerator.Create();
            var message = new UserMessage(messageId, chatMessage.FormUserId, chatMessage.FormUserName, chatMessage.Content, chatMessage.MessageType);
            message.SendToUser(chatMessage.ToUserId.Value);
            await MessageRepository.InsertUserMessageAsync(message);

            chatMessage.MessageId = messageId.ToString();
        }

        protected virtual async Task StoreGroupMessageAsync(ChatMessage chatMessage, long groupId)
        {
            var userHasBlacked = await GroupRepository
                   .UserHasBlackedAsync(groupId, chatMessage.FormUserId);
            if (userHasBlacked)
            {
                // 当前发送的用户已被拉黑
                throw new BusinessException(MessageServiceErrorCodes.GroupUserHasBlack);
            }
            var group = await GroupRepository.GetByIdAsync(groupId);
            if (!group.AllowSendMessage)
            {
                // 当前群组不允许发言
                throw new BusinessException(MessageServiceErrorCodes.GroupNotAllowedToSpeak);
            }
            if (chatMessage.IsAnonymous && !group.AllowAnonymous)
            {
                // 当前群组不允许匿名发言
                throw new BusinessException(MessageServiceErrorCodes.GroupNotAllowedToSpeakAnonymously);
            }
            var messageId = SnowflakeIdGenerator.Create();
            var message = new GroupMessage(messageId, chatMessage.FormUserId, chatMessage.FormUserName, chatMessage.Content, chatMessage.MessageType);

            message.SendToGroup(groupId);
            await MessageRepository.InsertGroupMessageAsync(message);

            chatMessage.MessageId = messageId.ToString();
        }
    }
}
