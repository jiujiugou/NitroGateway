using Microsoft.Extensions.DependencyInjection;
using NitroGateway.Protocols.Modbus;
using NitroGateway.Protocols.S7;
using System;
using System.Collections.Generic;
using System.Text;

namespace NitroGateway.Protocols
{
    public static class ProtocolServiceCollectionExtensions
    {
        public static IServiceCollection AddNitroProtocol(this IServiceCollection services)
        {
            services.AddNitroProtocolFactory();
            services.AddNitroModbus();
            services.AddNitroS7();
            return services;
        }
    }
}
