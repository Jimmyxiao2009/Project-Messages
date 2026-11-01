using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    public sealed class AuthorizedFolder
    {
        public string Token { get; set; } = "";
        public string Name { get; set; } = "";
        public string ProfileId { get; set; } = "";
        public bool ReadOnly { get; set; } = true;
    }
    public sealed class FileMount
    {
        public AuthorizedFolder Configuration;
        public Func<Task<IFileStore>> Open;
    }

    // External folders are mounted inside the profile sandbox, never reached via "..".
    public sealed class MountedFileStore : IFileStore
    {
        private readonly IFileStore _workspace;
        private readonly string _root;
        private readonly Dictionary<string,FileMount> _mounts;
        private sealed class Target { public IFileStore Store; public string Path; public bool ReadOnly; public string Prefix; }
        public MountedFileStore(IFileStore workspace,string root,IEnumerable<FileMount> mounts)
        {
            _workspace=workspace;_root=root;
            _mounts=mounts.ToDictionary(x=>x.Configuration.Token,StringComparer.Ordinal);
        }
        private async Task<Target> ResolveAsync(string path,bool write)
        {
            var prefix=_root+"/_folders/";
            if(!(path??"").StartsWith(prefix,StringComparison.Ordinal))return new Target { Store=_workspace,Path=path,Prefix="" };
            var rest=path.Substring(prefix.Length);int slash=rest.IndexOf('/');
            var token=slash<0?rest:rest.Substring(0,slash);FileMount mount;
            if(!_mounts.TryGetValue(token,out mount))throw new UnauthorizedAccessException("目录未授权给此档案。");
            string relative=slash<0?"":rest.Substring(slash+1);
            if(relative.Length>0)WorkspaceOperations.ValidatePath(relative);
            if(write&&mount.Configuration.ReadOnly)throw new UnauthorizedAccessException("此目录只有读取权限。");
            return new Target { Store=await mount.Open().ConfigureAwait(false),Path=relative,
                ReadOnly=mount.Configuration.ReadOnly,Prefix=prefix+token };
        }
        public async Task<bool> ExistsAsync(string path){var t=await ResolveAsync(path,false);return await t.Store.ExistsAsync(t.Path);}
        public async Task<string> ReadTextAsync(string path){var t=await ResolveAsync(path,false);return await t.Store.ReadTextAsync(t.Path);}
        public async Task<byte[]> ReadBytesAsync(string path){var t=await ResolveAsync(path,false);return await t.Store.ReadBytesAsync(t.Path);}
        public async Task WriteTextAsync(string path,string content){var t=await ResolveAsync(path,true);await t.Store.WriteTextAsync(t.Path,content);}
        public async Task WriteBytesAsync(string path,byte[] data){var t=await ResolveAsync(path,true);await t.Store.WriteBytesAsync(t.Path,data);}
        public async Task DeleteAsync(string path){var t=await ResolveAsync(path,true);await t.Store.DeleteAsync(t.Path);}
        public async Task EnsureDirectoryAsync(string path){var t=await ResolveAsync(path,true);await t.Store.EnsureDirectoryAsync(t.Path);}
        public async Task MoveAsync(string from,string to)
        {
            var source=await ResolveAsync(from,true);var target=await ResolveAsync(to,true);
            if(source.Prefix!=target.Prefix)throw new UnauthorizedAccessException("不允许跨授权目录移动。");
            await source.Store.MoveAsync(source.Path,target.Path);
        }
        public async Task<IReadOnlyList<string>> ListFilesAsync(string path,bool recursive)
        {
            if(path==_root+"/_folders")return new List<string>();
            var t=await ResolveAsync(path,false);var files=await t.Store.ListFilesAsync(t.Path,recursive);
            return t.Prefix.Length==0?files:files.Select(x=>t.Prefix+"/"+x).ToList();
        }
        public async Task<IReadOnlyList<string>> ListDirectoriesAsync(string path)
        {
            if(path==_root+"/_folders")return _mounts.Keys.Select(x=>path+"/"+x).ToList();
            var t=await ResolveAsync(path,false);var dirs=await t.Store.ListDirectoriesAsync(t.Path);
            var result=t.Prefix.Length==0?dirs.ToList():dirs.Select(x=>t.Prefix+"/"+x).ToList();
            if(path==_root&&_mounts.Count>0)result.Add(_root+"/_folders");
            return result;
        }
        public string GetAbsolutePath(string path)
        {
            if((path??"").StartsWith(_root+"/_folders/",StringComparison.Ordinal))
                throw new UnauthorizedAccessException("外部目录仅通过授权文件接口访问。");
            return _workspace.GetAbsolutePath(path);
        }
    }
}
