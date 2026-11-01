using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Ui.Pages
{
    public sealed class FileEditContext { public string Path; public string Text; public string Hash; }

    public sealed class FilesPage : DetailPage
    {
        protected override async Task BuildAsync()
        {
            var p = Context.Profile;
            if (p == null) throw new InvalidOperationException("先选择档案。");
            if (Context.Section == "avatar") {
                Title("选择头像", p.Name);
                if (p.Avatar.Length > 0) {
                    var image = new WorkspaceImage { SourcePath = p.Avatar, Height = 180 };
                    Body.Children.Add(image);
                }
                Row("从相册选择", "PNG / JPEG，自动降采样", async delegate {
                    var file = await PickAsync(true); if (file == null) return;
                    var bytes = await ReadBytes(file, 10 * 1024 * 1024);
                    Go<AvatarCropPage>(null, bytes);
                });
                Row("拍照", "使用系统相机并裁剪头像", async delegate {
                    if (!Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.Media.Capture.CameraCaptureUI"))
                        throw new InvalidOperationException("此设备不支持系统相机选择器。");
                    var camera = new Windows.Media.Capture.CameraCaptureUI();
                    camera.PhotoSettings.AllowCropping = true;
                    camera.PhotoSettings.CroppedAspectRatio = new Windows.Foundation.Size(1, 1);
                    var file = await camera.CaptureFileAsync(Windows.Media.Capture.CameraCaptureUIMode.Photo);
                    if (file != null) Go<AvatarCropPage>(null, await ReadBytes(file, 10 * 1024 * 1024));
                });
                if (await S.Files.ExistsAsync(WorkspaceLayout.CardPngFile(p.Slug))) Row("从角色卡图片", "裁剪已导入卡图", async delegate {
                    Go<AvatarCropPage>(null, await S.Files.ReadBytesAsync(WorkspaceLayout.CardPngFile(p.Slug)));
                });                Row("使用首字", "删除自定义头像", async delegate { p.Avatar = ""; await W.SaveProfileAsync(p); Finish(); });
                return;
            }
            string directory = Context.Item as string ?? WorkspaceLayout.FilesDir(p.Slug);
            bool attachments = Context.Section == "attachments";
            Title(attachments ? "会话附件" : WorkspaceLayout.FileName(directory), p.Name);
            if (attachments && Context.Conversation != null) {
                foreach (var attachment in Context.Conversation.Messages.SelectMany(x => x.Attachments).GroupBy(x => x.Path).Select(x => x.First()))
                    Link<FilePreviewPage>(attachment.Name, StorageStats.FormatBytes(attachment.SizeBytes), null, attachment.Path);
            } else {
                foreach (var folder in await S.Files.ListDirectoriesAsync(directory))
                    Link<FilesPage>("▣ " + WorkspaceLayout.FileName(folder), "文件夹", null, folder);
                foreach (var file in await S.Files.ListFilesAsync(directory, false))
                    Link<FilePreviewPage>(WorkspaceLayout.FileName(file), WorkspaceLayout.Extension(file), null, file);
            }
            if (Body.Children.Count == 0) Text("还没有文件。导入或新建文本文件。");
            Command("导入", Symbol.Add, async delegate {
                RequireIdle(); var file = await PickAsync(false); if (file == null) return;
                var bytes = await ReadBytes(file, 32 * 1024 * 1024);
                string path = directory + "/" + file.Name;
                if (await S.Files.ExistsAsync(path)) path = directory + "/" + Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + file.Name;
                await S.Files.WriteBytesAsync(path, bytes);
                Go<FilePreviewPage>(attachments ? "attach" : null, path);
            });
            Command("新建文本", Symbol.Edit, delegate { Go<FileEditorPage>(null, new FileEditContext { Path = directory }); return Task.CompletedTask; });
            Link<SettingsPage>("附件处理设置", "PDF 页数与文档解析", "attachment");
        }

        internal static async Task<StorageFile> PickAsync(bool image)
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = image ? PickerLocationId.PicturesLibrary : PickerLocationId.DocumentsLibrary };
            if (image) { picker.FileTypeFilter.Add(".png"); picker.FileTypeFilter.Add(".jpg"); picker.FileTypeFilter.Add(".jpeg"); }
            else picker.FileTypeFilter.Add("*");
            return await picker.PickSingleFileAsync();
        }

        internal static async Task<byte[]> ReadBytes(StorageFile file, long max)
        {
            var properties = await file.GetBasicPropertiesAsync();
            if ((long)properties.Size > max) throw new InvalidOperationException("文件超过 " + StorageStats.FormatBytes(max) + "。");
            using (var stream = await file.OpenStreamForReadAsync())
            using (var memory = new MemoryStream()) { await stream.CopyToAsync(memory); return memory.ToArray(); }
        }
    }

    public sealed class FilePreviewPage : DetailPage
    {
        protected override async Task BuildAsync()
        {
            string path = Context.Item as string;
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("文件路径缺失。");
            string name = WorkspaceLayout.FileName(path), extension = WorkspaceLayout.Extension(path);
            Title(name, "文件预览");
            bool image = new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp" }.Contains(extension);
            DocumentResult document = null;
            if (image) Body.Children.Add(new WorkspaceImage { SourcePath = path, MaxHeight = 480,
                Stretch = Windows.UI.Xaml.Media.Stretch.Uniform });
            else {
                var extractor = new DocumentExtractor(S.Files, new Yanshuai.Messages.Platform.UwpPdfRenderer(S.Files));
                document = await extractor.ExtractAsync(path, name, W.Settings.AttachmentMaxPages, Lifetime.Token);
                foreach (var warning in document.Warnings) Text(warning, true);
                if (!document.Ok) Text("此文件无法作为文本解析，可保留在档案文件中。");
                if (document.Text.Length > 0) { var preview = Field("解析文字", document.Text, true); preview.IsReadOnly = true; }
                foreach (var page in document.Images) {
                    var bitmap = new BitmapImage();
                    using (var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
                    using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0))) {
                        writer.WriteBytes(Convert.FromBase64String(page.Base64)); await writer.StoreAsync(); await bitmap.SetSourceAsync(stream);
                    }
                    Body.Children.Add(new Image { Source = bitmap, MaxHeight = 300 });
                }
            }
            if (Context.Conversation != null && Context.AddAttachment != null && (image || (document != null && document.Ok)))
                Command("附到输入框", Symbol.Attach, async delegate {
                    RequireIdle();
                    if (image) {
                        var bytes = await S.Files.ReadBytesAsync(path);
                        var compressed = await Yanshuai.Messages.Platform.ImagePreparation.CompressAsync(bytes, 1536);
                        string target = WorkspaceLayout.AttachmentsDir(Context.Profile.Slug) + "/" + Guid.NewGuid().ToString("N") + ".jpg";
                        await S.Files.WriteBytesAsync(target, compressed);
                        Context.AddAttachment(new Attachment { Name = name, Path = target, Kind = "image", MimeType = "image/jpeg", SizeBytes = compressed.Length });
                    } else {
                        if (document.Text.Length > 0) {
                            string target = WorkspaceLayout.AttachmentsDir(Context.Profile.Slug) + "/" + Guid.NewGuid().ToString("N") + ".txt";
                            await S.Files.WriteTextAsync(target, document.Text);
                            Context.AddAttachment(new Attachment { Name = name, Path = target, Kind = "text", SizeBytes = System.Text.Encoding.UTF8.GetByteCount(document.Text) });
                        }
                        foreach (var page in document.Images) {
                            var bytes = Convert.FromBase64String(page.Base64);
                            string target = WorkspaceLayout.AttachmentsDir(Context.Profile.Slug) + "/" + Guid.NewGuid().ToString("N") + ".jpg";
                            await S.Files.WriteBytesAsync(target, bytes);
                            Context.AddAttachment(new Attachment { Name = name + " · " + page.PageNumber, Path = target, Kind = "image",
                                MimeType = page.MimeType, SizeBytes = bytes.Length });
                        }
                    }
                    Context.Close();
                });
            if (new[] { ".txt", ".md", ".json", ".csv", ".cs", ".py", ".js", ".xml", ".yaml", ".yml" }.Contains(extension))
                Command("编辑", Symbol.Edit, async delegate {
                    var text = await S.Files.ReadTextAsync(path);
                    Go<FileEditorPage>(null, new FileEditContext { Path = path, Text = text, Hash = TextUtil.Sha256Hex(text) });
                });
            Command("删除", Symbol.Delete, async delegate {
                RequireIdle();
                if (await Confirm("删除文件？", name + " 将移入回收区；相关附件可能暂时无法读取。", "删除")) {
                    await S.Operations.RecycleFileAsync(path, name, "file"); Finish();
                }
            }, true);
        }
    }

    public sealed class FileEditorPage : DetailPage
    {
        protected override Task BuildAsync()
        {
            var file = (FileEditContext)Context.Item;
            bool create = file.Hash == null;
            Title(create ? "新建文本文件" : "编辑文本文件", "文件");
            var name = Field("文件名", create ? "notes.md" : WorkspaceLayout.FileName(file.Path)); name.IsReadOnly = !create;
            var text = Field("内容", file.Text, true);
            Save(async delegate {
                RequireIdle(); string path = create ? file.Path + "/" + Required(name) : file.Path;
                if (create && (name.Text.Contains("/") || name.Text.Contains("\\"))) throw new ArgumentException("文件名不能包含目录。");
                if (!await S.Operations.SaveTextAsync(path, file.Hash, text.Text, false)) {
                    if (!await Confirm("文件保存冲突", "文件已被修改。覆盖会丢弃外部修改，也可以取消并重新打开。", "覆盖"))
                        throw new InvalidOperationException("未覆盖，编辑内容仍保留。");
                    await S.Operations.SaveTextAsync(path, file.Hash, text.Text, true);
                }
            });
            return Task.CompletedTask;
        }
    }
}
