using System;
using System.Threading.Tasks;
using Yanshuai.Messages.Core;
namespace Yanshuai.Messages.Ui.Pages
{
    public sealed class CompactPreview
    {
        public CompactResult Result;
        public int MessageCount;
        public string LastMessageId;
    }
    public sealed class CompactPreviewPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            var preview=(CompactPreview)Context.Item;
            Title("上下文压缩预览",Context.Conversation.Title);
            Text(preview.Result.CompressedCount+" 条消息 · 约 "+preview.Result.TokensBefore+" → "+preview.Result.TokensAfter+" tokens",true);
            var summary=Field("新摘要",preview.Result.Summary,true);
            Text("应用后使用此摘要参与后续请求，原消息保留。",true);
            Save(async delegate {
                RequireIdle();var c=Context.Conversation;
                string last=c.Messages.Count>0?c.Messages[c.Messages.Count-1].Id:"";
                if(c.Messages.Count!=preview.MessageCount||last!=preview.LastMessageId)throw new InvalidOperationException("会话已发生变化，请重新生成预览。");
                c.Summary=Required(summary);c.SummarizedUpTo=preview.Result.SummarizedUpTo;c.LastCompactAtUtc=S.Clock.UtcNow;
                await W.SaveConversationAsync(Context.Profile.Slug,c);
            });
            return Task.CompletedTask;
        }
    }
}
