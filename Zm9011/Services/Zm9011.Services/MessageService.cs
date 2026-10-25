using Zm9011.Services.Interfaces;

namespace Zm9011.Services
{
    public class MessageService : IMessageService
    {
        public string GetMessage()
        {
            return "Hello from the Message Service";
        }
    }
}
