using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

// WASAPI shared-mode capture; loopback records the default output device.
sealed class AudioCapture : IDisposable {
    readonly ManualResetEvent ready = new ManualResetEvent(false);
    readonly ManualResetEvent stop = new ManualResetEvent(false);
    readonly Thread worker;
    Exception error;
    public AudioCapture(string path, bool loopback) {
        worker = new Thread(delegate() { Run(path, loopback); });
        worker.IsBackground=true; worker.SetApartmentState(ApartmentState.MTA); worker.Start();
        if(!ready.WaitOne(5000)) { stop.Set(); throw new Exception("音频设备启动超时。"); }
        if(error!=null) { Dispose(); throw new Exception((loopback ? "电脑声音" : "麦克风")+"不可用："+error.Message); }
    }
    static void Check(int hr) { Marshal.ThrowExceptionForHR(hr); }
    void Run(string path, bool loopback) {
        IMMDeviceEnumerator enumerator=null; IMMDevice device=null;
        IAudioClient client=null; IAudioCaptureClient capture=null;
        IntPtr format=IntPtr.Zero;
        bool running=false;
        try {
            enumerator=(IMMDeviceEnumerator)new MMDeviceEnumerator();
            Check(enumerator.GetDefaultAudioEndpoint(loopback ? 0 : 1, 0, out device));
            object obj; Guid iid=typeof(IAudioClient).GUID;
            Check(device.Activate(ref iid,23,IntPtr.Zero,out obj)); client=(IAudioClient)obj;
            Check(client.GetMixFormat(out format));
            int formatSize=18+(ushort)Marshal.ReadInt16(format,16);
            byte[] fmt=new byte[formatSize]; Marshal.Copy(format,fmt,0,fmt.Length);
            int rate=Marshal.ReadInt32(format,4), alignment=(ushort)Marshal.ReadInt16(format,12);
            Check(client.Initialize(0,loopback ? 0x20000u : 0u,10000000,0,format,IntPtr.Zero));
            iid=typeof(IAudioCaptureClient).GUID; Check(client.GetService(ref iid,out obj)); capture=(IAudioCaptureClient)obj;
            using(var stream=new FileStream(path,FileMode.Create,FileAccess.ReadWrite,FileShare.Read))
            using(var writer=new BinaryWriter(stream)) {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(0);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(fmt.Length); writer.Write(fmt);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); long dataSizePosition=stream.Position; writer.Write(0);
                long dataStart=stream.Position, framesWritten=0;
                byte[] silence=new byte[16384];
                Action<long> pad=delegate(long target) {
                    long bytes=(target-framesWritten)*alignment;
                    while(bytes>0) { int count=(int)Math.Min(bytes,silence.Length); writer.Write(silence,0,count); bytes-=count; }
                    if(target>framesWritten) framesWritten=target;
                };
                long qpcStart=Stopwatch.GetTimestamp();
                Check(client.Start()); running=true; ready.Set();
                do {
                    uint packets; Check(capture.GetNextPacketSize(out packets));
                    while(packets>0) {
                        IntPtr buffer; uint frames,flags; ulong devicePosition,qpc;
                        Check(capture.GetBuffer(out buffer,out frames,out flags,out devicePosition,out qpc));
                        try {
                            // QPC timestamps use 100 ns units. Preserve gaps when no app is playing sound.
                            double packetSeconds=qpc/10000000.0-qpcStart/(double)Stopwatch.Frequency;
                            if((flags & 4)==0 && packetSeconds>=0) pad((long)(packetSeconds*rate));
                            int bytes=checked((int)frames*alignment);
                            if((flags&2)!=0) { long target=framesWritten+frames; pad(target); }
                            else { byte[] data=new byte[bytes]; Marshal.Copy(buffer,data,0,bytes); writer.Write(data); framesWritten+=frames; }
                        } finally { Check(capture.ReleaseBuffer(frames)); }
                        Check(capture.GetNextPacketSize(out packets));
                    }
                } while(!stop.WaitOne(10));
                Check(client.Stop()); running=false;
                pad((long)((Stopwatch.GetTimestamp()-qpcStart)/(double)Stopwatch.Frequency*rate));
                long dataLength=stream.Position-dataStart;
                if(dataLength>uint.MaxValue-100) throw new Exception("音轨超过 WAV 文件大小限制，请分段录制。");
                stream.Position=dataSizePosition; writer.Write((uint)dataLength);
                stream.Position=4; writer.Write((uint)(stream.Length-8));
            }
        } catch(Exception ex) { error=ex; ready.Set(); }
        finally {
            if(running && client!=null) client.Stop();
            if(format!=IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            if(capture!=null) Marshal.ReleaseComObject(capture);
            if(client!=null) Marshal.ReleaseComObject(client);
            if(device!=null) Marshal.ReleaseComObject(device);
            if(enumerator!=null) Marshal.ReleaseComObject(enumerator);
        }
    }
    public void Finish() {
        stop.Set();
        if(!worker.Join(5000)) throw new Exception("音频设备未能停止，临时文件已保留。");
        if(error!=null) throw new Exception("音频录制失败："+error.Message);
    }
    public void Dispose() { stop.Set(); if(worker.Join(5000)) { ready.Dispose(); stop.Dispose(); } }
}

[ComImport,Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }
[ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator {
    [PreserveSig] int EnumAudioEndpoints(int flow,uint mask,out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int flow,int role,out IMMDevice device);
}
[ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice {
    [PreserveSig] int Activate(ref Guid iid,uint context,IntPtr activation,[MarshalAs(UnmanagedType.IUnknown)] out object obj);
}
[ComImport,Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient {
    [PreserveSig] int Initialize(int mode,uint flags,long duration,long periodicity,IntPtr format,IntPtr session);
    [PreserveSig] int GetBufferSize(out uint size);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int mode,IntPtr format,out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long normal,out long minimum);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid,[MarshalAs(UnmanagedType.IUnknown)] out object service);
}
[ComImport,Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient {
    [PreserveSig] int GetBuffer(out IntPtr data,out uint frames,out uint flags,out ulong position,out ulong qpc);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}
