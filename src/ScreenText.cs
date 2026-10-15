using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace DosboxEnhanced;

public static class ScreenText
{
    public static async Task<object> Read(VideoFrame frame, CancellationToken token)
    {
        var engine = OcrEngine.TryCreateFromLanguage(new Language("en-US")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null) throw new InvalidOperationException("Windows has no installed OCR language. Use dosbox_screenshot.");
        int factor = Math.Clamp(1280 / frame.Width, 1, 4);
        if (frame.Width * factor > OcrEngine.MaxImageDimension || frame.Height * factor > OcrEngine.MaxImageDimension) factor = 1;
        int width = frame.Width * factor, height = frame.Height * factor;
        if (width > OcrEngine.MaxImageDimension || height > OcrEngine.MaxImageDimension) throw new InvalidOperationException("This screen exceeds Windows OCR size limits; use dosbox_screenshot.");
        var pixels = new byte[width*height*4];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)System.Buffer.BlockCopy(frame.Pixels,((y/factor)*frame.Width+x/factor)*4,pixels,(y*width+x)*4,4);
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8,width,height,BitmapAlphaMode.Ignore);
        using(var writer = new DataWriter()) { writer.WriteBytes(pixels);bitmap.CopyFromBuffer(writer.DetachBuffer()); }
        var result = await engine.RecognizeAsync(bitmap).AsTask(token);
        return new { source="windows_ocr", text=result.Text, lines=result.Lines.Select(l=>l.Text).ToArray(), frame=frame.Sequence, note="OCR can misread DOS pixel fonts. Verify uncertain text against dosbox_screenshot." };
    }
}
