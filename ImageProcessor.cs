using System;
using System.IO;
using SkiaSharp;

namespace SimpleSyndicator;

/// <summary>
/// Validates, resizes, and compresses images to meet Telegram photo upload limits using SkiaSharp.
/// </summary>
public static class ImageProcessor
{
    public const int MaxDimension = 5120;
    public const int MaxDimensionSum = 10000;
    public const double MaxAspectRatio = 20.0;
    public const long MaxFileSizeBytes = 10_485_760;

    private static readonly SKSamplingOptions DownscaleSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    public static (int Width, int Height, bool Resized) CalculateTargetDimensions(int originalWidth, int originalHeight)
    {
        if (originalWidth <= 0 || originalHeight <= 0)
        {
            return (originalWidth, originalHeight, false);
        }

        var newWidth = originalWidth;
        var newHeight = originalHeight;
        var resized = false;

        // Check max dimensions (5120x5120)
        if (newWidth > MaxDimension || newHeight > MaxDimension)
        {
            var ratio = Math.Min((double)MaxDimension / newWidth, (double)MaxDimension / newHeight);
            newWidth = Math.Max(1, (int)Math.Floor(newWidth * ratio));
            newHeight = Math.Max(1, (int)Math.Floor(newHeight * ratio));
            resized = true;
        }

        // Check total sum of width + height <= 10000
        if (newWidth + newHeight > MaxDimensionSum)
        {
            var ratio = (double)MaxDimensionSum / (newWidth + newHeight);
            newWidth = Math.Max(1, (int)Math.Floor(newWidth * ratio));
            newHeight = Math.Max(1, (int)Math.Floor(newHeight * ratio));
            resized = true;
        }

        // Check aspect ratio <= 20:1
        if ((double)newWidth / newHeight > MaxAspectRatio)
        {
            newWidth = Math.Max(1, (int)Math.Floor(newHeight * MaxAspectRatio));
            resized = true;
        }
        else if ((double)newHeight / newWidth > MaxAspectRatio)
        {
            newHeight = Math.Max(1, (int)Math.Floor(newWidth * MaxAspectRatio));
            resized = true;
        }

        return (newWidth, newHeight, resized);
    }

