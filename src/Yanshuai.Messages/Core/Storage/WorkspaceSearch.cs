using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    public sealed class WorkspaceSearchHit
    {
        public string Kind;
        public string ProfileId;
        public string Id;
        public string Title;
        public string Preview;
    }
    public sealed class WorkspaceSearch
    {
        private readonly Workspace _workspace; private readonly IFileStore _store; private readonly ILogger _log;
        public WorkspaceSearch(Workspace workspace,IFileStore store,ILogger log){_workspace=workspace;_store=store;_log=log;}
        private static bool Contains(string text,string query){return (text??"").IndexOf(query,StringComparison.CurrentCultureIgnoreCase)>=0;}
        public async Task<List<WorkspaceSearchHit>> SearchAsync(string query,CancellationToken ct)
        {
            var result=new List<WorkspaceSearchHit>();
            if(string.IsNullOrWhiteSpace(query))return result;query=query.Trim();
            foreach(var profile in _workspace.Profiles) {
                ct.ThrowIfCancellationRequested();
                if(Contains(profile.Name+" "+profile.Persona.Description,query))
                    result.Add(new WorkspaceSearchHit { Kind="profile",ProfileId=profile.Id,Title=profile.Name,Preview=TextUtil.Truncate(profile.Persona.Description,120) });
                foreach(var entry in await _workspace.ListConversationsAsync(profile.Slug,true).ConfigureAwait(false)) {
                    ct.ThrowIfCancellationRequested();
                    var conversation=await _workspace.LoadConversationAsync(profile.Slug,entry.Id).ConfigureAwait(false);
                    if(conversation==null)continue;
                    string preview=null;
                    if(Contains(entry.Title,query))preview=entry.Preview;
                    else foreach(var message in conversation.Messages)if(Contains(message.Content,query)){preview=TextUtil.Truncate(message.Content,160);break;}
                    if(preview!=null)result.Add(new WorkspaceSearchHit { Kind="conversation",ProfileId=profile.Id,Id=entry.Id,Title=entry.Title,Preview=preview });
                    if(result.Count>=100)return result;
                }
                var memory=await JsonStore.TryReadAsync<MemoryFile>(_store,WorkspaceLayout.MemoryFile(profile.Slug),_log).ConfigureAwait(false);
                if(memory!=null)foreach(var item in memory.Items)if(Contains(item.Text,query)) {
                    result.Add(new WorkspaceSearchHit { Kind="memory",ProfileId=profile.Id,Id=item.Id,Title=item.Text,Preview=MemoryCategories.Label(item.Category) });
                    if(result.Count>=100)return result;
                }
            }
            var global=await JsonStore.TryReadAsync<MemoryFile>(_store,WorkspaceLayout.SharedGlobalMemory,_log).ConfigureAwait(false);
            if(global!=null)foreach(var item in global.Items)if(Contains(item.Text,query)) {
                result.Add(new WorkspaceSearchHit { Kind="global-memory",Id=item.Id,Title=item.Text,Preview="全局记忆" });
                if(result.Count>=100)break;
            }
            return result;
        }
    }
}
