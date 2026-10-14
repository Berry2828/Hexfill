// Application-scoped persistent WFP filters, including localhost proxy connections.
// Uses only the Windows built-in filtering engine; no driver or background service.
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

public static class AutoCadOffline
{
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct Display { [MarshalAs(UnmanagedType.LPWStr)] public string name; [MarshalAs(UnmanagedType.LPWStr)] public string description; }
    [StructLayout(LayoutKind.Sequential)] struct Blob { public uint size; public IntPtr data; }
    [StructLayout(LayoutKind.Explicit, Size=16)] struct Value { [FieldOffset(0)] public uint type; [FieldOffset(8)] public IntPtr pointer; [FieldOffset(8)] public byte uint8; }
    [StructLayout(LayoutKind.Sequential)] struct SubLayer { public Guid key; public Display display; public uint flags; public IntPtr provider; public Blob data; public ushort weight; }
    [StructLayout(LayoutKind.Sequential)] struct Condition { public Guid key; public uint match; public Value value; }
    [StructLayout(LayoutKind.Sequential)] struct Action { public uint type; public Guid key; }
    [StructLayout(LayoutKind.Explicit, Size=16)] struct Context { [FieldOffset(0)] public ulong raw; [FieldOffset(0)] public Guid provider; }
    [StructLayout(LayoutKind.Sequential)] struct Filter
    {
        public Guid key; public Display display; public uint flags; public IntPtr provider; public Blob data;
        public Guid layer; public Guid sublayer; public Value weight; public uint conditionCount;
        public IntPtr conditions; public Action action; public Context context; public IntPtr reserved;
        public ulong id; public Value effectiveWeight;
    }
    [DllImport("fwpuclnt.dll", CharSet=CharSet.Unicode)] static extern uint FwpmEngineOpen0(string server, uint auth, IntPtr identity, IntPtr session, out IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmEngineClose0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmTransactionBegin0(IntPtr engine, uint flags);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmTransactionCommit0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmTransactionAbort0(IntPtr engine);
    [DllImport("fwpuclnt.dll", CharSet=CharSet.Unicode)] static extern uint FwpmSubLayerAdd0(IntPtr engine, ref SubLayer layer, IntPtr descriptor);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmSubLayerDeleteByKey0(IntPtr engine, ref Guid key);
    [DllImport("fwpuclnt.dll", CharSet=CharSet.Unicode)] static extern uint FwpmGetAppIdFromFileName0(string path, out IntPtr appId);
    [DllImport("fwpuclnt.dll", CharSet=CharSet.Unicode)] static extern uint FwpmFilterAdd0(IntPtr engine, ref Filter filter, IntPtr descriptor, out ulong id);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmFilterDeleteByKey0(IntPtr engine, ref Guid key);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmFilterGetByKey0(IntPtr engine, ref Guid key, out IntPtr filter);
    [DllImport("fwpuclnt.dll")] static extern void FwpmFreeMemory0(ref IntPtr memory);

    static readonly Guid SubLayerKey = new Guid("636e8fdd-e616-49cf-baba-41c8ea19bc85");
    static readonly Guid AppIdCondition = new Guid("d78e1e87-8644-4ea5-9437-d809ecefc971");
    static readonly Guid[] Layers = {
        new Guid("c38d57d1-05a7-4c33-904f-7fbceee60e82"), // ALE_AUTH_CONNECT_V4
        new Guid("4a72393b-319f-44bc-84c3-ba54dcb3b6b4"), // ALE_AUTH_CONNECT_V6
        new Guid("e1cd9fe7-f4b5-4273-96c0-592e487b8650"), // ALE_AUTH_RECV_ACCEPT_V4
        new Guid("a3b42c97-9f04-4672-b87e-cee9c483257f")  // ALE_AUTH_RECV_ACCEPT_V6
    };
    static Guid Key(string path, Guid layer)
    {
        using(var hash=MD5.Create()) return new Guid(hash.ComputeHash(Encoding.Unicode.GetBytes("AutoCAD Offline v1|"+path.ToLowerInvariant()+"|"+layer)));
    }
    static void Check(uint status, string operation) { if(status!=0) throw new Win32Exception((int)status, operation+" failed: 0x"+status.ToString("X8")); }
    static IntPtr Open()
    {
        if(IntPtr.Size!=8 || Marshal.SizeOf(typeof(Filter))!=200 || Marshal.SizeOf(typeof(Condition))!=40 || Marshal.SizeOf(typeof(SubLayer))!=72)
            throw new InvalidOperationException("WFP ABI mismatch: requires x64 Windows");
        IntPtr engine; Check(FwpmEngineOpen0(null,10,IntPtr.Zero,IntPtr.Zero,out engine),"FwpmEngineOpen"); return engine;
    }
    public static int Install(string[] paths)
    {
        IntPtr engine=Open(); bool transaction=false;
        try
        {
            Check(FwpmTransactionBegin0(engine,0),"Begin"); transaction=true;
            var sublayer=new SubLayer{key=SubLayerKey,display=new Display{name="AutoCAD Offline (including local proxy)",description="User-requested application-only offline policy"},flags=1,weight=65535};
            uint status=FwpmSubLayerAdd0(engine,ref sublayer,IntPtr.Zero);
            if(status!=0x80320009) Check(status,"Add sublayer"); // FWP_E_ALREADY_EXISTS
            int count=0;
            foreach(string path in paths)
            {
                IntPtr blob; Check(FwpmGetAppIdFromFileName0(path,out blob),"Get app ID: "+path);
                IntPtr conditions=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Condition)));
                try
                {
                    var condition=new Condition{key=AppIdCondition,match=0,value=new Value{type=12,pointer=blob}};
                    Marshal.StructureToPtr(condition,conditions,false);
                    foreach(Guid layer in Layers)
                    {
                        Guid key=Key(path,layer); uint deleted=FwpmFilterDeleteByKey0(engine,ref key);
                        if(deleted!=0x80320003) Check(deleted,"Replace previous filter");
                        var filter=new Filter{key=key,display=new Display{name="AutoCAD Offline: "+System.IO.Path.GetFileName(path),description=path},
                            flags=1,layer=layer,sublayer=SubLayerKey,weight=new Value{type=1,uint8=15},conditionCount=1,conditions=conditions,
                            action=new Action{type=0x1001}}; // FWP_ACTION_BLOCK
                        ulong id; Check(FwpmFilterAdd0(engine,ref filter,IntPtr.Zero,out id),"Add app filter"); count++;
                    }
                }
                finally { Marshal.FreeHGlobal(conditions); FwpmFreeMemory0(ref blob); }
            }
            Check(FwpmTransactionCommit0(engine),"Commit"); transaction=false; return count;
        }
        finally { if(transaction) FwpmTransactionAbort0(engine); FwpmEngineClose0(engine); }
    }
    public static int Verify(string[] paths)
    {
        IntPtr engine=Open(); int count=0;
        try
        {
            foreach(string path in paths) foreach(Guid layer in Layers)
            {
                Guid key=Key(path,layer); IntPtr memory;
                Check(FwpmFilterGetByKey0(engine,ref key,out memory),"Read persisted filter");
                try
                {
                    var filter=(Filter)Marshal.PtrToStructure(memory,typeof(Filter));
                    var condition=(Condition)Marshal.PtrToStructure(filter.conditions,typeof(Condition));
                    var actual=(Blob)Marshal.PtrToStructure(condition.value.pointer,typeof(Blob));
                    IntPtr expectedMemory; Check(FwpmGetAppIdFromFileName0(path,out expectedMemory),"Read expected app ID");
                    try
                    {
                        var expected=(Blob)Marshal.PtrToStructure(expectedMemory,typeof(Blob));
                        byte[] a=new byte[actual.size],b=new byte[expected.size]; Marshal.Copy(actual.data,a,0,a.Length); Marshal.Copy(expected.data,b,0,b.Length);
                        if(filter.action.type!=0x1001 || (filter.flags&1)==0 || filter.layer!=layer || filter.sublayer!=SubLayerKey
                            || filter.conditionCount!=1 || condition.key!=AppIdCondition || condition.match!=0
                            || Convert.ToBase64String(a)!=Convert.ToBase64String(b)) throw new InvalidOperationException("Unexpected filter: "+path);
                    }
                    finally { FwpmFreeMemory0(ref expectedMemory); }
                    count++;
                }
                finally { FwpmFreeMemory0(ref memory); }
            }
            return count;
        }
        finally { FwpmEngineClose0(engine); }
    }
    public static void Remove(string[] paths)
    {
        IntPtr engine=Open(); bool transaction=false;
        try
        {
            Check(FwpmTransactionBegin0(engine,0),"Begin remove"); transaction=true;
            foreach(string path in paths) foreach(Guid layer in Layers)
            {
                Guid key=Key(path,layer); uint status=FwpmFilterDeleteByKey0(engine,ref key);
                if(status!=0x80320003) Check(status,"Remove owned filter");
            }
            Guid sublayer=SubLayerKey; uint removed=FwpmSubLayerDeleteByKey0(engine,ref sublayer);
            if(removed!=0x80320007) Check(removed,"Remove owned sublayer");
            Check(FwpmTransactionCommit0(engine),"Commit remove"); transaction=false;
        }
        finally { if(transaction) FwpmTransactionAbort0(engine); FwpmEngineClose0(engine); }
    }
}
