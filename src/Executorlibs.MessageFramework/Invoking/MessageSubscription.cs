using System.Collections.Generic;
using System.Threading.Tasks;
using Executorlibs.MessageFramework.Clients;
using Executorlibs.MessageFramework.Handlers;
using Executorlibs.MessageFramework.Models.General;

namespace Executorlibs.MessageFramework.Invoking
{
    public class MessageSubscription<TMessage> : IMessageSubscription<TMessage> where TMessage : IMessage
    {
        protected virtual IEnumerable<IMessageHandler<TMessage>> Handlers { get; }

        public MessageSubscription(IEnumerable<IMessageHandler> handlers)
        {
            Handlers = ResolveHandlers(handlers);
        }

        protected virtual IEnumerable<IMessageHandler<TMessage>> ResolveHandlers(IEnumerable<IMessageHandler> handlers)
        {
            List<IMessageHandler<TMessage>> filteredHandlers = new List<IMessageHandler<TMessage>>();
            var expectedHandler = typeof(IContravarianceMessageHandler<TMessage>);
            var expectedInvarianceHandler = typeof(IInvarianceMessageHandler<TMessage>);
            foreach (IMessageHandler handler in handlers)
            {
                if (expectedHandler.IsAssignableFrom(handler.GetType()) ||
                    expectedInvarianceHandler.IsAssignableFrom(handler.GetType()))
                {
                    filteredHandlers.Add((IMessageHandler<TMessage>)handler);
                }
            }
            return filteredHandlers.ToArray();
        }

        public virtual async Task HandleMessage(IMessageClient client, TMessage message)
        {
            foreach (IMessageHandler<TMessage> handler in Handlers)
            {
                await handler.HandleMessage(client, message);
            }
        }
    }
}