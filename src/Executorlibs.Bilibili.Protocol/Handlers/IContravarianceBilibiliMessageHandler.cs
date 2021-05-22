using Executorlibs.Bilibili.Protocol.Models.General;
using Executorlibs.MessageFramework.Handlers;

namespace Executorlibs.Bilibili.Protocol.Handlers
{
    public interface IContravarianceBilibiliMessageHandler<in TMessage> : IBilibiliMessageHandler<TMessage>, IContravarianceMessageHandler<TMessage> where TMessage : IBilibiliMessage
    {
        
    }
}