    public static MemoryStream ProcessPhotoForTelegram(MemoryStream imageStream)
    {
        if (imageStream == null || imageStream.Length == 0)
        {
            return imageStream;
        }

        imageStream.Seek(0, SeekOrigin.Begin);

        using var skData = SKData.Create(imageStream);
        if (skData == null)
        {
            imageStream.Seek(0, SeekOrigin.Begin);
            return imageStream;
        }

        using var codec = SKCodec.Create(skData);
        if (codec == null)
        {
            imageStream.Seek(0, SeekOrigin.Begin);
            return imageStream;
        }

        var (_, _, needsResize) = CalculateTargetDimensions(codec.Info.Width, codec.Info.Height);
        if (!needsResize && imageStream.Length <= MaxFileSizeBytes)
        {
            imageStream.Seek(0, SeekOrigin.Begin);
            return imageStream;
        }

        var format = codec.EncodedFormat;
        var origin = codec.EncodedOrigin;
        var workingBitmap = SKBitmap.Decode(codec);
        if (workingBitmap == null)
        {
            imageStream.Seek(0, SeekOrigin.Begin);
            return imageStream;
        }

        var workingStream = imageStream;
        try
        {
            if (origin != SKEncodedOrigin.TopLeft)
            {
                var orientedBitmap = ApplyExifOrigin(workingBitmap, origin);
                if (!ReferenceEquals(orientedBitmap, workingBitmap))
                {
                    workingBitmap.Dispose();
                    workingBitmap = orientedBitmap;
                }
            }

            var (targetWidth, targetHeight, shouldResize) = CalculateTargetDimensions(workingBitmap.Width, workingBitmap.Height);
            if (shouldResize)
            {
                var targetInfo = new SKImageInfo(targetWidth, targetHeight, workingBitmap.ColorType, workingBitmap.AlphaType);
                var resizedBitmap = workingBitmap.Resize(targetInfo, DownscaleSampling);
                if (resizedBitmap != null)
                {
                    workingBitmap.Dispose();
                    workingBitmap = resizedBitmap;
                }

                var resizeFormat = GetResizeOutputFormat(format);
                using var encodedData = EncodeBitmap(workingBitmap, resizeFormat, quality: 90);
                if (encodedData != null)
                {
                    var resizedStream = new MemoryStream();
                    encodedData.SaveTo(resizedStream);
                    if (!ReferenceEquals(workingStream, imageStream))
                    {
                        workingStream.Dispose();
                    }

                    workingStream = resizedStream;
                }
            }

            if (workingStream.Length > MaxFileSizeBytes)
            {
                var compressionFormat = GetCompressionOutputFormat(format);
                int quality = 80;
                double scale = 1.0;
                bool sizeFits = false;

                while (!sizeFits && scale >= 0.05)
                {
                    SKBitmap scaledBitmap = null;
                    SKData encodedData = null;
                    try
                    {
                        var targetBitmap = workingBitmap;
                        if (scale < 1.0)
                        {
                            int scaledWidth = Math.Max(1, (int)Math.Floor(workingBitmap.Width * scale));
                            int scaledHeight = Math.Max(1, (int)Math.Floor(workingBitmap.Height * scale));
                            var scaledInfo = new SKImageInfo(scaledWidth, scaledHeight, workingBitmap.ColorType, workingBitmap.AlphaType);
                            scaledBitmap = workingBitmap.Resize(scaledInfo, DownscaleSampling);
                            if (scaledBitmap != null)
                            {
                                targetBitmap = scaledBitmap;
                            }
                        }

                        encodedData = EncodeBitmap(targetBitmap, compressionFormat, quality);
                        if (encodedData != null && encodedData.Size <= MaxFileSizeBytes)
                        {
                            var compressedStream = new MemoryStream();
                            encodedData.SaveTo(compressedStream);
                            if (!ReferenceEquals(workingStream, imageStream))
                            {
                                workingStream.Dispose();
                            }

                            workingStream = compressedStream;
                            sizeFits = true;
                        }
                        else
                        {
                            // Step 1: Reduce quality
                            if (quality > 50)
                            {
                                quality -= 15;
                            }
                            // Step 2: Scale down dimensions
                            else
                            {
                                scale *= 0.8;
                                quality = 75; // reset quality for smaller dimensions
                            }
                        }
                    }
                    finally
                    {
                        encodedData?.Dispose();
                        scaledBitmap?.Dispose();
                    }
                }
            }

            if (!ReferenceEquals(workingStream, imageStream))
            {
                imageStream.Dispose();
            }

            workingStream.Seek(0, SeekOrigin.Begin);
            return workingStream;
        }
        catch
        {
            if (!ReferenceEquals(workingStream, imageStream))
            {
                workingStream.Dispose();
            }

            throw;
        }
        finally
        {
            workingBitmap?.Dispose();
        }
    }

    private static SKEncodedImageFormat GetResizeOutputFormat(SKEncodedImageFormat format)
    {
        return format is SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp
            ? format
            : SKEncodedImageFormat.Jpeg;
    }

    private static SKEncodedImageFormat GetCompressionOutputFormat(SKEncodedImageFormat format)
    {
        // Save JPEGs and WebPs in their native format; fallback other formats (like high-size PNGs) to JPEG
        return format is SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp
            ? format
            : SKEncodedImageFormat.Jpeg;
    }

    private static SKData EncodeBitmap(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var skImage = SKImage.FromBitmap(bitmap);
        return skImage?.Encode(format, quality);
    }

    private static SKBitmap ApplyExifOrigin(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
        {
            return source;
        }

        int width = source.Width;
        int height = source.Height;
        bool swapsAxes = origin is SKEncodedOrigin.LeftTop
            or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom
            or SKEncodedOrigin.LeftBottom;

        int destWidth = swapsAxes ? height : width;
        int destHeight = swapsAxes ? width : height;

        var rotated = new SKBitmap(new SKImageInfo(destWidth, destHeight));
        using var canvas = new SKCanvas(rotated);
        canvas.Clear(SKColors.Transparent);

        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(width, height);
                canvas.Scale(-1, -1);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(-90);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(height, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(height, width);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, width);
                canvas.RotateDegrees(-90);
                break;
        }

        canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
        canvas.Flush();
        return rotated;
    }
}
