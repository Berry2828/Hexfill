// Loaded only into a disposable CAD Core process to verify program-scoped blocks.
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Autodesk.AutoCAD.Runtime;
using Application=Autodesk.AutoCAD.ApplicationServices.Application;
public class OfflineNetworkChecks
{
    [CommandMethod("HNETOFFTEST")]
    public void Test()
    {
        var editor=Application.DocumentManager.MdiActiveDocument.Editor;
        string result=editor.GetString("Result: ").StringResult;
        int port4=int.Parse(editor.GetString("IPv4 port: ").StringResult);
        int port6=int.Parse(editor.GetString("IPv6 port: ").StringResult);
        using(var log=new StreamWriter(result))
        {
            Probe(log,"localhost-proxy-v4","127.0.0.1",port4);
            Probe(log,"localhost-proxy-v6","::1",port6);
            Probe(log,"remote-v4","192.0.2.1",443);
            Probe(log,"remote-v6","2001:db8::1",443);
        }
    }
    private static void Probe(StreamWriter log,string name,string address,int port)
    {
        var ip=IPAddress.Parse(address);
        using(var socket=new Socket(ip.AddressFamily,SocketType.Stream,ProtocolType.Tcp))
        {
            try
            {
                var pending=socket.BeginConnect(new IPEndPoint(ip,port),null,null);
                if(!pending.AsyncWaitHandle.WaitOne(3000)) { log.WriteLine("FAIL "+name+" timeout (does not prove blocking)"); return; }
                socket.EndConnect(pending);
                log.WriteLine("FAIL "+name+" connection succeeded");
            }
            catch(SocketException ex) { log.WriteLine((ex.SocketErrorCode==SocketError.AccessDenied?"PASS ":"FAIL ")+name+" error="+ex.NativeErrorCode); }
        }
    }
}
