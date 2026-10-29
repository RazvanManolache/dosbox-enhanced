using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace DosboxEnhanced;

public static class ScreenText
{
    public static async Task<object> Read(VideoFrame frame, CancellationToken token, string colorFilter="", int left=0, int top=0, int cropWidth=0, int cropHeight=0)
    {
        colorFilter=colorFilter.Trim().ToLowerInvariant();
        if(colorFilter is not ("" or "none" or "yellow" or "white"))throw new ArgumentException("OCR color_filter must be none, yellow or white.");
        if(cropWidth==0)cropWidth=frame.Width;if(cropHeight==0)cropHeight=frame.Height;
        if(left<0||top<0||cropWidth<1||cropHeight<1||left+cropWidth>frame.Width||top+cropHeight>frame.Height)throw new ArgumentException("OCR crop must fit inside the native DOS frame.");
        var engine = OcrEngine.TryCreateFromLanguage(new Language("en-US")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null) throw new InvalidOperationException("Windows has no installed OCR language. Use dosbox_screenshot.");
        int factor = Math.Clamp(1280 / cropWidth, 1, colorFilter is "yellow" or "white" ? 8 : 4);
        if (cropWidth * factor > OcrEngine.MaxImageDimension || cropHeight * factor > OcrEngine.MaxImageDimension) factor = 1;
        int width = cropWidth * factor, height = cropHeight * factor;
        if (width > OcrEngine.MaxImageDimension || height > OcrEngine.MaxImageDimension) throw new InvalidOperationException("This screen exceeds Windows OCR size limits; use dosbox_screenshot.");
        var pixels = new byte[width*height*4];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            int source=((top+y/factor)*frame.Width+left+x/factor)*4,target=(y*width+x)*4;
            if(colorFilter is "yellow" or "white")
            {
                int b=frame.Pixels[source],g=frame.Pixels[source+1],r=frame.Pixels[source+2];
                bool ink=colorFilter=="yellow" ? r>160&&g>140&&b<150&&r-b>50 : r>170&&g>170&&b>170;
                pixels[target]=pixels[target+1]=pixels[target+2]=(byte)(ink?0:255);pixels[target+3]=255;
            }
            else System.Buffer.BlockCopy(frame.Pixels,source,pixels,target,4);
        }
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8,width,height,BitmapAlphaMode.Ignore);
        using(var writer = new DataWriter()) { writer.WriteBytes(pixels);bitmap.CopyFromBuffer(writer.DetachBuffer()); }
        var result = await engine.RecognizeAsync(bitmap).AsTask(token);
        return new { source="windows_ocr", text=result.Text, lines=result.Lines.Select(l=>l.Text).ToArray(), frame=frame.Sequence, colorFilter, region=new{left,top,width=cropWidth,height=cropHeight}, note="OCR can misread DOS pixel fonts. Verify uncertain text against dosbox_screenshot." };
    }
}
