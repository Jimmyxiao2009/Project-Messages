using System;

namespace Yanshuai.Messages.Core
{
    public static class ModelSelection
    {
        // ApiProfile is the stored provider. Return a request copy so switching
        // a conversation never edits another conversation or provider defaults.
        public static ApiProfile Resolve(ApiProfileFile providers, Profile profile, Conversation conversation)
        {
            if (providers == null) return null;
            bool conversationProvider = conversation != null && !string.IsNullOrEmpty(conversation.ModelApiProfileId);
            var source = conversationProvider ? providers.Find(conversation.ModelApiProfileId)
                : providers.Resolve(profile != null ? profile.Api.ProfileId : "");
            if (source == null) return null;
            var api = source.Clone();
            bool profileModel = profile != null && !string.IsNullOrEmpty(profile.Api.Model) && !conversationProvider;
            if (profileModel) api.Model = profile.Api.Model;
            if (conversation != null && !string.IsNullOrEmpty(conversation.ModelOverride)) api.Model = conversation.ModelOverride;
            var metadata = source.ResolveMetadata(api.Model);
            if (metadata.ContextWindow > 0) api.MaxContextTokens = metadata.ContextWindow;
            if (metadata.Vision.HasValue) api.VisionEnabled = metadata.Vision.Value;
            if (profile != null && profile.Api.MaxContextTokens > 0 &&
                (conversation == null || string.IsNullOrEmpty(conversation.ModelOverride)) && !conversationProvider)
                api.MaxContextTokens = profile.Api.MaxContextTokens;
            return api;
        }

        /// <summary>
        /// 副手模型。顺序：全局指定 → 旧的档案 / Provider subModel（只在跟随档案时才看档案的）→ 对话模型。
        /// 返回请求用的副本，不会改动存储里的 Provider。
        /// </summary>
        public static ApiProfile ResolveAssistant(ApiProfileFile providers, ModelRef assistant, Profile profile, Conversation conversation)
        {
            var chosen = ResolveRef(providers, assistant);
            if (chosen != null) return chosen;
            var api = Resolve(providers, profile, conversation);
            if (api == null) return null;
            bool followsProfile = conversation == null || string.IsNullOrEmpty(conversation.ModelApiProfileId);
            if (followsProfile && profile != null && profile.Api != null && !string.IsNullOrEmpty(profile.Api.SubModel)) api.Model = profile.Api.SubModel;
            else if (!string.IsNullOrEmpty(api.SubModel)) api.Model = api.SubModel;
            return api;
        }

        /// <summary>按引用取 Provider 副本并套上模型；引用不完整或 Provider 已删时返回 null。</summary>
        public static ApiProfile ResolveRef(ApiProfileFile providers, ModelRef reference)
        {
            if (providers == null || reference == null || !reference.IsSet) return null;
            var source = providers.Find(reference.ApiProfileId);
            if (source == null) return null;
            var api = source.Clone();
            api.Model = reference.Model;
            var metadata = source.ResolveMetadata(api.Model);
            if (metadata.ContextWindow > 0) api.MaxContextTokens = metadata.ContextWindow;
            return api;
        }

        /// <summary>
        /// 「这个模型收不收图片」的唯一判定入口。三个调用点必须都用它 ——
        /// 判断依据分散过一次，症状是**图片被静默丢弃**：
        ///   ChatService 用元数据（放行，不报错）→ AgentRunner 用元数据（请求带 VisionEnabled=true）
        ///   → PromptAssembler 只看裸布尔量（false，于是 turn.Images 是空的）
        /// 结果请求声明支持图片、消息里却没有图，模型回答"我没收到图片"，
        /// 界面上既不报错也没有任何日志。所以别再在别处写第二份判断。
        ///
        /// 优先级：模型元数据（手动覆盖 > 服务端 > 内置表）> 档案级开关 > Provider 级开关。
        /// </summary>
        public static bool ResolveVision(Profile profile, ApiProfile api)
        {
            var metadata = api != null ? api.ResolveMetadata(api.Model) : null;
            if (metadata != null && metadata.Vision.HasValue) return metadata.Vision.Value;
            if (profile != null && profile.Api != null && profile.Api.VisionEnabled.HasValue) return profile.Api.VisionEnabled.Value;
            return api != null && api.VisionEnabled;
        }

        public static void SetDefault(ApiProfileFile providers, string providerId, string modelId)
        {
            var provider = providers.Find(providerId);
            if (provider == null || string.IsNullOrWhiteSpace(modelId)) throw new InvalidOperationException("请选择有效的 Provider 和模型。");
            provider.Model = modelId;
            providers.DefaultId = providerId;
        }
    }
}
