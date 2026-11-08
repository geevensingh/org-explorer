using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace OrgExplorer.Workflow;

[SupportedOSPlatform("windows")]
internal static class WindowsImageDecoder
{
    internal static (int Width, int Height) Decode(byte[] bytes, bool png)
    {
        var factory = (ImagingFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(
            new Guid("cacaf262-9370-4615-a13b-9f5539da4c0a"), true)!)!;
        IStream? stream = null;
        BitmapDecoder? decoder = null;
        BitmapFrame? frame = null;
        try
        {
            stream = SHCreateMemStream(bytes, (uint)bytes.Length)
                ?? throw new OutOfMemoryException("Image stream allocation failed.");
            factory.CreateDecoderFromStream(stream, IntPtr.Zero, 1, out decoder);
            decoder.GetContainerFormat(out var container);
            var expected = new Guid(png ? "1b7cfaf4-713f-473c-bbcd-6137425faeaf" : "19e4a5aa-5662-4fc5-a0c0-1758028e1057");
            if (container != expected) throw new ArgumentException("Image content is not the supplied JPEG/PNG format.");
            decoder.GetFrame(0, out frame);
            frame.GetSize(out var width, out var height);
            if (width == 0 || height == 0) throw new ArgumentException("Image has no pixels.");
            // Decode every row; reading a header alone misses corrupt/truncated pixel data.
            var stride = checked(width * 16);
            var row = new byte[checked((int)stride)];
            for (uint y = 0; y < height; y++)
            {
                var rectangle = new Rectangle { X = 0, Y = checked((int)y), Width = checked((int)width), Height = 1 };
                frame.CopyPixels(ref rectangle, stride, stride, row);
            }
            return (checked((int)width), checked((int)height));
        }
        finally
        {
            if (frame is not null) Marshal.FinalReleaseComObject(frame);
            if (decoder is not null) Marshal.FinalReleaseComObject(decoder);
            if (stream is not null) Marshal.FinalReleaseComObject(stream);
            Marshal.FinalReleaseComObject(factory);
        }
    }

    [DllImport("shlwapi.dll")]
    [return: MarshalAs(UnmanagedType.Interface)]
    private static extern IStream? SHCreateMemStream(byte[] bytes, uint length);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle { public int X, Y, Width, Height; }

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ImagingFactory
    {
        void CreateDecoderFromFilename();
        void CreateDecoderFromStream(IStream stream, IntPtr vendor, uint options, out BitmapDecoder decoder);
    }

    [ComImport, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface BitmapDecoder
    {
        void QueryCapability();
        void Initialize();
        void GetContainerFormat(out Guid container);
        void GetDecoderInfo();
        void CopyPalette();
        void GetMetadataQueryReader();
        void GetPreview();
        void GetColorContexts();
        void GetThumbnail();
        void GetFrameCount();
        void GetFrame(uint index, out BitmapFrame frame);
    }

    [ComImport, Guid("3b16811b-6a43-4ec9-a813-3d930c13b940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface BitmapFrame
    {
        void GetSize(out uint width, out uint height);
        void GetPixelFormat();
        void GetResolution();
        void CopyPalette();
        void CopyPixels(ref Rectangle rectangle, uint stride, uint length, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] pixels);
    }
}
