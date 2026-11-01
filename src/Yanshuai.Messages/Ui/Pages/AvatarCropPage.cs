using System;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Yanshuai.Messages.Core;
using Yanshuai.Messages.Platform;

namespace Yanshuai.Messages.Ui.Pages
{
    public sealed class AvatarCropPage : DetailPage
    {
        protected override async Task BuildAsync()
        {
            Title("裁剪头像",Context.Profile.Name);
            var bytes=await ImagePreparation.CompressAsync((byte[])Context.Item,1024);
            var image=new Image { Height=200,Stretch=Windows.UI.Xaml.Media.Stretch.Uniform };
            Body.Children.Add(image);
            var x=Range("水平位置",.5,0,1,.05);var y=Range("垂直位置",.5,0,1,.05);var size=Range("裁剪范围",1,.1,1,.05);
            async Task Preview()
            {
                var cropped=await ImagePreparation.CropSquareAsync(bytes,x.Value,y.Value,size.Value);
                var bitmap=new BitmapImage();
                using(var stream=await ImagePreparation.StreamAsync(cropped))await bitmap.SetSourceAsync(stream);
                image.Source=bitmap;
            }
            await Preview();
            Command("预览裁剪",Symbol.View,Preview);
            Save(async delegate {
                RequireIdle();
                var cropped=await ImagePreparation.CropSquareAsync(bytes,x.Value,y.Value,size.Value);
                var path=WorkspaceLayout.AvatarFile(Context.Profile.Slug);
                await S.Files.WriteBytesAsync(path,cropped);Context.Profile.Avatar=path;await W.SaveProfileAsync(Context.Profile);
            });
        }
    }
}
