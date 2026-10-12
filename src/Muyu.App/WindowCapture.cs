using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Storage;
using WinRT.Interop;
namespace Muyu.App;

/// <summary>Diagnostic-only capture of this app's window; unused by the material renderer.</summary>
internal static class WindowCapture
{
    internal static async Task Save(Microsoft.UI.Xaml.Window window,string path,int minimumContrast=30)
    {
        var handle=WindowNative.GetWindowHandle(window);GetWindowRect(handle,out var bounds);
        int width=bounds.Right-bounds.Left,height=bounds.Bottom-bounds.Top;
        var screen=GetDC(IntPtr.Zero);var dc=CreateCompatibleDC(screen);var bitmap=CreateCompatibleBitmap(screen,width,height);var previous=SelectObject(dc,bitmap);
        try
        {
            var info=new BitmapInfo{Size=40,Width=width,Height=-height,Planes=1,Bits=32};
            var pixels=new byte[checked(width*height*4)];
            for(int attempt=0;attempt<40;attempt++)
            {
                DwmFlush();
                if(!PrintWindow(handle,dc,2))throw new IOException("无法截取播放器窗口");
                if(GetDIBits(dc,bitmap,0,(uint)height,pixels,ref info,0)==0)throw new IOException("无法读取窗口截图");
                // A legitimate graphite scene can be #060606. Reject empty black frames,
                // without rejecting the dark monochrome artwork case itself.
                int visible=0,samples=0,minimum=765,maximum=0;
                for(int i=0;i<pixels.Length;i+=128)
                {
                    int level=pixels[i]+pixels[i+1]+pixels[i+2];samples++;if(level>6)visible++;
                    int column=(i/4)%width;
                    if(column>32&&column<width-32&&i>pixels.Length/4&&i<pixels.Length*3/4){minimum=Math.Min(minimum,level);maximum=Math.Max(maximum,level);}
                }
                // Initial shader compilation can leave an all-white client with only native
                // caption buttons. That is an empty frame just like an all-black client.
                if(visible>samples/20&&maximum-minimum>minimumContrast)break;
                if(attempt==39)throw new IOException("窗口尚未呈现有效 GPU 内容，截图未通过空帧检查");
                await Task.Delay(200);
            }
            for(int i=3;i<pixels.Length;i+=4)pixels[i]=255;
            var folder=await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
            var output=await folder.CreateFileAsync(Path.GetFileName(path),CreationCollisionOption.ReplaceExisting);
            using var stream=await output.OpenAsync(FileAccessMode.ReadWrite);
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Ignore,(uint)width,(uint)height,96,96,pixels);await encoder.FlushAsync();
        }
        finally{SelectObject(dc,previous);DeleteObject(bitmap);DeleteDC(dc);ReleaseDC(IntPtr.Zero,screen);}
    }
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct BitmapInfo{public uint Size;public int Width,Height;public ushort Planes,Bits;public uint Compression,ImageSize;public int X,Y;public uint Colors,Important;}
    [DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr window,out Rect bounds);
    [DllImport("user32.dll")]private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]private static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("gdi32.dll")]private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]private static extern IntPtr CreateCompatibleBitmap(IntPtr dc,int width,int height);
    [DllImport("gdi32.dll")]private static extern IntPtr SelectObject(IntPtr dc,IntPtr value);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")]private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")]private static extern bool PrintWindow(IntPtr window,IntPtr dc,uint flags);
    [DllImport("gdi32.dll")]private static extern int GetDIBits(IntPtr dc,IntPtr bitmap,uint start,uint lines,[Out]byte[] pixels,ref BitmapInfo info,uint usage);
    [DllImport("dwmapi.dll")]private static extern int DwmFlush();
}
