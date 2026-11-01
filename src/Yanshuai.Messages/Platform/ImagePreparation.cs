using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Yanshuai.Messages.Platform
{
    public sealed class ImagePreparation
    {
        public static async Task<byte[]> CompressAsync(byte[] source, int maxDimension)
        {
            using (var input = await StreamAsync(source))
            {
                var decoder = await BitmapDecoder.CreateAsync(input);
                double scale = Math.Min(1.0, maxDimension / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
                var transform = new BitmapTransform { ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * scale)),
                    ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * scale)), InterpolationMode = BitmapInterpolationMode.Fant };
                using (var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb))
                    return await EncodeAsync(bitmap);
            }
        }

        public static async Task<byte[]> CropSquareAsync(byte[] source, double horizontal, double vertical, double fraction)
        {
            using (var input = await StreamAsync(source))
            {
                var decoder = await BitmapDecoder.CreateAsync(input);
                uint side = Math.Max(1, (uint)(Math.Min(decoder.PixelWidth, decoder.PixelHeight) * Math.Max(.1, Math.Min(1, fraction))));
                var transform = new BitmapTransform { Bounds = new BitmapBounds {
                    X = (uint)((decoder.PixelWidth - side) * Math.Max(0, Math.Min(1, horizontal))),
                    Y = (uint)((decoder.PixelHeight - side) * Math.Max(0, Math.Min(1, vertical))), Width = side, Height = side } };
                using (var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb))
                    return await EncodeAsync(bitmap, true);
            }
        }

        public static async Task<InMemoryRandomAccessStream> StreamAsync(byte[] source)
        {
            if (source == null || source.Length == 0) throw new ArgumentException("图片为空。");
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(source); await writer.StoreAsync(); }
            stream.Seek(0); return stream;
        }

        private static async Task<byte[]> EncodeAsync(SoftwareBitmap bitmap, bool png = false)
        {
            using (var output = new InMemoryRandomAccessStream())
            {
                var encoder = await BitmapEncoder.CreateAsync(png ? BitmapEncoder.PngEncoderId : BitmapEncoder.JpegEncoderId, output);
                var buffer = new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
                bitmap.CopyToBuffer(buffer);
                var pixels = new byte[buffer.Length];
                using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
                await encoder.FlushAsync(); output.Seek(0);
                using (var stream = output.AsStreamForRead())
                using (var memory = new MemoryStream()) { await stream.CopyToAsync(memory); return memory.ToArray(); }
            }
        }
    }
}
