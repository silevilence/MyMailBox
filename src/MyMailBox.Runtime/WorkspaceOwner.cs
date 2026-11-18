using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace MyMailBox.Runtime;

public sealed class WorkspaceOwner : IDisposable
{
    private readonly FileStream _lease;
    private readonly CancellationTokenSource _stop=new();
    private readonly Task _listen;
    private readonly object _gate=new();
    private Action? _restore;private bool _pending;
    private WorkspaceOwner(FileStream file,string pipe)
    {
        _lease=file;_listen=Task.Run(async()=>
        {
            try
            {
                while(!_stop.IsCancellationRequested)
                {
                    using var server=new NamedPipeServerStream(pipe,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_stop.Token);var value=new byte[1];
                    if(await server.ReadAsync(value,_stop.Token)==1 && value[0]==1)
                    {
                        Action? callback;lock(_gate){callback=_restore;_pending=callback is null;}
                        callback?.Invoke();
                    }
                }
            }
            catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
        });
    }
    public static WorkspaceOwner? Open(string directory,bool activate=true)
    {
        var path=Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));Directory.CreateDirectory(path);
        var key="MyMailBox-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..32];
        try{return new(new FileStream(Path.Combine(path,"desktop.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None),key);}
        catch(IOException e)when((e.HResult&0xffff)is 32 or 33)
        {
            if(activate)
            {
                using var client=new NamedPipeClientStream(".",key,PipeDirection.Out,PipeOptions.CurrentUserOnly);
                try{client.Connect(1500);client.WriteByte(1);client.Flush();}catch(TimeoutException){}catch(IOException){}
            }
            return null;
        }
    }
    public void OnRestore(Action callback)
    {
        bool pending;lock(_gate){_restore=callback;pending=_pending;_pending=false;}if(pending)callback();
    }
    public void Dispose(){_stop.Cancel();try{_listen.GetAwaiter().GetResult();}finally{_lease.Dispose();_stop.Dispose();}}
}
