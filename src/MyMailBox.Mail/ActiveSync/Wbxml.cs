using System.Text;

namespace MyMailBox.Mail.ActiveSync;

// MS-ASWBXML code pages. High byte is the page, low byte is its token.
internal enum Tag
{
    Sync=0x05, Responses=0x06, Add=0x07, Change=0x08, Delete=0x09, SyncKey=0x0b, SyncClientId=0x0c, ServerId=0x0d, Status=0x0e,
    Collection=0x0f, CollectionId=0x12, GetChanges=0x13, MoreAvailable=0x14, WindowSize=0x15, Commands=0x16,
    Options=0x17, Collections=0x1c, ApplicationData=0x1d, SoftDelete=0x21, MIMESupport=0x22,
    DateReceived=0x020f, Subject=0x0214, Read=0x0215, To=0x0216, Cc=0x0217, From=0x0218, Bcc=0x1616,
    MoveItems=0x0505, Move=0x0506, SrcMsgId=0x0507, SrcFldId=0x0508, DstFldId=0x0509, MoveResponse=0x050a,
    MoveStatus=0x050b, DstMsgId=0x050c,
    FolderName=0x0707, FolderId=0x0708, ParentId=0x0709, FolderType=0x070a, FolderStatus=0x070c, FolderChanges=0x070e,
    FolderAdd=0x070f, FolderDelete=0x0710, FolderUpdate=0x0711, FolderKey=0x0712, FolderSync=0x0716,
    BodyPreference=0x1105, BodyType=0x1106, TruncationSize=0x1107, Body=0x110a, BodyData=0x110b, Truncated=0x110d,
    Attachments=0x110e, DisplayName=0x1110, Method=0x1112, ContentId=0x1113, IsInline=0x1115, ContentType=0x1117,
    AddAttachment=0x111c, AttachmentClientId=0x111e, Content=0x111f,
    ItemOperations=0x1405, FetchItem=0x1406, Store=0x1407, FetchOptions=0x1408, Properties=0x140b,
    ItemStatus=0x140d, ItemResponse=0x140e,
    SendMail=0x1505, SaveInSentItems=0x1508, Mime=0x1510, ClientId=0x1511, SendStatus=0x1512
}

internal sealed class EasNode(Tag tag)
{
    internal Tag Tag {get;}=tag;
    internal List<EasNode> Children {get;}=[];
    internal byte[]? Data {get;set;}
    internal bool Opaque {get;set;}
    internal EasNode? Child(Tag name)=>Children.SingleOrDefault(c=>c.Tag==name);
    internal EasNode Need(Tag name)=>Child(name) ?? throw new InvalidDataException("Exchange 响应缺少 "+name+"。");
    internal string Text=>Data==null?"":Encoding.UTF8.GetString(Data);
    internal string Value(Tag name)=>Need(name).Text;
    internal static EasNode Element(Tag tag,params EasNode[] children){var n=new EasNode(tag);n.Children.AddRange(children);return n;}
    internal static EasNode Value(Tag tag,string value)=>new(tag){Data=Encoding.UTF8.GetBytes(value)};
    internal static EasNode Binary(Tag tag,byte[] data)=>new(tag){Data=data,Opaque=true};
}

internal static class Wbxml
{
    private static readonly byte[] Header=[3,1,106,0];
    internal static byte[] Write(EasNode root)
    {
        using var output=new MemoryStream(); output.Write(Header);int page=0; WriteNode(root);
        return output.ToArray();
        void WriteNode(EasNode n)
        {
            int next=(int)n.Tag>>8;
            if(page!=next){output.WriteByte(0);output.WriteByte((byte)next);page=next;}
            bool content=n.Data!=null || n.Children.Count!=0;
            output.WriteByte((byte)(((int)n.Tag & 63) | (content?64:0)));
            if(!content)return;
            if(n.Data is {} data)
            {
                if(n.Opaque){output.WriteByte(0xc3);WriteNumber(output,data.Length);output.Write(data);}
                else {output.WriteByte(3);output.Write(data);output.WriteByte(0);}
            }
            foreach(var c in n.Children)WriteNode(c);
            output.WriteByte(1);
        }
    }
    private static void WriteNumber(Stream output,int value)
    {
        Span<byte> bytes=stackalloc byte[5];int pos=4;bytes[pos]=(byte)(value&127);
        while((value>>=7)!=0)bytes[--pos]=(byte)(128 | (value&127));
        output.Write(bytes[pos..]);
    }
    internal static EasNode Read(byte[] data)
    {
        if(!data.AsSpan().StartsWith(Header))throw Invalid();
        var stack=new Stack<EasNode>(); EasNode? root=null;int pos=Header.Length,page=0,nodes=0;
        while(pos<data.Length)
        {
            int code=data[pos++];
            if(code==0){if(pos==data.Length)throw Invalid();page=data[pos++];continue;}
            if(code==1){if(!stack.TryPop(out _))throw Invalid();continue;}
            if(code is 3 or 0xc3)
            {
                if(stack.Count==0)throw Invalid();
                int length=code==3?Array.IndexOf(data,(byte)0,pos)-pos:Number();
                if(length<0 || length>data.Length-pos)throw Invalid();
                var n=stack.Peek();if(n.Data!=null)throw Invalid();
                n.Data=data.AsSpan(pos,length).ToArray();n.Opaque=code==0xc3;pos+=length+(code==3?1:0);continue;
            }
            if((code&128)!=0 || (code&63)<5 || stack.Count>64 || ++nodes>250000)throw Invalid();
            var node=new EasNode((Tag)((page<<8)|(code&63)));
            if(stack.TryPeek(out var parent))parent.Children.Add(node);
            else if(root==null)root=node;
            else throw Invalid();
            if((code&64)!=0)stack.Push(node);
        }
        return root!=null && stack.Count==0?root:throw Invalid();
        int Number()
        {
            int value=0;
            for(int n=0;n<5 && pos<data.Length;n++)
            {
                int b=data[pos++];if(value>(int.MaxValue>>7))throw Invalid();
                value=(value<<7)|(b&127);if((b&128)==0)return value;
            }
            throw Invalid();
        }
        static InvalidDataException Invalid()=>new("Exchange WBXML 数据损坏或格式不受支持。");
    }
}
